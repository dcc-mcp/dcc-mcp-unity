import importlib.util
import json
import sys
import types
from pathlib import Path

import pytest
import yaml
from jsonschema import Draft7Validator

ROOT = Path(__file__).parents[1]
SKILL = ROOT / "src/dcc_mcp_unity/skills/unity-diagnostics"


def envelope(success, message, error=None, **context):
    return {
        "success": success,
        "message": message,
        "error": error,
        "prompt": None,
        "context": context,
    }


@pytest.fixture
def wrapper(monkeypatch):
    # Exercise the wrapper without importing Core's native SDK or connecting a bridge.
    core = types.ModuleType("dcc_mcp_core")
    core_skill = types.ModuleType("dcc_mcp_core.skill")
    core_skill.skill_entry = lambda function: function
    core_skill.skill_success = lambda message, **context: envelope(True, message, **context)
    core_skill.skill_error = lambda message, error, **context: envelope(
        False, message, error, **context
    )
    bridge = types.ModuleType("dcc_mcp_unity.bridge")
    bridge.call_host = lambda *_args: pytest.fail("Unexpected unbound Host call")
    monkeypatch.setitem(sys.modules, "dcc_mcp_core", core)
    monkeypatch.setitem(sys.modules, "dcc_mcp_core.skill", core_skill)
    monkeypatch.setitem(sys.modules, "dcc_mcp_unity.bridge", bridge)
    path = SKILL / "scripts/inspect_dirty_assets.py"
    spec = importlib.util.spec_from_file_location("test_dirty_wrapper", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


@pytest.fixture
def declaration():
    tools = yaml.safe_load((SKILL / "tools.yaml").read_text("utf-8"))["tools"]
    return next(tool for tool in tools if tool["name"] == "inspect_dirty_assets")


def snapshot(count=41):
    item = {
        "object_id": "-6450",
        "type": "UnityEngine.Object, UnityEngine.CoreModule",
        "name": "exact name\n\u6d4b\u8bd5",
        "path": "",
        "hide_flags": 61,
        "is_main_asset": False,
        "is_sub_asset": False,
        "is_persistent": True,
        "is_dirty": True,
        "asset_identity_status": "unknown",
        "asset_guid": None,
        "local_file_id": None,
        "asset_identity_reason": "not_found",
        "global_object_id_status": "unknown",
        "global_object_id": None,
        "global_object_id_reason": "default_identifier",
        "field_errors": {},
        "identity_complete": False,
    }
    return {
        "schema_version": 1,
        "capture_id": "f" * 32,
        "sampled_at_utc": "2026-10-10T00:00:00.0000000Z",
        "owner": {
            "process_id": 42,
            "project_path": "C:/OwnedProject",
            "engine_version": "2022.3",
            "session_instance_id": "a" * 32,
        },
        "source": {"assembly_full_name": "DccMcp.Unity.Editor", "module_version_id": "fixture"},
        "dirty_persistent_count": count,
        "items_count": count,
        "complete": True,
        "incomplete": False,
        "identity_complete": False,
        "budget_bytes": 786432,
        "required_bytes": None,
        "error_code": None,
        "error_message": None,
        "items": [{**item, "object_id": str(-6450 - offset)} for offset in range(count)],
    }


def test_wrapper_preserves_all_exact_records_once(wrapper, monkeypatch, declaration):
    value = snapshot()
    value["items"][0].update(
        asset_identity_status="known",
        asset_guid="a" * 32,
        local_file_id="9223372036854775807",
        asset_identity_reason=None,
    )
    calls = []
    monkeypatch.setattr(
        wrapper, "call_host", lambda method, params: calls.append((method, params)) or value
    )
    result = wrapper.main()
    assert calls == [("editor.inspect_dirty_assets", {})]
    assert result["success"] is True
    assert result["context"]["items"] == value["items"]
    assert len(result["context"]["items"]) == 41
    assert result["context"]["items"][0]["local_file_id"] == "9223372036854775807"
    assert result["context"]["items"][0]["path"] == ""
    assert result["context"]["identity_complete"] is False
    Draft7Validator(declaration["output_schema"]).validate(result)


@pytest.mark.parametrize("code", ["response_budget_exceeded", "dirty_query_unsupported"])
def test_wrapper_rejects_incomplete_without_retry(wrapper, monkeypatch, declaration, code):
    value = snapshot(0)
    value.update(
        dirty_persistent_count=None if code == "dirty_query_unsupported" else 901,
        complete=False,
        incomplete=True,
        error_code=code,
        error_message="Original native reason",
        required_bytes=900000 if code == "response_budget_exceeded" else None,
    )
    calls = []
    monkeypatch.setattr(
        wrapper, "call_host", lambda method, params: calls.append((method, params)) or value
    )
    result = wrapper.main()
    assert len(calls) == 1
    assert result["success"] is False
    assert result["error"] == code
    assert result["context"]["items"] == []
    assert result["context"]["dirty_persistent_count"] == value["dirty_persistent_count"]
    Draft7Validator(declaration["output_schema"]).validate(result)


@pytest.mark.parametrize(
    "change",
    [
        {"items_count": 32},
        {"dirty_persistent_count": True},
        {"items": []},
        {"dirty_persistent_count": -1},
        {"owner": {}},
        {"source": {}},
        {"schema_version": True},
        {"error_code": "response_budget_exceeded"},
    ],
)
def test_wrapper_fails_closed_on_inconsistent_counts(wrapper, monkeypatch, change):
    monkeypatch.setattr(wrapper, "call_host", lambda *_args: {**snapshot(), **change})
    result = wrapper.main()
    assert result["success"] is False
    assert result["error"] == "dirty_snapshot_invalid"


def test_schema_rejects_extra_input_and_incomplete_success(declaration):
    Draft7Validator.check_schema(declaration["input_schema"])
    Draft7Validator.check_schema(declaration["output_schema"])
    inputs = Draft7Validator(declaration["input_schema"])
    assert inputs.is_valid({})
    assert not inputs.is_valid({"limit": 32})
    assert not inputs.is_valid({"code": "anything"})
    outputs = Draft7Validator(declaration["output_schema"])
    assert outputs.is_valid(envelope(False, "original error", "transport_error"))
    assert not outputs.is_valid(envelope(True, "bad", **{"complete": False}))
    assert declaration["read_only"] is True
    assert declaration["destructive"] is False
    assert declaration["affinity"] == "main"
    assert declaration["enforce_thread_affinity"] is True
    # Large identifiers and exact text remain JSON strings after a browser-style round trip.
    value = snapshot(1)
    assert json.loads(json.dumps(value)) == value


def test_wrapper_rejects_parameters_before_dispatch(wrapper):
    result = wrapper.main(limit=32)
    assert result["success"] is False
    assert result["error"] == "invalid_parameters"
