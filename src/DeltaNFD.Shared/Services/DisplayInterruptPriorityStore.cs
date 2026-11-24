using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeltaNFD.Backends;

namespace DeltaNFD.Services;

public sealed record DisplayInterruptBackup(int Schema, string Machine, string InstanceId, string Identity,
    [property: JsonRequired] uint? Original, DateTimeOffset SavedUtc)
{
    [JsonIgnore] public string Id => DisplayInterruptPriorityPolicy.Hash(InstanceId.ToUpperInvariant());
    [JsonIgnore] public string Digest => DisplayInterruptPriorityPolicy.Hash(JsonSerializer.Serialize(this));
}

/// <summary>Machine-wide original values for this feature. Old driver priority records use a separate store.</summary>
public sealed class DisplayInterruptPriorityStore(string directory, string machine, bool protect = true)
{
    private const string Admin = "S-1-5-32-544";
    private const string SystemAccount = "S-1-5-18";
    private static readonly JsonSerializerOptions JsonOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DeltaNFD Display Interrupt Priority");
    public static DisplayInterruptPriorityStore Default => new(DirectoryPath, ClockEnvironmentStore.MachineId());
    public string Machine => machine;

    public T Exclusive<T>(Func<T> operation)
    {
        // Share the lock name with older versions that can edit the same registry value.
        using var mutex = new Mutex(false, @"Global\DeltaNFD.DriverPriority.System.v1");
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("另一项设备中断设置正在执行，请稍后重试。");
            return operation();
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    public IReadOnlyList<DisplayInterruptBackup> ReadAll()
    {
        if (!CheckDirectory(directory)) return [];
        var files = Directory.EnumerateFiles(directory, "*.json").Take(129).ToArray();
        if (files.Length > 128) throw new IOException("显卡中断备份数量超出核对范围。");
        return files.Select(path =>
        {
            using var stream = Open(path);
            var backup = Read(stream);
            if (!Path.GetFileNameWithoutExtension(path).Equals(backup.Id, StringComparison.OrdinalIgnoreCase))
                throw new IOException("显卡中断备份的设备标识无法核对。");
            return backup;
        }).ToArray();
    }

    public void Save(DisplayInterruptBackup backup)
    {
        EnsureDirectory(directory);
        var path = PathFor(backup);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, backup, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            if (protect)
            {
                var info = new FileInfo(temp);
                var acl = info.GetAccessControl();
                acl.SetOwner(new SecurityIdentifier(Admin));
                info.SetAccessControl(acl);
                CheckAcl(info.GetAccessControl(), isDirectory: false);
            }
            File.Move(temp, path);
            using var verified = Hold(backup);
        }
        finally { File.Delete(temp); }
    }

    public IDisposable Hold(DisplayInterruptBackup backup)
    {
        var stream = Open(PathFor(backup));
        try
        {
            if (Read(stream) != backup) throw new IOException("显卡中断原值备份已变化。");
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    public void Archive(DisplayInterruptBackup backup)
    {
        using (Hold(backup)) { }
        var archive = Path.Combine(directory, "archive");
        EnsureDirectory(archive);
        File.Move(PathFor(backup), Path.Combine(archive, backup.Id + "-" + Guid.NewGuid().ToString("N") + ".json"));
    }

    private string PathFor(DisplayInterruptBackup backup)
    {
        if (!CheckDirectory(directory) || !DisplayInterruptPriorityPolicy.ValidId(backup.InstanceId))
            throw new IOException("显卡中断原值备份路径未通过核对。");
        var path = Path.Combine(directory, backup.Id + ".json");
        ClockEnvironmentStore.NoLinks(path);
        return path;
    }

    private FileStream Open(string path)
    {
        ClockEnvironmentStore.NoLinks(path);
        if (protect) CheckAcl(new FileInfo(path).GetAccessControl(), isDirectory: false);
        return new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private DisplayInterruptBackup Read(FileStream stream)
    {
        if (stream.Length is < 2 or > 8192) throw new IOException("显卡中断备份大小异常。");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        using var doc = BackendJson.ParseStrict(bytes, "显卡中断原值备份");
        var backup = JsonSerializer.Deserialize<DisplayInterruptBackup>(doc.RootElement, JsonOptions);
        if (backup is not { Schema: 1 } || backup.Machine != machine || !DisplayInterruptPriorityPolicy.ValidId(backup.InstanceId)
            || !DisplayInterruptPriorityPolicy.ValidHash(backup.Identity) || backup.SavedUtc == default
            || backup.Original == DisplayInterruptPriorityPolicy.High)
            throw new IOException("显卡中断备份的本机身份、设备或原值未通过核对。");
        return backup;
    }

    private bool CheckDirectory(string path)
    {
        ClockEnvironmentStore.NoLinks(path);
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) == 0)
                throw new IOException("显卡中断备份路径不是目录。");
            if (protect) CheckAcl(new DirectoryInfo(path).GetAccessControl(), isDirectory: true);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    private void EnsureDirectory(string path)
    {
        if (CheckDirectory(path)) return;
        if (protect)
        {
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            acl.SetOwner(new SecurityIdentifier(Admin));
            foreach (var sid in new[] { Admin, SystemAccount })
                acl.AddAccessRule(new(new SecurityIdentifier(sid), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).Create(acl);
        }
        else Directory.CreateDirectory(path);
        if (!CheckDirectory(path)) throw new IOException("显卡中断备份目录未通过核对。");
    }

    private static void CheckAcl(FileSystemSecurity security, bool isDirectory)
    {
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.Owner?.Value is not (Admin or SystemAccount) || descriptor.DiscretionaryAcl is not { } acl
            || !descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclPresent)
            || isDirectory && !descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected)
            || acl.Cast<GenericAce>().Any(rule => rule is not CommonAce { AceType: AceType.AccessAllowed } ace
                || ace.SecurityIdentifier.Value is not (Admin or SystemAccount)))
            throw new IOException("显卡中断备份权限未通过核对。");
    }
}
