using System;
using System.Globalization;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DccMcp.Unity.Tests
{
    public sealed class DccMcpCommandsTests
    {
        [SetUp]
        public void SetUp()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [Test]
        public void CreateTransformAndInspectSceneRoundTrips()
        {
            var created = DccMcpCommands.Execute(
                "scene.create_game_object",
                new JObject { ["name"] = "CI Probe" });
            var instanceId = created["instance_id"].ToString();
#if UNITY_6000_5_OR_NEWER
            Assert.That(created["instance_id"].Type, Is.EqualTo(JTokenType.String));
#else
            Assert.That(created["instance_id"].Type, Is.EqualTo(JTokenType.Integer));
#endif
            JToken requestId = instanceId;
#if !UNITY_6000_5_OR_NEWER
            requestId = int.Parse(instanceId, CultureInfo.InvariantCulture);
#endif

            DccMcpCommands.Execute(
                "scene.set_transform",
                new JObject
                {
                    ["instance_id"] = requestId,
                    ["position"] = new JArray(1, 2, 3),
                });

            var gameObject = DccMcpObjectIdentity.Resolve(instanceId) as GameObject;
            Assert.That(gameObject, Is.Not.Null);
            Assert.That(gameObject.transform.position, Is.EqualTo(new Vector3(1, 2, 3)));

            var snapshot = DccMcpCommands.Execute(
                "scene.inspect",
                new JObject { ["max_nodes"] = 10 });
            var roots = (JArray)snapshot["roots"];
            Assert.That(roots, Has.Count.EqualTo(1));
            Assert.That((string)roots[0]["name"], Is.EqualTo("CI Probe"));
            Assert.That((bool)snapshot["truncated"], Is.False);
        }

        [Test]
        public void CreateGameObjectCanBeUndone()
        {
            var created = DccMcpCommands.Execute(
                "scene.create_game_object",
                new JObject { ["name"] = "Undo Probe" });
            var instanceId = created["instance_id"].ToString();

            Undo.PerformUndo();

            Assert.That(DccMcpObjectIdentity.Resolve(instanceId), Is.Null);
        }

        [Test]
        public void InspectSceneRejectsOutOfRangeNodeLimit()
        {
            Assert.That(
                () => DccMcpCommands.Execute(
                    "scene.inspect",
                    new JObject { ["max_nodes"] = 0 }),
                Throws.TypeOf<InvalidOperationException>()
                    .With.Message.EqualTo("max_nodes must be between 1 and 5000."));
        }
    }
}
