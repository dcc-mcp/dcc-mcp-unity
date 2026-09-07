using System;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DccMcp.Unity.Tests
{
    public sealed class DccMcpComponentsTests
    {
        private GameObject target;
        private string objectId;
        [SetUp]
        public void SetUp()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            target = new GameObject("Components", typeof(RectTransform));
            objectId = (string)DccMcpComponents.List(new JObject {
                ["instance_id"] = DccMcpObjectIdentity.GetId(target) })["object_id"];
        }
        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset("Assets/DccMcpComponentProbe.prefab");
        }
        private JObject Add(string name)
        {
            return DccMcpCommands.Execute("components.add", new JObject {
                ["object_id"] = objectId, ["type_name"] = name });
        }
        private JObject Set(string id, params JObject[] writes)
        {
            return DccMcpCommands.Execute("components.set", new JObject {
                ["component_id"] = id, ["properties"] = new JArray(writes) });
        }
        private static JObject Value(string path, JToken value)
        {
            return new JObject { ["path"] = path, ["value"] = value };
        }
        [Test]
        public void AddSetReadbackUndoRedoAndStaleFingerprint()
        {
            var added = Add("UnityEngine.Canvas");
            var id = (string)added["component_id"];
            var result = Set(id, Value("m_SortingOrder", 7));
            Assert.That(target.GetComponent<Canvas>().sortingOrder, Is.EqualTo(7));
            Assert.That((int)result["properties"][0]["value"], Is.EqualTo(7));
            Assert.Throws<InvalidOperationException>(() => DccMcpCommands.Execute("components.set", new JObject {
                ["component_id"] = id, ["expected_fingerprint"] = added["fingerprint"],
                ["properties"] = new JArray(Value("m_SortingOrder", 8)) }));
            Undo.PerformUndo();
            Assert.That(target.GetComponent<Canvas>().sortingOrder, Is.EqualTo(0));
            Undo.PerformRedo();
            Assert.That(target.GetComponent<Canvas>().sortingOrder, Is.EqualTo(7));
        }
        [Test]
        public void InvalidBatchDoesNotApplyEarlierWrites()
        {
            var id = (string)Add("UnityEngine.Canvas")["component_id"];
            Assert.Throws<InvalidOperationException>(() => Set(id,
                Value("m_SortingOrder", 9), Value("does_not_exist", 1)));
            Assert.That(target.GetComponent<Canvas>().sortingOrder, Is.EqualTo(0));
            Assert.Throws<InvalidOperationException>(() => Set(id, Value("m_GameObject", JValue.CreateNull())));
            Assert.Throws<InvalidOperationException>(() => Set(id, Value("m_Enabled", "false")));
        }
        [Test]
        public void RectTransformAndExactTypes()
        {
            var list = DccMcpComponents.List(new JObject { ["instance_id"] = DccMcpObjectIdentity.GetId(target) });
            var id = (string)list["components"][0]["component_id"];
            Set(id, Value("m_AnchorMin", new JArray(0.1f, 0.2f)), Value("m_Pivot", new JArray(0.3f, 0.4f)));
            Assert.That(((RectTransform)target.transform).anchorMin, Is.EqualTo(new Vector2(0.1f, 0.2f)));
            Assert.Throws<InvalidOperationException>(() => Add("Canvas"));
            Assert.Throws<InvalidOperationException>(() => DccMcpCommands.Execute("components.remove", new JObject { ["component_id"] = id }));
            Assert.Throws<InvalidOperationException>(() => DccMcpComponents.Inspect(new JObject { ["component_id"] = Guid.NewGuid().ToString("N") }));
        }
        [Test]
        public void RemovalCanBeUndoneAndDeadHandlesFail()
        {
            var added = Add("UnityEngine.CanvasRenderer");
            DccMcpCommands.Execute("components.remove", new JObject { ["component_id"] = added["component_id"] });
            Assert.That(target.GetComponent<CanvasRenderer>(), Is.Null);
            Assert.Throws<InvalidOperationException>(() => DccMcpComponents.Inspect(added));
            Undo.PerformUndo();
            Assert.That(target.GetComponent<CanvasRenderer>(), Is.Not.Null);
        }
        [Test]
        public void PrefabOverrideDoesNotChangeAsset()
        {
            target.AddComponent<Canvas>();
            var asset = PrefabUtility.SaveAsPrefabAsset(target, "Assets/DccMcpComponentProbe.prefab");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(asset);
            var list = DccMcpComponents.List(new JObject { ["instance_id"] = DccMcpObjectIdentity.GetId(instance) });
            var id = (string)list["components"][1]["component_id"];
            var result = Set(id, Value("m_SortingOrder", 12));
            Assert.That((string)result["state"]["edit_kind"], Is.EqualTo("prefab_override"));
            Assert.That(instance.GetComponent<Canvas>().sortingOrder, Is.EqualTo(12));
            Assert.That(asset.GetComponent<Canvas>().sortingOrder, Is.EqualTo(0));
            Assert.That(PrefabUtility.GetPropertyModifications(instance), Is.Not.Empty);
            Undo.PerformUndo();
            Assert.That(instance.GetComponent<Canvas>().sortingOrder, Is.EqualTo(0));
        }
    }
}
