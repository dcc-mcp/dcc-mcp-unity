using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DccMcp.Unity
{
    internal static class DccMcpAssetReuse
    {
        internal static bool IsSavingScene { get; private set; }

        private static string AssetPath(JObject p)
        {
            var path = (string)p["path"];
            if (string.IsNullOrEmpty(path) || path.Length > 512 || path.Contains("\\")
                || !(path.StartsWith("Assets/", StringComparison.Ordinal)
                    || path.StartsWith("Packages/", StringComparison.Ordinal)))
                throw new InvalidOperationException("Expected a project or installed package asset path.");
            foreach (var part in path.Split('/'))
                if (part == "" || part == "." || part == ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new InvalidOperationException("Invalid asset path segment.");
            return path;
        }

        private static JObject Metadata(string path)
        {
            var type = AssetDatabase.GetMainAssetTypeAtPath(path);
            return new JObject {
                ["path"] = path, ["guid"] = AssetDatabase.AssetPathToGUID(path),
                ["type"] = type == null ? null : type.FullName,
                ["source"] = path.StartsWith("Packages/", StringComparison.Ordinal) ? "installed_package" : "project",
                ["license_status"] = "not_verified",
                ["dependency_hash"] = AssetDatabase.GetAssetDependencyHash(path).ToString()
            };
        }

        internal static JObject Find(JObject p)
        {
            var root = (string)p["root"] ?? "Assets";
            if (root != "Assets" && root != "Packages") AssetPath(new JObject { ["path"] = root });
            if (!AssetDatabase.IsValidFolder(root)) throw new InvalidOperationException("Asset search root does not exist.");
            var query = (string)p["query"] ?? "t:Prefab";
            if (query.Length > 256) throw new InvalidOperationException("Query exceeds 256 characters.");
            var limit = (int?)p["limit"] ?? 50;
            if (limit < 1 || limit > 200) throw new InvalidOperationException("limit must be 1..200.");
            var guids = AssetDatabase.FindAssets(query, new[] { root });
            Array.Sort(guids, StringComparer.Ordinal);
            var items = new JArray();
            for (var i = 0; i < Math.Min(limit, guids.Length); i++)
                items.Add(Metadata(AssetDatabase.GUIDToAssetPath(guids[i])));
            return new JObject { ["assets"] = items, ["total"] = guids.Length, ["truncated"] = guids.Length > limit };
        }

        internal static JObject Inspect(JObject p)
        {
            var path = AssetPath(p);
            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path))) throw new InvalidOperationException("Asset does not exist.");
            var result = Metadata(path);
            var dependencies = AssetDatabase.GetDependencies(path, true);
            Array.Sort(dependencies, StringComparer.Ordinal);
            var items = new JArray();
            for (var i = 0; i < Math.Min(200, dependencies.Length); i++) items.Add(dependencies[i]);
            result["dependencies"] = items;
            result["dependencies_total"] = dependencies.Length;
            result["dependencies_truncated"] = dependencies.Length > 200;
            return result;
        }

        private static Scene ExactScene(JObject p)
        {
            var handle = DccMcpSceneIdentity.Require(p);
            var scene = default(Scene);
            for (var i = 0; i < SceneManager.sceneCount; i++)
                if (DccMcpSceneIdentity.GetId(SceneManager.GetSceneAt(i)) == handle) scene = SceneManager.GetSceneAt(i);
            if (!scene.IsValid() || !scene.isLoaded) throw new InvalidOperationException("Scene handle is stale.");
            return scene;
        }

        private static JObject SceneInfo(Scene scene)
        {
            return new JObject { ["scene_handle"] = DccMcpSceneIdentity.GetId(scene), ["path"] = scene.path,
                ["dirty"] = scene.isDirty, ["root_count"] = scene.rootCount };
        }

        internal static JObject Instantiate(JObject p)
        {
            var scene = ExactScene(p);
            var path = AssetPath(p);
            var expected = (string)p["expected_dependency_hash"];
            if (string.IsNullOrEmpty(expected) || expected != AssetDatabase.GetAssetDependencyHash(path).ToString())
                throw new InvalidOperationException("Asset dependencies changed; inspect the asset again.");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null || !PrefabUtility.IsPartOfPrefabAsset(prefab))
                throw new InvalidOperationException("Asset is not an instantiable prefab or model.");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            Undo.RegisterCreatedObjectUndo(instance, "Instantiate prefab");
            EditorSceneManager.MarkSceneDirty(scene);
            var result = SceneInfo(scene);
            result["instance_id"] = DccMcpObjectIdentity.GetId(instance);
            result["name"] = instance.name;
            result["asset_path"] = path;
            return result;
        }

        internal static JObject CreateScene()
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
                if (string.IsNullOrEmpty(SceneManager.GetSceneAt(i).path))
                    throw new InvalidOperationException("Save untitled scenes with save_exact_scene before creating an additive scene.");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            return SceneInfo(scene);
        }

        internal static JObject SaveScene(JObject p)
        {
            var scene = ExactScene(p);
            var path = AssetPath(p);
            DccMcpJobs.EnsureProjectAssetPathSafe(path);
            if (!path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Scene path must end in .unity.");
            if (File.Exists(path) && !string.Equals(scene.path, path, StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to overwrite another scene asset.");
            if (!AssetDatabase.IsValidFolder(Path.GetDirectoryName(path).Replace('\\', '/')))
                throw new InvalidOperationException("Destination folder must already exist.");
            if (IsSavingScene) throw new InvalidOperationException("Reentrant exact scene saves are disabled.");
            IsSavingScene = true;
            try
            {
                // Unity's native scene save can flush unrelated dirty assets.
                // Capture immediately in this main-thread call; never trust an
                // earlier client snapshot or save/restore those assets afterward.
                var snapshot = DccMcpDirtyAssets.Inspect(new JObject());
                RequireCleanLoadedAssets(snapshot);
                if (!EditorSceneManager.SaveScene(scene, path)) throw new InvalidOperationException("Scene save failed.");
                var result = SceneInfo(scene);
                result["asset_preflight_capture_id"] = snapshot["capture_id"];
                result["asset_preflight_scope"] = "loaded_persistent_objects";
                return result;
            }
            finally
            {
                IsSavingScene = false;
            }
        }

        internal static void RequireCleanLoadedAssets(JObject snapshot)
        {
            if (snapshot == null
                || !JToken.DeepEquals(snapshot["schema_version"], new JValue(1))
                || !JToken.DeepEquals(snapshot["complete"], new JValue(true))
                || !JToken.DeepEquals(snapshot["incomplete"], new JValue(false))
                || !JToken.DeepEquals(snapshot["identity_complete"], new JValue(true))
                || snapshot["error_code"] == null || snapshot["error_code"].Type != JTokenType.Null
                || snapshot["error_message"] == null || snapshot["error_message"].Type != JTokenType.Null)
                throw new InvalidOperationException("Exact scene save cannot establish loaded persistent asset state: "
                    + (snapshot == null ? "snapshot_unavailable" : (string)snapshot["error_code"] ?? "snapshot_incomplete") + ".");
            var items = snapshot["items"] as JArray;
            if (!JToken.DeepEquals(snapshot["dirty_persistent_count"], new JValue(0))
                || !JToken.DeepEquals(snapshot["items_count"], new JValue(0))
                || items == null || items.Count != 0)
                throw new InvalidOperationException("Exact scene save refuses existing loaded persistent dirty assets. "
                    + "Inspect them and explicitly resolve their changes before saving the scene.");
        }

        internal static JObject ReopenScene(JObject p)
        {
            var scene = ExactScene(p);
            var path = scene.path;
            if (scene.isDirty || string.IsNullOrEmpty(path) || !File.Exists(path))
                throw new InvalidOperationException("Reopen requires a clean saved scene.");
            if (SceneManager.sceneCount < 2) throw new InvalidOperationException("Keep another scene loaded before reopening.");
            if (!EditorSceneManager.CloseScene(scene, true)) throw new InvalidOperationException("Scene close failed.");
            var reopened = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            SceneManager.SetActiveScene(reopened);
            return SceneInfo(reopened);
        }
    }
}
