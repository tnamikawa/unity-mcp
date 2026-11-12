import pytest

from services.resources.editor_state import EditorStateData, _enrich_advice_and_staleness

from services.registry import get_registered_resources

from .test_helpers import DummyContext


@pytest.mark.parametrize("section,details,reason", [
    ("compilation", {"is_compile_pending": True}, "compilation_pending"),
    ("compilation", {"has_compile_errors": True}, "compilation_failed"),
    ("assets", {"is_updating": True}, "asset_refresh"),
    ("editor", {"play_mode": {"is_changing": True}}, "playmode_transition"),
    ("execution", {"is_running": True, "execution_id": "running-1"}, "execution_in_progress"),
])
def test_execution_gate_states_survive_model_and_block_readiness(monkeypatch, section, details, reason):
    monkeypatch.setattr("services.resources.editor_state._now_unix_ms", lambda: 1000)
    state = EditorStateData(
        schema_version="unity-mcp/editor_state@2", observed_at_unix_ms=1000,
        sequence=1, **{section: details},
    ).model_dump()
    enriched = _enrich_advice_and_staleness(state)
    assert enriched["advice"]["ready_for_tools"] is False
    assert reason in enriched["advice"]["blocking_reasons"]


def test_stable_play_mode_is_not_a_transition(monkeypatch):
    monkeypatch.setattr("services.resources.editor_state._now_unix_ms", lambda: 1000)
    state = {"observed_at_unix_ms": 1000,
             "editor": {"play_mode": {"is_playing": True, "is_changing": False}}}
    assert _enrich_advice_and_staleness(state)["advice"]["ready_for_tools"] is True


@pytest.mark.asyncio
async def test_editor_state_v2_is_registered_and_has_contract_fields(monkeypatch):
    """
    Canonical editor state resource should be `mcpforunity://editor/state` and conform to v2 contract fields.
    """
    # Import module to ensure it registers its decorator without disturbing global registry state.
    import services.resources.editor_state  # noqa: F401

    resources = get_registered_resources()

    state_res = next(
        (r for r in resources if r.get("uri") == "mcpforunity://editor/state"),
        None,
    )
    assert state_res is not None, (
        "Expected canonical editor state resource `mcpforunity://editor/state` to be registered. "
        "This is required so clients can poll readiness/staleness and avoid tool loops."
    )

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        # Minimal stub payload for v2 resource tests. The server layer should enrich with staleness/advice.
        assert command_type == "get_editor_state"
        return {
            "success": True,
            "data": {
                "schema_version": "unity-mcp/editor_state@2",
                "observed_at_unix_ms": 1730000000000,
                "sequence": 1,
                "compilation": {"is_compiling": False, "is_domain_reload_pending": False},
                "tests": {"is_running": False},
            },
        }

    # Patch transport so the resource can be invoked without Unity running.
    import transport.unity_transport as unity_transport
    monkeypatch.setattr(unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)

    result = await state_res["func"](DummyContext())
    payload = result.model_dump() if hasattr(result, "model_dump") else result
    assert isinstance(payload, dict)

    # Contract assertions (top-level)
    assert payload.get("success") is True
    data = payload.get("data")
    assert isinstance(data, dict)
    assert data.get("schema_version") == "unity-mcp/editor_state@2"
    assert "observed_at_unix_ms" in data
    assert "sequence" in data
    assert "advice" in data
    assert "staleness" in data


