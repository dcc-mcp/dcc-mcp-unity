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


def _builder(*, verify_cli=False):
    spec = importlib.util.spec_from_file_location(
        "unity_build_binary", ROOT / "tools" / "build_binary.py"
    )
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    if not verify_cli:
        module._verify_msvc_kit = lambda _path: None
    return module


def _options(**overrides):
    values = dict(msvc_kit="", msvc_kit_dir="", msvc_version="", sdk_version="")
    values.update(overrides)
    if values["msvc_kit"] and "msvc_kit_dir" not in overrides:
        values["msvc_kit_dir"] = "C:/tools/compiler"
    return SimpleNamespace(**values)


def _report():
    return {
        "schema": "msvc-kit.doctor.v1",
        "status": "passed",
        "checks": [
            {"id": f"probe.{stage}", "status": "passed"}
            for stage in ("compile", "resource", "link", "manifest", "execute")
        ],
        "toolchain": {
            "arch": "x64",
            "host_arch": "x64",
            "msvc": {"version": "14.44.35207"},
            "sdk": {"version": "10.0.22621.0"},
            "tools": {"cl": "C:/compiler/cl.exe", "link": "C:/compiler/link.exe"},
            "env_vars": {
                "PATH": "compiler",
                "INCLUDE": "headers",
                "LIB": "libraries",
                "CC": "C:/compiler/cl.exe",
                "CXX": "C:/compiler/cl.exe",
                "CC_x86_64-pc-windows-msvc": "C:/compiler/cl.exe",
                "CC_x86_64_pc_windows_msvc": "C:/compiler/cl.exe",
                "CXX_x86_64-pc-windows-msvc": "C:/compiler/cl.exe",
                "CXX_x86_64_pc_windows_msvc": "C:/compiler/cl.exe",
                "CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_LINKER": "C:/compiler/link.exe",
            },
        },
    }


def test_normal_sidecar_build_does_not_require_or_invoke_msvc(monkeypatch):
    module = _builder()
    monkeypatch.setattr(module.sys, "platform", "linux")
    monkeypatch.setattr(
        module.subprocess,
        "check_output",
        lambda *_args, **_kwargs: pytest.fail("unexpected MSVC call"),
    )
    assert module._toolchain_environment(_options()) == (None, None)


def test_windows_sidecar_cannot_silently_bypass_self_msvc_kit(monkeypatch):
    module = _builder()
    monkeypatch.setattr(module.sys, "platform", "win32")
    with pytest.raises(ValueError, match="Windows standalone.*msvc-kit"):
        module._toolchain_environment(_options())


def test_windows_sidecar_requires_an_explicit_toolchain_root(monkeypatch):
    module = _builder()
    monkeypatch.setattr(module.sys, "platform", "win32")
    monkeypatch.setattr(module.subprocess, "check_output", lambda *_a, **_k: json.dumps(_report()))
    with pytest.raises(ValueError, match="--msvc-kit-dir"):
        module._toolchain_environment(
            _options(
                msvc_kit="msvc-kit",
                msvc_kit_dir="",
                msvc_version="14.44.35207",
                sdk_version="10.0.22621.0",
            )
        )


def test_unrecognized_msvc_kit_binary_is_rejected_before_execution(tmp_path):
    module = _builder(verify_cli=True)
    cli = tmp_path / "msvc-kit.exe"
    cli.write_bytes(b"not the approved official release")
    with pytest.raises(ValueError, match="SHA256"):
        module._verify_msvc_kit(str(cli))


@pytest.mark.parametrize("change", ["missing", "skipped", "duplicate"])
def test_msvc_contract_requires_all_real_probe_stages(monkeypatch, change):
    module = _builder()
    monkeypatch.setattr(module.sys, "platform", "win32")
    report = _report()
    if change == "missing":
        report["checks"].pop(0)
    elif change == "skipped":
        report["checks"][0]["status"] = "skipped"
    else:
        report["checks"].append(report["checks"][0].copy())
    monkeypatch.setattr(module.subprocess, "check_output", lambda *_a, **_k: json.dumps(report))
    with pytest.raises(ValueError, match="probe.compile"):
        module._toolchain_environment(
            _options(msvc_kit="msvc-kit", msvc_version="14.44.35207", sdk_version="10.0.22621.0")
        )


def test_doctor_failure_preserves_machine_readable_diagnostics(monkeypatch):
    module = _builder()
    monkeypatch.setattr(module.sys, "platform", "win32")

    def failed(command, **_kwargs):
        raise subprocess.CalledProcessError(
            10, command, output='{"status":"failed","reason":"missing SDK"}'
        )

    monkeypatch.setattr(module.subprocess, "check_output", failed)
    with pytest.raises(ValueError, match="missing SDK"):
        module._toolchain_environment(
            _options(msvc_kit="msvc-kit", msvc_version="14.44.35207", sdk_version="10.0.22621.0")
        )


@pytest.mark.parametrize(
    "variable", ["CC", "CXX_x86_64_pc_windows_msvc", "CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_LINKER"]
)
def test_consumer_cannot_use_an_ambient_compiler_or_linker(monkeypatch, variable):
    module = _builder()
    monkeypatch.setattr(module.sys, "platform", "win32")
    report = _report()
    report["toolchain"]["env_vars"][variable] = "C:/unrelated/tool.exe"
    monkeypatch.setattr(module.subprocess, "check_output", lambda *_a, **_k: json.dumps(report))
    with pytest.raises(ValueError, match="must select the reported"):
        module._toolchain_environment(
            _options(msvc_kit="msvc-kit", msvc_version="14.44.35207", sdk_version="10.0.22621.0")
        )


def _evidence_directory():
    path = ROOT / "dist" / "toolchain-evidence"
    path.mkdir(parents=True, exist_ok=True)
    preparation = os.environ.get("DCC_MCP_UNITY_MSVC_KIT_PREPARATION")
    if preparation:
        (path / "preparation.json").write_bytes(Path(preparation).read_bytes())
    return path


def _assert_real_report(report):
    assert report["schema"] == "msvc-kit.doctor.v1"
    assert report["status"] == "passed"
    selected = report["toolchain"]
    assert selected["arch"] == selected["host_arch"] == "x64"
    assert selected["msvc"]["version"] == os.environ["DCC_MCP_UNITY_MSVC_VERSION"]
    assert selected["sdk"]["version"] == os.environ["DCC_MCP_UNITY_SDK_VERSION"]
    for stage in ("compile", "resource", "link", "manifest", "execute"):
        checks = [check for check in report["checks"] if check["id"] == f"probe.{stage}"]
        assert len(checks) == 1 and checks[0]["status"] == "passed"
    preparation = json.loads(
        Path(os.environ["DCC_MCP_UNITY_MSVC_KIT_PREPARATION"]).read_text(encoding="utf-8-sig")
    )
    for name in ("cl", "link", "lib", "rc", "mt"):
        assert (
            hashlib.sha256(Path(selected["tools"][name]).read_bytes()).hexdigest()
            == preparation["tools"][name]["sha256"]
        )


def test_real_msvc_kit_standalone_consumer_compiles_without_toolchain_download(tmp_path):
    if sys.platform != "win32" or os.environ.get("DCC_MCP_REQUIRE_SELF_MSVC_KIT") != "1":
        pytest.skip("Real released msvc-kit consumer runs on the prepared Windows CI host")
    module = _builder(verify_cli=True)
    options = _options(
        msvc_kit=os.environ["DCC_MCP_UNITY_MSVC_KIT"],
        msvc_kit_dir=os.environ["DCC_MCP_UNITY_MSVC_KIT_DIR"],
        msvc_version=os.environ["DCC_MCP_UNITY_MSVC_VERSION"],
        sdk_version=os.environ["DCC_MCP_UNITY_SDK_VERSION"],
    )
    evidence = _evidence_directory()
    try:
        environment, report = module._toolchain_environment(options)
    except Exception as error:
        (evidence / "python-doctor-failure.txt").write_text(str(error), encoding="utf-8")
        raise
    (evidence / "python-doctor.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    _assert_real_report(report)
    source = tmp_path / "consumer.cpp"
    source.write_text(
        "#include <Windows.h>\n#include <vector>\n"
        "int main(){std::vector<DWORD> v{GetCurrentProcessId()};return v[0]==0;}\n",
        encoding="utf-8",
    )
    binary = tmp_path / "consumer.exe"
    # This invokes the real compiler in the real environment returned to PyOxidizer.
    # It is a consumer contract smoke, not the full standalone packaging build.
    compile_result = subprocess.run(
        [
            report["toolchain"]["tools"]["cl"],
            "/nologo",
            "/EHsc",
            "/MD",
            str(source),
            f"/Fe{binary}",
        ],
        cwd=tmp_path,
        env=environment,
        capture_output=True,
        text=True,
        timeout=60,
    )
    (evidence / "consumer-compile.txt").write_text(
        compile_result.stdout + compile_result.stderr, encoding="utf-8"
    )
    assert compile_result.returncode == 0
    subprocess.run([str(binary)], cwd=tmp_path, env=environment, check=True, timeout=30)
    (evidence / "consumer-result.json").write_text(
        json.dumps(
            {
                "compiler": report["toolchain"]["tools"]["cl"],
                "executable_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
                "compile_exit_code": compile_result.returncode,
                "execute_exit_code": 0,
                "full_pyoxidizer_build": False,
            },
            indent=2,
        ),
        encoding="utf-8",
    )
    options.msvc_version = "0.0.0"
    with pytest.raises(ValueError, match="doctor failed") as failure:
        module._toolchain_environment(options)
    (evidence / "absent-pin-failure.txt").write_text(str(failure.value), encoding="utf-8")


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
    for key in ("PATH", "INCLUDE", "LIB"):
        report["toolchain"]["env_vars"].pop(key)
    report["toolchain"]["env_vars"].update(
        {"pAtH": "compiler", "include": "headers", "Lib": "libraries"}
    )
    monkeypatch.setattr(
        module.subprocess, "check_output", lambda *_args, **_kwargs: json.dumps(report)
    )
    environment, _ = module._toolchain_environment(
        _options(msvc_kit="msvc-kit", msvc_version="14.44.35207", sdk_version="10.0.22621.0")
    )
    assert environment == {**report["toolchain"]["env_vars"], "pAtH": "compiler;ambient-bin"}
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
    if os.environ.get("DCC_MCP_REQUIRE_SELF_MSVC_KIT") == "1":
        evidence = _evidence_directory()
        result = subprocess.run(
            [*runtime, str(executable), "--doctor"],
            capture_output=True,
            text=True,
            encoding="utf-8",
            timeout=200,
        )
        (evidence / "csharp-doctor-stdout.json").write_text(result.stdout, encoding="utf-8")
        (evidence / "csharp-doctor-stderr.txt").write_text(result.stderr, encoding="utf-8")
        assert result.returncode == 0
        report = json.loads(result.stdout)
        _assert_real_report(report)
        selected = report["toolchain"]
        result = subprocess.run(
            [
                *runtime,
                str(executable),
                "--selected",
                selected["msvc"]["version"],
                selected["sdk"]["version"],
                *[selected["tools"][name] for name in ("cl", "link", "rc", "mt")],
            ],
            capture_output=True,
            text=True,
            timeout=60,
        )
        (evidence / "csharp-registered-selection.txt").write_text(
            result.stdout + result.stderr, encoding="utf-8"
        )
        assert result.returncode == 0
        assert "MSVC_KIT_REGISTERED_SELECTION_OK" in result.stdout
