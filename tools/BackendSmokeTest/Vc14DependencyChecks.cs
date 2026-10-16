using System.Text;
using DeltaNFD.Services;

internal static class Vc14DependencyChecks
{
    internal static void Run()
    {
        var count = 0;
        void Check(bool yes, string text) { if (!yes) throw new Exception(text); count++; }
        void Reject(Action action, string text)
        {
            try { action(); } catch (IOException) { count++; return; }
            throw new Exception("Expected refusal: " + text);
        }
        var product = new Vc14Entry("{931A2CF0-2404-45EA-82F5-345735AE6A90}",
            "Microsoft Visual C++ 2022 X64 Minimum Runtime - 14.51.36247", "14.51.36247", "x64", "Minimum",
            "{36F68A90-239C-34DF-B58C-64B30153CE35}");
        var baseline = Vc14Reinstall.BaselineBundles["x64"];
        var own = "{11111111-1111-1111-1111-111111111111}";
        var provider = new Vc14DependencyProvider("LocalMachine:Registry64", "Microsoft.VS.VC_RuntimeMinimumVSU_amd64,v14",
            product.Code, "provider-values", [baseline]);
        Vc14DependencyOwner Owner(string version = "14.51.36247") => new("LocalMachine:Registry64:ARP",
            "Microsoft Visual C++ v14 Redistributable (x64) - " + version, version, "Microsoft Corporation", true, true, "registry-and-cache-hash");
        Vc14DependencyApproval Evaluate(Vc14DependencyProvider? p = null, Vc14DependencyOwner[]? owners = null, bool pinned = true)
            => Vc14DependencyPolicy.Evaluate(product, [p ?? provider], _ => owners ?? [], _ => pinned);
        var noDependencies = Vc14DependencyPolicy.Evaluate(product, [], _ => throw new Exception("unexpected owner read"), _ => throw new Exception("unexpected package read"));
        Check(noDependencies.Clients.Count == 0 && noDependencies.Argument == "", "no dependencies must not add ignore property");
        var approved = Evaluate();
        Check(approved.Argument == "IGNOREDEPENDENCIES=" + baseline && approved.Clients.Count == 1,
            "logs (9) orphan baseline reference should be narrowly approved");
        Check(approved.Evidence.Contains(Vc14Reinstall.BaselineHashes["x64"]), "approval evidence must bind pinned source hash");
        Check(Evaluate().Fingerprint == approved.Fingerprint, "same dependency evidence must have same fingerprint");
        foreach (var arch in new[] { "x64", "x86" })
            foreach (var role in new[] { "Minimum", "Additional" })
            {
                var p = product with { Architecture = arch, Role = role,
                    Name = $"Microsoft Visual C++ 2022 {arch} {role} Runtime - 14.51.36247" };
                var reference = Vc14Reinstall.BaselineBundles[arch];
                var dependent = provider with { Key = "Microsoft.VS.VC_Runtime" + role + "VSU_" + (arch == "x64" ? "amd64" : "x86") + ",v14", Clients = [reference] };
                var result = Vc14DependencyPolicy.Evaluate(p, [dependent], _ => [], a => a == arch);
                Check(result.Argument == "IGNOREDEPENDENCIES=" + reference, "baseline approval must preserve architecture and role");
            }
        Reject(() => Evaluate(pinned: false), "bad pinned source cannot authorize orphan baseline");
        Reject(() => Evaluate(provider with { Clients = [own] }), "arbitrary orphan must not be approved by pinned baseline");
        Reject(() => Evaluate(provider with { Clients = [Vc14Reinstall.BaselineBundles["x86"]] }), "wrong-architecture baseline must not pass");
        Reject(() => Evaluate(provider with { Clients = ["ALL"] }), "global ignore must not pass");
        Reject(() => Evaluate(provider with { Clients = [baseline + ";ALL"] }), "argument injection must not pass");
        Reject(() => Evaluate(provider with { ProductCode = own }), "provider must name same product");
        Reject(() => Evaluate(provider with { Key = "Microsoft.VS.VC_RuntimeAdditionalVSU_amd64,v14" }), "provider role mismatch");
        Reject(() => Evaluate(provider with { Key = "Microsoft.VS.VC_RuntimeMinimumVSU_x86,v14" }), "provider architecture mismatch");
        Reject(() => Evaluate(provider with { Key = "Unknown.Provider,v14" }), "unknown provider");
        var validOwner = Owner(Vc14Reinstall.BaselineVersion);
        Check(Evaluate(owners: [validOwner]).Clients.Count == 1, "registered baseline must pass with signed cache");
        Check(Evaluate(owners: [validOwner with { CacheExists = false, CacheVerified = false }]).Clients.Count == 1,
            "missing baseline cache can be replaced only by verified pinned source identity");
        Reject(() => Evaluate(owners: [validOwner with { CacheVerified = false }]), "present invalid cache cannot use pinned fallback");
        Reject(() => Evaluate(owners: [validOwner with { Publisher = "Other Publisher" }]), "baseline GUID alone is insufficient");
        Reject(() => Evaluate(owners: [validOwner with { Name = "Some Game" }]), "other software must not be approved");
        Reject(() => Evaluate(owners: [validOwner with { Name = "Microsoft Visual C++ v14 Redistributable (x86)" }]), "owner architecture mismatch");
        Reject(() => Evaluate(owners: [validOwner with { Version = "14.51.36247" }]), "baseline ID with contradictory version");
        Reject(() => Evaluate(owners: [validOwner, validOwner with { Location = "CurrentUser:Registry32", Publisher = "Other" }]), "contradictory registry view");
        var sameRuntime = provider with { Clients = [own] };
        Check(Evaluate(sameRuntime, [Owner()]).Clients.SequenceEqual([own]), "signed same-version CRT wrapper can be approved");
        Reject(() => Evaluate(sameRuntime, [Owner() with { CacheExists = false, CacheVerified = false }]), "unknown missing wrapper cache");
        Reject(() => Evaluate(sameRuntime, [Owner() with { CacheVerified = false }]), "unknown invalid wrapper signature");
        Reject(() => Evaluate(sameRuntime, [Owner("14.44.0")]), "different runtime version cannot claim current MSI");
        Reject(() => Evaluate(provider with { Clients = [baseline, own] }), "one unverified dependent blocks whole command");
        Check(Evaluate(provider with { Values = "changed provider" }).Fingerprint != approved.Fingerprint, "provider values must affect confirmation");
        Check(Evaluate(provider with { ClientValues = "changed client" }).Fingerprint != approved.Fingerprint, "dependent values must affect confirmation");
        Check(Evaluate(owners: [validOwner]).Fingerprint != Evaluate(owners: [validOwner with { Values = "changed cache hash" }]).Fingerprint,
            "cache identity must affect confirmation");
        Check(Evaluate(owners: [validOwner]).Fingerprint != Evaluate(owners: [validOwner with { Location = "CurrentUser:Registry32" }]).Fingerprint,
            "registration location must affect confirmation");
        var item = new Vc14RemovalPlanItem(product.Code, "x64", product.Name, product.Version, "msi", product.Code, "msiexec", "verified",
            DependencyFingerprint: approved.Fingerprint, Command: approved.Argument);
        Check(Vc14Reinstall.Fingerprint([item]) != Vc14Reinstall.Fingerprint([item with { DependencyFingerprint = "changed" }]),
            "repair preview must bind dependency evidence");

        var fixture = "WixDependencyCheck: Found dependent \"" + baseline + "\", name: \"Unknown\".\r\n"
            + "Action ended 18:43:15: WixDependencyCheck. Return value 5.\r\n"
            + "Product: Microsoft Visual C++ 2022 X64 Minimum Runtime -- Removal completed successfully.\r\n"
            + "MainEngineThread is returning 0\r\n";
        var noop = Vc14DependencyPolicy.VerifyRemoval(new(0), () => product, () => fixture);
        Check(!noop.Success && !noop.PostcheckPassed && !noop.Unknown && noop.ExitCode == 0, "successful process exit must not imply successful removal");
        Check(noop.Detail.Contains("WixDependencyCheck") && noop.Detail.Contains(baseline), "logs (9) explanation should identify exact dependency");
        var removed = Vc14DependencyPolicy.VerifyRemoval(new(0), () => null, () => fixture);
        Check(removed.Success, "actual MSI absence is authoritative");
        var failed = Vc14DependencyPolicy.VerifyRemoval(new(1603), () => throw new Exception("must not read"), () => fixture);
        Check(!failed.Success && failed.ExitCode == 1603, "ordinary process failure must not be hidden");
        var reboot = Vc14DependencyPolicy.VerifyRemoval(new(3010), () => throw new Exception("must not read"), () => fixture);
        Check(reboot.Reboot && reboot.ExitCode == 3010, "reboot must stop before postcheck and further execution");
        var unknown = Vc14DependencyPolicy.VerifyRemoval(new(0, true), () => throw new Exception("must not read"), () => fixture);
        Check(unknown.Unknown && !unknown.Success, "unknown installer transaction must remain unknown");
        var unreadable = Vc14DependencyPolicy.VerifyRemoval(new(0), () => throw new IOException("cannot query MSI"), () => fixture);
        Check(unreadable.Unknown && !unreadable.Success, "unreadable MSI state must stop");
        Check(!Vc14DependencyPolicy.VerifyRemoval(new(0), () => product, () => throw new IOException("log missing")).Success,
            "missing log must not turn remaining product into success");
        var temp = Path.Combine(Path.GetTempPath(), "DeltaNFD-dependency-log-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            File.WriteAllText(temp, fixture, Encoding.Unicode);
            Check(Vc14DependencyPolicy.ReadInstallerLog(temp) == fixture, "real MSI UTF-16 logs must decode correctly");
        }
        finally { File.Delete(temp); }
        Console.WriteLine($"VC14 dependency and no-op removal checks: {count} passed. Pure identities and isolated log replay; no registry writes or installers.");
    }
}
