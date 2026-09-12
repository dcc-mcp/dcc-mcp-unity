"""Bounded ADB operations. Unity supplies project/build provenance, never shell text."""

from __future__ import annotations

import hashlib
import json
import os
import re
import shlex
import shutil
import stat
import struct
import subprocess
import tempfile
import time
import uuid
import zlib
from pathlib import Path
from typing import Any

from dcc_mcp_unity.bridge import call_host

PACKAGE = re.compile(r"[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*)+")


class DeviceError(RuntimeError):
    """Public-safe error, with no raw ADB output or serial number."""


def _run(adb: str, args: list[str], timeout: float = 30, limit: int = 1024 * 1024) -> bytes:
    # Spool both streams to avoid deadlocks and unbounded process output in memory.
    with tempfile.TemporaryFile() as output, tempfile.TemporaryFile() as errors:
        options = {"creationflags": subprocess.CREATE_NO_WINDOW} if os.name == "nt" else {}
        try:
            process = subprocess.Popen(
                [adb, *args], stdout=output, stderr=errors, stdin=subprocess.DEVNULL, **options
            )
        except OSError:
            raise DeviceError("ADB could not be started.") from None
        deadline = time.monotonic() + timeout
        try:
            while process.poll() is None:
                if time.monotonic() >= deadline:
                    raise DeviceError(
                        "ADB timed out; transport or mutation outcome may be unknown."
                    )
                if os.fstat(output.fileno()).st_size + os.fstat(errors.fileno()).st_size > limit:
                    raise DeviceError("ADB output exceeded the request limit.")
                time.sleep(0.05)
            if process.returncode:
                raise DeviceError("ADB failed; inspect device authorization and transport state.")
            if os.fstat(output.fileno()).st_size + os.fstat(errors.fileno()).st_size > limit:
                raise DeviceError("ADB output exceeded the request limit.")
            output.seek(0)
            return output.read(limit)
        finally:
            if process.poll() is None:
                process.kill()
            process.wait()


def _adb(context: dict) -> str:
    name = "adb.exe" if os.name == "nt" else "adb"
    for sdk in (context.get("configured_sdk"), context.get("bundled_sdk")):
        if sdk:
            candidate = Path(sdk) / "platform-tools" / name
            if candidate.is_file():
                return str(candidate)
    found = shutil.which(name)
    if not found:
        raise DeviceError("Install Unity Android SDK platform-tools or configure an Android SDK.")
    return found


def _device_id(project: str, serial: str) -> str:
    return hashlib.sha256((project + "\0" + serial).encode()).hexdigest()[:32]


def _devices(adb: str, project: str) -> list[dict]:
    devices = []
    for line in _run(adb, ["devices", "-l"]).decode("utf-8", "replace").splitlines():
        fields = line.split()
        if len(fields) < 2 or fields[0] in ("List", "*"):
            continue
        serial, state = fields[:2]
        if state not in ("device", "offline", "unauthorized", "no"):
            continue
        devices.append(
            {
                "device_id": _device_id(project, serial),
                "state": state,
                "authorized": state == "device",
                "_serial": serial,
            }
        )
    return devices


def enumerate_devices() -> dict:
    context = call_host("android.context", {})
    adb = _adb(context)
    result = []
    for device in _devices(adb, context["project_path"]):
        serial = device.pop("_serial")
        if device["authorized"]:
            try:
                device["model"] = (
                    _run(adb, ["-s", serial, "shell", "getprop", "ro.product.model"])
                    .decode()
                    .strip()[:128]
                )
                device["api_level"] = int(
                    _run(adb, ["-s", serial, "shell", "getprop", "ro.build.version.sdk"]).strip()
                )
            except (DeviceError, ValueError):
                device["metadata_unavailable"] = True
        result.append(device)
    return {"devices": result, "serials_redacted": True}


def _select(adb: str, project: str, device_id: str) -> str:
    devices = _devices(adb, project)
    selected = [d for d in devices if d["device_id"] == device_id]
    if len(selected) != 1 or not selected[0]["authorized"]:
        raise DeviceError("Select one exact authorized device_id from enumerate_android_devices.")
    return selected[0]["_serial"]


def _safe_child(root: Path, relative: str) -> Path:
    path = root / relative
    resolved = path.resolve()
    if root.resolve() not in resolved.parents:
        raise DeviceError("Artifact path escapes the project.")
    for part in (path, *path.parents):
        attributes = getattr(part.lstat(), "st_file_attributes", 0) if part.exists() else 0
        if part.is_symlink() or attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0):
            raise DeviceError("Artifact paths must not traverse links.")
        if part == root:
            break
    return path


def _sha(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _build(context: dict) -> tuple[Path, str, str]:
    job = context.get("build", {})
    result = job.get("result", {})
    if job.get("state") != "succeeded" or result.get("artifact_kind") != "apk":
        raise DeviceError("A completed APK build job is required.")
    package = result.get("package_name", "")
    if not PACKAGE.fullmatch(package):
        raise DeviceError("Build lacks a valid captured package name; rebuild with this adapter.")
    root = Path(context["project_path"])
    relative = result.get("relative_path", "")
    if not relative.startswith("Builds/DccMcp/") or not relative.endswith(".apk"):
        raise DeviceError("Build artifact path is invalid.")
    apk = _safe_child(root, relative)
    digest = result.get("sha256", "")
    if not apk.is_file() or apk.stat().st_size != result.get("bytes") or _sha(apk) != digest:
        raise DeviceError("APK no longer matches its completed build metadata.")
    return apk, package, digest


def _installed_digest(adb: str, serial: str, package: str, directory: Path) -> str:
    paths = _run(adb, ["-s", serial, "shell", "pm", "path", package]).decode().splitlines()
    if len(paths) != 1 or not re.fullmatch(
        r"package:/data/app/[A-Za-z0-9_./=+~-]+/base\.apk", paths[0]
    ):
        raise DeviceError("Cannot verify one installed base APK for the built package.")
    destination = directory / "installed.apk"
    _run(adb, ["-s", serial, "pull", paths[0][8:], str(destination)], timeout=120)
    try:
        return _sha(destination)
    finally:
        destination.unlink(missing_ok=True)


def _png(data: bytes) -> tuple[int, int]:
    if len(data) < 45 or data[:8] != b"\x89PNG\r\n\x1a\n" or data[12:16] != b"IHDR":
        raise DeviceError("Device screenshot is not a PNG.")
    width, height = struct.unpack(">II", data[16:24])
    if not 0 < width <= 8192 or not 0 < height <= 8192 or width * height > 32_000_000:
        raise DeviceError("Device screenshot dimensions exceed limits.")
    if data[-12:] != b"\x00\x00\x00\x00IEND\xaeB`\x82":
        raise DeviceError("Device screenshot is incomplete.")
    offset = 8
    compressed = bytearray()
    while offset < len(data):
        if offset + 12 > len(data):
            raise DeviceError("Truncated PNG chunk.")
        length = struct.unpack(">I", data[offset : offset + 4])[0]
        end = offset + length + 12
        if end > len(data):
            raise DeviceError("Truncated PNG chunk.")
        chunk = data[offset + 4 : end - 4]
        if zlib.crc32(chunk) & 0xFFFFFFFF != struct.unpack(">I", data[end - 4 : end])[0]:
            raise DeviceError("PNG checksum mismatch.")
        if chunk[:4] == b"IDAT":
            compressed.extend(chunk[4:])
        offset = end
    # Android screencap emits non-interlaced, 8-bit RGB or RGBA.
    depth, color, compression, filtering, interlace = data[24:29]
    if (depth, compression, filtering, interlace) != (8, 0, 0, 0) or color not in (2, 6):
        raise DeviceError("Unsupported screenshot PNG encoding.")
    expected = height * (1 + width * (4 if color == 6 else 3))
    try:
        decoder = zlib.decompressobj()
        pixels = decoder.decompress(compressed, expected + 1)
        if len(pixels) != expected or not decoder.eof or decoder.unused_data:
            raise DeviceError("Invalid screenshot pixel data.")
    except zlib.error:
        raise DeviceError("Invalid screenshot pixel data.") from None
    return width, height


def execute(
    operation: str,
    request_id: str,
    build_request_id: str,
    device_id: str,
    permission: str | None = None,
    marker: str | None = None,
    timeout_seconds: int = 10,
) -> dict:
    """One durable identity; replay returns the receipt and never repeats a device mutation."""
    if operation not in {"install", "launch", "stop", "grant", "logs", "screenshot"}:
        raise DeviceError("Unsupported device operation.")
    try:
        if (
            str(uuid.UUID(request_id)) != request_id
            or str(uuid.UUID(build_request_id)) != build_request_id
        ):
            raise ValueError()
    except (ValueError, TypeError, AttributeError):
        raise DeviceError("Canonical UUID request and build identities are required.") from None
    if not re.fullmatch(r"[0-9a-f]{32}", device_id):
        raise DeviceError("Invalid device_id.")
    if permission is not None and not re.fullmatch(r"android\.permission\.[A-Z_]+", permission):
        raise DeviceError("Invalid Android permission name.")
    if operation == "grant" and permission is None:
        raise DeviceError("An explicit permission is required.")
    if not isinstance(timeout_seconds, int) or not 1 <= timeout_seconds <= 30:
        raise DeviceError("timeout_seconds must be 1..30.")
    if marker is not None and (not isinstance(marker, str) or not 1 <= len(marker) <= 256):
        raise DeviceError("marker must be 1..256 characters.")
    context = call_host("android.context", {"build_request_id": build_request_id})
    root = Path(context["project_path"])
    directory = _safe_child(root, "Library/DccMcp/AndroidDevice/" + request_id)
    directory.mkdir(parents=True, exist_ok=True)
    receipt = directory / "receipt.json"
    fingerprint = hashlib.sha256(
        json.dumps(
            [operation, build_request_id, device_id, permission, marker, timeout_seconds]
        ).encode()
    ).hexdigest()
    # Atomic exclusive creation prevents duplicate mutation across calls/processes.
    pending = {
        "request_id": request_id,
        "fingerprint": fingerprint,
        "state": "unknown",
        "retry_safe": False,
        "message": "Request started; inspect device before a new mutation.",
    }
    try:
        with receipt.open("x", encoding="utf-8") as stream:
            json.dump(pending, stream)
            stream.flush()
            os.fsync(stream.fileno())
    except FileExistsError:
        try:
            previous = json.loads(receipt.read_text(encoding="utf-8"))
        except (ValueError, OSError):
            return pending
        if previous.get("fingerprint") != fingerprint:
            raise DeviceError("request_id already belongs to different arguments.") from None
        return previous
    mutating = operation in {"install", "launch", "stop", "grant"}
    started = False
    try:
        apk, package, digest = _build(context)
        adb = _adb(context)
        serial = _select(adb, context["project_path"], device_id)
        prefix = ["-s", serial]
        result: dict[str, Any] = {"package_name": package, "device_id": device_id}
        if operation != "install":
            if _installed_digest(adb, serial, package, directory) != digest:
                raise DeviceError("Installed APK does not match the requested build.")
        if operation == "install":
            # Install a request-local verified copy to close the build-path replacement race.
            staged = directory / "input.apk"
            shutil.copyfile(apk, staged)
            if _sha(staged) != digest:
                raise DeviceError("APK changed during staging.")
            started = True
            output = _run(adb, prefix + ["install", "-r", str(staged)], timeout=120)
            if b"Success" not in output.splitlines():
                raise DeviceError("ADB did not confirm installation.")
            installed = _installed_digest(adb, serial, package, directory)
            if installed != digest:
                raise DeviceError("Installed APK digest differs from the build artifact.")
            result["installed_sha256"] = installed
        elif operation == "launch":
            activity = (
                _run(
                    adb,
                    prefix
                    + [
                        "shell",
                        "cmd",
                        "package",
                        "resolve-activity",
                        "--brief",
                        "-a",
                        "android.intent.action.MAIN",
                        "-c",
                        "android.intent.category.LAUNCHER",
                        package,
                    ],
                )
                .decode()
                .strip()
                .splitlines()[-1]
            )
            if not re.fullmatch(re.escape(package) + r"/[A-Za-z0-9_.$]+", activity):
                raise DeviceError("Built package has no unambiguous launcher activity.")
            started = True
            output = _run(adb, prefix + ["shell", "am", "start", "-W", "-n", shlex.quote(activity)])
            if b"Status: ok" not in output:
                raise DeviceError("Android did not confirm activity launch.")
        elif operation == "stop":
            started = True
            _run(adb, prefix + ["shell", "am", "force-stop", package])
            # pidof returns nonzero when absent; use bounded dumpsys to verify stopped state.
            state = _run(adb, prefix + ["shell", "dumpsys", "package", package]).decode()
            if "stopped=true" not in state:
                raise DeviceError("Package stopped state could not be verified.")
        elif operation == "grant":
            state = _run(adb, prefix + ["shell", "dumpsys", "package", package]).decode()
            declared = (
                state.partition("requested permissions:")[2]
                .split("install permissions:")[0]
                .split("User ")[0]
            )
            if permission not in {line.strip() for line in declared.splitlines()}:
                raise DeviceError("Permission is not declared by the installed package.")
            started = True
            _run(adb, prefix + ["shell", "pm", "grant", package, permission])
            state = _run(adb, prefix + ["shell", "dumpsys", "package", package]).decode()
            if permission + ": granted=true" not in state:
                raise DeviceError("Permission grant could not be verified.")
            result["permission"] = permission
        elif operation == "logs":
            deadline = time.monotonic() + timeout_seconds
            while True:
                pid = _run(adb, prefix + ["shell", "pidof", package], timeout=5).decode().strip()
                if not re.fullmatch(r"[0-9]+", pid):
                    raise DeviceError("Package must have one running main process for log capture.")
                data = _run(
                    adb,
                    prefix + ["logcat", "-d", "-t", "1000", "--pid=" + pid, "-v", "threadtime"],
                    timeout=5,
                )
                content = data.decode("utf-8", "replace").replace(serial, "[device]")
                matched = marker is None or marker in content
                if matched or time.monotonic() >= deadline:
                    break
                time.sleep(0.2)
            data = content.encode()
            artifact = directory / "logcat.txt"
            artifact.write_bytes(data)
            result.update(
                marker_found=matched,
                timed_out=not matched,
                crash_detected="FATAL EXCEPTION" in content or "Fatal signal" in content,
                anr_detected="ANR in " in content,
            )
            result.update(_artifact(root, artifact, data))
        else:
            data = _run(adb, prefix + ["exec-out", "screencap", "-p"], limit=32 * 1024 * 1024)
            width, height = _png(data)
            artifact = directory / "screenshot.png"
            artifact.write_bytes(data)
            result.update(width=width, height=height, **_artifact(root, artifact, data))
        terminal = dict(
            pending,
            state="succeeded",
            retry_safe=True,
            result=result,
            message="Completed with verified readback.",
        )
    except (DeviceError, OSError, ValueError, IndexError):
        terminal = dict(
            pending,
            state="unknown" if mutating and started else "failed",
            retry_safe=not (mutating and started),
            message=(
                "Device operation could not be verified. Inspect transport, "
                "authorization, and installed package before retrying."
            ),
        )
    temporary = directory / "receipt.tmp"
    temporary.write_text(json.dumps(terminal), encoding="utf-8")
    os.replace(temporary, receipt)
    return terminal


def _artifact(root: Path, path: Path, data: bytes) -> dict:
    return {
        "path": path.relative_to(root).as_posix(),
        "bytes": len(data),
        "sha256": hashlib.sha256(data).hexdigest(),
    }
