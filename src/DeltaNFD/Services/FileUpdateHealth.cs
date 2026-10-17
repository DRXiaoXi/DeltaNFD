using System.Diagnostics;
using System.Text.Json;
using DeltaNFD.Updates;

namespace DeltaNFD.Services;

internal static class FileUpdateHealth
{
    internal static int Verify(string session, string nonce)
    {
        try
        {
            ProtectedSessions.Validate(session);
            if (!Guid.TryParseExact(nonce, "N", out _)) return 2;
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(session, "health-request.json")));
            var request = document.RootElement;
            if (request.GetProperty("nonce").GetString() != nonce
                || !Path.GetFullPath(request.GetProperty("root").GetString()!).TrimEnd(Path.DirectorySeparatorChar)
                    .Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !UpdateService.TryParseVersion(request.GetProperty("version").GetString(), out var version)
                || UpdateService.CompareVersions(version, AppVersion.Current) != 0) return 2;
            foreach (var name in new[] { "Ace", "Gpu", "Runtime", "SystemMaintenance", "CpuFrame", "Procman" })
            {
                var file = Path.Combine(AppContext.BaseDirectory, "Backends", "DeltaNFD.Backend." + name + ".exe");
                if (!UpdateService.TryParseVersion(FileVersionInfo.GetVersionInfo(file).FileVersion, out var backendVersion)
                    || UpdateService.CompareVersions(version, backendVersion) != 0) return 2;
            }
            File.WriteAllText(Path.Combine(session, "health-ok.txt"), nonce);
            return 0;
        }
        catch { return 2; }
    }
}
