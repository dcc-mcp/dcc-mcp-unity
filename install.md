# Install DCC-MCP Unity

## Requirements

- Unity Editor 2021.3 or newer
- Python 3.9 or newer
- A Unity project with `Assets/` and `ProjectSettings/` directories

## Install the adapter and Editor package

```bash
pip install dcc-mcp-unity
dcc-mcp-unity-install /path/to/UnityProject
```

The installer reads `ProjectSettings/ProjectVersion.txt` and rejects projects older than Unity
2021.3 before changing `Packages/`.

Open or restart the Unity project and wait for Package Manager to compile **DCC-MCP Unity**.
Start the Python adapter in a separate terminal:

```bash
dcc-mcp-unity
```

After the Unity Console reports a bridge connection, verify the registered instance:

```bash
dcc-mcp-cli list
```

## Upgrade

Upgrade the Python package, then replace the embedded Unity package explicitly:

```bash
pip install --upgrade dcc-mcp-unity
dcc-mcp-unity-install /path/to/UnityProject --overwrite
```

Restart Unity so it reloads the package assemblies.

## Optional endpoints

- `DCC_MCP_UNITY_PORT` fixes the adapter HTTP port; otherwise a free loopback port is selected.
- `DCC_MCP_UNITY_BRIDGE_PORT` changes the Python WebSocket listener from port `3852`.
- `DCC_MCP_UNITY_BRIDGE_URL` changes the URL used by the Unity Editor package.
- `DCC_MCP_UNITY_BRIDGE_TIMEOUT` may increase, but not lower, the 60-second RPC timeout.

When changing the bridge port, set both bridge variables before starting the adapter and Unity.
The default endpoint is single-Editor; concurrent Editors require one adapter and a unique bridge
port/URL pair per Editor process.
