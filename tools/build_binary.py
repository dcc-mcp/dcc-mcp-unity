"""Build the PyOxidizer standalone Unity sidecar."""

from __future__ import annotations

import argparse
import hashlib
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BUILD = ROOT / "build"
OUTPUT = ROOT / "dist" / "standalone"
NAME = "dcc-mcp-unity" + (".exe" if sys.platform == "win32" else "")


def _run(command: list[str]) -> None:
    subprocess.run(command, cwd=ROOT, check=True)


def _find_binary() -> Path:
    matches = (
        sorted(path for path in BUILD.rglob(NAME) if "pyoxidizer" not in path.parts)
        if BUILD.exists()
        else []
    )
    if not matches:
        raise FileNotFoundError(f"PyOxidizer did not produce {NAME} under {BUILD}")
    return matches[-1]


def _write_manifest(directory: Path) -> None:
    lines = []
    for path in sorted(p for p in directory.rglob("*") if p.is_file()):
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        lines.append(f"{digest}  {path.relative_to(directory).as_posix()}")
    (directory / "SHA256SUMS").write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--verbose", action="store_true")
    args = parser.parse_args()
    _run(["pyoxidizer", "build", "--path", str(ROOT), *(["--verbose"] if args.verbose else [])])

    OUTPUT.mkdir(parents=True, exist_ok=True)
    binary = _find_binary()
    destination = OUTPUT / binary.name
    shutil.copy2(binary, destination)
    runtime = binary.parent / "lib"
    if runtime.is_dir():
        shutil.copytree(runtime, OUTPUT / "lib", dirs_exist_ok=True)
    if sys.platform == "win32":
        for dll in binary.parent.glob("*.dll"):
            shutil.copy2(dll, OUTPUT / dll.name)
    _write_manifest(OUTPUT)
    print(f"Built {destination}")


if __name__ == "__main__":
    main()
