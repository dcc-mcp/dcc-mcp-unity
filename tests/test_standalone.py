from __future__ import annotations

import os
import subprocess
import sys
import threading
from pathlib import Path

from dcc_mcp_unity._standalone_entry import (
    _claim_pid_file,
    _parse_args,
    _process_is_alive,
    _release_pid_file,
)


def test_standalone_options_are_explicit() -> None:
    options = _parse_args(
        ["dcc-mcp-unity", "--bridge-port", "4000", "--mcp-port", "4100", "--watch-pid", "12"]
    )
    assert options.bridge_port == 4000
    assert options.mcp_port == 4100
    assert options.watch_pid == 12


def test_pid_file_claim_is_atomic(tmp_path, monkeypatch) -> None:
    pid_file = tmp_path / "sidecar.pid"
    barrier = threading.Barrier(2)
    original_exists = Path.exists

    def synchronized_exists(path: Path) -> bool:
        if path == pid_file:
            barrier.wait(timeout=2)
        return original_exists(path)

    monkeypatch.setattr(Path, "exists", synchronized_exists)
    monkeypatch.setattr(os, "kill", lambda *_args: None)
    outcomes: list[str] = []

    def claim() -> None:
        try:
            _claim_pid_file(str(pid_file))
        except SystemExit:
            outcomes.append("rejected")
        else:
            outcomes.append("claimed")

    workers = [threading.Thread(target=claim) for _ in range(2)]
    for worker in workers:
        worker.start()
    for worker in workers:
        worker.join(timeout=3)

    assert sorted(outcomes) == ["claimed", "rejected"]


def test_process_probe_does_not_terminate_the_target() -> None:
    process = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"])
    try:
        assert _process_is_alive(process.pid) is True
        assert process.poll() is None
    finally:
        process.terminate()
        process.wait(timeout=5)


def test_pid_file_release_preserves_a_replacement_owner(tmp_path) -> None:
    pid_file = tmp_path / "sidecar.pid"
    pid_file.write_text(str(os.getpid() + 1), encoding="ascii")
    _release_pid_file(pid_file)
    assert pid_file.exists()

    pid_file.write_text(str(os.getpid()), encoding="ascii")
    _release_pid_file(pid_file)
    assert not pid_file.exists()
