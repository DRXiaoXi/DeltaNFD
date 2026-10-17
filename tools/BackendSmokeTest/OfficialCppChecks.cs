using DeltaNFD.Services;
using DeltaNFD.Services.AlphaRestore;

internal static class OfficialCppChecks
{
    internal static async Task RunAsync(string? assets)
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        var catalog = OfficialCppCatalog.Packages;
        Check(catalog.Count == 10 && catalog.Select(p => p.Id).Distinct().Count() == 10, "ten original packages");
        Check(catalog.All(p => new Uri(p.Url).Scheme == "https" && new[] { "download.microsoft.com", "download.visualstudio.microsoft.com" }.Contains(new Uri(p.Url).Host)), "Microsoft sources only");
        Check(catalog.SelectMany(p => p.Files).All(f => f.Sha256.Length == 64 && f.Bytes > 0), "all original bytes pinned");
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-official-cpp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var isolated = Guid.NewGuid(); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), isolated.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, isolated);
        try
        {
            async Task<RuntimeStepResult> Run(Fake fake, bool force = false, CancellationToken token = default) =>
                await new OfficialCppInstaller(fake, new RuntimeRepairDiagnostics(root)).RunAsync(catalog[0], force, TimeSpan.FromSeconds(3), null, token);
            var missing = new Fake { Missing = true };
            Check((await Run(missing)).Success && missing.Actions.SequenceEqual(new[] { false }), "missing product installs original");
            var healthy = new Fake();
            Check((await Run(healthy)).Skipped && healthy.Actions.Count == 0, "healthy exact product skipped");
            var strong = new Fake();
            Check((await Run(strong, true)).Success && strong.Actions.SequenceEqual(new[] { true }), "strong repairs even healthy exact product");
            var broken = new Fake { Broken = true };
            Check((await Run(broken)).Success && broken.Actions.SequenceEqual(new[] { true }), "broken exact product repaired");
            var lower = new Fake { LowerVersion = true };
            Check((await Run(lower)).Success && lower.Actions.Count == 1, "older same product never falsely healthy skipped");
            var noOp = new Fake { Missing = true, NoOpCount = 1 };
            Check((await Run(noOp)).Success && noOp.Actions.SequenceEqual(new[] { false, true }), "install no-op gets one repair for missing child");
            var permanentNoOp = new Fake { Missing = true, NoOpCount = 9 };
            Check(!(await Run(permanentNoOp)).Success && permanentNoOp.Actions.Count == 2, "repeated no-op stops after one repair");
            var brokenNoOp = new Fake { Broken = true, NoOpCount = 9 };
            Check(!(await Run(brokenNoOp)).Success && brokenNoOp.Actions.Count == 1, "failed repair postcheck cannot report success");
            var newer = new Fake { Newer = true };
            Check((await Run(newer, true)).RetainedNewer && newer.Actions.Count == 0, "newer source product retained in strong mode");
            var otherNewer = new Fake { Missing = true, Newer = true };
            Check((await Run(otherNewer)).RetainedNewer && otherNewer.Actions.Count == 0, "newer related product retained without downgrade");
            foreach (var arch in new[] { "x64", "x86" })
            {
                var original2008 = catalog.Single(p => p.Year == "2008" && p.Arch == arch);
                async Task<RuntimeStepResult> Replay(Serviced2008 fixture) => await new OfficialCppInstaller(fixture,
                    new RuntimeRepairDiagnostics(root)).RunAsync(original2008, false, TimeSpan.FromSeconds(3), null, default);
                var serviced = new Serviced2008(arch);
                Check((await Replay(serviced)).RetainedNewer && serviced.Executions == 0,
                    "VC2008 6161 source retains 7523 same-product versioned name after cache/files verification " + arch);
                foreach (var fault in new[] { "name", "publisher", "architecture", "family", "version", "files", "product" })
                {
                    var invalid = new Serviced2008(arch) { Fault = fault };
                    Check(!(await Replay(invalid)).Success && invalid.Executions == 0,
                        "serviced VC2008 cannot bypass identity/files validation " + arch + " " + fault);
                }
            }
            var badNewer = new Fake { Newer = true, Broken = true };
            Check(!(await Run(badNewer, true)).Success && badNewer.Actions.Count == 0, "broken newer product requires matching source not downgrade");
            var mixed = new Fake { Missing = true, MixedNewer = true, MultipleSources = true };
            Check(!(await Run(mixed)).Success && mixed.Actions.Count == 0, "newer minimum with missing additional rejected");
            foreach (var exit in new RuntimeStepResult[] { new(1603), new(1638), new(3010), new(1641), new(null, OutcomeUnknown: true) })
            {
                var failure = new Fake { Missing = true, Result = exit };
                var result = await Run(failure);
                Check(failure.Actions.Count == 1 && result.ExitCode == exit.ExitCode && result.RequiresReboot == exit.RequiresReboot
                    && result.OutcomeUnknown == exit.OutcomeUnknown, "failure/reboot/unknown preserved without replay " + exit.ExitCode);
            }
            var wrongIdentity = new Fake { WrongIdentity = true };
            Check(!(await Run(wrongIdentity)).Success && wrongIdentity.Actions.Count == 0, "wrong installed identity rejected");
            var transient = new Fake { Transient = true };
            Check(!(await Run(transient)).Success && transient.Actions.Count == 0, "unknown product state rejected");
            var badHash = new Fake { VerificationFails = true };
            Check(!(await Run(badHash)).Success && badHash.Actions.Count == 0, "payload corruption before mutation");
            var changedBytes = new Fake { Missing = true, VerificationFailsAfterFirst = true };
            Check(!(await Run(changedBytes)).Success && changedBytes.Actions.Count == 0, "payload changed after planning rejected");
            var changedProduct = new Fake { Missing = true, ChangeBeforeExecute = true };
            Check(!(await Run(changedProduct)).Success && changedProduct.Actions.Count == 0, "product race before execution rejected");
            var multi = new Fake { Missing = true, ManyNewer = true };
            Check(!(await Run(multi)).Success && multi.Actions.Count == 0, "ambiguous newer related product rejected");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var cancelFake = new Fake { Missing = true };
            try { await Run(cancelFake, token: cancelled.Token); throw new Exception("cancel lost"); }
            catch (OperationCanceledException) { Check(cancelFake.Actions.Count == 0, "cancel before install zero calls"); }
            var cancelAfter = new Fake { Missing = true, CancelOnExecute = new CancellationTokenSource() };
            try { await Run(cancelAfter, token: cancelAfter.CancelOnExecute.Token); throw new Exception("cancel after install lost"); }
            catch (OperationCanceledException) { Check(cancelAfter.Actions.Count == 1, "cancel after install no retry"); }
            finally { cancelAfter.CancelOnExecute.Dispose(); }

            foreach (var package in catalog)
            {
                foreach (var file in package.Files)
                {
                    var path = Path.Combine(root, "commands", file.Path.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "fixture-not-executed");
                }
                var commandRoot = Path.Combine(root, "commands");
                foreach (var repair in new[] { false, true })
                {
                    var info = OfficialCppInstaller.BuildCommand(commandRoot, package, repair, Path.Combine(root, "fixture.log"));
                    Check(!info.UseShellExecute && info.ArgumentList.Contains("/norestart") && !info.ArgumentList.Contains("/uninstall")
                        && !info.ArgumentList.Contains("/x") && !info.ArgumentList.Contains("/f"), "no shell/uninstall/reboot " + package.Id + repair);
                    if (package.Year == "2005" || repair && package.Year == "2008")
                        Check(Path.GetFileName(info.FileName).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase)
                            && info.ArgumentList.Contains("/i") && info.ArgumentList.Contains("REINSTALL=ALL") == repair,
                            "legacy original MSI repair flags " + package.Id + repair);
                    else Check(info.FileName.EndsWith("vcredist.exe") && (package.Year is "2012" or "2013"
                        ? info.ArgumentList.Contains(repair ? "/repair" : "/install") : info.ArgumentList.Contains(repair ? "/repair" : "/q") || !repair && info.ArgumentList.Contains("/quiet")),
                        "year-specific original EXE flags " + package.Id + repair);
                }
            }
            foreach (var relative in new[] { "../outside.exe", "2005/../outside.exe", "C:/outside.exe", "2005//outside.exe" })
            {
                try { OfficialCppCatalog.Resolve(root, relative); throw new Exception("path escaped"); }
                catch (IOException) { count++; }
            }
            if (assets is not null)
            {
                var actualRoot = Path.Combine(Path.GetFullPath(assets), "RuntimeCppOfficial");
                foreach (var package in catalog)
                {
                    var children = OfficialCppCatalog.Verify(actualRoot, package);
                    Check(children.All(c => c.Architecture == package.Arch && c.Files.Count > 0), "actual original MSI identity " + package.Id);
                    Check(children.All(c => c.Files.All(f => f.Version.Major is >= 8 and <= 12)), "actual original DLL branches " + package.Id);
                }
                var copy = Path.Combine(root, "changed-payload");
                foreach (var file in catalog[0].Files)
                {
                    var target = Path.Combine(copy, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(Path.Combine(actualRoot, file.Path), target);
                }
                File.AppendAllText(Path.Combine(copy, catalog[0].Exe), "bad");
                try { OfficialCppCatalog.Verify(copy, catalog[0]); throw new Exception("tampered original accepted"); }
                catch (IOException) { count++; }
            }
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"Official C++ originals: {count} checks passed. Installer operations mocked; actual payload only hashed/read as MSI databases.");
    }

    private sealed class Serviced2008(string arch) : IOfficialCppEnvironment
    {
        internal string Fault = "";
        internal int Executions;
        private string Code => arch == "x64" ? "{5FCE6D76-F5DC-37AB-B2B8-22AB8CEDB1D4}" : "{9BE518E6-ECC6-35A9-88E4-87755C07200F}";
        private string Family => arch == "x64" ? "{FDA45DDF-8E17-336F-A3ED-356B7B7C688A}" : "{DE2C306F-A067-38EF-B86C-03DE4B0312F9}";
        private RuntimeMsiPackage Package(string version) => new(Code,
            "Microsoft Visual C++ 2008 Redistributable - " + arch + " " + version, "Microsoft Corporation", version, arch,
            [new("fixture-component", "msvcr90.dll", "msvcr90.dll", Version.Parse(version))]) { UpgradeCode = Family };
        public IReadOnlyList<RuntimeMsiPackage> Verify(OfficialCppPackage package) => [Package("9.0.30729.6161")];
        public RuntimeMsiProduct ReadProduct(string product) => new(Code, 5, Package("9.0.30729.7523").Name, "Microsoft Corporation", "9.0.30729.7523", arch);
        public IReadOnlyList<RuntimeMsiPackage> NewerRelated(RuntimeMsiPackage source)
        {
            var cached = Package("9.0.30729.7523");
            return [Fault switch
            {
                "name" => cached with { Name = "other product" },
                "publisher" => cached with { Publisher = "other publisher" },
                "architecture" => cached with { Architecture = "arm64" },
                "family" => cached with { UpgradeCode = Guid.NewGuid().ToString("B") },
                "version" => cached with { Version = "9.0.30729.8000" },
                "product" => cached with { Product = Guid.NewGuid().ToString("B") },
                _ => cached,
            }];
        }
        public OperationResult VerifyFiles(RuntimeMsiPackage package) => Fault == "files" ? OperationResult.Fail("fixture broken DLL") : OperationResult.Ok("verified");
        public Task<RuntimeStepResult> ExecuteAsync(OfficialCppPackage package, bool repair, string logPath, TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
        { Executions++; throw new Exception("serviced package must not execute older source"); }
    }

    private sealed class Fake : IOfficialCppEnvironment
    {
        internal bool Missing, Broken, Newer, MixedNewer, MultipleSources, ManyNewer, WrongIdentity, Transient,
            VerificationFails, VerificationFailsAfterFirst, ChangeBeforeExecute, LowerVersion;
        internal int NoOpCount, Verifications;
        internal List<bool> Actions = [];
        internal CancellationTokenSource? CancelOnExecute;
        internal RuntimeStepResult Result = new(0);
        private static RuntimeMsiPackage Source(string product) => new(product, "Microsoft Visual C++ 2005", "Microsoft Corporation", "8.0.61000", "x64",
            [new("component", "fixture.dll", "fixture.dll", new Version(8, 0, 61000))]) { UpgradeCode = "{11111111-1111-1111-1111-111111111111}" };
        public IReadOnlyList<RuntimeMsiPackage> Verify(OfficialCppPackage package)
        {
            Verifications++;
            if (VerificationFails || VerificationFailsAfterFirst && Verifications > 1) throw new IOException("fixture invalid original");
            return MultipleSources ? [Source("minimum"), Source("additional")] : [Source("product")];
        }
        public RuntimeMsiProduct ReadProduct(string product)
        {
            var present = !Missing || ChangeBeforeExecute && Verifications > 1;
            return new(product, Transient ? 2 : present ? 5 : -1, WrongIdentity ? "other" : Source(product).Name, "Microsoft Corporation",
                Newer ? "8.0.62000" : LowerVersion ? "8.0.60000" : "8.0.61000", "x64");
        }
        public IReadOnlyList<RuntimeMsiPackage> NewerRelated(RuntimeMsiPackage source)
        {
            var candidate = source with { Version = "8.0.62000" };
            if (ManyNewer) return [candidate, candidate with { Product = "other" }];
            return Newer || MixedNewer && source.Product == "minimum" ? [candidate] : [];
        }
        public OperationResult VerifyFiles(RuntimeMsiPackage package) => Broken ? OperationResult.Fail("fixture DLL mismatch") : OperationResult.Ok("verified");
        public Task<RuntimeStepResult> ExecuteAsync(OfficialCppPackage package, bool repair, string logPath, TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
        {
            Actions.Add(repair);
            if (NoOpCount-- <= 0) { Missing = false; Broken = false; LowerVersion = false; }
            CancelOnExecute?.Cancel();
            return Task.FromResult(Result);
        }
    }
}
