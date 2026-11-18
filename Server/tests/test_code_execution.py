import asyncio
import time
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from transport.code_execution import CodeExecutionCoordinator, SlotStore


READY = {"success": True, "data": {"advice": {"ready_for_code_execution": True}}}
BUSY = {"success": True, "data": {"advice": {"ready_for_code_execution": False}}}
DONE = {"success": True, "data": {"result": 42}, "execution": {"state": "completed"}}
PARAMS = {"action": "execute", "code": "return 42;", "execution_id": "first"}


def context(session="A"):
    return SimpleNamespace(session_id=session, report_progress=AsyncMock())


@pytest.fixture
def coordinator(tmp_path):
    return CodeExecutionCoordinator(SlotStore(tmp_path / "slots.sqlite3"), idle_timeout=.1,
                                    poll_interval=.002, progress_interval=.01)


@pytest.mark.asyncio
async def test_other_session_rejected_and_same_session_changed_request_attaches(coordinator):
    entered, release = asyncio.Event(), asyncio.Event()
    executions = []

    async def send(command, params):
        if command == "get_editor_state":
            return READY
        executions.append(params)
        entered.set()
        await release.wait()
        return DONE

    first = asyncio.create_task(coordinator.run(context(), "unity", "execute_code", PARAMS, send))
    await asyncio.wait_for(entered.wait(), 1)
    started = time.monotonic()
    refused = await coordinator.run(context("B"), "unity", "execute_code", PARAMS, send)
    assert time.monotonic() - started < .1
    assert refused["error"] == "code_execution_slot_occupied"
    assert refused["data"]["execution_id"] == "first"
    assert refused["data"]["elapsed_seconds"] >= 0
    second = asyncio.create_task(coordinator.run(context(), "unity", "execute_code",
                                                 {**PARAMS, "code": "different", "execution_id": "new"}, send))
    await asyncio.sleep(.01)
    release.set()
    assert await first == await second == DONE
    assert len(executions) == 1


@pytest.mark.asyncio
async def test_waiter_cancellation_keeps_execution_and_slot(coordinator):
    entered, release = asyncio.Event(), asyncio.Event()
    calls = []

    async def send(command, params):
        if command == "get_editor_state":
            return READY
        calls.append(params)
        entered.set()
        await release.wait()
        return DONE

    first = asyncio.create_task(coordinator.run(context(), "unity", "execute_code", PARAMS, send))
    await entered.wait()
    first.cancel()
    with pytest.raises(asyncio.CancelledError):
        await first
    rejected = await coordinator.run(context("B"), "unity", "execute_code", PARAMS, send)
    assert rejected["error"] == "code_execution_slot_occupied"
    second = asyncio.create_task(coordinator.run(context(), "unity", "execute_code", PARAMS, send))
    release.set()
    assert await second == DONE
    assert len(calls) == 1


@pytest.mark.asyncio
async def test_busy_wait_and_running_both_emit_progress(coordinator):
    coordinator.idle_timeout = 2
    idle, release = asyncio.Event(), asyncio.Event()
    waiting_progress, running_progress = asyncio.Event(), asyncio.Event()
    calls = []

    async def send(command, params):
        if command == "get_editor_state":
            return READY if idle.is_set() else BUSY
        calls.append(params)
        await release.wait()
        return DONE

    ctx = context()
    def progress(**kwargs):
        if "waiting_idle" in kwargs["message"] and ctx.report_progress.call_count >= 2:
            waiting_progress.set()
        if "running" in kwargs["message"]:
            running_progress.set()
    ctx.report_progress.side_effect = progress
    task = asyncio.create_task(coordinator.run(ctx, "unity", "execute_code", PARAMS, send))
    await asyncio.wait_for(waiting_progress.wait(), 1)
    assert not calls
    assert ctx.report_progress.call_count >= 2
    assert (await coordinator.run(context("B"), "unity", "execute_code", PARAMS, send))["error"] == "code_execution_slot_occupied"
    idle.set()
    await asyncio.wait_for(running_progress.wait(), 1)
    assert len(calls) == 1
    assert any("running" in c.kwargs["message"] for c in ctx.report_progress.call_args_list)
    release.set()
    assert await task == DONE


@pytest.mark.asyncio
async def test_idle_timeout_never_sends_even_after_late_probe(coordinator):
    calls = []

    async def send(command, params):
        calls.append(command)
        if command == "get_editor_state":
            await asyncio.sleep(.3)
            return READY
        pytest.fail("Timed out code must never be sent")

    result = await coordinator.run(context(), "unity", "execute_code", PARAMS, send)
    assert result["error"] == "code_idle_wait_timeout"
    assert result["data"]["state"] == "not_started"
    await asyncio.sleep(.32)
    assert calls and set(calls) == {"get_editor_state"}
    _, fresh = coordinator.store.admit("unity", "B", "next")
    assert fresh


@pytest.mark.asyncio
@pytest.mark.parametrize("state", ["completed", "not_started", "running", "unknown", "interrupted"])
async def test_disconnect_reconciles_ledger_without_resending(coordinator, state):
    calls = []
    probes = 0

    async def send(command, params):
        nonlocal probes
        calls.append((command, params))
        if command == "get_editor_state":
            return READY
        if params["action"] == "execute":
            raise ConnectionError("reload")
        probes += 1
        current = state if probes == 1 else "completed"
        return {"success": True, "data": {"execution": {"state": current},
                                          "response": {"status": "success", "result": DONE}}}

    result = await coordinator.run(context(), "unity", "execute_code", PARAMS, send)
    if state in {"not_started", "interrupted"}:
        assert result["data"]["state"] == state
    else:
        assert result == DONE
    assert sum(p.get("action") == "execute" for _, p in calls) == 1
    assert all(p.get("settle_unstarted") for c, p in calls if p.get("action") == "get_execution_status")
    assert probes == (2 if state in {"running", "unknown"} else 1)


@pytest.mark.asyncio
async def test_progress_delivery_failure_preserves_worker(coordinator):
    ctx = context()
    ctx.report_progress.side_effect = ConnectionError()
    async def send(command, params):
        await asyncio.sleep(.015)
        return READY if command == "get_editor_state" else DONE
    assert await coordinator.run(ctx, "unity", "execute_code", PARAMS, send) == DONE


@pytest.mark.asyncio
async def test_unidentified_session_does_not_admit(coordinator):
    send = AsyncMock()
    result = await coordinator.run(SimpleNamespace(), "unity", "execute_code", PARAMS, send)
    assert result["error"] == "code_execution_session_required"
    send.assert_not_called()


@pytest.mark.asyncio
async def test_separate_server_process_store_has_one_slot(coordinator):
    other = SlotStore(coordinator.store.path)
    first, fresh = coordinator.store.admit("unity", "A", "first")
    assert fresh
    second, fresh = other.admit("unity", "B", "second")
    assert not fresh and first.token == second.token
    coordinator.store.finish(first, DONE)
    _, fresh = other.admit("unity", "B", "second")
    assert fresh


@pytest.mark.asyncio
async def test_abandoned_slot_is_reconciled_and_never_dispatched(coordinator):
    slot, _ = coordinator.store.admit("unity", "A", "original")
    with coordinator.store.connect() as db:
        db.execute("UPDATE code_slots SET heartbeat=0 WHERE token=?", (slot.token,))
    calls = []
    async def send(command, params):
        calls.append((command, params))
        return {"success": True, "data": {"execution": {"state": "not_started"}}}
    result = await coordinator.run(context(), "unity", "execute_code", PARAMS, send)
    assert result["error"] == "execution_not_started"
    assert calls == [("execute_code", {"action": "get_execution_status", "execution_id": "original",
                                       "settle_unstarted": True})]


@pytest.mark.asyncio
async def test_busy_race_before_handler_start_keeps_same_id(coordinator):
    ids = []
    async def send(command, params):
        if command == "get_editor_state":
            return READY
        ids.append(params["execution_id"])
        return {"success": False, "error": "editor_busy"} if len(ids) == 1 else DONE
    assert await coordinator.run(context(), "unity", "execute_code", PARAMS, send) == DONE
    assert ids == ["first", "first"]


@pytest.mark.asyncio
async def test_independent_unity_slots(coordinator):
    async def send(command, params):
        return READY if command == "get_editor_state" else DONE
    results = await asyncio.gather(*(coordinator.run(context(s), u, "execute_code", PARAMS, send)
                                     for s, u in [("A", "one"), ("B", "two")]))
    assert results == [DONE, DONE]


def test_simultaneous_process_store_connections_have_one_winner(coordinator):
    from concurrent.futures import ThreadPoolExecutor
    def admit(i):
        return SlotStore(coordinator.store.path).admit("unity", str(i), str(i))[1]
    with ThreadPoolExecutor(max_workers=12) as pool:
        assert sum(pool.map(admit, range(12))) == 1


@pytest.mark.asyncio
async def test_orphaned_foreign_session_reconciles_without_editor_restart(coordinator):
    slot, _ = coordinator.store.admit("unity", "old-session", "original")
    with coordinator.store.connect() as db:
        db.execute("UPDATE code_slots SET heartbeat=0 WHERE token=?", (slot.token,))
    reconciled = asyncio.Event()
    async def send(command, params):
        assert params["action"] == "get_execution_status"
        reconciled.set()
        return {"success": True, "data": {"execution": {"state": "not_started"}}}
    refused = await coordinator.run(context(), "unity", "execute_code", PARAMS, send)
    assert refused["error"] == "code_execution_slot_occupied"
    await asyncio.wait_for(reconciled.wait(), 1)
    await asyncio.gather(*list(coordinator.workers.values()))
    assert coordinator.store.get(slot.token).result["error"] == "execution_not_started"
    assert coordinator.store.admit("unity", "A", "next")[1]


@pytest.mark.asyncio
@pytest.mark.parametrize("command,params", [
    ("manage_editor", {"action": "stop"}), ("ping", {}), ("refresh_unity", {}),
    ("read_console", {"action": "get"}), ("execute_code", {"action": "get_execution_status"}),
    ("batch_execute", {"commands": [{"tool": "manage_editor", "params": {"action": "stop"}}]}),
])
async def test_ordinary_and_recovery_transport_bypasses_admission(monkeypatch, command, params):
    import transport.unity_transport as transport
    raw = AsyncMock(return_value={"success": True})
    monkeypatch.setattr(transport, "_send_with_unity_instance_raw", raw)
    sent = AsyncMock()
    assert await transport.send_with_unity_instance(sent, "project", command, params,
                                                    retry_on_reload=False) == {"success": True}
    raw.assert_awaited_once_with(sent, "project", command, params, user_id=None, retry_on_reload=False)


@pytest.mark.asyncio
@pytest.mark.parametrize("command,params", [
    ("execute_code", PARAMS),
    ("batch_execute", {"commands": [{"tool": "execute_code", "params": PARAMS}]}),
    ("execute_custom_tool", {"tool_name": "execute_code", "parameters": PARAMS}),
])
async def test_code_wrappers_use_canonical_instance_and_session(monkeypatch, coordinator, command, params):
    from core.config import config
    import fastmcp.server.dependencies as deps
    import transport.code_execution as admission
    import transport.unity_transport as transport
    monkeypatch.setattr(config, "transport_mode", "http")
    monkeypatch.setattr(config, "http_remote_hosted", False)
    monkeypatch.setattr(deps, "get_context", lambda: context())
    monkeypatch.setattr(transport.PluginHub, "get_sessions", AsyncMock(return_value=SimpleNamespace(
        sessions={"plugin": SimpleNamespace(hash="1234567890abcdef", project="game")})))
    monkeypatch.setattr(admission, "get_coordinator", lambda: coordinator)
    calls = []
    async def raw(send_fn, target, tool, args, **kwargs):
        assert target == "1234567890abcdef"
        calls.append((tool, args))
        return READY if tool == "get_editor_state" else DONE
    monkeypatch.setattr(transport, "_send_with_unity_instance_raw", raw)
    assert await transport.send_with_unity_instance(AsyncMock(), "game", command, params) == DONE
    assert [c for c, _ in calls] == ["get_editor_state", command]


@pytest.mark.asyncio
async def test_mcp_context_progress_reaches_client(monkeypatch, coordinator):
    from fastmcp import FastMCP, Client, Context
    server = FastMCP("admission-test")
    @server.tool
    async def execute(ctx: Context):
        async def send(command, params):
            await asyncio.sleep(.02)
            return READY if command == "get_editor_state" else DONE
        return await coordinator.run(ctx, "unity", "execute_code", PARAMS, send)
    notifications = []
    async def progress(progress, total, message):
        notifications.append(message)
    async with Client(server, progress_handler=progress) as client:
        result = await client.call_tool("execute", {})
    assert result.data == DONE
    assert any("waiting_idle" in message for message in notifications)
    assert any("running" in message for message in notifications)
