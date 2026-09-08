using System;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DccMcp.Unity.Tests
{
    public sealed class DccMcpAssetReuseTests
    {
        private const string Folder = "Assets/DccMcpAssetReuseProbe";

        [SetUp]
        public void SetUp()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.CreateFolder("Assets", "DccMcpAssetReuseProbe");
            var source = new GameObject("Reusable module");
            PrefabUtility.SaveAsPrefabAsset(source, Folder + "/Module.prefab");
            UnityEngine.Object.DestroyImmediate(source);
            EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(), Folder + "/Base.unity");
        }

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(Folder);
        }

        [Test]
        public void DiscoverInstantiateSaveReopenPreservesPrefabConnection()
        {
            var found = DccMcpCommands.Execute("assets.find", new JObject { ["root"] = Folder });
            Assert.AreEqual(1, (int)found["total"]);
            var asset = DccMcpCommands.Execute("assets.inspect", new JObject { ["path"] = Folder + "/Module.prefab" });
            Assert.AreEqual("not_verified", (string)asset["license_status"]);
            var scene = DccMcpCommands.Execute("scene.create_isolated", new JObject());
            var input = new JObject { ["scene_handle"] = scene["scene_handle"],
                ["path"] = asset["path"], ["expected_dependency_hash"] = asset["dependency_hash"] };
            var created = DccMcpCommands.Execute("scene.instantiate_prefab", input);
            Assert.AreEqual(1, (int)created["root_count"]);
            Assert.Throws<InvalidOperationException>(() => DccMcpCommands.Execute("scene.reopen_exact", scene));
            var save = new JObject { ["scene_handle"] = scene["scene_handle"], ["path"] = Folder + "/Layout.unity" };
            DccMcpCommands.Execute("scene.save_exact", save);
            var reopened = DccMcpCommands.Execute("scene.reopen_exact", scene);
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
    }
}
