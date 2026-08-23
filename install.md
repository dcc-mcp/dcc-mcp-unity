# Install DCC-MCP Unity

This runbook is the contract for agent-driven installation on Windows, macOS, and Linux.
The installer plans by default and uses stable exit codes: `0` success, `10` preflight,
`20` acquire, `30` install, `40` verify, and `50` restart required.

## Requirements

- Unity Editor 2018.4.25f1 or newer (including supported Tuanjie `2022.3.*t*` releases)
- The .NET 4.x Equivalent scripting runtime when using Unity 2018.4
- Python 3.9 or newer with `dcc-mcp-core>=0.19.90,<1.0.0`
- A Unity project containing `Assets/` and `ProjectSettings/ProjectVersion.txt`

The installer supports Windows, macOS, and Linux. Use `--dcc-path` when Unity Hub is in a
nonstandard location and `--python` when the adapter belongs to a different interpreter.
Interpreter selection is `--python`, then `DCC_MCP_INSTALL_PYTHON`, then the interpreter running
the command.

## Supported versions

The minimum host version is Unity `2018.4.25f1`. The project version is checked before any write.
Unity alpha/beta releases below that exact floor are rejected. Tuanjie `t` releases are compared
at the same stability level as Unity final releases.

## Agent quick path

Install the wheel through the pinned DCC-MCP catalog, plan the project change, then execute it:

```bash
dcc-mcp-cli install --dcc-type unity --execute
dcc-mcp-unity install --project /path/to/UnityProject --json --dry-run
dcc-mcp-unity install --project /path/to/UnityProject --json --yes
```

The JSON schema contains `schema_version`, status and versions, selected host/interpreter,
`steps[]`, executable `next_steps[]`, `receipt_path`, and the verify-to-usable verdict. A completed
copy can return `50` until Unity has reloaded the package; execute the returned `next_steps[]` and
run `verify` again.

## Manual path

```bash
python -m pip install dcc-mcp-unity
dcc-mcp-unity install --project /path/to/UnityProject --yes
```

The package is staged and swapped into `Packages/com.dcc-mcp.unity`; an existing package is kept
as a rollback candidate until the new package and receipt are durable. The receipt is written to
`.dcc-mcp/receipts/unity.json` and records file digests, versions, interpreter, and touched paths.
Re-running the command converges without rewriting an already-current install.

The legacy command remains available during migration:

```bash
dcc-mcp-unity-install /path/to/UnityProject
```

Open or restart the project after installation. If `DCC_MCP_UNITY_SIDECAR_PATH` is not configured,
start `dcc-mcp-unity` in a terminal.

## Verify

```bash
dcc-mcp-unity status --project /path/to/UnityProject --json
dcc-mcp-unity verify --project /path/to/UnityProject --json
```

`verify` checks the installed files against the receipt, imports the adapter in the selected
Python interpreter, and probes a registered Unity sidecar. Only a successful live probe produces
`directly_usable: true`; copied files alone are not reported as usable.

## Upgrade

```bash
python -m pip install --upgrade dcc-mcp-unity
dcc-mcp-unity upgrade --project /path/to/UnityProject --json --dry-run
dcc-mcp-unity upgrade --project /path/to/UnityProject --json --yes
```

The upgrade uses the same staged transaction and restores both the previous package and previous
receipt when committing the replacement fails. Close Unity and retry if the command returns `50`.

## Uninstall

```bash
dcc-mcp-unity uninstall --project /path/to/UnityProject --json --dry-run
dcc-mcp-unity uninstall --project /path/to/UnityProject --json --yes
python -m pip uninstall dcc-mcp-unity
```

Uninstall consumes the receipt and removes only its exact package path. It is idempotent when both
the package and receipt are absent, and refuses to delete an unreceipted directory. After stopping
the sidecar, remove stale Unity adapter registrations with `dcc-mcp-cli unregister --dcc-type unity`
if your Core version exposes that command. Stale standalone pid files use the project hash and live
under the operating-system temporary directory; a dead-owner file is reclaimed on the next start.

## Troubleshooting

- **Exit `10`, not a Unity project or unsupported version:** pass the project root, not `Assets/`,
  and inspect `ProjectSettings/ProjectVersion.txt`.
- **Exit `40`, artifact/import failure:** run `status --json`; use `upgrade --yes` to repair a
  partial or modified package, and ensure `--python` names the interpreter containing the adapter.
- **Exit `40`, readiness failure:** open the project, wait for compilation, start the sidecar, and
  inspect `dcc-mcp-cli list` before rerunning `verify`.
- **Exit `50`, restart required:** close every Unity process using the project and repeat the exact
  command. The installer does not delete a loaded tree.
- **Bridge connection failure:** keep `DCC_MCP_UNITY_BRIDGE_PORT` and
  `DCC_MCP_UNITY_BRIDGE_URL` consistent. The default is `ws://127.0.0.1:3852`.
- **Editor bootstrap failure:** inspect `Library/DccMcp/bootstrap-errors.jsonl`. Startup hooks append
  structured stage, exception type, message, and timestamp records there.
- **Concurrent Editors:** use one adapter and a unique bridge port/URL pair per Editor process.

Optional runtime controls are `DCC_MCP_UNITY_PORT`, `DCC_MCP_UNITY_BRIDGE_TIMEOUT`, and
`DCC_MCP_UNITY_ALLOW_SOURCE_WRITES=1`. Tool arguments cannot enable source writes.
