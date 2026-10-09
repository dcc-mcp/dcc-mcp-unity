using System;
using DccMcp.Unity;

internal static class WindowsToolchainProbe
{
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

    private static int Main(string[] arguments)
    {
        if (arguments.Length == 1)
        {
            var error = DccMcpWindowsToolchain.InspectCompiler(arguments[0]);
            Console.WriteLine(string.IsNullOrEmpty(error) ? "WINDOWS_REGISTERED_TOOLCHAIN_DISCOVERED" : error);
            return string.IsNullOrEmpty(error) ? 0 : 2;
        }
        var probed = false;
        Func<string> inspect = () => { probed = true; return null; };
        DccMcpWindowsToolchain.Ensure(false, false, false, inspect);
        if (probed) { throw new Exception("Mono must not require an MSVC toolchain"); }
        MustFail(() => DccMcpWindowsToolchain.Ensure(true, false, true, inspect), "Windows Unity Editor");
        MustFail(() => DccMcpWindowsToolchain.Ensure(true, true, false, inspect), "module in Unity Hub");
        if (probed) { throw new Exception("Unsupported host/module must fail before compiler discovery"); }
        MustFail(() => DccMcpWindowsToolchain.Ensure(true, true, true, () => "SDK absent"), "SDK absent");
        DccMcpWindowsToolchain.Ensure(true, true, true, inspect);
        if (!probed) { throw new Exception("IL2CPP must verify Unity compiler discovery"); }
        Console.WriteLine("WINDOWS_TOOLCHAIN_CONTRACT_OK");
        return 0;
    }
}
