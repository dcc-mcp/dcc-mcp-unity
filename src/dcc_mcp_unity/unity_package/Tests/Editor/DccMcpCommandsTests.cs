using System;
using System.Collections;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace DccMcp.Unity.Tests
{
    public sealed class DccMcpCommandsTests
    {
        private const string SourceWriteGate = "DCC_MCP_UNITY_ALLOW_SOURCE_WRITES";
        private string originalJobStore;
        private string originalSourceWriteGate;
        private EditorBuildSettingsScene[] originalBuildScenes;

        [SetUp]
        public void SetUp()
        {
            originalSourceWriteGate = Environment.GetEnvironmentVariable(SourceWriteGate);
            originalBuildScenes = EditorBuildSettings.scenes;
            originalJobStore = SessionState.GetString(DccMcpJobs.SessionStateKey, string.Empty);
            SessionState.EraseString(DccMcpJobs.SessionStateKey);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable(SourceWriteGate, originalSourceWriteGate);
            EditorBuildSettings.scenes = originalBuildScenes;
            if (string.IsNullOrEmpty(originalJobStore))
            {
                SessionState.EraseString(DccMcpJobs.SessionStateKey);
            }
            else
            {
                SessionState.SetString(DccMcpJobs.SessionStateKey, originalJobStore);
            }
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (AssetDatabase.IsValidFolder("Assets/DccMcpJobTests"))
            {
                AssetDatabase.DeleteAsset("Assets/DccMcpJobTests");
            }
            else
            {
                var testDirectory = Path.Combine(Application.dataPath, "DccMcpJobTests");
                if (Directory.Exists(testDirectory))
                {
                    Directory.Delete(testDirectory, true);
                }
            }
            AssetDatabase.Refresh();
        }

        [Test]
        public void CreateTransformAndInspectSceneRoundTrips()
        {
            var created = DccMcpCommands.Execute(
                "scene.create_game_object",
                new JObject { ["name"] = "CI Probe" });
            var instanceIdToken = created["instance_id"];
            var instanceId = instanceIdToken.ToString();
#if UNITY_6000_5_OR_NEWER
            Assert.That(instanceIdToken.Type, Is.EqualTo(JTokenType.String));
#else
            Assert.That(instanceIdToken.Type, Is.EqualTo(JTokenType.Integer));
#endif

            DccMcpCommands.Execute(
                "scene.set_transform",
                new JObject
                {
                    ["instance_id"] = instanceIdToken.DeepClone(),
                    ["position"] = new JArray(1, 2, 3),
                });

            var gameObject = DccMcpObjectIdentity.Resolve(instanceId) as GameObject;
            Assert.That(gameObject, Is.Not.Null);
            Assert.That(gameObject.transform.position, Is.EqualTo(new Vector3(1, 2, 3)));

            var snapshot = DccMcpCommands.Execute(
                "scene.inspect",
                new JObject { ["max_nodes"] = 10 });
            var roots = (JArray)snapshot["roots"];
            Assert.That(roots, Has.Count.EqualTo(1));
            Assert.That((string)roots[0]["name"], Is.EqualTo("CI Probe"));
            Assert.That((bool)snapshot["truncated"], Is.False);

            Assert.That(
                () => DccMcpCommands.Execute(
                    "scene.set_transform",
                    new JObject
                    {
                        ["instance_id"] = "1e3",
                        ["position"] = new JArray(0, 0, 0),
                    }),
                Throws.TypeOf<InvalidOperationException>()
                    .With.Message.EqualTo("instance_id is not a valid Unity object ID."));
        }

        [Test]
        public void CreateGameObjectCanBeUndone()
        {
            var created = DccMcpCommands.Execute(
                "scene.create_game_object",
                new JObject { ["name"] = "Undo Probe" });
            var instanceId = created["instance_id"].ToString();

            Undo.PerformUndo();

            Assert.That(DccMcpObjectIdentity.Resolve(instanceId), Is.Null);
        }

        [Test]
        public void InspectSceneRejectsOutOfRangeNodeLimit()
        {
            Assert.That(
                () => DccMcpCommands.Execute(
                    "scene.inspect",
                    new JObject { ["max_nodes"] = 0 }),
                Throws.TypeOf<InvalidOperationException>()
                    .With.Message.EqualTo("max_nodes must be between 1 and 5000."));
        }

        [Test]
        public void TextAssetWritesRequireTheOperatorGateAndBoundedAssetPath()
        {
            Environment.SetEnvironmentVariable(SourceWriteGate, null);
            Assert.That(
                () => DccMcpCommands.Execute(
                    "assets.upsert_text",
                    new JObject
                    {
                        ["request_id"] = Guid.NewGuid().ToString("D"),
                        ["path"] = "Assets/DccMcpJobTests/Probe.cs",
                        ["content"] = "class Probe {}",
                        ["expected_sha256"] = "absent",
                    }),
                Throws.TypeOf<InvalidOperationException>()
                    .With.Message.Contains("DCC_MCP_UNITY_ALLOW_SOURCE_WRITES=1"));

            Assert.That(
                () => DccMcpCommands.Execute(
                    "assets.read_text",
                    new JObject { ["path"] = "Assets/../ProjectSettings/ProjectVersion.txt" }),
                Throws.TypeOf<InvalidOperationException>());

            var testDirectory = Path.Combine(Application.dataPath, "DccMcpJobTests");
            Directory.CreateDirectory(testDirectory);
            File.WriteAllText(Path.Combine(testDirectory, "Control.txt"), "unsafe\0text");
            Assert.That(
                () => DccMcpCommands.Execute(
                    "assets.read_text",
                    new JObject { ["path"] = "Assets/DccMcpJobTests/Control.txt" }),
                Throws.TypeOf<InvalidOperationException>()
                    .With.Message.Contains("control characters"));
        }

        [Test]
        public void JobSubmissionRejectsUnknownInputsUtf8OverflowAndConcurrency()
        {
            Environment.SetEnvironmentVariable(SourceWriteGate, "1");
            var requestId = Guid.NewGuid().ToString("D");
            var write = new JObject
            {
                ["request_id"] = requestId,
                ["path"] = "Assets/DccMcpJobTests/Probe.txt",
                ["content"] = "text",
                ["expected_sha256"] = "absent",
                ["extra"] = true,
            };
            Assert.That(
                () => DccMcpCommands.Execute("assets.upsert_text", write),
                Throws.TypeOf<InvalidOperationException>()
                    .With.Message.Contains("Unexpected parameter"));

            write.Remove("extra");
            write["content"] = "unsafe\0text";
            Assert.That(
                () => DccMcpCommands.Execute("assets.upsert_text", write),
                Throws.TypeOf<InvalidOperationException>()
                    .With.Message.Contains("control characters"));

            write["content"] = new string('\u00e9', 131073);
            Assert.That(
                () => DccMcpCommands.Execute("assets.upsert_text", write),
                Throws.TypeOf<InvalidOperationException>()
                    .With.Message.Contains("256 KiB"));

            write["content"] = "text";
            DccMcpCommands.Execute("assets.upsert_text", write);
            Assert.That(
                () => DccMcpCommands.Execute(
                    "project.refresh_and_compile",
                    new JObject { ["request_id"] = Guid.NewGuid().ToString("D") }),
                Throws.TypeOf<InvalidOperationException>()
                    .With.Message.Contains("queued or running"));
        }

        [Test]
        public void JobStoreParsingPreservesRoundTripUtcTimestampsAsStrings()
        {
            var timestamp = DateTime.UtcNow.ToString("O");
            var serialized = new JObject
            {
                ["jobs"] = new JArray
                {
                    new JObject
                    {
                        ["created_at_utc"] = timestamp,
                        ["updated_at_utc"] = timestamp,
                    },
                },
            }.ToString(Newtonsoft.Json.Formatting.None);

            var parsed = DccMcpJobs.ParseStore(serialized);
            var job = (JObject)((JArray)parsed["jobs"])[0];

            Assert.That(job["created_at_utc"].Type, Is.EqualTo(JTokenType.String));
            Assert.That((string)job["created_at_utc"], Is.EqualTo(timestamp));
            Assert.That(job["updated_at_utc"].Type, Is.EqualTo(JTokenType.String));
        }

        [Test]
        public void TestRunnerParametersAreBoundedAndCanonical()
        {
            var normalized = DccMcpTestRunner.NormalizeParameters(new JObject
            {
                ["request_id"] = "778e72dd-e536-4ff8-aad0-9b752ab61c3b",
                ["test_mode"] = "edit_mode",
                ["test_names"] = new JArray("Z.Tests.Last", "A.Tests.First", "A.Tests.First"),
            });

            Assert.That((string)normalized["test_mode"], Is.EqualTo("edit_mode"));
            Assert.That(
                ((JArray)normalized["test_names"]).Values<string>(),
                Is.EqualTo(new[] { "A.Tests.First", "Z.Tests.Last" }));

            Assert.That(
                () => DccMcpTestRunner.NormalizeParameters(new JObject
                {
                    ["request_id"] = Guid.NewGuid().ToString("D"),
                    ["test_mode"] = "batch_mode",
                }),
                Throws.TypeOf<InvalidOperationException>()
                    .With.Message.Contains("edit_mode or play_mode"));

            var tooMany = new JArray();
            for (var index = 0; index < 129; index++)
            {
                tooMany.Add("Tests.Case" + index);
            }
            Assert.That(
                () => DccMcpTestRunner.NormalizeParameters(new JObject
                {
                    ["request_id"] = Guid.NewGuid().ToString("D"),
                    ["test_mode"] = "edit_mode",
                    ["test_names"] = tooMany,
                }),
                Throws.TypeOf<InvalidOperationException>()
                    .With.Message.Contains("at most 128"));
        }

        [Test]
        public void UnityTestFrameworkReflectionContractIsAvailable()
        {
            Assert.That(
                () => DccMcpTestFrameworkBridge.ValidateContract(),
                Throws.Nothing);
        }

        [Test]
        public void TestRunnerCallbackCanBeRehydratedAndReleasedByRequestId()
        {
            var requestId = Guid.NewGuid().ToString("D");
            var reportPath = Path.Combine(
                Path.GetDirectoryName(Application.dataPath) ?? string.Empty,
                "Library",
                "DccMcpCallbackProbe-" + requestId + ".xml");

            try
            {
                DccMcpTestFrameworkBridge.EnsureCallback(requestId, reportPath);
                DccMcpTestFrameworkBridge.EnsureCallback(requestId, reportPath);
                Assert.That(
                    DccMcpTestFrameworkBridge.IsCallbackRegistered(requestId),
                    Is.True);
            }
            finally
            {
                DccMcpTestFrameworkBridge.ReleaseCallback(requestId);
            }

            Assert.That(
                DccMcpTestFrameworkBridge.IsCallbackRegistered(requestId),
                Is.False);
        }

        [Test]
        public void TestRunnerReportSummaryRequiresRunnableTestsAndPreservesCounts()
        {
            var projectPath = Path.GetDirectoryName(Application.dataPath) ?? string.Empty;
            var directory = Path.Combine(
                projectPath,
                "Library",
                "DccMcpUnityTestReport-" + Guid.NewGuid().ToString("N"));
            var reportPath = Path.Combine(directory, "results.xml");
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(
                    reportPath,
                    "<test-run result=\"Failed\" total=\"3\" passed=\"2\" failed=\"1\""
                    + " inconclusive=\"0\" skipped=\"0\" duration=\"1.25\" />");

                var result = DccMcpTestRunner.SummarizeReport(reportPath);
                Assert.That((int)result["total"], Is.EqualTo(3));
                Assert.That((int)result["passed"], Is.EqualTo(2));
                Assert.That((int)result["failed"], Is.EqualTo(1));
                Assert.That((string)result["outcome"], Is.EqualTo("failed"));

                File.WriteAllText(
                    reportPath,
                    "<test-run result=\"Passed\" total=\"0\" passed=\"0\" failed=\"0\""
                    + " inconclusive=\"0\" skipped=\"0\" duration=\"0\" />");
                Assert.That(
                    () => DccMcpTestRunner.SummarizeReport(reportPath),
                    Throws.TypeOf<InvalidOperationException>()
                        .With.Message.Contains("did not match any tests"));
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
        }

        [Test]
        public void TextAssetLimitFitsBothBridgeDirectionsAfterJsonEscaping()
        {
            Assert.That(
                DccMcpBridge.MaxEscapedTextEnvelopeBytes,
                Is.LessThanOrEqualTo(DccMcpBridge.MaxInboundMessageBytes));
            Assert.That(
                DccMcpBridge.MaxEscapedTextEnvelopeBytes,
                Is.LessThanOrEqualTo(DccMcpBridge.MaxOutboundMessageBytes));
        }

        [Test]
        public void EnabledBuildScenesMustExistAndHaveNoUnsavedOpenChanges()
        {
            var originalScenes = EditorBuildSettings.scenes;
            var testDirectory = Path.Combine(Application.dataPath, "DccMcpJobTests");
            try
            {
                Directory.CreateDirectory(testDirectory);
                var invalidScenePath = "Assets/DccMcpJobTests/NotAScene.txt";
                File.WriteAllText(Path.Combine(testDirectory, "NotAScene.txt"), "not a scene");
                AssetDatabase.ImportAsset(invalidScenePath);
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(invalidScenePath, true) };
                Assert.That(
                    () => DccMcpJobs.ValidateEnabledBuildScenes(),
                    Throws.TypeOf<InvalidOperationException>()
                        .With.Message.Contains("saved .unity asset"));

                var scenePath = "Assets/DccMcpJobTests/BuildScene.unity";
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Assert.That(EditorSceneManager.SaveScene(scene, scenePath), Is.True);
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(scenePath, true) };

                Assert.That(DccMcpJobs.ValidateEnabledBuildScenes(), Is.EqualTo(new[] { scenePath }));

                new GameObject("Unsaved change");
                EditorSceneManager.MarkSceneDirty(scene);
                Assert.That(
                    () => DccMcpJobs.ValidateEnabledBuildScenes(),
                    Throws.TypeOf<InvalidOperationException>()
                        .With.Message.Contains("unsaved changes"));
            }
            finally
            {
                EditorBuildSettings.scenes = originalScenes;
                AssetDatabase.SaveAssets();
            }
        }

        [Test]
        public void PngValidationRequiresARealDecodableImage()
        {
            var projectPath = Path.GetDirectoryName(Application.dataPath) ?? string.Empty;
            var directory = Path.Combine(
                projectPath,
                "Library",
                "DccMcpUnityPng-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var validPath = Path.Combine(directory, "valid.png");
            var fakePath = Path.Combine(directory, "fake.png");
            var texture = new Texture2D(2, 3, TextureFormat.RGBA32, false);
            try
            {
                File.WriteAllBytes(validPath, ImageConversion.EncodeToPNG(texture));
                File.WriteAllBytes(fakePath, new byte[]
                {
                    137, 80, 78, 71, 13, 10, 26, 10,
                    0, 0, 0, 13, 73, 72, 68, 82,
                    0, 0, 0, 1, 0, 0, 0, 1,
                    0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130,
                });

                int width;
                int height;
                Assert.That(DccMcpJobs.TryDecodePng(validPath, out width, out height), Is.True);
                Assert.That(width, Is.EqualTo(2));
                Assert.That(height, Is.EqualTo(3));
                Assert.That(DccMcpJobs.TryDecodePng(fakePath, out width, out height), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void PngValidationRejectsFilesAboveTheCaptureBudgetBeforeDecode()
        {
            var projectPath = Path.GetDirectoryName(Application.dataPath) ?? string.Empty;
            var directory = Path.Combine(
                projectPath,
                "Library",
                "DccMcpUnityOversize-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "oversize.png");
            try
            {
                Directory.CreateDirectory(directory);
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                {
                    stream.SetLength(DccMcpJobs.MaxCaptureBytes + 1L);
                }

                int width;
                int height;
                Assert.That(DccMcpJobs.TryDecodePng(path, out width, out height), Is.False);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
        }

        [Test]
        public void CaptureReaderRejectsLengthChangesAfterTheInitialCheck()
        {
            byte[] bytes;
            using (var shortened = new MemoryStream(new byte[32], false))
            {
                Assert.That(
                    DccMcpJobs.TryReadExactCapture(shortened, 33, out bytes),
                    Is.False);
                Assert.That(bytes, Is.Null);
            }

            using (var grown = new MemoryStream(new byte[34], false))
            {
                Assert.That(
                    DccMcpJobs.TryReadExactCapture(grown, 33, out bytes),
                    Is.False);
                Assert.That(bytes, Is.Null);
            }
        }

        [Test]
        public void CaptureReaderFailsClosedWhenTheStreamReadThrows()
        {
            byte[] bytes;
            using (var stream = new ThrowingReadStream(33))
            {
                Assert.That(
                    DccMcpJobs.TryReadExactCapture(stream, stream.Length, out bytes),
                    Is.False);
                Assert.That(bytes, Is.Null);
            }
        }

        [Test]
        public void CaptureDimensionsAllowEightKUhdButRejectOversizedImages()
        {
            Assert.That(DccMcpJobs.AreCaptureDimensionsAllowed(7680, 4320), Is.True);
            Assert.That(DccMcpJobs.AreCaptureDimensionsAllowed(8193, 1), Is.False);
            Assert.That(DccMcpJobs.AreCaptureDimensionsAllowed(8192, 4097), Is.False);
            Assert.That(DccMcpJobs.AreCaptureDimensionsAllowed(0, 1080), Is.False);
        }

        [Test]
        public void CaptureRejectsPausedPlayModeWithAnActionableError()
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => DccMcpJobs.EnsureCaptureCanAdvance(true, true));

            Assert.That(exception.Message, Does.Contain("Resume Play Mode"));
            Assert.That(exception.Message, Does.Contain("new request_id"));
        }

        [Test]
        public void PlayModeWaitingStateIsPersistedBeforeTheTransitionRequest()
        {
            var timestamp = DateTime.UtcNow.ToString("O");
            var job = new JObject
            {
                ["phase"] = "starting",
                ["updated_at_utc"] = timestamp,
            };
            var store = new JObject { ["jobs"] = new JArray { job } };
            var observedPersistedWaitingState = false;

            DccMcpJobs.PersistPlayModeTransition(
                store,
                job,
                () =>
                {
                    var persisted = DccMcpJobs.ParseStore(
                        SessionState.GetString(DccMcpJobs.SessionStateKey, string.Empty));
                    var persistedJob = (JObject)((JArray)persisted["jobs"])[0];
                    observedPersistedWaitingState =
                        (string)persistedJob["phase"] == "waiting_for_play_mode";
                });

            Assert.That(observedPersistedWaitingState, Is.True);
        }

        [TestCase(false, false, false, true)]
        [TestCase(false, false, true, false)]
        [TestCase(false, true, false, false)]
        [TestCase(false, true, true, false)]
        [TestCase(true, false, false, false)]
        [TestCase(true, false, true, false)]
        [TestCase(true, true, false, false)]
        [TestCase(true, true, true, true)]
        public void PlayModeCompletionRequiresBothEditorStatesToMatchTarget(
            bool play,
            bool isPlaying,
            bool isPlayingOrWillChangePlaymode,
            bool expected)
        {
            Assert.That(
                DccMcpJobs.IsPlayModeTransitionComplete(
                    play,
                    isPlaying,
                    isPlayingOrWillChangePlaymode),
                Is.EqualTo(expected));
        }

        [Test]
        public void CasConflictPreservesBackupWithoutOverwritingALaterExternalWrite()
        {
            var directory = Path.Combine(Application.dataPath, "DccMcpJobTests", "CasConflict");
            Directory.CreateDirectory(directory);
            var targetPath = Path.Combine(directory, "Probe.txt");
            var temporaryPath = targetPath + ".tmp";
            var backupPath = targetPath + ".backup";
            File.WriteAllText(targetPath, "external write B\n");
            File.WriteAllText(temporaryPath, "DCC-MCP desired write\n");

            Assert.That(
                () => DccMcpJobs.ReplaceExistingFileWithCas(
                    "Assets/DccMcpJobTests/CasConflict/Probe.txt",
                    temporaryPath,
                    targetPath,
                    backupPath,
                    new string('0', 64),
                    () => File.WriteAllText(targetPath, "later external write C\n")),
                Throws.TypeOf<InvalidOperationException>()
                    .With.Message.Contains("preserved conflict backup"));
            Assert.That(File.ReadAllText(targetPath), Is.EqualTo("later external write C\n"));
            Assert.That(File.ReadAllText(backupPath), Is.EqualTo("external write B\n"));
        }

        [UnityTest]
        public IEnumerator TextAssetJobUsesCasAndRequestIdDeduplication()
        {
            Environment.SetEnvironmentVariable(SourceWriteGate, "1");
            var requestId = Guid.NewGuid().ToString("D");
            var parameters = new JObject
            {
                ["request_id"] = requestId,
                ["path"] = "Assets/DccMcpJobTests/Probe.txt",
                ["content"] = "bounded text\n",
                ["expected_sha256"] = "absent",
            };

            var submitted = DccMcpCommands.Execute("assets.upsert_text", parameters);
            Assert.That((string)submitted["state"], Is.EqualTo("queued"));

            JObject status = null;
            for (var frame = 0; frame < 10; frame++)
            {
                yield return null;
                status = DccMcpCommands.Execute(
                    "jobs.inspect",
                    new JObject { ["request_id"] = requestId });
                if ((string)status["state"] == "succeeded")
                {
                    break;
                }
            }
            Assert.That((string)status["state"], Is.EqualTo("succeeded"));

            var read = DccMcpCommands.Execute(
                "assets.read_text",
                new JObject { ["path"] = "Assets/DccMcpJobTests/Probe.txt" });
            Assert.That((string)read["content"], Is.EqualTo("bounded text\n"));
            Assert.That(((string)read["sha256"]).Length, Is.EqualTo(64));

            var update = (JObject)parameters.DeepClone();
            update["request_id"] = Guid.NewGuid().ToString("D");
            update["content"] = "updated text\n";
            update["expected_sha256"] = read["sha256"].DeepClone();
            DccMcpCommands.Execute("assets.upsert_text", update);
            for (var frame = 0; frame < 10; frame++)
            {
                yield return null;
                status = DccMcpCommands.Execute(
                    "jobs.inspect",
                    new JObject { ["request_id"] = update["request_id"].DeepClone() });
                if ((string)status["state"] == "succeeded")
                {
                    break;
                }
            }
            Assert.That((string)status["state"], Is.EqualTo("succeeded"));
            read = DccMcpCommands.Execute(
                "assets.read_text",
                new JObject { ["path"] = "Assets/DccMcpJobTests/Probe.txt" });
            Assert.That((string)read["content"], Is.EqualTo("updated text\n"));

            var duplicate = DccMcpCommands.Execute("assets.upsert_text", parameters);
            Assert.That((string)duplicate["state"], Is.EqualTo("succeeded"));

            var staleCreate = (JObject)parameters.DeepClone();
            staleCreate["request_id"] = Guid.NewGuid().ToString("D");
            var staleSubmitted = DccMcpCommands.Execute("assets.upsert_text", staleCreate);
            Assert.That((string)staleSubmitted["state"], Is.EqualTo("queued"));
            for (var frame = 0; frame < 10; frame++)
            {
                yield return null;
                status = DccMcpCommands.Execute(
                    "jobs.inspect",
                    new JObject { ["request_id"] = staleCreate["request_id"].DeepClone() });
                if ((string)status["state"] == "failed")
                {
                    break;
                }
            }
            Assert.That((string)status["state"], Is.EqualTo("failed"));
            Assert.That((string)status["error"], Does.Contain("CAS mismatch"));

            var changed = (JObject)parameters.DeepClone();
            changed["content"] = "different\n";
            Assert.That(
                () => DccMcpCommands.Execute("assets.upsert_text", changed),
                Throws.TypeOf<InvalidOperationException>()
                    .With.Message.Contains("same request_id"));
        }

        [Test]
        public void BridgeGuardSkipsImportWorkerAndBatchModeProcesses()
        {
            // Every EditMode test process runs with -runTests: on Unity 2020.2+
            // the worker probe is already false, and on older Unity the flag
            // exempts test runs from the batch-mode fallback. The guard must
            // stay false here so the bridge smoke and job pumps keep working.
            Assert.That(DccMcpBridge.IsImportWorkerOrBatchMode(), Is.False);
            // Genuine non-interactive batch processes (builds, import workers on
            // Unity versions without the worker probe) must still be skipped.
            Assert.That(DccMcpBridge.IsBatchProcessWithoutEditorTests(true, false), Is.True);
            Assert.That(DccMcpBridge.IsBatchProcessWithoutEditorTests(true, true), Is.False);
            Assert.That(DccMcpBridge.IsBatchProcessWithoutEditorTests(false, false), Is.False);
            Assert.That(DccMcpBridge.IsBatchProcessWithoutEditorTests(false, true), Is.False);
        }

        private sealed class ThrowingReadStream : MemoryStream
        {
            internal ThrowingReadStream(int length)
                : base(new byte[length], false)
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new IOException("Deterministic capture read failure.");
            }
        }
    }
}
