using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeltaNFD.Backends;

namespace DeltaNFD.Services;

internal sealed class NvContainerFileBackup(string directory, string machine, Func<NvContainerBackup?> readLegacy,
    string administratorSid = "S-1-5-32-544", NvContainerTarget target = NvContainerTarget.Display) : INvContainerBackupStore
{
    private sealed record Record(int Schema, string Machine, NvContainerBackup Backup, [property: JsonRequired] bool Restored);
    private static readonly JsonSerializerOptions FileJson = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    private const string SystemAccount = "S-1-5-18";
    private string PathName => Path.Combine(directory, target switch
    {
        NvContainerTarget.LegacyTelemetry => "telemetry-container.json",
        NvContainerTarget.LocalSystem => "local-system-container.json",
        _ => "display-container.json",
    });
    private string Label => target switch
    {
        NvContainerTarget.LegacyTelemetry => "NVIDIA 遥测服务",
        NvContainerTarget.LocalSystem => "NVIDIA LocalSystem Container",
        _ => "显示容器",
    };

    internal static NvContainerFileBackup Default => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DeltaNFD NVIDIA Display Container"),
        ClockEnvironmentStore.MachineId(), new NvContainerRegistryBackup().Read);

    internal static NvContainerFileBackup Telemetry => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DeltaNFD NVIDIA Telemetry"),
        ClockEnvironmentStore.MachineId(), () => null, target: NvContainerTarget.LegacyTelemetry);

    internal static NvContainerFileBackup LocalSystem => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DeltaNFD NVIDIA LocalSystem Container"),
        ClockEnvironmentStore.MachineId(), () => null, target: NvContainerTarget.LocalSystem);

    public NvContainerBackup? Read()
    {
        var record = ReadRecord();
        return record is null ? readLegacy() : record.Restored ? null : record.Backup;
    }

    public void Save(NvContainerBackup backup)
    {
        var record = ReadRecord();
        if (record is { Restored: false } || record is null && readLegacy() is not null)
            throw new IOException($"已有{Label}原值备份，未覆盖。");
        Write(new(1, machine, backup, false), record);
    }

    public void ReplaceConflicting(NvContainerBackup expected, NvContainerBackup replacement)
    {
        var record = ReadRecord();
        if (target != NvContainerTarget.Display || (record is null ? readLegacy() : record.Restored ? null : record.Backup) != expected)
            throw new IOException($"{Label}冲突备份已变化，未操作服务。");

        var archive = Path.Combine(directory, $"display-container.conflict-{Guid.NewGuid():N}.json");
        Write(record ?? new(1, machine, expected, false), record, archive);
        if (record is null && readLegacy() != expected)
            throw new IOException($"{Label}旧注册表备份已变化，未操作服务。");
        Write(new(1, machine, replacement, false), record);
    }

    public void PrepareRestore(NvContainerBackup backup)
    {
        var record = ReadRecord();
        if (record is { Restored: false } && record.Backup == backup) return;
        if (record is not null || readLegacy() != backup)
            throw new IOException($"{Label}原值备份已变化，未操作服务。");
        // Import the legacy original before touching the service. The file then becomes authoritative.
        Write(new(1, machine, backup, false), null);
    }

    public void Clear(NvContainerBackup backup)
    {
        if (ReadRecord() is not { Restored: false } record || record.Backup != backup)
            throw new IOException($"{Label}原值备份已变化，未确认备份收尾。");
        // Keep the restored original and suppress stale registry data, including after a restart.
        Write(record with { Restored = true }, record);
    }

    private Record? ReadRecord(string? filePath = null)
    {
        if (!CheckDirectory()) return null;
        try
        {
            var path = filePath ?? PathName;
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException($"{Label}备份路径不是普通文件。");
            VerifyAcl(new FileInfo(path).GetAccessControl());
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length is < 2 or > 16384) throw new IOException($"{Label}备份长度异常。");
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            using var document = BackendJson.ParseStrict(bytes, $"{Label}原值备份");
            var record = JsonSerializer.Deserialize<Record>(document.RootElement, FileJson) ?? throw new IOException($"{Label}备份为空。");
            Validate(record);
            return record;
        }
        catch (FileNotFoundException) { return null; }
    }

    private void Validate(Record record)
    {
        if (record.Schema != 1 || string.IsNullOrWhiteSpace(machine) || record.Machine != machine || record.Backup is not { } backup
            || backup.Mode is not (NvContainerMode.Automatic or NvContainerMode.Manual)
            || NvDisplayContainerService.ParseContainerImage(backup.Image, target) is null
            || (target switch
            {
                NvContainerTarget.LegacyTelemetry => backup.Account is not ("LocalSystem" or "NetworkService"),
                NvContainerTarget.LocalSystem => backup.Account != "LocalSystem",
                _ => backup.Account is not null,
            }))
            throw new IOException($"{Label}备份格式、本机身份或服务配置未通过核对。");
    }

    private void Write(Record record, Record? expected, string? archive = null)
    {
        Validate(record);
        if (!CheckDirectory())
        {
            var acl = new DirectorySecurity();
            acl.SetOwner(new SecurityIdentifier(administratorSid));
            acl.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { administratorSid, SystemAccount })
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).Create(acl);
            if (!CheckDirectory()) throw new IOException($"无法核对{Label}备份目录。");
        }

        var target = archive ?? PathName;
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, record);
                stream.Flush(flushToDisk: true);
            }
            var file = new FileInfo(temp);
            var acl = file.GetAccessControl();
            acl.SetOwner(new SecurityIdentifier(administratorSid));
            file.SetAccessControl(acl);
            VerifyAcl(file.GetAccessControl());
            // Recheck the destination before replacing it. Invalid or untrusted data is never overwritten.
            if (ReadRecord() != expected) throw new IOException($"{Label}备份在保存前已变化。");
            if (!MoveFileExW(temp, target, archive is null ? 1u | 8u : 8u)) // ReplaceExisting | WriteThrough
                throw NvContainerScm.Error($"原子保存{Label}备份文件", Marshal.GetLastWin32Error());
            if (ReadRecord(archive) != record) throw new IOException($"{Label}备份写后核对失败。");
        }
        finally { File.Delete(temp); }
    }

    private bool CheckDirectory()
    {
        var current = Path.GetFullPath(directory);
        var exists = true;
        for (var entry = current; !string.IsNullOrEmpty(entry); entry = Path.GetDirectoryName(entry))
        {
            try
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                    throw new IOException($"{Label}备份目录包含链接或非目录项。");
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { exists = false; }
        }
        if (exists) VerifyAcl(new DirectoryInfo(current).GetAccessControl(), directoryAcl: true);
        return exists;
    }

    private bool IsTrusted(string? sid) => sid == administratorSid || sid == SystemAccount;

    private void VerifyAcl(FileSystemSecurity security, bool directoryAcl = false)
    {
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        var acl = descriptor.DiscretionaryAcl;
        if (!IsTrusted(descriptor.Owner?.Value) || acl is null
            || !descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclPresent)
            || directoryAcl && !descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected)
            || acl.Cast<GenericAce>().Any(rule => rule is not CommonAce { AceType: AceType.AccessAllowed } ace
                || !IsTrusted(ace.SecurityIdentifier.Value)))
            throw new IOException($"{Label}备份权限未通过核对。");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string existing, string destination, uint flags);
}
