namespace DeltaNFD.Updates;

public sealed class FileJournalEntry
{
    public string Path { get; set; } = "";
    public string OldHash { get; set; } = "";
    public string NewHash { get; set; } = "";
    public long OldSize { get; set; }
    public string Backup { get; set; } = "";
    public string State { get; set; } = "Pending";
}
public sealed class FileJournal
{
    public string Root { get; set; } = "";
    public string State { get; set; } = "Prepared";
    public List<FileJournalEntry> Entries { get; set; } = [];
}

public static class FileTransaction
{
    public static List<FileUpdateEntry> Changed(string root, FileUpdateManifest manifest, CancellationToken token = default)
    {
        var result = new List<FileUpdateEntry>();
        foreach (var file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            if (!FileUpdateRules.Matches(FileUpdateRules.Under(root, file.Path), file.SizeBytes, file.Sha256)) result.Add(file);
        }
        return result;
    }

    public static void ValidatePayload(string root, string payload, FileUpdateManifest manifest)
    {
        foreach (var file in Changed(root, manifest))
            if (!FileUpdateRules.Matches(FileUpdateRules.Under(payload, file.Sha256.ToLowerInvariant()), file.SizeBytes, file.Sha256))
                throw new InvalidDataException("待替换文件缺失或已变化：" + file.Path);
    }

    public static void Apply(string root, string payload, FileUpdateManifest manifest, string transaction,
        Action verifyStartup, Action<int>? afterReplace = null)
    {
        root = System.IO.Path.GetFullPath(root);
        FileUpdateRules.Validate(manifest, requireProduct: false);
        if (Directory.Exists(transaction)) throw new InvalidOperationException("事务目录已存在，必须先核对或恢复。");
        ValidatePayload(root, payload, manifest);
        var changed = Changed(root, manifest);
        var removals = manifest.Remove.Where(r => File.Exists(FileUpdateRules.Under(root, r.Path))
            && FileUpdateRules.Hash(FileUpdateRules.Under(root, r.Path)).Equals(r.PreviousSha256, StringComparison.OrdinalIgnoreCase)).ToList();
        Directory.CreateDirectory(transaction);
        Directory.CreateDirectory(System.IO.Path.Combine(transaction, "backup"));
        var journal = new FileJournal { Root = root };
        var journalPath = System.IO.Path.Combine(transaction, "journal.json");
        foreach (var item in changed.Select(f => (f.Path, Hash: f.Sha256)).Concat(removals.Select(r => (r.Path, Hash: ""))))
        {
            var target = FileUpdateRules.Under(root, item.Path);
            var entry = new FileJournalEntry { Path = item.Path, NewHash = item.Hash, Backup = "backup/" + journal.Entries.Count };
            if (File.Exists(target))
            {
                entry.OldSize = new FileInfo(target).Length;
                entry.OldHash = FileUpdateRules.Hash(target);
                File.Copy(target, FileUpdateRules.Under(transaction, entry.Backup));
                if (!FileUpdateRules.Matches(FileUpdateRules.Under(transaction, entry.Backup), entry.OldSize, entry.OldHash))
                    throw new IOException("备份校验失败。");
                using var backup = new FileStream(FileUpdateRules.Under(transaction, entry.Backup), FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                backup.Flush(true);
            }
            journal.Entries.Add(entry);
        }
        Save(journalPath, journal);
        try
        {
            journal.State = "Applying"; Save(journalPath, journal);
            var count = 0;
            foreach (var entry in journal.Entries)
            {
                var target = FileUpdateRules.Under(root, entry.Path);
                var current = File.Exists(target) ? FileUpdateRules.Hash(target) : "";
                if (!current.Equals(entry.OldHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("备份后原文件发生变化。");
                entry.State = "Intent"; Save(journalPath, journal);
                if (entry.NewHash.Length == 0) File.Delete(target);
                else AtomicCopy(FileUpdateRules.Under(payload, entry.NewHash.ToLowerInvariant()), target);
                entry.State = "Done"; Save(journalPath, journal);
                afterReplace?.Invoke(++count);
            }
            journal.State = "Verifying"; Save(journalPath, journal);
            if (Changed(root, manifest).Count > 0) throw new IOException("替换后完整目录校验失败。");
            verifyStartup();
            journal.State = "Committed"; Save(journalPath, journal);
        }
        catch (Exception failure)
        {
            try { Rollback(transaction, root); }
            catch (Exception rollbackFailure) { throw new AggregateException("增量更新失败且恢复尚未完成，保留独立更新器和备份。", failure, rollbackFailure); }
            throw;
        }
    }

    public static void Rollback(string transaction, string expectedRoot)
    {
        var path = System.IO.Path.Combine(transaction, "journal.json");
        var journal = System.Text.Json.JsonSerializer.Deserialize<FileJournal>(File.ReadAllBytes(path), FileUpdateRules.Json)
            ?? throw new InvalidDataException("事务记录缺失。");
        if (!System.IO.Path.GetFullPath(journal.Root).Equals(System.IO.Path.GetFullPath(expectedRoot), StringComparison.OrdinalIgnoreCase)
            || journal.Entries.Count > 10000) throw new InvalidDataException("事务安装目录不匹配。");
        if (journal.State is "Committed" or "RolledBack") return;
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in journal.Entries)
        {
            if (!unique.Add(FileUpdateRules.Relative(entry.Path)) || (entry.OldHash.Length > 0 && !FileUpdateRules.IsHash(entry.OldHash))
                || (entry.NewHash.Length > 0 && !FileUpdateRules.IsHash(entry.NewHash))
                || !System.Text.RegularExpressions.Regex.IsMatch(entry.Backup, @"^backup/[0-9]+$"))
                throw new InvalidDataException("事务条目非法。");
            if (entry.State != "Pending" && entry.OldHash.Length > 0
                && !FileUpdateRules.Matches(FileUpdateRules.Under(transaction, entry.Backup), entry.OldSize, entry.OldHash))
                throw new IOException("恢复备份缺失或不匹配，保留现场。");
        }
        journal.State = "RollingBack"; Save(path, journal);
        try
        {
            foreach (var entry in journal.Entries.AsEnumerable().Reverse())
            {
                if (entry.State == "Pending") continue;
                var target = FileUpdateRules.Under(expectedRoot, entry.Path);
                var current = File.Exists(target) ? FileUpdateRules.Hash(target) : "";
                if (current.Equals(entry.OldHash, StringComparison.OrdinalIgnoreCase)) continue;
                if (!current.Equals(entry.NewHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("恢复目标已被其他程序修改，拒绝覆盖：" + entry.Path);
                if (entry.OldHash.Length == 0) File.Delete(target);
                else
                {
                    AtomicCopy(FileUpdateRules.Under(transaction, entry.Backup), target);
                    if (!FileUpdateRules.Matches(target, entry.OldSize, entry.OldHash)) throw new IOException("恢复后的文件校验失败。");
                }
            }
            journal.State = "RolledBack"; Save(path, journal);
        }
        catch { journal.State = "RollbackFailed"; Save(path, journal); throw; }
    }

    private static void Save(string path, FileJournal journal) => FileUpdateRules.AtomicJson(path, journal);
    private static void AtomicCopy(string source, string target)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        var temporary = target + ".file-update-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            { input.CopyTo(output); output.Flush(true); }
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
