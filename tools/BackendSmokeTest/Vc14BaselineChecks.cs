using System.Diagnostics;
using System.Security.Cryptography;
using DeltaNFD.Services;

internal static class Vc14BaselineChecks
{
    internal static async Task RunAsync(string assetsRoot)
    {
        int count = 0;
        void Check(bool ok, string text) { if (!ok) throw new IOException(text); count++; }
        void Reject(Action action, string text)
        { try { action(); } catch (IOException) { count++; return; } throw new IOException("Expected refusal: " + text); }
        var assets = Path.GetFullPath(assetsRoot);var media = Path.Combine(assets, "Vc14FileRepair14.44");
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-vc144-only-" + Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid();Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        Check((int)Vc14Baseline.V142 == 0 && (int)Vc14Baseline.V144 == 1, "Legacy wire enum unchanged");
        Check(Vc14BaselineOption.Default == Vc14Baseline.V144 && Vc14Baseline.V144.IsSupported() && !Vc14Baseline.V142.IsSupported(), "Only current baseline supported");
        Check(Vc14Baseline.V142.Label().Contains("已过时") && Vc14Baseline.V142.Description().Contains("不再提供"), "Obsolete label explicit");
        Check(new Vc14RepairArgs().Baseline == Vc14Baseline.V144 && new Vc14ForceReplaceArgs().Baseline == Vc14Baseline.V144 && new RuntimeHighFpsArgs().Baseline == Vc14Baseline.V144, "All command defaults current");
        Check(Vc14BaselineInfo.All.Count == 1 && Vc14BaselineInfo.Default.Id == Vc14Baseline.V144, "Single install source");
        Reject(() => Vc14BaselineInfo.For(Vc14Baseline.V142), "Old local media cannot be selected");
        Check(Vc14BaselineInfo.FromBundle("x64", "{804e7d66-ccc2-4c12-84ba-476da31d103d}") is null, "Legacy wrapper does not require a removed pinned source");
        foreach (var arch in new[] { "x64", "x86" })
        {
            var exe = Path.Combine(assets, "Vc14Baseline14.44", "vc_redist." + arch + ".exe");
            Check(Hash(exe) == Vc14BaselineInfo.V144.ExeHashes[arch], "Exact Microsoft original: " + arch);
            Check(Vc14FileRepairSource.Verify(media, arch).Count == 2, "Default MSI/CAB identity: " + arch);
            Reject(() => Vc14FileRepairSource.Verify(media, arch, Vc14Baseline.V142), "Retired source refused");
        }
        Check(Vc14FileRepairSource.DirectoryName == "Vc14FileRepair14.44" && Vc14FileRepairSource.Packages.All(p => Vc14Catalog.SameVersion(p.Version, "14.44.35211.0")), "Default catalog current");
        Check(Vc14ForceReplace.PayloadSet(Vc14Baseline.V144).Files.Count == 51 && Vc14ForceReplace.ExpectedVersion == "14.44.35211.0", "Current force payload");
        Reject(() => Vc14ForceReplace.PayloadSet(Vc14Baseline.V142), "Retired force payload absent");
        Check(Vc14Catalog.Classify("Microsoft Visual C++ 2022", "14.42.34433.0") == Vc14Severity.Warning, "Old installed version yellow");
        Check(Vc14CatalogInspection.CompatibilityDetail("14.42.34433.0", "已核验").Contains("已过时"), "Notice explains obsolete version");
        Check(Vc14Catalog.Classify("Microsoft Visual C++ 2022", "14.44.35211.0") == Vc14Severity.Normal, "Current installed version green");
        Check(Vc14Catalog.Classify("Microsoft Visual C++ 2022", "14.50.0") == Vc14Severity.Abnormal && Vc14Catalog.Classify("Microsoft Visual C++ 2022", "14.51.0") == Vc14Severity.Abnormal, "Red rules retained");
        var bundle = Vc14BaselineInfo.V142.BundleIds["x64"];
        var product = new Vc14Entry("{A0A0A0A0-0000-0000-0000-000000000144}", "Microsoft Visual C++ 2022 X64 Minimum Runtime - 14.42.34433", "14.42.34433.0", "x64", "Minimum", "{36F68A90-239C-34DF-B58C-64B30153CE35}");
        var provider = new Vc14DependencyProvider("fixture", "Microsoft.VS.VC_RuntimeMinimumVSU_amd64,v14", product.Code, "", [bundle]);
        Check(new Vc14Scan([product], [], []).HasObsolete && !new Vc14Scan([product with { Version = "14.44.35211.0" }], [], []).HasObsolete,
            "Homepage marks obsolete only when installed MSI proves 14.42");
        Check(!new Vc14Scan([], [new("old wrapper", "14.42.34433.0", "x64", Vc14Severity.Warning, "registration only")], []).HasObsolete,
            "Wrapper-only notice never proves installed obsolete runtime");
        var owner = new Vc14DependencyOwner("fixture", "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.42.34433", product.Version, "Microsoft Corporation", true, true, "",
            new("fixture-cache", Vc14BaselineInfo.V142.ExeHashes["x64"], bundle, product.Version, "x64", true, "verified fixture"));
        Check(Vc14DependencyPolicy.Evaluate(product, [provider], _ => [owner], _ => false).Argument == "IGNOREDEPENDENCIES=" + bundle, "Verified historical wrapper removable with its own cached proof");
        var legacy = new Vc14Continuation(Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow) { UninstallVerified = true, Baseline = Vc14Baseline.V142 };
        var store = new Vc14ContinuationStore(root);store.Save(legacy);
        Check(store.Read() == legacy, "Old record remains readable and unchanged");
        var oldWire = System.Text.Json.JsonSerializer.Serialize(new { legacy.Id, legacy.CreatedUtc, legacy.UninstallVerified });
        Check(System.Text.Json.JsonSerializer.Deserialize<Vc14Continuation>(oldWire)?.Baseline == Vc14Baseline.V142,
            "Missing historical field still means legacy 14.42, not a new 14.44 transaction");
        Check(legacy.CanContinueWith(Vc14Baseline.V144) && !(legacy with { UninstallVerified = false }).CanContinueWith(Vc14Baseline.V144), "Old continuation requires confirmed uninstall");
        var env = new FakeEnvironment(root, legacy);var preview = Vc14Reinstall.Preview(env, Vc14RepairMode.InstallOnly);
        Check(preview.CanRepair && env.Saves == 0 && env.Installs == 0, "Preview never migrates or installs");
        var result = await Vc14Reinstall.RunAsync(env, preview.Fingerprint, null, CancellationToken.None, Vc14RepairMode.InstallOnly);
        Check(result.Success && env.Installs == 2 && env.Pending?.Baseline == Vc14Baseline.V144 && env.Pending.Id == legacy.Id && env.Complete, "Confirmed migration installs current baseline and preserves record identity: " + result.Message + "; installs=" + env.Installs + "; pending=" + env.Pending?.Baseline);
        Check(env.Removals == 0, "No repeated old uninstall");
        Check(!Vc14Reinstall.Preview(new FakeEnvironment(root, legacy with { UninstallVerified = false }), Vc14RepairMode.InstallOnly).CanRepair, "Unverified old transaction needs full preview");
        var stale = new FakeEnvironment(root, legacy);var oldPreview = Vc14Reinstall.Preview(stale, Vc14RepairMode.InstallOnly);stale.Pending = legacy with { CreatedUtc = legacy.CreatedUtc.AddSeconds(1) };
        Check(!(await Vc14Reinstall.RunAsync(stale, oldPreview.Fingerprint, null, CancellationToken.None, Vc14RepairMode.InstallOnly)).Success && stale.Installs == 0, "Stale consent cannot execute");
        var host = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msvcp140.dll");var before = File.Exists(host) ? Hash(host) : "";
        var payload = Vc14ForceReplace.ExtractPayload(media, Path.Combine(root, "extracted"), CancellationToken.None);
        Check(payload.Count == 51 && payload.Values.All(p => FileVersionInfo.GetVersionInfo(p).FileVersion == "14.44.35211.0"), "Current official DLLs only in fake root");
        Check(!File.Exists(host) || Hash(host) == before, "Host runtime unchanged");
        Console.WriteLine($"VC14 14.44-only baseline checks passed: {count}; fake transactions and isolated records only.");
    }
    private static string Hash(string p) { using var f = File.OpenRead(p);return Convert.ToHexString(SHA256.HashData(f)); }
    private sealed class FakeEnvironment(string root, Vc14Continuation? pending) : IVc14ReinstallEnvironment
    {
        public Vc14Baseline Baseline => Vc14Baseline.V144;public Vc14Continuation? Pending = pending;
        public int Saves, Installs, Removals;public bool Complete;
        private readonly List<Vc14Entry> _installed = [];
        public Vc14Scan Scan() => new(_installed.ToArray(), [], []);public IReadOnlyList<Vc14RemovalPlanItem> Plan(Vc14Scan scan) => [];
        public IReadOnlyList<Vc14ResidueItem> Residues(Vc14Scan scan) => [];
        public OperationResult Cleanup(Vc14Scan scan, IProgress<string>? progress, CancellationToken token) => OperationResult.Ok("fake");
        public string? Blocker() => null;public void VerifyBaselines() { }public void VerifyInstalled() { }
        public string DiagnosticsDirectory => root;public void Trace(string stage, object value) { }
        public Vc14Continuation? PendingInstall() => Pending;public void SavePendingInstall(Vc14Continuation record) { Pending = record;Saves++; }
        public void CompletePendingInstall() => Complete = true;
        public Task<Vc14StepResult> RemoveAsync(Vc14RemovalPlanItem item, IProgress<string>? progress, CancellationToken token) { Removals++;throw new Exception("No real uninstall"); }
        public Task<Vc14StepResult> InstallAsync(string arch, IProgress<string>? progress, CancellationToken token)
        {
            Installs++;
            _installed.AddRange(Vc14FileRepairSource.Packages.Where(p => p.Architecture == arch)
                .Select(p => new Vc14Entry(p.Code, p.Name, p.Version, arch, p.Role, p.UpgradeCode)));
            return Task.FromResult(new Vc14StepResult(0));
        }
        public Task<Vc14StepResult> ForceFileRepairAsync(string arch, IProgress<string>? progress, CancellationToken token)
            => Task.FromResult(new Vc14StepResult(0));
    }
}
