from __future__ import annotations

import hashlib
import importlib.util
import json
import os
import subprocess
import sys
from pathlib import Path
from types import SimpleNamespace

import pytest

ROOT = Path(__file__).parents[1]


def _builder():
    spec = importlib.util.spec_from_file_location(
        "unity_build_binary", ROOT / "tools" / "build_binary.py"
    )
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _options(**overrides):
    values = dict(msvc_kit="", msvc_kit_dir="", msvc_version="", sdk_version="")
    values.update(overrides)
    return SimpleNamespace(**values)


def _report():
    return {
        "schema": "msvc-kit.doctor.v1",
        "status": "passed",
        "toolchain": {
            "arch": "x64",
            "host_arch": "x64",
            "msvc": {"version": "14.44.35207"},
            "sdk": {"version": "10.0.22621.0"},
            "env_vars": {"PATH": "compiler", "INCLUDE": "headers", "LIB": "libraries"},
        },
    }


def test_normal_sidecar_build_does_not_require_or_invoke_msvc(monkeypatch):
    module = _builder()
    monkeypatch.setattr(
        module.subprocess,
        "check_output",
        lambda *_args, **_kwargs: pytest.fail("unexpected MSVC call"),
    )
    assert module._toolchain_environment(_options()) == (None, None)


def test_repeated_build_checksums_cover_toolchain_record_without_hashing_the_manifest(tmp_path):
    module = _builder()
    (tmp_path / "sidecar.exe").write_bytes(b"executable")
    record = tmp_path / "msvc-kit-toolchain.json"
    record.write_text(json.dumps(_report()), encoding="utf-8")
    module._write_manifest(tmp_path)
    manifest = tmp_path / "SHA256SUMS"
    first = manifest.read_bytes()
    module._write_manifest(tmp_path)
    assert manifest.read_bytes() == first
    lines = manifest.read_text(encoding="utf-8").splitlines()
    assert len(lines) == 2
    assert any(line.endswith("  msvc-kit-toolchain.json") for line in lines)
    for line in lines:
        expected, name = line.split("  ", 1)
        assert hashlib.sha256((tmp_path / name).read_bytes()).hexdigest() == expected


@pytest.mark.parametrize(
    "options",
    [
        _options(msvc_version="14.44.35207"),
        _options(msvc_kit="msvc-kit"),
        _options(msvc_kit="msvc-kit", msvc_version="14.44", sdk_version="10.0.22621.0"),
    ],
)
def test_requested_contract_cannot_silently_fall_back(monkeypatch, options):
    module = _builder()
    monkeypatch.setattr(module.sys, "platform", "win32")
    with pytest.raises(ValueError):
        module._toolchain_environment(options)


def test_msvc_environment_is_only_given_to_the_build_child(monkeypatch):
    module = _builder()
    monkeypatch.setattr(module.sys, "platform", "win32")
    monkeypatch.setenv("PATH", "original")
    calls = []

    def doctor(command, **_kwargs):
        calls.append(command)
        return json.dumps(_report())

    monkeypatch.setattr(module.subprocess, "check_output", doctor)
    environment, report = module._toolchain_environment(
        _options(
            msvc_kit="C:/tools/msvc-kit.exe",
            msvc_kit_dir="C:/tools/compiler",
            msvc_version="14.44.35207",
            sdk_version="10.0.22621.0",
        )
    )
    assert environment["PATH"] == "compiler;original"
    assert os.environ["PATH"] == "original"
    assert calls[0][1:9] == [
        "doctor",
        "--format",
        "json",
        "--compile",
        "--arch",
        "x64",
        "--host-arch",
        "x64",
    ]
    assert calls[0][-2:] == ["--dir", "C:/tools/compiler"]
    assert report["status"] == "passed"


@pytest.mark.parametrize("change", ["schema", "status", "compiler", "sdk", "environment"])
def test_msvc_contract_rejects_unverified_or_mismatched_results(monkeypatch, change):
    module = _builder()
    monkeypatch.setattr(module.sys, "platform", "win32")
    report = _report()
    if change == "schema":
        report["schema"] = "unrecognized"
    elif change == "status":
        report["status"] = "failed"
    elif change == "compiler":
        report["toolchain"]["msvc"]["version"] = "14.38.33130"
    elif change == "sdk":
        report["toolchain"]["sdk"]["version"] = "10.0.26100.0"
    else:
        report["toolchain"]["env_vars"] = None
    monkeypatch.setattr(
        module.subprocess, "check_output", lambda *_args, **_kwargs: json.dumps(report)
    )
    with pytest.raises(ValueError):
        module._toolchain_environment(
            _options(
                msvc_kit="msvc-kit",
                msvc_version="14.44.35207",
                sdk_version="10.0.22621.0",
            )
        )


def test_requested_missing_cli_preserves_the_real_failure(monkeypatch):
    module = _builder()
    monkeypatch.setattr(module.sys, "platform", "win32")

    def missing(*_args, **_kwargs):
        raise FileNotFoundError("explicit CLI is absent")

    monkeypatch.setattr(module.subprocess, "check_output", missing)
    with pytest.raises(FileNotFoundError, match="explicit CLI"):
        module._toolchain_environment(
            _options(
                msvc_kit="missing.exe",
                msvc_version="14.44.35207",
                sdk_version="10.0.22621.0",
            )
        )


@pytest.mark.parametrize("key", ["arch", "host_arch"])
@pytest.mark.parametrize("value", [None, "", "arm64"])
def test_msvc_report_requires_the_requested_host_and_target(monkeypatch, key, value):
    module = _builder()
    monkeypatch.setattr(module.sys, "platform", "win32")
    report = _report()
    if value is None:
        del report["toolchain"][key]
    else:
        report["toolchain"][key] = value
    monkeypatch.setattr(
        module.subprocess, "check_output", lambda *_args, **_kwargs: json.dumps(report)
    )
    with pytest.raises(ValueError, match="host/target architecture"):
        module._toolchain_environment(
            _options(msvc_kit="msvc-kit", msvc_version="14.44.35207", sdk_version="10.0.22621.0")
        )


@pytest.mark.parametrize("key", ["PATH", "INCLUDE", "LIB"])
@pytest.mark.parametrize("value", [None, "", " ", "; ;"])
def test_msvc_environment_requires_complete_search_lists(monkeypatch, key, value):
    module = _builder()
    monkeypatch.setattr(module.sys, "platform", "win32")
    monkeypatch.setenv(key, "ambient-must-not-rescue-incomplete-report")
    report = _report()
    if value is None:
        del report["toolchain"]["env_vars"][key]
    else:
        report["toolchain"]["env_vars"][key] = value
    monkeypatch.setattr(
        module.subprocess, "check_output", lambda *_args, **_kwargs: json.dumps(report)
    )
    with pytest.raises(ValueError, match=key):
        module._toolchain_environment(
            _options(msvc_kit="msvc-kit", msvc_version="14.44.35207", sdk_version="10.0.22621.0")
        )


def test_msvc_environment_keys_are_case_insensitive_without_ambient_include_lib(monkeypatch):
    module = _builder()
    monkeypatch.setattr(module.sys, "platform", "win32")
    inherited = {"Path": "ambient-bin", "INCLUDE": "ambient-headers", "lib": "ambient-libraries"}
    monkeypatch.setattr(module.os, "environ", inherited)
    report = _report()
    report["toolchain"]["env_vars"] = {"pAtH": "compiler", "include": "headers", "Lib": "libraries"}
    monkeypatch.setattr(
        module.subprocess, "check_output", lambda *_args, **_kwargs: json.dumps(report)
    )
    environment, _ = module._toolchain_environment(
        _options(msvc_kit="msvc-kit", msvc_version="14.44.35207", sdk_version="10.0.22621.0")
    )
    assert environment == {"pAtH": "compiler;ambient-bin", "include": "headers", "Lib": "libraries"}
    assert inherited == {
        "Path": "ambient-bin",
        "INCLUDE": "ambient-headers",
        "lib": "ambient-libraries",
    }


def test_msvc_environment_rejects_ambiguous_case_duplicate_keys(monkeypatch):
    module = _builder()
    monkeypatch.setattr(module.sys, "platform", "win32")
    report = _report()
    report["toolchain"]["env_vars"]["Path"] = "different-compiler"
    monkeypatch.setattr(
        module.subprocess, "check_output", lambda *_args, **_kwargs: json.dumps(report)
    )
    with pytest.raises(ValueError, match="duplicate case-insensitive"):
        module._toolchain_environment(
            _options(msvc_kit="msvc-kit", msvc_version="14.44.35207", sdk_version="10.0.22621.0")
        )


def test_windows_il2cpp_preflight_contract_in_compiled_csharp(tmp_path):
    if sys.platform != "win32":
        pytest.skip("The compiled Windows discovery contract runs on Windows")
    contents = os.environ.get("UNITY_EDITOR_CONTENTS")
    runtime = []
    if contents:
        mono = Path(contents) / "MonoBleedingEdge" / "bin" / "mono.exe"
        compiler = Path(contents) / "MonoBleedingEdge" / "lib" / "mono" / "4.5" / "mcs.exe"
        assert mono.is_file() and compiler.is_file(), "Configured Unity Mono compiler is missing"
        compile_command = [str(mono), str(compiler)]
        runtime = [str(mono)]
    else:
        # Windows includes the .NET Framework compiler; no Unity/SDK download or
        # PATH change is needed to execute this small, Unity-independent helper.
        compiler = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
        assert compiler.is_file(), "The installed Windows .NET Framework C# compiler is missing"
        compile_command = [str(compiler), "/nologo", "/codepage:65001"]
    helper = ROOT / "src/dcc_mcp_unity/unity_package/Editor/DccMcpWindowsToolchain.cs"
    harness = ROOT / "tests/native/WindowsToolchainProbe.cs"
    executable = tmp_path / "WindowsToolchainProbe.exe"
    subprocess.run(
        [*compile_command, "-out:" + str(executable), str(helper), str(harness)],
        check=True,
        timeout=60,
    )
    result = subprocess.run(
        [*runtime, str(executable)], check=True, capture_output=True, text=True, timeout=60
    )
    assert "WINDOWS_TOOLCHAIN_FIXTURES_OK" in result.stdout
    assert "WINDOWS_TOOLCHAIN_CONTRACT_OK" in result.stdout
    if os.environ.get("DCC_MCP_REQUIRE_WINDOWS_TOOLCHAIN") == "1":
        result = subprocess.run(
            [*runtime, str(executable), "--discover"],
            check=True,
            capture_output=True,
            text=True,
            timeout=30,
        )
        assert "WINDOWS_REGISTERED_TOOLCHAIN_DISCOVERED" in result.stdout
