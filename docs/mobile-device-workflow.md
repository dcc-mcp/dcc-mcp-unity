# Mobile game input and device testing

Start with `unity_project__inspect_mobile_input(path, target_platform)`. This only
classifies the supplied file or directory. It does not extract files, open a
project, install a game, sign a package, connect to a phone, or launch anything.
Container recognition is not signature or compatibility verification.

| Input | Android route | iOS route | Adapter coverage |
| --- | --- | --- | --- |
| Unity source project/archive | Review/open in compatible Unity; install Android modules; build APK | Export/build using Unity iOS support and supported Mac/Xcode; configure signing/provisioning | Android APK build and completed-build deployment; iOS guidance only |
| APK | Verify identity, signing, ABI/API and digest; no source needed in principle | Obtain an iOS build/source | External APK import remains follow-up work |
| AAB | Generate/sign device-specific APK set with bundletool | Obtain an iOS build/source | Conversion and split APK installation not implemented |
| IPA | Obtain an Android build/source | Check distribution method, signing and device eligibility | Guidance only; no IPA deployment tool |
| Xcode project | Obtain Android source/build | Build/sign on supported Mac/Xcode, pair device | Guidance only |
| Windows EXE | Obtain Android source/build | Obtain iOS source/build | No binary conversion |

Do not assume the user has source code. Ask for the intended platform if unclear,
then only the missing prerequisite for the selected route. When discussing the
capability without files, explain the matrix rather than requesting a phone now.

## Completed Unity Android APK workflow

1. Use `build_android_player` with a UUID `request_id` and `artifact_kind: apk`.
   Wait for `inspect_job` to report `succeeded`. The build records its package
   identifier, request-scoped path, size and SHA-256. Older jobs lacking the
   captured package identifier require a new build.
2. For an actual deployment request, connect the phone by USB, enable USB
   debugging and manually authorize the computer on the phone. Discover with
   `enumerate_android_devices` and select one exact opaque `device_id`. Multiple
   devices, offline devices and unauthorized devices are never silently selected.
3. Call `install_android_apk` with a fresh UUID `request_id`, the completed
   `build_request_id` and the selected `device_id`. It stages the verified APK,
   installs it, retrieves the installed base APK and compares its SHA-256.
4. Call `launch_android_package` with another request UUID. It verifies the
   installed APK and resolves the package's launcher activity. `stop_android_package`
   explicitly stops it; restart consists of stop and then launch with distinct IDs.
5. `grant_android_permission` grants only one explicitly requested declared
   permission. `read_android_logcat` captures a bounded live main-process log and
   can wait for a literal marker. `capture_android_screenshot` returns a verified
   PNG with dimensions, bytes, SHA-256 and a request-scoped relative artifact path.

Each operation runs as a normal Core async job, with its terminal receipt under
`device_request`. Reusing identical request arguments returns the stored receipt;
it never repeats installation or launch. An ambiguous transport failure reports
`state: unknown` and `retry_safe: false`. Inspect the device before intentionally
starting a replacement request. Receipts survive sidecar restarts in the same
project. Device IDs are stable project-scoped hashes; raw serials are not returned.

No raw ADB shell tool is exposed. SDK resolution prefers Unity configuration and
the active Editor's bundled SDK before PATH. The adapter never bypasses device
authorization, trust or lock-screen prompts. Logs summarize only captured messages
from the current main PID; they cannot prove that no crash or ANR happened after
process exit or in another process. A device screenshot is the selected device's
display, not proof that the game is foreground or visually correct.

## Follow-up boundaries

External APK support should add an explicit import receipt capturing SHA-256,
package/version/ABI/API and signing-certificate metadata from Android SDK tools.
That receipt can share the existing verified deployment path without pretending
the artifact came from a Unity build. AAB/split APKs need a separate artifact-set
contract and certificate checks; a single base-APK hash is insufficient.

iOS needs a Mac host provider for Xcode build/signing, paired-device discovery,
deployment and artifact/log capture. Signing credentials stay with the operator;
the workflow should report provisioning and device eligibility without exporting
private keys. Windows can coordinate such a host, but this adapter does not yet
implement that provider. Development installations may require Developer Mode;
TestFlight/App Store use their own distribution route.

Official references: [Android ADB](https://developer.android.com/tools/adb),
[AAB testing](https://developer.android.com/guide/app-bundle/test),
[Xcode supported hosts](https://developer.apple.com/xcode/system-requirements/),
[Xcode device signing](https://developer.apple.com/documentation/xcode/building-and-running-an-app),
[Apple Developer Mode](https://developer.apple.com/documentation/xcode/enabling-developer-mode-on-a-device).

Validation in this iteration uses deterministic fake ADB tests and Unity host
tests. No physical Android device acceptance is claimed until an authorized
device has exercised the built APK, launch marker and screenshot path.
