---
name: unity-diagnostics
description: >-
  Domain skill — Inspect persistent Unity jobs, read bounded Console messages,
  and capture a Play-Mode Game View PNG. Use for compile, build, and gameplay
  verification. Not for clearing logs or arbitrary Editor access.
license: MIT
compatibility: "Unity 2018.4.25f1+ (.NET 4.x); dcc-mcp-core 0.19.45+"
allowed-tools: "python"
metadata:
  dcc-mcp:
    dcc: unity
    layer: domain
    version: "0.6.1"  # x-release-please-version
    search-hint: "Unity job status Console logs errors Game View screenshot PNG diagnostics"
    tags: "unity,console,logs,diagnostics,game-development"
    tools: tools.yaml
    depends: "dcc-diagnostics"
---

# Unity Diagnostics

Read the captured Unity Console after an operation or when the Editor reports a failure. Results
are bounded and may be truncated; increase `limit` up to 200 or narrow by severity when needed.

Poll `inspect_job` with the exact UUID used to submit a mutation. Treat `failed` as terminal and do
not replace an ambiguous queued/running request with a new ID. `capture_game_view` is accepted only
in active, unpaused Play Mode, focuses Game View, waits a rendered frame, and succeeds only after
Unity decodes a bounded nonzero PNG in its fixed output directory.
