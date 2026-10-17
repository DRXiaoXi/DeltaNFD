using DeltaNFD.Services;
using System.Diagnostics;

internal static class Vc14FileRepairChecks
{
    internal static async Task RunAsync(string sourceRoot)
    {
        int count = 0;
        void Check(bool value, string text) { if (!value) throw new IOException(text); count++; }
        void Reject(Action action, string text)
        { try { action(); } catch (IOException) { count++; return; } throw new IOException("Expected refusal: " + text); }
        async Task<Vc14StepResult> Run(Fake fake, CancellationToken token = default) => await Vc14FileRecovery.RunAsync(fake, null, token);
        var healthy = new Fake { Bad = [] };
        Check((await Run(healthy)).Success && healthy.Calls.Count == 0, "healthy files do not run another installer");
        var ordinary = new Fake { OrdinaryFixes = true };
        Check((await Run(ordinary)).Success && ordinary.Calls.SequenceEqual(["ordinary:x64"]), "ordinary repair success never enters force mode");
        var fixedFiles = new Fake();
        Check((await Run(fixedFiles)).Success && fixedFiles.Calls.SequenceEqual(["ordinary:x64", "force:x64"]), "wrong version after ordinary repair falls back exactly once");
        var both = new Fake { Bad = ["x64", "x86"] };
        Check((await Run(both)).Success && both.Calls.SequenceEqual(["ordinary:x64", "force:x64", "ordinary:x86", "force:x86"]), "only affected architectures use the fallback");
        foreach (var result in new[] { new Vc14StepResult(1603), new Vc14StepResult(3010), new Vc14StepResult(1641), new Vc14StepResult(0, true) })
        {
            var failed = new Fake { Ordinary = result, Bad = ["x64", "x86"] };
            var outcome = await Run(failed);
            Check((!outcome.Success || outcome.Reboot) && failed.Calls.SequenceEqual(["ordinary:x64"]), "ordinary failure, reboot or unknown never enables force");
            var forcedFailure = new Fake { Forced = result, Bad = ["x64", "x86"] };
            outcome = await Run(forcedFailure);
            Check((!outcome.Success || outcome.Reboot) && forcedFailure.Calls.SequenceEqual(["ordinary:x64", "force:x64"]), "force failure/reboot/unknown stops later architectures");
        }
        var stillWrong = new Fake { ForceFixes = false };
        Check(!(await Run(stillWrong)).Success && stillWrong.Calls.Count == 2, "exit zero with unchanged DLL is failure, not another retry");
        var unknownScan = new Fake { ScanIssue = true };
        Check(!(await Run(unknownScan)).Success && unknownScan.Calls.SequenceEqual(["ordinary:x64"]), "unknown MSI scan forbids force");
        var changedMsi = new Fake { ChangedMsi = true };
        Check(!(await Run(changedMsi)).Success && !changedMsi.Calls.Any(c => c.StartsWith("force")), "changed installed version forbids force");
        var safe = new Fake { IsSafeMode = true };
        Check(!(await Run(safe)).Success && safe.Calls.Count == 0, "file repair requires normal mode");
        var blocked = new Fake { Blocked = true };
        Check(!(await Run(blocked)).Success && blocked.Calls.Count == 0, "existing blockers remain effective");
        var inaccessible = new Fake { UnreadableAfterRepair = true };
        try { await Run(inaccessible); throw new Exception("must fail"); }
        catch (IOException) { Check(inaccessible.Calls.SequenceEqual(["ordinary:x64"]), "unreadable DLL is not a known version mismatch and cannot authorize force"); }
        using (var cancel = new CancellationTokenSource())
        {
            var cancelled = new Fake { AfterOrdinary = cancel.Cancel };
            try { await Run(cancelled, cancel.Token); throw new Exception("must cancel"); }
            catch (OperationCanceledException) { Check(cancelled.Calls.SequenceEqual(["ordinary:x64"]), "cancel after ordinary repair never starts force"); }
        }
        foreach (var arch in new[] { "x64", "x86" })
        {
            var sources = Vc14FileRepairSource.Verify(Path.GetFullPath(sourceRoot), arch);
            Check(sources.Count == 2, "actual fixed MSI/CAB bytes and read-only metadata verified: " + arch);
            foreach (var source in sources)
            {
                var command = Vc14FileRepairSource.BuildCommand(Path.GetFullPath(sourceRoot), source, Path.Combine(Path.GetTempPath(), "isolated-force-command.log"));
                Check(command.FileName.EndsWith("msiexec.exe") && !command.UseShellExecute && command.CreateNoWindow,
                    "force uses fixed msiexec without a command shell");
                Check(command.ArgumentList.Contains("REINSTALL=ALL") && command.ArgumentList.Contains("REINSTALLMODE=amus")
                    && command.ArgumentList.Contains("/norestart") && !command.ArgumentList.Any(a => a.StartsWith("IGNOREDEPENDENCIES"))
                    && !command.ArgumentList.Contains("/x"), "force is reinstall-only, with explicit file mode and no dependency bypass");
                var product = new Vc14Entry(source.Code, source.Name, source.Version, source.Architecture, source.Role, source.UpgradeCode);
                Vc14FileRepairSource.ValidateInstalled(source, product);
                Check(true, "verified matching product is repairable");
                Reject(() => Vc14FileRepairSource.ValidateInstalled(source, null), "missing product must install normally");
                Reject(() => Vc14FileRepairSource.ValidateInstalled(source, product with { Version = "14.51.36247" }), "do not silently force another MSI version");
                Reject(() => Vc14FileRepairSource.ValidateInstalled(source, product with { Code = Guid.NewGuid().ToString("B") }), "fixed product identity cannot be swapped");
            }
        }
        Reject(() => Vc14FileRepairSource.Verify(Path.GetFullPath(sourceRoot), "arm64"), "no scope expansion to ARM64");
        var fixture = Path.Combine(Path.GetTempPath(), "DeltaNFD-force-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            foreach (var file in Vc14FileRepairSource.Packages.SelectMany(p => p.Files))
            {
                var target = Path.Combine(fixture, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(sourceRoot, file.Path), target);
            }
            var cab = Vc14FileRepairSource.Packages.First(p => p.Architecture == "x64").Files.Single(f => f.Path.EndsWith(".cab"));
            var path = Path.Combine(fixture, cab.Path);
            using (var content = new FileStream(path, FileMode.Open, FileAccess.ReadWrite)) { content.Position = 40; content.WriteByte((byte)(content.ReadByte() ^ 1)); }
            Reject(() => Vc14FileRepairSource.Verify(fixture, "x64"), "changed CAB cannot reach execution");
        }
        finally
        {
            var resolved = Path.GetFullPath(fixture);
            if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                || !Path.GetFileName(resolved).StartsWith("DeltaNFD-force-source-")) throw new IOException("Bad fixture cleanup target");
            Directory.Delete(resolved, true);
        }
        Console.WriteLine($"VC14 forced-file fallback checks: {count} passed. Actual original MSI/CAB read only; all installer and recovery operations simulated.");
    }

    private sealed class Fake : IVc14ReinstallEnvironment
    {
        public HashSet<string> Bad = ["x64"];
        public List<string> Calls = [];
        public bool OrdinaryFixes, ScanIssue, ChangedMsi, Blocked, UnreadableAfterRepair;
        public bool ForceFixes = true;
        public bool IsSafeMode { get; set; }
        public Action? AfterOrdinary;
        public Vc14StepResult Ordinary = new(0), Forced = new(0);
        public Vc14Scan Scan() => new(Vc14Catalog.Families.Select(f => new Vc14Entry(Guid.NewGuid().ToString("B"), "fixture", ChangedMsi ? "14.51.36247" : Vc14Reinstall.BaselineVersion,
            f.Arch, f.Role, f.Upgrade)).ToArray(), [], ScanIssue ? ["unknown"] : []);
        public string? Blocker() => Blocked ? "blocked" : null;
        public void VerifyBaselines() { }
        public void VerifyInstalled() { VerifyInstalled("x64"); VerifyInstalled("x86"); }
        public void VerifyInstalled(string arch)
        {
            if (UnreadableAfterRepair && Calls.Count > 0) throw new IOException("file unreadable");
            if (Bad.Contains(arch)) throw new Vc14DllVersionMismatch(arch, "vcruntime140.dll", "14.51.36247");
        }
        public string DiagnosticsDirectory { get; } = Path.Combine(Path.GetTempPath(), "DeltaNFD-force-log-fixture-" + Guid.NewGuid().ToString("N"));
        public void Trace(string stage, object value) { }
        public Task<Vc14StepResult> RemoveAsync(Vc14RemovalPlanItem item, IProgress<string>? progress, CancellationToken token) => throw new Exception("never uninstall");
        public Task<Vc14StepResult> InstallAsync(string arch, IProgress<string>? progress, CancellationToken token)
        { Calls.Add("ordinary:" + arch); if (OrdinaryFixes) Bad.Remove(arch); AfterOrdinary?.Invoke(); return Task.FromResult(Ordinary); }
        public Task<Vc14StepResult> ForceFileRepairAsync(string arch, IProgress<string>? progress, CancellationToken token)
        { Calls.Add("force:" + arch); if (ForceFixes) Bad.Remove(arch); return Task.FromResult(Forced); }
    }
}
