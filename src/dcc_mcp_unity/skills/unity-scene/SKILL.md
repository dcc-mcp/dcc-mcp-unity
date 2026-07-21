---
name: unity-scene
description: >-
  Domain skill — Inspect and edit the active Unity scene with typed, undoable
  GameObject operations. Use for hierarchy, object creation, transforms, and
  scene saves. Not for asset imports — use unity-project.
license: MIT
compatibility: "Unity 2021.3+; dcc-mcp-core 0.19.45+"
allowed-tools: "python"
metadata:
  dcc-mcp:
    dcc: unity
    layer: domain
    version: "0.2.0"  # x-release-please-version
    search-hint: "Unity scene hierarchy GameObject transform save Undo"
    tags: "unity,scene,gameobject,transform,game-development"
    tools: tools.yaml
    depends: "dcc-diagnostics"
---

# Unity Scene

Inspect the hierarchy immediately before using instance IDs; Unity instance IDs are scoped to the
current Editor session. Object creation and transform edits register with Unity Undo.

Do not automatically retry a timed-out mutation. Inspect the project and scene first because the
Editor may have completed the original request near the timeout boundary.
