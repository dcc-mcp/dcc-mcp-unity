using System;
using UnityEditor;
using UnityEngine;

namespace DccMcp.Unity
{
    internal static class DccMcpMenu
    {
        private const string AdapterVersion = "0.8.1"; // x-release-please-version
        private const string ProjectUrl = "https://github.com/dcc-mcp/dcc-mcp-unity";
        private const string DefaultBridgeUrl = "ws://127.0.0.1:3852";

        [MenuItem("DCC MCP/Copy Instance ID", false, 0)]
        private static void CopyInstanceId()
        {
            var instanceId = DccMcpBridge.GetSessionInstanceId();
            GUIUtility.systemCopyBuffer = instanceId;
            Debug.Log("DCC MCP: Instance ID copied to clipboard: " + instanceId);
        }

        [MenuItem("DCC MCP/Server Info", false, 1)]
        private static void ShowServerInfo()
        {
            var instanceId = DccMcpBridge.GetSessionInstanceId();
            var bridgeUrl = Environment.GetEnvironmentVariable("DCC_MCP_UNITY_BRIDGE_URL");
            if (string.IsNullOrEmpty(bridgeUrl))
            {
                bridgeUrl = DefaultBridgeUrl;
            }

            var info = string.Join("\n", new[]
            {
                "Instance UUID: " + instanceId,
                "Unity: " + Application.unityVersion,
                "Project: " + Application.productName,
                "PID: " + System.Diagnostics.Process.GetCurrentProcess().Id,
                "Bridge URL: " + bridgeUrl,
                "Adapter: dcc-mcp-unity " + AdapterVersion,
            });

            EditorUtility.DisplayDialog("DCC MCP — Server Info", info, "OK");
        }

        [MenuItem("DCC MCP/About DCC MCP", false, 12)]
        private static void ShowAbout()
        {
            var about = string.Join("\n", new[]
            {
                "dcc-mcp-unity v" + AdapterVersion,
                "Unity " + Application.unityVersion,
                "",
                "DCC MCP — AI-driven DCC automation.",
                ProjectUrl,
            });

            EditorUtility.DisplayDialog("About DCC MCP", about, "OK");
        }
    }
}
