using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
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
                RequireSafeFixture();
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
        public void ReopenRefusesAnUnloadedSiblingWithoutClosingTheTarget()
        {
            var target = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var targetId = DccMcpSceneIdentity.GetId(target);
            var targetPath = target.path;
            var targetBytes = File.ReadAllBytes(targetPath);
            var siblingPath = Folder + "/UnloadedSibling.unity";
            var sibling = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            Assert.IsTrue(EditorSceneManager.SaveScene(sibling, siblingPath));
            var siblingBytes = File.ReadAllBytes(siblingPath);
            Assert.IsTrue(UnityEngine.SceneManagement.SceneManager.SetActiveScene(target));
            Assert.IsTrue(EditorSceneManager.CloseScene(sibling, false));
            var unloaded = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(siblingPath);
            Assert.IsTrue(unloaded.IsValid());
            Assert.IsFalse(unloaded.isLoaded);
            Assert.AreEqual(2, UnityEngine.SceneManagement.SceneManager.sceneCount);
            Assert.IsTrue(target.isLoaded);
            Assert.IsFalse(target.isDirty);

            Assert.That(() => DccMcpCommands.Execute("scene.reopen_exact",
                new JObject { ["scene_handle"] = targetId }),
                Throws.TypeOf<InvalidOperationException>().With.Message.Contains("Keep another scene loaded"));

            var retained = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(targetPath);
            Assert.IsTrue(retained.IsValid());
            Assert.AreEqual(targetId, DccMcpSceneIdentity.GetId(retained));
            Assert.IsTrue(retained.isLoaded);
            Assert.IsFalse(retained.isDirty);
            Assert.AreEqual(target, UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            Assert.AreEqual(2, UnityEngine.SceneManagement.SceneManager.sceneCount);
            Assert.IsFalse(UnityEngine.SceneManagement.SceneManager.GetSceneByPath(siblingPath).isLoaded);
            CollectionAssert.AreEqual(targetBytes, File.ReadAllBytes(targetPath));
            CollectionAssert.AreEqual(siblingBytes, File.ReadAllBytes(siblingPath));
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
            var targetScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(targetScene);
            UnityEngine.SceneManagement.SceneManager.SetActiveScene(sibling);
            Assert.AreEqual(3123, material.renderQueue, "The fixture must retain its unsaved value before save.");
            var dirtyQuery = typeof(EditorUtility).GetMethod("IsDirty", new[] { typeof(UnityEngine.Object) });
            if (dirtyQuery != null)
                Assert.IsTrue((bool)dirtyQuery.Invoke(null, new object[] { material }), "The fixture must be dirty before save.");

            CollectionAssert.AreEqual(materialBytes, File.ReadAllBytes(materialPath), "Creation must not save the sentinel.");
            var targetPath = Folder + "/Target.unity";
            var refusal = Assert.Throws<InvalidOperationException>(() => DccMcpCommands.Execute("scene.save_exact", new JObject {
                ["scene_handle"] = target["scene_handle"], ["path"] = targetPath }));
            StringAssert.Contains(DccMcpDirtyAssets.SupportsDirtyQuery ? materialPath : "dirty_query_unsupported", refusal.Message);

            Assert.IsFalse(File.Exists(targetPath));
            Assert.IsFalse(DccMcpAssetReuse.IsSavingScene, "The guard must reset after refusal.");
            Assert.IsTrue(sibling.isDirty);
            Assert.IsTrue(targetScene.isDirty);
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
            var siblingRoot = new GameObject("Unsaved sibling root");
            EditorSceneManager.MarkSceneDirty(sibling);
            var target = DccMcpCommands.Execute("scene.create_isolated", new JObject());
            var targetScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var targetRoot = new GameObject("Saved target root");
            EditorSceneManager.MarkSceneDirty(targetScene);
            UnityEngine.SceneManagement.SceneManager.SetActiveScene(sibling);
            var path = Folder + "/CleanTarget.unity";
            var input = new JObject { ["scene_handle"] = target["scene_handle"], ["path"] = path };
            if (DccMcpDirtyAssets.SupportsDirtyQuery)
            {
#if UNITY_2019_3_OR_NEWER
                SceneVisibilityManager.instance.Hide(siblingRoot, false);
                SceneVisibilityManager.instance.DisablePicking(siblingRoot, false);
                SceneVisibilityManager.instance.Hide(targetRoot, false);
                SceneVisibilityManager.instance.DisablePicking(targetRoot, false);
#endif
                var fixtureSnapshot = RequireSafeFixture();
                var packageFiles = ImmutableScriptFileHashes(fixtureSnapshot);
                var visibilityBefore = FileHashOrMissing("Library/SceneVisibilityState.asset");
                var result = DccMcpCommands.Execute("scene.save_exact", input);
                Debug.Log("Exact-save editor-local visibility cache SHA256 before=" + visibilityBefore
                    + " after=" + FileHashOrMissing("Library/SceneVisibilityState.asset"));
                Debug.Log("Exact-save classification: " + result["asset_preflight_classification"].ToString());
                foreach (var file in packageFiles)
                    Assert.AreEqual(file.Value, FileHashOrMissing(file.Key), "Immutable package script/meta bytes must not change.");
                Debug.Log("Exact-save immutable package source/meta files preserved: " + packageFiles.Count);
                Assert.IsFalse((bool)result["dirty"]);
                Assert.IsTrue(File.Exists(path));
                Assert.AreEqual("Library/SceneVisibilityState.asset",
                    (string)result["asset_preflight_classification"]["editor_local_state_may_write"]);
#if UNITY_2019_3_OR_NEWER
                Assert.IsTrue(SceneVisibilityManager.instance.IsHidden(siblingRoot));
                Assert.IsTrue(SceneVisibilityManager.instance.IsPickingDisabled(siblingRoot));
                Assert.IsTrue(SceneVisibilityManager.instance.IsHidden(targetRoot));
                Assert.IsTrue(SceneVisibilityManager.instance.IsPickingDisabled(targetRoot));
#endif
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

        private static string FileHashOrMissing(string path)
        {
            if (!File.Exists(path)) return "missing";
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
        }

        private static Dictionary<string, string> ImmutableScriptFileHashes(JObject snapshot)
        {
            var hashes = new Dictionary<string, string>();
#if UNITY_2019_3_OR_NEWER
            foreach (var item in (JArray)snapshot["items"])
            {
                var path = (string)item["path"];
                if (!path.StartsWith("Packages/", StringComparison.Ordinal)
                    || (string)item["type"] != typeof(MonoScript).AssemblyQualifiedName) continue;
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path);
                if (package == null || !DccMcpAssetReuse.IsImmutablePackageSource(package.source)) continue;
                Assert.IsTrue(path.StartsWith(package.assetPath + "/", StringComparison.Ordinal));
                var physical = Path.Combine(package.resolvedPath, path.Substring(package.assetPath.Length + 1));
                foreach (var file in new[] { physical, physical + ".meta" })
                {
                    var hash = FileHashOrMissing(file);
                    Assert.AreNotEqual("missing", hash, "The immutable script fixture must have source/meta bytes.");
                    hashes[file] = hash;
                }
            }
#endif
            return hashes;
        }

        [TestCase("hidden_material")]
        [TestCase("subasset")]
        [TestCase("prefab")]
        public void DirtyUserContentCannotUseEngineExceptions(string kind)
        {
            UnityEngine.Object value;
            string path;
            if (kind == "prefab")
            {
                path = Folder + "/Module.prefab";
                value = AssetDatabase.LoadAssetAtPath<GameObject>(path).transform;
            }
            else
            {
                path = Folder + "/Protected.asset";
                var material = new Material(Shader.Find("Hidden/InternalErrorShader"));
                AssetDatabase.CreateAsset(material, path);
                value = material;
                if (kind == "subasset")
                {
                    value = new Texture2D(1, 1) { name = "Protected child" };
                    AssetDatabase.AddObjectToAsset(value, material);
                }
                AssetDatabase.SaveAssets(); // Baseline setup in this disposable fixture only.
            }
            var bytes = File.ReadAllBytes(path);
            var scene = DccMcpCommands.Execute("scene.inspect", new JObject());
            var scenePath = (string)scene["scene_path"];
            Assert.AreEqual(Folder + "/Base.unity", scenePath);
            var sceneBytes = File.ReadAllBytes(scenePath);
            if (kind == "prefab") ((Transform)value).localPosition = Vector3.one;
            else if (kind == "subasset") ((Texture2D)value).anisoLevel = 7;
            else
            {
                value.hideFlags = HideFlags.HideAndDontSave;
                ((Material)value).renderQueue = 3123;
            }
            EditorUtility.SetDirty(value);
            var refusal = Assert.Throws<InvalidOperationException>(() => DccMcpCommands.Execute("scene.save_exact",
                new JObject { ["scene_handle"] = scene["scene_handle"], ["path"] = scenePath }));
            StringAssert.Contains(DccMcpDirtyAssets.SupportsDirtyQuery ? path : "dirty_query_unsupported", refusal.Message);
            if (kind == "prefab") Assert.AreEqual(Vector3.one, ((Transform)value).localPosition);
            else if (kind == "subasset") Assert.AreEqual(7, ((Texture2D)value).anisoLevel);
            else Assert.AreEqual(3123, ((Material)value).renderQueue);
            if (DccMcpDirtyAssets.SupportsDirtyQuery)
                Assert.IsTrue((bool)typeof(EditorUtility).GetMethod("IsDirty", new[] { typeof(UnityEngine.Object) })
                    .Invoke(null, new object[] { value }));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
            CollectionAssert.AreEqual(sceneBytes, File.ReadAllBytes(scenePath));
        }

        [Test]
        public void ExactSaveRejectsAlteredOrDuplicateEngineDescriptors()
        {
            var original = DccMcpDirtyAssets.Inspect(new JObject());
            if (!DccMcpDirtyAssets.SupportsDirtyQuery)
            {
                Assert.Throws<InvalidOperationException>(() => DccMcpAssetReuse.RequireSafeLoadedAssets(original));
                return;
            }
            Assert.Greater(((JArray)original["items"]).Count, 0, "Exercise real ambient engine objects.");
            foreach (var change in new[] { "path", "type", "object_id", "field_errors", "duplicate" })
            {
                var snapshot = (JObject)original.DeepClone();
                var items = (JArray)snapshot["items"];
                var item = (JObject)items[0];
                if (change == "path") item["path"] = "Library/not-a-builtin-resource";
                if (change == "type") item["type"] = "UnityEditor.SceneVisibilityState, UntrustedAssembly";
                if (change == "object_id") item["object_id"] = "not-an-object";
                if (change == "field_errors") item["field_errors"] = new JObject { ["path"] = "unknown" };
                if (change == "duplicate")
                {
                    items.Add(item.DeepClone());
                    snapshot["dirty_persistent_count"] = items.Count;
                    snapshot["items_count"] = items.Count;
                }
                Assert.Throws<InvalidOperationException>(() => DccMcpAssetReuse.RequireSafeLoadedAssets(snapshot), change);
            }
        }

        [Test]
        public void ExactSavePreflightDoesNotReloadAnUnloadedCapturedAsset()
        {
            if (!DccMcpDirtyAssets.SupportsDirtyQuery)
            {
                Assert.Throws<InvalidOperationException>(() => DccMcpAssetReuse.RequireSafeLoadedAssets(
                    DccMcpDirtyAssets.Inspect(new JObject())));
                return;
            }
            var material = new Material(Shader.Find("Hidden/InternalErrorShader"));
            AssetDatabase.CreateAsset(material, Folder + "/Unloaded.mat");
            AssetDatabase.SaveAssets();
            EditorUtility.SetDirty(material);
            var id = DccMcpObjectIdentity.GetId(material);
            var snapshot = DccMcpDirtyAssets.Inspect(new JObject());
            // Intentionally unload only this disposable probe to test stale capture.
            Resources.UnloadAsset(material);
            Assert.That(() => DccMcpAssetReuse.RequireSafeLoadedAssets(snapshot),
                Throws.TypeOf<InvalidOperationException>().With.Message.Contains("changed dirty-object descriptor"));
            foreach (var loaded in Resources.FindObjectsOfTypeAll<UnityEngine.Object>())
                if (loaded != null) Assert.AreNotEqual(id, DccMcpObjectIdentity.GetId(loaded), "Preflight must not reload the probe.");
        }

#if UNITY_2019_3_OR_NEWER
        [Test]
        public void WritableAndUnknownPackageSourcesNeverUseTheImmutableException()
        {
            Assert.IsFalse(DccMcpAssetReuse.IsImmutablePackageSource(UnityEditor.PackageManager.PackageSource.Local));
            Assert.IsFalse(DccMcpAssetReuse.IsImmutablePackageSource(UnityEditor.PackageManager.PackageSource.Embedded));
            Assert.IsFalse(DccMcpAssetReuse.IsImmutablePackageSource(UnityEditor.PackageManager.PackageSource.Unknown));
            Assert.IsTrue(DccMcpAssetReuse.IsImmutablePackageSource(UnityEditor.PackageManager.PackageSource.Registry));
        }
#endif

        private static JObject CleanSnapshot()
        {
            return new JObject { ["schema_version"] = 1, ["complete"] = true, ["incomplete"] = false,
                ["identity_complete"] = true, ["dirty_persistent_count"] = 0, ["items_count"] = 0,
                ["items"] = new JArray(), ["error_code"] = JValue.CreateNull(), ["error_message"] = JValue.CreateNull() };
        }

        private static JObject RequireSafeFixture()
        {
            var snapshot = DccMcpDirtyAssets.Inspect(new JObject());
            Assert.DoesNotThrow(() => DccMcpAssetReuse.RequireSafeLoadedAssets(snapshot),
                "Disposable fixture contains an unclassified or user-owned dirty asset: " + snapshot.ToString());
            return snapshot;
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
            RequireSafeFixture();
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
            Assert.Throws<InvalidOperationException>(() => DccMcpAssetReuse.RequireSafeLoadedAssets(snapshot));
            Assert.DoesNotThrow(() => DccMcpAssetReuse.RequireSafeLoadedAssets(CleanSnapshot()));
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
