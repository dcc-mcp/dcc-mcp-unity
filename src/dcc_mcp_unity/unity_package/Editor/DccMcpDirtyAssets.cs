using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DccMcp.Unity
{
    internal static class DccMcpDirtyAssets
    {
        // Leave room for the bridge response and the standard Core skill envelope.
        internal const int MaxResultBytes = 768 * 1024;
        private static readonly Func<Object, bool> DirtyQuery = ResolveDirtyQuery();

        internal static bool SupportsDirtyQuery { get { return DirtyQuery != null; } }

        private static Func<Object, bool> ResolveDirtyQuery()
        {
            // Unity 2018.4 exposes this only internally; later Editors expose a
            // public overload. Query the exact public API instead of guessing a
            // version threshold or invoking a private native binding.
            var method = typeof(EditorUtility).GetMethod("IsDirty", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(Object) }, null);
            if (method == null || method.ReturnType != typeof(bool)) return null;
            return (Func<Object, bool>)Delegate.CreateDelegate(typeof(Func<Object, bool>), method);
        }

        internal static JObject Inspect(JObject parameters)
        {
            if (parameters == null || parameters.Count != 0)
                throw new InvalidOperationException("inspect_dirty_assets accepts no parameters.");

            var context = Context();
            if (!SupportsDirtyQuery)
                return Failure(context, "dirty_query_unsupported",
                    "This Unity version has no supported public persistent-asset dirty query.");
            try
            {
                // Capture the loaded set once; never load assets, refresh, save or yield.
                var loaded = Resources.FindObjectsOfTypeAll<Object>();
                var dirty = new List<Object>();
                foreach (var value in loaded)
                {
                    if (value != null && EditorUtility.IsPersistent(value) && DirtyQuery(value))
                        dirty.Add(value);
                }

                var records = new JArray();
                long recordBytes = 0;
                var retain = true;
                var identityComplete = true;
                foreach (var value in dirty)
                {
                    var record = Describe(value);
                    identityComplete &= (bool)record["identity_complete"];
                    var bytes = Encoding.UTF8.GetByteCount(record.ToString(Formatting.None));
                    recordBytes = checked(recordBytes + bytes + (recordBytes == 0 ? 0 : 1));
                    if (recordBytes > MaxResultBytes)
                    {
                        retain = false;
                        records.Clear();
                    }
                    if (retain) records.Add(record);
                }
                return Finish(context, records, dirty.Count, recordBytes, identityComplete);
            }
            catch (Exception exception)
            {
                return Failure(context, "dirty_capture_failed",
                    exception.GetType().FullName + ": " + exception.Message);
            }
        }

        private static JObject Context()
        {
            var assembly = typeof(DccMcpDirtyAssets).Assembly;
            return new JObject
            {
                ["schema_version"] = 1,
                ["capture_id"] = Guid.NewGuid().ToString("N"),
                ["sampled_at_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["owner"] = new JObject
                {
                    ["process_id"] = System.Diagnostics.Process.GetCurrentProcess().Id,
                    ["project_path"] = System.IO.Path.GetDirectoryName(Application.dataPath),
                    ["engine_version"] = Application.unityVersion,
                    ["session_instance_id"] = DccMcpBridge.GetSessionInstanceId(),
                },
                ["source"] = new JObject
                {
                    ["assembly_full_name"] = assembly.FullName,
                    ["module_version_id"] = assembly.ManifestModule.ModuleVersionId.ToString("D"),
                },
                ["dirty_persistent_count"] = JValue.CreateNull(),
                ["items_count"] = 0,
                ["complete"] = false,
                ["incomplete"] = true,
                ["identity_complete"] = false,
                ["budget_bytes"] = MaxResultBytes,
                ["required_bytes"] = JValue.CreateNull(),
                ["error_code"] = JValue.CreateNull(),
                ["error_message"] = JValue.CreateNull(),
                ["items"] = new JArray(),
            };
        }

        internal static JObject Finish(
            JObject context, JArray records, int count, long recordBytes, bool identityComplete)
        {
            context["dirty_persistent_count"] = count;
            context["items_count"] = count;
            context["complete"] = true;
            context["incomplete"] = false;
            context["identity_complete"] = identityComplete;
            context["items"] = new JArray();
            var requiredBytes = checked(
                Encoding.UTF8.GetByteCount(context.ToString(Formatting.None)) + recordBytes);
            if (requiredBytes > MaxResultBytes)
            {
                context["required_bytes"] = requiredBytes;
                return Failure(context, "response_budget_exceeded",
                    "Full dirty snapshot exceeds the result budget; no object prefix is returned.");
            }
            if (records.Count != count)
                return Failure(context, "dirty_capture_failed", "Dirty snapshot record count differs.");
            context["items"] = records;
            return context;
        }

        private static JObject Failure(JObject context, string code, string message)
        {
            context["items"] = new JArray();
            context["items_count"] = 0;
            context["complete"] = false;
            context["incomplete"] = true;
            context["identity_complete"] = false;
            context["error_code"] = code;
            context["error_message"] = message;
            if (Encoding.UTF8.GetByteCount(context.ToString(Formatting.None)) > MaxResultBytes)
                throw new InvalidOperationException("Dirty snapshot error envelope exceeds its budget.");
            return context;
        }

        private static JObject Describe(Object value)
        {
            var errors = new JObject();
            var record = new JObject
            {
                ["object_id"] = Read(() => DccMcpObjectIdentity.GetId(value), errors, "object_id"),
                ["type"] = Read(() => value.GetType().AssemblyQualifiedName, errors, "type"),
                ["name"] = Read(() => value.name, errors, "name"),
                ["path"] = Read(() => AssetDatabase.GetAssetPath(value), errors, "path"),
                ["hide_flags"] = Read(() => (int)value.hideFlags, errors, "hide_flags"),
                ["is_main_asset"] = Read(() => AssetDatabase.IsMainAsset(value), errors, "is_main_asset"),
                ["is_sub_asset"] = Read(() => AssetDatabase.IsSubAsset(value), errors, "is_sub_asset"),
                ["is_persistent"] = true,
                ["is_dirty"] = true,
                ["asset_identity_status"] = "unknown",
                ["asset_guid"] = JValue.CreateNull(),
                ["local_file_id"] = JValue.CreateNull(),
                ["asset_identity_reason"] = "not_found",
                ["global_object_id_status"] = "unknown",
                ["global_object_id"] = JValue.CreateNull(),
                ["global_object_id_reason"] = "unsupported",
                ["field_errors"] = errors,
            };
            try
            {
                string guid;
                long localId;
                var found = AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out guid, out localId);
                SetAssetIdentity(record, found, guid, localId);
            }
            catch (Exception exception)
            {
                record["asset_identity_reason"] = exception.GetType().FullName + ": " + exception.Message;
            }
#if UNITY_2019_2_OR_NEWER
            try
            {
                var id = GlobalObjectId.GetGlobalObjectIdSlow(value);
                if (id.Equals(default(GlobalObjectId)) || id.identifierType == 0)
                {
                    record["global_object_id_reason"] = "default_identifier";
                }
                else
                {
                    record["global_object_id_status"] = "known";
                    record["global_object_id"] = id.ToString();
                    record["global_object_id_reason"] = JValue.CreateNull();
                }
            }
            catch (Exception exception)
            {
                record["global_object_id_reason"] = exception.GetType().FullName + ": " + exception.Message;
            }
#endif
            record["identity_complete"] = errors.Count == 0 &&
                (string)record["asset_identity_status"] == "known" &&
                (string)record["global_object_id_status"] == "known";
            return record;
        }

        internal static void SetAssetIdentity(JObject record, bool found, string guid, long localId)
        {
            record["asset_identity_status"] = "unknown";
            record["asset_guid"] = JValue.CreateNull();
            record["local_file_id"] = JValue.CreateNull();
            Guid parsed;
            if (!found || !Guid.TryParseExact(guid, "N", out parsed) || parsed == Guid.Empty || localId == 0)
            {
                record["asset_identity_reason"] = found ? "default_identifier" : "not_found";
                return;
            }
            record["asset_identity_status"] = "known";
            record["asset_guid"] = guid;
            // JSON numbers lose long precision in browser clients.
            record["local_file_id"] = localId.ToString(CultureInfo.InvariantCulture);
            record["asset_identity_reason"] = JValue.CreateNull();
        }

        private static JToken Read<T>(Func<T> read, JObject errors, string field)
        {
            try
            {
                object value = read();
                return value == null ? JValue.CreateNull() : JToken.FromObject(value);
            }
            catch (Exception exception)
            {
                errors[field] = exception.GetType().FullName + ": " + exception.Message;
                return JValue.CreateNull();
            }
        }
    }
}
