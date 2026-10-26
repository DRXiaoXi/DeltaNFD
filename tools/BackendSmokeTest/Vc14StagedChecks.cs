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
            var diagnosticProbe = Fresh();
            var firstScan = diagnosticProbe.Scan();
            var laterScan = diagnosticProbe.Scan();
            var diagnosticPlan = diagnosticProbe.Plan(firstScan);
            Check(firstScan.MsiStateDiagnostics.Count > 0
                && firstScan.MsiStateDiagnostics[0].CapturedUtc != laterScan.MsiStateDiagnostics[0].CapturedUtc,
                "fixture carries changing diagnostic timestamps across otherwise identical scans");
            foreach (var mode in Enum.GetValues<Vc14RepairMode>())
                Check(Vc14Reinstall.StageFingerprint(diagnosticPlan, mode, null, firstScan)
                    == Vc14Reinstall.StageFingerprint(diagnosticPlan, mode, null, laterScan),
                    "diagnostic timestamps must not invalidate confirmation: " + mode);
            var notices = firstScan.Products.Select(p => new Vc14Notice(p.Name, p.Version, p.Architecture,
                Vc14Severity.Warning, "fixture decision")).ToArray();
            var orderedScan = firstScan with { Notices = notices };
            string Approval(Vc14Scan scan) => Vc14Reinstall.StageFingerprint(diagnosticPlan, Vc14RepairMode.Full, null, scan);
            Check(Approval(orderedScan) == Approval(orderedScan with
                { Products = Enumerable.Reverse(orderedScan.Products).ToArray(), Notices = Enumerable.Reverse(notices).ToArray() }),
                "product and notice enumeration order must not invalidate confirmation");
            Check(Approval(orderedScan) != Approval(orderedScan with
                { Products = [orderedScan.Products[0] with { Version = "14.51.36247" }, .. orderedScan.Products.Skip(1)] }),
                "actual product changes still invalidate confirmation");
            Check(Approval(orderedScan) != Approval(orderedScan with
                { Notices = [notices[0] with { Severity = Vc14Severity.Abnormal }, .. notices.Skip(1)] }),
                "notice decisions remain part of confirmation");
            Check(Approval(orderedScan) != Approval(orderedScan with { Issues = ["incomplete scan"] }),
                "scan issues remain part of confirmation");
            var pendingProof = new Vc14Continuation(Guid.NewGuid().ToString("D"), DateTimeOffset.UnixEpoch);
            Check(Vc14Reinstall.StageFingerprint(diagnosticPlan, Vc14RepairMode.Full, pendingProof, firstScan)
                != Vc14Reinstall.StageFingerprint(diagnosticPlan, Vc14RepairMode.Full,
                    pendingProof with { UninstallVerified = true }, firstScan),
                "continuation proof changes still invalidate confirmation");
            Check(Vc14Reinstall.StageFingerprint(diagnosticPlan, Vc14RepairMode.UninstallOnly, null, firstScan,
                    new(true, true, "before", "fixture"))
                != Vc14Reinstall.StageFingerprint(diagnosticPlan, Vc14RepairMode.UninstallOnly, null, firstScan,
                    new(true, true, "after", "fixture")),
                "temporary Installer confirmation remains bound");
            Check(Vc14Reinstall.StageFingerprint(diagnosticPlan, Vc14RepairMode.Full, null, firstScan)
                != Vc14Reinstall.StageFingerprint([diagnosticPlan[0] with { Identity = "changed" }, .. diagnosticPlan.Skip(1)],
                    Vc14RepairMode.Full, null, firstScan), "removal identity remains bound");
            var full = Fresh();
            var ordinaryFull = Fresh();
            var prepared = await DeltaNFD.Services.AlphaRestore.RuntimeBetaRepairEnvironment.PrepareV14Async(ordinaryFull,
                Vc14Reinstall.Fingerprint(ordinaryFull.Plan(ordinaryFull.Scan())), null, default);
            Check(prepared.Success && !ordinaryFull.IsSafeMode, "full repair permits actual controlled removal in normal mode: " + prepared.Message);
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
            Fake Missing()
            { var env = Fresh(); env.Products.Clear(); env.ExcludeOrphanWrappers = true; return env; }
            var manualRemoval = Missing();
            Check(Vc14Reinstall.Preview(manualRemoval).CanRepair, "manually removed CRT with no removal plan still permits baseline installation");
            var missingResult = await Run(manualRemoval, Vc14RepairMode.Full);
            Check(missingResult.Success && manualRemoval.Calls.SequenceEqual(new[] { "cleanup", "install:x64", "install:x86", "verify" }),
                "missing CRT installs both architectures and verifies without running an uninstaller");
            Check(missingResult.Message.Contains("本次未执行运行库卸载") && manualRemoval.SavedBeforeInstall
                && manualRemoval.PendingInstall() is null, "missing path records intent before install and accurately reports no removal");
            var manualInstall = Missing();
            Check(Vc14Reinstall.Preview(manualInstall, Vc14RepairMode.InstallOnly).CanRepair,
                "install button permits confirmed absence without a tool-created uninstall checkpoint");
            Check((await Run(manualInstall, Vc14RepairMode.InstallOnly)).Success && manualInstall.SavedBeforeInstall
                && !manualInstall.Calls.Any(c => c.StartsWith("remove")), "fresh install-only persists its own checkpoint before installation");
            var missingUninstall = Missing();
            Check(!Vc14Reinstall.Preview(missingUninstall, Vc14RepairMode.UninstallOnly).CanRepair
                && Vc14Reinstall.Preview(missingUninstall, Vc14RepairMode.UninstallOnly).Blocker.Contains("补装")
                && !(await Run(missingUninstall, Vc14RepairMode.UninstallOnly)).Success && missingUninstall.PendingInstall() is null,
                "empty uninstall-only points to install without creating a fake uninstall record");
            var missingFailure = Missing(); missingFailure.InstallFail = "x86";
            Check(!(await Run(missingFailure, Vc14RepairMode.Full)).Success
                && missingFailure.PendingInstall()?.UninstallVerified == true && missingFailure.Products.Count == 2,
                "failed second architecture retains verified absence proof and the pending record");
            missingFailure.InstallFail = "";
            Check((await Run(missingFailure, Vc14RepairMode.InstallOnly)).Success && missingFailure.Products.Count == 4
                && !missingFailure.Calls.Any(c => c.StartsWith("remove")), "missing installation retries partial baseline without uninstalling it");
            var unknownMissing = Missing(); unknownMissing.ScanIssues.Add("MSI state unavailable");
            foreach (var mode in new[] { Vc14RepairMode.Full, Vc14RepairMode.InstallOnly })
                Check(!Vc14Reinstall.Preview(unknownMissing, mode).CanRepair
                    && !(await Run(unknownMissing, mode)).Success && unknownMissing.Calls.Count == 0 && unknownMissing.PendingInstall() is null,
                    "incomplete scan cannot authorize a missing-runtime installation");
            var missingStale = Missing(); var missingConsent = Vc14Reinstall.Preview(missingStale);
            missingStale.AddBaseline("x64");
            Check(!(await Vc14Reinstall.RunAsync(missingStale, missingConsent.Fingerprint, null, default)).Success
                && missingStale.Calls.Count == 0, "a newly installed component invalidates missing-runtime confirmation");
            var missingSave = Missing(); missingSave.SaveError = true;
            Check(!(await Run(missingSave, Vc14RepairMode.InstallOnly)).Success && missingSave.Calls.Count == 0,
                "cannot start a fresh installation without persisting its checkpoint");
            var dirtyCleanup = Missing(); dirtyCleanup.CleanupAddsProduct = true;
            Check(!(await Run(dirtyCleanup, Vc14RepairMode.Full)).Success && !dirtyCleanup.Calls.Any(c => c.StartsWith("install"))
                && dirtyCleanup.PendingInstall()?.UninstallVerified == false, "post-cleanup MSI reappearance prevents proof and installation");
            var safeMissing = Missing(); safeMissing.IsSafeMode = true;
            Check(!Vc14Reinstall.Preview(safeMissing).CanRepair && !Vc14Reinstall.Preview(safeMissing, Vc14RepairMode.InstallOnly).CanRepair,
                "confirmed absence does not enable installation in safe mode");
            var plan = full.Plan(full.Scan());
            Check(plan.Count == 2 && plan.All(p => p.Kind == "bundle" && p.BundleChildren?.Count == 2), "prefer two original wrappers over four MSI children");
            Check(plan.SelectMany(p => p.BundleChildren!).Select(p => p.Product.Code).Distinct().Count() == 4, "cover every child once");
            Check((await Run(full, Vc14RepairMode.Full)).Success, "full wrapper-first reinstall");
            Check(full.Calls.SequenceEqual(new[] { "remove:x64", "remove:x86", "cleanup", "install:x64", "install:x86", "verify" }), "wrapper cleanup install verify order");
            var wrongDll = Fresh(); wrongDll.WrongDll = true;
            Check((await Run(wrongDll, Vc14RepairMode.Full)).Success && wrongDll.Calls.Count(c => c == "force:x64") == 1
                && wrongDll.PendingInstall() is null, "single repair uses forced-file fallback and completes checkpoint only after verification");
            var stillWrong = Fresh(); stillWrong.WrongDll = true; stillWrong.ForceKeepsMismatch = true;
            Check(!(await Run(stillWrong, Vc14RepairMode.Full)).Success && stillWrong.Calls.Count(c => c == "force:x64") == 1
                && stillWrong.PendingInstall() is not null, "unchanged DLL after force retains pending-install evidence");

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
        public bool PlanError, LeaveProducts, SaveError, SavedBeforeRemoval, SavedBeforeInstall, ServiceBlocked, ExcludeOrphanWrappers, CleanupAddsProduct;
        public List<string> ScanIssues = [];
        public bool WrongDll, ForceKeepsMismatch;
        public int RemoveExit;
        public string InstallFail = "";
        public List<string> Calls = [];
        public List<Vc14Entry> Products = Vc14Catalog.Families.Select((f, i) => new Vc14Entry(
            "{" + (i + 1).ToString("x8") + "-0000-0000-0000-000000000001}",
            "Microsoft Visual C++ 2022 " + f.Arch.ToUpperInvariant() + " " + f.Role + " Runtime - 14.40.33816", "14.40.33816", f.Arch, f.Role, f.Upgrade)).ToList();
        private Vc14ContinuationStore Store => new(root);
        public string DiagnosticsDirectory => root;
        private long _scanGeneration;
        public Vc14Scan Scan() => new(Products.ToArray(), [], ScanIssues.ToArray())
        {
            MsiStateDiagnostics = Products.Select(p => new Vc14MsiStateDiagnostic(p.Code, 5, "INSTALLSTATE_DEFAULT",
                "fixture installed", DateTimeOffset.UnixEpoch.AddTicks(++_scanGeneration), [], "diagnostic only", "diagnostic only")).ToArray(),
        };
        public string? Blocker() => ServiceBlocked ? Vc14InstallerReadiness.Evaluate(IsSafeMode, true, false, false) : null;
        public void VerifyBaselines() { }
        public void VerifyInstalled() { Calls.Add("verify"); VerifyInstalled("x64"); VerifyInstalled("x86"); }
        public void VerifyInstalled(string arch)
        { if (Products.Count != 4) throw new IOException("incomplete baseline"); if (WrongDll && arch == "x64") throw new Vc14DllVersionMismatch(arch, "msvcp140.dll", "14.51.36247"); }
        public Task<Vc14StepResult> ForceFileRepairAsync(string arch, IProgress<string>? progress, CancellationToken token)
        { Calls.Add("force:" + arch); if (!ForceKeepsMismatch) WrongDll = false; return Task.FromResult(new Vc14StepResult(0)); }
        public void Trace(string stage, object value) { }
        public Vc14Continuation? PendingInstall() => Store.Read();
        public void SavePendingInstall(Vc14Continuation record) { if (SaveError) throw new IOException("fixture disk full"); Store.Save(record); }
        public void CompletePendingInstall() => Store.Complete();
        public OperationResult Cleanup(Vc14Scan scan, IProgress<string>? progress, CancellationToken token)
        { Calls.Add("cleanup"); if (CleanupAddsProduct) AddBaseline("x64"); return OperationResult.Ok("无可清理项"); }
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
        { SavedBeforeInstall = PendingInstall() is not null; Calls.Add("install:" + arch); if (InstallFail == arch) return Task.FromResult(new Vc14StepResult(1603)); AddBaseline(arch); return Task.FromResult(new Vc14StepResult(0)); }
    }
}
