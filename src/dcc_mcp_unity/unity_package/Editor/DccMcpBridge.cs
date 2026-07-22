using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace DccMcp.Unity
{
    [InitializeOnLoad]
    internal static class DccMcpBridge
    {
        internal const int MaxInboundMessageBytes = 1024 * 1024;
        internal const int MaxOutboundMessageBytes = 900 * 1024;
        internal const int MaxEscapedTextEnvelopeBytes =
            DccMcpJobs.MaxTextAssetBytes * 3 + 64 * 1024;
        private const int MaxPendingRequests = 256;
        private const int MaxPendingLogs = 64;
        private static readonly TimeSpan RequestQueueLifetime = TimeSpan.FromSeconds(55);
        private static readonly DateTime UnixEpochUtc =
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly string UnityVersion = Application.unityVersion;
        private static readonly string ProjectPath =
            Path.GetDirectoryName(Application.dataPath) ?? string.Empty;
        private static readonly string ProjectName = Application.productName;
        private static readonly string ProjectPathHash = HashProjectPath(ProjectPath);
        private static readonly string SessionInstanceId = GetSessionInstanceId();
        private static readonly int ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;

        private sealed class WorkItem
        {
            internal readonly ClientWebSocket Socket;
            internal readonly JObject Request;
            internal readonly DateTime ExpiresAtUtc;

            internal WorkItem(ClientWebSocket socket, JObject request)
            {
                Socket = socket;
                Request = request;
                ExpiresAtUtc = DateTime.UtcNow.Add(RequestQueueLifetime);
                var parameters = request["params"] as JObject;
                var deadline = parameters?["_dcc_mcp_deadline_unix_ms"];
                parameters?.Remove("_dcc_mcp_deadline_unix_ms");
                if (deadline == null)
                {
                    return;
                }
                if (deadline.Type != JTokenType.Integer)
                {
                    ExpiresAtUtc = DateTime.MinValue;
                    return;
                }
                try
                {
                    var requestedDeadline = UnixEpochUtc.AddMilliseconds((long)deadline);
                    if (requestedDeadline < ExpiresAtUtc)
                    {
                        ExpiresAtUtc = requestedDeadline;
                    }
                }
                catch (ArgumentOutOfRangeException)
                {
                    ExpiresAtUtc = DateTime.MinValue;
                }
                catch (OverflowException)
                {
                    ExpiresAtUtc = DateTime.MinValue;
                }
            }
        }

        private sealed class LogItem
        {
            internal readonly string Message;
            internal readonly bool IsWarning;

            internal LogItem(string message, bool isWarning)
            {
                Message = message;
                IsWarning = isWarning;
            }
        }

        private static readonly ConcurrentQueue<WorkItem> Pending = new ConcurrentQueue<WorkItem>();
        private static readonly ConcurrentQueue<LogItem> PendingLogs = new ConcurrentQueue<LogItem>();
        private static readonly CancellationTokenSource Lifetime = new CancellationTokenSource();
        private static readonly SemaphoreSlim SendGate = new SemaphoreSlim(1, 1);
        private static int PendingCount;
        private static int PendingLogCount;

        static DccMcpBridge()
        {
            if (IsImportWorkerOrBatchMode())
            {
                return;
            }
            if (MaxEscapedTextEnvelopeBytes > Math.Min(
                MaxInboundMessageBytes,
                MaxOutboundMessageBytes))
            {
                throw new InvalidOperationException(
                    "Unity text asset and bridge message limits are inconsistent.");
            }
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.quitting += Stop;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            DccMcpSidecarLauncher.StartIfConfigured();
            _ = RunAsync(Lifetime.Token);
        }

        internal static bool IsImportWorkerOrBatchMode()
        {
#if UNITY_2020_2_OR_NEWER
            return AssetDatabase.IsAssetImportWorkerProcess();
#else
            return Application.isBatchMode;
#endif
        }

        private static void Stop()
        {
            if (!Lifetime.IsCancellationRequested)
            {
                EditorApplication.update -= OnEditorUpdate;
                EditorApplication.quitting -= Stop;
                AssemblyReloadEvents.beforeAssemblyReload -= Stop;
                Lifetime.Cancel();
                while (Pending.TryDequeue(out _))
                {
                }
                while (PendingLogs.TryDequeue(out _))
                {
                }
                Interlocked.Exchange(ref PendingCount, 0);
                Interlocked.Exchange(ref PendingLogCount, 0);
            }
        }

        private static async Task RunAsync(CancellationToken cancellationToken)
        {
            var reconnectDelayMs = 1000;
            while (!cancellationToken.IsCancellationRequested)
            {
                using (var socket = new ClientWebSocket())
                {
                    try
                    {
                        var configured = Environment.GetEnvironmentVariable("DCC_MCP_UNITY_BRIDGE_URL");
                        var url = string.IsNullOrWhiteSpace(configured)
                            ? "ws://127.0.0.1:3852"
                            : configured;
                        await socket.ConnectAsync(new Uri(url), cancellationToken)
                            .ConfigureAwait(false);
                        if (reconnectDelayMs > 1000)
                        {
                            QueueLog("DCC-MCP Unity bridge reconnected.");
                        }
                        else
                        {
                            QueueLog("DCC-MCP Unity bridge connected.");
                        }
                        reconnectDelayMs = 1000;
                        await SendAsync(socket, new JObject
                        {
                            ["type"] = "hello",
                            ["client"] = "unity",
                            ["version"] = UnityVersion,
                            ["project_name"] = ProjectName,
                            ["project_path"] = ProjectPath,
                            ["project_path_hash"] = ProjectPathHash,
                            ["session_instance_id"] = SessionInstanceId,
                            ["process_id"] = ProcessId,
                        }, cancellationToken).ConfigureAwait(false);
                        await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        if (reconnectDelayMs == 1000)
                        {
                            QueueLog(
                                "DCC-MCP Unity bridge disconnected; retrying in the background: "
                                + exception.Message,
                                true);
                        }
                    }
                }

                try
                {
                    await Task.Delay(reconnectDelayMs, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                reconnectDelayMs = Math.Min(reconnectDelayMs * 2, 30000);
            }
        }

        private static async Task ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using (var stream = new MemoryStream())
                {
                    WebSocketReceiveResult received;
                    do
                    {
                        received = await socket.ReceiveAsync(
                                new ArraySegment<byte>(buffer), cancellationToken)
                            .ConfigureAwait(false);
                        if (received.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }
                        if (stream.Length + received.Count > MaxInboundMessageBytes)
                        {
                            await socket.CloseAsync(
                                    WebSocketCloseStatus.MessageTooBig,
                                    "DCC-MCP message exceeds 1 MiB.",
                                    cancellationToken)
                                .ConfigureAwait(false);
                            return;
                        }
                        stream.Write(buffer, 0, received.Count);
                    }
                    while (!received.EndOfMessage);

                    if (received.MessageType != WebSocketMessageType.Text)
                    {
                        continue;
                    }

                    var message = JObject.Parse(Encoding.UTF8.GetString(stream.ToArray()));
                    if (string.Equals((string)message["type"], "request", StringComparison.Ordinal))
                    {
                        if (Interlocked.Increment(ref PendingCount) > MaxPendingRequests)
                        {
                            Interlocked.Decrement(ref PendingCount);
                            await SendSafelyAsync(
                                    socket,
                                    ErrorResponse(message["id"], -32001, "Unity request queue is full."),
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            Pending.Enqueue(new WorkItem(socket, message));
                        }
                    }
                }
            }
        }

        private static void OnEditorUpdate()
        {
            var logged = 0;
            while (logged < 16 && PendingLogs.TryDequeue(out var log))
            {
                logged++;
                Interlocked.Decrement(ref PendingLogCount);
                if (log.IsWarning)
                {
                    Debug.LogWarning(log.Message);
                }
                else
                {
                    Debug.Log(log.Message);
                }
            }

            var processed = 0;
            while (processed < 16 && Pending.TryDequeue(out var item))
            {
                processed++;
                Interlocked.Decrement(ref PendingCount);
                if (item.Socket.State != WebSocketState.Open)
                {
                    continue;
                }

                var id = item.Request["id"];
                if (DateTime.UtcNow > item.ExpiresAtUtc)
                {
                    _ = SendSafelyAsync(
                        item.Socket,
                        ErrorResponse(id, -32002, "Unity request expired before main-thread execution."),
                        Lifetime.Token);
                    continue;
                }

                JObject response;
                try
                {
                    var method = (string)item.Request["method"] ?? string.Empty;
                    var parameters = item.Request["params"] as JObject ?? new JObject();
                    response = new JObject
                    {
                        ["type"] = "response",
                        ["id"] = id,
                        ["result"] = DccMcpCommands.Execute(method, parameters),
                    };
                }
                catch (Exception exception)
                {
                    response = ErrorResponse(id, -32000, exception.Message);
                }
                _ = SendSafelyAsync(item.Socket, response, Lifetime.Token);
            }
        }

        private static JObject ErrorResponse(JToken id, int code, string message)
        {
            return new JObject
            {
                ["type"] = "response",
                ["id"] = id,
                ["error"] = new JObject
                {
                    ["code"] = code,
                    ["message"] = message,
                },
            };
        }

        private static string HashProjectPath(string projectPath)
        {
            var normalized = Path.GetFullPath(projectPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace('\\', '/');
            if (Path.DirectorySeparatorChar == '\\')
            {
                normalized = normalized.ToLowerInvariant();
            }

            using (var sha256 = SHA256.Create())
            {
                var digest = sha256.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                return BitConverter.ToString(digest, 0, 8).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static string GetSessionInstanceId()
        {
            var key = "DccMcp.Unity.SessionInstanceId." + ProjectPathHash;
            var instanceId = SessionState.GetString(key, string.Empty);
            if (string.IsNullOrEmpty(instanceId))
            {
                instanceId = Guid.NewGuid().ToString("N");
                SessionState.SetString(key, instanceId);
            }
            return instanceId;
        }

        private static void QueueLog(string message, bool isWarning = false)
        {
            if (Lifetime.IsCancellationRequested)
            {
                return;
            }
            if (Interlocked.Increment(ref PendingLogCount) > MaxPendingLogs)
            {
                Interlocked.Decrement(ref PendingLogCount);
                return;
            }
            PendingLogs.Enqueue(new LogItem(message, isWarning));
        }

        private static async Task SendSafelyAsync(
            ClientWebSocket socket,
            JObject message,
            CancellationToken cancellationToken)
        {
            try
            {
                await SendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        await SendAsync(socket, message, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    SendGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                QueueLog("DCC-MCP Unity response failed: " + exception.Message, true);
            }
        }

        private static Task SendAsync(
            ClientWebSocket socket,
            JObject message,
            CancellationToken cancellationToken)
        {
            var payload = Encoding.UTF8.GetBytes(message.ToString(Formatting.None));
            if (payload.Length > MaxOutboundMessageBytes)
            {
                if (!string.Equals((string)message["type"], "response", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Unity bridge message exceeds 900 KiB.");
                }
                var boundedError = ErrorResponse(
                    message["id"],
                    -32003,
                    "Unity response exceeds 900 KiB; request a smaller bounded result.");
                payload = Encoding.UTF8.GetBytes(boundedError.ToString(Formatting.None));
            }
            return socket.SendAsync(
                new ArraySegment<byte>(payload),
                WebSocketMessageType.Text,
                true,
                cancellationToken);
        }
    }
}
