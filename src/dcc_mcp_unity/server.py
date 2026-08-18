"""Unity MCP server composition and lifecycle."""

from __future__ import annotations

import logging
import os
import signal
import sys
import threading
import time
from pathlib import Path
from typing import Any, Optional

from dcc_mcp_core import DccServerOptions, HostExecutionBridge
from dcc_mcp_core.bridge import BridgeConnectionError, BridgeRpcError, BridgeTimeoutError
from dcc_mcp_core.host import QueueDispatcher, StandaloneHost
from dcc_mcp_core.readiness import AdapterReadinessBinder
from dcc_mcp_core.server_base import DccServerBase

from .__version__ import __version__
from .bridge import (
    get_bridge,
    probe_host_dispatch,
    set_host_dispatch_ready,
    start_bridge,
    stop_bridge,
)
from .dispatcher import UnityBridgeDispatcher

_server: Optional["UnityMcpServer"] = None
_READINESS_POLL_SECONDS = 0.25
_BRIDGE_DISCONNECT_GRACE_SECONDS = 5.0
_HOST_PROBE_DEADLINE_SECONDS = 1.0
_HOST_PROBE_STALE_SECONDS = 2.0
_logger = logging.getLogger(__name__)


def publish_scene_snapshot(snapshot: dict[str, Any]) -> None:
    """Publish a bounded Unity scene inspection through Core resources."""
    if _server is not None:
        _server.set_scene_resource(snapshot)


class UnityMcpServer(DccServerBase):
    """DCC-MCP server backed by the bundled Unity Editor package."""

    def __init__(self, port: Optional[int] = None) -> None:
        self._host_dispatcher = QueueDispatcher()
        self._host_driver = StandaloneHost(
            self._host_dispatcher,
            thread_name="dcc-mcp-unity-host",
        )
        execution_bridge = HostExecutionBridge(
            dispatcher=UnityBridgeDispatcher(),
            host_dispatcher=self._host_dispatcher,
            default_thread_affinity="main",
            default_execution="sync",
            default_timeout_hint_secs=60,
        )
        options = DccServerOptions.from_env(
            "unity",
            Path(__file__).resolve().parent / "skills",
            port=port,
            server_name="dcc-mcp-unity",
            server_version=__version__,
            execution_bridge=execution_bridge,
        )
        super().__init__(options=options)
        self._readiness = AdapterReadinessBinder(self)
        self._readiness_stop = threading.Event()
        self._readiness_thread: Optional[threading.Thread] = None
        self._bridge_ready = False
        self._bridge_disconnected_at: Optional[float] = None
        self._host_dispatch_ready = False
        self._host_probe_lock = threading.Lock()
        self._host_probe_thread: Optional[threading.Thread] = None
        self._host_probe_completed_at: Optional[float] = None
        self._host_probe_responded = False
        self._set_bridge_readiness(False, False)

    def start(self, **kwargs: Any) -> Any:
        start_bridge()
        try:
            self._host_driver.start()
            handle = super().start(**kwargs)
            self._start_readiness_monitor()
            return handle
        except Exception:
            try:
                super().stop()
            finally:
                self._host_driver.stop()
                stop_bridge()
            raise

    def stop(self) -> None:
        self._stop_readiness_monitor()
        try:
            super().stop()
        finally:
            try:
                self._host_driver.stop()
            finally:
                stop_bridge()

    def _set_bridge_readiness(self, transport_ready: bool, host_dispatch_ready: bool) -> None:
        self._bridge_ready = transport_ready
        self._host_dispatch_ready = host_dispatch_ready
        set_host_dispatch_ready(host_dispatch_ready)
        self._readiness.mark_dispatcher_ready(
            transport_ready,
            host_execution_bridge_ready=transport_ready,
            main_thread_executor_ready=transport_ready,
            dcc_ready=transport_ready,
        )

    def _sync_bridge_readiness(self) -> bool:
        connected = get_bridge().is_connected()
        if connected:
            self._bridge_disconnected_at = None
            self._ensure_host_probe()
        elif self._bridge_ready:
            now = time.monotonic()
            if self._bridge_disconnected_at is None:
                self._bridge_disconnected_at = now
            if now - self._bridge_disconnected_at < _BRIDGE_DISCONNECT_GRACE_SECONDS:
                self._set_bridge_readiness(True, self._host_dispatch_ready)
                return True
        host_ready = connected and self._host_probe_is_fresh()
        self._set_bridge_readiness(connected, host_ready)
        return connected

    def _ensure_host_probe(self) -> None:
        with self._host_probe_lock:
            if self._host_probe_thread is not None and self._host_probe_thread.is_alive():
                return
            thread = threading.Thread(
                target=self._run_host_probe,
                name="dcc-mcp-unity-host-probe",
                daemon=True,
            )
            self._host_probe_thread = thread
            thread.start()

    def _run_host_probe(self) -> None:
        responded = False
        try:
            probe_host_dispatch(_HOST_PROBE_DEADLINE_SECONDS)
            responded = True
        except BridgeRpcError:
            # Any Unity RPC response, including an expired queued probe, proves
            # that EditorApplication.update is dispatching again.
            responded = True
        except (BridgeConnectionError, BridgeTimeoutError, OSError):
            responded = False
        except Exception:
            _logger.exception("Unity host responsiveness probe failed")
            responded = False
        finally:
            with self._host_probe_lock:
                self._host_probe_completed_at = time.monotonic()
                self._host_probe_responded = responded

    def _host_probe_is_fresh(self) -> bool:
        now = time.monotonic()
        with self._host_probe_lock:
            completed_at = self._host_probe_completed_at
            responded = self._host_probe_responded
        if completed_at is not None and responded:
            return now - completed_at <= _HOST_PROBE_STALE_SECONDS
        return False

    def _start_readiness_monitor(self) -> None:
        if self._readiness_thread is not None and self._readiness_thread.is_alive():
            return
        self._readiness_stop.clear()
        self._sync_bridge_readiness()
        self._readiness_thread = threading.Thread(
            target=self._monitor_bridge_readiness,
            name="dcc-mcp-unity-readiness",
            daemon=True,
        )
        self._readiness_thread.start()

    def _monitor_bridge_readiness(self) -> None:
        while not self._readiness_stop.wait(_READINESS_POLL_SECONDS):
            self._sync_bridge_readiness()

    def _stop_readiness_monitor(self) -> None:
        self._readiness_stop.set()
        thread, self._readiness_thread = self._readiness_thread, None
        if thread is not None and thread is not threading.current_thread():
            thread.join(timeout=1.0)
        self._set_bridge_readiness(False, False)

    def _version_string(self) -> str:
        return os.environ.get("DCC_MCP_UNITY_VERSION", "unknown")


def start_server(port: Optional[int] = None) -> UnityMcpServer:
    global _server
    if _server is None or not _server.is_running:
        _server = UnityMcpServer(port)
        _server.register_builtin_actions()
        _server.start()
    return _server


def stop_server() -> None:
    global _server
    if _server is not None:
        _server.stop()
        _server = None


def main() -> None:
    """Run the standalone adapter until interrupted."""
    stopped = threading.Event()
    signal.signal(signal.SIGINT, lambda *_: stopped.set())
    if hasattr(signal, "SIGTERM"):
        signal.signal(signal.SIGTERM, lambda *_: stopped.set())
    start_server()
    try:
        stopped.wait()
    finally:
        stop_server()


def _run_module_entrypoint() -> None:
    sys.modules["dcc_mcp_unity.server"] = sys.modules[__name__]
    main()


if __name__ == "__main__":
    _run_module_entrypoint()
