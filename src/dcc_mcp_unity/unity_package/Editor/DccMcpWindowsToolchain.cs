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
        internal const string MsvcKitVersion = "0.2.21";
        internal const string MsvcKitSha256 = "2c76d707b964040ef39a16af344ab873d47673955e63486f49e537885f7b657b";

        internal static string RequiredSelection(string suffix)
        {
            var key = "DCC_MCP_UNITY_" + suffix;
            var value = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    "Windows IL2CPP requires self-managed msvc-kit: set " + key
                    + " before starting the Editor. See docs/windows-toolchain.md for local setup"
                    + " of the pinned v0.2.21 CLI and existing modern VS/SDK. No toolchain is installed automatically.");
            }
            return value;
        }

        // The pinned release is small; neither this gate nor doctor installs a compiler/SDK.
        // JSON parsing is owned by the Editor's existing Newtonsoft dependency.
        internal static string ReadMsvcKitDoctor()
        {
            var executable = RequiredSelection("MSVC_KIT");
            var root = RequiredSelection("MSVC_KIT_DIR");
            var msvc = RequiredSelection("MSVC_VERSION");
            var sdk = RequiredSelection("SDK_VERSION");
            if (!Regex.IsMatch(msvc, @"\A\d+\.\d+\.\d+\z")
                || !Regex.IsMatch(sdk, @"\A\d+\.\d+\.\d+\.\d+\z"))
            {
                throw new InvalidOperationException("msvc-kit requires full MSVC and Windows SDK version pins.");
            }
            if (!File.Exists(executable) || HashFile(executable) != MsvcKitSha256)
            {
                throw new InvalidOperationException(
                    "msvc-kit is missing or its SHA256 does not match the official v0.2.21 Windows release.");
            }
            var start = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "doctor --format json --compile --arch x64 --host-arch x64 --msvc-version "
                    + QuoteArgument(msvc) + " --sdk-version " + QuoteArgument(sdk) + " --dir " + QuoteArgument(root),
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
            var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft Visual Studio", "Installer", "vswhere.exe");
            return InspectPinnedRegisteredToolchain(msvc, sdk, tools, vswhere, ReadRegistration);
        }

        // Relate the kit's exact selection to registered installations, even when its
        // portable root uses temporary directory junctions. This does not prove which
        // compiler a particular Unity version eventually chooses in BuildPlayer.
        internal static string InspectPinnedRegisteredToolchain(
            string msvc, string sdk, IDictionary<string, string> tools,
            string vswhere, Func<RegistryView, string, string, string> readRegistration)
        {
            if (!File.Exists(vswhere))
            {
                return "Pinned msvc-kit selection requires modern registered Visual Studio; vswhere is missing.";
            }
            var matchedCompiler = false;
            foreach (var registration in ReadVisualStudioRegistrations(vswhere)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var toolset = Path.Combine(registration.Trim(), "VC", "Tools", "MSVC", msvc);
                var bin = Path.Combine(toolset, "bin", "Hostx64", "x64");
                if (HasCompilerFiles(toolset, Path.Combine("bin", "Hostx64", "x64"), "x64")
                    && MatchesTool(tools, "cl", Path.Combine(bin, "cl.exe"))
                    && MatchesTool(tools, "link", Path.Combine(bin, "link.exe")))
                {
                    matchedCompiler = true;
                    break;
                }
            }
            if (!matchedCompiler)
            {
                return "msvc-kit selected compiler/version does not match a registered modern Visual Studio x64 toolset.";
            }
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                var root = readRegistration(view, @"SOFTWARE\Microsoft\Windows Kits\Installed Roots", "KitsRoot10");
                if (string.IsNullOrWhiteSpace(root)) { continue; }
                var bin = Path.Combine(root, "bin", sdk, "x64");
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
                    if (HasCompilerFiles(toolset, Path.Combine("bin", "Hostx64", "x64"), "x64"))
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
                    && (HasSdkTools(Path.Combine(sdkRoot, "bin", version, "x64"))
                        || (sdkVersion.Build <= 14393 && HasSdkTools(Path.Combine(sdkRoot, "bin", "x64")))))
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
