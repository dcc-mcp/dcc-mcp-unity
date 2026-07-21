# DCC-MCP Unity Editor package

This embedded UPM package connects Unity Editor to the loopback DCC-MCP bridge. It exposes only
the typed operations implemented in `DccMcpCommands`; it does not evaluate arbitrary C#.

The bridge URL defaults to `ws://127.0.0.1:3852` and can be overridden with the
`DCC_MCP_UNITY_BRIDGE_URL` environment variable before Unity starts.
