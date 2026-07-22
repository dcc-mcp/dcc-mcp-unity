using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Xml;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DccMcp.Unity
{
    internal static class DccMcpTestRunner
    {
        internal const int MaxTestNames = 128;
        private const int MaxTestNameCharacters = 512;
        private const long MaxReportBytes = 16L * 1024 * 1024;

        internal static JObject NormalizeParameters(JObject parameters)
        {
            RequireOnlyProperties(parameters, "request_id", "test_mode", "test_names");
            var requestId = NormalizeRequestId(parameters["request_id"]);
            var testMode = RequireString(parameters, "test_mode");
            if (testMode != "edit_mode" && testMode != "play_mode")
            {
                throw new InvalidOperationException("test_mode must be edit_mode or play_mode.");
            }

            var names = new SortedSet<string>(StringComparer.Ordinal);
            var namesToken = parameters["test_names"];
            if (namesToken != null)
            {
                if (!(namesToken is JArray array))
                {
                    throw new InvalidOperationException("test_names must be an array of exact test names.");
                }
                if (array.Count > MaxTestNames)
                {
                    throw new InvalidOperationException("test_names may contain at most 128 entries.");
                }
                foreach (var item in array)
                {
                    if (item == null || item.Type != JTokenType.String)
                    {
                        throw new InvalidOperationException(
                            "Every test_names entry must be an exact test name string.");
                    }
                    var name = (string)item;
                    if (string.IsNullOrWhiteSpace(name)
                        || name.Length > MaxTestNameCharacters
                        || !string.Equals(name, name.Trim(), StringComparison.Ordinal)
                        || ContainsControlCharacter(name))
                    {
                        throw new InvalidOperationException(
                            "Every test_names entry must be a trimmed, non-empty string of at most 512 characters.");
                    }
                    names.Add(name);
                }
            }

            return new JObject
            {
                ["request_id"] = requestId,
                ["test_mode"] = testMode,
                ["test_names"] = new JArray(names),
            };
        }

        internal static string PrepareReportPath(string requestId, out string relativePath)
        {
            relativePath = Path.Combine(
                "Builds",
                "DccMcp",
                "Tests",
                requestId,
                "results.xml");
            var projectPath = Path.GetDirectoryName(Application.dataPath) ?? string.Empty;
            var fullPath = Path.GetFullPath(Path.Combine(projectPath, relativePath));
            EnsureProjectPathSafe(fullPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory))
            {
                throw new InvalidOperationException("Unity test report requires a parent directory.");
            }
            if (Directory.Exists(directory) || File.Exists(fullPath))
            {
                throw new InvalidOperationException(
                    "The fixed Unity test report directory already exists: " + relativePath);
            }
            Directory.CreateDirectory(directory);
            EnsureProjectPathSafe(directory);
            return fullPath;
        }

        internal static void Start(JObject parameters, string reportPath)
        {
            EnsureProjectPathSafe(reportPath);
            DccMcpTestFrameworkBridge.Start(parameters, reportPath);
        }

        internal static void EnsureCallback(string requestId, string reportPath)
        {
            EnsureProjectPathSafe(reportPath);
            DccMcpTestFrameworkBridge.EnsureCallback(requestId, reportPath);
        }

        internal static bool TrySummarizeReport(string reportPath, out JObject summary)
        {
            summary = null;
            if (!File.Exists(reportPath))
            {
                return false;
            }
            try
            {
                summary = SummarizeReport(reportPath);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (XmlException)
            {
                return false;
            }
        }

        internal static JObject SummarizeReport(string reportPath)
        {
            EnsureProjectPathSafe(reportPath);
            var info = new FileInfo(reportPath);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxReportBytes)
            {
                throw new InvalidOperationException(
                    "Unity test report must be a non-empty XML file no larger than 16 MiB.");
            }

            var document = new XmlDocument { XmlResolver = null };
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxReportBytes,
            };
            using (var stream = new FileStream(
                reportPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            using (var reader = XmlReader.Create(stream, settings))
            {
                document.Load(reader);
            }

            var root = document.DocumentElement;
            if (root == null || (root.Name != "test-run" && root.Name != "test-suite"))
            {
                throw new InvalidOperationException("Unity test report root is not an NUnit test run.");
            }
            var total = ReadIntAttribute(root, "total", "testcasecount");
            if (total <= 0)
            {
                throw new InvalidOperationException("Unity test filters did not match any tests.");
            }
            var passed = ReadIntAttribute(root, "passed");
            var failed = ReadIntAttribute(root, "failed");
            var inconclusive = ReadIntAttribute(root, "inconclusive");
            var skipped = ReadIntAttribute(root, "skipped");
            var failures = new JArray();
            var failedCases = document.SelectNodes("//test-case[@result='Failed']");
            if (failedCases != null)
            {
                for (var index = 0; index < failedCases.Count && index < 20; index++)
                {
                    var failedCase = failedCases[index];
                    failures.Add(new JObject
                    {
                        ["name"] = Truncate(Attribute(failedCase, "fullname"), 512),
                        ["message"] = Truncate(
                            failedCase.SelectSingleNode("failure/message")?.InnerText ?? string.Empty,
                            2000),
                    });
                }
            }

            return new JObject
            {
                ["outcome"] = failed == 0 && inconclusive == 0 ? "passed" : "failed",
                ["total"] = total,
                ["passed"] = passed,
                ["failed"] = failed,
                ["inconclusive"] = inconclusive,
                ["skipped"] = skipped,
                ["duration_seconds"] = ReadDoubleAttribute(root, "duration"),
                ["failure_details"] = failures,
                ["bytes"] = info.Length,
                ["sha256"] = HashFile(reportPath),
            };
        }

        internal static void ReleaseCallback(string requestId)
        {
            DccMcpTestFrameworkBridge.ReleaseCallback(requestId);
        }

        private static int ReadIntAttribute(XmlElement element, params string[] names)
        {
            foreach (var name in names)
            {
                int value;
                if (int.TryParse(
                    element.GetAttribute(name),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out value))
                {
                    return value;
                }
            }
            return 0;
        }

        private static double ReadDoubleAttribute(XmlElement element, string name)
        {
            double value;
            return double.TryParse(
                element.GetAttribute(name),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value)
                ? value
                : 0d;
        }

        private static string Attribute(XmlNode node, string name)
        {
            return node.Attributes?[name]?.Value ?? string.Empty;
        }

        private static string HashFile(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha256 = SHA256.Create())
            {
                return BitConverter.ToString(sha256.ComputeHash(stream))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }

        private static void EnsureProjectPathSafe(string path)
        {
            var projectPath = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath) ?? string.Empty);
            var fullPath = Path.GetFullPath(path);
            var comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!fullPath.Equals(projectPath, comparison)
                && !fullPath.StartsWith(projectPath + Path.DirectorySeparatorChar, comparison))
            {
                throw new InvalidOperationException("Unity test evidence path must stay inside the project.");
            }
            var current = projectPath;
            CheckReparsePointIfPresent(current);
            var remainder = fullPath.Substring(projectPath.Length).TrimStart(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            foreach (var segment in remainder.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                CheckReparsePointIfPresent(current);
            }
        }

        private static void CheckReparsePointIfPresent(string path)
        {
            if ((File.Exists(path) || Directory.Exists(path))
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Reparse points are not allowed: " + path);
            }
        }

        private static string NormalizeRequestId(JToken token)
        {
            if (token == null || token.Type != JTokenType.String)
            {
                throw new InvalidOperationException("request_id must be a UUID string.");
            }
            Guid parsed;
            if (!Guid.TryParseExact((string)token, "D", out parsed))
            {
                throw new InvalidOperationException("request_id must use canonical UUID format.");
            }
            return parsed.ToString("D");
        }

        private static string RequireString(JObject value, string property)
        {
            var token = value[property];
            if (token == null || token.Type != JTokenType.String || string.IsNullOrEmpty((string)token))
            {
                throw new InvalidOperationException(property + " must be a non-empty string.");
            }
            return (string)token;
        }

        private static void RequireOnlyProperties(JObject value, params string[] allowed)
        {
            var names = new HashSet<string>(allowed, StringComparer.Ordinal);
            foreach (var property in value.Properties())
            {
                if (!names.Contains(property.Name))
                {
                    throw new InvalidOperationException("Unexpected parameter: " + property.Name);
                }
            }
        }

        private static bool ContainsControlCharacter(string value)
        {
            foreach (var character in value)
            {
                if (char.IsControl(character))
                {
                    return true;
                }
            }
            return false;
        }

        private static string Truncate(string value, int maxCharacters)
        {
            return value.Length <= maxCharacters ? value : value.Substring(0, maxCharacters);
        }
    }
}
