using System.Text;
using DeltaNFD.Services;

internal static class Vc14DependencyChecks
{
    internal static void Replay(string directory, string? ownershipRoot = null)
    {
        // Local analysis snapshots are fixtures, never authorization for an actual uninstall.
        // Only flat numbered files from the bounded archive reader are loaded; no archive paths are followed.
        var root = Path.GetFullPath(directory);
        using var index = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "index.json")));
        var count = 0;
        foreach (var row in index.RootElement.EnumerateArray())
        {
            if (!row.GetProperty("member").GetString()!.Contains("vc14-dependencies-", StringComparison.Ordinal)) continue;
            var file = row.GetProperty("file").GetString()!;
            if (Path.GetFileName(file) != file || !file.EndsWith(".json", StringComparison.Ordinal)) throw new IOException("Invalid fixture filename");
            var path = Path.Combine(root, file);
            if (new FileInfo(path).Length > 1024 * 1024) throw new IOException("Oversized fixture");
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
            var report = System.Text.Json.JsonSerializer.Deserialize<Vc14DependencyReport>(json.RootElement.GetProperty("Report").GetRawText())!;
            Vc14DependencyReport? replayed = null;
            try
            {
                _ = Vc14DependencyPolicy.Evaluate(report.Product, report.Providers,
                    id =>
                    {
                        var owners = report.References.Where(r => r.Client.Equals(id, StringComparison.OrdinalIgnoreCase))
                            .SelectMany(r => r.Owners).Distinct().ToArray();
                        return ownershipRoot is null ? owners : Vc14OwnershipOriginals.Resolve(id, report.Product.Architecture, owners, ownershipRoot);
                    }, _ => true, r => replayed = r);
            }
            catch (IOException) when (replayed is not null) { }
            var denied = replayed!.References.Where(r => !r.Approved).Select(r => r.Client).Distinct().Order().ToArray();
            var expected = row.GetProperty("archive").GetString() == "rar" && ownershipRoot is null ? new[] { "{2d507699-404c-4c8b-a54a-38e352f32cdd}" } : [];
            if (!denied.SequenceEqual(expected)) throw new Exception("Unexpected dependency replay result: " + file);
            Console.WriteLine($"{file}: references={replayed.References.Count}, remaining unknown clients={denied.Length}");
            count++;
        }
        if (count != 4) throw new Exception("Expected the four logs12/rar4 dependency snapshots");
        Console.WriteLine($"Dependency report replay: {count} passed. Existing saved proofs simulated; supplied ownership original verified read-only; no registry writes or installers.");
    }

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
        var baselineProof = new Vc14BundleProof("C:/ProgramData/Package Cache/" + baseline + "/vc_redist.x64.exe",
            Vc14Reinstall.BaselineHashes["x64"], baseline, Vc14Reinstall.BaselineVersion, "x64", true, "signed exact identity",
            "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.42.34433");
        for (var fields = 1; fields < 8; fields++)
        {
            var partial = validOwner with { Name = (fields & 1) == 0 ? validOwner.Name : "",
                Version = (fields & 2) == 0 ? validOwner.Version : "",
                Publisher = (fields & 4) == 0 ? validOwner.Publisher : "", CacheVerified = false, Proof = baselineProof,
                Values = "Installed=1;Resume=3;raw-registry-snapshot" };
            var reconciled = Vc14DependencyPolicy.ResolveCacheOwners(baseline, "x64", [partial], "C:/nonexistent-test-cache").Single();
            Check(reconciled.CacheVerified && reconciled.IdentitySource == "SignedOriginalMissingArpFields"
                && reconciled.Values == partial.Values, "missing ARP metadata is recovered from exact signed original while retaining raw evidence");
            Check(Evaluate(owners: [reconciled]).Clients.Single() == baseline, "partial Burn registration no longer blocks its own verified dependency");
            Check(Vc14DependencyPolicy.HasVerifiedBaselineOwners("x64", [reconciled]), "the same incomplete baseline registration is valid for subsequent original repair");
        }
        foreach (var partial in new[] {
            validOwner with { Name = "", Publisher = "Other", Proof = baselineProof },
            validOwner with { Publisher = "", Version = "14.44.0", Proof = baselineProof },
            validOwner with { Version = "", Name = "Third Party Software", Proof = baselineProof },
            validOwner with { Name = "", Proof = baselineProof with { Verified = false } },
            validOwner with { Name = "", Proof = baselineProof with { BundleId = own } },
            validOwner with { Name = "", Proof = baselineProof with { Architecture = "x86" } },
            validOwner with { Name = "", Proof = baselineProof, ReadIssue = "registry snapshot denied" } })
        {
            var resolved = Vc14DependencyPolicy.CompleteMissingOwnerIdentity(baseline, "x64", partial);
            Reject(() => Evaluate(owners: [resolved]), "contradictory ARP or invalid cache proof cannot fill missing fields");
            Check(!Vc14DependencyPolicy.HasVerifiedBaselineOwners("x64", [resolved]), "contradictory owner cannot select baseline repair");
        }
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
        var historicalProof = new Vc14BundleProof("C:/ProgramData/Package Cache/" + own + "/vc_redist.x64.exe",
            new string('a', 64), own, "14.44.35211.0", "x64", true, "signed intrinsic bundle ID");
        var historicalOwner = Owner(historicalProof.Version) with { Proof = historicalProof };
        // logs (12): historical x86 14.42.34438 cache proves an ARP entry containing only Installed/Resume.
        var historicalX86Id = "{ba10fda9-f731-441f-a999-000bbb7ceec2}";
        var x86 = product with { Architecture = "x86", Name = "Microsoft Visual C++ 2022 X86 Minimum Runtime - 14.51.36247" };
        var x86Provider = provider with { Key = "Microsoft.VS.VC_RuntimeMinimumVSU_x86,v14", Clients = [historicalX86Id] };
        var x86Partial = new Vc14DependencyOwner("LocalMachine:Registry32:ARP", "", "", "", true, false,
            "Installed=1;Resume=3", historicalProof with { BundleId = historicalX86Id, Architecture = "x86", Version = "14.42.34438.0",
                Name = "Microsoft Visual C++ 2015-2022 Redistributable (x86) - 14.42.34438" });
        var x86Approval = Vc14DependencyPolicy.Evaluate(x86, [x86Provider], _ => [x86Partial], _ => false);
        Check(x86Approval.Clients.Single() == historicalX86Id && x86Approval.Evidence.Contains("SignedOriginalMissingArpFields")
            && x86Approval.Evidence.Contains("Installed=1;Resume=3"), "logs12 incomplete historical x86 registration uses exact signed cache and preserves raw values");
        foreach (var arch in new[] { "x64", "x86" })
        {
            var id = Vc14Reinstall.BaselineBundles[arch];
            var p = product with { Architecture = arch, Name = $"Microsoft Visual C++ 2022 {arch} Minimum Runtime - 14.51.36247" };
            var dependent = provider with { Key = "Microsoft.VS.VC_RuntimeMinimumVSU_" + (arch == "x64" ? "amd64" : "x86") + ",v14", Clients = [id] };
            var label = $"Microsoft Visual C++ 2015-2022 Redistributable ({arch}) - 14.42.34433";
            var incomplete = new Vc14DependencyOwner("LocalMachine:Registry32:ARP", label, "", "", false, false, "DisplayName-only");
            var fixedOwner = Vc14DependencyPolicy.Evaluate(p, [dependent], _ => [incomplete], a => a == arch);
            Check(fixedOwner.Clients.Single() == id && fixedOwner.Evidence.Contains("PinnedOriginalMissingArpFields"),
                "rar4 missing baseline metadata and cache can use the exact verified pinned original");
            Check(Vc14DependencyPolicy.HasVerifiedBaselineOwners(arch, [incomplete], pinned: true), "the same pinned proof permits subsequent baseline repair");
            Check(!Vc14DependencyPolicy.HasVerifiedBaselineOwners(arch, [incomplete]), "baseline ID without signed original cannot authorize repair");
            foreach (var conflict in new[] { incomplete with { Publisher = "Other" }, incomplete with { Version = "14.51.36247" },
                incomplete with { Name = "Third-party Software" }, incomplete with { CacheExists = true }, incomplete with { ReadIssue = "denied" } })
            {
                Reject(() => Vc14DependencyPolicy.Evaluate(p, [dependent], _ => [conflict], _ => true), "pinned original must not override conflicting identity or an existing bad cache");
                Check(!Vc14DependencyPolicy.HasVerifiedBaselineOwners(arch, [conflict], pinned: true), "conflicting identity cannot select the repair path");
            }
        }
        Check(Evaluate(sameRuntime, [historicalOwner]).Argument == "IGNOREDEPENDENCIES=" + own,
            "signed older CRT owner is approved only for its exact dependency key");
        Check(Evaluate(sameRuntime, [historicalOwner with { Location = "ExactGuidPackageCache", Values = "NoArpRegistration" }]).Clients.Count == 1,
            "missing ARP is recoverable through an exact-ID signed original");
        foreach (var badProof in new[] { historicalProof with { Verified = false }, historicalProof with { BundleId = baseline },
            historicalProof with { Architecture = "x86" }, historicalProof with { Version = "14.43.0" }, historicalProof with { Sha256 = "missing" } })
            Reject(() => Evaluate(sameRuntime, [historicalOwner with { Proof = badProof }]), "conflicting historical cache proof is refused");
        Reject(() => Evaluate(sameRuntime, [historicalOwner with { ReadIssue = "registry read denied" }]), "unreadable conflicting view is refused");
        Reject(() => Evaluate(sameRuntime, [historicalOwner with { Publisher = "Game Studio" }]), "historical proof cannot override third-party ARP identity");
        Reject(() => Evaluate(sameRuntime, [historicalOwner, historicalOwner with { Location = "CurrentUser:Registry32", Version = "14.45.0" }]),
            "historical proof does not bypass contradictory views");
        Check(Evaluate(sameRuntime, [historicalOwner]).Fingerprint != Evaluate(sameRuntime,
            [historicalOwner with { Proof = historicalProof with { Sha256 = new string('b', 64) } }]).Fingerprint,
            "historical original bytes affect the confirmation fingerprint");
        Vc14DependencyReport? diagnostics = null;
        var unknownClient = "{2d507699-404c-4c8b-a54a-38e352f32cdd}";
        var thirdParty = "{22222222-2222-2222-2222-222222222222}";
        Reject(() => Vc14DependencyPolicy.Evaluate(product, [provider with { Clients = [unknownClient, thirdParty, own] }],
            c => c == own ? [historicalOwner] : c == thirdParty ? [Owner() with { Name = "Third Party Game" }] : [],
            _ => true, r => diagnostics = r), "mixed references block the whole removal");
        Check(diagnostics is { References.Count: 3, Issues.Count: 2 }, "diagnostics collect all references after a refusal");
        Check(diagnostics!.References.Single(r => r.Client == unknownClient).Reason.Contains("孤立引用"), "feedback GUID without proof stays unknown, not allowlisted");
        Check(diagnostics.References.Single(r => r.Client == thirdParty).Owners.Single().Name == "Third Party Game",
            "diagnostics retain the actual referencing application");
        Check(diagnostics.References.Single(r => r.Client == own).Approved
            && diagnostics.References.Single(r => r.Client == own).Reason.Contains("历史"), "diagnostics distinguish approved historical ownership");
        Check(diagnostics.Providers.Count == 1 && diagnostics.Product == product, "diagnostics bind provider and MSI identity");
        var diagnosticRoot = Path.Combine(Path.GetTempPath(), "DeltaNFD-dependency-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(diagnosticRoot);
        var diagnosticId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(diagnosticRoot, "isolated-test.marker"), diagnosticId.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(diagnosticRoot, diagnosticId);
        bool WriteDiagnostic(Vc14DependencyReport value) => (bool)typeof(Vc14DependencyPolicy)
            .GetMethod("SaveReport", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, [value])!;
        Check(WriteDiagnostic(diagnostics), "production diagnostic writer saves a blocked snapshot in isolated logs");
        var diagnosticFile = Path.Combine(Log.LogDirectory, "vc14-dependencies-x64-Minimum.json");
        using (var savedDiagnostic = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(diagnosticFile)))
        {
            Check(savedDiagnostic.RootElement.GetProperty("OwnerLookupScope").GetArrayLength() == 6
                && savedDiagnostic.RootElement.GetProperty("Report").GetProperty("References").GetArrayLength() == 3,
                "saved diagnostic retains all references and four-view/exact-cache scope");
            Check(savedDiagnostic.RootElement.GetProperty("Report").GetProperty("Issues").GetArrayLength() == 2
                && File.ReadAllText(diagnosticFile).Contains(historicalProof.Sha256), "saved diagnostic binds cache hash and all denial reasons");
        }
        Check(WriteDiagnostic(diagnostics with { Issues = ["updated snapshot"] })
            && File.ReadAllText(diagnosticFile).Contains("updated snapshot"), "production diagnostic atomically refreshes the fixed snapshot file");
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
