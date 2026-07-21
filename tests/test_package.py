import json
import re
from pathlib import Path

from dcc_mcp_unity import __version__
from dcc_mcp_unity.server import UnityMcpServer

ROOT = Path(__file__).parents[1]
PACKAGE = ROOT / "src" / "dcc_mcp_unity" / "unity_package"


def test_server_constructs_with_unity_contract():
    server = UnityMcpServer(port=0)
    try:
        assert server._options.server_name == "dcc-mcp-unity"
        assert server._options.dcc_name == "unity"
        assert server._execution_bridge.resolve_host_dispatcher() is server._host_dispatcher
    finally:
        server.stop()


def test_server_uses_dynamic_port_by_default(monkeypatch):
    monkeypatch.delenv("DCC_MCP_UNITY_PORT", raising=False)
    server = UnityMcpServer()
    try:
        assert server._options.port == 0
    finally:
        server.stop()


def test_bundled_skills_release_and_upm_package_exist():
    skills = ROOT / "src" / "dcc_mcp_unity" / "skills"
    assert {path.name for path in skills.iterdir() if path.is_dir()} == {
        "unity-diagnostics",
        "unity-project",
        "unity-scene",
    }
    assert (ROOT / ".github" / "workflows" / "release.yml").is_file()
    package = json.loads((PACKAGE / "package.json").read_text(encoding="utf-8"))
    assert package["name"] == "com.dcc-mcp.unity"
    assert package["unity"] == "2018.4"
    assert package["unityRelease"] == "36f1"
    assert package["dependencies"]["com.unity.nuget.newtonsoft-json"] == "3.2.2"
    assert (PACKAGE / "LICENSE.md").is_file()

    assembly = json.loads(
        (PACKAGE / "Editor" / "DccMcp.Unity.Editor.asmdef").read_text(encoding="utf-8")
    )
    assert assembly["references"] == []
    assert "rootNamespace" not in assembly
    assert assembly["includePlatforms"] == ["Editor"]

    test_assembly = json.loads(
        (PACKAGE / "Tests" / "Editor" / "DccMcp.Unity.Editor.Tests.asmdef").read_text(
            encoding="utf-8"
        )
    )
    assert test_assembly["references"] == ["DccMcp.Unity.Editor"]
    assert "rootNamespace" not in test_assembly
    assert test_assembly["overrideReferences"] is True
    assert test_assembly["precompiledReferences"] == ["Newtonsoft.Json.dll"]
    assert test_assembly["includePlatforms"] == ["Editor"]
    assert test_assembly["optionalUnityReferences"] == ["TestAssemblies"]
    assert (PACKAGE / "Tests" / "Editor" / "DccMcpCommandsTests.cs").is_file()

    legacy_project = ROOT / "tests" / "unity-2018-project"
    legacy_manifest = json.loads(
        (legacy_project / "Packages" / "manifest.json").read_text(encoding="utf-8")
    )
    assert legacy_manifest["dependencies"]["com.dcc-mcp.unity"] == (
        "file:../../../src/dcc_mcp_unity/unity_package"
    )
    assert legacy_manifest["testables"] == ["com.dcc-mcp.unity"]
    assert "2018.4.36f1" in (legacy_project / "ProjectSettings" / "ProjectVersion.txt").read_text(
        encoding="utf-8"
    )

    for skill in skills.iterdir():
        if skill.is_dir():
            dependencies = skill / "metadata" / "depends.md"
            lines = [
                line
                for line in dependencies.read_text(encoding="utf-8").splitlines()
                if line.strip()
            ]
            assert lines[-1] == "- dcc-diagnostics"


def test_unity_bridge_preserves_main_thread_and_undo_contracts():
    bridge = (PACKAGE / "Editor" / "DccMcpBridge.cs").read_text(encoding="utf-8")
    commands = (PACKAGE / "Editor" / "DccMcpCommands.cs").read_text(encoding="utf-8")
    console = (PACKAGE / "Editor" / "DccMcpConsole.cs").read_text(encoding="utf-8")
    assert "EditorApplication.update += OnEditorUpdate" in bridge
    assert "MaxPendingRequests" in bridge
    assert "RequestQueueLifetime" in bridge
    assert "_dcc_mcp_deadline_unix_ms" in bridge
    assert "MaxOutboundMessageBytes" in bridge
    assert "Unity response exceeds 900 KiB" in bridge
    assert "project_path_hash" in bridge
    assert "session_instance_id" in bridge
    assert "item.Socket.State != WebSocketState.Open" in bridge
    assert "EditorApplication.update -= OnEditorUpdate" in bridge
    assert "Pending.TryDequeue" in bridge
    assert "Undo.RegisterCreatedObjectUndo" in commands
    assert "Undo.RecordObject" in commands
    assert "Undo.IncrementCurrentGroup" in commands
    assert "Undo.RevertAllDownToGroup" in commands
    assert commands.index("var parent =") < commands.index("var gameObject = new GameObject")
    assert "double.IsNaN" in commands
    assert "max_nodes must be between 1 and 5000" in commands
    assert "EditorApplication.isCompiling" in commands
    assert "EditorApplication.isPlayingOrWillChangePlaymode" in commands
    assert "must be at most 120 characters" in commands
    assert "must be an integer" in commands
    assert "editor.read_console" in commands
    assert "Application.logMessageReceivedThreaded" in console
    assert "MaxEntries = 500" in console
    assert "MaxMessageCharacters" in console
    assert "MaxEntriesPayloadBytes" in console
    assert "limit must be an integer" in console
    assert "scene.create_game_object" in commands
    assert "CompileAssemblyFromSource" not in bridge + commands


def test_runtime_version_matches_distribution_metadata():
    pyproject = (ROOT / "pyproject.toml").read_text(encoding="utf-8")
    project_version = re.search(r'^version = "([^"]+)"$', pyproject, re.MULTILINE)
    assert project_version is not None
    assert __version__ == project_version.group(1)
    upm_package = json.loads((PACKAGE / "package.json").read_text(encoding="utf-8"))
    assert __version__ == upm_package["version"]
    for skill in (ROOT / "src" / "dcc_mcp_unity" / "skills").iterdir():
        if skill.is_dir():
            skill_text = (skill / "SKILL.md").read_text(encoding="utf-8")
            skill_version = re.search(r'^    version: "([^"]+)"', skill_text, re.MULTILINE)
            assert skill_version is not None
            assert __version__ == skill_version.group(1)

    lock = (ROOT / "uv.lock").read_text(encoding="utf-8")
    lock_package = re.search(
        r'name = "dcc-mcp-unity"\nversion = "([^"]+)" # x-release-please-version',
        lock,
    )
    assert lock_package is not None
    assert __version__ == lock_package.group(1)

    release_config = json.loads((ROOT / "release-please-config.json").read_text(encoding="utf-8"))
    extra_paths = {item["path"] for item in release_config["packages"]["."]["extra-files"]}
    assert {
        "src/dcc_mcp_unity/unity_package/package.json",
        "src/dcc_mcp_unity/skills/unity-project/SKILL.md",
        "src/dcc_mcp_unity/skills/unity-scene/SKILL.md",
        "src/dcc_mcp_unity/skills/unity-diagnostics/SKILL.md",
        "uv.lock",
    } <= extra_paths
