using DeltaNFD.Backends;
using DeltaNFD.Services;

internal static class Vc14Checks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool value, string detail) { if (!value) throw new Exception(detail); count++; }
        foreach (var row in new[] {
            ("Microsoft Visual C++ 2022", "14.51.36247", Vc14Severity.Abnormal),
            ("Microsoft Visual C++ 2026", "14.51.0.0", Vc14Severity.Abnormal),
            ("Microsoft Visual C++ v14", "14.42.34433", Vc14Severity.Abnormal),
            ("Microsoft Visual C++ v14", "unknown", Vc14Severity.Abnormal),
            ("Microsoft Visual C++ 2022", "14.44.0", Vc14Severity.Warning),
            ("Microsoft Visual C++ 2022", "14.0.0", Vc14Severity.Warning),
            ("Microsoft Visual C++ 2022", "14.42.34433.0", Vc14Severity.Normal),
            ("Microsoft Visual C++ 2013", "12.0.21005", Vc14Severity.Normal),
            ("Microsoft Visual C++ 2022", "unknown", Vc14Severity.Warning) })
            Check(Vc14Catalog.Classify(row.Item1, row.Item2) == row.Item3, "severity: " + row);
        foreach (var family in Vc14Catalog.Families)
        {
            var p = Entry(family, "14.51.36247");
            Vc14Catalog.Validate(p, p.Code, p.Name, p.Version, "Microsoft Corporation", p.Architecture, p.UpgradeCode);
            count++;
            foreach (var bad in new[] { p with { Name = p.Name.Replace("Runtime", "Debug Runtime") },
                p with { Code = "bad" }, p with { Version = "12.0.21005" },
                p with { Architecture = "arm64" }, p with { Role = "SDK" }, p with { UpgradeCode = Guid.NewGuid().ToString("B") } })
            {
                var rejected = false;
                try { Vc14Catalog.Validate(bad, p.Code, p.Name, p.Version, "Microsoft Corporation", p.Architecture, p.UpgradeCode); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected, "unsafe product identity accepted: " + bad);
            }
            foreach (var publisher in new[] { "Other", "", "Microsoft" })
            {
                var rejected = false;
                try { Vc14Catalog.Validate(p, p.Code, p.Name, p.Version, publisher, p.Architecture, p.UpgradeCode); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected, "non-Microsoft publisher accepted");
            }
        }
        foreach (var command in new[] { BackendCommands.Vc14Scan, BackendCommands.Vc14Preview })
            Check(BackendModules.IsAllowed(BackendModules.Runtime, command) && BackendModules.IsReadOnly(BackendModules.Runtime, command), "scan/preview must be read-only");
        Check(BackendModules.RequiresWriteIntent(BackendModules.Runtime, BackendCommands.Vc14Repair), "repair intent missing");
        Check(Vc14InstallPolicy.SelectAction(true) == "/repair", "registered Burn baseline must repair missing children");
        Check(Vc14InstallPolicy.SelectAction(false) == "/install", "new baseline must install");
        var env = new Fake();
        var good = await Run(env);
        Check(good.Success, "normal simulated repair failed: " + good.Message);
        Check(env.Calls.Count == 6 && env.Calls.Take(4).All(s => s.StartsWith("remove:"))
            && env.Calls.Skip(4).SequenceEqual(new[] { "install:x64", "install:x86" }), "two-phase repair order changed");
        Check(env.Verified, "actual DLL verification skipped");
        env = new();
        var invalid = await Vc14Repair.RunAsync(env, "stale", null, CancellationToken.None);
        Check(!invalid.Success && env.Calls.Count == 0, "stale fingerprint mutated state");
        env = new() { Block = "shared dependency" };
        var blocked = await Run(env);
        Check(!blocked.Success && env.Calls.Count == 0, "blocked preflight started uninstall");
        env = new() { CorruptBaseline = true };
        Check(!(await Run(env)).Success && env.Calls.Count == 0, "corrupt baseline started uninstall");
        env = new() { Issue = "cache missing" };
        Check(!(await Run(env)).Success && env.Calls.Count == 0, "unknown identity started uninstall");
        foreach (var step in new[] { new Vc14Step(1603), new Vc14Step(1618), new Vc14Step(null, true), new Vc14Step(3010), new Vc14Step(1641) })
        {
            env = new() { RemoveResult = step };
            var result = await Run(env);
            Check(!result.Success && env.Calls.Count == 1 && !env.Calls.Any(s => s.StartsWith("install")), "failed/unknown/reboot removal continued");
            Check(result.OutcomeUnknown == step.Unknown && result.RequiresReboot == step.Reboot, "transaction result lost");
        }
        env = new() { ChangeDuringRemoval = true };
        Check(!(await Run(env)).Success && env.Calls.Count == 1, "concurrent new product ignored");
        env = new() { KeepRemovedProduct = true };
        Check(!(await Run(env)).Success && env.Calls.Count == 1, "MSI success without removal accepted");
        foreach (var step in new[] { new Vc14Step(1603), new Vc14Step(null, true), new Vc14Step(3010) })
        {
            env = new() { InstallResult = step };
            Check(!(await Run(env)).Success && env.Calls.Count == 5, "failed installation continued into x86");
        }
        env = new() { BadFinalVersion = true };
        Check(!(await Run(env)).Success && !env.Verified, "wrong final MSI version accepted");
        Check(env.Calls.Count == 5, "unknown products in installation phase allowed another install");
        env = new() { BadDll = true };
        Check(!(await Run(env)).Success && env.Verified, "wrong actual DLL version accepted");
        env = new() { RegisteredBaseline = true, Products = [] };
        Check((await Run(env)).Success && env.Actions.SequenceEqual(new[] { "/repair", "/repair" }), "registered wrappers with absent children were not repaired");
        env = new() { Products = [] };
        Check((await Run(env)).Success && env.Actions.SequenceEqual(new[] { "/install", "/install" }), "fresh install strategy changed");
        env = new() { SuccessfulNoOp = true };
        var noOp = await Run(env);
        Check(!noOp.Success && !noOp.OutcomeUnknown && noOp.Message.Contains("未装齐") && env.Calls.Count == 5, "exit-zero no-op accepted or continued into x86");
        env = new() { MissingAdditional = true };
        Check(!(await Run(env)).Success && env.Calls.Count == 5, "partial x64 install continued into x86");
        env = new(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Check(!(await Vc14Repair.RunAsync(env, Vc14Catalog.Fingerprint(env.Scan()), null, cancel.Token)).Success && env.Calls.Count == 0, "canceled operation started");
        env = new() { Legacy = new("old-inf", [new("x64", "Old wrapper", "14.51", false, "needs repair")], []) };
        Check((await Run(env)).Success && env.Calls.Last() == "legacy", "complete repair skipped legacy processing");
        env = new() { Legacy = new("old-inf", [new("x64", "Old wrapper", "14.51", false, "needs repair")], []), LegacyFailure = true };
        var legacyFailure = await Run(env);
        Check(!legacyFailure.Success && legacyFailure.IsPartial && env.Verified, "legacy failure hid the verified core result");
        Console.WriteLine($"VC14 policy and simulated transaction: {count} checks passed; no real install/uninstall.");
    }
    internal static void ReadOnly()
    {
        var scan = Vc14Catalog.Scan();
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(scan));
        Console.WriteLine("Dependency preflight: " + (Vc14Catalog.DependencyIssue(scan.Products) ?? "No unknown shared dependency"));
        if (scan.Issues.Count > 0) throw new Exception(string.Join("\n", scan.Issues));
        foreach (var arch in new[] { "x64", "x86" }) Console.WriteLine(arch + " baseline action: " + Vc14InstallPolicy.ReadAction(arch));
        foreach (var product in scan.Products)
        {
            var clients = Vc14Catalog.ApprovedClients(product);
            if (clients.Any(c => !Guid.TryParse(c, out _) || c.Equals("ALL", StringComparison.OrdinalIgnoreCase)))
                throw new Exception("Unscoped dependency ignore key");
            Console.WriteLine(product.Architecture + " " + product.Role + " exact verified wrapper keys: " + string.Join(";", clients));
        }
    }
    private static Task<OperationResult> Run(Fake env) => Vc14Repair.RunAsync(env, Vc14Catalog.Fingerprint(env.Scan()), null, CancellationToken.None);
    private static Vc14Entry Entry((string Arch, string Role, string Upgrade) family, string version) => new(
        Guid.NewGuid().ToString("B"), "Microsoft Visual C++ 2022 " + family.Arch.ToUpperInvariant() + " " + family.Role + " Runtime - " + version,
        version, family.Arch, family.Role, family.Upgrade);
    private sealed class Fake : IVc14Environment
    {
        public List<Vc14Entry> Products = Vc14Catalog.Families.Select(f => Entry(f, "14.51.36247")).ToList();
        public List<string> Calls = [];
        public List<string> Actions = [];
        public string? Block; public string? Issue;
        public bool CorruptBaseline, ChangeDuringRemoval, KeepRemovedProduct, BadFinalVersion, BadDll, Verified;
        public bool RegisteredBaseline, SuccessfulNoOp, MissingAdditional;
        public bool LegacyFailure;
        public Vc14LegacyReport Legacy = new("", [], []);
        public Vc14Step RemoveResult = new(0), InstallResult = new(0);
        public Vc14Scan Scan() => new(Products.ToArray(), [], Issue is null ? [] : [Issue]) { Legacy = Legacy };
        public Task<OperationResult> CleanLegacyAsync(string fingerprint, IProgress<string>? progress, CancellationToken token)
        {
            Calls.Add("legacy");
            if (LegacyFailure) return Task.FromResult(OperationResult.Fail("legacy write failed"));
            Legacy = new("after", [], []); return Task.FromResult(OperationResult.Ok("metadata processed"));
        }
        public string? Blocker() => Block;
        public void VerifyBaselines() { if (CorruptBaseline) throw new IOException("bad baseline"); }
        public void VerifyInstalled() { Verified = true; if (BadDll) throw new IOException("unexpected DLL version"); }
        public Task<Vc14Step> RemoveAsync(Vc14Entry entry, IProgress<string>? progress, CancellationToken token)
        {
            Calls.Add("remove:" + entry.Role);
            if (RemoveResult.Success && !KeepRemovedProduct) Products.Remove(entry);
            if (ChangeDuringRemoval) Products.Add(Entry(Vc14Catalog.Families[0], "14.44.0"));
            return Task.FromResult(RemoveResult);
        }
        public Task<Vc14Step> InstallAsync(string arch, IProgress<string>? progress, CancellationToken token)
        {
            Calls.Add("install:" + arch);
            Actions.Add(Vc14InstallPolicy.SelectAction(RegisteredBaseline));
            if (InstallResult.Success && !SuccessfulNoOp) Products.AddRange(Vc14Catalog.Families.Where(f => f.Arch == arch && (!MissingAdditional || f.Role != "Additional"))
                .Select(f => Entry(f, BadFinalVersion ? "14.51.36247" : "14.42.34433.0")));
            return Task.FromResult(InstallResult);
        }
    }
}
