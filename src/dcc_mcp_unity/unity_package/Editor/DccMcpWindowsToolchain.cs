using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace DccMcp.Unity
{
    // Check discoverable registrations rather than ambient PATH. BuildPlayer
    // remains the authority for this Editor's actual toolchain selection.
    internal static class DccMcpWindowsToolchain
    {
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
