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
        public void DiscoverInstantiateSaveReopenPreservesPrefabConnection()
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
            DccMcpCommands.Execute("scene.save_exact", save);
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
        public void SaveExactPreservesActiveDirtySiblingAndDirtyAsset()
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
            material.name = "Unsaved material edit";
            EditorUtility.SetDirty(material);
            var target = DccMcpCommands.Execute("scene.create_isolated", new JObject());
            new GameObject("Saved target root");
            EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            UnityEngine.SceneManagement.SceneManager.SetActiveScene(sibling);

            var saved = DccMcpCommands.Execute("scene.save_exact", new JObject {
                ["scene_handle"] = target["scene_handle"], ["path"] = Folder + "/Target.unity" });

            Assert.IsFalse((bool)saved["dirty"]);
            Assert.IsTrue(sibling.isDirty);
            Assert.AreEqual("Unsaved material edit", material.name);
            // Unity 2018.4 has no public IsDirty(Object) API. Disk isolation and
            // the unsaved in-memory value are still checked on that version.
            var dirtyQuery = typeof(EditorUtility).GetMethod("IsDirty", new[] { typeof(UnityEngine.Object) });
            if (dirtyQuery != null) Assert.IsTrue((bool)dirtyQuery.Invoke(null, new object[] { material }));
            Assert.AreEqual(sibling, UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            CollectionAssert.AreEqual(siblingBytes, File.ReadAllBytes(sibling.path));
            CollectionAssert.AreEqual(materialBytes, File.ReadAllBytes(materialPath));
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
