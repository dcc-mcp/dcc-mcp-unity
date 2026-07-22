"""Unity MCP server composition and lifecycle."""

from __future__ import annotations

import os
import signal
import threading
from pathlib import Path
from typing import Any, Optional

from dcc_mcp_core import DccServerOptions, HostExecutionBridge
from dcc_mcp_core.host import QueueDispatcher, StandaloneHost
from dcc_mcp_core.readiness import AdapterReadinessBinder
from dcc_mcp_core.server_base import DccServerBase

from .__version__ import __version__
from .bridge import get_bridge, start_bridge, stop_bridge
from .dispatcher import UnityBridgeDispatcher

_server: Optional["UnityMcpServer"] = None
_READINESS_POLL_SECONDS = 0.25


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
        self._set_bridge_readiness(False)

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

    def _set_bridge_readiness(self, ready: bool) -> None:
        self._readiness.mark_dispatcher_ready(
            ready,
            host_execution_bridge_ready=ready,
            main_thread_executor_ready=ready,
            dcc_ready=ready,
        )

    def _sync_bridge_readiness(self) -> bool:
        ready = get_bridge().is_connected()
        self._set_bridge_readiness(ready)
        return ready

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
        self._set_bridge_readiness(False)

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


if __name__ == "__main__":
    main()
