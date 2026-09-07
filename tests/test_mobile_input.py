from zipfile import ZipFile

import pytest

from dcc_mcp_unity.mobile_input import inspect_mobile_input


@pytest.mark.parametrize(
    ("entries", "kind", "platform", "route"),
    [
        (["AndroidManifest.xml"], "apk", "android", "external_apk_import_not_implemented"),
        (["AndroidManifest.xml"], "apk", "ios", "platform_build_required"),
        (
            ["base/manifest/AndroidManifest.xml", "BundleConfig.pb"],
            "aab",
            "android",
            "bundle_conversion_required",
        ),
        (["Payload/Game.app/Info.plist"], "ipa", "ios", "ios_deploy_not_implemented"),
        (
            ["Project/ProjectSettings/ProjectVersion.txt"],
            "unity_archive",
            "android",
            "unity_android_build",
        ),
    ],
)
def test_archive_intake_never_extracts_or_claims_ready(tmp_path, entries, kind, platform, route):
    path = tmp_path / "upload.zip"
    with ZipFile(path, "w") as archive:
        for entry in entries:
            archive.writestr(entry, "")
    result = inspect_mobile_input(str(path), platform)
    assert result["input_kind"] == kind
    assert result["route"] == route
    assert result["deployment_ready"] is False
    assert list(tmp_path.iterdir()) == [path]


def test_unity_source_routes_ios_to_supported_host(tmp_path):
    (tmp_path / "Assets").mkdir()
    (tmp_path / "ProjectSettings").mkdir()
    (tmp_path / "ProjectSettings/ProjectVersion.txt").write_text("m_EditorVersion: 2022.3.62f3")
    assert inspect_mobile_input(str(tmp_path), "ios")["route"] == "mac_xcode_required"


def test_windows_binary_requires_mobile_build(tmp_path):
    path = tmp_path / "game.exe"
    path.write_bytes(b"MZ\x00\x00")
    assert inspect_mobile_input(str(path))["route"] == "platform_build_required"


def test_extension_does_not_prove_apk(tmp_path):
    path = tmp_path / "game.apk"
    path.write_bytes(b"unrecognized")
    assert inspect_mobile_input(str(path))["input_kind"] == "unknown"
