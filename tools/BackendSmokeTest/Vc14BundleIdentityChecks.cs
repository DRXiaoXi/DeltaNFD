using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using DeltaNFD.Services;

internal static class Vc14BundleIdentityChecks
{
    internal static void Run(string root)
    {
        int count = 0;
        void Check(bool condition, string detail) { if (!condition) throw new IOException(detail); count++; }
        void Reject(Action action, string detail)
        {
            try { action(); } catch (Exception ex) when (ex is IOException or BadImageFormatException) { count++; return; }
            throw new IOException("Expected refusal: " + detail);
        }
        foreach (var arch in new[] { "x64", "x86" })
        {
            var path = Path.Combine(Path.GetFullPath(root), "vc_redist." + arch + ".exe");
            var expected = Vc14Reinstall.BaselineBundles[arch];
            var proof = Vc14BundleIdentity.Read(path, expected, arch);
            Check(proof.Verified && Vc14BundleIdentity.SameId(proof.BundleId, expected), "actual Microsoft original intrinsic identity: " + arch + " " + proof.Reason);
            Check(proof.Sha256 == Vc14Reinstall.BaselineHashes[arch] && Vc14Catalog.SameVersion(proof.Version, Vc14Reinstall.BaselineVersion), "original hash and version");
            Check(!Vc14BundleIdentity.Read(path, Guid.NewGuid().ToString("B"), arch).Verified, "wrong reference cannot claim original");
            Check(!Vc14BundleIdentity.Read(path, expected, arch == "x64" ? "x86" : "x64").Verified, "signed wrong architecture refused");
            var bytes = File.ReadAllBytes(path);
            int offset, certificateDirectory;
            using (var stream = new MemoryStream(bytes, writable: false))
            using (var pe = new PEReader(stream))
            {
                offset = pe.PEHeaders.SectionHeaders.Single(s => s.Name == ".wixburn").PointerToRawData;
                certificateDirectory = pe.PEHeaders.PEHeaderStartOffset + (pe.PEHeaders.PEHeader!.Magic == PEMagic.PE32 ? 96 : 112) + 4 * 8;
            }
            foreach (var position in new[] { offset, offset + 4 })
            {
                var altered = (byte[])bytes.Clone(); altered[position] ^= 0xff;
                Reject(() => Vc14BundleIdentity.ReadId(new MemoryStream(altered)), "unsupported magic/version");
            }
            var emptyId = (byte[])bytes.Clone(); Array.Clear(emptyId, offset + 8, 16);
            Reject(() => Vc14BundleIdentity.ReadId(new MemoryStream(emptyId)), "empty bundle identity");
            var outsideSignature = (byte[])bytes.Clone();
            BinaryPrimitives.WriteInt32LittleEndian(outsideSignature.AsSpan(certificateDirectory), offset + 12);
            Reject(() => Vc14BundleIdentity.ReadId(new MemoryStream(outsideSignature)), "identity outside signed region");
            var malformed = bytes[..256];
            Reject(() => Vc14BundleIdentity.ReadId(new MemoryStream(malformed)), "truncated PE");
            var temporary = Path.Combine(Path.GetTempPath(), "DeltaNFD-bundle-proof-" + Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                bytes[offset + 8] ^= 0x01; File.WriteAllBytes(temporary, bytes);
                Check(!Vc14BundleIdentity.Read(temporary, expected, arch).Verified, "tampered signed original refused without execution");
            }
            finally { File.Delete(temporary); }
            var candidates = Vc14BundleIdentity.FixedCacheCandidates("C:/ProgramData/Package Cache", expected, arch);
            Check(candidates.Count == 1 && candidates[0].EndsWith("vc_redist." + arch + ".exe")
                && Path.GetFileName(Path.GetDirectoryName(candidates[0]))!.Equals(expected, StringComparison.OrdinalIgnoreCase), "only the exact referenced directory is searched");
            var cacheRoot = Path.Combine(Path.GetTempPath(), "DeltaNFD-cache-proof-" + Guid.NewGuid().ToString("N"));
            try
            {
                var cachePath = Vc14BundleIdentity.FixedCacheCandidates(cacheRoot, expected, arch).Single();
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!); File.Copy(path, cachePath);
                var recovered = Vc14DependencyPolicy.ResolveCacheOwners(expected, arch, [], cacheRoot);
                Check(recovered.Count == 1 && recovered[0].CacheVerified && recovered[0].Name == proof.Name
                    && recovered[0].Location.StartsWith("ExactGuidPackageCache:"), "cache-only discovery retains signed original name and provenance");
                var msi = new Vc14Entry("{931A2CF0-2404-45EA-82F5-345735AE6A90}", "Microsoft Visual C++ 2022 " + arch + " Minimum Runtime - 14.51.36247",
                    "14.51.36247", arch, "Minimum", "{36F68A90-239C-34DF-B58C-64B30153CE35}");
                var provider = new Vc14DependencyProvider("test", "Microsoft.VS.VC_RuntimeMinimumVSU_" + (arch == "x64" ? "amd64" : "x86") + ",v14", msi.Code, "", [expected]);
                Check(Vc14DependencyPolicy.Evaluate(msi, [provider], _ => recovered, _ => true).Clients.Single() == expected,
                    "actual original flows from missing ARP through narrow approval");
                var wrong = Guid.NewGuid().ToString("B");
                var wrongPath = Vc14BundleIdentity.FixedCacheCandidates(cacheRoot, wrong, arch).Single();
                Directory.CreateDirectory(Path.GetDirectoryName(wrongPath)!); File.Copy(path, wrongPath);
                var unknown = Vc14DependencyPolicy.ResolveCacheOwners(wrong, arch, [], cacheRoot);
                Check(unknown.Count == 1 && !unknown[0].CacheVerified, "signed original in another GUID folder is not that owner");
                Reject(() => Vc14DependencyPolicy.Evaluate(msi, [provider with { Clients = [wrong] }], _ => unknown, _ => false),
                    "copied signed original cannot authorize arbitrary orphan");
                var missing = new Vc14DependencyOwner("ARP", proof.Name, proof.Version, "Microsoft Corporation", false, false, "original registration");
                var reconciled = Vc14DependencyPolicy.ResolveCacheOwners(expected, arch, [missing], cacheRoot);
                Check(reconciled[0].CacheVerified && reconciled[0].Values == missing.Values, "missing registered cache can use exact-ID original while binding registration evidence");
                var partial = new Vc14DependencyOwner("ARP:Registry32", "", "", "", false, false, "Installed=1;Resume=3");
                var resumedOwner = Vc14DependencyPolicy.ResolveCacheOwners(expected, arch, [partial], cacheRoot).Single();
                Check(resumedOwner.CacheVerified && resumedOwner.Name == proof.Name && resumedOwner.Version == proof.Version
                    && resumedOwner.Publisher == "Microsoft Corporation" && resumedOwner.Values == partial.Values
                    && resumedOwner.IdentitySource == "SignedOriginalMissingArpFields", "logs11 partial Burn metadata recovered through actual signed cache");
                Check(Vc14DependencyPolicy.Evaluate(msi, [provider], _ => [resumedOwner], _ => true).Clients.Single() == expected,
                    "actual signed cache allows the incomplete ARP owner's exact dependency");
                var conflict = Vc14DependencyPolicy.ResolveCacheOwners(expected, arch, [missing with { Version = "14.44.0" }], cacheRoot);
                Check(!conflict[0].CacheVerified && conflict[0].Proof!.Verified, "valid original does not repair contradictory registration identity");
            }
            finally
            {
                // This fresh GUID test root is the only directory created/deleted here.
                var resolved = Path.GetFullPath(cacheRoot);
                if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                    || !Path.GetFileName(resolved).StartsWith("DeltaNFD-cache-proof-", StringComparison.Ordinal))
                    throw new IOException("Refusing cleanup outside the isolated cache fixture");
                if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
            }
        }
        foreach (var invalid in new[] { "../escape", "ALL", "{11111111-1111-1111-1111-111111111111};ALL", Guid.Empty.ToString("B") })
            Check(Vc14BundleIdentity.FixedCacheCandidates("C:/Cache", invalid, "x64").Count == 0, "no cache-path injection");
        Check(Vc14BundleIdentity.FixedCacheCandidates("C:/Cache", Guid.NewGuid().ToString("B"), "arm64").Count == 0, "no ARM64 expansion");
        Console.WriteLine($"VC14 intrinsic bundle checks: {count} passed. Actual originals read/hashed; installers never executed.");
    }
}
