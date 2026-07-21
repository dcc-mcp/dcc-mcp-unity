"""Loopback WebSocket bridge shared by the MCP server and Unity package."""

from __future__ import annotations

import math
import os
import threading
import time
from typing import Any

from dcc_mcp_core.bridge import DccBridge

_bridge: DccBridge | None = None
_lock = threading.Lock()
_HOST_REQUEST_LIFETIME_SECONDS = 55


def _bridge_timeout() -> float:
    value = float(os.environ.get("DCC_MCP_UNITY_BRIDGE_TIMEOUT", "60"))
    if not math.isfinite(value) or value < 60:
        raise ValueError(
            "DCC_MCP_UNITY_BRIDGE_TIMEOUT must be a finite value of at least 60 seconds"
        )
    return value


def get_bridge() -> DccBridge:
    """Return the process-wide bridge, creating it without starting it."""
    global _bridge
    with _lock:
        if _bridge is None:
            port = int(os.environ.get("DCC_MCP_UNITY_BRIDGE_PORT", "3852"))
            _bridge = DccBridge(
                host="127.0.0.1",
                port=port,
                timeout=_bridge_timeout(),
                server_name="dcc-mcp-unity",
            )
            os.environ.setdefault("DCC_MCP_UNITY_BRIDGE_URL", f"ws://127.0.0.1:{port}")
        return _bridge


def start_bridge() -> DccBridge:
    bridge = get_bridge()
    bridge.connect(wait_for_dcc=False)
    return bridge


def stop_bridge() -> None:
    global _bridge
    with _lock:
        bridge, _bridge = _bridge, None
    if bridge is not None:
        bridge.disconnect()


def call_host(method: str, params: dict[str, Any] | None = None) -> dict[str, Any]:
    """Invoke one typed command in the connected Unity Editor."""
    request_params = dict(params or {})
    request_params["_dcc_mcp_deadline_unix_ms"] = int(
        (time.time() + _HOST_REQUEST_LIFETIME_SECONDS) * 1000
    )
    result = get_bridge().call(method, **request_params)
    return result if isinstance(result, dict) else {"value": result}
