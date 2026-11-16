using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using DeltaNFD.Backends;

namespace DeltaNFD.Services;

public sealed record ClockEnvironmentStep(string Key, string Identity, string Original, string Applied)
{
    public bool Attempted { get; init; }
    public bool Verified { get; init; }
}
public sealed record ClockEnvironmentBackup
{
    public int Schema { get; init; } = 1;
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Kind { get; init; } = "";
    public string Machine { get; init; } = "";
    public string Sid { get; init; } = "";
    public string EntryId { get; init; } = "";
    public string Inheritance { get; init; } = "";
    public string BootId { get; init; } = "";
    public string Phase { get; init; } = "Prepared";
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<ClockEnvironmentStep> Steps { get; init; } = [];
    public bool Restored => Phase == "Restored";
    public bool Settled => Phase is "Applied" or "Restored";
    public bool Attempted => Steps.Any(s => s.Attempted);
    [JsonIgnore] public bool HasSource => Kind == "source" || Kind == "hpet" && Schema is 2 or 3;
    [JsonIgnore] public string Digest => ClockEnvironmentPolicy.Digest(this);
}

/// <summary>Machine-wide, administrator-protected journals and a crash-safe file lease shared with clock users.</summary>
public sealed class ClockEnvironmentStore(string directory, string machine, string sid, bool protectedDirectory = false)
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DeltaNFD Clock Environment");
    public static ClockEnvironmentStore Default => new(DirectoryPath, MachineId(), CurrentSid(), true);
    public string Machine => machine;
    public string Sid => sid;
    public Action? BeforeSaveForTests { get; set; }
    private string FilePath(string kind) => Path.Combine(directory, kind is "source" or "hpet" ? kind + ".json" : throw new IOException("计时源记录类别无效。"));
    public static string CurrentSid() => WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("无法确认当前用户。");
    public static string MachineId()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        var value = key?.GetValue("MachineGuid") as string;
        return Guid.TryParse(value, out var id) ? ClockEnvironmentPolicy.Hash(id.ToString("D")) : throw new IOException("无法确认本机身份。");
    }
    public static void NoLinks(string path)
    {
        for (var d = Path.GetFullPath(path); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
            try { if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) throw new IOException("计时源记录路径包含链接。"); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
    }
    private static void VerifyAcl(FileSystemSecurity acl)
    {
        if ((acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value is not ("S-1-5-18" or "S-1-5-32-544"))
            throw new IOException("计时源记录所有者未通过核对。");
        const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule r in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (r.AccessControlType == AccessControlType.Allow && (r.FileSystemRights & writes) != 0
                && ((SecurityIdentifier)r.IdentityReference).Value is not ("S-1-5-18" or "S-1-5-32-544"))
                throw new IOException("计时源记录允许非管理员写入，已停止。");
    }
    private void CheckDirectory()
    {
        NoLinks(directory);
        if (protectedDirectory && Directory.Exists(directory)) VerifyAcl(new DirectoryInfo(directory).GetAccessControl());
    }
    private void EnsureDirectory()
    {
        NoLinks(directory);
        if (!Directory.Exists(directory))
        {
            if (protectedDirectory)
            {
                var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
                acl.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
                foreach (var id in new[] { "S-1-5-18", "S-1-5-32-544" })
                    acl.AddAccessRule(new(new SecurityIdentifier(id), FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                acl.AddAccessRule(new(new SecurityIdentifier("S-1-5-32-545"), FileSystemRights.ReadAndExecute,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(directory).Create(acl);
            }
            else Directory.CreateDirectory(directory);
        }
        CheckDirectory();
    }
    public IDisposable Lease(bool writing)
    {
        EnsureDirectory(); var path = Path.Combine(directory, "activity.lock"); NoLinks(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, writing ? FileShare.None : FileShare.ReadWrite); }
        catch (IOException ex) { throw new IOException("计时源修改或钟摆持有、扫描、测试正在执行，请等待释放后重试。", ex); }
    }
    public IReadOnlyList<ClockEnvironmentBackup> Read()
    {
        CheckDirectory(); if (!Directory.Exists(directory)) return [];
        var result = new List<ClockEnvironmentBackup>();
        foreach (var kind in new[] { "source", "hpet" })
        {
            var path = FilePath(kind); if (File.Exists(path)) result.Add(ReadFile(path, kind));
        }
        return result;
    }
    private ClockEnvironmentBackup ReadFile(string path, string kind)
    {
        NoLinks(path);
        if (protectedDirectory) VerifyAcl(new FileInfo(path).GetAccessControl());
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 2 or > 65536) throw new IOException("计时源记录大小异常。");
        var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
        using var doc = BackendJson.ParseStrict(bytes, "计时源记录");
        var record = JsonSerializer.Deserialize<ClockEnvironmentBackup>(doc.RootElement) ?? throw new IOException("计时源记录为空。");
        Validate(record, kind); return record;
    }
    private void Validate(ClockEnvironmentBackup b, string kind)
    {
        if (b.Schema is not (1 or 2 or 3 or 4) || b.Schema != 1 && kind != "hpet" || !Guid.TryParseExact(b.Id, "N", out _) || b.Kind != kind || b.Machine != machine
            || b.Sid.Length > 192 || !b.Sid.StartsWith("S-1-", StringComparison.Ordinal) || b.BootId.Length is < 1 or > 128
            || !b.BootId.All(char.IsAsciiDigit) || b.UpdatedUtc == default || b.Phase is not ("Prepared" or "Writing" or "Applied" or "Restoring" or "Restored")
            || b.Steps is null || b.Steps.Count is < 1 or > 18 || b.Schema == 1 && b.Steps.Count > 16
            || b.Steps.Select(s => s.Key.ToUpperInvariant()).Distinct().Count() != b.Steps.Count)
            throw new IOException("计时源原始记录的机器、格式或阶段未通过核对。");
        if (b.HasSource ? !ClockEnvironmentPolicy.EntryId(b.EntryId) || !ClockEnvironmentPolicy.ValidHash(b.Inheritance)
            : b.EntryId.Length != 0 || b.Inheritance.Length != 0)
            throw new IOException("计时源原始记录的启动项身份无效。");
        foreach (var step in b.Steps)
        {
            if (ClockEnvironmentPolicy.IsBcdKey(step.Key))
            {
                if (!b.HasSource || step.Identity != b.EntryId
                    || !ClockEnvironmentPolicy.BcdValue(step.Original) || step.Applied != (b.Schema == 3 && step.Key == "useplatformclock" ? "no" : ""))
                    throw new IOException("启动计时源记录范围或原值无效。");
            }
            else if (kind != "hpet" || !ClockEnvironmentPolicy.ValidDevice(step.Key)
                || !ClockEnvironmentPolicy.ValidHash(step.Identity)
                || !(step.Original == "enabled" || b.Schema is 3 or 4 && step.Original == "disabled") || step.Applied != "disabled")
                throw new IOException("HPET 原始记录设备或状态无效。");
        }
        if (b.Settled && b.Steps.Any(s => !s.Verified)) throw new IOException("计时源记录完成标记与逐项核对不一致。");
        var sourceCount = b.Steps.Count(s => ClockEnvironmentPolicy.IsBcdKey(s.Key));
        if (sourceCount != (b.HasSource ? 2 : 0) || kind == "hpet" && b.Steps.Count - sourceCount is < 1 or > 16)
            throw new IOException("计时源与 HPET 联动记录不完整。");
        if (kind == "source" && (b.Steps.Count != 2 || !b.Steps.Any(s => s.Original.Length > 0))) throw new IOException("平台计时源原值记录不完整。");
    }
    public void Save(ClockEnvironmentBackup record)
    {
        BeforeSaveForTests?.Invoke(); Validate(record, record.Kind); EnsureDirectory();
        var path = FilePath(record.Kind); NoLinks(path);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, record); file.Flush(true); }
            if (protectedDirectory)
            {
                var info = new FileInfo(temp); var acl = info.GetAccessControl(); acl.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
                info.SetAccessControl(acl); VerifyAcl(info.GetAccessControl());
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        if (Read().Single(r => r.Kind == record.Kind).Digest != record.Digest) throw new IOException("计时源记录写后核对失败。");
    }
    public IDisposable Hold(ClockEnvironmentBackup record)
    {
        CheckDirectory(); var path = FilePath(record.Kind); NoLinks(path);
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try { if (Read().Single(r => r.Kind == record.Kind).Digest != record.Digest) throw new IOException("原始记录已经变化。"); return file; }
        catch { file.Dispose(); throw; }
    }
    public void PreservePrevious(ClockEnvironmentBackup record)
    {
        if (!record.Restored) throw new IOException("仍有原始备份待恢复。");
        var found = Read().Single(r => r.Kind == record.Kind);
        if (found.Digest != record.Digest) throw new IOException("旧记录已经变化。");
        var archive = Path.Combine(directory, "archive"); NoLinks(archive); Directory.CreateDirectory(archive);
        if (protectedDirectory) VerifyAcl(new DirectoryInfo(archive).GetAccessControl());
        var destination = Path.Combine(archive, record.Kind + "-" + record.Id + ".json"); NoLinks(destination);
        if (!File.Exists(destination))
        {
            using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, record); file.Flush(true); }
            if (protectedDirectory)
            {
                var info = new FileInfo(destination); var acl = info.GetAccessControl(); acl.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
                info.SetAccessControl(acl);
            }
        }
        if (ReadFile(destination, record.Kind).Digest != record.Digest) throw new IOException("历史原始备份与当前记录不一致，未覆盖。");
    }
}
