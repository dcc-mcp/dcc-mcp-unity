import importlib.util
import json
import re
from pathlib import Path

import pytest
import yaml
from dcc_mcp_core.skill import skill_error
from jsonschema import Draft7Validator

from dcc_mcp_unity.job_result import job_state_result

ROOT = Path(__file__).parents[1]
SKILLS = ROOT / "src" / "dcc_mcp_unity" / "skills"


def _load_script(skill: str, name: str):
    path = SKILLS / skill / "scripts" / f"{name}.py"
    spec = importlib.util.spec_from_file_location(f"test_{skill}_{name}", path)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


@pytest.mark.parametrize(
    ("skill", "name", "method", "arguments"),
    [
        ("unity-project", "read_text_asset", "assets.read_text", {"path": "Assets/Game.cs"}),
        (
            "unity-project",
            "upsert_text_asset",
            "assets.upsert_text",
            {
                "request_id": "778e72dd-e536-4ff8-aad0-9b752ab61c3b",
                "path": "Assets/Game.cs",
                "content": "class Game {}\n",
                "expected_sha256": "absent",
            },
        ),
        (
            "unity-project",
            "refresh_and_compile",
            "project.refresh_and_compile",
            {"request_id": "778e72dd-e536-4ff8-aad0-9b752ab61c3b"},
        ),
        (
            "unity-project",
            "set_play_mode",
            "editor.set_play_mode",
            {"request_id": "778e72dd-e536-4ff8-aad0-9b752ab61c3b", "play": True},
        ),
        (
            "unity-project",
            "build_windows_player",
            "project.build_windows_player",
            {"request_id": "778e72dd-e536-4ff8-aad0-9b752ab61c3b"},
        ),
        (
            "unity-diagnostics",
            "inspect_job",
            "jobs.inspect",
            {"request_id": "778e72dd-e536-4ff8-aad0-9b752ab61c3b"},
        ),
        (
            "unity-diagnostics",
            "capture_game_view",
            "editor.capture_game_view",
            {"request_id": "778e72dd-e536-4ff8-aad0-9b752ab61c3b"},
        ),
    ],
)
def test_game_authoring_wrappers_forward_only_typed_arguments(
    monkeypatch, skill, name, method, arguments
):
    module = _load_script(skill, name)
    calls = []
    monkeypatch.setattr(
        module,
        "call_host",
        lambda method, params=None: (
            calls.append((method, params)) or {"state": "queued", "phase": "queued"}
        ),
    )

    result = module.main(**arguments, ignored_core_metadata=True)

    assert result["success"] is True
    assert calls == [(method, arguments)]


def test_game_authoring_manifests_declare_the_bounded_surface():
    project = (SKILLS / "unity-project" / "tools.yaml").read_text(encoding="utf-8")
    diagnostics = (SKILLS / "unity-diagnostics" / "tools.yaml").read_text(encoding="utf-8")

    for name in (
        "read_text_asset",
        "upsert_text_asset",
        "refresh_and_compile",
        "set_play_mode",
        "build_windows_player",
    ):
        assert f"- name: {name}" in project
    for name in ("inspect_job", "capture_game_view"):
        assert f"- name: {name}" in diagnostics

    assert "maxLength: 262144" in project
    assert "DCC_MCP_UNITY_ALLOW_SOURCE_WRITES=1" in project
    assert "enum: [absent]" not in project
    assert project.count("pattern: '") >= 5
    assert "deferred_hint: true" not in project + diagnostics
    assert "queued/running/completed" not in project


def test_text_asset_schema_rejects_traversal_and_backslashes():
    manifest = yaml.safe_load((SKILLS / "unity-project" / "tools.yaml").read_text("utf-8"))
    tools = {tool["name"]: tool for tool in manifest["tools"]}
    for name in ("read_text_asset", "upsert_text_asset"):
        pattern = tools[name]["input_schema"]["properties"]["path"]["pattern"]
        assert re.fullmatch(pattern, "Assets/Game/Scripts/Player.cs")
        for invalid in (
            "Assets/../ProjectSettings/ProjectVersion.txt",
            "Assets/./Player.cs",
            r"Assets\Game\Player.cs",
            "Assets/Game/../Player.cs",
        ):
            assert re.fullmatch(pattern, invalid) is None


def test_job_tool_schemas_publish_states_and_accept_the_standard_error_envelope():
    job_tools = []
    for skill in ("unity-project", "unity-diagnostics"):
        manifest = yaml.safe_load((SKILLS / skill / "tools.yaml").read_text("utf-8"))
        job_tools.extend(
            tool
            for tool in manifest["tools"]
            if tool["name"]
            in {
                "upsert_text_asset",
                "refresh_and_compile",
                "set_play_mode",
                "build_windows_player",
                "inspect_job",
                "capture_game_view",
            }
        )

    assert len(job_tools) == 6
    standard_error = skill_error("failed", "transport error")
    successful_job = {
        "success": True,
        "message": "Unity job is queued.",
        "prompt": None,
        "error": None,
        "context": {"state": "queued", "phase": "queued"},
    }
    incomplete_success = {**successful_job, "context": {}}
    for tool in job_tools:
        schema = tool["output_schema"]
        context = schema["properties"]["context"]
        validator = Draft7Validator(schema)
        assert schema["required"] == ["success", "message", "prompt", "error", "context"]
        assert schema["properties"]["prompt"]["type"] == ["string", "null"]
        assert schema["properties"]["error"]["type"] == ["string", "null"]
        assert standard_error["context"] == {}
        validator.validate(standard_error)
        validator.validate(successful_job)
        assert not validator.is_valid(incomplete_success)
        assert context["properties"]["state"]["enum"] == [
            "queued",
            "running",
            "succeeded",
            "failed",
        ]
        assert tool["annotations"]["deferred_hint"] is False


def test_utf8_text_limit_fits_the_bridge_after_worst_case_json_escaping(monkeypatch):
    module = _load_script("unity-project", "upsert_text_asset")
    calls = []
    monkeypatch.setattr(module, "call_host", lambda *_args, **_kwargs: calls.append(True))

    result = module.main(
        request_id="778e72dd-e536-4ff8-aad0-9b752ab61c3b",
        path="Assets/Game.cs",
        content="\u00e9" * (128 * 1024 + 1),
        expected_sha256="absent",
    )

    assert result["success"] is False
    assert "256 KiB" in result["error"]
    assert calls == []
    escaped = json.dumps({"content": "\u00e9" * (128 * 1024)}).encode("utf-8")
    assert len(escaped) + 64 * 1024 < 900 * 1024


def test_text_wrapper_rejects_json_unsafe_controls_before_transport(monkeypatch):
    module = _load_script("unity-project", "upsert_text_asset")
    calls = []
    monkeypatch.setattr(module, "call_host", lambda *_args, **_kwargs: calls.append(True))

    result = module.main(
        request_id="778e72dd-e536-4ff8-aad0-9b752ab61c3b",
        path="Assets/Game.cs",
        content="unsafe\u0000text",
        expected_sha256="absent",
    )

    assert result["success"] is False
    assert "control characters" in result["error"]
    assert calls == []


def test_invalid_host_job_state_uses_schema_safe_error_context():
    result = job_state_result("Unity operation", {"state": "complete", "phase": "done"})

    assert result["success"] is False
    assert "state" not in result["context"]
    assert result["context"]["returned_state"] == "complete"


@pytest.mark.parametrize(
    ("state", "expected_success"),
    [("queued", True), ("succeeded", True), ("failed", False)],
)
def test_submit_wrapper_preserves_terminal_job_state(monkeypatch, state, expected_success):
    module = _load_script("unity-project", "upsert_text_asset")
    host_result = {
        "request_id": "778e72dd-e536-4ff8-aad0-9b752ab61c3b",
        "state": state,
        "phase": "complete",
    }
    if state == "failed":
        host_result["error"] = "compile failed"
    monkeypatch.setattr(module, "call_host", lambda *_args, **_kwargs: host_result)

    result = module.main(
        request_id="778e72dd-e536-4ff8-aad0-9b752ab61c3b",
        path="Assets/Game.cs",
        content="class Game {}\n",
        expected_sha256="absent",
    )

    assert result["success"] is expected_success
    assert result["context"]["state"] == state
    assert state in result["message"]
    assert "submitted" not in result["message"].lower()


def test_editor_job_protocol_is_persistent_fail_closed_and_bounded():
    editor = ROOT / "src" / "dcc_mcp_unity" / "unity_package" / "Editor"
    commands = (editor / "DccMcpCommands.cs").read_text(encoding="utf-8")
    jobs = (editor / "DccMcpJobs.cs").read_text(encoding="utf-8")

    for method in (
        "assets.read_text",
        "assets.upsert_text",
        "project.refresh_and_compile",
        "editor.set_play_mode",
        "jobs.inspect",
        "project.build_windows_player",
        "editor.capture_game_view",
    ):
        assert method in commands

    assert "SessionState.GetString" in jobs
    assert "SessionState.SetString" in jobs
    assert '"queued"' in jobs
    assert '"running"' in jobs
    assert '"succeeded"' in jobs
    assert '"failed"' in jobs
    assert "Guid.TryParseExact" in jobs
    assert "same request_id was already used with different parameters" in jobs
    assert "another mutating unity job is queued or running" in jobs.lower()
    assert '"DCC_MCP_UNITY_ALLOW_SOURCE_WRITES"' in jobs
    assert '"1"' in jobs
    assert "MaxTextAssetBytes = 256 * 1024" in jobs
    assert "AcquireAssetWriteLock" in jobs
    assert "File.Replace(temporaryPath, targetPath, backupPath)" in jobs
    assert "VerifyOpenedPath" in jobs
    assert "FileAttributes.ReparsePoint" in jobs
    assert "File.Replace" in jobs
    assert "EditorBuildSettings.scenes" in jobs
    assert "SwitchActiveBuildTarget" in jobs
    assert "activeBuildTarget" in jobs
    assert "openScene.isDirty" in jobs
    assert '"Builds", "DccMcp"' in jobs
    assert "BuildTarget.StandaloneWindows64" in jobs
    assert '"DccMcpGame_Data"' in jobs
    assert "ScreenCapture.CaptureScreenshot" in jobs
    assert 'GetType("UnityEditor.GameView")' in jobs
    assert "capture_after_frame" in jobs
    assert "ImageConversion.LoadImage" in jobs
    assert "Game View capture requires Play Mode" in jobs
    assert "CompileAssemblyFromSource" not in jobs
    assert "Process.Start" not in jobs


def test_source_write_security_contract_bounds_external_writer_and_reparse_races():
    jobs = (
        ROOT / "src" / "dcc_mcp_unity" / "unity_package" / "Editor" / "DccMcpJobs.cs"
    ).read_text(encoding="utf-8")
    documentation = "\n".join(
        path.read_text(encoding="utf-8")
        for path in (
            ROOT / "README.md",
            ROOT / "docs" / "architecture-benchmark.md",
            ROOT / "src" / "dcc_mcp_unity" / "unity_package" / "README.md",
        )
    ).lower()

    assert "file.replace(backup" not in jobs.lower()
    assert "preserved conflict backup" in jobs.lower()
    assert "File.Exists(current) || Directory.Exists(current)" not in jobs
    assert "CheckReparsePointIfPresent(current)" in jobs
    assert "cooperative" in documentation
    assert "same-user" in documentation
    assert "conflict backup" in documentation
