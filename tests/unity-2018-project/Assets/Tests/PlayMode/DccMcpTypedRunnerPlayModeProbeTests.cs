using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace DccMcp.Unity.LiveTests
{
    public sealed class DccMcpTypedRunnerPlayModeProbeTests
    {
        [UnityTest]
        public IEnumerator ExactFilteredProjectTestSurvivesPlayModeTransition()
        {
            yield return null;
            Assert.That(true, Is.True);
        }
    }
}
