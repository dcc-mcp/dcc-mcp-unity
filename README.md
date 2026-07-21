# dcc-mcp-unity

![DCC-MCP Unity lockup](https://raw.githubusercontent.com/dcc-mcp/dcc-mcp-unity/main/docs/assets/dcc-mcp-unity.svg)

Unity Editor adapter for the DCC Model Context Protocol ecosystem. It ships a UPM Editor package,
a loopback WebSocket bridge, and typed project and scene tools.

The supported Editor range starts at Unity 2018.4.36f1 with the .NET 4.x Equivalent scripting
runtime. CI pins the final Unity 2018 LTS patch, the 2021.3 baseline, and the current stable Unity 6
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

## Agent workflow

1. Load `unity-project` and call `inspect_project` before assuming project or editor state. Stop if
   the returned project is not the intended target or the Editor is compiling, updating, entering
   Play Mode, or playing.
2. Load `unity-scene` and call `inspect_scene` immediately before using an instance ID.
3. Create GameObjects or change transforms through typed operations backed by Unity Undo.
4. Verify the hierarchy, then explicitly call `save_scene`.
5. Load `unity-diagnostics` and call `read_console` after failures or as a final verification step.

Do not automatically retry a timed-out scene mutation. Inspect the project and scene first because
the Editor may have completed the original request near the timeout boundary.

No raw C# evaluation, shell command, or arbitrary filesystem write is exposed. The Editor bridge
accepts only the methods implemented in `DccMcpCommands` and executes them on Unity's editor update
loop. Mutations fail closed during compilation, asset updates, and Play Mode. Requests, queued work,
scene snapshots, Console reads, and serialized responses have explicit size or lifetime budgets.

## Validation boundary

Public CI validates Python 3.9 and 3.12 on Windows, macOS, and Linux; validates the bundled skill
contracts; performs static checks for the UPM package and main-thread/Undo contracts; and builds the
PyPI artifacts. Trusted pull requests, `main`, and the weekly schedule also compile the UPM package
and run its command, scene, Undo, and validation tests through GameCI in Unity 2018.4.36f1,
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
