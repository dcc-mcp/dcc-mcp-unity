from __future__ import annotations

import importlib.util
from pathlib import Path

from dcc_mcp_unity import server


def _load_inspect_scene_module():
    path = (
        Path(__file__).parents[1]
        / "src"
        / "dcc_mcp_unity"
        / "skills"
        / "unity-scene"
        / "scripts"
        / "inspect_scene.py"
    )
    spec = importlib.util.spec_from_file_location("unity_inspect_scene", path)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def test_publish_scene_snapshot_uses_live_server(monkeypatch) -> None:
    published = []
    fake_server = type(
        "FakeServer",
        (),
        {"set_scene_resource": lambda self, snapshot: published.append(snapshot)},
    )()
    monkeypatch.setattr(server, "_server", fake_server)

    snapshot = {"scene": "Assets/Scenes/SampleScene.scene", "nodes": []}
    server.publish_scene_snapshot(snapshot)

    assert published == [snapshot]


def test_inspect_scene_publishes_returned_snapshot(monkeypatch) -> None:
    module = _load_inspect_scene_module()
    snapshot = {"scene": "Assets/Scenes/SampleScene.scene", "nodes": [{"name": "Hero"}]}
    published = []
    monkeypatch.setattr(module, "call_host", lambda method, params: snapshot)
    monkeypatch.setattr(module, "publish_scene_snapshot", published.append)

    result = module.main(max_nodes=25)

    assert result["success"] is True
    assert published == [snapshot]
