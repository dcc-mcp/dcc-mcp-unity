---
name: unity-project
description: >-
  Domain skill — Inspect and compile an open Unity project, read or safely
  upsert bounded source assets, run typed Unity tests, change Play Mode, and build a Windows player.
  Not for GameObject edits — use unity-scene.
license: MIT
compatibility: "Unity 2018.4.25f1+ (.NET 4.x); dcc-mcp-core 0.19.45+"
allowed-tools: "python"
metadata:
  dcc-mcp:
    dcc: unity
    layer: domain
    version: "0.6.3"  # x-release-please-version
    search-hint: "Unity project source script CAS compile Play Mode Windows player build"
    tags: "unity,project,assets,game-development"
    tools: tools.yaml
    depends: "dcc-diagnostics"
---

# Unity Project

Inspect before assuming the active project, scene, Unity version, or Play/compile state. Source
writes require the operator-owned environment gate, an `Assets/...` allowlisted text path, bounded
UTF-8 content, and either `expected_sha256: absent` for creation or the digest returned by
`read_text_asset` for replacement.

Every long or domain-reloading mutation returns a persistent job snapshot. Reuse the same UUID and
poll `unity_diagnostics__inspect_job`; never retry an ambiguous result with a new request ID. Build
uses only enabled scenes and writes a new request directory below `Builds/DccMcp`.

`run_tests` invokes the installed Unity Test Framework through an exact typed contract. Use
`edit_mode` or `play_mode` and optional exact fully-qualified test or fixture names; an empty list
runs the selected mode. Poll the returned job and treat `result.outcome`, counts, report SHA-256,
and request-scoped NUnit XML as the test evidence. The tool never launches another Unity process.
