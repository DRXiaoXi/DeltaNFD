using System.Text.Json;

namespace DeltaNFD.Updates;

/// <summary>Only created after the player confirms an update; stored inside the protected session.</summary>
public sealed class FileFallbackIntent
{
    public string Nonce { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Root { get; set; } = "";
    public string ManifestHash { get; set; } = "";
    public string ReleaseJson { get; set; } = "";
    public int Mode { get; set; }
    public int Server { get; set; }
    public int Channel { get; set; }
}

public sealed class FileFallbackReady
{
    public string Nonce { get; set; } = "";
    public string Reason { get; set; } = "";
    public int WorkerPid { get; set; }
    public long WorkerStartUtcTicks { get; set; }
}

public static class FileUpdateFallback
{
    public static string ActivePath(string root) => Path.Combine(ProtectedSessions.BaseDirectory,
        "active-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())))[..24] + ".json");

    // A missing/invalid journal in an existing transaction is unknown, not proof of rollback.
    public static void RequireSafeTransaction(string session, string root)
    {
        var transaction = FileUpdateRules.Under(session, "transaction");
        if (!Directory.Exists(transaction)) return;
        var path = FileUpdateRules.Under(session, "transaction/journal.json");
        var journal = Read<FileJournal>(path);
        if (journal.State != "RolledBack" || !SameRoot(journal.Root, root))
            throw new IOException("文件更新事务尚未确认回滚，已保留现场，请先恢复：" + session);
    }

    public static FileFallbackIntent ReadIntent(string session, string root, string nonce)
    {
        var intent = Read<FileFallbackIntent>(FileUpdateRules.Under(session, "full-fallback-intent.json"));
        if (!Guid.TryParseExact(nonce, "N", out _) || intent.Nonce != nonce || !SameRoot(intent.Root, root)
            || !FileUpdateRules.IsHash(intent.ManifestHash) || intent.ReleaseJson.Length is < 2 or > 262144
            || intent.CreatedUtc > DateTimeOffset.UtcNow.AddMinutes(1)
            || intent.CreatedUtc < DateTimeOffset.UtcNow.AddHours(-2))
            throw new InvalidDataException("全量回退记录身份无效或已过期，请重新确认更新。");
        var approval = FileUpdateRules.Under(session, "apply-approved.txt");
        if (!File.Exists(approval) || !File.ReadAllText(approval).Equals(intent.ManifestHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("文件更新未经本次确认，不能自动续接全量更新。");
        RequireSafeTransaction(session, root);
        return intent;
    }

    public static FileFallbackReady Claim(string session, string nonce)
    {
        var ready = FileUpdateRules.Under(session, "full-fallback-ready.json");
        var request = Read<FileFallbackReady>(ready);
        if (request.Nonce != nonce || request.WorkerPid <= 0 || request.WorkerStartUtcTicks <= 0)
            throw new InvalidDataException("全量回退请求与更新器身份不一致。");
        // Atomic, one use. An interruption or a canceled fallback never creates an automatic retry loop.
        File.Move(ready, FileUpdateRules.Under(session, "full-fallback-consumed.json"), overwrite: false);
        return request;
    }

    public static bool SameRoot(string first, string second) => Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar)
        .Equals(Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static T Read<T>(string path)
    {
        if (new FileInfo(path).Length > 512 * 1024) throw new InvalidDataException("更新恢复记录过大。");
        return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), FileUpdateRules.Json)
            ?? throw new InvalidDataException("更新恢复记录为空。");
    }
}
