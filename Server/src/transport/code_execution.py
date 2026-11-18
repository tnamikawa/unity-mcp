"""One durable admission slot per Unity; only Python waits for readiness/results.

The database coordinates HTTP and separate stdio server processes. It contains
ownership and results, never submitted source. Unity's journal remains authoritative
after a transport failure. Worker tasks outlive individual MCP requests.
"""
from __future__ import annotations

import asyncio
import json
import logging
import math
import os
from pathlib import Path
import sqlite3
import time
from dataclasses import dataclass
from typing import Awaitable, Callable
from uuid import uuid4

from models.unity_response import normalize_unity_response

log = logging.getLogger(__name__)
Send = Callable[[str, dict], Awaitable[dict]]


def response(value):
    if hasattr(value, "model_dump"):
        value = value.model_dump()
    return normalize_unity_response(value)


def error(code, execution_id=None, **data):
    return {"success": False, "error": code,
            "data": {"execution_id": execution_id, **data}}


def without_retry(value, *, _in_data=False):
    if isinstance(value, list):
        return [without_retry(v) for v in value]
    if not isinstance(value, dict):
        return value
    # data.result is the C# program's return value, not transport advice.
    return {k: (v if _in_data and k == "result" else without_retry(v, _in_data=k == "data"))
            for k, v in value.items()
            if k not in {"retry_after_ms", "recommended_retry_after_ms"}
            and not (k == "hint" and v == "retry")}


@dataclass
class Slot:
    token: str
    instance: str
    session: str
    execution_id: str
    created: float
    phase: str
    result: dict | None = None
    owner: str = ""
    heartbeat: float = 0.


class SlotStore:
    """Short SQLite transactions make admission and terminal removal atomic."""

    def __init__(self, path: Path):
        self.path = path
        self.owner = uuid4().hex
        path.parent.mkdir(parents=True, exist_ok=True)
        with self.connect() as db:
            db.execute("CREATE TABLE IF NOT EXISTS code_slots ("
                       "token TEXT PRIMARY KEY, instance TEXT, session TEXT, "
                       "execution_id TEXT, created REAL, phase TEXT, result TEXT, owner TEXT, heartbeat REAL)")
            db.execute("CREATE UNIQUE INDEX IF NOT EXISTS active_code_slot "
                       "ON code_slots(instance) WHERE result IS NULL")

    def connect(self):
        return sqlite3.connect(str(self.path), timeout=2)

    @staticmethod
    def slot(row):
        if row is None:
            return None
        return Slot(*row[:6], json.loads(row[6]) if row[6] else None, *row[7:])

    def admit(self, instance, session, execution_id):
        with self.connect() as db:
            db.execute("BEGIN IMMEDIATE")
            row = db.execute("SELECT * FROM code_slots WHERE instance=? AND result IS NULL",
                             (instance,)).fetchone()
            if row:
                return self.slot(row), False
            slot = Slot(uuid4().hex, instance, session, execution_id, time.time(), "waiting_idle")
            db.execute("INSERT INTO code_slots VALUES (?,?,?,?,?,?,NULL,?,?)",
                       (slot.token, instance, session, execution_id, slot.created, slot.phase,
                        self.owner, time.time()))
            return slot, True

    def claim_abandoned(self, slot):
        with self.connect() as db:
            return bool(db.execute("UPDATE code_slots SET owner=?, heartbeat=? WHERE token=? "
                                   "AND result IS NULL AND heartbeat<?",
                                   (self.owner, time.time(), slot.token, time.time() - 30)).rowcount)

    def heartbeat(self, slot):
        with self.connect() as db:
            db.execute("UPDATE code_slots SET heartbeat=? WHERE token=? AND owner=? AND result IS NULL",
                       (time.time(), slot.token, self.owner))

    def get(self, token):
        with self.connect() as db:
            return self.slot(db.execute("SELECT * FROM code_slots WHERE token=?", (token,)).fetchone())

    def phase(self, slot, phase):
        with self.connect() as db:
            changed = db.execute("UPDATE code_slots SET phase=? WHERE token=? AND owner=? AND result IS NULL",
                                 (phase, slot.token, self.owner)).rowcount
        if changed:
            slot.phase = phase
        return bool(changed)

    def finish(self, slot, result):
        result = without_retry(result)
        with self.connect() as db:
            db.execute("UPDATE code_slots SET phase='completed', result=? "
                       "WHERE token=? AND result IS NULL", (json.dumps(result), slot.token))
        return result


class CodeExecutionCoordinator:
    def __init__(self, store: SlotStore, *, idle_timeout=600., poll_interval=1., progress_interval=12.):
        self.store = store
        self.idle_timeout = idle_timeout
        self.poll_interval = poll_interval
        self.progress_interval = progress_interval
        self.workers: dict[str, asyncio.Task] = {}

    async def run(self, ctx, instance: str, command: str, params: dict, send: Send):
        try:
            session = ctx.session_id
        except (AttributeError, RuntimeError):
            session = None
        if not isinstance(session, str) or not session:
            return error("code_execution_session_required", state="not_started")
        execution_id = params.get("execution_id") or uuid4().hex
        slot, fresh = self.store.admit(instance, session, execution_id)
        if slot.session != session:
            if slot.token not in self.workers and self.store.claim_abandoned(slot):
                self._start(slot, command, params, send, fresh=False)
            return error("code_execution_slot_occupied", slot.execution_id,
                         reason="another_session_" + slot.phase,
                         elapsed_seconds=max(0., time.time() - slot.created))
        last_progress = 0.
        while True:
            current = self.store.get(slot.token)
            if current.result is not None:
                return current.result
            if slot.token not in self.workers and (fresh or self.store.claim_abandoned(slot)):
                # A process restart can only reconcile old admissions; never replay them.
                self._start(slot, command, params, send, fresh=fresh)
                fresh = False
            now = time.monotonic()
            if now - last_progress >= self.progress_interval:
                try:
                    await asyncio.wait_for(ctx.report_progress(
                        progress=max(0., time.time() - slot.created),
                        message=f"execute_code {current.phase}: {slot.execution_id}"), timeout=2.)
                except Exception:
                    # A disconnected waiter has no effect on the independent worker.
                    log.debug("Code progress delivery failed", exc_info=True)
                last_progress = now
            await asyncio.sleep(min(self.poll_interval, self.progress_interval))

    def _start(self, slot, command, params, send, *, fresh):
        task = asyncio.create_task(self._owned_work(
            slot, command, dict(params, execution_id=slot.execution_id), send, fresh=fresh))
        self.workers[slot.token] = task
        task.add_done_callback(lambda t: self.workers.pop(slot.token, None))

    async def _owned_work(self, slot, command, params, send, *, fresh):
        async def heartbeat():
            while True:
                self.store.heartbeat(slot)
                await asyncio.sleep(5)
        pulse = asyncio.create_task(heartbeat())
        try:
            await self._work(slot, command, params, send, fresh=fresh)
        finally:
            pulse.cancel()
            await asyncio.gather(pulse, return_exceptions=True)

    async def _call(self, send, command, params):
        try:
            return response(await send(command, params))
        except Exception:
            log.debug("Code control request unavailable", exc_info=True)
            return error("code_transport_unavailable")

    async def _work(self, slot, command, params, send, *, fresh):
        try:
            if not fresh:
                await self._reconcile(slot, send)
                return
            deadline = time.monotonic() + self.idle_timeout
            while True:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    self.store.finish(slot, error("code_idle_wait_timeout", slot.execution_id,
                                                  state="not_started", execution_started=False))
                    return
                # Only the read-only probe has a deadline. No execute task is queued
                # until the atomic phase transition below, so idle expiry cannot run late.
                try:
                    state = await asyncio.wait_for(self._call(send, "get_editor_state", {}), remaining)
                except asyncio.TimeoutError:
                    continue
                data = state.get("data") or {}
                # Unity sends the raw snapshot; advice is a Python resource
                # enrichment, not part of the Editor's wire response.
                from services.resources.editor_state import _enrich_advice_and_staleness
                advice = _enrich_advice_and_staleness(dict(data))["advice"]
                if state.get("success") and advice.get("ready_for_code_execution") is True:
                    if time.monotonic() >= deadline:
                        continue
                    if not self.store.phase(slot, "running"):
                        return
                    result = await self._call(send, command, params)
                    if result.get("error") == "editor_busy" and not result.get("execution"):
                        # Unity atomically defers before handler start if readiness
                        # changed after the probe. Reuse the same admission identity.
                        self.store.phase(slot, "waiting_idle")
                        await asyncio.sleep(self.poll_interval)
                        continue
                    execution = result.get("execution") or {}
                    state = (result.get("data") or {}).get("state")
                    if execution.get("state") == "completed" or (
                        result.get("error") not in {"execution_result_unknown", "code_transport_unavailable",
                                                   "unity_session_unavailable", "unity_connection_unavailable",
                                                   "execution_in_progress"}
                        and state not in {"not_sent", "unknown", "running", "queued"}
                    ):
                        self.store.finish(slot, result)
                        return
                    if state == "not_sent":
                        self.store.phase(slot, "waiting_idle")
                        continue
                    await self._reconcile(slot, send)
                    return
                await asyncio.sleep(min(self.poll_interval, max(0., deadline - time.monotonic())))
        except asyncio.CancelledError:
            # Server shutdown leaves a durable slot for the next process to reconcile.
            raise
        except Exception:
            log.exception("Code worker failed; preserving admission for journal reconciliation")
            await self._reconcile(slot, send)

    async def _reconcile(self, slot, send):
        self.store.phase(slot, "reconciling")
        while True:
            result = await self._call(send, "execute_code", {
                "action": "get_execution_status", "execution_id": slot.execution_id,
                "settle_unstarted": True,
            })
            data = result.get("data") or {}
            execution = data.get("execution") or {}
            state = execution.get("state")
            if state == "completed":
                saved = data.get("response")
                self.store.finish(slot, response(saved) if saved else
                                  error("execution_result_not_retained", slot.execution_id, state=state))
                return
            if state in {"not_started", "interrupted"}:
                self.store.finish(slot, error("execution_" + state, slot.execution_id,
                                              state=state, execution_started=state != "not_started"))
                return
            await asyncio.sleep(self.poll_interval)


_coordinator: CodeExecutionCoordinator | None = None


def get_coordinator():
    global _coordinator
    if _coordinator is None:
        timeout = float(os.environ.get("UNITY_MCP_CODE_IDLE_TIMEOUT_SECONDS", "600"))
        if not math.isfinite(timeout) or timeout <= 0:
            raise ValueError("UNITY_MCP_CODE_IDLE_TIMEOUT_SECONDS must be positive and finite")
        _coordinator = CodeExecutionCoordinator(
            SlotStore(Path.home() / ".unity-mcp" / "code-execution-slots.sqlite3"), idle_timeout=timeout)
    return _coordinator
