using NUnit.Framework;

namespace DccMcp.Unity.LiveTests
{
    public sealed class DccMcpTypedRunnerProbeTests
    {
        [Test]
        public void ExactFilteredProjectTestRunsInsideTheConnectedEditor()
        {
            Assert.That(1 + 1, Is.EqualTo(2));
        }
    }
}
