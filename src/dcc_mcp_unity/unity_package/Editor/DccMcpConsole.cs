using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace DccMcp.Unity
{
    [InitializeOnLoad]
    internal static class DccMcpConsole
    {
        private const int MaxEntries = 500;
        private const int MaxMessageCharacters = 4096;
        private const int MaxStackCharacters = 8192;
        private const int MaxEntriesPayloadBytes = 700 * 1024;
        private static readonly ConcurrentQueue<Entry> Entries = new ConcurrentQueue<Entry>();
        private static readonly DateTime StartedAtUtc = DateTime.UtcNow;
        private static int EntryCount;

        private sealed class Entry
        {
            internal readonly DateTime TimestampUtc;
            internal readonly string Message;
            internal readonly string StackTrace;
            internal readonly LogType Type;

            internal Entry(DateTime timestampUtc, string message, string stackTrace, LogType type)
            {
                TimestampUtc = timestampUtc;
                Message = message;
                StackTrace = stackTrace;
                Type = type;
            }
        }

        static DccMcpConsole()
        {
            Application.logMessageReceivedThreaded += Capture;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
        }

        internal static JObject Read(JObject parameters)
        {
            var limit = ReadLimit(parameters);
            if (limit < 1 || limit > 200)
            {
                throw new InvalidOperationException("limit must be between 1 and 200.");
            }

            var severity = ((string)parameters["severity"] ?? "all").Trim().ToLowerInvariant();
            if (severity != "all" && severity != "error" && severity != "warning" && severity != "log")
            {
                throw new InvalidOperationException(
                    "severity must be one of all, error, warning, or log.");
            }

            var selected = new List<JObject>();
            var selectedBytes = 0;
            var truncated = false;
            var snapshot = Entries.ToArray();
            for (var index = snapshot.Length - 1; index >= 0; index--)
            {
                var entry = snapshot[index];
                if (!Matches(entry.Type, severity))
                {
                    continue;
                }
                if (selected.Count >= limit)
                {
                    truncated = true;
                    break;
                }
                var item = Serialize(entry);
                var itemBytes = System.Text.Encoding.UTF8.GetByteCount(
                    item.ToString(Formatting.None));
                if (selectedBytes + itemBytes + 1 > MaxEntriesPayloadBytes)
                {
                    truncated = true;
                    break;
                }
                selected.Add(item);
                selectedBytes += itemBytes + 1;
            }
            selected.Reverse();

            var items = new JArray();
            foreach (var item in selected)
            {
                items.Add(item);
            }

            return new JObject
            {
                ["captured_since_utc"] = StartedAtUtc.ToString("O"),
                ["captured_count"] = Volatile.Read(ref EntryCount),
                ["severity"] = severity,
                ["entries"] = items,
                ["truncated"] = truncated,
            };
        }

        private static void Capture(string message, string stackTrace, LogType type)
        {
            Entries.Enqueue(new Entry(
                DateTime.UtcNow,
                Truncate(message, MaxMessageCharacters),
                Truncate(stackTrace, MaxStackCharacters),
                type));
            Interlocked.Increment(ref EntryCount);
            while (Volatile.Read(ref EntryCount) > MaxEntries && Entries.TryDequeue(out _))
            {
                Interlocked.Decrement(ref EntryCount);
            }
        }

        private static bool Matches(LogType type, string severity)
        {
            if (severity == "all")
            {
                return true;
            }
            if (severity == "warning")
            {
                return type == LogType.Warning;
            }
            if (severity == "log")
            {
                return type == LogType.Log;
            }
            return type == LogType.Error || type == LogType.Assert || type == LogType.Exception;
        }

        private static int ReadLimit(JObject parameters)
        {
            var token = parameters["limit"];
            if (token == null)
            {
                return 100;
            }
            if (token.Type != JTokenType.Integer)
            {
                throw new InvalidOperationException("limit must be an integer.");
            }
            try
            {
                var value = (long)token;
                if (value < int.MinValue || value > int.MaxValue)
                {
                    throw new InvalidOperationException("limit is outside the 32-bit range.");
                }
                return (int)value;
            }
            catch (OverflowException)
            {
                throw new InvalidOperationException("limit is outside the 32-bit range.");
            }
        }

        private static JObject Serialize(Entry entry)
        {
            return new JObject
            {
                ["timestamp_utc"] = entry.TimestampUtc.ToString("O"),
                ["severity"] = Label(entry.Type),
                ["message"] = entry.Message,
                ["stack_trace"] = entry.StackTrace,
            };
        }

        private static string Label(LogType type)
        {
            if (type == LogType.Warning)
            {
                return "warning";
            }
            if (type == LogType.Log)
            {
                return "log";
            }
            return "error";
        }

        private static string Truncate(string value, int maxCharacters)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxCharacters)
            {
                return value ?? string.Empty;
            }
            return value.Substring(0, maxCharacters) + "…";
        }

        private static void Stop()
        {
            Application.logMessageReceivedThreaded -= Capture;
        }
    }
}
