"""Build the PyOxidizer standalone Unity sidecar."""

from __future__ import annotations

import argparse
import hashlib
import json
import ntpath
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
MSVC_KIT_VERSION = "0.2.22"
MSVC_KIT_SHA256 = "184f07fb2896f0a7d27c45445990bd7d9e525b6ee58956cfe857bf0027848925"
PROBE_STAGES = ("compile", "resource", "link", "manifest", "execute")


def _valid_msvc_pin(version: str) -> bool:
    return version == "14.0" or (
        not version.startswith("14.0.") and re.fullmatch(r"\d+\.\d+\.\d+", version) is not None
    )


def _same_windows_path(left: str, right: str) -> bool:
    return ntpath.normcase(ntpath.abspath(left)) == ntpath.normcase(ntpath.abspath(right))


def _run(command: list[str], environment: dict[str, str] | None = None) -> None:
    subprocess.run(command, cwd=ROOT, check=True, env=environment)


def _verify_msvc_kit(executable: str) -> None:
    path = Path(executable)
    if not path.is_file():
        raise ValueError(
            "The pinned msvc-kit executable is missing. Prepare the official v0.2.22 CLI "
            "and an existing toolchain; see docs/windows-toolchain.md "
            "(.github/scripts/prepare-msvc-kit.ps1 prepares CI runners only)."
        )
    if hashlib.sha256(path.read_bytes()).hexdigest() != MSVC_KIT_SHA256:
        raise ValueError("msvc-kit SHA256 does not match the approved official v0.2.22 release")


def _toolchain_environment(args: argparse.Namespace) -> tuple[dict[str, str] | None, dict | None]:
    host_arch = getattr(args, "host_arch", "x64")
    sdk_dir = getattr(args, "sdk_dir", None)
    requested = (args.msvc_kit, args.msvc_version, args.sdk_version, args.msvc_kit_dir)
    if sys.platform != "win32":
        if not any(requested) and sdk_dir is None and host_arch == "x64":
            return None, None
        raise ValueError("The msvc-kit build contract is supported only on Windows")
    if not all(requested):
        raise ValueError(
            "Windows standalone builds require msvc-kit: supply --msvc-kit, --msvc-kit-dir, "
            "--msvc-version and --sdk-version, or their DCC_MCP_UNITY_* environment variables. "
            "See docs/windows-toolchain.md for local setup or the CI preparation script; "
            "no compiler or SDK is installed automatically."
        )
    if not _valid_msvc_pin(args.msvc_version):
        raise ValueError("Pin a full modern MSVC version, or exactly 14.0 for verified VS2015")
    if not re.fullmatch(r"\d+(?:\.\d+){3}", args.sdk_version):
        raise ValueError("Pin a full SDK version, for example 10.0.22621.0")
    if host_arch not in ("x64", "x86"):
        raise ValueError("Windows x64 builds require an explicit x64 or x86 compiler host")
    if sdk_dir is not None and not sdk_dir.strip():
        raise ValueError("An explicit --sdk-dir must not be empty")
    if args.msvc_version == "14.0" and sdk_dir is None:
        raise ValueError("VS2015 family 14.0 requires an explicit --sdk-dir for the existing SDK")
    if (args.msvc_version == "14.0" or sdk_dir is not None) and tuple(
        int(part) for part in MSVC_KIT_VERSION.split(".")
    ) < (0, 2, 22):
        raise ValueError(
            "Legacy or separate-SDK selection requires an approved released msvc-kit 0.2.22+ pin; "
            "the current consumer release identity has not been upgraded"
        )
    _verify_msvc_kit(args.msvc_kit)
    command = [
        args.msvc_kit,
        "doctor",
        "--format",
        "json",
        "--compile",
        "--arch",
        "x64",
        "--host-arch",
        host_arch,
        "--msvc-version",
        args.msvc_version,
        "--sdk-version",
        args.sdk_version,
    ]
    command += ["--dir", args.msvc_kit_dir]
    if sdk_dir is not None:
        command += ["--sdk-dir", sdk_dir]
    try:
        output = subprocess.check_output(command, text=True, encoding="utf-8", timeout=180)
    except subprocess.CalledProcessError as error:
        raise ValueError(
            f"msvc-kit doctor failed (exit {error.returncode}); repair the selected toolchain. "
            f"Doctor report: {error.output}"
        ) from error
    except subprocess.TimeoutExpired as error:
        raise ValueError("msvc-kit doctor exceeded its 180-second deadline") from error
    try:
        report = json.loads(output)
    except json.JSONDecodeError as error:
        raise ValueError("msvc-kit doctor returned invalid JSON; verify the pinned CLI") from error
    if not isinstance(report, dict):
        raise ValueError("msvc-kit doctor must return a JSON object")
    if report.get("schema") != "msvc-kit.doctor.v1" or report.get("status") != "passed":
        raise ValueError("Requested msvc-kit must report a passing msvc-kit.doctor.v1 contract")
    checks = report.get("checks", [])
    if not isinstance(checks, list) or any(not isinstance(check, dict) for check in checks):
        raise ValueError("msvc-kit doctor must include its individual probe checks")
    for stage in PROBE_STAGES:
        matches = [check for check in checks if check.get("id") == f"probe.{stage}"]
        allowed = (
            ("passed", "skipped") if stage == "execute" and host_arch == "x86" else ("passed",)
        )
        if len(matches) != 1 or matches[0].get("status") not in allowed:
            raise ValueError(f"msvc-kit doctor did not execute and pass probe.{stage}")
    toolchain = report.get("toolchain", {})
    if not isinstance(toolchain, dict):
        raise ValueError("msvc-kit doctor must include its selected toolchain")
    # Doctor serializes QueryResult.arch and host_arch as strings. Both must
    # match the explicit host and x64 target, independently of version pins.
    if toolchain.get("arch") != "x64" or toolchain.get("host_arch") != host_arch:
        raise ValueError(
            "msvc-kit selected a different or missing host/target architecture (x64 target)"
        )
    if (
        not isinstance(toolchain.get("msvc"), dict)
        or toolchain["msvc"].get("version") != args.msvc_version
    ):
        raise ValueError("msvc-kit selected a different MSVC version from the requested pin")
    if (
        not isinstance(toolchain.get("sdk"), dict)
        or toolchain["sdk"].get("version") != args.sdk_version
    ):
        raise ValueError("msvc-kit selected a different SDK version from the requested pin")
    if sdk_dir is not None:
        selected_root = toolchain["sdk"].get("install_path")
        if not isinstance(selected_root, str) or not _same_windows_path(selected_root, sdk_dir):
            raise ValueError("msvc-kit selected a different SDK root from --sdk-dir")
    if args.msvc_version == "14.0":
        family = [check for check in checks if check.get("id") == "version.msvc"]
        if len(family) != 1 or family[0].get("status") != "skipped":
            raise ValueError(
                "VS2015 must explicitly report its unverified patch version as family 14.0"
            )
        selected_root = toolchain["msvc"].get("install_path")
        if not isinstance(selected_root, str) or not _same_windows_path(
            selected_root, ntpath.join(args.msvc_kit_dir, "VC")
        ):
            raise ValueError(
                "VS2015 must resolve the original selected installation's VC directory"
            )
    variables = toolchain.get("env_vars")
    if (
        not isinstance(variables, dict)
        or not variables
        or not all(
            isinstance(key, str) and isinstance(value, str) for key, value in variables.items()
        )
    ):
        raise ValueError("msvc-kit doctor did not supply a valid toolchain environment")
    # msvc-kit get_env_vars exports all three search lists. Missing/empty lists
    # must not inherit ambient compiler, header or library selection instead.
    normalized = {key.casefold(): value for key, value in variables.items()}
    if len(normalized) != len(variables):
        raise ValueError("msvc-kit doctor supplied duplicate case-insensitive environment keys")
    for key in ("path", "include", "lib"):
        if not any(part.strip() for part in normalized.get(key, "").split(";")):
            raise ValueError(f"msvc-kit doctor must supply a non-empty {key.upper()} search list")
    tools = toolchain.get("tools", {})
    if not isinstance(tools, dict):
        raise ValueError("msvc-kit doctor must supply the selected compiler/linker paths")
    bindings = {
        "CC": "cl",
        "CXX": "cl",
        "CC_x86_64-pc-windows-msvc": "cl",
        "CC_x86_64_pc_windows_msvc": "cl",
        "CXX_x86_64-pc-windows-msvc": "cl",
        "CXX_x86_64_pc_windows_msvc": "cl",
        "CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_LINKER": "link",
    }
    for variable, tool in bindings.items():
        selected = tools.get(tool)
        exported = normalized.get(variable.casefold())
        if (
            not isinstance(selected, str)
            or not selected
            or not exported
            or ntpath.normcase(ntpath.normpath(exported))
            != ntpath.normcase(ntpath.normpath(selected))
        ):
            raise ValueError(f"msvc-kit {variable} must select the reported {tool} executable")
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
    report["consumer_cli"] = {"version": MSVC_KIT_VERSION, "sha256": MSVC_KIT_SHA256}
    report["compiler_version_kind"] = "family" if args.msvc_version == "14.0" else "exact"
    report["consumer_probe_execution_verified"] = next(
        check["status"] == "passed" for check in checks if check.get("id") == "probe.execute"
    )
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
    parser.add_argument("--sdk-dir", default=os.environ.get("DCC_MCP_UNITY_SDK_DIR") or None)
    parser.add_argument("--host-arch", default=os.environ.get("DCC_MCP_UNITY_HOST_ARCH") or "x64")
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
