"""Read-only file intake; classification is never installation authorization."""

from pathlib import Path
from zipfile import BadZipFile, ZipFile


def inspect_mobile_input(path: str, target_platform: str = "android") -> dict:
    if target_platform not in ("android", "ios"):
        raise ValueError("target_platform must be android or ios")
    source = Path(path)
    kind = "unknown"
    if source.is_dir():
        if (source / "ProjectSettings/ProjectVersion.txt").is_file() and (
            source / "Assets"
        ).is_dir():
            kind = "unity_project"
        elif source.suffix in (".xcodeproj", ".xcworkspace"):
            marker = (
                "project.pbxproj" if source.suffix == ".xcodeproj" else "contents.xcworkspacedata"
            )
            if (source / marker).is_file():
                kind = "xcode_project"
    elif source.is_file():
        with source.open("rb") as stream:
            magic = stream.read(4)
        if magic[:2] == b"MZ" and source.suffix.lower() == ".exe":
            kind = "windows_executable"
        elif magic[:2] == b"PK":
            try:
                with ZipFile(source) as archive:
                    entries = archive.infolist()
                    if len(entries) > 100000:
                        raise ValueError("Archive has too many entries for bounded inspection")
                    names = {entry.filename for entry in entries}
                    if "AndroidManifest.xml" in names:
                        kind = "apk"
                    elif (
                        "base/manifest/AndroidManifest.xml" in names and "BundleConfig.pb" in names
                    ):
                        kind = "aab"
                    elif any(
                        name.startswith("Payload/") and name.endswith(".app/Info.plist")
                        for name in names
                    ):
                        kind = "ipa"
                    elif any(name.endswith("ProjectSettings/ProjectVersion.txt") for name in names):
                        kind = "unity_archive"
            except BadZipFile:
                kind = "invalid_archive"
    else:
        raise ValueError("Input file or directory does not exist")

    steps = []
    if kind in ("unity_project", "unity_archive"):
        if kind == "unity_archive":
            steps.append(
                "Extract to an isolated directory after reviewing archive "
                "paths; intake does not extract files."
            )
        if target_platform == "android":
            route = "unity_android_build"
            steps += [
                "Open the reviewed project in its compatible Unity Editor with "
                "Android Build Support, SDK and JDK.",
                "Run build_android_player with artifact_kind=apk and inspect_job until succeeded.",
                "Select an authorized device, then install the exact completed "
                "build using install_android_apk.",
            ]
        else:
            route = "mac_xcode_required"
            steps += [
                "Use a supported Mac/Xcode host and Unity iOS Build Support to "
                "export and build the iOS project.",
                "Configure the signing team and provisioning for the target "
                "device; pair and trust the device and enable Developer Mode "
                "when required.",
                "iOS build/deploy tools are not implemented by this adapter.",
            ]
    elif kind == "apk" and target_platform == "android":
        route = "external_apk_import_not_implemented"
        steps = [
            "APK is an Android installable candidate; source code is not "
            "required for an existing compatible signed APK.",
            "Before deployment, verify package identity, signing certificate, "
            "ABI/API compatibility and SHA-256.",
            "This adapter currently installs only completed Unity build "
            "artifacts; external APK provenance/import is a follow-up "
            "capability.",
        ]
    elif kind == "aab" and target_platform == "android":
        route = "bundle_conversion_required"
        steps = [
            "An AAB is not directly installable; generate a signed "
            "device-specific APK set with bundletool.",
            "Split APK import/install is not implemented in this adapter; use a "
            "Unity APK build when source is available.",
        ]
    elif kind in ("ipa", "xcode_project") and target_platform == "ios":
        route = "ios_deploy_not_implemented"
        steps = [
            "Use a supported Mac/Xcode host for the development build and device workflow.",
            "For an IPA, verify its distribution/signing method and "
            "provisioning allow the target device; an IPA alone does not prove "
            "installability.",
            "Pair/trust the device and enable Developer Mode when required. "
            "This adapter has no iOS deployment tools.",
        ]
    elif kind in ("apk", "aab", "ipa", "xcode_project", "windows_executable"):
        route = "platform_build_required"
        steps = [
            "This artifact cannot be converted directly into the requested platform build.",
            "Obtain a compatible mobile artifact or the source project and "
            "required build toolchain.",
        ]
    else:
        route = "clarify_input"
        steps = ["Identify the engine/project format or obtain a supported mobile build artifact."]
    if target_platform == "android" and route in (
        "unity_android_build",
        "external_apk_import_not_implemented",
        "bundle_conversion_required",
    ):
        steps.append(
            "When deployment is requested, connect an Android device, enable "
            "USB debugging and manually authorize this computer; select one "
            "exact device_id."
        )
    return {
        "input_kind": kind,
        "target_platform": target_platform,
        "route": route,
        "deployment_ready": False,
        "next_steps": steps,
        "performed_actions": ["read_only_classification"],
        "verification": (
            "Container structure only; signatures, compatibility and device state are not verified."
        ),
    }
