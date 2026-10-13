using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DccMcp.Unity
{
    // Check discoverable registrations rather than ambient PATH. BuildPlayer
    // remains the authority for this Editor's actual toolchain selection.
    internal static class DccMcpWindowsToolchain
    {
        internal const string MsvcKitVersion = "0.2.22";
        internal const string MsvcKitSha256 = "184f07fb2896f0a7d27c45445990bd7d9e525b6ee58956cfe857bf0027848925";

        internal static bool IsSupportedMsvcPin(string version)
        {
            return version == "14.0" || (!string.IsNullOrEmpty(version) && !version.StartsWith("14.0.", StringComparison.Ordinal)
                && Regex.IsMatch(version, @"\A\d+\.\d+\.\d+\z"));
        }

        internal static string SelectedHostArchitecture()
        {
            var host = Environment.GetEnvironmentVariable("DCC_MCP_UNITY_HOST_ARCH");
            if (string.IsNullOrWhiteSpace(host)) { return "x64"; }
            if (host != "x64" && host != "x86")
            {
                throw new InvalidOperationException("DCC_MCP_UNITY_HOST_ARCH must be x64 or x86 for the x64 player target.");
            }
            return host;
        }

        internal static string SelectedSdkRoot()
        {
            var root = Environment.GetEnvironmentVariable("DCC_MCP_UNITY_SDK_DIR");
            return string.IsNullOrWhiteSpace(root) ? null : root;
        }

        internal static string RequiredSelection(string suffix)
        {
            var key = "DCC_MCP_UNITY_" + suffix;
            var value = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    "Windows IL2CPP requires self-managed msvc-kit: set " + key
                    + " before starting the Editor. See docs/windows-toolchain.md for local setup"
                    + " of the pinned v0.2.22 CLI and existing supported VS/SDK. No toolchain is installed automatically.");
            }
            return value;
        }

        // Cheap checks run before a target switch as well as immediately before build.
        // They do not launch doctor or modify the Editor environment.
        internal static void ValidateMsvcKitConfiguration()
        {
            var executable = RequiredSelection("MSVC_KIT");
            var root = RequiredSelection("MSVC_KIT_DIR");
            var msvc = RequiredSelection("MSVC_VERSION");
            var sdk = RequiredSelection("SDK_VERSION");
            var sdkRoot = SelectedSdkRoot();
            SelectedHostArchitecture();
            if (!IsSupportedMsvcPin(msvc)
                || !Regex.IsMatch(sdk, @"\A\d+\.\d+\.\d+\.\d+\z"))
            {
                throw new InvalidOperationException("msvc-kit requires full modern MSVC/SDK pins, or exactly 14.0 for VS2015.");
            }
            if (msvc == "14.0" && sdkRoot == null)
            {
                throw new InvalidOperationException("VS2015 family 14.0 requires DCC_MCP_UNITY_SDK_DIR for its existing SDK.");
            }
            if ((msvc == "14.0" || sdkRoot != null) && new Version(MsvcKitVersion).CompareTo(new Version(0, 2, 22)) < 0)
            {
                throw new InvalidOperationException(
                    "Legacy or separate-SDK selection requires an approved released msvc-kit 0.2.22+ pin; the consumer release identity is not upgraded.");
            }
            if (!Directory.Exists(root))
            {
                throw new InvalidOperationException(
                    "The configured msvc-kit toolchain root is missing; prepare it before starting the build.");
            }
            if (sdkRoot != null && !Directory.Exists(sdkRoot))
            {
                throw new InvalidOperationException("DCC_MCP_UNITY_SDK_DIR does not exist; no ambient SDK fallback is allowed.");
            }
            if (!File.Exists(executable) || HashFile(executable) != MsvcKitSha256)
            {
                throw new InvalidOperationException(
                    "msvc-kit is missing or its SHA256 does not match the official v0.2.22 Windows release.");
            }
        }

        internal static string CheckMsvcKitBuildPhase(string phase, Action validateConfiguration, Func<string> readDoctor)
        {
            if (phase != "starting" && phase != "switching_build_target" && phase != "ready_to_build")
            {
                throw new InvalidOperationException("Unknown Windows build preflight phase: " + phase);
            }
            validateConfiguration();
            // Only the final preflight needs the expensive native compile probe.
            // Return that report directly so the successful build retains its evidence.
            return phase == "ready_to_build" ? readDoctor() : null;
        }

        // The pinned release is small; neither this gate nor doctor installs a compiler/SDK.
        // JSON parsing is owned by the Editor's existing Newtonsoft dependency.
        internal static string ReadMsvcKitDoctor()
        {
            ValidateMsvcKitConfiguration();
            var executable = RequiredSelection("MSVC_KIT");
            var root = RequiredSelection("MSVC_KIT_DIR");
            var msvc = RequiredSelection("MSVC_VERSION");
            var sdk = RequiredSelection("SDK_VERSION");
            var start = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = MsvcKitDoctorArguments(root, msvc, sdk, SelectedHostArchitecture(), SelectedSdkRoot()),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using (var process = Process.Start(start))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(180000))
                {
                    process.Kill();
                    throw new InvalidOperationException("msvc-kit doctor exceeded its 180-second deadline.");
                }
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        "msvc-kit doctor failed (exit " + process.ExitCode + "). Repair the selected toolchain. "
                        + output.Result + " " + error.Result);
                }
                return output.Result;
            }
        }

        internal static string MsvcKitDoctorArguments(string root, string msvc, string sdk, string host, string sdkRoot)
        {
            return "doctor --format json --compile --arch x64 --host-arch " + QuoteArgument(host)
                + " --msvc-version " + QuoteArgument(msvc) + " --sdk-version " + QuoteArgument(sdk)
                + " --dir " + QuoteArgument(root) + (sdkRoot == null ? "" : " --sdk-dir " + QuoteArgument(sdkRoot));
        }

        internal static string QuoteArgument(string value)
        {
            var result = new StringBuilder("\"");
            var slashes = 0;
            foreach (var character in value)
            {
                if (character == '\\') { slashes++; continue; }
                result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                result.Append(character);
                slashes = 0;
            }
            result.Append('\\', slashes * 2);
            return result.Append('"').ToString();
        }

        private static string HashFile(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var hash = SHA256.Create())
            {
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
        }

        private static bool MatchesTool(IDictionary<string, string> tools, string name, string registered)
        {
            string selected;
            return tools.TryGetValue(name, out selected) && File.Exists(selected) && File.Exists(registered)
                && HashFile(selected) == HashFile(registered);
        }

        internal static string InspectPinnedRegisteredToolchain(
            string msvc, string sdk, IDictionary<string, string> tools)
        {
            return InspectPinnedRegisteredToolchain(msvc, sdk, "x64", tools);
        }

        internal static string InspectPinnedRegisteredToolchain(
            string msvc, string sdk, string host, IDictionary<string, string> tools)
        {
            var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft Visual Studio", "Installer", "vswhere.exe");
            return InspectPinnedRegisteredToolchain(msvc, sdk, host, tools, vswhere, ReadRegistration);
        }

        // Relate the kit's exact selection to registered installations, even when its
        // portable root uses temporary directory junctions. This does not prove which
        // compiler a particular Unity version eventually chooses in BuildPlayer.
        internal static string InspectPinnedRegisteredToolchain(
            string msvc, string sdk, IDictionary<string, string> tools,
            string vswhere, Func<RegistryView, string, string, string> readRegistration)
        {
            return InspectPinnedRegisteredToolchain(msvc, sdk, "x64", tools, vswhere, readRegistration);
        }

        internal static string InspectPinnedRegisteredToolchain(
            string msvc, string sdk, string host, IDictionary<string, string> tools,
            string vswhere, Func<RegistryView, string, string, string> readRegistration)
        {
            if (!IsSupportedMsvcPin(msvc) || (host != "x64" && host != "x86"))
            {
                return "Unsupported pinned compiler version or host architecture.";
            }
            var matchedCompiler = false;
            if (msvc == "14.0")
            {
                var legacy = readRegistration(RegistryView.Registry32, @"SOFTWARE\Microsoft\VisualStudio\SxS\VS7", "14.0");
                if (!string.IsNullOrWhiteSpace(legacy) && HasLegacyFamilyIdentity(legacy))
                {
                    var vc = Path.Combine(legacy, "VC");
                    var relativeBin = Path.Combine("bin", host == "x64" ? "amd64" : "x86_amd64");
                    var bin = Path.Combine(vc, relativeBin);
                    matchedCompiler = HasCompilerFiles(vc, relativeBin, "amd64")
                        && MatchesTool(tools, "cl", Path.Combine(bin, "cl.exe"))
                        && MatchesTool(tools, "link", Path.Combine(bin, "link.exe"));
                }
            }
            else
            {
                if (!File.Exists(vswhere))
                {
                    return "Pinned modern msvc-kit selection requires registered Visual Studio; vswhere is missing.";
                }
                foreach (var registration in ReadVisualStudioRegistrations(vswhere)
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var toolset = Path.Combine(registration.Trim(), "VC", "Tools", "MSVC", msvc);
                    var relativeBin = Path.Combine("bin", "Host" + host, "x64");
                    var bin = Path.Combine(toolset, relativeBin);
                    if (HasCompilerFiles(toolset, relativeBin, "x64")
                        && MatchesTool(tools, "cl", Path.Combine(bin, "cl.exe"))
                        && MatchesTool(tools, "link", Path.Combine(bin, "link.exe")))
                    {
                        matchedCompiler = true;
                        break;
                    }
                }
            }
            if (!matchedCompiler)
            {
                return "msvc-kit selected compiler/version/host does not match a registered Visual Studio x64-target toolset.";
            }
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                var root = readRegistration(view, @"SOFTWARE\Microsoft\Windows Kits\Installed Roots", "KitsRoot10");
                if (string.IsNullOrWhiteSpace(root)) { continue; }
                var bin = SdkToolsDirectory(root, sdk, host);
                if (File.Exists(Path.Combine(root, "Include", sdk, "um", "Windows.h"))
                    && File.Exists(Path.Combine(root, "Lib", sdk, "um", "x64", "kernel32.lib"))
                    && MatchesTool(tools, "rc", Path.Combine(bin, "rc.exe"))
                    && MatchesTool(tools, "mt", Path.Combine(bin, "mt.exe")))
                {
                    return null;
                }
            }
            return "msvc-kit selected SDK version/tools do not match a registered Windows SDK.";
        }

        private static bool HasLegacyFamilyIdentity(string root)
        {
            var crt = Path.Combine(root, "VC", "include", "crtversion.h");
            if (!File.Exists(Path.Combine(root, "VC", "vcvarsall.bat")) || !File.Exists(crt)) { return false; }
            var contents = File.ReadAllText(crt);
            return Regex.IsMatch(contents, @"(?m)^\s*#\s*define\s+_VC_CRT_MAJOR_VERSION\s+14(?:\s|$)")
                && Regex.IsMatch(contents, @"(?m)^\s*#\s*define\s+_VC_CRT_MINOR_VERSION\s+0(?:\s|$)");
        }

        internal static string SdkToolsDirectory(string root, string version, string host)
        {
            var versioned = Path.Combine(root, "bin", version);
            var early = version == "10.0.10240.0" || version == "10.0.10586.0" || version == "10.0.14393.0";
            // A partial versioned directory is an incomplete selection, never a fallback.
            return early && !Directory.Exists(versioned) ? Path.Combine(root, "bin", host) : Path.Combine(versioned, host);
        }

        internal static void Ensure(
            bool il2cpp,
            bool windowsHost,
            bool targetSupported,
            Func<string> inspectCompiler)
        {
            if (!il2cpp)
            {
                return;
            }
            if (!windowsHost)
            {
                throw new InvalidOperationException("Windows IL2CPP player builds require a Windows Unity Editor host.");
            }
            if (!targetSupported)
            {
                throw new InvalidOperationException(
                    "Windows IL2CPP Build Support is unavailable; add the matching module in Unity Hub.");
            }
            var error = inspectCompiler();
            if (!string.IsNullOrEmpty(error))
            {
                throw new InvalidOperationException(
                    "Windows IL2CPP toolchain preflight failed: " + error
                    + " Install a Visual Studio C++ toolset and Windows SDK supported by this Unity version,"
                    + " then restart the Editor. A portable MSVC PATH alone does not establish Unity discovery.");
            }
        }

        internal static string InspectCompiler(string editorContentsPath)
        {
            var variations = Path.Combine(editorContentsPath, "PlaybackEngines", "windowsstandalonesupport", "Variations");
            if (!Directory.Exists(variations)
                || Directory.GetDirectories(variations, "win64*il2cpp").Length == 0)
            {
                return "Windows x64 IL2CPP player resources are missing; add this Editor's Windows IL2CPP module.";
            }
            return InspectRegisteredToolchain();
        }

        // Also used by Windows CI without claiming that Unity/player resources exist.
        internal static string InspectRegisteredToolchain()
        {
            var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft Visual Studio", "Installer", "vswhere.exe");
            return InspectRegisteredToolchain(vswhere, ReadRegistration);
        }

        private static string ReadRegistration(RegistryView view, string path, string name)
        {
            using (var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
            using (var key = registry.OpenSubKey(path))
            {
                return key == null ? null : key.GetValue(name) as string;
            }
        }

        // The reader seam permits registration fixtures without changing the registry.
        internal static string InspectRegisteredToolchain(
            string vswhere, Func<RegistryView, string, string, string> readRegistration)
        {
            try
            {
                // VS2015 has no Setup API/vswhere installation. Use the same registry
                // source as vswhere's LegacyProvider, restricted to VS14 (not older VS).
                var legacy = readRegistration(RegistryView.Registry32,
                    @"SOFTWARE\Microsoft\VisualStudio\SxS\VS7", "14.0");
                var compilerFound = HasLegacyCompiler(legacy);
                if (!compilerFound && File.Exists(vswhere))
                {
                    compilerFound = HasModernCompiler(ReadVisualStudioRegistrations(vswhere));
                }
                if (!compilerFound)
                {
                    return "No registered Visual Studio installation has a complete x64 C++ toolset"
                        + (File.Exists(vswhere) ? "." : "; modern Visual Studio discovery (vswhere) is missing.");
                }
                foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
                {
                    var sdkRoot = readRegistration(view, @"SOFTWARE\Microsoft\Windows Kits\Installed Roots", "KitsRoot10");
                    if (HasWindowsSdk(sdkRoot)) { return null; }
                }
                return "No registered Windows SDK has complete x64 headers, libraries, rc.exe and mt.exe.";
            }
            catch (Exception exception)
            {
                return exception.Message;
            }
        }

        internal static string ReadVisualStudioRegistrations(string vswhere)
        {
            var start = new ProcessStartInfo
            {
                FileName = vswhere,
                Arguments = "-all -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath -utf8",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using (var process = Process.Start(start))
            {
                // Drain while waiting so a full redirected pipe cannot deadlock discovery.
                var output = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(10000))
                {
                    process.Kill();
                    throw new InvalidOperationException("Visual Studio discovery timed out.");
                }
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException("Visual Studio discovery failed.");
                }
                return output.Result;
            }
        }

        private static bool HasModernCompiler(string registrations)
        {
            foreach (var registration in registrations.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var tools = Path.Combine(registration.Trim(), "VC", "Tools", "MSVC");
                if (!Directory.Exists(tools)) { continue; }
                foreach (var toolset in Directory.GetDirectories(tools))
                {
                    if (HasCompilerFiles(toolset, Path.Combine("bin", "Hostx64", "x64"), "x64")
                        || HasCompilerFiles(toolset, Path.Combine("bin", "Hostx86", "x64"), "x64"))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool HasLegacyCompiler(string registration)
        {
            if (string.IsNullOrWhiteSpace(registration)) { return false; }
            var tools = Path.Combine(registration, "VC");
            // Both native and x86-hosted x64 compilers were offered by VS2015.
            return HasCompilerFiles(tools, Path.Combine("bin", "amd64"), "amd64")
                || HasCompilerFiles(tools, Path.Combine("bin", "x86_amd64"), "amd64");
        }

        private static bool HasCompilerFiles(string tools, string binaries, string architecture)
        {
            return File.Exists(Path.Combine(tools, binaries, "cl.exe"))
                && File.Exists(Path.Combine(tools, binaries, "link.exe"))
                && File.Exists(Path.Combine(tools, "include", "vcruntime.h"))
                && File.Exists(Path.Combine(tools, "lib", architecture, "libcmt.lib"));
        }

        private static bool HasWindowsSdk(string sdkRoot)
        {
            if (string.IsNullOrWhiteSpace(sdkRoot)) { return false; }
            var includes = Path.Combine(sdkRoot, "Include");
            if (!Directory.Exists(includes)) { return false; }
            foreach (var include in Directory.GetDirectories(includes))
            {
                var version = Path.GetFileName(include);
                Version sdkVersion;
                if (!Version.TryParse(version, out sdkVersion) || sdkVersion.Major != 10) { continue; }
                var libraries = Path.Combine(sdkRoot, "Lib", version);
                if (File.Exists(Path.Combine(include, "um", "Windows.h"))
                    && File.Exists(Path.Combine(include, "ucrt", "stdio.h"))
                    && File.Exists(Path.Combine(libraries, "um", "x64", "kernel32.lib"))
                    && File.Exists(Path.Combine(libraries, "ucrt", "x64", "ucrt.lib"))
                    && (HasSdkTools(SdkToolsDirectory(sdkRoot, version, "x64"))
                        || HasSdkTools(SdkToolsDirectory(sdkRoot, version, "x86"))))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasSdkTools(string binaries)
        {
            // Early Windows 10 SDKs installed tools in bin/x64 (without a version).
            return File.Exists(Path.Combine(binaries, "rc.exe")) && File.Exists(Path.Combine(binaries, "mt.exe"));
        }
    }
}
