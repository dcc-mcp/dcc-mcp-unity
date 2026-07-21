---
name: unity-project
description: >-
  Domain skill — Inspect the open Unity project and refresh its Asset Database.
  Use for project identity, Unity version, Editor state, active scene, build
  scenes, and asset import refresh. Not for GameObject edits — use unity-scene.
license: MIT
compatibility: "Unity 2018.4.25f1+ (.NET 4.x); dcc-mcp-core 0.19.45+"
allowed-tools: "python"
metadata:
  dcc-mcp:
    dcc: unity
    layer: domain
    version: "0.4.1"  # x-release-please-version
    search-hint: "Unity project version build scenes Asset Database refresh"
    tags: "unity,project,assets,game-development"
    tools: tools.yaml
    depends: "dcc-diagnostics"
---

# Unity Project

Inspect before assuming the active project, scene, Unity version, or Play/compile state. Asset
refresh is an explicit mutation because it may import changed files and update generated metadata.

Stop if the returned project path is not the intended target. Do not automatically retry a timed-out
refresh; inspect the project again first.
