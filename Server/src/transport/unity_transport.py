"""Transport helpers for routing commands to Unity."""
from __future__ import annotations

import logging
import re
from typing import Awaitable, Callable, TypeVar

from transport.plugin_hub import InstanceSelectionRequiredError, PluginHub
from core.config import config
from core.constants import API_KEY_HEADER
from services.api_key_service import ApiKeyService
from models.models import MCPResponse
from models.unity_response import normalize_unity_response
from transport.execution_policy import requires_code_execution, prepare_execution, uncertain_response

logger = logging.getLogger(__name__)
T = TypeVar("T")


def _is_http_transport() -> bool:
    return config.transport_mode.lower() == "http"


async def _resolve_user_id_from_request() -> str | None:
    """Extract user_id from the current HTTP request's API key header."""
    if not config.http_remote_hosted:
        return None
    if not ApiKeyService.is_initialized():
        return None
    try:
        from fastmcp.server.dependencies import get_http_headers
        headers = get_http_headers(include_all=True)
        api_key = headers.get(API_KEY_HEADER.lower())
        if not api_key:
            return None
        service = ApiKeyService.get_instance()
        result = await service.validate(api_key)
        return result.user_id if result.valid else None
    except Exception as e:
        logger.debug("Failed to resolve user_id from HTTP request: %s", e)
        return None


async def _send_with_unity_instance_raw(
    send_fn: Callable[..., Awaitable[T]],
    unity_instance: str | None,
    *args,
    user_id: str | None = None,
    **kwargs,
) -> T:
    if _is_http_transport():
        if not args:
            raise ValueError("HTTP transport requires command arguments")
        command_type = args[0]
        params = args[1] if len(args) > 1 else kwargs.get("params")
        if params is None:
            params = {}
        if not isinstance(params, dict):
            raise TypeError(
                "Command parameters must be a dict for HTTP transport")
        params = prepare_execution(command_type, params)
        code_execution = requires_code_execution(command_type, params)

        # Auto-resolve user_id from HTTP request API key (remote-hosted mode)
        if user_id is None:
            user_id = await _resolve_user_id_from_request()

        # Auth check
        if config.http_remote_hosted and not user_id:
            return normalize_unity_response(
                MCPResponse(
                    success=False,
                    error="auth_required",
                    message="API key required",
                ).model_dump()
            )

        retry_on_reload = kwargs.pop("retry_on_reload", True)
        if not isinstance(retry_on_reload, bool):
            retry_on_reload = True

        try:
            raw = await PluginHub.send_command_for_instance(
                unity_instance,
                command_type,
                params,
                user_id=user_id,
                retry_on_reload=retry_on_reload,
            )
            result = normalize_unity_response(raw)
            if (code_execution and isinstance(result, dict) and result.get("hint") == "retry"
                    and (result.get("data") or {}).get("state") != "not_sent"):
                result = dict(result, hint="inspect_execution")
                result.setdefault("execution_id", params["execution_id"])
            return result
        except InstanceSelectionRequiredError as exc:
            # Not retryable: a blind retry fails identically. The client must pick
            # an instance, so hint at selection and hand over the ids structurally.
            return normalize_unity_response(
                MCPResponse(
                    success=False,
                    error=str(exc),
                    hint="select_instance",
                    data={
                        "reason": "instance_selection_required",
                        "available_instances": exc.available_instances,
                    },
                ).model_dump()
            )
        except Exception as exc:
            # NOTE: asyncio.TimeoutError has an empty str() by default, which is confusing for clients.
            err = str(exc) or f"{type(exc).__name__}"
            if code_execution:
                return uncertain_response(command_type, params, err)
            # Fail fast with a retry hint instead of hanging for COMMAND_TIMEOUT.
            # The client can decide whether retrying is appropriate for the command.
            return normalize_unity_response(
                MCPResponse(success=False, error=err,
                            hint="retry").model_dump()
            )

    if unity_instance:
        kwargs.setdefault("instance_id", unity_instance)
    return await send_fn(*args, **kwargs)


async def send_with_unity_instance(send_fn, unity_instance, *args, user_id=None, **kwargs):
    # Only C# execution and containing wrappers enter admission. All other calls
    # retain the exact original transport path, deadlines, retries and cancellation.
    command = args[0] if args else None
    params = args[1] if len(args) > 1 else kwargs.get("params")
    if not requires_code_execution(command, params):
        return await _send_with_unity_instance_raw(
            send_fn, unity_instance, *args, user_id=user_id, **kwargs)

    from fastmcp.server.dependencies import get_context
    from transport.code_execution import get_coordinator, error
    try:
        ctx = get_context()
    except RuntimeError:
        return error("code_execution_session_required", state="not_started")
    if user_id is None:
        user_id = await _resolve_user_id_from_request()
    if config.http_remote_hosted and not user_id:
        return error("auth_required", state="not_started")
    # Middleware supplies a resolved Name@hash. Resolve aliases for internal
    # callers too, so different spellings/transports cannot acquire two slots.
    instance = unity_instance or await ctx.get_state("unity_instance")
    if _is_http_transport():
        sessions = (await PluginHub.get_sessions(user_id=user_id)).sessions
        matches = [(sid, details) for sid, details in sessions.items()
                   if not instance or instance in {details.hash, details.project,
                                                   details.project + "@" + details.hash}
                   or details.hash.startswith(instance)]
        if len(matches) == 1:
            instance = matches[0][1].hash
        elif instance and "@" in instance:
            instance = instance.rsplit("@", 1)[1]
        elif instance and re.fullmatch(r"[0-9a-fA-F]{16,64}", instance):
            pass  # Full instance hash remains stable across a plugin reconnect.
        else:
            return error("code_execution_instance_required", state="not_started")
    else:
        from transport.legacy.stdio_port_registry import stdio_port_registry
        info = stdio_port_registry.get_instance(instance)
        if info is None and not (instance and "@" in instance):
            return error("code_execution_instance_required", state="not_started")
        target = info.id if info else instance
        instance = target.rsplit("@", 1)[-1]
    if _is_http_transport():
        target = instance
    code_kwargs = dict(kwargs)
    code_kwargs.pop("params", None)

    async def send(command, arguments):
        return await _send_with_unity_instance_raw(
            send_fn, target, command, arguments, user_id=user_id,
            **{**code_kwargs, "retry_on_reload": False})

    return await get_coordinator().run(ctx, (user_id or "local") + ":" + instance,
                                     command, params, send)
