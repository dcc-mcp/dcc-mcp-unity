"""Agent-first installation lifecycle for the bundled Unity package."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Sequence

from dcc_mcp_core.install_lifecycle import (
    inspect_install_root,
    safe_remove_tree,
    safe_replace_tree,
    wait_for_sidecar_ready,
)

from .__version__ import __version__

PACKAGE_NAME = "com.dcc-mcp.unity"
MIN_UNITY_VERSION = (2018, 4, 25)
MIN_UNITY_RELEASE = ("f", 1)
MIN_CORE_VERSION = "0.19.90"
SCHEMA_VERSION = "1"
RECEIPT_RELATIVE_PATH = Path(".dcc-mcp") / "receipts" / "unity.json"
EXIT_OK, EXIT_PREFLIGHT, EXIT_ACQUIRE = 0, 10, 20
EXIT_INSTALL, EXIT_VERIFY, EXIT_REQUIRES_RESTART = 30, 40, 50

_EDITOR_VERSION_PATTERN = re.compile(r"^m_EditorVersion:\s*(\S+)\s*$", re.MULTILINE)
_UNITY_VERSION_PATTERN = re.compile(
    r"^(\d+)\.(\d+)\.(\d+)([abfpt])(\d+)(?:[a-z]\d+)*$", re.IGNORECASE
)
_RELEASE_CHANNEL_ORDER = {"a": 0, "b": 1, "f": 2, "t": 2, "p": 3}
_VERBS = {"install", "status", "verify", "uninstall", "upgrade"}


class InstallFailure(ValueError):
    def __init__(self, exit_code: int, stage: str, reason: str):
        super().__init__(reason)
        self.exit_code, self.stage, self.reason = exit_code, stage, reason


def read_unity_version(project: Path) -> str:
    version_file = project / "ProjectSettings" / "ProjectVersion.txt"
    try:
        contents = version_file.read_text(encoding="utf-8-sig")
    except OSError as exc:
        raise ValueError(f"Unity project version file is unavailable: {version_file}") from exc
    match = _EDITOR_VERSION_PATTERN.search(contents)
    if match is None:
        raise ValueError(f"Unity project version is missing from: {version_file}")
    return match.group(1)


def _require_supported_unity_version(version: str) -> None:
    match = _UNITY_VERSION_PATTERN.fullmatch(version)
    if match is None:
        raise ValueError(f"unsupported Unity version format: {version}")
    channel = match.group(4).lower()
    parsed = (
        int(match.group(1)),
        int(match.group(2)),
        int(match.group(3)),
        _RELEASE_CHANNEL_ORDER[channel],
        int(match.group(5)),
    )
    minimum = (
        *MIN_UNITY_VERSION,
        _RELEASE_CHANNEL_ORDER[MIN_UNITY_RELEASE[0]],
        MIN_UNITY_RELEASE[1],
    )
    if parsed < minimum:
        raise ValueError(
            f"Unity {version} is unsupported; DCC-MCP Unity requires Unity 2018.4.25f1 or newer"
        )


def _version_tuple(value: str) -> tuple[int, ...]:
    match = re.match(r"^(\d+(?:\.\d+)*)", value)
    return tuple(int(part) for part in match.group(1).split(".")) if match else ()


def _target_versions(python: Path) -> dict[str, str]:
    code = (
        "import importlib.metadata as m, json; "
        "print(json.dumps({name: m.version(name) for name in "
        "('dcc-mcp-core', 'dcc-mcp-unity')}))"
    )
    try:
        completed = subprocess.run(
            [str(python), "-c", code],
            capture_output=True,
            text=True,
            timeout=15,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise InstallFailure(
            EXIT_PREFLIGHT, "python", f"cannot inspect target interpreter: {exc}"
        ) from exc
    if completed.returncode:
        error_lines = completed.stderr.strip().splitlines()
        reason = error_lines[-1] if error_lines else "package metadata query failed"
        raise InstallFailure(EXIT_PREFLIGHT, "python", reason)
    try:
        versions = json.loads(completed.stdout.strip())
    except json.JSONDecodeError as exc:
        raise InstallFailure(
            EXIT_PREFLIGHT, "python", "target interpreter returned invalid package metadata"
        ) from exc
    version = versions["dcc-mcp-core"]
    if _version_tuple(version) < _version_tuple(MIN_CORE_VERSION):
        raise InstallFailure(
            EXIT_PREFLIGHT,
            "core",
            f"dcc-mcp-core {version} is unsupported; "
            f"version {MIN_CORE_VERSION} or newer is required",
        )
    return versions


def _resolve_project(value: Path | None) -> Path:
    project = (value or Path.cwd()).expanduser().resolve()
    if not (project / "Assets").is_dir() or not (project / "ProjectSettings").is_dir():
        raise InstallFailure(EXIT_PREFLIGHT, "project", f"not a Unity project: {project}")
    try:
        _require_supported_unity_version(read_unity_version(project))
    except ValueError as exc:
        raise InstallFailure(EXIT_PREFLIGHT, "unity_version", str(exc)) from exc
    writable_root = project / "Packages" if (project / "Packages").exists() else project
    if not os.access(writable_root, os.W_OK):
        raise InstallFailure(
            EXIT_PREFLIGHT, "permissions", f"Unity project is not writable: {writable_root}"
        )
    return project


def _resolve_python(value: Path | None) -> Path:
    configured = value
    if configured is None and os.environ.get("DCC_MCP_INSTALL_PYTHON"):
        configured = Path(os.environ["DCC_MCP_INSTALL_PYTHON"])
    resolved = (configured or Path(sys.executable)).expanduser().resolve()
    if not resolved.is_file():
        raise InstallFailure(EXIT_PREFLIGHT, "python", f"Python interpreter not found: {resolved}")
    return resolved


def _resolve_editor(value: Path | None, version: str) -> Path | None:
    configured = value
    if configured is None and os.environ.get("UNITY_EDITOR_PATH"):
        configured = Path(os.environ["UNITY_EDITOR_PATH"])
    if configured is not None:
        editor = configured.expanduser().resolve()
        if editor.is_dir():
            choices = [editor / "Unity.exe", editor / "Unity", editor / "Contents/MacOS/Unity"]
            editor = next((path for path in choices if path.is_file()), editor)
        if not editor.is_file():
            raise InstallFailure(
                EXIT_PREFLIGHT, "unity_editor", f"Unity Editor not found: {editor}"
            )
        return editor
    candidates: list[Path] = []
    if os.name == "nt":
        for variable in ("ProgramFiles", "ProgramFiles(x86)"):
            if os.environ.get(variable):
                candidates.append(
                    Path(os.environ[variable])
                    / "Unity"
                    / "Hub"
                    / "Editor"
                    / version
                    / "Editor"
                    / "Unity.exe"
                )
    elif sys.platform == "darwin":
        candidates.append(
            Path("/Applications/Unity/Hub/Editor") / version / "Unity.app/Contents/MacOS/Unity"
        )
    else:
        candidates.extend([Path("/usr/bin/unity-editor"), Path("/usr/local/bin/unity-editor")])
    return next((path.resolve() for path in candidates if path.is_file()), None)


def _source_package() -> Path:
    source = Path(__file__).resolve().parent / "unity_package"
    if not (source / "package.json").is_file():
        raise InstallFailure(EXIT_ACQUIRE, "package", f"bundled Unity package is missing: {source}")
    return source


def _files_manifest(root: Path) -> list[dict[str, Any]]:
    result = []
    for path in sorted(item for item in root.rglob("*") if item.is_file()):
        data = path.read_bytes()
        result.append(
            {
                "path": path.relative_to(root).as_posix(),
                "sha256": hashlib.sha256(data).hexdigest(),
                "size": len(data),
            }
        )
    return result


def _manifest_digest(files: list[dict[str, Any]]) -> str:
    payload = json.dumps(files, sort_keys=True, separators=(",", ":")).encode()
    return hashlib.sha256(payload).hexdigest()


def _read_receipt(project: Path) -> dict[str, Any] | None:
    path = project / RECEIPT_RELATIVE_PATH
    if not path.is_file():
        return None
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise InstallFailure(
            EXIT_PREFLIGHT, "receipt", f"install receipt is unreadable: {path}"
        ) from exc
    if payload.get("schema_version") != SCHEMA_VERSION:
        raise InstallFailure(EXIT_PREFLIGHT, "receipt", f"unsupported install receipt: {path}")
    return payload


def _write_json_atomic(path: Path, payload: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f".{path.name}.{uuid.uuid4().hex}.tmp")
    temporary.write_text(json.dumps(payload, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    os.replace(temporary, path)


def _installation_state(project: Path, source_files: list[dict[str, Any]]) -> str:
    target, receipt = project / "Packages" / PACKAGE_NAME, _read_receipt(project)
    if not target.exists():
        return "partial" if receipt else "fresh"
    if receipt is None:
        return "partial"
    if (
        Path(receipt.get("project_path", "")).resolve() != project.resolve()
        or Path(receipt.get("package_path", "")).resolve() != target.resolve()
    ):
        return "partial"
    try:
        target_digest = _manifest_digest(_files_manifest(target))
    except OSError:
        return "partial"
    if target_digest != receipt.get("package_digest"):
        return "repair"
    return "current" if target_digest == _manifest_digest(source_files) else "upgrade"


def _next_steps(project: Path, dcc_path: str | None) -> list[dict[str, Any]]:
    project_text = str(project)
    return [
        {
            "id": "open-unity-project",
            "description": "Open or restart the Unity project and wait for package compilation.",
            "command": [dcc_path or "unity", "-projectPath", project_text],
            "why": "Unity must load the Editor package before the adapter can become ready.",
        },
        {
            "id": "start-sidecar",
            "description": "Start the DCC-MCP Unity sidecar if Unity does not launch it.",
            "command": ["dcc-mcp-unity"],
            "why": "The Editor bridge requires the Python sidecar on loopback.",
        },
        {
            "id": "verify-ready",
            "description": "Verify import, package integrity, and live Unity readiness.",
            "command": ["dcc-mcp-unity", "verify", "--project", project_text, "--json"],
            "why": "A copied package is not usable until Unity and the sidecar are ready.",
        },
    ]


def plan(
    verb: str,
    project_value: Path | None,
    python_value: Path | None,
    dcc_path: Path | None,
) -> dict[str, Any]:
    project, python = _resolve_project(project_value), _resolve_python(python_value)
    version = read_unity_version(project)
    editor = _resolve_editor(dcc_path, version)
    source_files = _files_manifest(_source_package())
    state = _installation_state(project, source_files)
    target_versions = _target_versions(python)
    return {
        "schema_version": SCHEMA_VERSION,
        "status": "planned",
        "dcc_type": "unity",
        "verb": verb,
        "adapter_version": __version__,
        "core_version": target_versions["dcc-mcp-core"],
        "target_adapter_version": target_versions["dcc-mcp-unity"],
        "project_path": str(project),
        "unity_version": version,
        "dcc_path": str(editor) if editor else None,
        "python": str(python),
        "installation_state": state,
        "steps": [
            {"id": "preflight", "status": "ok", "unity_version": version},
            {"id": "resolve-python", "status": "ok", "path": str(python)},
            {
                "id": "resolve-unity-editor",
                "status": "ok" if editor else "not-found",
                "path": str(editor) if editor else None,
            },
            {"id": verb, "status": "planned", "installation_state": state},
        ],
        "next_steps": [],
        "receipt_path": str(project / RECEIPT_RELATIVE_PATH),
        "verify": None,
    }


def _replace_package(project: Path, report: dict[str, Any]) -> None:
    source, target = _source_package(), project / "Packages" / PACKAGE_NAME
    target.parent.mkdir(parents=True, exist_ok=True)
    lock_state = inspect_install_root(target)
    if lock_state.get("requires_restart"):
        raise InstallFailure(
            EXIT_REQUIRES_RESTART,
            "install",
            lock_state.get("recommended_next_action", "Unity restart required"),
        )
    token = uuid.uuid4().hex
    stage = target.parent / f".{PACKAGE_NAME}.{token}.stage"
    backup = target.parent / f".{PACKAGE_NAME}.{token}.backup"
    receipt_path = project / RECEIPT_RELATIVE_PATH
    old_receipt = receipt_path.read_bytes() if receipt_path.is_file() else None
    staged = safe_replace_tree(source, stage)
    if not staged.get("success"):
        safe_remove_tree(stage)
        code = EXIT_REQUIRES_RESTART if staged.get("requires_restart") else EXIT_INSTALL
        raise InstallFailure(code, "stage", staged.get("message", "failed to stage package"))
    previous_moved = False
    replacement_moved = False
    try:
        if target.exists():
            os.replace(target, backup)
            previous_moved = True
        os.replace(stage, target)
        replacement_moved = True
        files = _files_manifest(target)
        _write_json_atomic(
            receipt_path,
            {
                "schema_version": SCHEMA_VERSION,
                "dcc_type": "unity",
                "adapter_version": __version__,
                "core_version": report["core_version"],
                "project_path": str(project),
                "package_path": str(target),
                "unity_version": report["unity_version"],
                "dcc_path": report["dcc_path"],
                "python": report["python"],
                "installed_at": datetime.now(timezone.utc).isoformat(),
                "package_digest": _manifest_digest(files),
                "files": files,
            },
        )
    except Exception as exc:
        failed = target.parent / f".{PACKAGE_NAME}.{token}.failed"
        if replacement_moved and target.exists():
            os.replace(target, failed)
        if previous_moved and backup.exists():
            os.replace(backup, target)
        if old_receipt is None:
            receipt_path.unlink(missing_ok=True)
        else:
            receipt_path.parent.mkdir(parents=True, exist_ok=True)
            receipt_path.write_bytes(old_receipt)
        safe_remove_tree(failed)
        safe_remove_tree(stage)
        raise InstallFailure(EXIT_INSTALL, "install", f"install rolled back: {exc}") from exc
    finally:
        if stage.exists():
            safe_remove_tree(stage)
    if backup.exists():
        removed = safe_remove_tree(backup)
        if not removed.get("success"):
            code = EXIT_REQUIRES_RESTART if removed.get("requires_restart") else EXIT_INSTALL
            raise InstallFailure(code, "cleanup", removed.get("message", "backup cleanup failed"))


def _python_import_check(python: Path) -> dict[str, Any]:
    code = (
        "import json, dcc_mcp_unity; "
        "print(json.dumps({'version': dcc_mcp_unity.__version__, 'importable': True}))"
    )
    try:
        completed = subprocess.run(
            [str(python), "-c", code],
            capture_output=True,
            text=True,
            timeout=15,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        return {"success": False, "reason": str(exc)}
    if completed.returncode:
        error_lines = completed.stderr.strip().splitlines()
        return {"success": False, "reason": error_lines[-1] if error_lines else "import failed"}
    try:
        payload = json.loads(completed.stdout.strip())
    except json.JSONDecodeError:
        return {"success": False, "reason": "target interpreter returned invalid output"}
    if payload.get("version") != __version__:
        return {
            "success": False,
            "version": payload.get("version"),
            "expected_version": __version__,
            "reason": "target interpreter adapter version does not match the installed package",
        }
    return {"success": bool(payload.get("importable")), **payload}


def verify_install(project: Path, python: Path, timeout: float) -> dict[str, Any]:
    target, receipt = project / "Packages" / PACKAGE_NAME, _read_receipt(project)
    result: dict[str, Any] = {
        "directly_usable": False,
        "failure_stage": None,
        "failure_reason": None,
        "artifact": {"success": False},
        "import": {"success": False},
        "readiness": {"success": False},
    }
    if receipt is None or not target.is_dir():
        result.update(failure_stage="artifact", failure_reason="package or receipt is missing")
        return result
    if (
        Path(receipt.get("project_path", "")).resolve() != project.resolve()
        or Path(receipt.get("package_path", "")).resolve() != target.resolve()
    ):
        result.update(
            failure_stage="artifact", failure_reason="receipt path does not match project"
        )
        return result
    actual = _manifest_digest(_files_manifest(target))
    expected = receipt.get("package_digest")
    result["artifact"] = {
        "success": actual == expected,
        "expected_sha256": expected,
        "actual_sha256": actual,
    }
    if actual != expected:
        result.update(failure_stage="artifact", failure_reason="package differs from receipt")
        return result
    result["import"] = _python_import_check(python)
    if not result["import"].get("success"):
        result.update(failure_stage="import", failure_reason=result["import"].get("reason"))
        return result
    readiness = wait_for_sidecar_ready(
        dcc_type="unity",
        timeout_secs=timeout,
        probe_tool="unity_diagnostics__ping",
    )
    result["readiness"] = readiness
    if not readiness.get("success"):
        result.update(
            failure_stage="readiness",
            failure_reason=readiness.get("message", "Unity sidecar is not ready"),
        )
        return result
    result["directly_usable"] = True
    return result


def _execute_install(report: dict[str, Any], timeout: float) -> tuple[dict[str, Any], int]:
    project, state = Path(report["project_path"]), report["installation_state"]
    if state != "current":
        _replace_package(project, report)
    report["steps"][-1] = {"id": report["verb"], "status": "ok", "previous_state": state}
    report["verify"] = verify_install(project, Path(report["python"]), timeout)
    if report["verify"]["directly_usable"]:
        report["status"] = "ok"
        return report, EXIT_OK
    report["next_steps"] = _next_steps(project, report.get("dcc_path"))
    if report["verify"]["failure_stage"] == "readiness":
        report["status"] = "requires_restart"
        return report, EXIT_REQUIRES_RESTART
    report["status"] = "failed"
    return report, EXIT_VERIFY


def _execute_uninstall(report: dict[str, Any]) -> tuple[dict[str, Any], int]:
    project = Path(report["project_path"])
    target, receipt_path = project / "Packages" / PACKAGE_NAME, project / RECEIPT_RELATIVE_PATH
    receipt = _read_receipt(project)
    if not target.exists() and receipt is None:
        report["status"] = "ok"
        report["steps"][-1] = {"id": "uninstall", "status": "already-absent"}
        return report, EXIT_OK
    if receipt is None:
        raise InstallFailure(
            EXIT_PREFLIGHT,
            "receipt",
            f"refusing to remove unreceipted package {target}; run install --yes to repair it",
        )
    if Path(receipt.get("package_path", "")).resolve() != target.resolve():
        raise InstallFailure(EXIT_PREFLIGHT, "receipt", "receipt path does not match project")
    lock_state = inspect_install_root(target)
    if lock_state.get("requires_restart"):
        raise InstallFailure(
            EXIT_REQUIRES_RESTART,
            "uninstall",
            lock_state.get("recommended_next_action", "Unity restart required"),
        )
    backup = target.parent / f".{PACKAGE_NAME}.{uuid.uuid4().hex}.uninstall"
    try:
        if target.exists():
            os.replace(target, backup)
        receipt_path.unlink()
    except Exception as exc:
        if backup.exists():
            os.replace(backup, target)
        raise InstallFailure(EXIT_INSTALL, "uninstall", f"uninstall rolled back: {exc}") from exc
    removed = safe_remove_tree(backup)
    if not removed.get("success"):
        code = EXIT_REQUIRES_RESTART if removed.get("requires_restart") else EXIT_INSTALL
        raise InstallFailure(code, "uninstall", removed.get("message", "cleanup failed"))
    report["status"] = "ok"
    report["steps"][-1] = {"id": "uninstall", "status": "ok"}
    return report, EXIT_OK


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Install and verify DCC-MCP Unity.")
    subparsers = parser.add_subparsers(dest="verb", required=True)
    for verb in sorted(_VERBS):
        command = subparsers.add_parser(verb)
        command.add_argument("--project", type=Path, help="Unity project; defaults to cwd.")
        command.add_argument("--dcc-path", type=Path, help="Unity Editor executable or directory.")
        command.add_argument("--python", type=Path, help="Target Python interpreter.")
        command.add_argument("--json", action="store_true", dest="as_json")
        command.add_argument("--yes", action="store_true", help="Execute the planned mutation.")
        command.add_argument("--dry-run", action="store_true", help="Print plan without changes.")
        command.add_argument("--ready-timeout", type=float, default=0.0, help=argparse.SUPPRESS)
    return parser


def _failure_result(
    verb: str, failure: InstallFailure, as_json: bool
) -> tuple[dict[str, Any], int, bool]:
    return (
        {
            "schema_version": SCHEMA_VERSION,
            "status": "requires_restart"
            if failure.exit_code == EXIT_REQUIRES_RESTART
            else "failed",
            "dcc_type": "unity",
            "verb": verb,
            "adapter_version": __version__,
            "core_version": None,
            "steps": [{"id": failure.stage, "status": "failed", "reason": failure.reason}],
            "next_steps": [],
            "receipt_path": None,
            "verify": {
                "directly_usable": False,
                "failure_stage": failure.stage,
                "failure_reason": failure.reason,
            },
        },
        failure.exit_code,
        as_json,
    )


def run(argv: Sequence[str]) -> tuple[dict[str, Any], int, bool]:
    args = _parser().parse_args(list(argv))
    mutating = args.verb in {"install", "upgrade", "uninstall"}
    report = None
    try:
        report = plan(args.verb, args.project, args.python, args.dcc_path)
        if args.dry_run or (mutating and not args.yes):
            return report, EXIT_OK, args.as_json
        if args.verb in {"install", "upgrade"}:
            if args.verb == "upgrade" and report["installation_state"] == "fresh":
                raise InstallFailure(EXIT_PREFLIGHT, "upgrade", "nothing is installed; use install")
            report, code = _execute_install(report, max(0.0, args.ready_timeout))
        elif args.verb == "uninstall":
            report, code = _execute_uninstall(report)
        elif args.verb == "verify":
            report["verify"] = verify_install(
                Path(report["project_path"]), Path(report["python"]), max(0.0, args.ready_timeout)
            )
            report["status"] = "ok" if report["verify"]["directly_usable"] else "failed"
            if report["status"] == "failed":
                report["next_steps"] = _next_steps(
                    Path(report["project_path"]), report.get("dcc_path")
                )
            code = EXIT_OK if report["status"] == "ok" else EXIT_VERIFY
        else:
            state = report["installation_state"]
            report["status"] = "ok" if state == "current" else state
            report["steps"][-1] = {
                "id": "status",
                "status": report["status"],
                "installation_state": state,
            }
            code = EXIT_OK if state in {"fresh", "current"} else EXIT_VERIFY
        return report, code, args.as_json
    except InstallFailure as exc:
        return _failure_result(args.verb, exc, args.as_json)
    except OSError as exc:
        code = EXIT_PREFLIGHT if report is None else (EXIT_INSTALL if mutating else EXIT_VERIFY)
        return _failure_result(
            args.verb,
            InstallFailure(code, args.verb, f"{exc.__class__.__name__}: {exc}"),
            args.as_json,
        )


def _print_report(report: dict[str, Any], as_json: bool) -> None:
    if as_json:
        print(json.dumps(report, sort_keys=True, separators=(",", ":")))
        return
    print(f"DCC-MCP Unity {report.get('verb')}: {report['status']}")
    if report.get("project_path"):
        print(f"Project: {report['project_path']}")
    if report.get("installation_state"):
        print(f"Installation: {report['installation_state']}")
    verification = report.get("verify") or {}
    if verification.get("failure_reason"):
        print(f"Verification: {verification['failure_reason']}")
    for step in report.get("next_steps", []):
        print(f"Next: {step['description']}")


def install_package(project: Path, *, overwrite: bool = False) -> Path:
    """Compatibility API that performs a transactional package install."""
    project = _resolve_project(project)
    state = _installation_state(project, _files_manifest(_source_package()))
    if state != "fresh" and not overwrite:
        raise FileExistsError(f"package already exists: {project / 'Packages' / PACKAGE_NAME}")
    report = plan("upgrade" if overwrite else "install", project, None, None)
    _replace_package(project, report)
    return project / "Packages" / PACKAGE_NAME


def main(argv: Sequence[str] | None = None) -> None:
    """Run the standard CLI, retaining the legacy installer argument shape."""
    resolved = list(sys.argv[1:] if argv is None else argv)
    if resolved and resolved[0] not in _VERBS:
        legacy = argparse.ArgumentParser(description="Install DCC-MCP Unity into a Unity project.")
        legacy.add_argument("project", type=Path)
        legacy.add_argument("--overwrite", action="store_true")
        options = legacy.parse_args(resolved)
        print(install_package(options.project, overwrite=options.overwrite))
        return
    report, code, as_json = run(resolved)
    _print_report(report, as_json)
    if code:
        raise SystemExit(code)


if __name__ == "__main__":
    main()
