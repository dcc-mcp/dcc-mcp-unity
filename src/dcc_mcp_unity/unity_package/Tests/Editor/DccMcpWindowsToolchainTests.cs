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

        private static JObject LegacyReport(string host)
        {
            // Synthetic JSON only; this is not a native VS2015 acceptance result.
            var report = Report();
            report["toolchain"]["host_arch"] = host;
            report["toolchain"]["msvc"]["version"] = "14.0";
            report["toolchain"]["msvc"]["install_path"] = "C:/VS2015/VC";
            report["toolchain"]["sdk"]["version"] = "10.0.14393.0";
            report["toolchain"]["sdk"]["install_path"] = "C:/SDK";
            ((JArray)report["checks"]).Add(new JObject { ["id"] = "version.msvc", ["status"] = "skipped" });
            ((JArray)report["checks"]).Add(new JObject { ["id"] = "version.sdk_tools", ["status"] = "skipped" });
            if (host == "x86") { report["checks"][4]["status"] = "skipped"; }
            return report;
        }

        [TestCase("x64", true)]
        [TestCase("x86", false)]
        public void LegacyFamilyReportKeepsHostAndExecutionEvidenceDistinct(string host, bool executed)
        {
            var called = false;
            var result = DccMcpJobs.ValidateWindowsMsvcKitReport(LegacyReport(host), "14.0", "10.0.14393.0", host, "C:/SDK",
                (msvc, sdk, selectedHost, tools) =>
                {
                    called = true;
                    Assert.That(msvc, Is.EqualTo("14.0"));
                    Assert.That(selectedHost, Is.EqualTo(host));
                    return null;
                }, "C:/VS2015");
            Assert.That(called, Is.True);
            Assert.That((string)result["compiler_version_kind"], Is.EqualTo("family"));
            Assert.That((bool)result["consumer_probe_execution_verified"], Is.EqualTo(executed));
            Assert.That((bool)result["unity_compiler_selection_verified"], Is.False);
        }

        [TestCase("x64", "execute")]
        [TestCase("x86", "compile")]
        [TestCase("x86", "resource")]
        [TestCase("x86", "link")]
        [TestCase("x86", "manifest")]
        public void OnlyExplicitCrossHostExecutionMayBeSkipped(string host, string skipped)
        {
            var report = LegacyReport(host);
            foreach (var check in (JArray)report["checks"])
            {
                if ((string)check["id"] == "probe." + skipped) { check["status"] = "skipped"; }
            }
            Assert.Throws<InvalidOperationException>(() => DccMcpJobs.ValidateWindowsMsvcKitReport(
                report, "14.0", "10.0.14393.0", host, "C:/SDK", (msvc, sdk, selectedHost, tools) => null, "C:/VS2015"));
        }

        [TestCase("target")]
        [TestCase("host")]
        [TestCase("sdk_root")]
        [TestCase("compiler_root")]
        [TestCase("family_duplicate")]
        [TestCase("family_evidence")]
        public void LegacyReportCannotChangeTheRequestedSelection(string change)
        {
            var report = LegacyReport("x86");
            if (change == "target") { report["toolchain"]["arch"] = "x86"; }
            if (change == "host") { report["toolchain"]["host_arch"] = "x64"; }
            if (change == "sdk_root") { report["toolchain"]["sdk"]["install_path"] = "C:/wrong"; }
            if (change == "compiler_root") { report["toolchain"]["msvc"]["install_path"] = "C:/copied/VC"; }
            if (change == "family_duplicate") { ((JArray)report["checks"]).Add(report["checks"][5].DeepClone()); }
            if (change == "family_evidence") { report["checks"][5]["status"] = "passed"; }
            Assert.Throws<InvalidOperationException>(() => DccMcpJobs.ValidateWindowsMsvcKitReport(
                report, "14.0", "10.0.14393.0", "x86", "C:/SDK", (msvc, sdk, selectedHost, tools) => null, "C:/VS2015"));
        }

        [TestCase("14.0", true)]
        [TestCase("14.44.35207", true)]
        [TestCase("14.44", false)]
        [TestCase("14.0.24215", false)]
        public void CompilerPinDistinguishesLegacyFamilyFromModernExactVersion(string pin, bool valid)
        {
            Assert.That(DccMcpWindowsToolchain.IsSupportedMsvcPin(pin), Is.EqualTo(valid));
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
