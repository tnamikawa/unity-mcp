"""Execution-ID and retry policy for C# execution and its explicit wrappers."""
from uuid import uuid4


def requires_code_execution(command: str, params: dict | None) -> bool:
    pending = [(command, params)]
    while pending:
        tool, arguments = pending.pop()
        if not isinstance(arguments, dict):
            continue
        if tool == "execute_code":
            action = arguments.get("action")
            if isinstance(action, str) and action.lower() in {"execute", "replay"}:
                return True
        elif tool == "batch_execute":
            commands = arguments.get("commands")
            if isinstance(commands, list):
                for child in commands:
                    if not isinstance(child, dict):
                        continue
                    raw = child.get("params")
                    normalized = {}
                    if isinstance(raw, dict):
                        for key, value in raw.items():
                            parts = key.split("_")
                            name = parts[0] + "".join(p[:1].upper() + p[1:] for p in parts[1:])
                            normalized[name] = value  # BatchExecute uses the last matching key.
                    pending.append((child.get("tool"), normalized))
        elif tool == "execute_custom_tool":
            pending.append((arguments.get("tool_name"), arguments.get("parameters")))
    return False


def prepare_execution(command: str, params: dict | None) -> dict:
    prepared = dict(params or {})
    if requires_code_execution(command, prepared):
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
