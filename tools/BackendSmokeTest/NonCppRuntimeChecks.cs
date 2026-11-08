using DeltaNFD.Backends;
using DeltaNFD.Services;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

internal static class NonCppRuntimeChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
        Check(BackendModules.IsReadOnly(BackendModules.Runtime, BackendCommands.RuntimeNonCppPreview), "non-C++ preview read-only boundary");
        Check(BackendModules.RequiresWriteIntent(BackendModules.Runtime, BackendCommands.RuntimeNonCppRepair), "non-C++ repair requires intent");
        Check(EmergencyPolicyVerifier.CanBlock(BackendCommands.RuntimeNonCppRepair), "non-C++ repair emergency gate");
        Check(new Vc14RepairArgs().Baseline == Vc14Baseline.V144 && new Vc14ReinstallPreview(new([], [], []), [], "", false, "").Baseline == Vc14Baseline.V144, "new contracts fixed 144");
        Check(!Vc14Baseline.V142.IsSupported() && Vc14Baseline.V144.IsSupported(), "only 144 admitted");
        Check(!(await Vc14Maintenance.PreviewAsync(baseline: Vc14Baseline.V142)).CanRepair, "retired baseline preview rejected before system queries");
        Check(!(await Vc14Maintenance.RepairAsync("", null, default, baseline: Vc14Baseline.V142)).Success, "retired baseline repair rejected without execution");
        Check((int)Vc14Baseline.V142 == 0 && (int)Vc14Baseline.V144 == 1, "historical wire values preserved");
        var catalog = NonCppRuntimeMaintenance.Sources();
        Check(catalog.Length == 186, "catalog exact 186");
        Check(catalog.Count(f => f.OsManaged) == 4, "four OS entries excluded");
        Check(catalog.Count(f => f.RegisterCom && !f.OsManaged) == 54, "54 explicit COM exports");
        foreach (var f in catalog) { NonCppRuntimeMaintenance.Validate(f); count++; }
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-noncpp-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = File.ReadAllBytes(typeof(NonCppRuntimeChecks).Assembly.Location);
            using var pe = new PEReader(new MemoryStream(bytes));
            var arch = (ushort)pe.PEHeaders.CoffHeader.Machine == 0x8664 ? "x64" : "x86";
            var f = new NonCppSource("d3dx9_43.dll", arch, arch + "/d3dx9_43.dll", Convert.ToHexString(SHA256.HashData(bytes)), "1.0.0.0", bytes.Length, false, false);
            var e = new Fake(root);
            Directory.CreateDirectory(Path.Combine(e.Assets, arch)); Directory.CreateDirectory(Path.GetDirectoryName(e.Target(f))!);
            File.WriteAllBytes(Path.Combine(e.Assets, f.RelativePath), bytes);
            var scan = await NonCppRuntimeMaintenance.PreviewAsync(e, [f], default);
            Check(scan.CanRepair && scan.Files.Single().NeedsRepair, "missing detected");
            Check(!File.Exists(e.Target(f)), "preview never writes target");
            var bad = await NonCppRuntimeMaintenance.RepairAsync(e, [f], "stale", null, default);
            Check(!bad.Success && !File.Exists(e.Target(f)), "stale fingerprint rejected");
            e.Block = "game/authority unknown";
            Check(!(await NonCppRuntimeMaintenance.RepairAsync(e, [f], scan.Fingerprint, null, default)).Success, "environment rejection");
            e.Block = null;
            var applied = await NonCppRuntimeMaintenance.RepairAsync(e, [f], scan.Fingerprint, null, default);
            Check(applied.Success, "missing repair: " + applied.Message);
            Check(File.ReadAllBytes(e.Target(f)).SequenceEqual(bytes), "written bytes verified");
            Check(e.RegisterCount == 0, "non COM never registers");
            scan = await NonCppRuntimeMaintenance.PreviewAsync(e, [f], default);
            Check(!scan.Files.Single().NeedsRepair && scan.CanRepair, "healthy file retained and completed backup not blocker");
            Check((await NonCppRuntimeMaintenance.RestoreAsync(e, [f], null, default)).Success && !File.Exists(e.Target(f)), "created file restored to missing");
            File.WriteAllText(e.Target(f), "corrupt old bytes");
            scan = await NonCppRuntimeMaintenance.PreviewAsync(e, [f], default);
            Check(scan.CanRepair && scan.Files.Single().NeedsRepair, "invalid PE classified repairable");
            Check((await NonCppRuntimeMaintenance.RepairAsync(e, [f], scan.Fingerprint, null, default)).Success, "corrupt repair");
            Check((await NonCppRuntimeMaintenance.RestoreAsync(e, [f], null, default)).Success, "existing bytes ACL restore");
            Check(File.ReadAllText(e.Target(f)) == "corrupt old bytes", "original bytes exactly restored");
            var os = f with { Name = "xinput9_1_0.dll", RelativePath = arch + "/xinput9_1_0.dll", OsManaged = true };
            scan = await NonCppRuntimeMaintenance.PreviewAsync(e, [os], default);
            Check(scan.CanRepair && !scan.Files.Single().NeedsRepair, "missing OS component diagnostic only");
            File.WriteAllText(Path.Combine(e.Assets, f.RelativePath), "tampered");
            Check(!(await NonCppRuntimeMaintenance.PreviewAsync(e, [f], default)).CanRepair, "tampered source blocked");
            File.WriteAllBytes(Path.Combine(e.Assets, f.RelativePath), bytes);
            e.Trust = false;
            Check(!(await NonCppRuntimeMaintenance.PreviewAsync(e, [f], default)).CanRepair, "untrusted source blocked");
            e.Trust = true;
            scan = await NonCppRuntimeMaintenance.PreviewAsync(e, [f], default);
            var interrupted = await NonCppRuntimeMaintenance.RepairAsync(e, [f], scan.Fingerprint,
                new SyncProgress(() => File.WriteAllText(Path.Combine(e.Assets, f.RelativePath), "truncated source")), default);
            Check(!interrupted.Success, "staging failure reported");
            Check(File.ReadAllText(e.Target(f)) == "corrupt old bytes", "staging failure leaves original complete");
            Check((await NonCppRuntimeMaintenance.RestoreAsync(e, [f], null, default)).Success, "uncommitted write journal recoverable");
            Check(!Directory.EnumerateFiles(Path.GetDirectoryName(e.Target(f))!, "*.tmp").Any(), "failed stage cleaned");
            File.WriteAllBytes(Path.Combine(e.Assets, f.RelativePath), bytes);
            scan = await NonCppRuntimeMaintenance.PreviewAsync(e, [f], default);
            Check((await NonCppRuntimeMaintenance.RepairAsync(e, [f], scan.Fingerprint, null, default)).Success, "repeat repair keeps earlier backups separate");
            var journalPath = Path.Combine(e.State, "journal.json");
            var journal = JsonSerializer.Deserialize<NonCppJournal>(File.ReadAllText(journalPath))!;
            File.WriteAllText(journalPath, JsonSerializer.Serialize(journal with { Files = [journal.Files[0] with { Acl = "" }] }));
            Check(!(await NonCppRuntimeMaintenance.RestoreAsync(e, [f], null, default)).Success, "missing ACL cannot discharge restore");
            Check(!(await NonCppRuntimeMaintenance.RestoreAsync(e, [f], null, default)).Success && File.Exists(journalPath), "retry verifies ACL even when bytes already restored");
            File.WriteAllText(journalPath, JsonSerializer.Serialize(journal));
            Check((await NonCppRuntimeMaintenance.RestoreAsync(e, [f], null, default)).Success, "valid ACL evidence finishes retry");
            var com = f with { RegisterCom = true };
            scan = await NonCppRuntimeMaintenance.PreviewAsync(e, [com], default);
            Check((await NonCppRuntimeMaintenance.RepairAsync(e, [com], scan.Fingerprint, null, default)).Success && e.RegisterCount == 1, "explicit COM adapter only");
            var restored = await NonCppRuntimeMaintenance.RestoreAsync(e, [com], null, default);
            Check(!restored.Success && restored.OutcomeUnknown, "COM not falsely rolled back");
            Check((await NonCppRuntimeMaintenance.PreviewAsync(e, [com], default)).RecoveryPending, "COM responsibility evidence retained");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"NonCppRuntimeChecks: {count} passed (fake environment; no Windows/DLL execution)");
    }
    private sealed class SyncProgress(Action action) : IProgress<string>
    {
        public void Report(string _) => action();
    }
    private sealed class Fake(string root) : INonCppRuntimeEnvironment
    {
        public string Assets => Path.Combine(root, "assets");
        public string State => Path.Combine(root, "state");
        public string Target(NonCppSource f) => Path.Combine(root, "targets", f.Architecture, f.Name);
        public string? Block; public bool Trust = true; public int RegisterCount;
        public Task<string?> BlockerAsync(CancellationToken token) => Task.FromResult(Block);
        public bool Trusted(string path) => Trust;
        public Task RegisterAsync(NonCppSource f, string path, CancellationToken token) { RegisterCount++; return Task.CompletedTask; }
    }
}
