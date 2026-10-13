using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Win32;
using DccMcp.Unity;

internal static class WindowsToolchainProbe
{
    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new Exception(message); }
    }

    private static void MustFail(Action action, string expected)
    {
        try { action(); }
        catch (InvalidOperationException exception)
        {
            if (exception.Message.Contains(expected)) { return; }
            throw;
        }
        throw new Exception("Expected preflight to fail: " + expected);
    }

    private static void Touch(string root, params string[] paths)
    {
        foreach (var relative in paths)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "fixture");
        }
    }

    private static Func<RegistryView, string, string, string> Registrations(string legacy, string sdk)
    {
        return (view, key, name) =>
        {
            if (key == @"SOFTWARE\Microsoft\VisualStudio\SxS\VS7")
            {
                Require(view == RegistryView.Registry32 && name == "14.0", "Only the VS2015 registration is eligible");
                return legacy;
            }
            Require(key == @"SOFTWARE\Microsoft\Windows Kits\Installed Roots" && name == "KitsRoot10",
                "Only registered Windows 10 SDK roots are eligible");
            // Exercise the second registry view rather than relying on the first.
            return view == RegistryView.Registry64 ? sdk : null;
        };
    }

    private static void CheckBuildPhases()
    {
        var cheapCalls = 0;
        var doctorCalls = 0;
        const string finalReport = "fixture final doctor report";
        Action cheap = () => { cheapCalls++; };
        Func<string> doctor = () => { doctorCalls++; return finalReport; };
        Require(DccMcpWindowsToolchain.CheckMsvcKitBuildPhase("starting", cheap, doctor) == null,
            "Starting must validate configuration without running doctor");
        Require(DccMcpWindowsToolchain.CheckMsvcKitBuildPhase("switching_build_target", cheap, doctor) == null,
            "Post-reload checks must not repeat the native compile probe");
        Require(cheapCalls == 2 && doctorCalls == 0, "Early phases must only run cheap checks");
        var report = DccMcpWindowsToolchain.CheckMsvcKitBuildPhase("ready_to_build", cheap, doctor);
        Require(cheapCalls == 3 && doctorCalls == 1, "Exactly one doctor call must precede the actual build");
        Require(report == finalReport, "The final doctor report must be retained for the build result");
        foreach (var phase in new[] { "starting", "switching_build_target", "ready_to_build" })
        {
            MustFail(() => DccMcpWindowsToolchain.CheckMsvcKitBuildPhase(
                phase, () => { throw new InvalidOperationException("invalid mandatory configuration"); }, doctor),
                "invalid mandatory configuration");
        }
        Require(doctorCalls == 1, "Invalid mandatory configuration must fail before doctor at every phase");
        MustFail(() => DccMcpWindowsToolchain.CheckMsvcKitBuildPhase(
            "ready_to_build", cheap, () => { throw new InvalidOperationException("doctor failed"); }), "doctor failed");
        MustFail(() => DccMcpWindowsToolchain.CheckMsvcKitBuildPhase("unknown", cheap, doctor), "Unknown Windows build");
        Console.WriteLine("WINDOWS_MSVC_KIT_PHASE_CONTRACT_OK");
    }

    private static void CheckFixtures(string root)
    {
        var absentVswhere = Path.Combine(root, "absent-vswhere.exe");
        var legacy = Path.Combine(root, "Visual Studio 2015");
        var sdk = Path.Combine(root, "Windows Kits", "10");
        Touch(legacy, "VC/bin/amd64/cl.exe", "VC/bin/amd64/link.exe", "VC/include/vcruntime.h", "VC/lib/amd64/libcmt.lib");
        Touch(sdk, "Include/10.0.10240.0/um/Windows.h", "Include/10.0.10240.0/ucrt/stdio.h",
            "Lib/10.0.10240.0/um/x64/kernel32.lib", "Lib/10.0.10240.0/ucrt/x64/ucrt.lib", "bin/x64/rc.exe", "bin/x64/mt.exe");
        var legacyReader = Registrations(legacy, sdk);
        Require(DccMcpWindowsToolchain.InspectRegisteredToolchain(absentVswhere, legacyReader) == null,
            "Registered VS2015 with early Windows 10 SDK must work without vswhere");
        File.Delete(Path.Combine(legacy, "VC", "bin", "amd64", "cl.exe"));
        Require(DccMcpWindowsToolchain.InspectRegisteredToolchain(absentVswhere, legacyReader).Contains("C++ toolset"),
            "Incomplete legacy compiler must be rejected");
        Touch(legacy, "VC/bin/x86_amd64/cl.exe", "VC/bin/x86_amd64/link.exe");
        Require(DccMcpWindowsToolchain.InspectRegisteredToolchain(absentVswhere, legacyReader) == null,
            "VS2015 x86-hosted x64 toolset must also work");
        Require(DccMcpWindowsToolchain.InspectRegisteredToolchain(absentVswhere, Registrations(null, sdk)).Contains("C++ toolset"),
            "Files without a Visual Studio registration must not qualify");
        Require(DccMcpWindowsToolchain.InspectRegisteredToolchain(absentVswhere, Registrations(legacy, null)).Contains("Windows SDK"),
            "SDK files without registration must not qualify");
        File.Delete(Path.Combine(sdk, "bin", "x64", "mt.exe"));
        Require(DccMcpWindowsToolchain.InspectRegisteredToolchain(absentVswhere, legacyReader).Contains("Windows SDK"),
            "SDK without manifest tool must be rejected");
        Touch(sdk, "bin/10.0.10240.0/x64/rc.exe", "bin/10.0.10240.0/x64/mt.exe");
        Require(DccMcpWindowsToolchain.InspectRegisteredToolchain(absentVswhere, legacyReader) == null,
            "Versioned SDK tools must work");
        File.Delete(Path.Combine(sdk, "Lib", "10.0.10240.0", "ucrt", "x64", "ucrt.lib"));
        Require(DccMcpWindowsToolchain.InspectRegisteredToolchain(absentVswhere, legacyReader).Contains("Windows SDK"),
            "SDK without x64 libraries must be rejected");
        Touch(sdk, "Lib/10.0.10240.0/ucrt/x64/ucrt.lib");
        var newerSdk = Path.Combine(root, "New Windows SDK");
        Touch(newerSdk, "Include/10.0.22621.0/um/Windows.h", "Include/10.0.22621.0/ucrt/stdio.h",
            "Lib/10.0.22621.0/um/x64/kernel32.lib", "Lib/10.0.22621.0/ucrt/x64/ucrt.lib",
            "bin/x64/rc.exe", "bin/x64/mt.exe");
        Require(DccMcpWindowsToolchain.InspectRegisteredToolchain(absentVswhere, Registrations(legacy, newerSdk)).Contains("Windows SDK"),
            "A modern SDK must not borrow the older unversioned tools");
        Touch(newerSdk, "bin/10.0.22621.0/x64/rc.exe", "bin/10.0.22621.0/x64/mt.exe");
        Require(DccMcpWindowsToolchain.InspectRegisteredToolchain(absentVswhere, Registrations(legacy, newerSdk)) == null,
            "A complete modern versioned SDK must qualify");

        // Use an actual redirected child process, not only a string fixture: both
        // -utf8 and the reader encoding are needed to preserve this installation.
        var modern = Path.Combine(root, "开发工具 Visual Studio");
        Touch(modern, "VC/Tools/MSVC/14.44.35207/bin/Hostx64/x64/cl.exe",
            "VC/Tools/MSVC/14.44.35207/bin/Hostx64/x64/link.exe",
            "VC/Tools/MSVC/14.44.35207/include/vcruntime.h", "VC/Tools/MSVC/14.44.35207/lib/x64/libcmt.lib");
        var fakeVswhere = Path.Combine(root, "fake-vswhere.exe");
        File.Copy(Assembly.GetExecutingAssembly().Location, fakeVswhere);
        File.WriteAllText(fakeVswhere + ".txt", modern, Encoding.UTF8);
        Require(DccMcpWindowsToolchain.ReadVisualStudioRegistrations(fakeVswhere).Trim() == modern,
            "Redirected UTF-8 must preserve the Chinese installation path");
        Require(DccMcpWindowsToolchain.InspectRegisteredToolchain(fakeVswhere, Registrations(null, sdk)) == null,
            "Registered modern C++ tools with a Chinese installation path must be found");
        var pinnedTools = new Dictionary<string, string>
        {
            { "cl", Path.Combine(modern, "VC/Tools/MSVC/14.44.35207/bin/Hostx64/x64/cl.exe") },
            { "link", Path.Combine(modern, "VC/Tools/MSVC/14.44.35207/bin/Hostx64/x64/link.exe") },
            { "rc", Path.Combine(sdk, "bin/10.0.10240.0/x64/rc.exe") },
            { "mt", Path.Combine(sdk, "bin/10.0.10240.0/x64/mt.exe") },
        };
        Require(DccMcpWindowsToolchain.InspectPinnedRegisteredToolchain(
            "14.44.35207", "10.0.10240.0", pinnedTools, fakeVswhere, Registrations(null, sdk)) == null,
            "Pinned kit selection must correspond to registered compiler and SDK tools");
        var unrelated = Path.Combine(root, "unrelated-cl.exe");
        File.WriteAllText(unrelated, "different compiler");
        pinnedTools["cl"] = unrelated;
        Require(DccMcpWindowsToolchain.InspectPinnedRegisteredToolchain(
            "14.44.35207", "10.0.10240.0", pinnedTools, fakeVswhere, Registrations(null, sdk)).Contains("does not match"),
            "A different portable compiler cannot stand in for the registered selection");
        File.Delete(Path.Combine(modern, "VC", "Tools", "MSVC", "14.44.35207", "lib", "x64", "libcmt.lib"));
        Require(DccMcpWindowsToolchain.InspectRegisteredToolchain(fakeVswhere, Registrations(null, sdk)).Contains("C++ toolset"),
            "Incomplete modern C++ libraries must be rejected");
        File.WriteAllText(fakeVswhere + ".txt", "FAIL_DISCOVERY", Encoding.UTF8);
        Require(DccMcpWindowsToolchain.InspectRegisteredToolchain(fakeVswhere, Registrations(null, sdk)).Contains("discovery failed"),
            "Nonzero vswhere exit must fail closed");
        Require(DccMcpWindowsToolchain.InspectCompiler(root).Contains("player resources are missing"),
            "A toolchain discovery fixture must not count as a Unity player module");
        Console.WriteLine("WINDOWS_TOOLCHAIN_FIXTURES_OK");
    }

    private static int Main(string[] arguments)
    {
        if (arguments.Length == 1 && arguments[0] == "--doctor")
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.WriteLine(DccMcpWindowsToolchain.ReadMsvcKitDoctor());
            return 0;
        }
        if (arguments.Length == 7 && arguments[0] == "--selected")
        {
            var tools = new Dictionary<string, string>
            {
                { "cl", arguments[3] }, { "link", arguments[4] },
                { "rc", arguments[5] }, { "mt", arguments[6] },
            };
            var error = DccMcpWindowsToolchain.InspectPinnedRegisteredToolchain(arguments[1], arguments[2], tools);
            Console.WriteLine(error ?? "MSVC_KIT_REGISTERED_SELECTION_OK");
            return error == null ? 0 : 2;
        }
        if (arguments.Length > 0 && arguments[0] == "-all")
        {
            Require(string.Join(" ", arguments) == "-all -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath -utf8",
                "Modern discovery must request UTF-8 and must not combine -legacy with component filters");
            Console.OutputEncoding = new UTF8Encoding(false);
            var registration = File.ReadAllText(Assembly.GetExecutingAssembly().Location + ".txt", Encoding.UTF8);
            if (registration == "FAIL_DISCOVERY") { return 2; }
            Console.WriteLine(registration);
            return 0;
        }
        if (arguments.Length == 1)
        {
            var error = arguments[0] == "--discover"
                ? DccMcpWindowsToolchain.InspectRegisteredToolchain()
                : DccMcpWindowsToolchain.InspectCompiler(arguments[0]);
            Console.WriteLine(string.IsNullOrEmpty(error) ? "WINDOWS_REGISTERED_TOOLCHAIN_DISCOVERED" : error);
            return string.IsNullOrEmpty(error) ? 0 : 2;
        }
        var probed = false;
        Func<string> inspect = () => { probed = true; return null; };
        DccMcpWindowsToolchain.Ensure(false, false, false, inspect);
        Require(!probed, "Mono must not require an MSVC toolchain");
        MustFail(() => DccMcpWindowsToolchain.Ensure(true, false, true, inspect), "Windows Unity Editor");
        MustFail(() => DccMcpWindowsToolchain.Ensure(true, true, false, inspect), "module in Unity Hub");
        Require(!probed, "Unsupported host/module must fail before compiler discovery");
        MustFail(() => DccMcpWindowsToolchain.Ensure(true, true, true, () => "SDK absent"), "SDK absent");
        DccMcpWindowsToolchain.Ensure(true, true, true, inspect);
        Require(probed, "IL2CPP must verify Unity compiler discovery");
        CheckBuildPhases();
        Require(DccMcpWindowsToolchain.QuoteArgument(@"C:\中文 path\") == "\"C:\\中文 path\\\\\"",
            "Doctor arguments must preserve spaces, Unicode and a trailing directory separator");
        var root = Path.Combine(Path.GetTempPath(), "dcc-mcp-toolchain-" + Guid.NewGuid().ToString("N"));
        try { CheckFixtures(root); }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
        Console.WriteLine("WINDOWS_TOOLCHAIN_CONTRACT_OK");
        return 0;
    }
}
