"""Verify the real Unity WebSocket bridge used by licensed Editor CI."""

from __future__ import annotations

import argparse
import json
import os
import time
from pathlib import Path
from typing import Any

from dcc_mcp_core import BridgeConnectionError

from dcc_mcp_unity.bridge import call_host, start_bridge, stop_bridge


def _write_json_atomic(path: Path, payload: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f".{path.name}.tmp")
    temporary.write_text(json.dumps(payload, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    os.replace(temporary, path)


def run_smoke(
    *,
    expected_version: str,
    output: Path,
    ready: Path,
    timeout_seconds: float,
) -> dict[str, Any]:
    """Wait for Unity, execute a read-only request, and persist bounded evidence."""
    bridge = start_bridge()
    _write_json_atomic(ready, {"status": "listening"})
    deadline = time.monotonic() + timeout_seconds
    last_disconnect: BridgeConnectionError | None = None

    try:
        while time.monotonic() < deadline:
            if not bridge.is_connected():
                time.sleep(0.25)
                continue
            try:
                inspected = call_host("project.inspect")
            except BridgeConnectionError as exc:
                last_disconnect = exc
                time.sleep(0.25)
                continue

            actual_version = str(inspected.get("engine_version", ""))
            if actual_version != expected_version:
                raise RuntimeError(
                    f"expected Unity {expected_version}, got {actual_version or 'no version'}"
                )
            evidence = {
                "engine_version": actual_version,
                "project_name": str(inspected.get("name", "")),
                "status": "passed",
            }
            _write_json_atomic(output, evidence)
            return evidence

        detail = f": {last_disconnect}" if last_disconnect is not None else ""
        raise TimeoutError(
            "Unity bridge did not complete project.inspect within "
            f"{timeout_seconds:g} seconds{detail}"
        )
    finally:
        stop_bridge()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--expected-version", required=True)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--ready", required=True, type=Path)
    parser.add_argument("--timeout-seconds", type=float, default=900)
    args = parser.parse_args()
    if args.timeout_seconds <= 0:
        parser.error("--timeout-seconds must be positive")
    run_smoke(
        expected_version=args.expected_version,
        output=args.output,
        ready=args.ready,
        timeout_seconds=args.timeout_seconds,
    )


if __name__ == "__main__":
    main()
