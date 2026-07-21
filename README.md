# dcc-mcp-unity

![DCC-MCP Unity lockup](https://raw.githubusercontent.com/dcc-mcp/dcc-mcp-unity/main/docs/assets/dcc-mcp-unity.svg)

Unity Editor adapter for the DCC Model Context Protocol ecosystem. It ships a UPM Editor package,
a loopback WebSocket bridge, and typed project, scene, build, and diagnostic tools.

The supported Editor range starts at Unity 2018.4.25f1 with the .NET 4.x Equivalent scripting
runtime. CI pins that Unity 2018 baseline, the 2021.3 baseline, and the current stable Unity 6
release instead of using a drifting `latest` tag.

The first-release boundary and comparison with `unity-cli` and two established Unity MCP projects
are documented in the [architecture benchmark](https://github.com/dcc-mcp/dcc-mcp-unity/blob/main/docs/architecture-benchmark.md).

## Install

```bash
pip install dcc-mcp-unity
dcc-mcp-unity-install /path/to/UnityProject
```

Open or restart the Unity project so Package Manager compiles **DCC-MCP Unity**, then run:

```bash
dcc-mcp-unity
```

See the [installation guide](https://github.com/dcc-mcp/dcc-mcp-unity/blob/main/install.md) for
upgrade, environment, and connection verification details.

The MCP endpoint uses a free loopback port and is registered for gateway discovery. Set
`DCC_MCP_UNITY_PORT=8765` before starting the server only when a fixed direct endpoint is needed.
The Editor package reconnects to the loopback bridge at `ws://127.0.0.1:3852`; set
`DCC_MCP_UNITY_BRIDGE_PORT` before the MCP server and `DCC_MCP_UNITY_BRIDGE_URL` before Unity starts
to override it. `DCC_MCP_UNITY_BRIDGE_TIMEOUT` may increase the 60-second RPC timeout but cannot
lower it; queued Editor work expires first so timed-out mutations are not executed later.

The default bridge targets one Unity Editor. For concurrent Editors, run one adapter per Editor and
assign each pair a unique bridge port and URL before starting either process.

Source writes are disabled by default. An operator may set
`DCC_MCP_UNITY_ALLOW_SOURCE_WRITES=1` before starting Unity to enable the bounded
`upsert_text_asset` tool. It accepts only allowlisted UTF-8 text extensions below `Assets`, caps
encoded content at 256 KiB, rejects JSON-unsafe control characters, requires compare-and-swap state,
rejects reparse points, and replaces files atomically. The adapter exposes no delete, arbitrary path,
shell, or code-evaluation tool.

The per-asset lock is cooperative: it serializes DCC-MCP writers but cannot stop another same-user
process from ignoring the lock or swapping a junction or symlink after path validation and before the
operating system resolves a write. That same-user race is outside this in-process boundary. If an
external replacement conflict is detected, the adapter performs no automatic rollback, preserves the
displaced bytes in a unique `.dccmcp-*.backup` conflict backup, and does not write the target again
after detection. A failed conflict job does not prove the target stayed unchanged; inspect both the
target and backup before submitting another write.

## Standalone sidecar

Hosts without an embedded Python runtime can use the PyOxidizer sidecar released with each version.
The release asset contains the executable, its adjacent `lib/` runtime, and `SHA256SUMS`. Configure
the Unity package to start it on editor load by setting `DCC_MCP_UNITY_SIDECAR_PATH`; optionally set
`DCC_MCP_UNITY_SIDECAR_SHA256` to the published digest. The launcher is loopback-only, passes the
Unity process id to `--watch-pid`, and uses a per-project pid file to avoid duplicate sidecars after
assembly reloads.

For local development, run `dcc-mcp-unity-standalone --bridge-port 3852 --watch-pid <unity-pid>`.

## Agent workflow

1. Load `unity-project` and call `inspect_project` before assuming project or editor state. Stop if
   the returned project is not the intended target or the Editor is compiling, updating, entering
   Play Mode, or playing.
2. Read an existing source with `read_text_asset`, then pass its SHA-256 to `upsert_text_asset`;
   use `expected_sha256: absent` only for creation. Keep the UUID `request_id` and poll
   `inspect_job` until it reports `succeeded` or `failed`.
3. Call `refresh_and_compile`, poll its job, and inspect `read_console` before entering Play Mode.
4. Load `unity-scene` and call `inspect_scene` immediately before using an instance ID. Treat IDs
   as opaque values and return them unchanged. Unity 6000.5+ emits decimal strings; older Editors
   retain integer output, and both forms are accepted as input.
5. Create GameObjects or change transforms through typed operations backed by Unity Undo, verify
   the hierarchy, then explicitly call `save_scene`.
6. Use `set_play_mode` before `capture_game_view`; capture requires active, unpaused Play Mode,
   focuses Game View, waits a rendered frame, and succeeds only after Unity decodes a nonzero PNG
   below `Builds/DccMcp/Captures`. Captures are limited to 32 MiB, 8192 pixels per axis, and
   32M pixels total.
7. `build_windows_player` persists an active-target switch when needed, rejects dirty enabled scenes,
   and builds exactly the saved Build Settings scenes to a new UUID directory below `Builds/DccMcp`.
   Poll the job and launch the reported executable as a separate acceptance gate.

Do not replace a timed-out job with a new UUID. Reconnect and inspect the original `request_id`;
Unity persists queued/running/succeeded/failed state across domain reloads and rejects reuse with
different parameters.

Unity 2018 reloads Editor assemblies during normal Play Mode transitions, so the loopback socket
will disconnect and reconnect. `set_play_mode` persists its waiting state before requesting the
transition; a bridge disconnect is not completion or failure. After reconnection, inspect the same
`request_id` for the observed terminal state.

No raw C# evaluation, shell command, or arbitrary filesystem write is exposed. The Editor bridge
accepts only the methods implemented in `DccMcpCommands` and executes them on Unity's editor update
loop. One persistent mutating job runs at a time. Mutations fail closed in incompatible Editor
states. Requests, queued work, source text, scene snapshots, Console reads, and serialized responses
have explicit size or lifetime budgets.

## Validation boundary

Public CI validates Python 3.9 and 3.12 on Windows, macOS, and Linux; validates the bundled skill
contracts; performs static checks for the UPM package and main-thread/Undo contracts; and builds the
PyPI artifacts. Trusted pull requests, `main`, and the weekly schedule also compile the UPM package
and run its command, scene, Undo, and validation tests through GameCI in Unity 2018.4.25f1,
2021.3.45f1, and 6000.5.4f1. Fork pull requests skip the licensed Editor jobs because GitHub does
not expose repository secrets to forks. Licensed runs share one repository-wide queue across pull
requests, `main`, releases, and schedules so a Personal seat is never activated concurrently. Each
Editor also completes a real WebSocket `hello` → `project.inspect` → response smoke against the
Python sidecar and records the reported Editor version as an artifact.

## Development

```bash
uv sync --extra dev
uv run python -m pytest
uv run ruff check src tests tools
uv run ruff format --check src tests tools
uv run python tools/lint_skills.py
uv run python -m build
uv run python -m twine check dist/*
```

Unity and the Unity cube logo are trademarks of Unity Technologies. This independent adapter is
not affiliated with or endorsed by Unity Technologies.
