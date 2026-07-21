---
name: unity-diagnostics
description: >-
  Domain skill — Read a bounded snapshot of Unity Console messages captured
  after the Editor bridge loads. Use to verify operations and diagnose compile,
  import, or runtime errors. Not for clearing logs or arbitrary Editor access.
license: MIT
compatibility: "Unity 2018.4.25f1+ (.NET 4.x); dcc-mcp-core 0.19.45+"
allowed-tools: "python"
metadata:
  dcc-mcp:
    dcc: unity
    layer: domain
    version: "0.4.1"  # x-release-please-version
    search-hint: "Unity Console logs errors warnings stack trace diagnostics"
    tags: "unity,console,logs,diagnostics,game-development"
    tools: tools.yaml
    depends: "dcc-diagnostics"
---

# Unity Diagnostics

Read the captured Unity Console after an operation or when the Editor reports a failure. Results
are bounded and may be truncated; increase `limit` up to 200 or narrow by severity when needed.
