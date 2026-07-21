"""Install the bundled UPM package into a Unity project."""

from __future__ import annotations

import argparse
import re
import shutil
from pathlib import Path

PACKAGE_NAME = "com.dcc-mcp.unity"
MIN_UNITY_VERSION = (2021, 3)
_EDITOR_VERSION_PATTERN = re.compile(r"^m_EditorVersion:\s*(\S+)\s*$", re.MULTILINE)


def read_unity_version(project: Path) -> str:
    """Return the exact Editor version recorded by a Unity project."""
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
    match = re.match(r"^(\d+)\.(\d+)", version)
    if match is None:
        raise ValueError(f"unsupported Unity version format: {version}")
    parsed = (int(match.group(1)), int(match.group(2)))
    if parsed < MIN_UNITY_VERSION:
        raise ValueError(
            f"Unity {version} is unsupported; DCC-MCP Unity requires Unity 2021.3 or newer"
        )


def install_package(project: Path, *, overwrite: bool = False) -> Path:
    """Copy the bundled package below the target project's Packages directory."""
    project = project.resolve()
    if not (project / "Assets").is_dir() or not (project / "ProjectSettings").is_dir():
        raise ValueError(f"not a Unity project: {project}")
    _require_supported_unity_version(read_unity_version(project))

    source = Path(__file__).resolve().parent / "unity_package"
    target = project / "Packages" / PACKAGE_NAME
    if target.exists():
        if not overwrite:
            raise FileExistsError(f"package already exists: {target}")
        shutil.rmtree(target)
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copytree(source, target)
    return target


def main() -> None:
    parser = argparse.ArgumentParser(description="Install DCC-MCP Unity into a Unity project.")
    parser.add_argument("project", type=Path)
    parser.add_argument("--overwrite", action="store_true")
    args = parser.parse_args()
    print(install_package(args.project, overwrite=args.overwrite))


if __name__ == "__main__":
    main()
