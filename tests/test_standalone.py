from __future__ import annotations

from dcc_mcp_unity._standalone_entry import _parse_args


def test_standalone_options_are_explicit() -> None:
    options = _parse_args(
        ["dcc-mcp-unity", "--bridge-port", "4000", "--mcp-port", "4100", "--watch-pid", "12"]
    )
    assert options.bridge_port == 4000
    assert options.mcp_port == 4100
    assert options.watch_pid == 12
