using System;
using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DccMcp.Unity
{
    internal static class DccMcpCommands
    {
        private const int DefaultSceneNodes = 1000;
        private const int MaxSceneNodes = 5000;
        private const int MaxSnapshotTextCharacters = 512;

        internal static JObject Execute(string method, JObject parameters)
        {
            EnsureEditorReady(method);
            var undoable = IsUndoable(method);
            var undoGroup = -1;
            if (undoable)
            {
                Undo.IncrementCurrentGroup();
                undoGroup = Undo.GetCurrentGroup();
            }

            try
            {
                JObject result;
                switch (method)
                {
                    case "project.inspect":
                        result = InspectProject();
                        break;
                    case "assets.refresh":
                        result = RefreshAssets();
                        break;
                    case "scene.inspect":
                        result = InspectScene(parameters);
                        break;
                    case "scene.create_game_object":
                        result = CreateGameObject(parameters);
                        break;
                    case "scene.set_transform":
                        result = SetTransform(parameters);
                        break;
                    case "scene.save":
                        result = SaveScene();
                        break;
                    case "editor.read_console":
                        result = DccMcpConsole.Read(parameters);
                        break;
                    default:
                        throw new InvalidOperationException("Unknown Unity action: " + method);
                }

                if (undoable)
                {
                    Undo.SetCurrentGroupName("DCC-MCP: " + method);
                    Undo.CollapseUndoOperations(undoGroup);
                }
                return result;
            }
            catch
            {
                if (undoable)
                {
                    Undo.RevertAllDownToGroup(undoGroup);
                }
                throw;
            }
        }

        private static bool IsUndoable(string method)
        {
            return method == "scene.create_game_object" || method == "scene.set_transform";
        }

        private static void EnsureEditorReady(string method)
        {
            var mutating = method == "assets.refresh"
                || method == "scene.create_game_object"
                || method == "scene.set_transform"
                || method == "scene.save";
            if (!mutating)
            {
                return;
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                throw new InvalidOperationException(
                    "Unity Editor is compiling or updating assets; retry after inspect_project reports ready.");
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException(
                    "Editor mutation commands are disabled while Unity is entering or in Play Mode.");
            }
        }

        private static JObject InspectProject()
        {
            var scene = SceneManager.GetActiveScene();
            var buildScenes = new JArray();
            foreach (var item in EditorBuildSettings.scenes)
            {
                buildScenes.Add(new JObject
                {
                    ["path"] = item.path,
                    ["enabled"] = item.enabled,
                });
            }

            return new JObject
            {
                ["name"] = Application.productName,
                ["project_path"] = System.IO.Path.GetDirectoryName(Application.dataPath),
                ["engine_version"] = Application.unityVersion,
                ["active_scene"] = scene.IsValid() ? scene.path : string.Empty,
                ["build_scenes"] = buildScenes,
                ["is_playing"] = EditorApplication.isPlaying,
                ["is_playing_or_will_change_playmode"] =
                    EditorApplication.isPlayingOrWillChangePlaymode,
                ["is_paused"] = EditorApplication.isPaused,
                ["is_compiling"] = EditorApplication.isCompiling,
                ["is_updating"] = EditorApplication.isUpdating,
            };
        }

        private static JObject RefreshAssets()
        {
            AssetDatabase.Refresh();
            return new JObject { ["refreshed"] = true };
        }

        private static JObject InspectScene(JObject parameters)
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                throw new InvalidOperationException("No active Unity scene.");
            }

            var maxNodes = ReadOptionalInt(parameters, "max_nodes", DefaultSceneNodes);
            if (maxNodes < 1 || maxNodes > MaxSceneNodes)
            {
                throw new InvalidOperationException("max_nodes must be between 1 and 5000.");
            }

            var roots = new JArray();
            var remaining = maxNodes;
            var truncated = false;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (remaining == 0)
                {
                    truncated = true;
                    break;
                }
                roots.Add(Snapshot(root, 0, ref remaining, ref truncated));
            }
            return new JObject
            {
                ["scene_name"] = scene.name,
                ["scene_path"] = scene.path,
                ["dirty"] = scene.isDirty,
                ["roots"] = roots,
                ["truncated"] = truncated,
            };
        }

        private static JObject Snapshot(
            GameObject gameObject,
            int depth,
            ref int remaining,
            ref bool truncated)
        {
            remaining--;
            var children = new JArray();
            if (depth < 8)
            {
                foreach (Transform child in gameObject.transform)
                {
                    if (remaining == 0)
                    {
                        truncated = true;
                        break;
                    }
                    children.Add(Snapshot(child.gameObject, depth + 1, ref remaining, ref truncated));
                }
            }
            else if (gameObject.transform.childCount > 0)
            {
                truncated = true;
            }
            return new JObject
            {
                ["name"] = Truncate(gameObject.name, MaxSnapshotTextCharacters),
                ["instance_id"] = WriteObjectId(DccMcpObjectIdentity.GetId(gameObject)),
                ["active"] = gameObject.activeSelf,
                ["tag"] = gameObject.tag,
                ["layer"] = gameObject.layer,
                ["children"] = children,
            };
        }

        private static JObject CreateGameObject(JObject parameters)
        {
            var name = RequireString(parameters, "name");
            var parentId = ReadOptionalObjectId(parameters, "parent_instance_id", "0");
            var parent = parentId == "0" ? null : ResolveGameObject(parentId);
            var gameObject = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(gameObject, "DCC-MCP: Create " + name);

            if (parent != null)
            {
                Undo.SetTransformParent(
                    gameObject.transform,
                    parent.transform,
                    "DCC-MCP: Parent " + name);
            }

            Selection.activeGameObject = gameObject;
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            return new JObject
            {
                ["created"] = true,
                ["name"] = gameObject.name,
                ["instance_id"] = WriteObjectId(DccMcpObjectIdentity.GetId(gameObject)),
                ["parent_instance_id"] = WriteObjectId(parentId),
            };
        }

        private static JObject SetTransform(JObject parameters)
        {
            var instanceId = RequireObjectId(parameters, "instance_id");
            var gameObject = ResolveGameObject(instanceId);
            var position = ReadVector3(parameters, "position");
            var rotation = ReadVector3(parameters, "rotation_euler");
            var scale = ReadVector3(parameters, "scale");
            if (position == null && rotation == null && scale == null)
            {
                throw new InvalidOperationException(
                    "At least one of position, rotation_euler, or scale is required.");
            }

            Undo.RecordObject(gameObject.transform, "DCC-MCP: Set Transform");
            if (position.HasValue)
            {
                gameObject.transform.position = position.Value;
            }
            if (rotation.HasValue)
            {
                gameObject.transform.eulerAngles = rotation.Value;
            }
            if (scale.HasValue)
            {
                gameObject.transform.localScale = scale.Value;
            }
            EditorUtility.SetDirty(gameObject.transform);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);

            return new JObject
            {
                ["updated"] = true,
                ["instance_id"] = WriteObjectId(instanceId),
                ["position"] = Vector(gameObject.transform.position),
                ["rotation_euler"] = Vector(gameObject.transform.eulerAngles),
                ["scale"] = Vector(gameObject.transform.localScale),
            };
        }

        private static JObject SaveScene()
        {
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (scene.isDirty && string.IsNullOrEmpty(scene.path))
                {
                    throw new InvalidOperationException(
                        "Cannot save an untitled scene remotely; save it once in Unity first.");
                }
            }
            var saved = EditorSceneManager.SaveOpenScenes();
            if (!saved)
            {
                throw new InvalidOperationException("Unity did not save all open scenes.");
            }
            return new JObject
            {
                ["saved"] = true,
                ["scene_path"] = SceneManager.GetActiveScene().path,
            };
        }

        private static GameObject ResolveGameObject(string instanceId)
        {
            var gameObject = DccMcpObjectIdentity.Resolve(instanceId) as GameObject;
            if (gameObject == null)
            {
                throw new InvalidOperationException(
                    "Unity GameObject instance not found: " + instanceId);
            }
            if (gameObject.scene.handle != SceneManager.GetActiveScene().handle)
            {
                throw new InvalidOperationException(
                    "Unity GameObject is not in the active scene: " + instanceId);
            }
            return gameObject;
        }

        private static string RequireString(JObject parameters, string name)
        {
            var value = (string)parameters[name];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(name + " is required.");
            }
            if (value.Length > 120)
            {
                throw new InvalidOperationException(name + " must be at most 120 characters.");
            }
            return value;
        }

        private static int RequireInt(JObject parameters, string name)
        {
            var token = parameters[name];
            if (token == null)
            {
                throw new InvalidOperationException(name + " is required.");
            }
            return ReadInteger(token, name);
        }

        private static int ReadOptionalInt(JObject parameters, string name, int defaultValue)
        {
            var token = parameters[name];
            return token == null ? defaultValue : ReadInteger(token, name);
        }

        private static string RequireObjectId(JObject parameters, string name)
        {
            var token = parameters[name];
            if (token == null)
            {
                throw new InvalidOperationException(name + " is required.");
            }
            return ReadObjectId(token, name);
        }

        private static string ReadOptionalObjectId(
            JObject parameters,
            string name,
            string defaultValue)
        {
            var token = parameters[name];
            return token == null ? defaultValue : ReadObjectId(token, name);
        }

        private static string ReadObjectId(JToken token, string name)
        {
            if (token.Type != JTokenType.String && token.Type != JTokenType.Integer)
            {
                throw new InvalidOperationException(name + " must be a string or integer.");
            }

            var value = token.Type == JTokenType.String ? (string)token : token.ToString();
            string normalized;
            if (!DccMcpObjectIdentity.TryNormalize(value, out normalized))
            {
                throw new InvalidOperationException(name + " is not a valid Unity object ID.");
            }
            return normalized;
        }

        private static JToken WriteObjectId(string value)
        {
#if UNITY_6000_5_OR_NEWER
            return new JValue(value);
#else
            return new JValue(int.Parse(value, CultureInfo.InvariantCulture));
#endif
        }

        private static int ReadInteger(JToken token, string name)
        {
            if (token.Type != JTokenType.Integer)
            {
                throw new InvalidOperationException(name + " must be an integer.");
            }
            try
            {
                var value = (long)token;
                if (value < int.MinValue || value > int.MaxValue)
                {
                    throw new InvalidOperationException(name + " is outside the 32-bit range.");
                }
                return (int)value;
            }
            catch (OverflowException)
            {
                throw new InvalidOperationException(name + " is outside the 32-bit range.");
            }
        }

        private static Vector3? ReadVector3(JObject parameters, string name)
        {
            if (!(parameters[name] is JArray values))
            {
                return null;
            }
            if (values.Count != 3)
            {
                throw new InvalidOperationException(name + " must contain exactly three numbers.");
            }

            var components = new float[3];
            for (var index = 0; index < values.Count; index++)
            {
                var token = values[index];
                if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
                {
                    throw new InvalidOperationException(name + " must contain only numbers.");
                }
                var value = (double)token;
                if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > float.MaxValue)
                {
                    throw new InvalidOperationException(name + " must contain finite numbers.");
                }
                components[index] = (float)value;
            }
            return new Vector3(components[0], components[1], components[2]);
        }

        private static JArray Vector(Vector3 value)
        {
            return new JArray(value.x, value.y, value.z);
        }

        private static string Truncate(string value, int maxCharacters)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxCharacters)
            {
                return value ?? string.Empty;
            }
            return value.Substring(0, maxCharacters) + "…";
        }
    }
}
