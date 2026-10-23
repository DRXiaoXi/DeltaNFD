using System.Security.Cryptography;
using System.Text.Json;

namespace DeltaNFD.Services;

/// <summary>Reset only tool configuration. Recovery records and system optimization backups never enter this list.</summary>
internal sealed class FactoryResetConfiguration(string root)
{
    private static readonly string[] ConfigurationFiles = ["settings.json", "procman-rules.json", "procman-background.json"];
    private static readonly string[] EvidenceFiles = ["offline-mode.json", "procman-changes.json"];
    private readonly string _root = Path.GetFullPath(root);
    internal string BackupDirectory { get; private set; } = "";
    internal Action<string>? BeforeReplaceForTests { get; set; }

    internal void Backup()
    {
        CheckDirectory(_root);
        foreach (var name in ConfigurationFiles.Concat(EvidenceFiles)) ReadOptional(name);
        var backupRoot = Path.Combine(_root, "factory-reset-backups");
        if (Directory.Exists(backupRoot)) CheckDirectory(backupRoot);
        BackupDirectory = Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(BackupDirectory);
        var manifest = new List<object>();
        foreach (var name in ConfigurationFiles.Concat(EvidenceFiles))
        {
            var bytes = ReadOptional(name);
            if (bytes is null) { manifest.Add(new { File = name, Exists = false, Sha256 = "" }); continue; }
            var target = Path.Combine(BackupDirectory, name);
            File.WriteAllBytes(target, bytes);
            if (!File.ReadAllBytes(target).SequenceEqual(bytes)) throw new IOException("恢复出厂设置备份复检失败：" + name);
            manifest.Add(new { File = name, Exists = true, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) });
        }
        File.WriteAllText(Path.Combine(BackupDirectory, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal void PauseAutomation()
    {
        RequireBackup();
        var original = ReadOptional("settings.json");
        var settings = original is null ? new AppSettings() : JsonSerializer.Deserialize<AppSettings>(original)
            ?? throw new InvalidDataException("设置文件为空，未猜测恢复配置。");
        settings.FrameModeActive = settings.DwmRestartOnGameStart = settings.GamePriorityEnabled = settings.GameAffinityRuleEnabled = false;
        settings.SingleCcdExcludeCpu0Enabled = settings.DualCcdImmediateEnabled = false;
        settings.FrameAutomaticMemoryEnabled = false;
        settings.CloseToTrayEnabled = false;
        WriteAtomic("settings.json", JsonSerializer.SerializeToUtf8Bytes(settings));
    }

    internal void CommitDefaults()
    {
        RequireBackup();
        // Capture the settled configuration after recovery: rollback must not resurrect active pre-recovery flags.
        var settled = ConfigurationFiles.ToDictionary(name => name, ReadOptional);
        var beforeReset = Path.Combine(BackupDirectory, "after-recovery");
        Directory.CreateDirectory(beforeReset);
        foreach (var pair in settled.Where(p => p.Value is not null)) File.WriteAllBytes(Path.Combine(beforeReset, pair.Key), pair.Value!);
        var defaults = new Dictionary<string, byte[]>
        {
            ["settings.json"] = JsonSerializer.SerializeToUtf8Bytes(new AppSettings()),
            ["procman-rules.json"] = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, Rules = Array.Empty<ProcmanRuleEntry>() }),
            ["procman-background.json"] = JsonSerializer.SerializeToUtf8Bytes(new ProcmanBackgroundConfig()),
        };
        var written = new List<string>();
        try
        {
            foreach (var pair in defaults)
            {
                BeforeReplaceForTests?.Invoke(pair.Key);
                if (!Same(ReadOptional(pair.Key), settled[pair.Key])) throw new IOException("配置在恢复期间发生外部变化，未覆盖：" + pair.Key);
                WriteAtomic(pair.Key, pair.Value);
                written.Add(pair.Key);
                if (!Same(ReadOptional(pair.Key), pair.Value)) throw new IOException("默认配置写入复检失败：" + pair.Key);
            }
        }
        catch (Exception error)
        {
            var failures = new List<string>();
            foreach (var name in written.AsEnumerable().Reverse())
            {
                try
                {
                    if (!Same(ReadOptional(name), defaults[name])) throw new IOException("出现外部修改，未回写覆盖。");
                    if (settled[name] is { } bytes) WriteAtomic(name, bytes);
                    else File.Delete(Path.Combine(_root, name));
                }
                catch (Exception rollbackError) { failures.Add(name + "：" + rollbackError.Message); }
            }
            throw new IOException(error.Message + (failures.Count == 0 ? "；已回退本次配置重置，保留恢复后的安全状态。"
                : "；部分配置回退未完成，备份保留：" + string.Join("；", failures)), error);
        }
    }

    internal async Task CommitAfterRecoveryAsync(Func<Task<OperationResult>> recover)
    {
        RequireBackup();
        var result = await recover();
        if (!result.Success || result.IsPartial || result.OutcomeUnknown)
            throw new InvalidOperationException("恢复尚未确认完成，未写入默认配置：" + result.Message);
        CommitDefaults();
    }

    private static bool Same(byte[]? first, byte[]? second) => first is null ? second is null : second is not null && first.SequenceEqual(second);
    private void RequireBackup()
    {
        if (BackupDirectory.Length == 0 || !File.Exists(Path.Combine(BackupDirectory, "manifest.json")))
            throw new InvalidOperationException("原配置备份尚未完成，不能重置。");
    }
    private byte[]? ReadOptional(string name)
    {
        CheckDirectory(_root);
        var path = Path.Combine(_root, name);
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) throw new IOException("配置路径不是普通文件：" + name);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 16 * 1024 * 1024) throw new IOException("配置过大，未自动处理：" + name);
        using var output = new MemoryStream(); file.CopyTo(output); return output.ToArray();
    }
    private void WriteAtomic(string name, byte[] bytes)
    {
        var original = ReadOptional(name);
        var path = Path.Combine(_root, name);
        var temporary = Path.Combine(_root, ".factory-reset-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(true); }
            if (!Same(ReadOptional(name), original)) throw new IOException("配置文件被其他进程修改，未覆盖：" + name);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void CheckDirectory(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("配置目录包含链接，未执行重置。");
    }
}
