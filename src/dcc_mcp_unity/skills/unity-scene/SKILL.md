---
name: unity-scene
description: >-
  Domain skill — Inspect and edit the active Unity scene with typed, undoable
  GameObject operations. Use for hierarchy, object creation, transforms, and
  scene saves. Not for asset imports — use unity-project.
license: MIT
compatibility: "Unity 2018.4.25f1+ (.NET 4.x); dcc-mcp-core 0.19.45+"
allowed-tools: "python"
metadata:
  dcc-mcp:
    dcc: unity
    layer: domain
    version: "0.4.0"  # x-release-please-version
    search-hint: "Unity scene hierarchy GameObject transform save Undo"
    tags: "unity,scene,gameobject,transform,game-development"
    tools: tools.yaml
    depends: "dcc-diagnostics"
---

# Unity Scene

Inspect the hierarchy immediately before using instance IDs. Treat every Unity instance ID as an
opaque, session-scoped value and return it unchanged; Unity 6000.5+ emits decimal strings while
older Editors retain integer output for compatibility. Both forms are accepted as input. Object
creation and transform edits register with Unity Undo.

Do not automatically retry a timed-out mutation. Inspect the project and scene first because the
Editor may have completed the original request near the timeout boundary.
