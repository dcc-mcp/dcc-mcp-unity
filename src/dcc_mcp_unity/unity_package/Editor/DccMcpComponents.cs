using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DccMcp.Unity
{
    // Handles intentionally expire on domain reload, and never resolve by name or reused instance ID.
    internal static class DccMcpComponents
    {
        private static readonly Dictionary<string, Object> Handles = new Dictionary<string, Object>();

        private static string Handle(Object value)
        {
            if (value == null) return null;
            foreach (var pair in Handles)
                if (ReferenceEquals(pair.Value, value)) return pair.Key;
            if (Handles.Count >= 10000) throw new InvalidOperationException("Object handle limit reached; reload the domain.");
            var id = Guid.NewGuid().ToString("N");
            Handles.Add(id, value);
            return id;
        }

        private static Object Resolve(string id)
        {
            Object value;
            if (id == null || !Handles.TryGetValue(id, out value) || value == null)
                throw new InvalidOperationException("Stale object handle; inspect components again.");
            return value;
        }

        internal static JObject List(JObject args)
        {
            var go = DccMcpObjectIdentity.Resolve((string)args["instance_id"]) as GameObject;
            if (go == null) throw new InvalidOperationException("Exact GameObject instance_id is required.");
            var components = new JArray();
            foreach (var component in go.GetComponents<Component>())
                components.Add(component == null
                    ? new JObject { ["missing_script"] = true }
                    : new JObject { ["component_id"] = Handle(component), ["type"] = component.GetType().FullName });
            return new JObject { ["object_id"] = Handle(go), ["components"] = components };
        }

        internal static JObject Inspect(JObject args)
        {
            var component = Resolve((string)args["component_id"]) as Component;
            if (component == null) throw new InvalidOperationException("Handle is not a Component.");
            var limit = (int?)args["max_properties"] ?? 200;
            if (limit < 1 || limit > 1000) throw new InvalidOperationException("max_properties must be 1..1000.");
            return Snapshot(component, limit);
        }

        private static JObject Snapshot(Component component, int limit)
        {
            var serialized = new SerializedObject(component);
            serialized.Update();
            var values = new JArray();
            var iterator = serialized.GetIterator();
            var truncated = false;
            while (iterator.Next(true))
            {
                if (values.Count == limit) { truncated = true; break; }
                values.Add(new JObject {
                    ["path"] = iterator.propertyPath, ["kind"] = iterator.propertyType.ToString(),
                    ["editable"] = Writable(iterator), ["value"] = Read(iterator),
                    ["prefab_override"] = iterator.prefabOverride
                });
            }
            return new JObject {
                ["component_id"] = Handle(component), ["type"] = component.GetType().FullName,
                ["fingerprint"] = Fingerprint(component), ["properties"] = values,
                ["truncated"] = truncated, ["state"] = State(component)
            };
        }

        private static string Fingerprint(Component component)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(
                    Handle(component) + EditorJsonUtility.ToJson(component)))).Replace("-", "").ToLowerInvariant();
        }

        private static JObject State(Component component)
        {
            return new JObject {
                ["edit_kind"] = EditorUtility.IsPersistent(component) ? "prefab_asset" :
                    PrefabUtility.IsPartOfPrefabInstance(component) ? "prefab_override" : "scene_local",
                ["scene_path"] = component.gameObject.scene.path,
                ["scene_dirty"] = component.gameObject.scene.IsValid() && component.gameObject.scene.isDirty,
                ["asset_dirty"] = EditorUtility.IsPersistent(component) && EditorUtility.IsDirty(component),
                ["saved"] = false
            };
        }

        private static void Editable(Component component)
        {
            // Prefab assets require a separate explicit load/save transaction; never mutate them implicitly.
            if (EditorUtility.IsPersistent(component) || !component.gameObject.scene.IsValid())
                throw new InvalidOperationException("Edit a loaded scene or prefab instance; direct prefab assets are unsupported.");
        }

        private static void Dirty(Component component)
        {
            if (PrefabUtility.IsPartOfPrefabInstance(component))
                PrefabUtility.RecordPrefabInstancePropertyModifications(component);
            EditorSceneManager.MarkSceneDirty(component.gameObject.scene);
        }

        internal static JObject Add(JObject args)
        {
            var go = Resolve((string)args["object_id"]) as GameObject;
            if (go == null) throw new InvalidOperationException("object_id must identify a GameObject.");
            Editable(go.transform);
            var typeName = (string)args["type_name"];
            var matches = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType(typeName ?? "", false)).Where(t => t != null).Distinct().ToArray();
            if (matches.Length != 1 || !typeof(Component).IsAssignableFrom(matches[0]) ||
                matches[0].IsAbstract || matches[0].ContainsGenericParameters)
                throw new InvalidOperationException("An exact, unambiguous concrete Component type is required.");
            var component = Undo.AddComponent(go, matches[0]);
            if (component == null) throw new InvalidOperationException("Unity rejected adding the Component.");
            Dirty(component);
            return Snapshot(component, 200);
        }

        internal static JObject Remove(JObject args)
        {
            var component = Resolve((string)args["component_id"]) as Component;
            if (component == null || component is Transform)
                throw new InvalidOperationException("Cannot remove this Component.");
            Editable(component);
            Compare(component, args);
            var go = component.gameObject;
            Undo.DestroyObjectImmediate(component);
            if (component != null) throw new InvalidOperationException("Unity rejected Component removal (check dependencies).");
            EditorSceneManager.MarkSceneDirty(go.scene);
            return new JObject { ["removed"] = true, ["object_id"] = Handle(go), ["saved"] = false };
        }

        private static void Compare(Component component, JObject args)
        {
            var expected = (string)args["expected_fingerprint"];
            if (expected != null && expected != Fingerprint(component))
                throw new InvalidOperationException("Component fingerprint changed; inspect before retrying.");
        }

        internal static JObject Set(JObject args)
        {
            var component = Resolve((string)args["component_id"]) as Component;
            if (component == null) throw new InvalidOperationException("Handle is not a Component.");
            Editable(component);
            Compare(component, args);
            var writes = args["properties"] as JArray;
            if (writes == null || writes.Count < 1 || writes.Count > 64 || writes.ToString(Formatting.None).Length > 65536)
                throw new InvalidOperationException("Provide 1..64 properties within 64 KiB.");
            var serialized = new SerializedObject(component);
            serialized.Update();
            var paths = new HashSet<string>();
            foreach (var write in writes)
            {
                var path = (string)write["path"];
                if (path == null || !paths.Add(path)) throw new InvalidOperationException("Duplicate or missing property path.");
                var property = serialized.FindProperty(path);
                if (property == null || !Writable(property))
                    throw new InvalidOperationException("Unsupported or read-only property: " + path);
                Write(property, write["value"]); // staged only; no host mutation until every value validates
            }
            Undo.RecordObject(component, "DCC-MCP serialized properties");
            serialized.ApplyModifiedProperties();
            Dirty(component);
            serialized.Update();
            var readback = new JArray();
            foreach (var path in paths)
                readback.Add(new JObject { ["path"] = path, ["value"] = Read(serialized.FindProperty(path)) });
            return new JObject { ["component_id"] = Handle(component), ["fingerprint"] = Fingerprint(component),
                ["properties"] = readback, ["state"] = State(component) };
        }

        private static bool Writable(SerializedProperty p)
        {
            if (!p.editable || p.propertyPath == "m_Script" || p.propertyPath == "m_GameObject" ||
                p.propertyPath.StartsWith("m_Prefab", StringComparison.Ordinal) ||
                p.propertyPath == "m_CorrespondingSourceObject" || p.propertyPath == "m_ObjectHideFlags") return false;
            switch (p.propertyType)
            {
                case SerializedPropertyType.Boolean: case SerializedPropertyType.Integer:
                case SerializedPropertyType.Float: case SerializedPropertyType.String:
                case SerializedPropertyType.Enum: case SerializedPropertyType.Color:
                case SerializedPropertyType.Vector2: case SerializedPropertyType.Vector3:
                case SerializedPropertyType.Vector4: case SerializedPropertyType.Rect:
                case SerializedPropertyType.Bounds: case SerializedPropertyType.ObjectReference: return true;
                default: return false;
            }
        }

        private static JToken Read(SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Boolean: return p.boolValue;
                case SerializedPropertyType.Integer: return p.longValue;
                case SerializedPropertyType.Float: return p.doubleValue;
                case SerializedPropertyType.String: return p.stringValue.Length <= 4096 ? p.stringValue : p.stringValue.Substring(0, 4096);
                case SerializedPropertyType.Enum: return p.enumValueIndex;
                case SerializedPropertyType.Vector2: var v2 = p.vector2Value; return new JArray(v2.x, v2.y);
                case SerializedPropertyType.Vector3: var v3 = p.vector3Value; return new JArray(v3.x, v3.y, v3.z);
                case SerializedPropertyType.Vector4: var v4 = p.vector4Value; return new JArray(v4.x, v4.y, v4.z, v4.w);
                case SerializedPropertyType.Color: var c = p.colorValue; return new JArray(c.r, c.g, c.b, c.a);
                case SerializedPropertyType.Rect: var r = p.rectValue; return new JArray(r.x, r.y, r.width, r.height);
                case SerializedPropertyType.Bounds: var b = p.boundsValue; return new JArray(b.center.x, b.center.y, b.center.z, b.size.x, b.size.y, b.size.z);
                case SerializedPropertyType.ObjectReference:
                    return p.objectReferenceValue == null ? JValue.CreateNull() : new JObject {
                        ["object_id"] = Handle(p.objectReferenceValue), ["asset_path"] = AssetDatabase.GetAssetPath(p.objectReferenceValue),
                        ["type"] = p.objectReferenceValue.GetType().FullName };
                default: return JValue.CreateNull();
            }
        }

        private static float[] Vector(JToken value, int count)
        {
            var array = value as JArray;
            if (array == null || array.Count != count) throw new InvalidOperationException("Invalid vector length.");
            return array.Select(v => {
                if (v.Type != JTokenType.Float && v.Type != JTokenType.Integer) throw new InvalidOperationException("Expected number.");
                var f = (float)v;
                if (float.IsNaN(f) || float.IsInfinity(f)) throw new InvalidOperationException("Expected finite number.");
                return f;
            }).ToArray();
        }

        private static void Write(SerializedProperty p, JToken value)
        {
            if (value == null) throw new InvalidOperationException("A value is required.");
            switch (p.propertyType)
            {
                case SerializedPropertyType.Boolean:
                    if (value.Type != JTokenType.Boolean) throw new InvalidOperationException("Expected boolean.");
                    p.boolValue = (bool)value; break;
                case SerializedPropertyType.Integer:
                    if (value.Type != JTokenType.Integer) throw new InvalidOperationException("Expected integer.");
                    p.longValue = (long)value; break;
                case SerializedPropertyType.Float:
                    if (value.Type != JTokenType.Integer && value.Type != JTokenType.Float) throw new InvalidOperationException("Expected number.");
                    var number = (double)value;
                    if (double.IsNaN(number) || double.IsInfinity(number)) throw new InvalidOperationException("Expected finite number.");
                    p.doubleValue = number; break;
                case SerializedPropertyType.String:
                    if (value.Type != JTokenType.String || ((string)value).Length > 4096) throw new InvalidOperationException("Expected string up to 4096 characters.");
                    p.stringValue = (string)value; break;
                case SerializedPropertyType.Enum:
                    if (value.Type != JTokenType.Integer || (int)value < 0 || (int)value >= p.enumNames.Length) throw new InvalidOperationException("Invalid enum index.");
                    p.enumValueIndex = (int)value; break;
                case SerializedPropertyType.Vector2: var v2 = Vector(value, 2); p.vector2Value = new Vector2(v2[0], v2[1]); break;
                case SerializedPropertyType.Vector3: var v3 = Vector(value, 3); p.vector3Value = new Vector3(v3[0], v3[1], v3[2]); break;
                case SerializedPropertyType.Vector4: var v4 = Vector(value, 4); p.vector4Value = new Vector4(v4[0], v4[1], v4[2], v4[3]); break;
                case SerializedPropertyType.Color: var c = Vector(value, 4); p.colorValue = new Color(c[0], c[1], c[2], c[3]); break;
                case SerializedPropertyType.Rect: var r = Vector(value, 4); p.rectValue = new Rect(r[0], r[1], r[2], r[3]); break;
                case SerializedPropertyType.Bounds: var b = Vector(value, 6); p.boundsValue = new Bounds(new Vector3(b[0], b[1], b[2]), new Vector3(b[3], b[4], b[5])); break;
                case SerializedPropertyType.ObjectReference:
                    Object reference = null;
                    if (value.Type != JTokenType.Null)
                    {
                        var obj = value as JObject;
                        if (obj == null || (obj["object_id"] == null) == (obj["asset_path"] == null)) throw new InvalidOperationException("Provide object_id or asset_path.");
                        if (obj["object_id"] != null) reference = Resolve((string)obj["object_id"]);
                        else
                        {
                            var path = (string)obj["asset_path"];
                            DccMcpJobs.EnsureProjectAssetPathSafe(path);
                            reference = AssetDatabase.LoadMainAssetAtPath(path);
                            // Imported single-sprite textures expose their Sprite as a subasset.
                            if (p.type == "PPtr<$Sprite>" || p.type == "PPtr<Sprite>")
                                reference = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                            if (reference == null) throw new InvalidOperationException("Asset not found.");
                        }
                        var expected = p.type.Replace("PPtr<", "").Replace("$", "").TrimEnd('>');
                        var compatible = false;
                        for (var type = reference.GetType(); type != null; type = type.BaseType)
                            if (type.Name == expected) compatible = true;
                        if (!compatible) throw new InvalidOperationException("Incompatible object reference type.");
                    }
                    p.objectReferenceValue = reference; break;
                default: throw new InvalidOperationException("Unsupported serialized property kind.");
            }
        }
    }
}
