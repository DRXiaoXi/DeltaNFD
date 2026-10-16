using DeltaNFD.Services;

internal static class Vc14StagedChecks
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-staged-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int count = 0;
        void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); count++; }
        Fake Fresh() => new(Path.Combine(root, Guid.NewGuid().ToString("N")));
        Task<OperationResult> Run(Fake env, Vc14RepairMode mode, CancellationToken token = default) =>
            Vc14Reinstall.RunAsync(env, Vc14Reinstall.Preview(env, mode).Fingerprint, null, token, mode);
        try
        {
            var full = Fresh();
            var ordinaryFull = Fresh();
            var prepared = await DeltaNFD.Services.AlphaRestore.RuntimeBetaRepairEnvironment.PrepareV14Async(ordinaryFull,
                Vc14Reinstall.Fingerprint(ordinaryFull.Plan(ordinaryFull.Scan())), null, default);
            Check(prepared.Success && !ordinaryFull.IsSafeMode, "full repair permits actual controlled removal in normal mode");
            Check(ordinaryFull.Calls.SequenceEqual(new[] { "remove:x64", "remove:x86", "cleanup" })
                && ordinaryFull.PendingInstall()?.UninstallVerified == true, "full repair reuses wrapper-first removal and durable verification before installing");
            var noOpRemoval = Fresh(); noOpRemoval.LeaveProducts = true;
            Check(!(await DeltaNFD.Services.AlphaRestore.RuntimeBetaRepairEnvironment.PrepareV14Async(noOpRemoval,
                Vc14Reinstall.Fingerprint(noOpRemoval.Plan(noOpRemoval.Scan())), null, default)).Success
                && noOpRemoval.Calls.Count == 1 && noOpRemoval.PendingInstall()?.UninstallVerified == false,
                "full repair stops immediately when a successful wrapper exit leaves MSI children");
            var staleProof = Fresh(); staleProof.RemoveExit = 1601;
            staleProof.SavePendingInstall(new(Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow) { UninstallVerified = true });
            var proofFailure = await Run(staleProof, Vc14RepairMode.UninstallOnly);
            Check(!proofFailure.Success && !proofFailure.IsPartial && staleProof.PendingInstall()?.UninstallVerified == false,
                "a new failed uninstall must invalidate prior completion proof without claiming successful removal");
            var changedPlan = Fresh();
            Check(!(await DeltaNFD.Services.AlphaRestore.RuntimeBetaRepairEnvironment.PrepareV14Async(changedPlan,
                "stale-fingerprint", null, default)).Success && changedPlan.Calls.Count == 0,
                "normal-mode full repair refuses a changed confirmed removal plan");
            var resumedBaseline = Fresh(); resumedBaseline.Products.Clear(); resumedBaseline.AddBaseline("x64");
            resumedBaseline.SavePendingInstall(new(Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow) { UninstallVerified = true });
            resumedBaseline.ExcludeOrphanWrappers = true;
            Check((await DeltaNFD.Services.AlphaRestore.RuntimeBetaRepairEnvironment.PrepareV14Async(resumedBaseline,
                Vc14Reinstall.Fingerprint(resumedBaseline.Plan(resumedBaseline.Scan())), null, default)).Success
                && resumedBaseline.Calls.Count == 0 && resumedBaseline.Products.Count == 2,
                "full repair retains proven partial baseline on installation retry");
            var absent = Fresh(); absent.Products.Clear(); absent.ExcludeOrphanWrappers = true;
            Check((await DeltaNFD.Services.AlphaRestore.RuntimeBetaRepairEnvironment.PrepareV14Async(absent,
                Vc14Reinstall.Fingerprint(absent.Plan(absent.Scan())), null, default)).Success
                && absent.Calls.SequenceEqual(new[] { "cleanup" }) && absent.PendingInstall()?.UninstallVerified == true,
                "missing CRT proceeds to installation after residue handling without fake uninstall");
            var plan = full.Plan(full.Scan());
            Check(plan.Count == 2 && plan.All(p => p.Kind == "bundle" && p.BundleChildren?.Count == 2), "prefer two original wrappers over four MSI children");
            Check(plan.SelectMany(p => p.BundleChildren!).Select(p => p.Product.Code).Distinct().Count() == 4, "cover every child once");
            Check((await Run(full, Vc14RepairMode.Full)).Success, "full wrapper-first reinstall");
            Check(full.Calls.SequenceEqual(new[] { "remove:x64", "remove:x86", "cleanup", "install:x64", "install:x86", "verify" }), "wrapper cleanup install verify order");

            var noCache = Fresh(); var raw = noCache.RawPlan();
            raw = raw.Select(p => p.Kind == "bundle" ? p with { CacheSha256 = "" } : p).ToArray();
            Check(Vc14Reinstall.PreferVerifiedBundles(raw).Count(p => p.Kind == "msi") == 4, "unverified wrapper cannot cover MSI children");
            var wrongName = noCache.RawPlan().Select(p => p.Kind == "bundle" ? p with { Name = "Third-party installer" } : p).ToArray();
            Check(Vc14Reinstall.PreferVerifiedBundles(wrongName).Count(p => p.Kind == "msi") == 4, "nonstandard name must not cover CRT components");
            var noDependency = noCache.RawPlan().Select(p => p.Kind == "msi" ? p with { DependencyFingerprint = "" } : p).ToArray();
            Check(Vc14Reinstall.PreferVerifiedBundles(noDependency).Count(p => p.Kind == "msi") == 4, "dependency approval mandatory for grouping");
            var wrongVersion = noCache.RawPlan().Select(p => p.Kind == "bundle" ? p with { Version = "14.51.0.0" } : p).ToArray();
            Check(Vc14Reinstall.PreferVerifiedBundles(wrongVersion).Count(p => p.Kind == "msi") == 4, "never cover different version");
            var changedChild = plan.Select(p => p with { BundleChildren = p.BundleChildren!.Select(c => c with { DependencyFingerprint = "changed" }).ToArray() }).ToArray();
            Check(Vc14Reinstall.Fingerprint(plan) != Vc14Reinstall.Fingerprint(changedChild), "confirmation binds child dependencies");
            Check(Vc14Reinstall.Fingerprint(plan) != Vc14Reinstall.Fingerprint(plan.Select(p => p with { CacheSha256 = new string('B', 64) }).ToArray()), "confirmation binds cached bytes");
            var command = Vc14Reinstall.BuildBundleUninstall(@"C:\Package Cache\vc_redist.x64.exe", @"C:\logs\bundle.log");
            Check(!command.UseShellExecute && command.FileName == @"C:\Package Cache\vc_redist.x64.exe", "direct cache process, no shell");
            Check(command.ArgumentList.SequenceEqual(new[] { "/uninstall", "/quiet", "/norestart", "/log", @"C:\logs\bundle.log" }), "fixed wrapper arguments");

            var falseSuccess = Fresh(); falseSuccess.LeaveProducts = true;
            Check(!(await Run(falseSuccess, Vc14RepairMode.Full)).Success && falseSuccess.Calls.Count == 1, "zero exit while children remain must stop immediately");
            var failed = Fresh(); failed.RemoveExit = 1601;
            var failure = await Run(failed, Vc14RepairMode.Full);
            Check(!failure.Success && !failure.IsPartial && failure.Message.Contains("未确认卸载或安装变更"), "1601 is not a claim of partial successful removal");
            Check(!failed.Calls.Any(c => c.StartsWith("install")), "service failure must not install or fallback");
            var reboot = Fresh(); reboot.RemoveExit = 3010;
            Check((await Run(reboot, Vc14RepairMode.Full)) is { Success: false, RequiresReboot: true } && reboot.Calls.Count == 1, "reboot-required stops further operations");
            var blocked = Fresh(); blocked.PlanError = true;
            Check(!Vc14Reinstall.Preview(blocked).CanRepair && blocked.Calls.Count == 0, "unknown dependency refusal cannot be bypassed by wrapper-first");

            var safe = Fresh(); safe.IsSafeMode = true;
            Check(!Vc14Reinstall.Preview(safe).CanRepair, "safe mode must not allow full install");
            Check(Vc14Reinstall.Preview(safe, Vc14RepairMode.UninstallOnly).CanRepair, "prepared safe environment may allow uninstall-only");
            var removal = await Run(safe, Vc14RepairMode.UninstallOnly);
            Check(removal.Success && removal.Message.Contains("尚未安装"), "stage completion differs from full repair completion: " + removal.Message);
            Check(safe.Products.Count == 0 && safe.Calls.SequenceEqual(new[] { "remove:x64", "remove:x86", "cleanup" }), "uninstall-only never starts install");
            Check(safe.PendingInstall()?.UninstallVerified == true, "verified uninstall completion is persisted for full repair continuation");
            Check(safe.PendingInstall() is not null && safe.SavedBeforeRemoval, "persist recovery data before first uninstall");
            Check(!Vc14Reinstall.Preview(safe, Vc14RepairMode.InstallOnly).CanRepair, "cannot continue install while in safe mode");
            var normal = new Fake(safe.Root); normal.Products.Clear();
            Check(Vc14Reinstall.Preview(normal, Vc14RepairMode.InstallOnly).CanRepair, "new process reads pending stage after simulated reboot");
            Check((await Run(normal, Vc14RepairMode.InstallOnly)).Success, "continue in normal mode");
            Check(normal.Calls.All(c => !c.StartsWith("remove")) && normal.Products.Count == 4, "resume only installs fixed baseline");
            Check(normal.PendingInstall() is null && Directory.GetFiles(Path.Combine(normal.Root, "runtime-repair-history")).Length == 1, "archive checkpoint only after final verification");

            var none = Fresh();
            Check(!Vc14Reinstall.Preview(none, Vc14RepairMode.InstallOnly).CanRepair, "no checkpoint, no install-only execution");
            none.SavePendingInstall(new(Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow));
            Check(!Vc14Reinstall.Preview(none, Vc14RepairMode.InstallOnly).CanRepair, "checkpoint must not bypass remaining old components");
            var partial = Fresh(); partial.Products.Clear(); partial.AddBaseline("x64");
            partial.SavePendingInstall(new(Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow));
            Check((await Run(partial, Vc14RepairMode.InstallOnly)).Success && !partial.Calls.Contains("cleanup"), "partial baseline retry preserves installed components and repairs both architectures");
            var installFail = Fresh(); installFail.Products.Clear(); installFail.InstallFail = "x86";
            installFail.SavePendingInstall(new(Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow));
            Check(!(await Run(installFail, Vc14RepairMode.InstallOnly)).Success && installFail.PendingInstall() is not null, "failed baseline install retains checkpoint");
            installFail.InstallFail = "";
            Check((await Run(installFail, Vc14RepairMode.InstallOnly)).Success, "resume partial failed install without another uninstall");
            var stale = Fresh(); var consent = Vc14Reinstall.Preview(stale, Vc14RepairMode.UninstallOnly);
            Check(!(await Vc14Reinstall.RunAsync(stale, consent.Fingerprint, null, default)).Success && stale.Calls.Count == 0, "mode switching invalidates confirmation");
            var installConsent = Fresh(); installConsent.Products.Clear(); installConsent.SavePendingInstall(new(Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow));
            var before = Vc14Reinstall.Preview(installConsent, Vc14RepairMode.InstallOnly);
            installConsent.SavePendingInstall(new(Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow));
            Check(!(await Vc14Reinstall.RunAsync(installConsent, before.Fingerprint, null, default, Vc14RepairMode.InstallOnly)).Success, "checkpoint identity bound to consent");
            var cannotSave = Fresh(); cannotSave.SaveError = true;
            Check(!(await Run(cannotSave, Vc14RepairMode.UninstallOnly)).Success && cannotSave.Calls.Count == 0, "checkpoint persistence failure must prevent uninstall");
            var corrupt = Fresh(); Directory.CreateDirectory(corrupt.Root);
            var corruptFile = Path.Combine(corrupt.Root, "vc14-awaiting-install.json"); File.WriteAllText(corruptFile, "{broken");
            Check(!Vc14Reinstall.Preview(corrupt, Vc14RepairMode.InstallOnly).CanRepair && File.ReadAllText(corruptFile) == "{broken", "bad evidence preserved and not executed");
            using var cancel = new CancellationTokenSource(); cancel.Cancel(); var cancelled = Fresh();
            Check(!(await Run(cancelled, Vc14RepairMode.UninstallOnly, cancel.Token)).Success && cancelled.Calls.Count == 0 && cancelled.PendingInstall() is null, "cancel before execution creates no checkpoint or operations");

            Check(Vc14InstallerReadiness.Evaluate(true, true, false, false)?.Contains("0x8007043C") == true, "safe mode stopped service blocks before installer");
            Check(Vc14InstallerReadiness.Evaluate(true, true, false, true) is null, "running service may proceed to further checks");
            Check(Vc14InstallerReadiness.Evaluate(false, true, false, false) is null, "normal manual-start service can be started by installer");
            Check(Vc14InstallerReadiness.Evaluate(false, false, false, false) is not null, "missing service blocks");
            Check(Vc14InstallerReadiness.Evaluate(false, true, true, false) is not null, "disabled service blocks");
            var serviceBlocked = Fresh(); serviceBlocked.IsSafeMode = true; serviceBlocked.ServiceBlocked = true;
            Check(!Vc14Reinstall.Preview(serviceBlocked, Vc14RepairMode.UninstallOnly).CanRepair
                && !(await Run(serviceBlocked, Vc14RepairMode.UninstallOnly)).Success && serviceBlocked.Calls.Count == 0
                && serviceBlocked.PendingInstall() is null, "unavailable Installer must block before checkpoint or uninstall");
            var invalidMode = Fresh();
            Check(!Vc14Reinstall.Preview(invalidMode, (Vc14RepairMode)99).CanRepair, "unknown stage rejected");
            Console.WriteLine($"VC14 staged/wrapper checks: {count} passed. All install/uninstall/service scenarios mocked; no real services or runtimes changed.");
        }
        finally
        {
            // This is a newly created test-owned directory, never user data.
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }

    private sealed class Fake(string root) : IVc14ReinstallEnvironment
    {
        public string Root => root;
        public bool IsSafeMode { get; set; }
        public bool PlanError, LeaveProducts, SaveError, SavedBeforeRemoval, ServiceBlocked, ExcludeOrphanWrappers;
        public int RemoveExit;
        public string InstallFail = "";
        public List<string> Calls = [];
        public List<Vc14Entry> Products = Vc14Catalog.Families.Select((f, i) => new Vc14Entry(
            "{" + (i + 1).ToString("x8") + "-0000-0000-0000-000000000001}",
            "Microsoft Visual C++ 2022 " + f.Arch.ToUpperInvariant() + " " + f.Role + " Runtime - 14.40.33816", "14.40.33816", f.Arch, f.Role, f.Upgrade)).ToList();
        private Vc14ContinuationStore Store => new(root);
        public string DiagnosticsDirectory => root;
        public Vc14Scan Scan() => new(Products.ToArray(), [], []);
        public string? Blocker() => ServiceBlocked ? Vc14InstallerReadiness.Evaluate(IsSafeMode, true, false, false) : null;
        public void VerifyBaselines() { }
        public void VerifyInstalled() { Calls.Add("verify"); if (Products.Count != 4) throw new IOException("incomplete baseline"); }
        public void Trace(string stage, object value) { }
        public Vc14Continuation? PendingInstall() => Store.Read();
        public void SavePendingInstall(Vc14Continuation record) { if (SaveError) throw new IOException("fixture disk full"); Store.Save(record); }
        public void CompletePendingInstall() => Store.Complete();
        public OperationResult Cleanup(Vc14Scan scan, IProgress<string>? progress, CancellationToken token) { Calls.Add("cleanup"); return OperationResult.Ok("无可清理项"); }
        public IReadOnlyList<Vc14RemovalPlanItem> RawPlan()
        {
            var items = Products.Select(p => new Vc14RemovalPlanItem(p.Code, p.Architecture, p.Name, p.Version, "msi", p.Code, "msi", "fixture", p.Role, p.UpgradeCode, DependencyFingerprint: "approved")).ToList();
            foreach (var arch in new[] { "x64", "x86" })
            {
                if (ExcludeOrphanWrappers) continue;
                items.Add(new("bundle-" + arch, arch, "Microsoft Visual C++ 2015-2022 Redistributable (" + arch + ") - 14.40.33816", "14.40.33816.0", "bundle", "fixture-cache", "bundle", "fixture", CacheSha256: new string('A', 64)));
            }
            return items;
        }
        public IReadOnlyList<Vc14RemovalPlanItem> Plan(Vc14Scan scan)
        { if (PlanError) throw new IOException("unknown third-party dependency"); return Vc14Reinstall.PreferVerifiedBundles(RawPlan()); }
        public Task<Vc14StepResult> RemoveAsync(Vc14RemovalPlanItem item, IProgress<string>? progress, CancellationToken token)
        {
            Calls.Add("remove:" + item.Architecture); SavedBeforeRemoval = PendingInstall() is not null;
            if (RemoveExit == 0 && !LeaveProducts) Products.RemoveAll(p => item.BundleChildren?.Any(c => c.Product.Code == p.Code) == true);
            return Task.FromResult(new Vc14StepResult(RemoveExit));
        }
        public void AddBaseline(string arch)
        {
            Products.RemoveAll(p => p.Architecture == arch);
            Products.AddRange(Vc14Catalog.Families.Where(f => f.Arch == arch).Select(f => new Vc14Entry(Guid.NewGuid().ToString("B"),
                "Microsoft Visual C++ 2022 " + arch + " " + f.Role + " Runtime", Vc14Reinstall.BaselineVersion, arch, f.Role, f.Upgrade)));
        }
        public Task<Vc14StepResult> InstallAsync(string arch, IProgress<string>? progress, CancellationToken token)
        { Calls.Add("install:" + arch); if (InstallFail == arch) return Task.FromResult(new Vc14StepResult(1603)); AddBaseline(arch); return Task.FromResult(new Vc14StepResult(0)); }
    }
}
