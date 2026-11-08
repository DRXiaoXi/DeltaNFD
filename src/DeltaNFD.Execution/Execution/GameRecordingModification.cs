using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DeltaNFD.Services;

/// <summary>仅管理两项固定录制程序。新备份集中存入工具数据目录，兼容旧版同目录恢复记录。</summary>
public sealed class GameRecordingModification
{
    private readonly string dataRoot;
    private readonly Func<IEnumerable<string>> roots;
    private readonly Func<string?> writeBlocker;
    private readonly Action<string>? checkpoint;
    internal GameRecordingModification(string dataRoot, Func<IEnumerable<string>> roots, Func<string?> writeBlocker, Action<string>? checkpoint = null)
    { this.dataRoot = dataRoot; this.roots = roots; this.writeBlocker = writeBlocker; this.checkpoint = checkpoint; }
    private const string GameExe = "DeltaForceClient-Win64-Shipping.exe";
    private static readonly (string Id, string Title, string Relative)[] Components =
    [
        ("icreate", "洲洲时刻界面 · icreateDF.exe", @"icreate\icreate-df\icreateDF.exe"),
        ("recorder", "录制组件 · recorder.exe", @"icreate\recorder-release\recorder.exe"),
    ];

    public static GameRecordingModification CreateDefault() => new(
        Path.Combine(Path.GetDirectoryName(AppSettingsStore.DefaultPath)!, "GameModifications"),
        DeltaForceLocator.FindRoots, ReadWriteBlocker);

    private static string? ReadWriteBlocker()
    {
        if (GameTargetService.Default.IsCustom) return GameTargetService.DeltaOnlyMessage;
        try
        {
            foreach (var name in new[] { DeltaForceLocator.GameProcessName, "icreateDF", "recorder" })
            {
                var found = Process.GetProcessesByName(name);
                try { if (found.Length > 0) return $"请先完全退出三角洲及录制组件（{name}），再进行替换或恢复。"; }
                finally { foreach (var p in found) p.Dispose(); }
            }
            return null;
        }
        catch (Exception ex) { return "无法确认游戏/录制组件运行状态，未修改文件：" + ex.Message; }
    }

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool EqualPath(string a, string b) => string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);
    private static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private string RecordPath(string target) => Path.Combine(dataRoot, HashText(Full(target).ToUpperInvariant()) + ".json");

    private List<string> Installations()
    {
        var result = new List<string>();
        foreach (var input in roots())
        {
            var root = Full(input);
            RejectLinks(root);
            if (File.Exists(Path.Combine(root, "DeltaForce", "Binaries", "Win64", GameExe))) { }
            else if (File.Exists(Path.Combine(root, "Binaries", "Win64", GameExe)))
            {
                if (Path.GetFileName(root).Equals("DeltaForce", StringComparison.OrdinalIgnoreCase)
                    && !Directory.Exists(Path.Combine(root, "icreate")) && Directory.GetParent(root) is { } parent
                    && (Directory.Exists(Path.Combine(parent.FullName, "icreate"))
                        || Components.Any(c => File.Exists(RecordPath(Path.Combine(parent.FullName, c.Relative))))))
                    root = parent.FullName;
            }
            else continue;
            if (!result.Contains(root, StringComparer.OrdinalIgnoreCase)) result.Add(root);
        }
        return result;
    }

    public GameRecordingSnapshot Read()
    {
        var items = new List<GameRecordingItem>();
        var complete = true;
        string[] recordFiles;
        try
        {
            RejectLinks(dataRoot);
            recordFiles = Directory.Exists(dataRoot) ? Directory.GetFiles(dataRoot, "*.json") : [];
        }
        catch (Exception ex) { return new() { Complete = false, RecoveryReadFailed = true, Message = "恢复记录无法读取：" + ex.Message }; }
        try
        {
            var blocker = writeBlocker();
            foreach (var root in Installations())
            foreach (var component in Components)
            {
                try { items.Add(Inspect(root, component.Id, blocker).Item); }
                catch (Exception ex)
                {
                    complete = false;
                    items.Add(new() { InstallationRoot = root, ComponentId = component.Id, Title = component.Title,
                        FilePath = Path.Combine(root, component.Relative), State = "无法核对", Detail = ex.Message, NeedsReview = true,
                        HasRecord = recordFiles.Contains(RecordPath(Path.Combine(root, component.Relative)), StringComparer.OrdinalIgnoreCase) });
                }
            }
            var knownRecords = items.Select(i => RecordPath(i.FilePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unmatched = recordFiles.Count(p => !knownRecords.Contains(p));
            if (unmatched > 0) complete = false;
            return new() { Items = items, Complete = complete, UnmatchedRecords = unmatched, RecordCount = recordFiles.Length,
                Message = (unmatched > 0 ? $"另有 {unmatched} 份恢复记录未关联当前安装，请在设置中重新登记原游戏目录。 " : "")
                    + (blocker ?? (items.Count == 0 ? "未找到三角洲主程序，请先在设置中登记游戏安装目录。" : "已读取文件和备份的实际状态。")) };
        }
        catch (Exception ex) { return new() { Items = items, Complete = false, RecordCount = recordFiles.Length,
            Message = "读取游戏改动状态失败：" + ex.Message }; }
    }

    public OperationResult RestoreAll()
    {
        var snapshot = Read();
        var failures = new List<string>();
        if (!snapshot.Complete) failures.Add(snapshot.Message);
        foreach (var item in snapshot.Items.Where(i => i.HasRecord))
        {
            var result = Change(new() { InstallationRoot = item.InstallationRoot, ComponentId = item.ComponentId,
                ExpectedFingerprint = item.Fingerprint, Restore = true });
            if (!result.Success) failures.Add(item.FilePath + "：" + result.Message);
        }
        var after = Read();
        return failures.Count == 0 && after.Complete && !after.Items.Any(i => i.HasRecord)
            ? OperationResult.Ok("游戏录制组件的原件恢复及复核完成。")
            : OperationResult.Fail("仍有录制组件记录需要核对，备份保留。" + string.Join("；", failures));
    }

    public OperationResult Change(GameRecordingArgs args)
    {
        var started = false;
        try
        {
            CheckWritable();
            if (args.ArchiveRecord && args.Restore) return OperationResult.Fail("归档旧记录与恢复原文件不能同时执行。");
            if (!Components.Any(c => c.Id == args.ComponentId)) return OperationResult.Fail("未知录制组件，未修改文件。");
            var root = Installations().SingleOrDefault(r => EqualPath(r, args.InstallationRoot));
            if (root is null) return OperationResult.Fail("安装目录已变更或未登记，请刷新后重试。");
            RejectLinks(dataRoot);
            Directory.CreateDirectory(dataRoot);
            RejectLinks(dataRoot);
            var lockPath = Path.Combine(dataRoot, "operation.lock");
            RejectLinks(lockPath);
            using var gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var state = Inspect(root, args.ComponentId, null);
            if (string.IsNullOrWhiteSpace(args.ExpectedFingerprint) || state.Item.Fingerprint != args.ExpectedFingerprint)
                return OperationResult.Fail("文件或备份已变化，未执行。请刷新状态后重新确认。");
            if (args.ArchiveRecord ? !state.Item.CanArchiveRecord : args.Restore ? !state.Item.CanRestore : !state.Item.CanApply)
                return OperationResult.Fail(state.Item.Detail);
            CheckWritable();
            if (args.ArchiveRecord) ArchiveReviewedRecord(state, ref started);
            else if (args.Restore) Restore(state, ref started);
            else Apply(state, ref started);
            var after = Inspect(root, args.ComponentId, null).Item;
            var success = args.Restore || args.ArchiveRecord ? !after.HasRecord && after.CanApply : after.IsManaged;
            return success ? OperationResult.Ok(args.ArchiveRecord
                    ? "已核对当前文件内容并归档旧记录；游戏文件和现存备份均未修改。现在可重新备份并替换，备份保存在软件数据目录。"
                    : args.Restore ? "已恢复原文件及其原属性，并核对原件哈希。" : "已替换为只读空文件；原件及恢复记录已保留。")
                : OperationResult.Fail("操作后的文件状态不符合预期；原件和记录已保留，请刷新核对。");
        }
        catch (Exception ex)
        {
            Log.Warn("游戏录制组件改动未完成：" + ex.Message);
            return OperationResult.Fail((args.ArchiveRecord ? "旧记录归档未确认完成；游戏文件未修改，请刷新核对。"
                : started ? "操作中断，原件及恢复记录保留；请刷新后使用恢复原文件。" : "未执行替换或恢复。") + ex.Message);
        }
    }

    private void CheckWritable()
    {
        if (writeBlocker() is { } blocker) throw new InvalidOperationException(blocker);
    }

    private sealed record FileState(string Identity, long Length, FileAttributes Attributes, string Hash, bool Executable,
        long CreationTime, long WriteTime)
    {
        public string Stamp => $"{Identity}|{Length}|{(int)Attributes}|{Hash}|{WriteTime}";
    }

    private sealed class Journal
    {
        public int Version { get; set; } = 1;
        public string InstallationRoot { get; set; } = "";
        public string ComponentId { get; set; } = "";
        public string Token { get; set; } = "";
        public string OriginalIdentity { get; set; } = "";
        public string OriginalHash { get; set; } = "";
        public long OriginalLength { get; set; }
        public int OriginalAttributes { get; set; }
        public string PlaceholderIdentity { get; set; } = "";
        public string BackupIdentity { get; set; } = "";
        public string RestoreIdentity { get; set; } = "";
        public long OriginalCreationTime { get; set; }
        public long OriginalWriteTime { get; set; }
    }

    private sealed record Inspection(GameRecordingItem Item, string Record, Journal? Journal,
        string Backup, string Temporary, FileState? Target, FileState? Saved, FileState? Temp,
        string Restoring, FileState? RestoreTemp, string RecordHash);

    private string BackupPath(string token) => Path.Combine(dataRoot, "backups", token, "original.bak");

    private Inspection Inspect(string root, string id, string? blocker)
    {
        var component = Components.Single(c => c.Id == id);
        var target = Path.Combine(root, component.Relative);
        RejectLinks(target);
        RejectLinks(dataRoot);
        var record = RecordPath(target);
        RejectLinks(record);
        var targetState = Probe(target);
        Journal? journal = null;
        string backup = "", temporary = "", recordHash = "", restoring = "";
        FileState? saved = null, temp = null, restoreTemp = null;
        var hasRecord = File.Exists(record);
        var managed = false;
        var apply = false;
        var restore = false;
        var archiveRecord = false;
        var review = false;
        string state, detail;
        if (!hasRecord)
        {
            apply = targetState is { Executable: true };
            state = apply ? "原文件 · 未替换" : targetState is null ? "未安装此组件" : "文件异常 · 非本工具管理";
            detail = apply ? "可将原件备份到软件数据目录后替换为只读空文件；对应录制功能将无法启动。\n备份目录：" + Path.Combine(dataRoot, "backups")
                : targetState is null ? "此安装没有该文件，无需替换。" : "没有本工具的可信恢复记录；请用启动器修复游戏，不接管已有空文件。";
            review = targetState is not null && !apply;
        }
        else
        {
            var info = new FileInfo(record);
            if (info.Length > 32 * 1024) throw new InvalidDataException("恢复记录大小异常，保留文件并停止操作。");
            var bytes = File.ReadAllBytes(record);
            recordHash = Convert.ToHexString(SHA256.HashData(bytes));
            journal = JsonSerializer.Deserialize<Journal>(bytes) ?? throw new InvalidDataException("恢复记录为空。");
            if (journal.Version is not (1 or 2) || !EqualPath(journal.InstallationRoot, root) || journal.ComponentId != id
                || !Guid.TryParseExact(journal.Token, "N", out _) || journal.OriginalLength < 64
                || journal.OriginalHash.Length != 64 || string.IsNullOrWhiteSpace(journal.OriginalIdentity)
                || string.IsNullOrWhiteSpace(journal.PlaceholderIdentity) || journal.OriginalIdentity == journal.PlaceholderIdentity)
                throw new InvalidDataException("恢复记录身份不合法，未操作任何文件。");
            if (journal.Version == 2 && (string.IsNullOrWhiteSpace(journal.BackupIdentity)
                || journal.OriginalCreationTime <= 0 || journal.OriginalWriteTime <= 0))
                throw new InvalidDataException("集中备份记录不完整，保留文件并停止操作。");
            backup = journal.Version == 1 ? target + ".deltanfd-" + journal.Token + ".bak" : BackupPath(journal.Token);
            temporary = target + ".deltanfd-" + journal.Token + ".tmp";
            restoring = target + ".deltanfd-" + journal.Token + ".restore";
            saved = Probe(backup); temp = Probe(temporary);
            restoreTemp = journal.Version == 2 ? Probe(restoring) : null;
            var savedValid = SavedOriginal(saved, journal);
            var targetOriginal = Original(targetState, journal);
            var targetPlaceholder = Placeholder(targetState, journal);
            var tempValid = (temp is null || Placeholder(temp, journal))
                && (restoreTemp is null || RestoredOriginal(restoreTemp, journal));
            if (savedValid && tempValid && (targetState is null || targetPlaceholder))
            {
                managed = targetPlaceholder && targetState!.Attributes.HasFlag(FileAttributes.ReadOnly) && temp is null && restoreTemp is null;
                restore = true;
                state = managed ? "已替换 · 只读空文件" : "替换/恢复中断 · 可恢复";
                detail = (journal.Version == 1 ? "旧版同目录备份（恢复后再次替换会使用软件备份目录）：" : "软件目录中的原件备份：")
                    + backup + (managed ? "\n恢复原文件后可重新使用录制功能。" : "\n原件完整，点击恢复原文件完成恢复。" );
                review = !managed;
            }
            else if (targetOriginal && (saved is null || journal.Version == 2 && savedValid) && tempValid)
            {
                restore = true; review = true;
                state = "原件在位 · 待清理恢复记录";
                detail = "原件未改变；点击恢复原文件清理本工具留下的空文件和记录。";
            }
            else
            {
                review = true;
                state = "文件或备份已变化 · 需核对";
                var reason = saved is null ? "未找到记录中的原件备份。"
                    : !savedValid ? "备份内容或文件身份与记录不符。"
                    : !tempValid ? "临时文件与恢复记录不符。" : "目标文件与恢复记录不符。";
                detail = reason + " 不覆盖游戏更新后的文件或未知文件。请检查原安装目录及游戏完整性，并保留备份与恢复记录。\n备份：" + backup + "\n记录：" + record;
                // Launcher repair may recreate identical original bytes with a new NTFS identity.
                // This is a separate explicit metadata-only action, never a relaxation of restoration identity checks.
                archiveRecord = saved is null && temp is null && restoreTemp is null && OriginalBytes(targetState, journal);
                if (archiveRecord)
                {
                    state = "原件内容已核对 · 旧记录待归档";
                    detail = "当前文件的内容哈希和长度与记录中的原件一致，但文件身份已变化，旧备份已缺失。"
                        + "点击“核对处理”可归档旧记录，保留当前文件，然后重新备份并替换。\n旧记录：" + record;
                }
            }
        }
        var stamp = HashText(root.ToUpperInvariant() + "|" + id + "|" + recordHash + "|" + targetState?.Stamp + "|" + saved?.Stamp + "|" + temp?.Stamp + "|" + restoreTemp?.Stamp);
        if (blocker is not null) detail += "\n" + blocker;
        var item = new GameRecordingItem { InstallationRoot = root, ComponentId = id, Title = component.Title, FilePath = target,
            State = state, Detail = detail, Fingerprint = stamp, CanApply = apply && blocker is null,
            CanRestore = restore && blocker is null, CanArchiveRecord = archiveRecord && blocker is null,
            HasRecord = hasRecord, IsManaged = managed, NeedsReview = review };
        return new(item, record, journal, backup, temporary, targetState, saved, temp, restoring, restoreTemp, recordHash);
    }

    private static bool OriginalBytes(FileState? state, Journal journal) => state is { Executable: true }
        && state.Hash == journal.OriginalHash && state.Length == journal.OriginalLength;
    private static bool Original(FileState? state, Journal journal) => OriginalBytes(state, journal)
        && (state!.Identity == journal.OriginalIdentity || journal.Version == 2 && RestoredOriginal(state, journal));
    private static bool SavedOriginal(FileState? state, Journal journal) => OriginalBytes(state, journal)
        && state!.Identity == (journal.Version == 1 ? journal.OriginalIdentity : journal.BackupIdentity);
    // NTFS can tunnel the deleted destination's creation time into the renamed file.
    // Accept only the same volume + full file index, and the explicitly recorded prior creation time.
    private static bool IdentityAfterRename(string actual, string expected, string prior)
        => actual == expected || actual.Length == 43 && expected.Length == 43 && prior.Length == 43
            && actual[..26] == expected[..26] && actual[27..] == prior[27..];
    private static bool Placeholder(FileState? state, Journal journal) => state is { Length: 0 }
        && IdentityAfterRename(state.Identity, journal.PlaceholderIdentity, journal.OriginalIdentity);
    private static bool RestoredOriginal(FileState? state, Journal journal) => OriginalBytes(state, journal)
        && !string.IsNullOrEmpty(journal.RestoreIdentity)
        && IdentityAfterRename(state!.Identity, journal.RestoreIdentity, journal.PlaceholderIdentity);

    private void ArchiveReviewedRecord(Inspection state, ref bool started)
    {
        var archive = Path.Combine(dataRoot, "reviewed");
        RejectLinks(archive); Directory.CreateDirectory(archive); RejectLinks(archive);
        var prefix = Path.Combine(archive, state.Item.ComponentId + "-" + Guid.NewGuid().ToString("N"));
        var receipt = prefix + ".outcome.json";
        RejectLinks(receipt);
        CheckWritable();
        RequireJournal(state.Record, state.RecordHash);
        checkpoint?.Invoke("archive-prepared");
        // Keep the confirmed executable from being overwritten/deleted during the metadata commit.
        RejectLinks(state.Item.FilePath);
        using var targetLock = new FileStream(state.Item.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var current = Inspect(state.Item.InstallationRoot, state.Item.ComponentId, null).Item;
        if (!current.CanArchiveRecord || current.Fingerprint != state.Item.Fingerprint)
            throw new IOException("当前文件、备份或记录已变化，停止归档，请重新核对。");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Outcome = "original-content-confirmed-record-archived", state.Item.InstallationRoot, state.Item.ComponentId,
            state.Item.FilePath, state.RecordHash, state.Target!.Identity, state.Target.Hash, state.Target.Length,
            Message = "原件内容一致；只归档旧记录，未恢复或替换游戏文件。", CreatedUtc = DateTimeOffset.UtcNow,
        });
        using (var file = new FileStream(receipt, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { file.Write(bytes); file.Flush(true); }
        // Publish the receipt first. A crash before the move retains the active record and can be retried.
        CheckWritable();
        RequireJournal(state.Record, state.RecordHash);
        current = Inspect(state.Item.InstallationRoot, state.Item.ComponentId, null).Item;
        if (!current.CanArchiveRecord || current.Fingerprint != state.Item.Fingerprint)
            throw new IOException("归档前文件状态已变化，原记录保留。");
        started = true;
        File.Move(state.Record, prefix + ".json");
        checkpoint?.Invoke("archive-record-moved");
        Log.Info("录制组件原件内容已核对，旧记录逐字归档；未修改游戏文件：" + state.Item.FilePath);
    }

    private void Apply(Inspection state, ref bool started)
    {
        var target = state.Item.FilePath;
        var original = state.Target!;
        var token = Guid.NewGuid().ToString("N");
        var temp = target + ".deltanfd-" + token + ".tmp";
        var backup = BackupPath(token);
        RejectLinks(temp); RejectLinks(backup);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        RejectLinks(backup);
        // Always copy, including on the same volume. C: tool data and D: games are supported.
        CopyVerified(target, backup, original);
        var saved = Probe(backup)!;
        using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough)) file.Flush(true);
        File.SetAttributes(temp, File.GetAttributes(temp) | FileAttributes.ReadOnly);
        var placeholder = Probe(temp)!;
        var journal = new Journal { Version = 2, InstallationRoot = state.Item.InstallationRoot, ComponentId = state.Item.ComponentId,
            Token = token, OriginalIdentity = original.Identity, OriginalHash = original.Hash,
            OriginalLength = original.Length, OriginalAttributes = (int)original.Attributes, PlaceholderIdentity = placeholder.Identity,
            BackupIdentity = saved.Identity, OriginalCreationTime = original.CreationTime, OriginalWriteTime = original.WriteTime };
        string recordHash;
        try
        {
            recordHash = WriteJournal(state.Record, journal);
        }
        catch
        {
            DeletePlaceholder(temp, journal);
            if (SavedOriginal(Probe(backup), journal)) DeleteRecordedFile(backup);
            throw;
        }
        started = true;
        checkpoint?.Invoke("prepared");
        CheckWritable(); RejectLinks(target);
        if (Probe(target)?.Stamp != original.Stamp) throw new IOException("原文件已变化，停止替换。");
        if (!SavedOriginal(Probe(backup), journal)) throw new IOException("集中备份复核失败，保留原文件。");
        // The complete, flushed and journal-bound backup exists before removing the original.
        CheckWritable();
        RequireJournal(state.Record, recordHash);
        File.SetAttributes(target, original.Attributes & ~FileAttributes.ReadOnly);
        if (!Original(Probe(target), journal)) throw new IOException("删除前原文件身份变化，停止替换。");
        CheckWritable();
        File.Delete(target);
        checkpoint?.Invoke("original-removed");
        CheckWritable(); RejectLinks(target); RejectLinks(temp);
        RequireJournal(state.Record, recordHash);
        if (!SavedOriginal(Probe(backup), journal) || !Placeholder(Probe(temp), journal)) throw new IOException("备份或空文件身份变化。");
        File.Move(temp, target);
        checkpoint?.Invoke("placeholder-moved");
        Log.Info("游戏录制组件已替换，集中备份保留：" + backup);
    }

    private void Restore(Inspection state, ref bool started)
    {
        var journal = state.Journal!;
        var target = state.Item.FilePath;
        started = true;
        CheckWritable();
        if (journal.Version == 2) { RestoreCentral(state); return; }
        RequireJournal(state.Record, state.RecordHash);
        if (state.Saved is not null)
        {
            if (state.Target is not null)
            {
                if (!Placeholder(Probe(target), journal)) throw new IOException("目标不是本工具的空文件，拒绝覆盖。");
                File.Move(target, state.Temporary);
                checkpoint?.Invoke("restore-placeholder-moved");
            }
            CheckWritable(); RejectLinks(target);
            RequireJournal(state.Record, state.RecordHash);
            if (!Original(Probe(state.Backup), journal)) throw new IOException("原件备份已变化，停止恢复。");
            File.Move(state.Backup, target);
            checkpoint?.Invoke("restore-original-moved");
        }
        if (!Original(Probe(target), journal)) throw new IOException("原件哈希或身份复核失败，恢复记录保留。");
        CheckWritable();
        RequireJournal(state.Record, state.RecordHash);
        File.SetAttributes(target, (FileAttributes)journal.OriginalAttributes);
        if (Probe(state.Temporary) is not null) DeletePlaceholder(state.Temporary, journal);
        RejectLinks(state.Record);
        File.Delete(state.Record);
        Log.Info("游戏录制组件原件已恢复并核对：" + target);
    }

    private void RestoreCentral(Inspection state)
    {
        var journal = state.Journal!;
        var target = state.Item.FilePath;
        var recordHash = state.RecordHash;
        RequireJournal(state.Record, recordHash);
        if (!Original(Probe(target), journal))
        {
            if (!SavedOriginal(Probe(state.Backup), journal)) throw new IOException("集中备份已变化，停止恢复。");
            if (Probe(state.Restoring) is null)
            {
                CopyVerified(state.Backup, state.Restoring, state.Saved!);
                var candidate = Probe(state.Restoring)!;
                journal.RestoreIdentity = candidate.Identity;
                try { recordHash = WriteJournal(state.Record, journal, expectedHash: recordHash); }
                catch
                {
                    // A failed journal commit must not leave an unowned restore copy blocking retry.
                    if (RestoredOriginal(Probe(state.Restoring), journal)) DeleteRecordedFile(state.Restoring);
                    throw;
                }
            }
            if (!RestoredOriginal(Probe(state.Restoring), journal)) throw new IOException("恢复临时文件身份异常，保留备份和记录。");
            checkpoint?.Invoke("restore-copy-prepared");
            CheckWritable(); RejectLinks(target);
            RequireJournal(state.Record, recordHash);
            if (Probe(target) is not null)
            {
                if (!Placeholder(Probe(target), journal)) throw new IOException("目标不是本工具的空文件，拒绝覆盖。");
                File.Move(target, state.Temporary);
                checkpoint?.Invoke("restore-placeholder-moved");
            }
            CheckWritable(); RejectLinks(target);
            RequireJournal(state.Record, recordHash);
            if (!SavedOriginal(Probe(state.Backup), journal) || !RestoredOriginal(Probe(state.Restoring), journal))
                throw new IOException("恢复前备份或临时原件发生变化。");
            File.Move(state.Restoring, target); // same-directory, no overwrite; central backup stays until verification.
            checkpoint?.Invoke("restore-original-moved");
        }
        if (!Original(Probe(target), journal)) throw new IOException("恢复原件核对失败，集中备份及记录保留。");
        CheckWritable();
        RequireJournal(state.Record, recordHash);
        File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
        File.SetCreationTimeUtc(target, DateTime.FromFileTimeUtc(journal.OriginalCreationTime));
        File.SetLastWriteTimeUtc(target, DateTime.FromFileTimeUtc(journal.OriginalWriteTime));
        File.SetAttributes(target, (FileAttributes)journal.OriginalAttributes);
        if (!Original(Probe(target), journal)) throw new IOException("恢复后身份或哈希复核失败。");
        if (Probe(state.Temporary) is not null) DeletePlaceholder(state.Temporary, journal);
        if (Probe(state.Restoring) is not null)
        {
            if (!RestoredOriginal(Probe(state.Restoring), journal)) throw new IOException("恢复临时文件已变化，未清理。");
            DeleteRecordedFile(state.Restoring);
        }
        if (Probe(state.Backup) is not null)
        {
            if (!SavedOriginal(Probe(state.Backup), journal)) throw new IOException("集中备份已变化，未清理。");
            DeleteRecordedFile(state.Backup);
        }
        checkpoint?.Invoke("restore-backup-removed");
        RequireJournal(state.Record, recordHash);
        File.Delete(state.Record); // last: interrupted cleanup retains ownership until the next restore.
        Log.Info("游戏录制组件已从软件备份目录恢复并核对：" + target);
    }

    private static void DeleteRecordedFile(string path)
    {
        RejectLinks(path);
        File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        File.Delete(path);
    }

    private static void CopyVerified(string source, string destination, FileState expected)
    {
        RejectLinks(source); RejectLinks(destination);
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
        {
            if (Probe(source)?.Stamp != expected.Stamp) throw new IOException("复制前文件已变化。");
            // Windows CopyFile also preserves alternate streams and file security metadata.
            // Stream-only copying could discard data outside the default stream.
            File.Copy(source, destination, overwrite: false);
            RejectLinks(destination);
            File.SetAttributes(destination, File.GetAttributes(destination) & ~FileAttributes.ReadOnly);
            using var output = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough);
            output.Flush(true);
        }
        File.SetCreationTimeUtc(destination, DateTime.FromFileTimeUtc(expected.CreationTime));
        File.SetLastWriteTimeUtc(destination, DateTime.FromFileTimeUtc(expected.WriteTime));
        File.SetAttributes(destination, expected.Attributes);
        var copied = Probe(destination);
        if (copied is null || !copied.Executable || copied.Hash != expected.Hash || copied.Length != expected.Length)
            throw new IOException("复制后的原件哈希核对失败；未删除源文件。");
    }

    private static void DeletePlaceholder(string path, Journal journal)
    {
        if (!Placeholder(Probe(path), journal)) throw new IOException("临时文件不是本工具的空文件，保留以便核对。");
        File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        File.Delete(path);
    }

    private static void RequireJournal(string path, string expectedHash)
    {
        RejectLinks(path);
        if (new FileInfo(path).Length > 32 * 1024 || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) != expectedHash)
            throw new IOException("恢复记录在操作期间发生变化，保留原件和备份并停止操作。");
    }

    private static string WriteJournal(string path, Journal journal, string? expectedHash = null)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal);
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { file.Write(bytes); file.Flush(true); }
            RejectLinks(path);
            if (expectedHash is not null) RequireJournal(path, expectedHash);
            File.Move(temporary, path, overwrite: expectedHash is not null);
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void RejectLinks(string path)
    {
        for (string? current = Full(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("不操作链接/重解析路径：" + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static FileState? Probe(string path)
    {
        RejectLinks(path);
        try
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Directory)) throw new IOException("目标是目录而非普通文件：" + path);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (!GetFileInformationByHandle(file.SafeFileHandle, out var info))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            if (info.Links != 1) throw new IOException("不操作具有硬链接的文件：" + path);
            if (file.Length > 512L * 1024 * 1024) throw new IOException("组件文件大小异常，未操作。");
            var executable = false;
            if (file.Length >= 64)
            {
                using var reader = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);
                if (reader.ReadUInt16() == 0x5A4D)
                {
                    file.Position = 0x3c;
                    var offset = reader.ReadInt32();
                    if (offset >= 64 && offset <= file.Length - 4) { file.Position = offset; executable = reader.ReadUInt32() == 0x4550; }
                }
            }
            file.Position = 0;
            return new($"{info.Volume:X8}-{info.IndexHigh:X8}-{info.IndexLow:X8}-{info.CreationHigh:X8}{info.CreationLow:X8}", file.Length, attributes,
                Convert.ToHexString(SHA256.HashData(file)), executable,
                ((long)info.CreationHigh << 32) | info.CreationLow, ((long)info.WriteHigh << 32) | info.WriteLow);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
}
