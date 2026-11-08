using DeltaNFD.Services;
using DeltaNFD.Services.AlphaRestore;

internal static class CppReinstallChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        var root = Path.Combine(Path.GetTempPath(), "cpp-reinstall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var isolation = Guid.NewGuid(); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), isolation.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, isolation);
        try
        {
            var package = OfficialCppCatalog.Packages[0];
            async Task<RuntimeStepResult> Run(Fake fake, CancellationToken token = default) =>
                await new CppReinstaller(fake, new RuntimeRepairDiagnostics(root)).RunAsync(package, TimeSpan.FromSeconds(5), null, token);
            var code = "{67A8C9D0-1234-4321-9876-123456789ABC}";
            var source = new RuntimeMsiPackage(code, "Microsoft Visual C++ 2005 Redistributable (x64)", "Microsoft Corporation", "8.0.2.0", "x64",
                [new("component", "msvcr80.dll", "msvcr80.dll", new Version(8, 0, 2, 0))]) { UpgradeCode = "{67A8C9D0-1234-4321-9876-123456789ABD}" };
            var installed = new RuntimeMsiProduct(code, 5, source.Name, source.Publisher, source.Version, source.Architecture);
            CppReinstallEnvironment.ValidateReplacement(source, source, installed, "2005", "x64");
            Check(true, "actual replacement policy accepts verified same version");
            foreach (var bad in new[] { source with { UpgradeCode = Guid.NewGuid().ToString("B") }, source with { Publisher = "other" },
                source with { Architecture = "x86" }, source with { Version = "8.0.3.0" }, source with { Name = "Microsoft Visual C++ 2005 Debug" },
                source with { Files = [] }, source with { Product = Guid.NewGuid().ToString("B") } })
            {
                var identity = new RuntimeMsiProduct(bad.Product, 5, bad.Name, bad.Publisher, bad.Version, bad.Architecture);
                try { CppReinstallEnvironment.ValidateReplacement(source, bad, bad.Product == code ? identity : installed, "2005", "x64"); throw new Exception("invalid identity accepted"); }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { count++; }
            }
            var healthy = new Fake();
            Check((await Run(healthy)).Success && healthy.Calls.SequenceEqual(new[] { "remove", "install" }), "healthy CPP must uninstall then install, never skip/repair");
            var missing = new Fake { Present = false };
            Check((await Run(missing)).Success && missing.Calls.SequenceEqual(new[] { "install" }), "missing CPP installs original");
            foreach (var reason in new[] { "unknown", "newer", "dependency", "bad-cache", "bad-hash" })
            {
                var blocked = new Fake { Blocker = reason };
                Check(!(await Run(blocked)).Success && blocked.Calls.Count == 0, "blocked before mutation: " + reason);
            }
            foreach (var stop in new RuntimeStepResult[] { new(1603), new(3010), new(1641), new(null, OutcomeUnknown: true) })
            {
                var fake = new Fake { Removal = stop };
                var result = await Run(fake);
                Check(!result.Success || result.RequiresReboot, "failure/reboot preserved");
                Check(fake.Calls.SequenceEqual(new[] { "remove" }) && result.OutcomeUnknown == stop.OutcomeUnknown
                    && result.RequiresReboot == stop.RequiresReboot, "no install after removal failure/reboot/unknown");
            }
            var noOp = new Fake { RemovalNoOp = true };
            Check(!(await Run(noOp)).Success && noOp.Calls.Count == 1, "uninstall no-op must block original install");
            var changed = new Fake { ChangePlan = true };
            Check(!(await Run(changed)).Success && changed.Calls.Count == 0, "plan race before remove blocks");
            var stale = new Fake();
            var rejected = await new CppReinstaller(stale, new RuntimeRepairDiagnostics(root)).RunAsync(package,
                TimeSpan.FromSeconds(5), null, default, "stale-confirmation");
            Check(!rejected.Success && stale.Calls.Count == 0, "confirmed plan fingerprint blocks changed target");
            var appeared = new Fake { AppearAtInstall = true };
            Check(!(await Run(appeared)).Success && appeared.Calls.SequenceEqual(new[] { "remove" }), "newly appeared product cannot become repair or skip");
            var badInstall = new Fake { InstallNoOp = true };
            Check(!(await Run(badInstall)).Success, "install no-op cannot claim success");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            var cancelled = new Fake();
            try { await Run(cancelled, cancel.Token); throw new Exception("cancel lost"); }
            catch (OperationCanceledException) { Check(cancelled.Calls.Count == 0, "cancel before remove"); }
            var full = new FullFake();
            var resultFull = await new RuntimeBetaRepair(full, new RuntimeRepairDiagnostics(root)).RunAsync(null, default);
            Check(resultFull.Success && full.Cpp == 10 && full.Baseline == 2 && full.Aio == 0, "full CPP entry executes ten originals and two baselines without AIO");
            Check(Vc14BaselineInfo.Default.Id == Vc14Baseline.V144 && new RuntimeHighFpsArgs().Baseline == Vc14Baseline.V144
                && new Vc14ForceReplaceArgs().Baseline == Vc14Baseline.V144, "new defaults 14.44");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"CPP reinstall: {count} checks passed; fake APIs only, no installer executed.");
    }

    private sealed class Fake : ICppReinstallEnvironment
    {
        internal bool Present = true, RemovalNoOp, InstallNoOp, ChangePlan, AppearAtInstall;
        private int _verifications;
        internal string Blocker = "";
        internal int Plans;
        internal List<string> Calls = [];
        internal RuntimeStepResult Removal = new(0);
        private const string Code = "{67A8C9D0-1234-4321-9876-123456789ABC}";
        private static RuntimeMsiPackage Source => new(Code, "Microsoft Visual C++ 2005 Redistributable (x64)", "Microsoft Corporation", "8.0.1.0", "x64", []) { UpgradeCode = "{67A8C9D0-1234-4321-9876-123456789ABD}" };
        public IReadOnlyList<RuntimeMsiPackage> Verify(OfficialCppPackage package)
        { if (++_verifications == 4 && AppearAtInstall) Present = true; return [Source]; }
        public RuntimeMsiProduct ReadProduct(string product) => new(product, Present ? 5 : -1, Source.Name, Source.Publisher, Source.Version, "x64");
        public IReadOnlyList<RuntimeMsiPackage> NewerRelated(RuntimeMsiPackage source) => [];
        public OperationResult VerifyFiles(RuntimeMsiPackage package) => OperationResult.Ok("mock files");
        public IReadOnlyList<RuntimeUnifiedUninstallPlan> PlanRemoval(OfficialCppPackage package, IReadOnlyList<RuntimeMsiPackage> sources)
        {
            if (Blocker.Length > 0) throw new IOException(Blocker);
            Plans++;
            if (!Present) return [];
            var entry = new VcRedistEntry { KeyName = Code, DisplayName = Source.Name, Architecture = "x64", UninstallKind = "Msi", DisplayVersion = ChangePlan && Plans > 1 ? "changed" : Source.Version };
            return [new(entry, [Code], [])];
        }
        public Task<RuntimeStepResult> RemoveAsync(RuntimeUnifiedUninstallPlan plan, TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
        { Calls.Add("remove"); if (Removal.Success && !RemovalNoOp) Present = false; return Task.FromResult(Removal); }
        public Task<RuntimeStepResult> ExecuteAsync(OfficialCppPackage package, bool repair, string logPath, TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
        { Calls.Add(repair ? "repair" : "install"); if (!InstallNoOp) Present = true; return Task.FromResult(new RuntimeStepResult(0)); }
    }

    private sealed class FullFake : IRuntimeBetaRepairEnvironment
    {
        internal int Cpp, Baseline, Aio;
        public void VerifyInstalled() { }
        public Task<OperationResult> VerifyAsync(IProgress<string>? progress, CancellationToken token) => Task.FromResult(OperationResult.Ok("ready"));
        public Task<VcRedistScanReport> ScanAsync(CancellationToken token) => Task.FromResult(new VcRedistScanReport { All = [], Abnormal = [], Suboptimal = [], Advice = "" });
        public Task<OperationResult> PrepareV14Async(IProgress<string>? progress, CancellationToken token) => Task.FromResult(OperationResult.Ok("removed"));
        public Task<RuntimeStepResult> InstallAioAsync(TimeSpan timeout, IProgress<string>? progress, CancellationToken token) { Aio++; throw new Exception("CPP must never execute AIO"); }
        public List<RuntimePackageResult> ReadAioResults() => throw new Exception("CPP must never read AIO results");
        public Task<RuntimeStepResult> InstallBaselineAsync(string arch, TimeSpan timeout, IProgress<string>? progress, CancellationToken token) { Baseline++; return Task.FromResult(new RuntimeStepResult(0)); }
        public Task<RuntimeStepResult> OverwriteCppAsync(RuntimeRepairStep step, TimeSpan timeout, IProgress<string>? progress, CancellationToken token) { Cpp++; return Task.FromResult(new RuntimeStepResult(0)); }
    }
}
