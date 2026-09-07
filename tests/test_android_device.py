import hashlib
import json
import struct
import sys
import uuid
import zlib
from pathlib import Path

import pytest

from dcc_mcp_unity import android_device as android


@pytest.fixture
def device(tmp_path, monkeypatch):
    apk = tmp_path / "Builds/DccMcp/build/DccMcpGame.apk"
    apk.parent.mkdir(parents=True)
    apk.write_bytes(b"test apk contents")
    context = {
        "project_path": str(tmp_path),
        "build": {
            "state": "succeeded",
            "result": {
                "artifact_kind": "apk",
                "relative_path": apk.relative_to(tmp_path).as_posix(),
                "bytes": apk.stat().st_size,
                "sha256": android._sha(apk),
                "package_name": "com.test.game",
            },
        },
    }
    monkeypatch.setattr(android, "call_host", lambda *args: context)
    monkeypatch.setattr(android, "_adb", lambda context: "adb")
    calls = []

    def run(adb, args, **kwargs):
        calls.append(args)
        if args == ["devices", "-l"]:
            return b"List of devices attached\nPRIVATE_SERIAL device model:Test\nSECOND offline\n"
        assert args[:2] == ["-s", "PRIVATE_SERIAL"]
        command = args[2:]
        if command[:2] == ["install", "-r"]:
            return b"Success\n"
        if command == ["shell", "pm", "path", "com.test.game"]:
            return b"package:/data/app/~~token/com.test.game-token/base.apk\n"
        if command[0] == "pull":
            Path(command[-1]).write_bytes(apk.read_bytes())
            return b"1 file pulled"
        if command[:4] == ["shell", "cmd", "package", "resolve-activity"]:
            return b"com.test.game/.MainActivity\n"
        if command[:3] == ["shell", "am", "start"]:
            return b"Status: ok\n"
        if command == ["shell", "pidof", "com.test.game"]:
            return b"321\n"
        if command[0] == "logcat":
            return b"321 Unity Ready marker PRIVATE_SERIAL FATAL EXCEPTION\n"
        if command == ["exec-out", "screencap", "-p"]:
            return png()
        if command == ["shell", "dumpsys", "package", "com.test.game"]:
            return (
                b"requested permissions:\n  android.permission.CAMERA\n"
                b"install permissions:\nUser 0: stopped=true\n"
                b"android.permission.CAMERA: granted=true\n"
            )
        if command[:3] in (["shell", "pm", "grant"], ["shell", "am", "force-stop"]):
            return b""
        raise AssertionError(command)

    monkeypatch.setattr(android, "_run", run)
    kwargs = {
        "request_id": str(uuid.uuid4()),
        "build_request_id": str(uuid.uuid4()),
        "device_id": android._device_id(str(tmp_path), "PRIVATE_SERIAL"),
    }
    return context, calls, kwargs, run


def png():
    def chunk(kind, payload):
        body = kind + payload
        return struct.pack(">I", len(payload)) + body + struct.pack(">I", zlib.crc32(body))

    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", 2, 3, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(bytes(27)))
        + chunk(b"IEND", b"")
    )


@pytest.mark.parametrize("operation", ["install", "launch", "stop", "grant", "logs", "screenshot"])
def test_device_workflows_verify_and_replay_without_mutation(device, operation):
    context, calls, kwargs, _ = device
    if operation == "grant":
        kwargs["permission"] = "android.permission.CAMERA"
    if operation == "logs":
        kwargs["marker"] = "Ready marker"
    result = android.execute(operation, **kwargs)
    assert result["state"] == "succeeded"
    assert "PRIVATE_SERIAL" not in json.dumps(result)
    count = len(calls)
    assert android.execute(operation, **kwargs) == result
    assert len(calls) == count
    if operation == "install":
        assert result["result"]["installed_sha256"] == context["build"]["result"]["sha256"]
    if operation in ("logs", "screenshot"):
        artifact = result["result"]
        data = (Path(context["project_path"]) / artifact["path"]).read_bytes()
        assert hashlib.sha256(data).hexdigest() == artifact["sha256"]
        assert b"PRIVATE_SERIAL" not in data


def test_ambiguous_install_is_never_replayed(device, monkeypatch):
    _, calls, kwargs, original = device

    def fail(adb, args, **options):
        if "install" in args:
            calls.append(args)
            raise android.DeviceError("transport lost")
        return original(adb, args, **options)

    monkeypatch.setattr(android, "_run", fail)
    result = android.execute("install", **kwargs)
    assert result["state"] == "unknown" and result["retry_safe"] is False
    count = len(calls)
    assert android.execute("install", **kwargs) == result
    assert len(calls) == count


@pytest.mark.parametrize("state", ["offline", "unauthorized"])
def test_unavailable_device_fails_before_install(device, monkeypatch, state):
    _, calls, kwargs, _ = device
    monkeypatch.setattr(android, "_run", lambda *args, **kw: f"PRIVATE_SERIAL {state}\n".encode())
    result = android.execute("install", **kwargs)
    assert result["state"] == "failed" and result["retry_safe"] is True
    assert not calls


def test_hash_drift_and_changed_request_arguments(device):
    context, calls, kwargs, _ = device
    context["build"]["result"]["sha256"] = "0" * 64
    assert android.execute("install", **kwargs)["state"] == "failed"
    assert not calls
    with pytest.raises(android.DeviceError, match="different arguments"):
        android.execute("launch", **kwargs)


def test_permission_must_be_declared(device):
    _, calls, kwargs, _ = device
    result = android.execute("grant", permission="android.permission.RECORD_AUDIO", **kwargs)
    assert result["state"] == "failed"
    assert not any("grant" in call for call in calls)


def test_no_implicit_device_or_shell_input(device):
    _, calls, kwargs, _ = device
    kwargs["device_id"] = ""
    with pytest.raises(android.DeviceError, match="device_id"):
        android.execute("install", **kwargs)
    assert not calls
    with pytest.raises(android.DeviceError):
        android.execute("shell", **kwargs)


def test_process_timeout_and_output_bound():
    with pytest.raises(android.DeviceError, match="timed out"):
        android._run(sys.executable, ["-c", "import time; time.sleep(5)"], timeout=0.05)
    with pytest.raises(android.DeviceError, match="limit"):
        android._run(sys.executable, ["-c", "print('x' * 10000)"], limit=100)


def test_sdk_precedes_path(tmp_path, monkeypatch):
    import os

    name = "adb.exe" if os.name == "nt" else "adb"
    adb = tmp_path / "platform-tools" / name
    adb.parent.mkdir()
    adb.touch()
    monkeypatch.setattr(android.shutil, "which", lambda name: "wrong-adb")
    assert android._adb({"configured_sdk": str(tmp_path)}) == str(adb)


def test_truncated_screenshot_rejected():
    with pytest.raises(android.DeviceError):
        android._png(png()[:-1])


def test_request_survives_unverified_installed_hash(device, monkeypatch):
    _, _, kwargs, _ = device
    monkeypatch.setattr(android, "_installed_digest", lambda *args: "0" * 64)
    result = android.execute("install", **kwargs)
    assert result["state"] == "unknown"
    assert result["retry_safe"] is False


def test_png_pixel_corruption_is_rejected():
    data = bytearray(png())
    data[45] ^= 1
    with pytest.raises(android.DeviceError, match="checksum"):
        android._png(bytes(data))


@pytest.mark.parametrize("operation", ["install", "launch", "stop", "grant", "logs", "screenshot"])
def test_wrappers_keep_failed_receipts_as_errors(monkeypatch, operation):
    import importlib.util

    names = {
        "install": "install_android_apk",
        "launch": "launch_android_package",
        "stop": "stop_android_package",
        "grant": "grant_android_permission",
        "logs": "read_android_logcat",
        "screenshot": "capture_android_screenshot",
    }
    script = (
        Path(__file__).parents[1]
        / "src/dcc_mcp_unity/skills/unity-project/scripts"
        / (names[operation] + ".py")
    )
    spec = importlib.util.spec_from_file_location("android_wrapper_" + operation, script)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    calls = []

    def fail(*args, **kwargs):
        calls.append((args, kwargs))
        return {"state": "unknown", "retry_safe": False, "message": "Unverified"}

    monkeypatch.setattr(module, "execute", fail)
    args = dict(
        request_id=str(uuid.uuid4()), build_request_id=str(uuid.uuid4()), device_id="0" * 32
    )
    if operation == "grant":
        args["permission"] = "android.permission.CAMERA"
    result = module.main(**args, arbitrary_shell="ignored")
    assert "arbitrary_shell" not in calls[0][1]
    assert result["success"] is False
