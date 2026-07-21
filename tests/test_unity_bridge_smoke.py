import json
from pathlib import Path

import pytest
from dcc_mcp_core import BridgeConnectionError

from tools import unity_bridge_smoke


class FakeBridge:
    def is_connected(self) -> bool:
        return True


def test_bridge_smoke_records_verified_editor(monkeypatch, tmp_path: Path):
    stopped = []
    monkeypatch.setattr(unity_bridge_smoke, "start_bridge", FakeBridge)
    monkeypatch.setattr(
        unity_bridge_smoke,
        "call_host",
        lambda method: {"engine_version": "6000.5.4f1", "name": "CI Project"},
    )
    monkeypatch.setattr(unity_bridge_smoke, "stop_bridge", lambda: stopped.append(True))

    output = tmp_path / "bridge-smoke.json"
    ready = tmp_path / "bridge-ready.json"
    result = unity_bridge_smoke.run_smoke(
        expected_version="6000.5.4f1",
        output=output,
        ready=ready,
        timeout_seconds=1,
    )

    assert result == json.loads(output.read_text(encoding="utf-8"))
    assert result["status"] == "passed"
    assert result["engine_version"] == "6000.5.4f1"
    assert result["project_name"] == "CI Project"
    assert json.loads(ready.read_text(encoding="utf-8"))["status"] == "listening"
    assert stopped == [True]


def test_bridge_smoke_retries_transient_disconnect(monkeypatch, tmp_path: Path):
    attempts = []

    def call_host(method):
        attempts.append(method)
        if len(attempts) == 1:
            raise BridgeConnectionError("domain reload")
        return {"engine_version": "2021.3.45f1", "name": "CI Project"}

    monkeypatch.setattr(unity_bridge_smoke, "start_bridge", FakeBridge)
    monkeypatch.setattr(unity_bridge_smoke, "call_host", call_host)
    monkeypatch.setattr(unity_bridge_smoke, "stop_bridge", lambda: None)
    monkeypatch.setattr(unity_bridge_smoke.time, "sleep", lambda _seconds: None)

    result = unity_bridge_smoke.run_smoke(
        expected_version="2021.3.45f1",
        output=tmp_path / "bridge-smoke.json",
        ready=tmp_path / "bridge-ready.json",
        timeout_seconds=1,
    )

    assert result["engine_version"] == "2021.3.45f1"
    assert attempts == ["project.inspect", "project.inspect"]


def test_bridge_smoke_rejects_wrong_editor_version(monkeypatch, tmp_path: Path):
    monkeypatch.setattr(unity_bridge_smoke, "start_bridge", FakeBridge)
    monkeypatch.setattr(
        unity_bridge_smoke,
        "call_host",
        lambda method: {"engine_version": "2021.3.45f1", "name": "Wrong Project"},
    )
    monkeypatch.setattr(unity_bridge_smoke, "stop_bridge", lambda: None)
    output = tmp_path / "bridge-smoke.json"

    with pytest.raises(RuntimeError, match="expected Unity 6000.5.4f1, got 2021.3.45f1"):
        unity_bridge_smoke.run_smoke(
            expected_version="6000.5.4f1",
            output=output,
            ready=tmp_path / "bridge-ready.json",
            timeout_seconds=1,
        )

    assert not output.exists()
