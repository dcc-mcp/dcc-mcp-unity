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


def _process_is_alive(pid: int) -> bool:
    if pid <= 0:
        return False
    if os.name == "nt":
        import ctypes
        from ctypes import wintypes

        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        kernel32.OpenProcess.restype = wintypes.HANDLE
        kernel32.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        kernel32.WaitForSingleObject.restype = wintypes.DWORD
        kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
        kernel32.CloseHandle.restype = wintypes.BOOL
        handle = kernel32.OpenProcess(0x00100000, False, pid)
        if not handle:
            error = ctypes.get_last_error()
            if error == 5:
                return True
            if error == 87:
                return False
            raise OSError(error, ctypes.FormatError(error))
        try:
            result = kernel32.WaitForSingleObject(handle, 0)
            if result == 258:
                return True
            if result == 0:
                return False
            raise OSError(ctypes.get_last_error(), "WaitForSingleObject failed")
        finally:
            kernel32.CloseHandle(handle)

    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    return True


def _watch_pid(pid: int, stop: threading.Event) -> None:
    while not stop.is_set():
        if not _process_is_alive(pid):
            stop.set()
            return
        time.sleep(1.0)


def _claim_pid_file(path: str | None) -> Path | None:
    if not path:
        return None
    pid_path = Path(path).expanduser().resolve()
    pid_path.parent.mkdir(parents=True, exist_ok=True)
    candidate = pid_path.with_name(
        f".{pid_path.name}.{os.getpid()}.{threading.get_ident()}.{time.time_ns()}"
    )
    candidate.write_text(str(os.getpid()), encoding="ascii")
    try:
        while True:
            try:
                os.link(candidate, pid_path)
                return pid_path
            except FileExistsError:
                pass
            try:
                existing = pid_path.stat()
                raw_pid = pid_path.read_text(encoding="ascii").strip()
            except FileNotFoundError:
                continue
            try:
                old_pid = int(raw_pid)
            except ValueError:
                old_pid = None
            if old_pid is not None and _process_is_alive(old_pid):
                raise SystemExit(f"dcc-mcp-unity sidecar is already running (pid {old_pid})")
            try:
                current = pid_path.stat()
                if (current.st_dev, current.st_ino) == (existing.st_dev, existing.st_ino):
                    pid_path.unlink()
            except FileNotFoundError:
                pass
    finally:
        candidate.unlink(missing_ok=True)


def _release_pid_file(pid_path: Path | None) -> None:
    if pid_path is None:
        return
    try:
        existing = pid_path.stat()
        owner = int(pid_path.read_text(encoding="ascii").strip())
        current = pid_path.stat()
    except (FileNotFoundError, ValueError):
        return
    if owner == os.getpid() and (current.st_dev, current.st_ino) == (
        existing.st_dev,
        existing.st_ino,
    ):
        pid_path.unlink(missing_ok=True)


def main(argv: Sequence[str] | None = None) -> None:
    """Run the adapter CLI or a core-managed Python skill script."""
    resolved = list(sys.argv if argv is None else argv)
    os.environ.setdefault("DCC_MCP_PYTHON_EXECUTABLE", sys.executable)
    if _is_skill_script(resolved):
        _run_skill_script(resolved)
        return
    if len(resolved) > 1 and resolved[1] in {
        "install",
        "status",
        "verify",
        "uninstall",
        "upgrade",
    }:
        from .install import main as _install_main

        _install_main(resolved[1:])
        return

    options = _parse_args(resolved)
    _apply_options(options)
    pid_file = _claim_pid_file(options.pid_file)
    if options.watch_pid is None:
        try:
            _server_main()
        finally:
            _release_pid_file(pid_file)
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
        _release_pid_file(pid_file)


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
