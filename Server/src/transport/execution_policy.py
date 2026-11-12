"""Mutation retry policy shared by HTTP, stdio, and direct transport callers."""
from uuid import uuid4


def is_read_only(command: str, params: dict | None) -> bool:
    action = (params or {}).get("action")
    action = action.lower() if isinstance(action, str) else action
    if action is not None and not isinstance(action, str):
        return False
    command = command.lower()
    if command in {"ping", "get_editor_state", "get_test_job"}:
        return True
    if command == "read_console":
        return action in {None, "get"}
    return command == "execute_code" and action in {"get_history", "get_execution_status"}


def prepare_execution(command: str, params: dict | None) -> dict:
    prepared = dict(params or {})
    if not is_read_only(command, prepared):
        prepared.setdefault("execution_id", uuid4().hex)
    return prepared


def uncertain_response(command: str, params: dict, error: str) -> dict:
    return {
        "success": False,
        "error": "execution_result_unknown",
        "message": error,
        "hint": "inspect_execution",
        "data": {"execution_id": params.get("execution_id"), "command": command,
                 "state": "unknown", "next_action": "execute_code.get_execution_status"},
    }
