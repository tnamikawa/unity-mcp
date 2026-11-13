import asyncio
from unittest.mock import AsyncMock, Mock

import pytest

from core.config import config
from services.tools.execute_code import execute_code
from transport.execution_policy import requires_code_execution, prepare_execution
from transport.legacy import unity_connection
from transport.plugin_hub import PluginDisconnectedError, PluginHub
from transport.unity_transport import send_with_unity_instance


@pytest.mark.parametrize("command,params,expected", [
    ("ping", {}, False), ("get_editor_state", {}, False),
    ("read_console", {"action": "get"}, False), ("read_console", {"action": "clear"}, False),
    ("execute_code", {"action": "get_execution_status"}, False),
    ("execute_code", {"action": "get_history"}, False), ("execute_code", {"action": "clear_history"}, False),
    ("execute_code", {"action": "execute"}, True), ("execute_code", {"action": "replay"}, True),
    ("execute_code", {"action": "EXECUTE"}, True), ("execute_code", {"action": []}, False),
    ("batch_execute", {}, False), ("custom_tool", {}, False),
    ("manage_editor", {"action": "stop"}, False), ("manage_scene", {"action": "get_hierarchy"}, False),
    ("manage_asset", {"action": "search"}, False), ("refresh_unity", {}, False), ("get_project_info", {}, False),
])
def test_only_code_execution_requires_guard(command, params, expected):
    assert requires_code_execution(command, params) is expected
    assert ("execution_id" in prepare_execution(command, params)) is expected


def batch(tool, params):
    return {"commands": [{"tool": tool, "params": params}]}


def test_nested_wrappers_and_batch_normalization():
    code = batch("execute_code", {"action_": "execute", "code_": "return 42;"})
    nested = batch("batch_execute", code)
    assert requires_code_execution("batch_execute", nested)
    assert requires_code_execution("execute_custom_tool", {"tool_name": "batch_execute", "parameters": nested})
    assert requires_code_execution("batch_execute", batch("execute_code", {"action_": "get_history", "action": "execute"}))
    assert not requires_code_execution("batch_execute", batch("execute_code", {"action": "execute", "action_": "get_history"}))
    assert not requires_code_execution("execute_custom_tool", {"tool_name": "manage_editor", "parameters": {"action": "stop"}})


def test_large_and_deep_ordinary_batches_remain_untracked():
    query = {"tool": "manage_asset", "params": {"action": "search", "page_size": 10, "pageSize": 10}}
    args = {"commands": [{"tool": "batch_execute", "params": {"commands": [query] * 25}}] * 4}
    for _ in range(12):
        args = batch("batch_execute", args)
    assert not requires_code_execution("batch_execute", args)
    assert "execution_id" not in prepare_execution("batch_execute", args)


def test_prepare_does_not_replace_supplied_id_or_modify_caller():
    params = {"execution_id": "intent-1", "action": "execute"}
    assert prepare_execution("execute_code", params) == params
    original = {"action": "execute"}
    assert prepare_execution("execute_code", original)["execution_id"]
    assert "execution_id" not in original


def test_legacy_disconnect_does_not_resend(monkeypatch):
    conn = Mock()
    conn.send_command.side_effect = ConnectionResetError("lost response")
    monkeypatch.setattr(unity_connection, "get_unity_connection", lambda _: conn)
    result = unity_connection.send_command_with_retry("execute_code", {"action": "execute", "execution_id": "intent-1"})
    assert conn.send_command.call_count == 1
    assert conn.send_command.call_args.kwargs["max_attempts"] == 0
    assert result["data"]["execution_id"] == "intent-1"
    assert result["hint"] == "inspect_execution"


def test_legacy_reload_response_does_not_resend_code_batch(monkeypatch):
    conn = Mock()
    conn.send_command.return_value = {"success": False, "hint": "retry", "error": "reloading", "data": {"reason": "reloading"}}
    monkeypatch.setattr(unity_connection, "get_unity_connection", lambda _: conn)
    result = unity_connection.send_command_with_retry("batch_execute", batch("execute_code", {"action": "execute", "code": "return 42;"}))
    assert conn.send_command.call_count == 1
    assert result["hint"] == "inspect_execution"


@pytest.mark.parametrize("failure", ["disconnect", "timeout", "send_error"])
def test_http_unknown_outcome_preserves_id_and_never_retries(monkeypatch, failure):
    async def run():
        sent = []

        async def send(payload):
            sent.append(payload)
            if failure == "send_error":
                raise ConnectionResetError("send failed")
            if failure == "disconnect":
                PluginHub._pending[payload["id"]]["future"].set_exception(PluginDisconnectedError("lost connection"))

        monkeypatch.setattr(PluginHub, "_get_connection", AsyncMock(return_value=Mock(send_json=send)))
        monkeypatch.setattr(PluginHub, "_lock", asyncio.Lock())
        monkeypatch.setattr(PluginHub, "_pending", {})
        monkeypatch.setattr(PluginHub, "COMMAND_TIMEOUT", 0.01)
        result = await PluginHub.send_command("session", "execute_code", {"execution_id": "intent-1", "action": "execute"})
        assert len(sent) == 1
        assert result["hint"] == "inspect_execution"
        assert result["data"]["execution_id"] == "intent-1"
        assert result["data"]["state"] == "unknown"
    asyncio.run(run())


def test_http_wrapper_disables_reload_retries_and_sanitizes_hint(monkeypatch):
    monkeypatch.setattr(config, "transport_mode", "http")
    monkeypatch.setattr(config, "http_remote_hosted", False)
    send = AsyncMock(return_value={"success": False, "hint": "retry", "error": "disconnected"})
    monkeypatch.setattr(PluginHub, "send_command_for_instance", send)
    result = asyncio.run(send_with_unity_instance(AsyncMock(), "project", "execute_code", {"action": "execute"}))
    assert send.await_count == 1
    assert send.call_args.kwargs["retry_on_reload"] is False
    assert result["hint"] == "inspect_execution"
    assert result["execution_id"]


def test_status_query_uses_existing_id_and_offline_inspection_action(monkeypatch):
    monkeypatch.setattr("services.tools.execute_code.get_unity_instance_from_context", AsyncMock(return_value="project"))
    send = AsyncMock(return_value={"success": True, "data": {"state": "unknown"}})
    monkeypatch.setattr("services.tools.execute_code.send_with_unity_instance", send)
    result = asyncio.run(execute_code(Mock(), action="get_execution_status", execution_id="intent-1"))
    assert send.call_args.args[3] == {"action": "get_execution_status", "execution_id": "intent-1"}
    assert result["success"] is True


@pytest.mark.parametrize("command,params", [
    ("manage_editor", {"action": "stop"}), ("refresh_unity", {}),
    ("batch_execute", batch("manage_scene", {"action": "get_hierarchy"})),
    ("execute_custom_tool", {"tool_name": "manage_editor", "parameters": {"action": "stop"}}),
])
def test_ordinary_legacy_operations_keep_reload_retries(monkeypatch, command, params):
    conn = Mock()
    conn.send_command.side_effect = [
        {"success": False, "hint": "retry", "error": "reloading", "data": {"reason": "reloading"}},
        {"success": True},
    ]
    monkeypatch.setattr(unity_connection, "get_unity_connection", lambda _: conn)
    monkeypatch.setattr(unity_connection.time, "sleep", lambda _: None)
    result = unity_connection.send_command_with_retry(command, params, max_retries=1)
    assert result["success"]
    assert conn.send_command.call_count == 2
    assert conn.send_command.call_args_list[0].kwargs["max_attempts"] is None
    assert "execution_id" not in conn.send_command.call_args.args[1]


@pytest.mark.parametrize("command,params", [
    ("manage_editor", {"action": "stop"}), ("refresh_unity", {}),
    ("batch_execute", batch("manage_scene", {"action": "get_hierarchy"})),
])
def test_ordinary_http_operations_keep_retry_hints(monkeypatch, command, params):
    monkeypatch.setattr(config, "transport_mode", "http")
    monkeypatch.setattr(config, "http_remote_hosted", False)
    send = AsyncMock(return_value={"success": False, "hint": "retry", "error": "reloading"})
    monkeypatch.setattr(PluginHub, "send_command_for_instance", send)
    result = asyncio.run(send_with_unity_instance(AsyncMock(), "project", command, params))
    assert send.call_args.kwargs["retry_on_reload"] is True
    assert "execution_id" not in send.call_args.args[2]
    assert result["hint"] == "retry"
