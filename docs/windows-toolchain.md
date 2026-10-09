# Windows build toolchains

The Python sidecar and C# Editor bridge do not require MSVC for ordinary use.
MSVC and the Windows SDK are conditional build dependencies.

## Windows IL2CPP players

`build_windows_player` preserves the project's scripting backend. Mono builds
do not invoke compiler discovery. For IL2CPP, the persistent job checks before
target switching and again before the actual build:

- The Editor host is Windows.
- The installed Editor supports the Windows target and has x64 IL2CPP player resources.
- Microsoft's Setup API (through the installed `vswhere`) discovers a registered
  Visual Studio installation with x64 compiler, linker, headers and libraries.
- A registered Windows SDK has x64 headers, libraries and resource/manifest tools.

These checks verify the registrations consumed by Windows build tools; they do
not establish that the installed Unity version accepts the selected compiler.
A portable `cl.exe` on PATH or a passing msvc-kit probe cannot alone prove that
Unity recognizes the toolchain. Missing tools or IL2CPP resources fail with an
actionable error before creating the output directory. Install components
supported by that Unity version and restart the Editor after provisioning.

Preflight is followed by Unity's normal `BuildPipeline.BuildPlayer` and artifact
checks. That actual build is the authority for compiler recognition and version
compatibility; only a successful player build establishes build acceptance.

See [Unity's Windows requirements](https://docs.unity3d.com/6000.0/Documentation/Manual/windows-requirements-and-compatibility.html)
for supported compiler and SDK requirements. Requirements belong to the
installed editor version, rather than a universal MSVC version in this adapter.

## Optional standalone sidecar pin

The PyOxidizer builder can opt in to an explicitly provisioned msvc-kit CLI
supporting the `msvc-kit.doctor.v1` JSON contract:

```powershell
python tools/build_binary.py --msvc-kit C:\tools\msvc-kit.exe `
  --msvc-kit-dir C:\tools\compiler `
  --msvc-version 14.44.35207 --sdk-version 10.0.22621.0
```

This example is a complete caller-selected pin, not a compatibility claim for
every Unity version. `doctor --compile` must pass with both exact versions;
its environment is applied only to the PyOxidizer child process. The build
records the report in `dist/standalone/msvc-kit-toolchain.json`, covered by
`SHA256SUMS`. A requested missing CLI, failed probe or mismatched version fails
the build. Without this opt-in, the existing PyOxidizer environment is used.
No unreleased msvc-kit release is installed automatically.

The release workflow accepts these optional repository variables for Windows:
`DCC_MCP_UNITY_MSVC_KIT`, `DCC_MCP_UNITY_MSVC_KIT_DIR`,
`DCC_MCP_UNITY_MSVC_VERSION` and `DCC_MCP_UNITY_SDK_VERSION`. The executable and
toolchain must already exist on that runner; variables do not provision them.
The same names work as environment variables for local builds.

## Contract checks

Python tests cover absent opt-in, exact pins, environment isolation, real CLI
failure propagation and unsupported reports. To compile and run the C#
preflight contract without opening Unity:

```powershell
$env:UNITY_EDITOR_CONTENTS = 'C:\Program Files\Unity\Hub\Editor\<version>\Editor\Data'
python -m pytest tests/test_windows_toolchain.py
```

The compiled probe tests the conditional behavior. Its optional single path
argument invokes registration discovery; that read-only result establishes
preflight readiness, while a real IL2CPP player build remains a separate check.
