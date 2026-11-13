"""Transport helpers for routing commands to Unity."""
from __future__ import annotations

import logging
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


async def send_with_unity_instance(
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
        if code_execution:
            retry_on_reload = False

        try:
            raw = await PluginHub.send_command_for_instance(
                unity_instance,
                command_type,
                params,
                user_id=user_id,
                retry_on_reload=retry_on_reload,
            )
            result = normalize_unity_response(raw)
            if code_execution and isinstance(result, dict) and result.get("hint") == "retry":
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
