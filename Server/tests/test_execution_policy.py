import asyncio
from unittest.mock import AsyncMock, Mock

import pytest

from core.config import config
from services.tools.execute_code import execute_code
from transport.execution_policy import is_read_only, prepare_execution
from transport.legacy import unity_connection
from transport.plugin_hub import PluginDisconnectedError, PluginHub
from transport.unity_transport import send_with_unity_instance


@pytest.mark.parametrize("command,params,expected", [
    ("ping", {}, True), ("get_editor_state", {}, True),
    ("read_console", {"action": "get"}, True),
    ("read_console", {"action": "clear"}, False),
    ("execute_code", {"action": "get_execution_status"}, True),
    ("execute_code", {"action": "replay"}, False),
    ("batch_execute", {}, False), ("custom_tool", {}, False),
    ("read_console", {"action": []}, False),
])
def test_read_only_allowlist(command, params, expected):
    assert is_read_only(command, params) is expected


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


def test_legacy_reload_response_does_not_resend_mutation(monkeypatch):
    conn = Mock()
    conn.send_command.return_value = {"success": False, "hint": "retry", "error": "reloading", "data": {"reason": "reloading"}}
    monkeypatch.setattr(unity_connection, "get_unity_connection", lambda _: conn)
    result = unity_connection.send_command_with_retry("batch_execute", {})
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
