using System.Diagnostics;
using DeltaNFD.Services;

namespace BackendSmokeTest;

internal static class PluginTestCleanup
{
    public static bool HasLiveProcesses(string root) => Visit(root, terminate: false);
    public static void StopOwnedProcesses(string root) => Visit(root, terminate: true);

    private static bool Visit(string root, bool terminate)
    {
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture cleanup must remain below Temp.");
        var prefix = full + Path.DirectorySeparatorChar;
        var alive = false;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string path;
                try { path = GameTargetService.ReadProcessPath(process.Id); }
                catch { continue; }
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                _ = process.SafeHandle;
                if (process.HasExited) continue;
                alive = true;
                if (!terminate) continue;
                if (!GameTargetService.ReadProcessPath(process.Id).Equals(path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Fixture process identity changed.");
                process.Kill(entireProcessTree: false);
                if (!process.WaitForExit(5000)) throw new TimeoutException("Fixture process did not exit.");
            }
        }
        return alive;
    }
}
