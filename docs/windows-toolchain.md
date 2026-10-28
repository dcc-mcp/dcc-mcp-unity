# Windows build toolchains

The Python sidecar and C# Editor bridge need no MSVC for ordinary use. Mono
player builds also remain independent of MSVC. Windows IL2CPP builds and Windows
PyOxidizer packaging require the project's own **msvc-kit v0.2.22**. There is no
silent fallback to an ambient compiler when configuration or diagnostics fail.

## Required native-toolchain selection

Provide all four variables before starting the Windows Editor or standalone
builder:

- `DCC_MCP_UNITY_MSVC_KIT`: absolute path to the official v0.2.22 Windows x64 CLI.
- `DCC_MCP_UNITY_MSVC_KIT_DIR`: explicit toolchain root; for legacy, the original
  VS2015 installation directory containing `VC`, never a relabeled modern tree.
- `DCC_MCP_UNITY_MSVC_VERSION`: complete modern version, for example
  `14.44.35207`, or exactly family `14.0` for identified VS2015.
- `DCC_MCP_UNITY_SDK_VERSION`: complete version, for example `10.0.22621.0`.

Optional selection settings:

- `DCC_MCP_UNITY_HOST_ARCH`: `x64` (default) or explicit `x86`; the target remains
  x64. The selected compiler and SDK tools must match that host.
- `DCC_MCP_UNITY_SDK_DIR`: an existing Windows Kits/10 root separate from the
  compiler root. Required for VS2015, optional for modern layouts. No ambient
  registry/environment SDK is substituted for an explicit root.

Legacy and separate-SDK selection use the approved released msvc-kit v0.2.22
contract. An older consumer release identity fails early with an upgrade
diagnostic; no unpublished executable or digest is used.

Both consumers verify the CLI's published SHA256 before executing it:
`184f07fb2896f0a7d27c45445990bd7d9e525b6ee58956cfe857bf0027848925`.
The approved binary is the 7.4 MB asset from the
[official v0.2.22 release](https://github.com/loonghao/msvc-kit/releases/tag/v0.2.22).
Updating this identity is an explicit consumer change, not a `latest` download.

The real command is:

```powershell
& $env:DCC_MCP_UNITY_MSVC_KIT doctor --format json --compile `
  --arch x64 --host-arch x64 `
  --dir $env:DCC_MCP_UNITY_MSVC_KIT_DIR `
  --msvc-version $env:DCC_MCP_UNITY_MSVC_VERSION `
  --sdk-version $env:DCC_MCP_UNITY_SDK_VERSION
```

Use the selected host in `--host-arch`; add `--sdk-dir` when configured.
Its `msvc-kit.doctor.v1` report must confirm the requested compiler version
(or explicit legacy family), SDK version, host and x64 target. C++ compile,
resource compile, link and manifest must each pass once. Native x64 host/target
also requires execution to pass. Only explicit x86-hosted x64 cross compilation
may report `probe.execute: skipped`; `consumer_probe_execution_verified` is then
false. Missing/duplicate stages or any other skipped build stage fail closed. Doctor has a 180-second consumer deadline. A missing CLI, wrong
hash, failed probe or mismatched selection blocks the native build and reports
the failure. Doctor never installs missing components.

## Reusing already installed Windows tools in CI

`.github/scripts/prepare-msvc-kit.ps1` is a PowerShell 7 CI preparation script.
It uses registered modern Visual Studio and Windows SDK installations, chooses
complete x64-target versions for the selected host (honoring explicit version pins), downloads only the
pinned CLI, and verifies its published digest. It exports the four required variables and selected host
through the current job's `GITHUB_ENV`.

The default modern portable layout has a single root containing `VC/Tools/MSVC` and `Windows Kits/10`.
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
- Mandatory msvc-kit configuration, supported version pins, root availability and CLI hash.

Only the final `ready_to_build` phase, immediately before BuildPlayer, runs:

- The real, hash-pinned msvc-kit doctor compile contract described above, once.
- The selected full modern MSVC version or verified VS2015 family, host-specific
  compiler/linker hashes, and selected SDK version/resource/manifest-tool hashes
  correspond to registered tools. Legacy compiler and explicit SDK roots must
  also match the doctor selection.

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

### VS2015 and early Windows 10 SDK selection

Legacy mode requires the original registered VS2015 root, its `VC/vcvarsall.bat`
and `VC/include/crtversion.h` identifying CRT major/minor 14/0, and an explicit
SDK root. It reports only the verified compiler family `14.0`. A made-up patch
pin such as `14.0.24215` is rejected. `version.msvc: skipped` records that the
compiler patch version is unverified; `compiler_version_kind` is `family`.

The x64 host selects `VC/bin/amd64`; the x86 host selects `VC/bin/x86_amd64`.
Both target `VC/lib/amd64`. Compiler/linker hashes must agree with that registered
host selection. SDK 10.0.10240.0, 10.0.10586.0 and 10.0.14393.0 may use
`bin/<host>` only when the entire `bin/<version>` directory is absent. A partial
versioned directory or modern SDK cannot fall back. Informational
`version.sdk_tools: skipped` does not waive the required native build stages.

The CI preparation script discovers modern registered installations only.
Legacy selection is explicit; it never copies or renames old installations into
modern layouts. The caller pins the published v0.2.22 release identity.
Directory/JSON fixtures are not
native VS2015 compilation evidence. Unity 2018 C#/.NET 4.x compatibility also
does not prove Unity 2018 + VS2015 native builds. A real supported legacy Windows
installation and Unity build remain required acceptance evidence.

## Windows standalone packaging

```powershell
python tools/build_binary.py --msvc-kit C:\tools\msvc-kit.exe `
  --msvc-kit-dir C:\tools\compiler `
  --msvc-version 14.44.35207 --sdk-version 10.0.22621.0
```

The standalone options `--host-arch` and `--sdk-dir` mirror the optional
environment settings above. Legacy selection uses `--msvc-version 14.0` with
`--msvc-kit-dir` pointing to the original VS2015 root and a required `--sdk-dir`.

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
