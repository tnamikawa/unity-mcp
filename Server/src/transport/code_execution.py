"""One durable admission slot per Unity; only Python waits for readiness/results.

The database coordinates HTTP and separate stdio server processes. It contains
ownership, request fingerprints and results, never submitted source. Worker tasks
outlive individual MCP requests.

Unity is one stateful process that passes through transitions (compilation, domain
reload, play mode changes, player builds, imports). A domain reload discards every
queued command and the socket that carried it, so a send can be lost without an
answer. The worker therefore:

- sends only after a readiness probe reports no transition,
- numbers every send of an execution ID ("attempt"),
- after a lost answer asks Unity to settle the ID; an unstarted attempt is then
  rejected forever by Unity and the same request is sent again as a later attempt,
- gives up reconciliation after a bounded time without a definitive answer,
  releasing the slot with an explicit unknown result instead of holding it.
"""
from __future__ import annotations

import asyncio
import hashlib
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

# Attempts must increase across workers and server restarts for the same execution ID:
# seconds since 2026-01-01 UTC, bumped by one when sends follow within a second.
ATTEMPT_EPOCH = 1767225600
UNCERTAIN_ERRORS = {"execution_result_unknown", "code_transport_unavailable", "unity_session_unavailable",
                    "unity_connection_unavailable", "execution_in_progress"}
LIVE_STATES = {"running", "queued", "unknown"}


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


def fingerprint(command: str, params: dict | None) -> str:
    request = {k: v for k, v in (params or {}).items() if k not in {"execution_id", "attempt"}}
    text = json.dumps([command, request], sort_keys=True, separators=(",", ":"), default=str)
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def never_executed(result: dict | None) -> bool:
    """True when a stored result proves the request did not run in Unity."""
    if not isinstance(result, dict) or result.get("success") is not False:
        return False
    data = result.get("data") if isinstance(result.get("data"), dict) else {}
    return data.get("execution_started") is False or data.get("state") in {"not_started", "not_sent"}


def next_attempt(previous: int | None) -> int:
    now = int(time.time()) - ATTEMPT_EPOCH
    return now if previous is None else max(previous + 1, now)


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
    fingerprint: str | None = None


COLUMNS = "token, instance, session, execution_id, created, phase, result, owner, heartbeat, fingerprint"


class SlotStore:
    """Short SQLite transactions make admission and terminal removal atomic."""

    def __init__(self, path: Path):
        self.path = path
        self.owner = uuid4().hex
        path.parent.mkdir(parents=True, exist_ok=True)
        with self.connect() as db:
            db.execute("CREATE TABLE IF NOT EXISTS code_slots ("
                       "token TEXT PRIMARY KEY, instance TEXT, session TEXT, "
                       "execution_id TEXT, created REAL, phase TEXT, result TEXT, owner TEXT, heartbeat REAL, "
                       "fingerprint TEXT)")
            columns = {row[1] for row in db.execute("PRAGMA table_info(code_slots)")}
            if "fingerprint" not in columns:
                try:
                    db.execute("ALTER TABLE code_slots ADD COLUMN fingerprint TEXT")
                except sqlite3.OperationalError:
                    pass  # Another server process added it first.
            db.execute("CREATE UNIQUE INDEX IF NOT EXISTS active_code_slot "
                       "ON code_slots(instance) WHERE result IS NULL")
            db.execute("CREATE INDEX IF NOT EXISTS code_slot_execution ON code_slots(instance, execution_id)")

    def connect(self):
        return sqlite3.connect(str(self.path), timeout=2)

    @staticmethod
    def slot(row):
        if row is None:
            return None
        return Slot(*row[:6], json.loads(row[6]) if row[6] else None, *row[7:])

    def admit(self, instance, session, execution_id, request_fingerprint=None):
        """Returns (slot, fresh). A finished slot is returned when the same request already ran."""
        with self.connect() as db:
            db.execute("BEGIN IMMEDIATE")
            row = db.execute(f"SELECT {COLUMNS} FROM code_slots WHERE instance=? AND result IS NULL",
                             (instance,)).fetchone()
            if row:
                return self.slot(row), False
            # Unity forgets executions when the Editor restarts; the server remembers them,
            # so a repeated execution ID never silently runs a second time.
            for row in db.execute(f"SELECT {COLUMNS} FROM code_slots WHERE instance=? AND execution_id=? "
                                  "AND result IS NOT NULL ORDER BY created DESC", (instance, execution_id)):
                done = self.slot(row)
                if never_executed(done.result):
                    continue
                if done.fingerprint and request_fingerprint and done.fingerprint != request_fingerprint:
                    done.result = error("execution_id_conflict", execution_id, state="not_started",
                                        execution_started=False,
                                        message="This execution ID belongs to a different request.")
                return done, False
            slot = Slot(uuid4().hex, instance, session, execution_id, time.time(), "waiting_idle",
                        owner=self.owner, heartbeat=time.time(), fingerprint=request_fingerprint)
            db.execute(f"INSERT INTO code_slots ({COLUMNS}) VALUES (?,?,?,?,?,?,NULL,?,?,?)",
                       (slot.token, instance, session, execution_id, slot.created, slot.phase,
                        self.owner, slot.heartbeat, request_fingerprint))
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
            return self.slot(db.execute(f"SELECT {COLUMNS} FROM code_slots WHERE token=?", (token,)).fetchone())

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
    def __init__(self, store: SlotStore, *, idle_timeout=600., poll_interval=1., progress_interval=12.,
                 unresponsive_timeout=30., probe_timeout=5., reconcile_timeout=300.):
        self.store = store
        self.idle_timeout = idle_timeout
        # The Editor answers state probes on its main thread. A modal dialog blocks that
        # thread; report it instead of waiting the whole idle window. (A player build is
        # answered off the main thread and waits as an ordinary transition.)
        self.unresponsive_timeout = unresponsive_timeout
        self.probe_timeout = probe_timeout
        # Longest reconciliation without a definitive answer before the slot is released
        # with an unknown outcome. A live execution keeps answering and never expires.
        self.reconcile_timeout = reconcile_timeout
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
        slot, fresh = self.store.admit(instance, session, execution_id, fingerprint(command, params))
        if slot.result is not None:
            return slot.result
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
        attempt = None
        try:
            if not fresh:
                # Attempts sent by an earlier worker are unknown here: settle them all.
                await self._reconcile(slot, send, None)
                return
            deadline = time.monotonic() + self.idle_timeout
            last_answer = time.monotonic()
            while True:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    self.store.finish(slot, error("code_idle_wait_timeout", slot.execution_id,
                                                  state="not_started", execution_started=False))
                    return
                # Only the read-only probe has a deadline. No execute task is queued
                # until the atomic phase transition below, so idle expiry cannot run late.
                try:
                    state = await asyncio.wait_for(self._call(send, "get_editor_state", {}),
                                                   min(remaining, self.probe_timeout))
                except asyncio.TimeoutError:
                    state = None
                if state is not None and not state.get("success"):
                    # Disconnected during a domain reload: an ordinary wait, not a blocked thread.
                    last_answer = time.monotonic()
                    await asyncio.sleep(min(self.poll_interval, max(0., deadline - time.monotonic())))
                    continue
                if state is None:
                    silent = time.monotonic() - last_answer
                    if silent >= self.unresponsive_timeout:
                        self.store.finish(slot, error("editor_unresponsive", slot.execution_id,
                                                      state="not_started", execution_started=False,
                                                      unresponsive_seconds=round(silent, 1),
                                                      hint="Unity's main thread is blocked (modal dialog "
                                                           "or long import). Nothing was executed."))
                        return
                    await asyncio.sleep(min(self.poll_interval, max(0., deadline - time.monotonic())))
                    continue
                last_answer = time.monotonic()
                data = state.get("data") or {}
                # Unity sends the raw snapshot; advice is a Python resource
                # enrichment, not part of the Editor's wire response.
                from services.resources.editor_state import _enrich_advice_and_staleness
                advice = _enrich_advice_and_staleness(dict(data))["advice"]
                if not (state.get("success") and advice.get("ready_for_code_execution") is True):
                    await asyncio.sleep(min(self.poll_interval, max(0., deadline - time.monotonic())))
                    continue
                if not self.store.phase(slot, "running"):
                    return
                attempt = next_attempt(attempt)
                result = await self._call(send, command, dict(params, attempt=attempt))
                if result.get("error") == "editor_busy" and not result.get("execution"):
                    # Unity atomically declines before handler start if readiness changed
                    # after the probe (a transition began). Send again as a later attempt.
                    self.store.phase(slot, "waiting_idle")
                    await asyncio.sleep(self.poll_interval)
                    continue
                execution = result.get("execution") or {}
                result_state = (result.get("data") or {}).get("state")
                if execution.get("state") == "completed" or (
                    result.get("error") not in UNCERTAIN_ERRORS
                    and result_state not in {"not_sent", "unknown", "running", "queued"}
                ):
                    self.store.finish(slot, result)
                    return
                if result_state == "not_sent":
                    self.store.phase(slot, "waiting_idle")
                    continue
                # The answer was lost or the Editor is occupied: settle this attempt.
                if await self._reconcile(slot, send, attempt) != "not_started":
                    return
                if time.monotonic() >= deadline:
                    self.store.finish(slot, error("execution_not_started", slot.execution_id,
                                                  state="not_started", execution_started=False))
                    return
                # Proven unstarted (for example discarded by a domain reload): wait for the
                # Editor to settle and send the same request again.
                self.store.phase(slot, "waiting_idle")
        except asyncio.CancelledError:
            # Server shutdown leaves a durable slot for the next process to reconcile.
            raise
        except Exception:
            log.exception("Code worker failed; preserving admission for journal reconciliation")
            await self._reconcile(slot, send, None)

    async def _reconcile(self, slot, send, attempt):
        """Settle the execution in Unity. With an attempt, returns "not_started" instead of
        finishing so the caller can send again; otherwise every outcome finishes the slot."""
        self.store.phase(slot, "reconciling")
        status = {"action": "get_execution_status", "execution_id": slot.execution_id,
                  "settle_unstarted": True, "admitted_at_unix_ms": int(slot.created * 1000)}
        if attempt is not None:
            status["settle_attempt"] = attempt
        last_definite = time.monotonic()
        last_error = None
        while True:
            result = await self._call(send, "execute_code", status)
            data = result.get("data") or {}
            execution = data.get("execution") or {}
            state = execution.get("state")
            if state == "completed":
                saved = data.get("response")
                self.store.finish(slot, response(saved) if saved else
                                  error("execution_result_not_retained", slot.execution_id, state=state))
                return state
            if state == "not_started" and attempt is not None:
                return state
            if state in {"not_started", "interrupted", "lost"}:
                # lost: an Editor restart ended the process that may have received it.
                started = {"not_started": False, "interrupted": True, "lost": None}[state]
                self.store.finish(slot, error("execution_" + state, slot.execution_id,
                                              state=state, execution_started=started))
                return state
            if state in LIVE_STATES:
                # Unity reports the execution itself; its duration is the program's.
                last_definite = time.monotonic()
            else:
                # Disconnected (reload, restart) or an answer without a state.
                last_error = result.get("error") or "no_execution_state"
            if time.monotonic() - last_definite >= self.reconcile_timeout:
                self.store.finish(slot, error(
                    "execution_result_unknown", slot.execution_id, state="unknown",
                    last_error=last_error, reconcile_seconds=round(time.monotonic() - last_definite, 1),
                    hint="Unity gave no definitive state for this execution. The slot was released; "
                         "inspect the Editor before repeating side effects."))
                return "unknown"
            await asyncio.sleep(self.poll_interval)


_coordinator: CodeExecutionCoordinator | None = None


def _positive_env(name: str, default: str) -> float:
    value = float(os.environ.get(name, default))
    if not math.isfinite(value) or value <= 0:
        raise ValueError(f"{name} must be positive and finite")
    return value


def get_coordinator():
    global _coordinator
    if _coordinator is None:
        _coordinator = CodeExecutionCoordinator(
            SlotStore(Path.home() / ".unity-mcp" / "code-execution-slots.sqlite3"),
            idle_timeout=_positive_env("UNITY_MCP_CODE_IDLE_TIMEOUT_SECONDS", "600"),
            unresponsive_timeout=_positive_env("UNITY_MCP_CODE_UNRESPONSIVE_SECONDS", "30"),
            reconcile_timeout=_positive_env("UNITY_MCP_CODE_RECONCILE_SECONDS", "300"))
    return _coordinator
