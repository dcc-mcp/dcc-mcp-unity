using System;
using System.Diagnostics;
using System.IO;
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
            try
            {
                var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Microsoft Visual Studio", "Installer", "vswhere.exe");
                if (!File.Exists(vswhere))
                {
                    return "Microsoft Visual Studio discovery (vswhere) is missing.";
                }
                var start = new ProcessStartInfo
                {
                    FileName = vswhere,
                    Arguments = "-all -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                };
                string registrations;
                using (var process = Process.Start(start))
                {
                    if (!process.WaitForExit(10000))
                    {
                        process.Kill();
                        return "Visual Studio discovery timed out.";
                    }
                    registrations = process.StandardOutput.ReadToEnd();
                    if (process.ExitCode != 0)
                    {
                        return "Visual Studio discovery failed.";
                    }
                }
                var compilerFound = false;
                foreach (var registration in registrations.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var tools = Path.Combine(registration.Trim(), "VC", "Tools", "MSVC");
                    if (!Directory.Exists(tools)) { continue; }
                    foreach (var toolset in Directory.GetDirectories(tools))
                    {
                        var binaries = Path.Combine(toolset, "bin", "Hostx64", "x64");
                        if (File.Exists(Path.Combine(binaries, "cl.exe"))
                            && File.Exists(Path.Combine(binaries, "link.exe"))
                            && File.Exists(Path.Combine(toolset, "include", "vcruntime.h"))
                            && File.Exists(Path.Combine(toolset, "lib", "x64", "libcmt.lib")))
                        {
                            compilerFound = true;
                            break;
                        }
                    }
                }
                if (!compilerFound)
                {
                    return "No registered Visual Studio installation has a complete x64 C++ toolset.";
                }
                foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
                {
                    using (var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var roots = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows Kits\Installed Roots"))
                    {
                        var sdkRoot = roots == null ? null : roots.GetValue("KitsRoot10") as string;
                        if (string.IsNullOrEmpty(sdkRoot)) { continue; }
                        var includes = Path.Combine(sdkRoot, "Include");
                        if (!Directory.Exists(includes)) { continue; }
                        foreach (var include in Directory.GetDirectories(includes))
                        {
                            var version = Path.GetFileName(include);
                            var libraries = Path.Combine(sdkRoot, "Lib", version);
                            var binaries = Path.Combine(sdkRoot, "bin", version, "x64");
                            if (File.Exists(Path.Combine(include, "um", "Windows.h"))
                                && File.Exists(Path.Combine(include, "ucrt", "stdio.h"))
                                && File.Exists(Path.Combine(libraries, "um", "x64", "kernel32.lib"))
                                && File.Exists(Path.Combine(libraries, "ucrt", "x64", "ucrt.lib"))
                                && File.Exists(Path.Combine(binaries, "rc.exe"))
                                && File.Exists(Path.Combine(binaries, "mt.exe")))
                            {
                                return null;
                            }
                        }
                    }
                }
                return "No registered Windows SDK has complete x64 headers, libraries, rc.exe and mt.exe.";
            }
            catch (Exception exception)
            {
                return exception.Message;
            }
        }
    }
}
