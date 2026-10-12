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
  modern Visual Studio installation with x64 compiler, linker, headers and libraries.
  Registered VS2015 C++ installations are also recognized through the 32-bit
  `VisualStudio/SxS/VS7` `14.0` entry and their native or cross-hosted x64 files;
  they do not need `vswhere`. Older VS registrations and ambient PATH are not used.
- A registered Windows SDK has x64 headers, libraries and resource/manifest tools,
  including the unversioned `bin/x64` layout through SDK build 14393. Newer SDKs
  require versioned tools, following the
  [documented SDK layout change](https://learn.microsoft.com/en-us/windows/uwp/packaging/install-universal-windows-apps-with-the-winappdeploycmd-tool).

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
for supported compiler and SDK requirements. The
[Unity 2021.3 requirements](https://docs.unity3d.com/2021.3/Documentation/Manual/system-requirements.html#desktop)
allow VS2015 with C++ tools and a Windows 10 SDK. Requirements belong to the
installed editor version, rather than a universal MSVC version in this adapter.
Legacy registration follows Microsoft's
[vswhere LegacyProvider](https://github.com/microsoft/vswhere/blob/44ff88ff4dd03f718493152b9d59c6436dc5d55a/src/vswhere.lib/LegacyProvider.h);
it is separate from modern component-filtered discovery. Redirected `vswhere`
output requests and decodes UTF-8 so non-ASCII installation paths are preserved.

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
the report's `arch` and `host_arch` must both confirm the requested `x64`.
These are the existing
[QueryResult fields](https://github.com/loonghao/msvc-kit/blob/14e692d0f5a91e98b7e07abe024960de5dd09bd3/src/query/mod.rs).
Its environment is applied only to the PyOxidizer child process. The build
records the report in `dist/standalone/msvc-kit-toolchain.json`, covered by
`SHA256SUMS`. A requested missing CLI, failed probe or mismatched version fails
the build. Without this opt-in, the existing PyOxidizer environment is used.
No unreleased msvc-kit release is installed automatically. Its exported `PATH`,
`INCLUDE` and `LIB` search lists must each be present and non-empty, checked
case-insensitively; duplicate spellings of a key are rejected. This follows
[msvc-kit's environment export](https://github.com/loonghao/msvc-kit/blob/14e692d0f5a91e98b7e07abe024960de5dd09bd3/src/env/mod.rs).
The selected PATH is prepended to inherited PATH, while INCLUDE and LIB replace
inherited values. An incomplete report cannot fall back to ambient search lists.

The release workflow accepts these optional repository variables for Windows:
`DCC_MCP_UNITY_MSVC_KIT`, `DCC_MCP_UNITY_MSVC_KIT_DIR`,
`DCC_MCP_UNITY_MSVC_VERSION` and `DCC_MCP_UNITY_SDK_VERSION`. The executable and
toolchain must already exist on that runner; variables do not provision them.
The same names work as environment variables for local builds.

## Contract checks

Python tests cover absent opt-in, exact pins, environment isolation, real CLI
failure propagation and incomplete/unsupported reports. On Windows, the tests
compile the C# helper with the already-installed .NET Framework C# compiler:

```powershell
python -m pytest tests/test_windows_toolchain.py
```

The C# fixtures exercise conditional behavior, VS2015 without `vswhere`, modern
C++ installations, both SDK layouts, missing files/registrations and an actual
UTF-8 subprocess round trip for a Chinese installation path. Fixtures create
only temporary files and inject registry reads; they never edit the registry.
Set `UNITY_EDITOR_CONTENTS` to use an installed Editor's Mono compiler instead.
A requested compiler that is missing fails rather than silently skipping.

The existing Windows CI jobs also set `DCC_MCP_REQUIRE_WINDOWS_TOOLCHAIN=1`, which
runs the probe's `--discover` mode against the runner's registered VS/SDK. This is
read-only discovery using preinstalled tools, with no Unity/SDK downloads or
system PATH/registry changes. Other operating systems skip this Windows probe.
The probe's optional Editor path argument additionally checks player resources.

These checks do not execute a Windows IL2CPP player build, a real msvc-kit doctor
compile, or a full PyOxidizer standalone build. Those remain separate acceptance
checks; passing discovery does not establish Unity compiler compatibility.
