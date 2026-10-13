using System;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace DccMcp.Unity.Tests
{
    public sealed class DccMcpWindowsToolchainTests
    {
        private static JObject Report()
        {
            var checks = new JArray();
            foreach (var stage in new[] { "compile", "resource", "link", "manifest", "execute" })
            {
                checks.Add(new JObject { ["id"] = "probe." + stage, ["status"] = "passed" });
            }
            return new JObject
            {
                ["schema"] = "msvc-kit.doctor.v1",
                ["status"] = "passed",
                ["checks"] = checks,
                ["toolchain"] = new JObject
                {
                    ["arch"] = "x64", ["host_arch"] = "x64",
                    ["msvc"] = new JObject { ["version"] = "14.44.35207" },
                    ["sdk"] = new JObject { ["version"] = "10.0.22621.0" },
                    ["tools"] = new JObject { ["cl"] = "selected-cl.exe" },
                },
            };
        }

        [Test]
        public void DoctorGateVerifiesSelectionWithoutClaimingActualUnityCompilerChoice()
        {
            var called = false;
            var result = DccMcpJobs.ValidateWindowsMsvcKitReport(
                Report(), "14.44.35207", "10.0.22621.0", (msvc, sdk, tools) =>
                {
                    called = true;
                    Assert.That(msvc, Is.EqualTo("14.44.35207"));
                    Assert.That(sdk, Is.EqualTo("10.0.22621.0"));
                    Assert.That(tools["cl"], Is.EqualTo("selected-cl.exe"));
                    return null;
                });
            Assert.That(called, Is.True);
            Assert.That((bool)result["unity_compiler_selection_verified"], Is.False);
        }

        [TestCase("schema")]
        [TestCase("status")]
        [TestCase("arch")]
        [TestCase("host_arch")]
        [TestCase("msvc")]
        [TestCase("sdk")]
        public void DoctorGateRejectsMismatchedContractsBeforeRegistration(string field)
        {
            var report = Report();
            if (field == "schema" || field == "status") { report[field] = "wrong"; }
            else if (field == "msvc" || field == "sdk") { report["toolchain"][field]["version"] = "wrong"; }
            else { report["toolchain"][field] = "arm64"; }
            Assert.Throws<InvalidOperationException>(() => DccMcpJobs.ValidateWindowsMsvcKitReport(
                report, "14.44.35207", "10.0.22621.0", (msvc, sdk, tools) =>
                {
                    Assert.Fail("Invalid doctor report must not reach registration checks.");
                    return null;
                }));
        }

        [TestCase("missing")]
        [TestCase("skipped")]
        [TestCase("duplicate")]
        public void DoctorGateRequiresEveryNativeProbeStage(string change)
        {
            var report = Report();
            var checks = (JArray)report["checks"];
            if (change == "missing") { checks.RemoveAt(0); }
            else if (change == "skipped") { checks[0]["status"] = "skipped"; }
            else { checks.Add(checks[0].DeepClone()); }
            Assert.Throws<InvalidOperationException>(() => DccMcpJobs.ValidateWindowsMsvcKitReport(
                report, "14.44.35207", "10.0.22621.0", (msvc, sdk, tools) => null));
        }

        [Test]
        public void DoctorGateCannotBypassUnrelatedRegisteredTools()
        {
            var error = Assert.Throws<InvalidOperationException>(() => DccMcpJobs.ValidateWindowsMsvcKitReport(
                Report(), "14.44.35207", "10.0.22621.0", (msvc, sdk, tools) => "registered compiler differs"));
            Assert.That(error.Message, Does.Contain("registered compiler differs"));
        }

        [Test]
        public void MissingKitConfigurationHasAnActionableDiagnostic()
        {
            const string key = "DCC_MCP_UNITY_MSVC_KIT";
            var previous = Environment.GetEnvironmentVariable(key);
            try
            {
                Environment.SetEnvironmentVariable(key, null);
                var error = Assert.Throws<InvalidOperationException>(() => DccMcpWindowsToolchain.RequiredSelection("MSVC_KIT"));
                Assert.That(error.Message, Does.Contain(key));
                Assert.That(error.Message, Does.Contain("before starting the Editor"));
            }
            finally { Environment.SetEnvironmentVariable(key, previous); }
        }
    }
}
