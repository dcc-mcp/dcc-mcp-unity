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


def test_install_supports_unity_2018_4_lts(tmp_path: Path):
    make_unity_project(tmp_path, "2018.4.25f1")
    assert install_package(tmp_path).is_dir()


@pytest.mark.parametrize(
    "version",
    [
        # Tuanjie 1.x (based on Unity 2022.3 LTS) — documented stable releases
        "2022.3.47t1",   # Tuanjie 1.5
        "2022.3.53t2",   # Tuanjie 1.6
        "2022.3.58t5",   # Tuanjie 1.7
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
