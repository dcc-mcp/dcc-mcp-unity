import json
from pathlib import Path

import pytest

import dcc_mcp_unity.install as installer
from dcc_mcp_unity.install import (
    EXIT_INSTALL,
    EXIT_OK,
    EXIT_REQUIRES_RESTART,
    PACKAGE_NAME,
    RECEIPT_RELATIVE_PATH,
    InstallFailure,
    install_package,
    read_unity_version,
)


def make_unity_project(path: Path, version: str = "2021.3.45f1") -> None:
    (path / "Assets").mkdir()
    (path / "ProjectSettings").mkdir()
    (path / "ProjectSettings" / "ProjectVersion.txt").write_text(
        f"m_EditorVersion: {version}\n",
        encoding="utf-8",
    )


def test_install_copies_bundled_upm_package(tmp_path: Path):
    make_unity_project(tmp_path)
    target = install_package(tmp_path)
    assert target == tmp_path / "Packages" / PACKAGE_NAME
    assert (target / "package.json").is_file()
    assert (target / "Editor" / "DccMcpBridge.cs").is_file()
    assert (target / "Editor" / "DccMcpConsole.cs").is_file()


def test_install_supports_unity_2018_4_lts(tmp_path: Path):
    make_unity_project(tmp_path, "2018.4.25f1")
    assert install_package(tmp_path).is_dir()


@pytest.mark.parametrize(
    "version",
    [
        # Tuanjie 1.x (based on Unity 2022.3 LTS) — documented stable releases
        "2022.3.47t1",  # Tuanjie 1.5
        "2022.3.53t2",  # Tuanjie 1.6
        "2022.3.58t5",  # Tuanjie 1.7
        "2022.3.62t11",  # Tuanjie 1.9 (latest stable as of 2026-07)
        # Tuanjie 1.x edge cases: high patch and release numbers
        "2022.3.99t99",
        "2022.3.62t1",
    ],
)
def test_install_supports_tuanjie_editor(tmp_path: Path, version: str):
    make_unity_project(tmp_path, version)
    assert install_package(tmp_path).is_dir()


def test_tuanjie_version_is_not_rejected_by_minimum_check(tmp_path: Path):
    """Tuanjie t-release channel is ordered equivalently to f-release."""
    # 2022.3.0f1 is the minimum Unity 2022.3; t1 must also pass
    make_unity_project(tmp_path, "2022.3.0t1")
    assert install_package(tmp_path).is_dir()


def test_tuanjie_version_preserved_exact(tmp_path: Path):
    make_unity_project(tmp_path, "2022.3.62t11")
    assert read_unity_version(tmp_path) == "2022.3.62t11"


def test_install_rejects_non_unity_directory(tmp_path: Path):
    with pytest.raises(ValueError, match="not a Unity project"):
        install_package(tmp_path)


def test_install_requires_explicit_overwrite(tmp_path: Path):
    make_unity_project(tmp_path)
    install_package(tmp_path)
    with pytest.raises(FileExistsError, match="package already exists"):
        install_package(tmp_path)
    assert install_package(tmp_path, overwrite=True).is_dir()


def test_install_rejects_unsupported_unity_version(tmp_path: Path):
    make_unity_project(tmp_path, "2018.4.24f1")
    with pytest.raises(ValueError, match="requires Unity 2018.4.25f1 or newer"):
        install_package(tmp_path)


@pytest.mark.parametrize("version", ["2018.4.25b1", "2018.4.25f0"])
def test_install_rejects_prerelease_before_minimum_editor(tmp_path: Path, version: str):
    make_unity_project(tmp_path, version)
    with pytest.raises(ValueError, match="requires Unity 2018.4.25f1 or newer"):
        install_package(tmp_path)


def test_read_unity_version_preserves_exact_editor_version(tmp_path: Path):
    make_unity_project(tmp_path, "6000.0.31f1")
    assert read_unity_version(tmp_path) == "6000.0.31f1"


def test_standard_install_dry_run_is_machine_readable_and_does_not_write(tmp_path: Path):
    make_unity_project(tmp_path)
    report, code, as_json = installer.run(
        ["install", "--project", str(tmp_path), "--json", "--dry-run"]
    )
    assert code == EXIT_OK
    assert as_json is True
    assert report["schema_version"] == "1"
    assert report["status"] == "planned"
    assert report["installation_state"] == "fresh"
    assert report["python"]
    assert not (tmp_path / "Packages" / PACKAGE_NAME).exists()
    assert not (tmp_path / RECEIPT_RELATIVE_PATH).exists()


def test_install_status_and_uninstall_round_trip(tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
    make_unity_project(tmp_path)
    monkeypatch.setattr(
        installer,
        "verify_install",
        lambda *_args: {
            "directly_usable": True,
            "failure_stage": None,
            "failure_reason": None,
        },
    )
    installed, install_code, _ = installer.run(
        ["install", "--project", str(tmp_path), "--yes", "--json"]
    )
    status, status_code, _ = installer.run(["status", "--project", str(tmp_path), "--json"])
    removed, remove_code, _ = installer.run(
        ["uninstall", "--project", str(tmp_path), "--yes", "--json"]
    )
    assert install_code == status_code == remove_code == EXIT_OK
    assert installed["verify"]["directly_usable"] is True
    assert status["installation_state"] == "current"
    assert removed["status"] == "ok"
    assert not (tmp_path / "Packages" / PACKAGE_NAME).exists()
    assert not (tmp_path / RECEIPT_RELATIVE_PATH).exists()


def test_install_failure_restores_previous_package_and_receipt(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
):
    make_unity_project(tmp_path)
    target = install_package(tmp_path)
    marker = target / "previous.txt"
    marker.write_text("keep me", encoding="utf-8")
    old_receipt = (tmp_path / RECEIPT_RELATIVE_PATH).read_bytes()

    def fail_receipt(*_args):
        raise OSError("receipt write failed")

    monkeypatch.setattr(installer, "_write_json_atomic", fail_receipt)
    with pytest.raises(InstallFailure, match="install rolled back") as raised:
        install_package(tmp_path, overwrite=True)
    assert raised.value.exit_code == EXIT_INSTALL
    assert marker.read_text(encoding="utf-8") == "keep me"
    assert (tmp_path / RECEIPT_RELATIVE_PATH).read_bytes() == old_receipt


def test_failed_backup_move_never_displaces_previous_package(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
):
    make_unity_project(tmp_path)
    target = install_package(tmp_path)
    marker = target / "previous.txt"
    marker.write_text("keep me", encoding="utf-8")
    old_receipt = (tmp_path / RECEIPT_RELATIVE_PATH).read_bytes()
    real_replace = installer.os.replace

    def reject_backup(source, destination):
        if Path(source) == target and str(destination).endswith(".backup"):
            raise PermissionError("package is locked")
        return real_replace(source, destination)

    monkeypatch.setattr(installer.os, "replace", reject_backup)
    with pytest.raises(InstallFailure, match="install rolled back"):
        install_package(tmp_path, overwrite=True)
    assert marker.read_text(encoding="utf-8") == "keep me"
    assert (tmp_path / RECEIPT_RELATIVE_PATH).read_bytes() == old_receipt


def test_loaded_install_root_returns_restart_contract(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
):
    make_unity_project(tmp_path)
    monkeypatch.setattr(
        installer,
        "inspect_install_root",
        lambda _path: {
            "requires_restart": True,
            "recommended_next_action": "Close Unity and retry.",
        },
    )
    report, code, _ = installer.run(["install", "--project", str(tmp_path), "--yes", "--json"])
    assert code == EXIT_REQUIRES_RESTART
    assert report["status"] == "requires_restart"
    assert report["verify"]["failure_reason"] == "Close Unity and retry."


def test_preflight_reports_unwritable_project_with_stable_exit_code(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
):
    make_unity_project(tmp_path)
    monkeypatch.setattr(installer.os, "access", lambda *_args: False)
    report, code, _ = installer.run(["install", "--project", str(tmp_path), "--json"])
    assert code == installer.EXIT_PREFLIGHT
    assert report["verify"]["failure_stage"] == "permissions"


def test_verify_rejects_receipt_from_another_project(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
):
    make_unity_project(tmp_path)
    install_package(tmp_path)
    receipt_path = tmp_path / RECEIPT_RELATIVE_PATH
    receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
    receipt["project_path"] = str(tmp_path / "other")
    receipt_path.write_text(json.dumps(receipt), encoding="utf-8")
    monkeypatch.setattr(
        installer,
        "_python_import_check",
        lambda _python: {"success": True, "version": installer.__version__},
    )
    report, code, _ = installer.run(["verify", "--project", str(tmp_path), "--json"])
    assert code == installer.EXIT_VERIFY
    assert report["verify"]["failure_stage"] == "artifact"
    assert report["verify"]["failure_reason"] == "receipt path does not match project"


def test_verify_failure_emits_machine_executable_next_steps(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
):
    make_unity_project(tmp_path)
    install_package(tmp_path)
    monkeypatch.setattr(
        installer,
        "_python_import_check",
        lambda _python: {"success": True, "version": installer.__version__},
    )
    monkeypatch.setattr(
        installer,
        "wait_for_sidecar_ready",
        lambda **_kwargs: {"success": False, "message": "sidecar unavailable"},
    )
    report, code, _ = installer.run(["verify", "--project", str(tmp_path), "--json"])
    assert code == installer.EXIT_VERIFY
    assert report["verify"]["directly_usable"] is False
    assert report["verify"]["failure_stage"] == "readiness"
    assert all("command" in step or "file_edit" in step for step in report["next_steps"])
