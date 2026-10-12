---
name: unity-diagnostics
description: >-
  Domain skill — Probe Unity main-thread readiness, inspect persistent jobs,
  read bounded Console messages, and capture a Play-Mode Game View PNG. Use for
  install, compile, build, and gameplay verification. Not for clearing logs or
  arbitrary Editor access.
license: MIT
compatibility: "Unity 2018.4.25f1+ (.NET 4.x); dcc-mcp-core 0.19.90+"
allowed-tools: "python"
metadata:
  dcc-mcp:
    dcc: unity
    layer: domain
    version: "0.14.0"  # x-release-please-version
    search-hint: "Unity ping readiness job status Console logs errors Game View screenshot PNG diagnostics"
    tags: "unity,console,logs,diagnostics,game-development"
    tools: tools.yaml
    depends: "dcc-diagnostics"
---

# Unity Diagnostics

`inspect_dirty_assets` reads the already loaded persistent dirty set once on the Editor
main thread. It returns every object or fails without an object prefix at the 768-KiB
result budget. It never loads assets, refreshes, saves, discards, or changes selection.
`complete` covers snapshot membership, not ownership or saveability. Read exact names,
paths, hide flags, main/subasset flags, GUID/64-bit local IDs and GlobalObjectIds;
unknown/default identifiers and field errors remain explicit. Local IDs are decimal
strings so browser clients preserve all 64 bits. Owner/session and assembly MVID belong
to the same capture; map the MVID to separately verified source/build receipts.
Editors without the public `EditorUtility.IsDirty(Object)` API (including Unity
2018.4) return `dirty_query_unsupported` with a null dirty count, not zero or clean.
A new capture describes its current set, not a historical count.

Call `ping` when install verification or recovery needs fresh proof that the Editor update loop can
execute work. A connected WebSocket alone is not sufficient readiness evidence.

Read the captured Unity Console after an operation or when the Editor reports a failure. Results
are bounded and may be truncated; increase `limit` up to 200 or narrow by severity when needed.

Persistent operations keep their Core async job open until Unity reports `succeeded` or `failed`;
wait on that job instead of replacing an ambiguous request with a new UUID. `inspect_job` remains
available for reconnect and audit recovery. `capture_game_view` is accepted only in active,
unpaused Play Mode, focuses Game View, waits a rendered frame, and succeeds only after Unity
decodes a bounded nonzero PNG in its fixed output directory.
