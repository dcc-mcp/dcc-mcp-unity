# Windows build toolchains

The Python sidecar and C# Editor bridge need no MSVC for ordinary use. Mono
player builds also remain independent of MSVC. Windows IL2CPP builds and Windows
PyOxidizer packaging require the project's own **msvc-kit v0.2.21**. There is no
silent fallback to an ambient compiler when configuration or diagnostics fail.

## Required native-toolchain selection

Provide all four variables before starting the Windows Editor or standalone
builder:

- `DCC_MCP_UNITY_MSVC_KIT`: absolute path to the official v0.2.21 Windows x64 CLI.
- `DCC_MCP_UNITY_MSVC_KIT_DIR`: explicit portable-layout toolchain root.
- `DCC_MCP_UNITY_MSVC_VERSION`: complete version, for example `14.44.35207`.
- `DCC_MCP_UNITY_SDK_VERSION`: complete version, for example `10.0.22621.0`.

Both consumers verify the CLI's published SHA256 before executing it:
`2c76d707b964040ef39a16af344ab873d47673955e63486f49e537885f7b657b`.
The approved binary is the 7.4 MB asset from the
[official v0.2.21 release](https://github.com/loonghao/msvc-kit/releases/tag/v0.2.21).
Updating this identity is an explicit consumer change, not a `latest` download.

The real command is:

```powershell
& $env:DCC_MCP_UNITY_MSVC_KIT doctor --format json --compile `
  --arch x64 --host-arch x64 `
  --dir $env:DCC_MCP_UNITY_MSVC_KIT_DIR `
  --msvc-version $env:DCC_MCP_UNITY_MSVC_VERSION `
  --sdk-version $env:DCC_MCP_UNITY_SDK_VERSION
```

Its `msvc-kit.doctor.v1` report must confirm the exact versions and x64 host and
target. All five native probe stages (C++ compile, resource compile, link,
manifest, execute) must pass once each; missing, duplicate or skipped stages do
not qualify. Doctor has a 180-second consumer deadline. A missing CLI, wrong
hash, failed probe or mismatched selection blocks the native build and reports
the failure. Doctor never installs missing components.

## Reusing already installed Windows tools in CI

`.github/scripts/prepare-msvc-kit.ps1` is a PowerShell 7 CI preparation script.
It uses registered modern Visual Studio and Windows SDK installations, chooses
complete x64 versions (honoring explicit version pins), downloads only the
pinned CLI, and verifies its published digest. It exports the four variables
through the current job's `GITHUB_ENV`.

msvc-kit expects a single root containing `VC/Tools/MSVC` and `Windows Kits/10`.
The preparation script creates a fresh UUID-named folder under `RUNNER_TEMP`
with directory junctions to the existing registered tools. It records original
paths, exact versions and tool hashes in a preparation manifest. It does not
install VS/SDK/Unity, change registry or machine PATH, or run doctor itself.
Consumer tests run the real doctor separately. Probe output is written only to
separate temporary workspaces. Never recursively delete a junction's target;
the script performs no deletion or cleanup of existing installations.

The release workflow runs this preparation by default on Windows. A custom CLI
or root suppresses automatic preparation; all four custom settings must then
be complete and the CLI must still match the approved digest. Non-Windows builds
do not use the Windows contract. Preparation's selected versions are exact for
that job; release reproducibility across runner-image updates requires explicit
repository MSVC and SDK version pins.

For a local Editor, provision a matching portable root or a read-only view of
existing modern registered tools separately, set the four variables in the
launching process, and restart the Editor. The CI preparation script requires
`RUNNER_TEMP` and `GITHUB_ENV`; it is not a local installation command.

## Windows IL2CPP players

`build_windows_player` preserves the project's scripting backend. For IL2CPP,
the persistent job checks before target switching and again before BuildPlayer:

- Windows Editor host and matching Windows x64 IL2CPP player resources.
- Existing registered VS C++ tools and Windows SDK availability.
- The real, hash-pinned msvc-kit doctor compile contract described above.
- The selected full modern MSVC version, compiler/linker hashes, and selected
  SDK version/resource/manifest-tool hashes correspond to registered tools.

The C# process uses Windows argument quoting and drains both UTF-8 output
streams while waiting; spaces, Unicode and trailing directory separators are
preserved. It does not change the Editor environment, registry or persistent
PATH. Any doctor/registration mismatch fails closed before output creation.
The build response includes `msvc_kit_preflight` with the real report and CLI
identity; `unity_compiler_selection_verified` remains false because preflight
cannot prove the compiler Unity ultimately chooses.

A passing portable probe is not Unity compiler compatibility evidence.
`BuildPipeline.BuildPlayer` remains authoritative. Acceptance still requires an
actual Windows IL2CPP build and build-log readback of compiler/SDK selection.
Compiler compatibility depends on the installed Unity version; see
[Unity Windows requirements](https://docs.unity3d.com/6000.0/Documentation/Manual/windows-requirements-and-compatibility.html).

### Legacy compatibility blocker

The earlier read-only registration checker still recognizes VS2015 and early
Windows 10 SDK layouts, and its fixtures remain. **A VS2015-only native build is
not supported by the new mandatory msvc-kit gate:** v0.2.21 resolves modern
`VC/Tools/MSVC/<version>` layouts and versioned SDK tools, whereas VS2015 uses
legacy compiler layouts, sometimes with x86-hosted x64 tools. The pinned
registration association check therefore requires modern VS as well.

This is a real compatibility gap, not validation supplied by an old green
fixture. Keeping Unity 2018 C#/.NET 4.x compilation support does not certify
Unity 2018 + VS2015 native builds. Preserving that exact native configuration
requires a tested msvc-kit legacy-selection enhancement before this mandatory
gate can be accepted for the full historical support scope.

## Windows standalone packaging

```powershell
python tools/build_binary.py --msvc-kit C:\tools\msvc-kit.exe `
  --msvc-kit-dir C:\tools\compiler `
  --msvc-version 14.44.35207 --sdk-version 10.0.22621.0
```

The standalone consumer requires nonempty, case-unambiguous PATH/INCLUDE/LIB
lists and checks CC/CXX, target-specific compiler variables and Cargo's x64
MSVC linker against the report's selected tools. The report environment is
applied only to the PyOxidizer child; selected PATH is prepended, INCLUDE/LIB
replace inherited lists, and compiler/linker bindings are preserved. Missing
Windows configuration is an error. Non-Windows packaging is unchanged.

Successful packaging writes `dist/standalone/msvc-kit-toolchain.json`, including
the verified CLI identity, into `SHA256SUMS`. The checksum manifest excludes
itself. A full PyOxidizer build and packaged-sidecar execution remain separate
acceptance checks from the small compiler consumer probe.

## Verification boundaries

Focused Python tests cover fail-closed configuration, release identity, doctor
stage/version/architecture validation, diagnostics, isolated environment and
compiler/linker binding. Pure Editor fixtures cover JSON parsing and the
registration-association gate. Windows C# fixtures additionally cover actual
UTF-8 subprocess handling, quoting, modern/legacy discovery and mismatched tool
identity, without writing the registry.

Windows CI is configured to run both real consumers against the hash-verified
released CLI and existing registered VS/SDK: the Python caller compiles and
executes a tiny C++ program using its returned environment, and the compiled C#
helper invokes the real doctor and relates the result to registered tools.
The absent exact-version case must fail. JSON reports, preparation identity and
native smoke evidence are uploaded from `dist/toolchain-evidence`.

These native consumer checks require Windows and are explicitly skipped on
other hosts. They do not execute a Windows Unity IL2CPP player build or a full
PyOxidizer standalone build. Neither the Linux Editor matrix nor the preserved
VS2015 discovery fixtures prove those remaining native acceptance boundaries.
