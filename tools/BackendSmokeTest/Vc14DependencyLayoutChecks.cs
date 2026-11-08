using DeltaNFD.Services;

/// <summary>Actual single-14.44 layout and signed originals; no registry or installer calls.</summary>
internal static class Vc14DependencyLayoutChecks
{
    internal static void Run(string historicalOriginalRoot)
    {
        var count = 0;
        void Check(bool value, string text) { if (!value) throw new IOException(text); count++; }
        void Reject(Action action, string text)
        {
            try { action(); } catch (IOException) { count++; return; }
            throw new IOException("Expected refusal: " + text);
        }
        var root = Path.GetFullPath(BackendEnvironment.AssetsRoot);
        Check(Path.GetDirectoryName(root)!.TrimEnd(Path.DirectorySeparatorChar)
            == Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar),
            "test must use its isolated output Assets, not an ancestor's media");
        Check(!Directory.Exists(Path.Combine(root, Vc14BaselineInfo.V142.BaselineDirectory)),
            "single 14.44 layout must not deploy the retired 14.42 installer");
        Check(!Vc14DependencyPolicy.VerifyPinnedBaseline(Vc14BaselineInfo.V144, "arm64"), "unsupported architecture refused");

        foreach (var arch in new[] { "x64", "x86" })
        {
            Check(Vc14DependencyPolicy.VerifyPinnedBaseline(Vc14BaselineInfo.V144, arch),
                "actual 14.44 original hash/version/architecture/Microsoft signature: " + arch);
            Check(!Vc14DependencyPolicy.VerifyPinnedBaseline(Vc14BaselineInfo.V142, arch),
                "missing retired original returns false without probing 14.44: " + arch);
            var currentId = Vc14BaselineInfo.V144.BundleIds[arch];
            var currentProof = Vc14BundleIdentity.Read(Vc14BaselineInfo.V144.Exe(arch), currentId, arch);
            Check(currentProof.Verified, "actual current intrinsic bundle identity: " + arch + " " + currentProof.Reason);
            var historicalId = Vc14BaselineInfo.V142.BundleIds[arch];
            var historicalProof = Vc14BundleIdentity.Read(Path.Combine(Path.GetFullPath(historicalOriginalRoot),
                "vc_redist." + arch + ".exe"), historicalId, arch);
            Check(historicalProof.Verified && Vc14Catalog.SameVersion(historicalProof.Version, Vc14BaselineInfo.V142.Version)
                && historicalProof.Sha256.Equals(Vc14BaselineInfo.V142.ExeHashes[arch], StringComparison.OrdinalIgnoreCase),
                "historical signed original is read only outside the deployed layout: " + arch + " " + historicalProof.Reason);
            Vc14DependencyOwner Owner(Vc14BundleProof proof) => new("Fixture:ARP", proof.Name, proof.Version,
                "Microsoft Corporation", true, true, "raw-registration-values", proof);

            foreach (var role in new[] { "Minimum", "Additional" })
            {
                var product = new Vc14Entry("{931A2CF0-2404-45EA-82F5-345735AE6A90}",
                    $"Microsoft Visual C++ 2022 {arch} {role} Runtime - 14.51.36247", "14.51.36247", arch, role,
                    "{36F68A90-239C-34DF-B58C-64B30153CE35}");
                var provider = new Vc14DependencyProvider("Fixture:HKLM:Registry64",
                    "Microsoft.VS.VC_Runtime" + role + "VSU_" + (arch == "x64" ? "amd64" : "x86") + ",v14",
                    product.Code, "provider-values", [currentId], "dependent-values");
                Vc14DependencyApproval Actual(string client, params Vc14DependencyOwner[] owners) =>
                    Vc14DependencyPolicy.Evaluate(product, [provider with { Clients = [client] }], _ => owners,
                        Vc14DependencyPolicy.VerifyPinnedBaseline);
                var orphan = Actual(currentId);
                Check(orphan.Argument == "IGNOREDEPENDENCIES=" + currentId
                    && orphan.Evidence.Contains(Vc14BaselineInfo.V144.ExeHashes[arch]),
                    "14.44 orphan uses its exact actual pinned original: " + arch + "/" + role);
                var incomplete = new Vc14DependencyOwner("Fixture:ARP", currentProof.Name, "", "", false, false, "DisplayName-only");
                Check(Actual(currentId, incomplete).Evidence.Contains("PinnedOriginalMissingArpFields"),
                    "actual 14.44 pin supplies only missing fields: " + arch + "/" + role);
                var cached = Vc14DependencyPolicy.Evaluate(product, [provider], _ => [Owner(currentProof)],
                    (_, _) => throw new Exception("verified current owner must not request another original"));
                Check(cached.Clients.Single() == currentId && cached.Evidence.Contains("\"PinnedBaselineSha256\":\"\""),
                    "complete current signed cache proves itself: " + arch + "/" + role);

                var oldProvider = provider with { Clients = [historicalId] };
                var historical = Vc14DependencyPolicy.Evaluate(product, [oldProvider], _ => [Owner(historicalProof)],
                    (_, _) => throw new Exception("verified historical owner must not request retired media"));
                Check(historical.Clients.Single() == historicalId && historical.Evidence.Contains(historicalProof.Sha256)
                    && historical.Evidence.Contains("\"PinnedBaselineSha256\":\"\""),
                    "actual historical signed cache remains valid without deployed 14.42: " + arch + "/" + role);
                var partialOld = Owner(historicalProof) with { Name = "", Version = "", Publisher = "", CacheVerified = false };
                Check(Actual(historicalId, partialOld).Evidence.Contains("SignedOriginalMissingArpFields"),
                    "historical signed identity completes absent ARP labels: " + arch + "/" + role);
                Reject(() => Actual(historicalId), "14.44 cannot authorize an orphan 14.42 reference");
                Reject(() => Actual(historicalId, Owner(historicalProof) with { CacheExists = false, CacheVerified = false, Proof = null }),
                    "historical registration without its own original remains blocked");

                // Capture both baseline and architecture, including when only the other pin is available.
                foreach (var info in Vc14BaselineInfo.All)
                {
                    var calls = 0;
                    var exact = Vc14DependencyPolicy.Evaluate(product, [provider with { Clients = [info.BundleIds[arch]] }], _ => [],
                        (requested, requestedArch) => { calls++; return ReferenceEquals(requested, info) && requestedArch == arch; });
                    Check(calls == 1 && exact.Evidence.Contains(info.ExeHashes[arch]), "pin is bound to reference baseline and architecture");
                    Reject(() => Vc14DependencyPolicy.Evaluate(product, [provider with { Clients = [info.BundleIds[arch]] }], _ => [],
                        (requested, _) => requested.Id != info.Id), "availability of only another baseline cannot authorize reference");
                }
                foreach (var bad in new[] {
                    Owner(historicalProof) with { Publisher = "Other Publisher" },
                    Owner(historicalProof) with { Name = "Third Party Game" },
                    Owner(historicalProof) with { Version = "14.44.35211.0" },
                    Owner(historicalProof) with { ReadIssue = "registry denied" },
                    Owner(historicalProof) with { CacheVerified = false },
                    Owner(historicalProof) with { Proof = historicalProof with { BundleId = currentId } },
                    Owner(historicalProof) with { Proof = historicalProof with { Architecture = arch == "x64" ? "x86" : "x64" } },
                    Owner(historicalProof) with { Proof = historicalProof with { Version = "14.44.35211.0" } },
                    Owner(historicalProof) with { Proof = historicalProof with { Sha256 = "invalid" } },
                    Owner(historicalProof) with { Proof = historicalProof with { Verified = false } } })
                    Reject(() => Actual(historicalId, bad), "historical pin retirement must not weaken contradictory/bad ownership refusal");
                foreach (var bad in new[] { incomplete with { Publisher = "Other" }, incomplete with { Version = "14.42.34433.0" },
                    incomplete with { CacheExists = true }, Owner(currentProof) with { Proof = currentProof with { Verified = false } } })
                    Reject(() => Actual(currentId, bad), "healthy 14.44 pin must not override bad caches or contradictory owners");
                var unknown = "{22222222-2222-2222-2222-222222222222}";
                Reject(() => Actual(unknown), "unknown orphan remains blocked");
                Vc14DependencyReport? report = null;
                Reject(() => Vc14DependencyPolicy.Evaluate(product, [provider with { Clients = [currentId, unknown] }], _ => [],
                    Vc14DependencyPolicy.VerifyPinnedBaseline, value => report = value), "one unknown reference blocks entire command");
                Check(report is { References.Count: 2, Issues.Count: 1 }
                    && report.References.Single(r => r.Client == currentId).Approved
                    && !report.References.Single(r => r.Client == unknown).Approved, "mixed diagnostic keeps individual decisions");
                Reject(() => Vc14DependencyPolicy.Evaluate(product, [provider], _ => [], (_, _) => false),
                    "missing/invalid exact pinned source cannot authorize an orphan");
            }
        }
        Console.WriteLine($"VC14 single-14.44 dependency layout checks: {count} passed. Actual signed originals; no registry or installer calls.");
    }
}
