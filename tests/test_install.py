from pathlib import Path

import pytest

from dcc_mcp_unity.install import PACKAGE_NAME, install_package, read_unity_version


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
    make_unity_project(tmp_path, "2020.3.48f1")
    with pytest.raises(ValueError, match="requires Unity 2021.3 or newer"):
        install_package(tmp_path)


def test_read_unity_version_preserves_exact_editor_version(tmp_path: Path):
    make_unity_project(tmp_path, "6000.0.31f1")
    assert read_unity_version(tmp_path) == "6000.0.31f1"
