using System;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace DccMcp.Unity.Tests
{
    public sealed class DccMcpDirtyAssetsTests
    {
        private string folder;

        [TearDown]
        public void TearDown()
        {
            if (folder != null && AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
        }

        [Test]
        public void DirtySnapshotRejectsParametersInsteadOfApplyingAResultLimit()
        {
            Assert.That(
                () => DccMcpCommands.Execute("editor.inspect_dirty_assets", new JObject { ["limit"] = 32 }),
                Throws.TypeOf<InvalidOperationException>());
        }

        [Test]
        public void DirtySnapshotReturnsAllOwnedDirtyMainAndSubassetsWithoutSavingThem()
        {
            if (!DccMcpDirtyAssets.SupportsDirtyQuery)
            {
                var unsupported = DccMcpCommands.Execute("editor.inspect_dirty_assets", new JObject());
                Assert.That((bool)unsupported["complete"], Is.False);
                Assert.That(unsupported["dirty_persistent_count"].Type, Is.EqualTo(JTokenType.Null));
                Assert.That((string)unsupported["error_code"], Is.EqualTo("dirty_query_unsupported"));
                Assert.That(((JArray)unsupported["items"]).Count, Is.Zero);
                return;
            }
            var folderName = "DccMcpDirtyAssetTests-" + Guid.NewGuid().ToString("N");
            folder = "Assets/" + folderName;
            AssetDatabase.CreateFolder("Assets", folderName);
            var main = new Material(Shader.Find("Hidden/InternalErrorShader")) { name = "Main exact" };
            AssetDatabase.CreateAsset(main, folder + "/Owned.asset");
            var values = new UnityEngine.Object[41];
            values[0] = main;
            for (var i = 1; i < values.Length; i++)
            {
                var child = new Texture2D(1, 1) { name = "Exact child " + i };
                AssetDatabase.AddObjectToAsset(child, main);
                values[i] = child;
            }
            AssetDatabase.SaveAssets();
            foreach (var value in values) EditorUtility.SetDirty(value);
            var selected = Selection.activeObject;
            var result = DccMcpCommands.Execute("editor.inspect_dirty_assets", new JObject());
            Assert.That((bool)result["complete"], Is.True, result.ToString());
            Assert.That((bool)result["incomplete"], Is.False);
            var items = (JArray)result["items"];
            Assert.That((int)result["items_count"], Is.EqualTo(items.Count));
            Assert.That((int)result["dirty_persistent_count"], Is.EqualTo(items.Count));
            Assert.That(items.Count, Is.GreaterThanOrEqualTo(values.Length));
            foreach (var value in values)
            {
                var item = items.Single(row => (string)row["object_id"] == DccMcpObjectIdentity.GetId(value));
                Assert.That((string)item["name"], Is.EqualTo(value.name));
                Assert.That((string)item["type"], Is.EqualTo(value.GetType().AssemblyQualifiedName));
                Assert.That((string)item["path"], Is.EqualTo(folder + "/Owned.asset"));
                Assert.That((int)item["hide_flags"], Is.EqualTo((int)value.hideFlags));
                Assert.That((bool)item["is_main_asset"], Is.EqualTo(value == main));
                Assert.That((bool)item["is_sub_asset"], Is.EqualTo(value != main));
                Assert.That((string)item["asset_identity_status"], Is.EqualTo("known"));
                Assert.That((string)item["local_file_id"], Is.Not.Empty);
                var isDirty = typeof(EditorUtility).GetMethod("IsDirty", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(UnityEngine.Object) }, null);
                Assert.That((bool)isDirty.Invoke(null, new object[] { value }), Is.True,
                    "Inspection must not save dirty assets.");
            }
            Assert.That(Selection.activeObject, Is.SameAs(selected));
            Assert.That((string)result["owner"]["session_instance_id"], Is.Not.Empty);
            Assert.That((string)result["source"]["module_version_id"], Is.Not.Empty);
            Assert.That(Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)),
                Is.LessThanOrEqualTo(DccMcpDirtyAssets.MaxResultBytes));
        }

        [TestCase("host.ping")]
        [TestCase("editor.inspect_dirty_assets")]
        [TestCase("scene.create_game_object")]
        public void PublicEntryRejectsWorkerBeforeAnyEditorAccess(string method)
        {
            Exception rejected = null;
            var worker = new Thread(() =>
            {
                try { DccMcpCommands.Execute(method, new JObject()); }
                catch (Exception exception) { rejected = exception; }
            });
            worker.Start();
            Assert.That(worker.Join(5000), Is.True, "Worker must fail before dispatching Editor work.");
            Assert.That(rejected, Is.TypeOf<InvalidOperationException>());
            Assert.That(rejected.Message, Is.EqualTo("Unity commands require the initialized Editor main thread."));
        }

        [Test]
        public void PublicEntryKeepsTheFixedMethodAllowlist()
        {
            Assert.That(
                () => DccMcpCommands.Execute("editor.eval", new JObject { ["code"] = "anything" }),
                Throws.TypeOf<InvalidOperationException>().With.Message.EqualTo("Unknown Unity action: editor.eval"));
        }

        [Test]
        public void DirtyQueryCapabilityMatchesOnlyTheExactPublicApi()
        {
            var method = typeof(EditorUtility).GetMethod("IsDirty", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(UnityEngine.Object) }, null);
            Assert.That(DccMcpDirtyAssets.SupportsDirtyQuery,
                Is.EqualTo(method != null && method.ReturnType == typeof(bool)));
        }

        [Test]
        public void AssetIdentityPreservesSignedLongPrecisionAndMarksDefaultsUnknown()
        {
            var item = new JObject();
            DccMcpDirtyAssets.SetAssetIdentity(item, true, new string('a', 32), long.MaxValue);
            Assert.That((string)item["local_file_id"], Is.EqualTo("9223372036854775807"));
            Assert.That((string)item["asset_identity_status"], Is.EqualTo("known"));
            DccMcpDirtyAssets.SetAssetIdentity(item, true, new string('0', 32), 0);
            Assert.That((string)item["asset_identity_status"], Is.EqualTo("unknown"));
            Assert.That(item["local_file_id"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That((string)item["asset_identity_reason"], Is.EqualTo("default_identifier"));
        }

        [Test]
        public void OversizedExactSnapshotFailsWithoutReturningAnyPrefix()
        {
            var records = new JArray(new JObject { ["name"] = new string('\u754c', DccMcpDirtyAssets.MaxResultBytes) });
            var bytes = Encoding.UTF8.GetByteCount(records[0].ToString(Formatting.None));
            var result = DccMcpDirtyAssets.Finish(new JObject(), records, 1, bytes, false);
            Assert.That((bool)result["complete"], Is.False);
            Assert.That((bool)result["incomplete"], Is.True);
            Assert.That((int)result["dirty_persistent_count"], Is.EqualTo(1));
            Assert.That((string)result["error_code"], Is.EqualTo("response_budget_exceeded"));
            Assert.That((long)result["required_bytes"], Is.GreaterThan(DccMcpDirtyAssets.MaxResultBytes));
            Assert.That(((JArray)result["items"]).Count, Is.Zero);
            Assert.That(DccMcpDirtyAssets.MaxResultBytes + 16 * 1024,
                Is.LessThanOrEqualTo(DccMcpBridge.MaxOutboundMessageBytes));
        }
    }
}
