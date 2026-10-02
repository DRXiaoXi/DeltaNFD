using System.Security.Cryptography;
using DeltaNFD.Services.Plugins;

namespace BackendSmokeTest;

internal static class PluginIntegrityChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD_PluginIntegrity_" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "Plugins/org.example.integrity/1.0.0");
        Directory.CreateDirectory(Path.Combine(install, "backend"));
        var executable = Path.Combine(install, "backend/probe.exe");
        File.WriteAllText(executable, "fixture");
        var entry = new PluginIndexEntry { Id = "org.example.integrity", Version = "1.0.0", Name = "Fixture", Author = "Tests",
            PackageSha256 = new string('a', 64), InstallDirectory = install, EntryExecutable = executable,
            State = PluginPackageState.Authorized, Files = [new("backend/probe.exe", Hash(executable), new FileInfo(executable).Length)] };
        var trust = new PluginAuthorizationStore(Path.Combine(root, "proof.json"));
        try
        {
            using (var locked = PluginPackageIntegrity.VerifyAndLock(entry))
            {
                Require(!trust.Matches(entry, locked.FileTableDigest, false, out _), "index authorization is not proof");
                Require(trust.Authorize(entry, locked.FileTableDigest, false, out _), "fixture authorization");
                Require(trust.Matches(entry, locked.FileTableDigest, false, out _), "matching proof");
                Require(!trust.Matches(entry, locked.FileTableDigest, true, out _), "ordinary proof is not offline permission");
                Require(trust.Revoke(entry.Id, out _), "revoke proof");
                Require(!trust.Matches(entry, locked.FileTableDigest, false, out _), "revocation survives an authorized index");
                Require(trust.Authorize(entry, locked.FileTableDigest, false, out _), "reauthorize fixture");
                try { File.WriteAllText(executable, "tamper"); throw new Exception("running package allowed a write"); }
                catch (IOException) { }
            }
            File.WriteAllText(executable, "tamper");
            Reject(() => PluginPackageIntegrity.VerifyAndLock(entry), "changed file");
            var changed = entry with { Files = [new("backend/probe.exe", Hash(executable), new FileInfo(executable).Length)] };
            using (var locked = PluginPackageIntegrity.VerifyAndLock(changed))
                Require(!trust.Matches(changed, locked.FileTableDigest, false, out _), "edited index cannot reauthorize new bytes");
            File.WriteAllText(Path.Combine(install, "extra.dll"), "extra");
            Reject(() => PluginPackageIntegrity.VerifyAndLock(changed), "unindexed dependency");
            File.Delete(Path.Combine(install, "extra.dll"));
            File.Delete(executable);
            Reject(() => PluginPackageIntegrity.VerifyAndLock(changed), "missing executable");
            File.WriteAllText(Path.Combine(root, "proof.json"), "null");
            Require(!trust.Matches(entry, "unknown", false, out _), "null proofs fail closed");
            var dataRoot = Path.Combine(root, "PluginData");
            var runStore = new PluginRunStore(Path.Combine(dataRoot, "managed-runs.json"));
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var run = new PluginManagedRun(Guid.NewGuid(), entry.Id, entry.Version, entry.PackageSha256, executable,
                process.Id, process.StartTime.ToUniversalTime().Ticks, process.Id, process.StartTime.ToUniversalTime().Ticks);
            Require(runStore.TryAdd(run, out _), "persist fixture run identity");
            Require(!runStore.TryAdd(run with { SessionId = Guid.NewGuid() }, out _), "duplicate run reservation rejected");
            var restarted = new PluginRuntimeService(dataRoot: dataRoot);
            Require(restarted.IsRunning(entry.Id), "new host detects unconfirmed old run");
            Require(!restarted.StopAsync(entry.Id).GetAwaiter().GetResult(), "new host cannot claim old backend stopped");
            Require(restarted.QueryStatus(entry.Id).LostContact, "old host status is explicitly unknown");
            var manager = new DeltaNFD.Services.PluginManagerService(Path.Combine(root, "Plugins"));
            Require(manager.Index.TryUpsert(entry, out _), "fixture index");
            Require(!manager.TrySetEnabled(entry.Id, false, out _, out _), "unknown run prevents disabling and losing ownership");
            Require(!manager.TryUninstall(entry.Id, out _), "unknown run prevents file deletion");
            Require(!runStore.TryRemove(entry.Id, Guid.NewGuid(), out _), "wrong session cannot remove evidence");
            Require(runStore.TryRemove(entry.Id, run.SessionId, out _), "fixture cleanup with exact identity");
            File.WriteAllText(Path.Combine(dataRoot, "managed-runs.json"), "null");
            Require(restarted.IsRunning(entry.Id), "corrupt run journal blocks new work");
            Require(restarted.StopAllAsync().GetAwaiter().GetResult().Count > 0, "corrupt journal cannot claim drained");
            Console.WriteLine("Plugin integrity checks passed: full file table, locked bytes, proof binding, offline separation, tamper/missing/addition rejection.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Func<IDisposable> operation, string message)
    {
        try { using var ignored = operation(); throw new Exception("Accepted " + message); }
        catch (InvalidDataException) { }
        catch (FileNotFoundException) { }
    }
}
