---
name: unity-project
description: >-
  Domain skill — Inspect and compile an open Unity project, read or safely
  upsert bounded source assets, configure generated PNG sprites, run typed Unity
  tests, change Play Mode, and build Windows or Android players. Not for GameObject edits
  — use unity-scene.
license: MIT
compatibility: "Unity 2018.4.25f1+ (.NET 4.x); dcc-mcp-core 0.19.49+"
allowed-tools: "python"
metadata:
  dcc-mcp:
    dcc: unity
    layer: domain
    version: "0.12.0"  # x-release-please-version
    search-hint: "Unity project source script sprite PNG TextureImporter CAS compile Play Mode Windows Android APK AAB player build"
    tags: "unity,project,assets,game-development"
    tools: tools.yaml
    depends: "dcc-diagnostics"
---

# Unity Project

Inspect before assuming the active project, scene, Unity version, or Play/compile state.
`configure_sprite_importer` imports one existing `Assets/.../*.png` as a single Sprite through
Unity's native `TextureImporter`; choose point or bilinear filtering and an explicit pixels-per-unit
value. Source writes require the operator-owned environment gate, an `Assets/...` allowlisted text
path, bounded UTF-8 content, and either `expected_sha256: absent` for creation or the digest returned
by `read_text_asset` for replacement.

Every long or domain-reloading mutation keeps its Core async job open until Unity reports a
terminal persistent state. Reuse the same UUID and wait on that job; use
`unity_diagnostics__inspect_job` for reconnect or audit recovery, and never replace an ambiguous
request with a new ID. Build uses only enabled scenes and writes a new request directory below
`Builds/DccMcp`.

`build_android_player` accepts only `apk` or `aab`. It uses the project's saved Player Settings,
never accepts signing secrets, requires custom project signing for AAB delivery, restores the
temporary app-bundle toggle, and returns the BuildReport outcome plus artifact size and SHA-256.

`run_tests` invokes the installed Unity Test Framework through an exact typed contract. Use
`edit_mode` or `play_mode` and optional exact fully-qualified test or fixture names; an empty list
runs the selected mode. Wait for the returned Core job and treat `result.outcome`, counts, report
SHA-256, and request-scoped NUnit XML as the test evidence. The tool never launches another Unity
process.
