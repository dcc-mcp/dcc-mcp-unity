"""Entry point for the PyOxidizer standalone Unity sidecar."""

from __future__ import annotations

import argparse
import os
import runpy
import sys
import threading
import time
from pathlib import Path
from typing import Sequence

from .__version__ import __version__
from .server import main as _server_main


def _is_skill_script(argv: Sequence[str]) -> bool:
    if len(argv) < 2:
        return False
    script = Path(argv[1])
    return script.suffix.lower() in {".py", ".pyw"} and script.is_file()


def _run_skill_script(argv: Sequence[str]) -> None:
    script = str(Path(argv[1]).resolve())
    original_argv = sys.argv
    sys.argv = [script, *argv[2:]]
    try:
        runpy.run_path(script, run_name="__main__")
    finally:
        sys.argv = original_argv


def _parse_args(argv: Sequence[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Run the DCC-MCP Unity standalone sidecar.",
    )
    parser.add_argument("--version", action="version", version=__version__)
    parser.add_argument("--bridge-port", type=int, help="Loopback Unity bridge port.")
    parser.add_argument("--mcp-port", type=int, help="Direct MCP server port.")
    parser.add_argument("--watch-pid", type=int, help="Exit when the Unity process exits.")
    parser.add_argument("--pid-file", help="Single-instance pid file.")
    return parser.parse_args(argv[1:])


def _apply_options(options: argparse.Namespace) -> None:
    if options.bridge_port is not None:
        os.environ["DCC_MCP_UNITY_BRIDGE_PORT"] = str(options.bridge_port)
        os.environ["DCC_MCP_UNITY_BRIDGE_URL"] = f"ws://127.0.0.1:{options.bridge_port}"
    if options.mcp_port is not None:
        os.environ["DCC_MCP_UNITY_PORT"] = str(options.mcp_port)


def _watch_pid(pid: int, stop: threading.Event) -> None:
    while not stop.is_set():
        try:
            os.kill(pid, 0)
        except (OSError, ProcessLookupError):
            stop.set()
            return
        time.sleep(1.0)


def _claim_pid_file(path: str | None) -> Path | None:
    if not path:
        return None
    pid_path = Path(path).expanduser().resolve()
    if pid_path.exists():
        try:
            old_pid = int(pid_path.read_text(encoding="ascii").strip())
            os.kill(old_pid, 0)
        except (OSError, ProcessLookupError, ValueError):
            pid_path.unlink(missing_ok=True)
        else:
            raise SystemExit(f"dcc-mcp-unity sidecar is already running (pid {old_pid})")
    pid_path.parent.mkdir(parents=True, exist_ok=True)
    pid_path.write_text(str(os.getpid()), encoding="ascii")
    return pid_path


def main(argv: Sequence[str] | None = None) -> None:
    """Run the adapter CLI or a core-managed Python skill script."""
    resolved = list(sys.argv if argv is None else argv)
    os.environ.setdefault("DCC_MCP_PYTHON_EXECUTABLE", sys.executable)
    if _is_skill_script(resolved):
        _run_skill_script(resolved)
        return

    options = _parse_args(resolved)
    _apply_options(options)
    pid_file = _claim_pid_file(options.pid_file)
    if options.watch_pid is None:
        try:
            _server_main()
        finally:
            if pid_file is not None:
                pid_file.unlink(missing_ok=True)
        return

    stopped = threading.Event()
    watcher = threading.Thread(
        target=_watch_pid,
        args=(options.watch_pid, stopped),
        name="dcc-mcp-unity-watch-pid",
        daemon=True,
    )
    watcher.start()
    try:
        _server_main_until(stopped)
    finally:
        stopped.set()
        if pid_file is not None:
            pid_file.unlink(missing_ok=True)


def _server_main_until(stopped: threading.Event) -> None:
    """Run the existing server lifecycle while a watch event remains clear."""
    from .server import start_server, stop_server

    start_server()
    try:
        stopped.wait()
    finally:
        stop_server()


if __name__ == "__main__":
    main()
