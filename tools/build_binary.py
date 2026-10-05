"""Build the PyOxidizer standalone Unity sidecar."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BUILD = ROOT / "build"
OUTPUT = ROOT / "dist" / "standalone"
NAME = "dcc-mcp-unity" + (".exe" if sys.platform == "win32" else "")


def _run(command: list[str], environment: dict[str, str] | None = None) -> None:
    subprocess.run(command, cwd=ROOT, check=True, env=environment)


def _toolchain_environment(args: argparse.Namespace) -> tuple[dict[str, str] | None, dict | None]:
    requested = (args.msvc_kit, args.msvc_version, args.sdk_version, args.msvc_kit_dir)
    if not any(requested):
        return None, None
    if sys.platform != "win32":
        raise ValueError("The msvc-kit build contract is supported only on Windows")
    if not args.msvc_kit or not args.msvc_version or not args.sdk_version:
        raise ValueError(
            "An explicit msvc-kit build requires --msvc-kit, --msvc-version and --sdk-version"
        )
    if not re.fullmatch(r"\d+\.\d+\.\d+", args.msvc_version):
        raise ValueError("Pin a full MSVC version, for example 14.44.35207")
    if not re.fullmatch(r"\d+(?:\.\d+){3}", args.sdk_version):
        raise ValueError("Pin a full SDK version, for example 10.0.22621.0")
    command = [
        args.msvc_kit,
        "doctor",
        "--format",
        "json",
        "--compile",
        "--arch",
        "x64",
        "--host-arch",
        "x64",
        "--msvc-version",
        args.msvc_version,
        "--sdk-version",
        args.sdk_version,
    ]
    if args.msvc_kit_dir:
        command += ["--dir", args.msvc_kit_dir]
    report = json.loads(subprocess.check_output(command, text=True, encoding="utf-8"))
    if report.get("schema") != "msvc-kit.doctor.v1" or report.get("status") != "passed":
        raise ValueError("Requested msvc-kit must report a passing msvc-kit.doctor.v1 contract")
    toolchain = report.get("toolchain", {})
    if toolchain.get("msvc", {}).get("version") != args.msvc_version:
        raise ValueError("msvc-kit selected a different MSVC version from the requested pin")
    if toolchain.get("sdk", {}).get("version") != args.sdk_version:
        raise ValueError("msvc-kit selected a different SDK version from the requested pin")
    variables = toolchain.get("env_vars")
    if (
        not isinstance(variables, dict)
        or not variables
        or not all(
            isinstance(key, str) and isinstance(value, str) for key, value in variables.items()
        )
    ):
        raise ValueError("msvc-kit doctor did not supply a valid toolchain environment")
    environment = os.environ.copy()
    # Windows environment keys are case insensitive; preserve the provided PATH once.
    for key, value in variables.items():
        if key.casefold() == "path":
            inherited = next(
                (
                    current
                    for previous, current in environment.items()
                    if previous.casefold() == "path"
                ),
                "",
            )
            value = ";".join(part for part in (value.rstrip(";"), inherited) if part)
        for previous in list(environment):
            if previous.casefold() == key.casefold():
                del environment[previous]
        environment[key] = value
    return environment, report


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
    manifest = directory / "SHA256SUMS"
    for path in sorted(p for p in directory.rglob("*") if p.is_file() and p != manifest):
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        lines.append(f"{digest}  {path.relative_to(directory).as_posix()}")
    manifest.write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--verbose", action="store_true")
    parser.add_argument("--msvc-kit", default=os.environ.get("DCC_MCP_UNITY_MSVC_KIT", ""))
    parser.add_argument("--msvc-version", default=os.environ.get("DCC_MCP_UNITY_MSVC_VERSION", ""))
    parser.add_argument("--sdk-version", default=os.environ.get("DCC_MCP_UNITY_SDK_VERSION", ""))
    parser.add_argument("--msvc-kit-dir", default=os.environ.get("DCC_MCP_UNITY_MSVC_KIT_DIR", ""))
    args = parser.parse_args()
    environment, toolchain = _toolchain_environment(args)
    _run(
        ["pyoxidizer", "build", "--path", str(ROOT), *(["--verbose"] if args.verbose else [])],
        environment,
    )

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
    toolchain_record = OUTPUT / "msvc-kit-toolchain.json"
    if toolchain is not None:
        toolchain_record.write_text(json.dumps(toolchain, indent=2) + "\n", encoding="utf-8")
    else:
        toolchain_record.unlink(missing_ok=True)
    _write_manifest(OUTPUT)
    print(f"Built {destination}")


if __name__ == "__main__":
    main()
