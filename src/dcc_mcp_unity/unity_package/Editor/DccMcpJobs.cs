using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DccMcp.Unity
{
    [InitializeOnLoad]
    internal static class DccMcpJobs
    {
        internal const string SessionStateKey = "DccMcp.Unity.Jobs.v1";
        private const int MaxJobs = 256;
        internal const int MaxTextAssetBytes = 256 * 1024;
        internal const int MaxCaptureBytes = 32 * 1024 * 1024;
        internal const int MaxCaptureDimension = 8192;
        internal const long MaxCapturePixels = 32L * 1024 * 1024;
        private const int MaxTextAssetPathCharacters = 512;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly uint[] PngCrcTable = CreatePngCrcTable();
        private static readonly HashSet<string> TextExtensions = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".json", ".txt", ".shader", ".cginc", ".asmdef", ".xml", ".uss", ".uxml",
        };
        private static bool IsTicking;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle handle,
            StringBuilder path,
            uint pathCharacters,
            uint flags);

        static DccMcpJobs()
        {
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            RestoreTestCallbackAfterReload();
        }

        internal static JObject ReadTextAsset(JObject parameters)
        {
            RequireOnlyProperties(parameters, "path");
            var relativePath = RequireString(parameters, "path");
            var fullPath = ResolveTextAssetPath(relativePath);
            if (!File.Exists(fullPath))
            {
                throw new InvalidOperationException("Unity text asset does not exist: " + relativePath);
            }

            EnsureNoReparsePoints(fullPath);
            var bytes = ReadBoundedBytes(fullPath);
            string content;
            try
            {
                content = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw new InvalidOperationException("Unity text asset is not valid UTF-8: " + relativePath);
            }
            EnsureTransportSafeText(content);

            return new JObject
            {
                ["path"] = NormalizeAssetPath(relativePath),
                ["content"] = content,
                ["utf8_bytes"] = bytes.Length,
                ["sha256"] = HashBytes(bytes),
            };
        }

        internal static JObject Submit(string kind, JObject parameters)
        {
            var normalized = NormalizeJobParameters(kind, parameters);
            var requestId = (string)normalized["request_id"];
            var fingerprint = Fingerprint(kind, normalized);
            var store = LoadStore();
            var jobs = (JArray)store["jobs"];

            foreach (JObject existing in jobs)
            {
                if (!string.Equals((string)existing["request_id"], requestId, StringComparison.Ordinal))
                {
                    continue;
                }
                if (!string.Equals((string)existing["fingerprint"], fingerprint, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The same request_id was already used with different parameters.");
                }
                return PublicJob(existing);
            }

            foreach (JObject existing in jobs)
            {
                var state = (string)existing["state"];
                if (state == "queued" || state == "running")
                {
                    throw new InvalidOperationException(
                        "Another mutating Unity job is queued or running; inspect it before submitting a new job.");
                }
            }

            if (jobs.Count >= MaxJobs)
            {
                throw new InvalidOperationException(
                    "Unity job history reached 256 entries; restart the Editor before submitting more jobs.");
            }
            var now = DateTime.UtcNow.ToString("O");
            var job = new JObject
            {
                ["request_id"] = requestId,
                ["kind"] = kind,
                ["state"] = "queued",
                ["phase"] = "queued",
                ["fingerprint"] = fingerprint,
                ["parameters"] = normalized,
                ["created_at_utc"] = now,
                ["updated_at_utc"] = now,
            };
            jobs.Add(job);
            SaveStore(store);
            return PublicJob(job);
        }

        internal static JObject Inspect(JObject parameters)
        {
            RequireOnlyProperties(parameters, "request_id");
            var requestId = NormalizeRequestId(parameters["request_id"]);
            foreach (JObject job in (JArray)LoadStore()["jobs"])
            {
                if (string.Equals((string)job["request_id"], requestId, StringComparison.Ordinal))
                {
                    return PublicJob(job);
                }
            }
            throw new InvalidOperationException("Unity job was not found for request_id: " + requestId);
        }

        private static void Tick()
        {
            if (IsTicking)
            {
                return;
            }
            IsTicking = true;
            try
            {
                var store = LoadStore();
                JObject active = null;
                foreach (JObject job in (JArray)store["jobs"])
                {
                    var state = (string)job["state"];
                    if (state == "queued" || state == "running")
                    {
                        active = job;
                        break;
                    }
                }
                if (active == null)
                {
                    return;
                }

                if ((string)active["state"] == "queued")
                {
                    active["state"] = "running";
                    active["phase"] = "starting";
                    Touch(active);
                    SaveStore(store);
                }

                try
                {
                    Advance(store, active);
                }
                catch (Exception exception)
                {
                    Fail(active, exception.Message);
                }
                SaveStore(store);
            }
            catch (Exception exception)
            {
                Debug.LogError("DCC-MCP Unity job protocol failed: " + exception.Message);
            }
            finally
            {
                IsTicking = false;
            }
        }

        private static void Advance(JObject store, JObject job)
        {
            switch ((string)job["kind"])
            {
                case "assets.upsert_text":
                    AdvanceUpsert(store, job);
                    break;
                case "project.refresh_and_compile":
                    AdvanceRefreshAndCompile(store, job);
                    break;
                case "editor.set_play_mode":
                    AdvancePlayMode(store, job);
                    break;
                case "project.build_windows_player":
                    AdvanceWindowsBuild(store, job);
                    break;
                case "project.run_tests":
                    AdvanceTestRun(store, job);
                    break;
                case "editor.capture_game_view":
                    AdvanceCapture(store, job);
                    break;
                default:
                    throw new InvalidOperationException("Unknown Unity job kind: " + (string)job["kind"]);
            }
        }

        private static void AdvanceUpsert(JObject store, JObject job)
        {
            RequireSourceWriteGate();
            EnsureEditorIdleAndEditing("Source writes");
            var parameters = (JObject)job["parameters"];
            var relativePath = (string)parameters["path"];
            var fullPath = ResolveTextAssetPath(relativePath);
            var bytes = StrictUtf8.GetBytes((string)parameters["content"]);
            if (bytes.Length > MaxTextAssetBytes)
            {
                throw new InvalidOperationException("content must be at most 256 KiB when encoded as UTF-8.");
            }

            using (AcquireAssetWriteLock(fullPath))
            {
                EnsureNoReparsePoints(fullPath);
                var desiredHash = HashBytes(bytes);
                var expectedHash = (string)parameters["expected_sha256"];
                var resumingValidatedWrite = (string)job["phase"] == "writing";
                var exists = File.Exists(fullPath);
                if (exists)
                {
                    var currentHash = HashBytes(ReadBoundedBytes(fullPath));
                    if (resumingValidatedWrite
                        && string.Equals(currentHash, desiredHash, StringComparison.Ordinal))
                    {
                        Succeed(job, new JObject
                        {
                            ["path"] = relativePath,
                            ["sha256"] = desiredHash,
                            ["utf8_bytes"] = bytes.Length,
                            ["changed"] = true,
                        });
                        return;
                    }
                    if (!string.Equals(currentHash, expectedHash, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "CAS mismatch for " + relativePath
                            + "; read the asset again before updating it.");
                    }
                    if (string.Equals(currentHash, desiredHash, StringComparison.Ordinal))
                    {
                        Succeed(job, new JObject
                        {
                            ["path"] = relativePath,
                            ["sha256"] = desiredHash,
                            ["utf8_bytes"] = bytes.Length,
                            ["changed"] = false,
                        });
                        return;
                    }
                }
                else if (!string.Equals(expectedHash, "absent", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "CAS mismatch for " + relativePath + "; the asset is absent.");
                }

                var directory = Path.GetDirectoryName(fullPath);
                if (string.IsNullOrEmpty(directory))
                {
                    throw new InvalidOperationException("Unity text asset requires a parent directory.");
                }
                EnsureNoReparsePoints(directory);
                Directory.CreateDirectory(directory);
                EnsureNoReparsePoints(directory);

                job["phase"] = "writing";
                Touch(job);
                SaveStore(store);

                var suffix = Guid.NewGuid().ToString("N");
                var temporary = fullPath + ".dccmcp-" + suffix + ".tmp";
                var backup = fullPath + ".dccmcp-" + suffix + ".backup";
                var deleteBackup = true;
                try
                {
                    using (var stream = new FileStream(
                        temporary,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        4096,
                        FileOptions.WriteThrough))
                    {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }

                    EnsureNoReparsePoints(fullPath);
                    if (File.Exists(fullPath))
                    {
                        var currentHash = HashBytes(ReadBoundedBytes(fullPath));
                        if (!string.Equals(currentHash, expectedHash, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                "CAS mismatch for " + relativePath
                                + "; the asset changed during the write.");
                        }
                        deleteBackup = false;
                        ReplaceExistingFileWithCas(
                            relativePath,
                            temporary,
                            fullPath,
                            backup,
                            expectedHash,
                            null);
                        deleteBackup = true;
                    }
                    else
                    {
                        if (!string.Equals(expectedHash, "absent", StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                "CAS mismatch for " + relativePath
                                + "; the asset disappeared during the write.");
                        }
                        try
                        {
                            File.Move(temporary, fullPath);
                        }
                        catch (IOException exception)
                        {
                            throw new InvalidOperationException(
                                "CAS mismatch for " + relativePath
                                + "; another writer created the asset first.",
                                exception);
                        }
                    }
                }
                finally
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                    if (deleteBackup && File.Exists(backup))
                    {
                        File.Delete(backup);
                    }
                }
                EnsureNoReparsePoints(fullPath);
                if (!string.Equals(
                    HashBytes(ReadBoundedBytes(fullPath)),
                    desiredHash,
                    StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Unity text asset verification failed after atomic replacement: " + relativePath);
                }

                Succeed(job, new JObject
                {
                    ["path"] = relativePath,
                    ["sha256"] = desiredHash,
                    ["utf8_bytes"] = bytes.Length,
                    ["changed"] = true,
                });
            }
        }

        internal static void ReplaceExistingFileWithCas(
            string relativePath,
            string temporaryPath,
            string targetPath,
            string backupPath,
            string expectedHash,
            Action afterReplacement)
        {
            File.Replace(temporaryPath, targetPath, backupPath);
            if (afterReplacement != null)
            {
                afterReplacement();
            }

            string replacedHash;
            try
            {
                replacedHash = HashBytes(ReadBoundedBytes(backupPath));
            }
            catch (IOException)
            {
                replacedHash = null;
            }
            catch (UnauthorizedAccessException)
            {
                replacedHash = null;
            }
            catch (InvalidOperationException)
            {
                replacedHash = null;
            }
            if (!string.Equals(replacedHash, expectedHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "CAS mismatch for " + relativePath
                    + "; a concurrent writer won the replacement race. The current target was left"
                    + " untouched after conflict detection, and displaced content remains in the"
                    + " preserved conflict backup " + backupPath + ".");
            }
            File.Delete(backupPath);
        }

        private static void AdvanceRefreshAndCompile(JObject store, JObject job)
        {
            if ((string)job["phase"] == "starting")
            {
                EnsureEditorIdleAndEditing("Asset refresh");
                job["phase"] = "waiting_for_compile";
                job.Remove("idle_since_utc");
                Touch(job);
                SaveStore(store);
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                return;
            }

            if (TimedOut(job, TimeSpan.FromMinutes(10)))
            {
                throw new InvalidOperationException("Unity refresh and compile did not finish within 10 minutes.");
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                job.Remove("idle_since_utc");
                Touch(job);
                return;
            }

            if (job["idle_since_utc"] == null)
            {
                job["idle_since_utc"] = DateTime.UtcNow.ToString("O");
                Touch(job);
                return;
            }
            if (!TimedOutSince((string)job["idle_since_utc"], TimeSpan.FromSeconds(1)))
            {
                Touch(job);
                return;
            }
            if (EditorUtility.scriptCompilationFailed)
            {
                throw new InvalidOperationException(
                    "Unity script compilation failed; inspect the Unity Console for compiler errors.");
            }
            Succeed(job, new JObject { ["compiled"] = true });
        }

        private static void AdvancePlayMode(JObject store, JObject job)
        {
            var play = (bool)((JObject)job["parameters"])["play"];
            if ((string)job["phase"] == "starting")
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    throw new InvalidOperationException(
                        "Play Mode cannot change while Unity is compiling or updating assets.");
                }
                PersistPlayModeTransition(
                    store,
                    job,
                    () => EditorApplication.isPlaying = play);
                return;
            }

            if (TimedOut(job, TimeSpan.FromMinutes(2)))
            {
                throw new InvalidOperationException("Unity Play Mode did not reach the requested state.");
            }
            if (EditorApplication.isPlaying != play)
            {
                Touch(job);
                return;
            }
            Succeed(job, new JObject { ["is_playing"] = play });
        }

        internal static void PersistPlayModeTransition(
            JObject store,
            JObject job,
            Action requestTransition)
        {
            if (requestTransition == null)
            {
                throw new ArgumentNullException(nameof(requestTransition));
            }
            job["phase"] = "waiting_for_play_mode";
            Touch(job);
            SaveStore(store);
            requestTransition();
        }

        private static void AdvanceWindowsBuild(JObject store, JObject job)
        {
            var phase = (string)job["phase"];
            if (phase == "building")
            {
                throw new InvalidOperationException(
                    "Unity reloaded while the player build was running; completion cannot be proven.");
            }
            if (TimedOut(job, TimeSpan.FromMinutes(10)))
            {
                throw new InvalidOperationException(
                    "Unity did not become ready for a Windows player build within 10 minutes.");
            }

            if (phase == "starting")
            {
                EnsureEditorIdleAndEditing("Windows player builds");
                var initialScenes = ValidateEnabledBuildScenes();
                var recordedScenes = new JArray();
                foreach (var scene in initialScenes)
                {
                    recordedScenes.Add(scene);
                }
                job["build_scenes"] = recordedScenes;
                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
                {
                    job["phase"] = "switching_build_target";
                    Touch(job);
                    SaveStore(store);
                    if (!EditorUserBuildSettings.SwitchActiveBuildTarget(
                        BuildTargetGroup.Standalone,
                        BuildTarget.StandaloneWindows64))
                    {
                        throw new InvalidOperationException(
                            "Unity could not switch the active build target to Windows x64.");
                    }
                    return;
                }
                job["phase"] = "ready_to_build";
                Touch(job);
                SaveStore(store);
                return;
            }

            if (phase == "switching_build_target")
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    Touch(job);
                    return;
                }
                EnsureEditorIdleAndEditing("Windows player builds");
                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
                {
                    throw new InvalidOperationException(
                        "Unity finished reloading without activating the Windows x64 build target.");
                }
                job["phase"] = "ready_to_build";
                Touch(job);
                SaveStore(store);
                return;
            }

            if (phase != "ready_to_build")
            {
                throw new InvalidOperationException("Unknown Windows build job phase: " + phase);
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                Touch(job);
                return;
            }
            EnsureEditorIdleAndEditing("Windows player builds");
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
            {
                throw new InvalidOperationException(
                    "The active build target changed before the Windows player build started.");
            }
            var scenes = ValidateEnabledBuildScenes();
            EnsureBuildScenesUnchanged((JArray)job["build_scenes"], scenes);

            var requestId = (string)job["request_id"];
            var relativeDirectory = Path.Combine("Builds", "DccMcp", requestId);
            var projectPath = Path.GetDirectoryName(Application.dataPath) ?? string.Empty;
            var outputDirectory = Path.Combine(projectPath, relativeDirectory);
            EnsureNoReparsePoints(outputDirectory);
            if (Directory.Exists(outputDirectory))
            {
                throw new InvalidOperationException(
                    "The fixed Unity build output directory already exists: " + relativeDirectory);
            }
            Directory.CreateDirectory(outputDirectory);
            EnsureNoReparsePoints(outputDirectory);

            var outputPath = Path.Combine(outputDirectory, "DccMcpGame.exe");
            job["phase"] = "building";
            Touch(job);
            SaveStore(store);
            var report = BuildPipeline.BuildPlayer(
                scenes,
                outputPath,
                BuildTarget.StandaloneWindows64,
                BuildOptions.None);
            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new InvalidOperationException(
                    "Unity Windows player build failed with " + report.summary.totalErrors
                    + " errors (" + report.summary.result + ").");
            }
            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                throw new InvalidOperationException(
                    "Unity reported a successful build but the Windows executable is missing or empty.");
            }
            var dataPath = Path.Combine(outputDirectory, "DccMcpGame_Data");
            if (!Directory.Exists(dataPath))
            {
                throw new InvalidOperationException(
                    "Unity reported a successful build but DccMcpGame_Data is missing.");
            }

            var sceneArray = new JArray();
            foreach (var scene in scenes)
            {
                sceneArray.Add(scene);
            }
            Succeed(job, new JObject
            {
                ["path"] = NormalizeSeparators(outputPath),
                ["relative_path"] = NormalizeSeparators(
                    Path.Combine(relativeDirectory, "DccMcpGame.exe")),
                ["data_path"] = NormalizeSeparators(dataPath),
                ["scenes"] = sceneArray,
                ["bytes"] = (long)report.summary.totalSize,
                ["executable_bytes"] = new FileInfo(outputPath).Length,
            });
        }

        internal static string[] ValidateEnabledBuildScenes()
        {
            var scenes = new List<string>();
            var enabled = new HashSet<string>(
                Path.DirectorySeparatorChar == '\\'
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);
            var projectPath = Path.GetDirectoryName(Application.dataPath) ?? string.Empty;
            foreach (var scene in EditorBuildSettings.scenes)
            {
                if (!scene.enabled)
                {
                    continue;
                }
                var path = (scene.path ?? string.Empty).Replace('\\', '/');
                if (string.IsNullOrEmpty(path)
                    || !path.StartsWith("Assets/", StringComparison.Ordinal)
                    || !path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Every enabled Build Settings scene must be a saved .unity asset below Assets.");
                }
                if (!enabled.Add(path))
                {
                    throw new InvalidOperationException(
                        "Build Settings contains a duplicate enabled scene: " + path);
                }
                var fullPath = Path.GetFullPath(Path.Combine(
                    projectPath,
                    path.Replace('/', Path.DirectorySeparatorChar)));
                EnsureNoReparsePoints(fullPath);
                if (!File.Exists(fullPath) || AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
                {
                    throw new InvalidOperationException(
                        "Enabled Build Settings scene does not exist as a Unity scene asset: " + path);
                }
                scenes.Add(path);
            }
            if (scenes.Count == 0)
            {
                throw new InvalidOperationException("No enabled scenes are configured in Build Settings.");
            }

            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var openScene = SceneManager.GetSceneAt(index);
                var openPath = (openScene.path ?? string.Empty).Replace('\\', '/');
                if (openScene.isLoaded && openScene.isDirty && enabled.Contains(openPath))
                {
                    throw new InvalidOperationException(
                        "Enabled scene has unsaved changes and cannot be built safely: " + openPath);
                }
            }
            return scenes.ToArray();
        }

        private static void AdvanceTestRun(JObject store, JObject job)
        {
            var parameters = (JObject)job["parameters"];
            if ((string)job["phase"] == "starting")
            {
                EnsureEditorIdleAndEditing("Unity tests");
                if (EditorUtility.scriptCompilationFailed)
                {
                    throw new InvalidOperationException(
                        "Unity tests cannot start while the project has script compilation errors.");
                }
                string relativePath;
                var reportPath = DccMcpTestRunner.PrepareReportPath(
                    (string)job["request_id"],
                    out relativePath);
                job["test_report_path"] = reportPath;
                job["test_report_relative_path"] = NormalizeSeparators(relativePath);
                job["phase"] = "waiting_for_tests";
                Touch(job);
                SaveStore(store);
                DccMcpTestRunner.Start(parameters, reportPath);
                return;
            }
            if ((string)job["phase"] != "waiting_for_tests")
            {
                throw new InvalidOperationException(
                    "Unknown Unity test job phase: " + (string)job["phase"]);
            }
            if (TimedOut(job, TimeSpan.FromMinutes(20)))
            {
                throw new InvalidOperationException("Unity tests did not finish within 20 minutes.");
            }

            DccMcpTestRunner.EnsureCallback(
                (string)job["request_id"],
                (string)job["test_report_path"]);

            JObject summary;
            if (!DccMcpTestRunner.TrySummarizeReport(
                (string)job["test_report_path"],
                out summary))
            {
                Touch(job);
                return;
            }
            summary["path"] = NormalizeSeparators((string)job["test_report_path"]);
            summary["relative_path"] = (string)job["test_report_relative_path"];
            summary["test_mode"] = parameters["test_mode"].DeepClone();
            summary["test_names"] = parameters["test_names"].DeepClone();
            DccMcpTestRunner.ReleaseCallback((string)job["request_id"]);
            Succeed(job, summary);
        }

        private static void EnsureBuildScenesUnchanged(JArray recorded, string[] current)
        {
            if (recorded == null || recorded.Count != current.Length)
            {
                throw new InvalidOperationException(
                    "Enabled Build Settings scenes changed while preparing the Windows build.");
            }
            for (var index = 0; index < current.Length; index++)
            {
                if (!string.Equals((string)recorded[index], current[index], StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Enabled Build Settings scenes changed while preparing the Windows build.");
                }
            }
        }

        private static void AdvanceCapture(JObject store, JObject job)
        {
            EnsureCaptureCanAdvance(EditorApplication.isPlaying, EditorApplication.isPaused);
            if ((string)job["phase"] == "starting")
            {
                var requestId = (string)job["request_id"];
                var relativePath = Path.Combine("Builds", "DccMcp", "Captures", requestId + ".png");
                var projectPath = Path.GetDirectoryName(Application.dataPath) ?? string.Empty;
                var fullPath = Path.Combine(projectPath, relativePath);
                EnsureNoReparsePoints(fullPath);
                if (File.Exists(fullPath))
                {
                    throw new InvalidOperationException(
                        "The fixed Game View capture path already exists: " + relativePath);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                EnsureNoReparsePoints(fullPath);
                job["capture_path"] = fullPath;
                job["capture_relative_path"] = NormalizeSeparators(relativePath);
                job["capture_started_at_utc"] = DateTime.UtcNow.ToString("O");
                job["capture_after_frame"] = Time.frameCount + 1;
                FocusGameView();
                job["phase"] = "waiting_for_game_view";
                Touch(job);
                SaveStore(store);
                return;
            }

            if ((string)job["phase"] == "waiting_for_game_view")
            {
                FocusGameView();
                if (Time.frameCount < (int)job["capture_after_frame"])
                {
                    if (TimedOutSince(
                        (string)job["capture_started_at_utc"],
                        TimeSpan.FromSeconds(30)))
                    {
                        throw new InvalidOperationException(
                            "Unity did not render a Game View frame within 30 seconds.");
                    }
                    Touch(job);
                    return;
                }
                job["phase"] = "waiting_for_capture";
                Touch(job);
                SaveStore(store);
                ScreenCapture.CaptureScreenshot((string)job["capture_path"]);
                return;
            }
            if ((string)job["phase"] != "waiting_for_capture")
            {
                throw new InvalidOperationException(
                    "Unknown Game View capture job phase: " + (string)job["phase"]);
            }

            var capturePath = (string)job["capture_path"];
            int width;
            int height;
            if (TryDecodePng(capturePath, out width, out height))
            {
                Succeed(job, new JObject
                {
                    ["path"] = NormalizeSeparators(capturePath),
                    ["relative_path"] = (string)job["capture_relative_path"],
                    ["bytes"] = new FileInfo(capturePath).Length,
                    ["width"] = width,
                    ["height"] = height,
                });
                return;
            }
            if (TimedOutSince((string)job["capture_started_at_utc"], TimeSpan.FromSeconds(30)))
            {
                throw new InvalidOperationException("Unity did not finish the Game View capture within 30 seconds.");
            }
            Touch(job);
        }

        internal static void EnsureCaptureCanAdvance(bool isPlaying, bool isPaused)
        {
            if (!isPlaying)
            {
                throw new InvalidOperationException("Game View capture requires Play Mode.");
            }
            if (isPaused)
            {
                throw new InvalidOperationException(
                    "Game View capture cannot advance while Unity is paused. Resume Play Mode,"
                    + " then submit the capture again with a new request_id.");
            }
        }

        private static JObject NormalizeJobParameters(string kind, JObject parameters)
        {
            if (kind == "project.run_tests")
            {
                return DccMcpTestRunner.NormalizeParameters(parameters);
            }
            var requestId = NormalizeRequestId(parameters["request_id"]);
            var normalized = new JObject { ["request_id"] = requestId };
            switch (kind)
            {
                case "assets.upsert_text":
                    RequireOnlyProperties(
                        parameters,
                        "request_id",
                        "path",
                        "content",
                        "expected_sha256");
                    RequireSourceWriteGate();
                    var path = NormalizeAssetPath(RequireString(parameters, "path"));
                    ResolveTextAssetPath(path);
                    var content = RequireStringToken(parameters, "content");
                    EnsureTransportSafeText(content);
                    if (StrictUtf8.GetByteCount(content) > MaxTextAssetBytes)
                    {
                        throw new InvalidOperationException(
                            "content must be at most 256 KiB when encoded as UTF-8.");
                    }
                    var expected = RequireString(parameters, "expected_sha256");
                    if (expected != "absent" && !IsLowerSha256(expected))
                    {
                        throw new InvalidOperationException(
                            "expected_sha256 must be absent or a lowercase SHA-256 digest.");
                    }
                    normalized["path"] = path;
                    normalized["content"] = content;
                    normalized["expected_sha256"] = expected;
                    break;
                case "editor.set_play_mode":
                    RequireOnlyProperties(parameters, "request_id", "play");
                    if (parameters["play"] == null || parameters["play"].Type != JTokenType.Boolean)
                    {
                        throw new InvalidOperationException("play must be a boolean.");
                    }
                    normalized["play"] = (bool)parameters["play"];
                    break;
                case "project.refresh_and_compile":
                case "project.build_windows_player":
                case "editor.capture_game_view":
                    RequireOnlyProperties(parameters, "request_id");
                    break;
                default:
                    throw new InvalidOperationException("Unknown Unity job kind: " + kind);
            }
            return normalized;
        }

        private static string ResolveTextAssetPath(string relativePath)
        {
            var normalized = NormalizeAssetPath(relativePath);
            var extension = Path.GetExtension(normalized);
            if (!TextExtensions.Contains(extension))
            {
                throw new InvalidOperationException("Unsupported Unity text asset extension: " + extension);
            }

            var projectPath = Path.GetDirectoryName(Application.dataPath) ?? string.Empty;
            var fullPath = Path.GetFullPath(Path.Combine(
                projectPath,
                normalized.Replace('/', Path.DirectorySeparatorChar)));
            var assetsRoot = Path.GetFullPath(Application.dataPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!fullPath.StartsWith(assetsRoot, comparison))
            {
                throw new InvalidOperationException("Unity text asset path must stay below Assets.");
            }
            return fullPath;
        }

        private static string NormalizeAssetPath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)
                || relativePath.Length > MaxTextAssetPathCharacters
                || relativePath.IndexOf('\\') >= 0
                || !relativePath.StartsWith("Assets/", StringComparison.Ordinal)
                || relativePath.EndsWith("/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "path must be a forward-slash Unity asset path below Assets.");
            }
            var segments = relativePath.Split('/');
            foreach (var segment in segments)
            {
                if (string.IsNullOrEmpty(segment) || segment == "." || segment == "..")
                {
                    throw new InvalidOperationException("path contains an invalid segment.");
                }
                if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    throw new InvalidOperationException("path contains invalid filename characters.");
                }
            }
            return relativePath;
        }

        private static void EnsureNoReparsePoints(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var projectPath = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath) ?? string.Empty);
            var comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!fullPath.Equals(projectPath, comparison)
                && !fullPath.StartsWith(projectPath + Path.DirectorySeparatorChar, comparison))
            {
                throw new InvalidOperationException("Path must stay inside the Unity project.");
            }

            var current = projectPath;
            CheckReparsePoint(current);
            var remainder = fullPath.Substring(projectPath.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var segment in remainder.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                CheckReparsePointIfPresent(current);
            }
        }

        private static void CheckReparsePoint(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Reparse points are not allowed: " + path);
            }
        }

        private static void CheckReparsePointIfPresent(string path)
        {
            try
            {
                CheckReparsePoint(path);
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
        }

        private static FileStream AcquireAssetWriteLock(string assetPath)
        {
            var projectPath = Path.GetDirectoryName(Application.dataPath) ?? string.Empty;
            var lockDirectory = Path.Combine(projectPath, "Library", "DccMcp", "AssetLocks");
            EnsureNoReparsePoints(lockDirectory);
            Directory.CreateDirectory(lockDirectory);
            EnsureNoReparsePoints(lockDirectory);
            var lockIdentity = Path.GetFullPath(assetPath);
            if (Path.DirectorySeparatorChar == '\\')
            {
                lockIdentity = lockIdentity.ToLowerInvariant();
            }
            var lockName = HashBytes(StrictUtf8.GetBytes(lockIdentity)) + ".lock";
            var lockPath = Path.Combine(lockDirectory, lockName);
            EnsureNoReparsePoints(lockPath);
            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough);
                try
                {
                    VerifyOpenedPath(stream.SafeFileHandle, lockPath);
                    EnsureNoReparsePoints(lockPath);
                    return stream;
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
            catch (IOException exception)
            {
                throw new InvalidOperationException(
                    "Another DCC-MCP writer is already updating this Unity text asset.",
                    exception);
            }
        }

        private static byte[] ReadBoundedBytes(string path)
        {
            EnsureNoReparsePoints(path);
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan))
            {
                VerifyOpenedPath(stream.SafeFileHandle, path);
                EnsureNoReparsePoints(path);
                if (stream.Length > MaxTextAssetBytes)
                {
                    throw new InvalidOperationException(
                        "Unity text asset exceeds the 256 KiB limit.");
                }
                var bytes = new byte[(int)stream.Length];
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0)
                    {
                        throw new InvalidOperationException(
                            "Unity text asset changed while it was being read.");
                    }
                    offset += read;
                }
                if (stream.ReadByte() != -1)
                {
                    throw new InvalidOperationException(
                        "Unity text asset exceeds the 256 KiB limit.");
                }
                return bytes;
            }
        }

        private static void VerifyOpenedPath(SafeFileHandle handle, string expectedPath)
        {
            if (Path.DirectorySeparatorChar != '\\')
            {
                return;
            }
            var buffer = new StringBuilder(512);
            uint length;
            while (true)
            {
                length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not verify the opened Unity project path.");
                }
                if (length < buffer.Capacity)
                {
                    break;
                }
                if (length > 32767)
                {
                    throw new InvalidOperationException("Opened Unity project path is too long.");
                }
                buffer.Capacity = (int)length + 1;
            }

            var resolved = buffer.ToString();
            const string uncPrefix = @"\\?\UNC\";
            const string devicePrefix = @"\\?\";
            if (resolved.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
            {
                resolved = @"\\" + resolved.Substring(uncPrefix.Length);
            }
            else if (resolved.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase))
            {
                resolved = resolved.Substring(devicePrefix.Length);
            }
            if (!string.Equals(
                Path.GetFullPath(resolved),
                Path.GetFullPath(expectedPath),
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Opened path resolved through a reparse point: " + expectedPath);
            }
        }

        private static void EnsureEditorIdleAndEditing(string operation)
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                throw new InvalidOperationException(
                    operation + " cannot start while Unity is compiling or updating assets.");
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException(operation + " cannot start in Play Mode.");
            }
        }

        private static void EnsureTransportSafeText(string content)
        {
            foreach (var character in content)
            {
                if ((character < 0x20 && character != '\t' && character != '\n' && character != '\r')
                    || character == 0x7f)
                {
                    throw new InvalidOperationException(
                        "Unity text assets cannot contain JSON-unsafe control characters.");
                }
            }
        }

        private static void RequireSourceWriteGate()
        {
            if (!string.Equals(
                Environment.GetEnvironmentVariable("DCC_MCP_UNITY_ALLOW_SOURCE_WRITES"),
                "1",
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Source writes require operator environment DCC_MCP_UNITY_ALLOW_SOURCE_WRITES=1.");
            }
        }

        private static JObject LoadStore()
        {
            var serialized = SessionState.GetString(SessionStateKey, string.Empty);
            if (string.IsNullOrEmpty(serialized))
            {
                return new JObject { ["jobs"] = new JArray() };
            }
            try
            {
                var store = ParseStore(serialized);
                if (!(store["jobs"] is JArray))
                {
                    throw new InvalidOperationException("jobs is missing");
                }
                return store;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "Unity job SessionState is invalid; restart the Editor before submitting mutations.",
                    exception);
            }
        }

        internal static JObject ParseStore(string serialized)
        {
            using (var stringReader = new StringReader(serialized))
            using (var jsonReader = new JsonTextReader(stringReader))
            {
                // The ledger owns ISO-8601 strings. Newtonsoft otherwise promotes them to
                // Date tokens during JObject.Parse, and the later explicit string cast loses
                // the round-trip representation on older Unity/Mono cultures.
                jsonReader.DateParseHandling = DateParseHandling.None;
                return JObject.Load(jsonReader);
            }
        }

        private static void RestoreTestCallbackAfterReload()
        {
            try
            {
                foreach (JObject job in (JArray)LoadStore()["jobs"])
                {
                    if ((string)job["kind"] != "project.run_tests"
                        || (string)job["state"] != "running"
                        || (string)job["phase"] != "waiting_for_tests"
                        || job["test_report_path"] == null)
                    {
                        continue;
                    }
                    DccMcpTestRunner.EnsureCallback(
                        (string)job["request_id"],
                        (string)job["test_report_path"]);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "DCC-MCP Unity could not restore its Test Runner callback: "
                    + exception.Message);
            }
        }

        private static void SaveStore(JObject store)
        {
            SessionState.SetString(SessionStateKey, store.ToString(Formatting.None));
        }

        private static JObject PublicJob(JObject job)
        {
            var result = new JObject
            {
                ["request_id"] = job["request_id"].DeepClone(),
                ["kind"] = job["kind"].DeepClone(),
                ["state"] = job["state"].DeepClone(),
                ["phase"] = job["phase"].DeepClone(),
                ["created_at_utc"] = job["created_at_utc"].DeepClone(),
                ["updated_at_utc"] = job["updated_at_utc"].DeepClone(),
            };
            if (job["result"] != null)
            {
                result["result"] = job["result"].DeepClone();
            }
            if (job["error"] != null)
            {
                result["error"] = job["error"].DeepClone();
            }
            return result;
        }

        private static void Succeed(JObject job, JObject result)
        {
            job["state"] = "succeeded";
            job["phase"] = "complete";
            job["result"] = result;
            job.Remove("parameters");
            job.Remove("capture_path");
            job.Remove("capture_relative_path");
            job.Remove("capture_started_at_utc");
            job.Remove("capture_after_frame");
            job.Remove("idle_since_utc");
            job.Remove("build_scenes");
            job.Remove("test_report_path");
            job.Remove("test_report_relative_path");
            Touch(job);
        }

        private static void Fail(JObject job, string message)
        {
            if ((string)job["kind"] == "project.run_tests")
            {
                DccMcpTestRunner.ReleaseCallback((string)job["request_id"]);
            }
            job["state"] = "failed";
            job["phase"] = "complete";
            job["error"] = message;
            job.Remove("parameters");
            job.Remove("capture_path");
            job.Remove("capture_relative_path");
            job.Remove("capture_started_at_utc");
            job.Remove("capture_after_frame");
            job.Remove("idle_since_utc");
            job.Remove("build_scenes");
            job.Remove("test_report_path");
            job.Remove("test_report_relative_path");
            Touch(job);
        }

        private static void Touch(JObject job)
        {
            job["updated_at_utc"] = DateTime.UtcNow.ToString("O");
        }

        private static string Fingerprint(string kind, JObject parameters)
        {
            var payload = kind + "\n" + Canonicalize(parameters).ToString(Formatting.None);
            return HashBytes(Encoding.UTF8.GetBytes(payload));
        }

        private static JToken Canonicalize(JToken token)
        {
            if (token is JObject sourceObject)
            {
                var properties = new List<JProperty>(sourceObject.Properties());
                properties.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
                var targetObject = new JObject();
                foreach (var property in properties)
                {
                    targetObject[property.Name] = Canonicalize(property.Value);
                }
                return targetObject;
            }
            if (token is JArray sourceArray)
            {
                var targetArray = new JArray();
                foreach (var item in sourceArray)
                {
                    targetArray.Add(Canonicalize(item));
                }
                return targetArray;
            }
            return token.DeepClone();
        }

        private static string NormalizeRequestId(JToken token)
        {
            if (token == null || token.Type != JTokenType.String)
            {
                throw new InvalidOperationException("request_id must be a UUID string.");
            }
            var value = (string)token;
            Guid parsed;
            if (!Guid.TryParseExact(value, "D", out parsed))
            {
                throw new InvalidOperationException("request_id must use canonical UUID format.");
            }
            return parsed.ToString("D");
        }

        private static string RequireString(JObject parameters, string name)
        {
            var value = RequireStringToken(parameters, name);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(name + " is required.");
            }
            return value;
        }

        private static string RequireStringToken(JObject parameters, string name)
        {
            var token = parameters[name];
            if (token == null || token.Type != JTokenType.String)
            {
                throw new InvalidOperationException(name + " must be a string.");
            }
            return (string)token;
        }

        private static void RequireOnlyProperties(JObject parameters, params string[] allowed)
        {
            foreach (var property in parameters.Properties())
            {
                if (Array.IndexOf(allowed, property.Name) < 0)
                {
                    throw new InvalidOperationException(
                        "Unexpected parameter for typed Unity command: " + property.Name);
                }
            }
        }

        private static bool IsLowerSha256(string value)
        {
            if (value == null || value.Length != 64)
            {
                return false;
            }
            foreach (var character in value)
            {
                if (!(character >= '0' && character <= '9')
                    && !(character >= 'a' && character <= 'f'))
                {
                    return false;
                }
            }
            return true;
        }

        private static string HashBytes(byte[] bytes)
        {
            using (var sha256 = SHA256.Create())
            {
                return BitConverter.ToString(sha256.ComputeHash(bytes))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }

        private static bool TimedOut(JObject job, TimeSpan limit)
        {
            return TimedOutSince((string)job["created_at_utc"], limit);
        }

        private static bool TimedOutSince(string timestamp, TimeSpan limit)
        {
            DateTime started;
            return !DateTime.TryParse(
                       timestamp,
                       null,
                       System.Globalization.DateTimeStyles.RoundtripKind,
                       out started)
                || DateTime.UtcNow - started.ToUniversalTime() > limit;
        }

        private static void FocusGameView()
        {
            var gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            if (gameViewType == null)
            {
                throw new InvalidOperationException("Unity Game View is unavailable.");
            }
            var gameView = EditorWindow.GetWindow(gameViewType);
            if (gameView == null)
            {
                throw new InvalidOperationException("Unity Game View could not be opened.");
            }
            gameView.Show();
            gameView.Focus();
            gameView.Repaint();
        }

        internal static bool TryDecodePng(string path, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }
            byte[] bytes;
            if (!TryReadBoundedCaptureBytes(path, out bytes))
            {
                return false;
            }
            if (!IsStructurallyValidPng(bytes))
            {
                return false;
            }
            var declaredWidth = ReadBigEndianUInt32(bytes, 16);
            var declaredHeight = ReadBigEndianUInt32(bytes, 20);
            if (!AreCaptureDimensionsAllowed(declaredWidth, declaredHeight))
            {
                return false;
            }
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!ImageConversion.LoadImage(texture, bytes, true)
                    || texture.width <= 0
                    || texture.height <= 0
                    || texture.width != declaredWidth
                    || texture.height != declaredHeight)
                {
                    return false;
                }
                width = texture.width;
                height = texture.height;
                return true;
            }
            catch (UnityException)
            {
                return false;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static bool TryReadBoundedCaptureBytes(string path, out byte[] bytes)
        {
            bytes = null;
            try
            {
                EnsureNoReparsePoints(path);
                using (var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.SequentialScan))
                {
                    VerifyOpenedPath(stream.SafeFileHandle, path);
                    EnsureNoReparsePoints(path);
                    return TryReadExactCapture(stream, stream.Length, out bytes);
                }
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (Win32Exception)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        internal static bool TryReadExactCapture(
            Stream stream,
            long expectedLength,
            out byte[] bytes)
        {
            bytes = null;
            if (stream == null || expectedLength < 33 || expectedLength > MaxCaptureBytes)
            {
                return false;
            }
            try
            {
                var buffer = new byte[(int)expectedLength];
                var offset = 0;
                while (offset < buffer.Length)
                {
                    var read = stream.Read(buffer, offset, buffer.Length - offset);
                    if (read == 0)
                    {
                        return false;
                    }
                    offset += read;
                }
                if (stream.ReadByte() != -1)
                {
                    return false;
                }
                bytes = buffer;
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        internal static bool AreCaptureDimensionsAllowed(uint width, uint height)
        {
            return width > 0
                && height > 0
                && width <= MaxCaptureDimension
                && height <= MaxCaptureDimension
                && (long)width * height <= MaxCapturePixels;
        }

        private static bool IsStructurallyValidPng(byte[] bytes)
        {
            if (bytes.Length < 33
                || bytes[0] != 137
                || bytes[1] != 80
                || bytes[2] != 78
                || bytes[3] != 71
                || bytes[4] != 13
                || bytes[5] != 10
                || bytes[6] != 26
                || bytes[7] != 10)
            {
                return false;
            }
            var offset = 8;
            var foundImageData = false;
            while (offset + 12 <= bytes.Length)
            {
                var length = ReadBigEndianUInt32(bytes, offset);
                var chunkEnd = (long)offset + 12 + length;
                if (chunkEnd > bytes.Length)
                {
                    return false;
                }
                var isHeader = bytes[offset + 4] == (byte)'I'
                    && bytes[offset + 5] == (byte)'H'
                    && bytes[offset + 6] == (byte)'D'
                    && bytes[offset + 7] == (byte)'R';
                var isImageData = bytes[offset + 4] == (byte)'I'
                    && bytes[offset + 5] == (byte)'D'
                    && bytes[offset + 6] == (byte)'A'
                    && bytes[offset + 7] == (byte)'T';
                var isEnd = bytes[offset + 4] == (byte)'I'
                    && bytes[offset + 5] == (byte)'E'
                    && bytes[offset + 6] == (byte)'N'
                    && bytes[offset + 7] == (byte)'D';
                if ((offset == 8 && (!isHeader || length != 13))
                    || (offset != 8 && isHeader))
                {
                    return false;
                }
                var crcOffset = checked(offset + 8 + (int)length);
                if (ReadBigEndianUInt32(bytes, crcOffset)
                    != ComputePngCrc(bytes, offset + 4, checked(4 + (int)length)))
                {
                    return false;
                }
                foundImageData |= isImageData;
                offset = checked((int)chunkEnd);
                if (isEnd)
                {
                    return length == 0 && foundImageData && offset == bytes.Length;
                }
            }
            return false;
        }

        private static uint ComputePngCrc(byte[] bytes, int offset, int count)
        {
            var crc = uint.MaxValue;
            for (var index = offset; index < offset + count; index++)
            {
                var tableIndex = (byte)(crc ^ bytes[index]);
                crc = PngCrcTable[tableIndex] ^ (crc >> 8);
            }
            return ~crc;
        }

        private static uint[] CreatePngCrcTable()
        {
            var table = new uint[256];
            for (var index = 0; index < table.Length; index++)
            {
                var value = (uint)index;
                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value >> 1) ^ ((value & 1) == 0 ? 0u : 0xedb88320u);
                }
                table[index] = value;
            }
            return table;
        }

        private static uint ReadBigEndianUInt32(byte[] bytes, int offset)
        {
            return ((uint)bytes[offset] << 24)
                | ((uint)bytes[offset + 1] << 16)
                | ((uint)bytes[offset + 2] << 8)
                | bytes[offset + 3];
        }

        private static string NormalizeSeparators(string path)
        {
            return path.Replace('\\', '/');
        }

        private static void Stop()
        {
            EditorApplication.update -= Tick;
            AssemblyReloadEvents.beforeAssemblyReload -= Stop;
        }
    }
}
