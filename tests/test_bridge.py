import pytest

from dcc_mcp_unity import bridge


class FakeBridge:
    def call(self, method, **params):
        return {"method": method, "params": params}


def test_call_host_forwards_typed_method_and_parameters(monkeypatch):
    monkeypatch.setattr(bridge, "get_bridge", lambda: FakeBridge())
    monkeypatch.setattr(bridge.time, "time", lambda: 1_000.0)
    assert bridge.call_host("scene.inspect", {"depth": 4}) == {
        "method": "scene.inspect",
        "params": {"depth": 4, "_dcc_mcp_deadline_unix_ms": 1_055_000},
    }


def test_bridge_timeout_rejects_stale_mutation_window(monkeypatch):
    monkeypatch.setenv("DCC_MCP_UNITY_BRIDGE_TIMEOUT", "30")
    with pytest.raises(ValueError, match="at least 60 seconds"):
        bridge._bridge_timeout()
