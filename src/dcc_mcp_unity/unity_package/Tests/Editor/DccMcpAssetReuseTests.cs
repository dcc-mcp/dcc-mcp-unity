using System;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DccMcp.Unity.Tests
{
    public sealed class DccMcpAssetReuseTests
    {
        private string Folder;
        private Exception nestedSaveError;

        [SetUp]
        public void SetUp()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var folderGuid = AssetDatabase.CreateFolder("Assets", "DccMcpAssetReuseProbe_" + Guid.NewGuid().ToString("N"));
            Folder = AssetDatabase.GUIDToAssetPath(folderGuid);
            Assert.IsNotEmpty(Folder);
            var source = new GameObject("Reusable module");
            PrefabUtility.SaveAsPrefabAsset(source, Folder + "/Module.prefab");
            UnityEngine.Object.DestroyImmediate(source);
            EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(), Folder + "/Base.unity");
        }

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (!string.IsNullOrEmpty(Folder)) AssetDatabase.DeleteAsset(Folder);
        }

        [Test]
        public void DiscoverInstantiateReopenAndExactSaveCapabilityRoundTrip()
        {
            var found = DccMcpCommands.Execute("assets.find", new JObject { ["root"] = Folder });
            Assert.AreEqual(1, (int)found["total"]);
            var asset = DccMcpCommands.Execute("assets.inspect", new JObject { ["path"] = Folder + "/Module.prefab" });
            Assert.AreEqual("not_verified", (string)asset["license_status"]);
            var scene = DccMcpCommands.Execute("scene.create_isolated", new JObject());
            Assert.AreEqual(JTokenType.String, scene["scene_handle"].Type);
            var inspected = DccMcpCommands.Execute("scene.inspect", new JObject());
            Assert.AreEqual((string)scene["scene_handle"], (string)inspected["scene_handle"]);
            var input = new JObject { ["scene_handle"] = scene["scene_handle"],
                ["path"] = asset["path"], ["expected_dependency_hash"] = asset["dependency_hash"] };
            var created = DccMcpCommands.Execute("scene.instantiate_prefab", input);
            Assert.AreEqual(1, (int)created["root_count"]);
            Assert.Throws<InvalidOperationException>(() => DccMcpCommands.Execute("scene.reopen_exact", scene));
            var save = new JObject { ["scene_handle"] = scene["scene_handle"], ["path"] = Folder + "/Layout.unity" };
            if (DccMcpDirtyAssets.SupportsDirtyQuery)
            {
                RequireCleanFixture();
                var saved = DccMcpCommands.Execute("scene.save_exact", save);
                Assert.AreEqual("loaded_persistent_objects", (string)saved["asset_preflight_scope"]);
                Assert.IsNotEmpty((string)saved["asset_preflight_capture_id"]);
            }
            else
            {
                Assert.That(() => DccMcpCommands.Execute("scene.save_exact", save),
                    Throws.TypeOf<InvalidOperationException>().With.Message.Contains("dirty_query_unsupported"));
                Assert.IsFalse(File.Exists(Folder + "/Layout.unity"));
                // Seed a clean file through this disposable fixture, not the
                // refused tool, so older Editors still exercise reopen identity.
                EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(), Folder + "/Layout.unity");
            }
            var reopened = DccMcpCommands.Execute("scene.reopen_exact", scene);
            Assert.AreEqual(JTokenType.String, reopened["scene_handle"].Type);
            Assert.AreNotEqual((string)scene["scene_handle"], (string)reopened["scene_handle"]);
            Assert.Throws<InvalidOperationException>(() => DccMcpCommands.Execute("scene.save_exact", save));
            Assert.Throws<InvalidOperationException>(() => DccMcpCommands.Execute("scene.instantiate_prefab", input));
            Assert.Throws<InvalidOperationException>(() => DccMcpCommands.Execute("scene.reopen_exact", scene));
            Assert.AreEqual(1, (int)reopened["root_count"]);
            var loaded = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            Assert.AreEqual(Folder + "/Module.prefab", PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(loaded.GetRootGameObjects()[0]));
        }

        [Test]
        public void RejectChangedDependenciesAndUnsafeSaveWithoutMutation()
        {
            var scene = DccMcpCommands.Execute("scene.create_isolated", new JObject());
            Assert.Throws<InvalidOperationException>(() => DccMcpCommands.Execute("scene.instantiate_prefab", new JObject {
                ["scene_handle"] = scene["scene_handle"], ["path"] = Folder + "/Module.prefab",
                ["expected_dependency_hash"] = new string('0', 32) }));
            Assert.Throws<InvalidOperationException>(() => DccMcpCommands.Execute("scene.save_exact", new JObject {
                ["scene_handle"] = scene["scene_handle"], ["path"] = "Assets/../Outside.unity" }));
            Assert.AreEqual(0, UnityEngine.SceneManagement.SceneManager.GetActiveScene().rootCount);
        }

        [Test]
        public void SaveExactRefusesDirtyAssetsWithoutSavingOrDiscardingThem()
        {
            var sibling = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var siblingBytes = File.ReadAllBytes(sibling.path);
            new GameObject("Unsaved sibling root");
            EditorSceneManager.MarkSceneDirty(sibling);
            var material = new Material(Shader.Find("Hidden/InternalErrorShader"));
            var materialPath = Folder + "/Sentinel.mat";
            AssetDatabase.CreateAsset(material, materialPath);
            AssetDatabase.SaveAssets();
            var materialBytes = File.ReadAllBytes(materialPath);
            material.renderQueue = 3123;
            EditorUtility.SetDirty(material);
            var target = DccMcpCommands.Execute("scene.create_isolated", new JObject());
            new GameObject("Saved target root");
            EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            UnityEngine.SceneManagement.SceneManager.SetActiveScene(sibling);
            Assert.AreEqual(3123, material.renderQueue, "The fixture must retain its unsaved value before save.");
            var dirtyQuery = typeof(EditorUtility).GetMethod("IsDirty", new[] { typeof(UnityEngine.Object) });
            if (dirtyQuery != null)
                Assert.IsTrue((bool)dirtyQuery.Invoke(null, new object[] { material }), "The fixture must be dirty before save.");

            CollectionAssert.AreEqual(materialBytes, File.ReadAllBytes(materialPath), "Creation must not save the sentinel.");
            var targetPath = Folder + "/Target.unity";
            Assert.Throws<InvalidOperationException>(() => DccMcpCommands.Execute("scene.save_exact", new JObject {
                ["scene_handle"] = target["scene_handle"], ["path"] = targetPath }));

            Assert.IsFalse(File.Exists(targetPath));
            Assert.IsFalse(DccMcpAssetReuse.IsSavingScene, "The guard must reset after refusal.");
            Assert.IsTrue(sibling.isDirty);
            Assert.AreEqual(3123, material.renderQueue);
            // Unity 2018.4 has no public IsDirty(Object) API. Disk isolation and
            // the unsaved in-memory value are still checked on that version.
            if (dirtyQuery != null) Assert.IsTrue((bool)dirtyQuery.Invoke(null, new object[] { material }));
            Assert.AreEqual(sibling, UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            CollectionAssert.AreEqual(siblingBytes, File.ReadAllBytes(sibling.path));
            CollectionAssert.AreEqual(materialBytes, File.ReadAllBytes(materialPath));
        }

        [Test]
        public void CleanAssetSavePreservesTheActiveDirtySiblingOrRefusesUnsupportedEditors()
        {
            var sibling = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var siblingBytes = File.ReadAllBytes(sibling.path);
            new GameObject("Unsaved sibling root");
            EditorSceneManager.MarkSceneDirty(sibling);
            var target = DccMcpCommands.Execute("scene.create_isolated", new JObject());
            var targetScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            new GameObject("Saved target root");
            EditorSceneManager.MarkSceneDirty(targetScene);
            UnityEngine.SceneManagement.SceneManager.SetActiveScene(sibling);
            var path = Folder + "/CleanTarget.unity";
            var input = new JObject { ["scene_handle"] = target["scene_handle"], ["path"] = path };
            if (DccMcpDirtyAssets.SupportsDirtyQuery)
            {
                RequireCleanFixture();
                var result = DccMcpCommands.Execute("scene.save_exact", input);
                Assert.IsFalse((bool)result["dirty"]);
                Assert.IsTrue(File.Exists(path));
            }
            else
            {
                Assert.That(() => DccMcpCommands.Execute("scene.save_exact", input),
                    Throws.TypeOf<InvalidOperationException>().With.Message.Contains("dirty_query_unsupported"));
                Assert.IsTrue(targetScene.isDirty);
                Assert.IsFalse(File.Exists(path));
            }
            Assert.IsTrue(sibling.isDirty);
            Assert.AreEqual(sibling, UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            CollectionAssert.AreEqual(siblingBytes, File.ReadAllBytes(sibling.path));
        }

        private static JObject CleanSnapshot()
        {
            return new JObject { ["schema_version"] = 1, ["complete"] = true, ["incomplete"] = false,
                ["identity_complete"] = true, ["dirty_persistent_count"] = 0, ["items_count"] = 0,
                ["items"] = new JArray(), ["error_code"] = JValue.CreateNull(), ["error_message"] = JValue.CreateNull() };
        }

        private static void RequireCleanFixture()
        {
            var snapshot = DccMcpDirtyAssets.Inspect(new JObject());
            Assert.DoesNotThrow(() => DccMcpAssetReuse.RequireCleanLoadedAssets(snapshot),
                "Disposable fixture did not establish clean loaded assets: " + snapshot.ToString());
        }

        private void TryNestedSave(UnityEngine.SceneManagement.Scene scene, string path)
        {
            try { DccMcpCommands.Execute("scene.save", new JObject()); }
            catch (Exception exception) { nestedSaveError = exception; }
        }

        [Test]
        public void NativeSaveCallbacksCannotReenterDccCommands()
        {
            var scene = DccMcpCommands.Execute("scene.inspect", new JObject());
            var input = new JObject { ["scene_handle"] = scene["scene_handle"], ["path"] = Folder + "/Base.unity" };
            if (!DccMcpDirtyAssets.SupportsDirtyQuery)
            {
                Assert.That(() => DccMcpCommands.Execute("scene.save_exact", input),
                    Throws.TypeOf<InvalidOperationException>().With.Message.Contains("dirty_query_unsupported"));
                Assert.IsFalse(DccMcpAssetReuse.IsSavingScene);
                return;
            }
            RequireCleanFixture();
            nestedSaveError = null;
            EditorSceneManager.sceneSaving += TryNestedSave;
            try { DccMcpCommands.Execute("scene.save_exact", input); }
            finally { EditorSceneManager.sceneSaving -= TryNestedSave; }
            Assert.That(nestedSaveError, Is.TypeOf<InvalidOperationException>());
            StringAssert.Contains("reentrancy", nestedSaveError.Message);
            Assert.IsFalse(DccMcpAssetReuse.IsSavingScene);
            Assert.IsTrue((bool)DccMcpCommands.Execute("host.ping", new JObject())["host_dispatch_ready"]);
        }

        [TestCase("unsupported")]
        [TestCase("incomplete")]
        [TestCase("unknown_identity")]
        [TestCase("budget")]
        [TestCase("unknown_count")]
        [TestCase("dirty")]
        [TestCase("missing_items")]
        public void ExactSavePreflightRejectsUnknownOrNonemptySnapshots(string change)
        {
            var snapshot = CleanSnapshot();
            if (change == "unsupported") snapshot["error_code"] = "dirty_query_unsupported";
            if (change == "incomplete") snapshot["complete"] = false;
            if (change == "unknown_identity") snapshot["identity_complete"] = false;
            if (change == "budget") snapshot["error_code"] = "response_budget_exceeded";
            if (change == "unknown_count") snapshot["dirty_persistent_count"] = JValue.CreateNull();
            if (change == "dirty") snapshot["dirty_persistent_count"] = 1;
            if (change == "missing_items") snapshot.Remove("items");
            Assert.Throws<InvalidOperationException>(() => DccMcpAssetReuse.RequireCleanLoadedAssets(snapshot));
            Assert.DoesNotThrow(() => DccMcpAssetReuse.RequireCleanLoadedAssets(CleanSnapshot()));
        }

        [Test]
        public void SceneHandlesPreserveVersionSpecificRange()
        {
            Assert.AreEqual("42", DccMcpSceneIdentity.Require(new JObject { ["scene_handle"] = 42 }));
            Assert.AreEqual("42", DccMcpSceneIdentity.Require(new JObject { ["scene_handle"] = "00042" }));
            Assert.Throws<InvalidOperationException>(() => DccMcpSceneIdentity.Require(
                new JObject { ["scene_handle"] = 9007199254740993L }));
#if UNITY_6000_5_OR_NEWER
            Assert.AreEqual("18446744073709551615", DccMcpSceneIdentity.Require(
                new JObject { ["scene_handle"] = "18446744073709551615" }));
            Assert.Throws<InvalidOperationException>(() => DccMcpSceneIdentity.Require(
                new JObject { ["scene_handle"] = "18446744073709551616" }));
#else
            Assert.AreEqual("-2147483648", DccMcpSceneIdentity.Require(
                new JObject { ["scene_handle"] = "-2147483648" }));
            Assert.Throws<InvalidOperationException>(() => DccMcpSceneIdentity.Require(
                new JObject { ["scene_handle"] = "2147483648" }));
#endif
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("-")]
        [TestCase("+42")]
        [TestCase(" 42")]
        [TestCase("42.0")]
        [TestCase("1e3")]
        [TestCase("not-a-scene")]
        public void RejectMalformedSceneHandles(string value)
        {
            Assert.Throws<InvalidOperationException>(() => DccMcpSceneIdentity.Require(
                new JObject { ["scene_handle"] = value }));
        }
    }
}
