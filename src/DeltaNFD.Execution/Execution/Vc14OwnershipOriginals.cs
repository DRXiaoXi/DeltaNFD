using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeltaNFD.Services;

internal sealed record Vc14OwnershipOriginal(string BundleId, string Architecture, string Version, string File,
    long Bytes, string Sha256, string Url);

/// <summary>Read-only ownership evidence. These historical packages are never installed or executed.</summary>
internal static class Vc14OwnershipOriginals
{
    internal static IReadOnlyList<Vc14OwnershipOriginal> Catalog { get; } = Load();

    private static IReadOnlyList<Vc14OwnershipOriginal> Load()
    {
        using var stream = typeof(Vc14OwnershipOriginals).Assembly.GetManifestResourceStream("Vc14OwnershipOriginals.catalog.json")
            ?? throw new IOException("旧包装身份原包清单缺失。");
        return JsonSerializer.Deserialize<Vc14OwnershipOriginal[]>(stream) ?? throw new IOException("旧包装身份原包清单无效。");
    }

    internal static Vc14BundleProof? Read(string client, string arch, string root)
    {
        var entry = Catalog.SingleOrDefault(p => Vc14BundleIdentity.SameId(client, p.BundleId) && arch == p.Architecture);
        if (entry is null) return null; // No search, download or GUID-only approval for an unknown reference.
        var path = Path.Combine(root, entry.File);
        try
        {
            path = AlphaRestore.OfficialCppCatalog.Resolve(root, entry.File);
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (source.Length != entry.Bytes || !Convert.ToHexString(SHA256.HashData(source)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("随工具提供的旧包装身份原包大小或SHA256不符。");
            var proof = Vc14BundleIdentity.Read(path, client, arch);
            if (!proof.Verified || !Vc14Catalog.SameVersion(proof.Version, entry.Version)
                || !proof.Sha256.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("旧包装身份原包未通过签名、版本、架构或内置BundleId核验：" + proof.Reason);
            return proof with { Reason = "随工具提供的微软历史原包已核验（仅作引用归属证明，不安装）" };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { return new(path, "", client, entry.Version, arch, false, "旧包装身份核验未完成：" + ex.Message); }
    }

    internal static IReadOnlyList<Vc14DependencyOwner> Resolve(string client, string arch,
        IReadOnlyList<Vc14DependencyOwner> owners, string sourceRoot)
    {
        // A missing original may be supplied; a contradictory/unreadable registry or bad existing
        // cache cannot be hidden by a healthy bundled source. Raw registration values are retained.
        if (owners.Any(o => o.ReadIssue.Length > 0 || o.CacheExists || o.Proof is not null)) return owners;
        var proof = Read(client, arch, sourceRoot);
        if (proof is null) return owners;
        if (owners.Count == 0)
            return [new("BundledSignedOwnershipOriginal:" + proof.Path, proof.Name, proof.Version, "Microsoft Corporation",
                proof.Verified, proof.Verified, "NoArpRegistration;OwnershipEvidenceOnly", proof,
                ReadIssue: proof.Verified ? "" : proof.Reason, IdentitySource: "BundledSignedOwnershipOriginal")];
        return owners.Select(o =>
        {
            var namedVersion = Regex.Match(o.Name, @" - (14\.\d+\.\d+(?:\.\d+)?)$");
            var issue = !proof.Verified ? proof.Reason
                : namedVersion.Success && !Vc14Catalog.SameVersion(namedVersion.Groups[1].Value, proof.Version)
                    ? "旧包装登记名称中的版本与原包身份互相矛盾。" : "";
            return Vc14DependencyPolicy.CompleteMissingOwnerIdentity(client, arch, o with
            {
                CacheExists = proof.Verified,
                CacheVerified = proof.Verified && Vc14Catalog.SameVersion(o.Version, proof.Version),
                Proof = proof, IdentitySource = "BundledSignedOwnershipOriginal", ReadIssue = issue,
            });
        }).ToArray();
    }
}
