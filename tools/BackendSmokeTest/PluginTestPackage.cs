using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DeltaNFD.Services.Plugins;

namespace BackendSmokeTest;

internal static class PluginTestPackage
{
    public static PluginIndexEntry Seal(PluginIndexEntry entry)
    {
        var path = Path.Combine(entry.InstallDirectory, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(path))!;
        manifest["backend"]!["entry"] = Path.GetRelativePath(entry.InstallDirectory, entry.EntryExecutable).Replace('\\', '/');
        File.WriteAllText(path, manifest.ToJsonString());
        var files = Directory.GetFiles(entry.InstallDirectory, "*", SearchOption.AllDirectories).Select(file =>
            new PluginIndexFile(Path.GetRelativePath(entry.InstallDirectory, file).Replace('\\', '/'),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant(), new FileInfo(file).Length)).ToArray();
        var sealedEntry = entry with { Files = files };
        if (entry.State == PluginPackageState.Authorized)
        {
            var dataRoot = Path.Combine(new DirectoryInfo(entry.InstallDirectory).Parent!.Parent!.Parent!.FullName, "PluginData");
            var authorizations = new PluginAuthorizationStore(Path.Combine(dataRoot, "authorizations.json"));
            using var package = PluginPackageIntegrity.VerifyAndLock(sealedEntry);
            if (!authorizations.Authorize(sealedEntry, package.FileTableDigest, entry.OfflineAuthorizedUtc is not null, out var error))
                throw new Exception(error);
        }
        return sealedEntry;
    }
}
