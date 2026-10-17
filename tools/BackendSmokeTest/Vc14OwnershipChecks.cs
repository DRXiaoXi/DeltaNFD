using DeltaNFD.Services;

internal static class Vc14OwnershipChecks
{
    internal static void Run(string sourceRoot)
    {
        var count = 0;
        void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); count++; }
        void Refuse(Action run, string reason)
        { try { run(); } catch (IOException) { count++; return; } throw new Exception(reason); }
        sourceRoot = Path.GetFullPath(sourceRoot);
        var original = Vc14OwnershipOriginals.Catalog.Single();
        var id = original.BundleId;
        var proof = Vc14OwnershipOriginals.Read(id, "x64", sourceRoot)!;
        Check(proof.Verified && proof.Architecture == "x64" && proof.Version == "14.32.31326.0"
            && proof.Sha256 == original.Sha256, "actual Microsoft 14.32 signed original matches pinned hash, version and intrinsic ID: " + proof.Reason);
        Check(Vc14BundleIdentity.SameId(proof.BundleId, id) && proof.Name.Contains("14.32.31326"), "actual original identifies feedback orphan");
        var owner = Vc14OwnershipOriginals.Resolve(id, "x64", [], sourceRoot);
        Check(owner.Count == 1 && owner[0].CacheVerified && owner[0].Proof!.Verified
            && owner[0].IdentitySource == "BundledSignedOwnershipOriginal", "missing ARP and machine cache use a verified bundled ownership original");
        var product = new Vc14Entry("{931A2CF0-2404-45EA-82F5-345735AE6A90}",
            "Microsoft Visual C++ 2022 X64 Minimum Runtime - 14.51.36247", "14.51.36247", "x64", "Minimum", "{36F68A90-239C-34DF-B58C-64B30153CE35}");
        var provider = new Vc14DependencyProvider("fixture", "Microsoft.VS.VC_RuntimeMinimumVSU_amd64,v14", product.Code, "", [id]);
        Vc14DependencyApproval Evaluate(IReadOnlyList<Vc14DependencyOwner> owners) => Vc14DependencyPolicy.Evaluate(product, [provider], _ => owners, _ => false);
        var approved = Evaluate(owner);
        Check(approved.Argument == "IGNOREDEPENDENCIES=" + id && !approved.Argument.Contains("ALL"), "only this exact verified reference is ignored");
        Check(approved.Evidence.Contains(original.Sha256) && approved.Evidence.Contains("BundledSignedOwnershipOriginal"), "consent fingerprint binds source bytes and provenance");
        Check(Vc14OwnershipOriginals.Read(id, "x86", sourceRoot) is null && Vc14OwnershipOriginals.Read(id, "arm64", sourceRoot) is null,
            "no cross-architecture approval");
        foreach (var unknown in new[] { Guid.NewGuid().ToString("B"), "ALL", "../escape", id + ";ALL", Guid.Empty.ToString("B") })
            Check(Vc14OwnershipOriginals.Resolve(unknown, "x64", [], sourceRoot).Count == 0, "unknown/injected GUID has no source-based approval");
        var partial = new Vc14DependencyOwner("ARP", "", "", "", false, false, "raw Installed=1;Resume=3");
        var recovered = Vc14OwnershipOriginals.Resolve(id, "x64", [partial], sourceRoot);
        Check(Evaluate(recovered).Clients.Single() == id && recovered[0].Values == partial.Values, "partial ARP is completed without changing raw evidence");
        foreach (var bad in new[] { partial with { Name = "Third-party Game" }, partial with { Publisher = "Other Studio" },
            partial with { Name = "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.51.36247" },
            partial with { Version = "14.40.0" }, partial with { ReadIssue = "denied" },
            partial with { CacheExists = true }, partial with { Proof = proof with { Verified = false, Reason = "bad signature" } } })
            Refuse(() => Evaluate(Vc14OwnershipOriginals.Resolve(id, "x64", [bad], sourceRoot)), "original must not hide contradictory fields, bad cache or read errors");
        Refuse(() => Evaluate(Vc14OwnershipOriginals.Resolve(id, "x64", [partial, partial with { Publisher = "Other" }], sourceRoot)),
            "one contradictory registry view blocks the entire operation");
        Check(Vc14Reinstall.Fingerprint([new(id, "x64", proof.Name, proof.Version, "msi", "identity", "", "", DependencyFingerprint: approved.Fingerprint)])
            != Vc14Reinstall.Fingerprint([new(id, "x64", proof.Name, proof.Version, "msi", "identity", "", "", DependencyFingerprint: "changed")]),
            "removal consent remains bound to dependency evidence");
        var fixture = Path.Combine(Path.GetTempPath(), "DeltaNFD-ownership-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            var absent = Vc14OwnershipOriginals.Read(id, "x64", fixture)!;
            Check(!absent.Verified, "a known GUID alone never approves a missing source");
            Refuse(() => Evaluate(Vc14OwnershipOriginals.Resolve(id, "x64", [], fixture)), "missing original blocks removal");
            var file = Path.Combine(fixture, original.File);
            File.Copy(Path.Combine(sourceRoot, original.File), file);
            using (var edit = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.None)) { edit.Position = 512; edit.WriteByte(0xff); }
            Check(!Vc14OwnershipOriginals.Read(id, "x64", fixture)!.Verified, "same-sized tampered original rejected by pinned hash");
            Refuse(() => Evaluate(Vc14OwnershipOriginals.Resolve(id, "x64", [], fixture)), "bad original cannot authorize the orphan");
            File.WriteAllBytes(file, [1, 2, 3]);
            Check(!Vc14OwnershipOriginals.Read(id, "x64", fixture)!.Verified, "truncated original rejected");
        }
        finally
        {
            var full = Path.GetFullPath(fixture);
            if (Path.GetDirectoryName(full) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                || !Path.GetFileName(full).StartsWith("DeltaNFD-ownership-", StringComparison.Ordinal)) throw new IOException("Unsafe fixture cleanup root");
            Directory.Delete(full, recursive: true);
        }
        Console.WriteLine($"VC14 historical ownership: {count} passed. Actual original signature/PE/hash read-only; no installers or registry writes.");
    }
}
