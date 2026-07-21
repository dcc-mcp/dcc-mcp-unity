from __future__ import annotations

import pytest

from dcc_mcp_unity._standalone_entry import _claim_pid_file, _parse_args


def test_standalone_options_are_explicit() -> None:
    options = _parse_args(
        ["dcc-mcp-unity", "--bridge-port", "4000", "--mcp-port", "4100", "--watch-pid", "12"]
    )
    assert options.bridge_port == 4000
    assert options.mcp_port == 4100
    assert options.watch_pid == 12


def test_pid_file_prevents_duplicate_process(tmp_path) -> None:
    pid_file = tmp_path / "sidecar.pid"
    claimed = _claim_pid_file(str(pid_file))
    assert claimed == pid_file.resolve()
    assert pid_file.read_text(encoding="ascii")
    with pytest.raises(SystemExit, match="already running"):
        _claim_pid_file(str(pid_file))
    pid_file.unlink()
