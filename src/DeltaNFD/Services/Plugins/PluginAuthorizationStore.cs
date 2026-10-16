using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace DeltaNFD.Services.Plugins;

public sealed record PluginAuthorizationProof(string PluginId, string Version, string PackageSha256,
    string FileTableDigest, string InstallDirectory, string EntryExecutable, bool OfflineAllowed);

public sealed class PluginAuthorizationStore
{
    private readonly string _path;
    private readonly bool _machine;
    public PluginAuthorizationStore(string path, bool machine = false)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("授权路径必须是绝对路径。");
        _path = Path.GetFullPath(path); _machine = machine;
    }
    public static PluginAuthorizationStore ForCurrentUser()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new UnauthorizedAccessException("无法确认用户 SID。");
        return new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Delta NFD", "PluginAuthorizations", sid + ".json"), machine: true);
    }

    public bool Authorize(PluginIndexEntry entry, string digest, bool offline, out string error)
    {
        try
        {
            using var mutex = Acquire();
            var all = Read();
            all.RemoveAll(p => p.PluginId == entry.Id);
            all.Add(new(entry.Id, entry.Version, entry.PackageSha256, digest, Path.GetFullPath(entry.InstallDirectory),
                Path.GetFullPath(entry.EntryExecutable), offline));
            Write(all); error = ""; return true;
        }
        catch (Exception ex) { error = "保存独立授权证明失败：" + ex.Message; return false; }
    }

    public bool Matches(PluginIndexEntry entry, string digest, bool requireOffline, out string error)
    {
        try
        {
            using var mutex = Acquire();
            var proof = Read().SingleOrDefault(p => p.PluginId == entry.Id);
            if (proof is null || proof.Version != entry.Version || !proof.PackageSha256.Equals(entry.PackageSha256, StringComparison.OrdinalIgnoreCase) ||
                proof.FileTableDigest != digest || !proof.InstallDirectory.Equals(Path.GetFullPath(entry.InstallDirectory), StringComparison.OrdinalIgnoreCase) ||
                !proof.EntryExecutable.Equals(Path.GetFullPath(entry.EntryExecutable), StringComparison.OrdinalIgnoreCase) || (requireOffline && !proof.OfflineAllowed))
                throw new UnauthorizedAccessException("授权证明缺失或文件/路径已改变，请重新确认授权。");
            error = ""; return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    public bool Revoke(string pluginId, out string error)
    {
        try
        {
            using var mutex = Acquire();
            var all = Read();
            if (all.RemoveAll(p => p.PluginId == pluginId) > 0) Write(all);
            error = ""; return true;
        }
        catch (Exception ex) { error = "撤销独立授权证明失败：" + ex.Message; return false; }
    }

    private IDisposable Acquire()
    {
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(_path.ToUpperInvariant())))[..20];
        var mutex = new Mutex(false, "Global\\DeltaNFD_PluginAuth_" + digest);
        try
        {
            try { if (!mutex.WaitOne(TimeSpan.FromSeconds(10))) throw new TimeoutException("授权仓库忙。"); }
            catch (AbandonedMutexException) { }
            return new Lease(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }
    private sealed class Lease(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }

    private List<PluginAuthorizationProof> Read()
    {
        ValidatePath();
        try
        {
            var info = new FileInfo(_path);
            if (info.Length > 1024 * 1024) throw new InvalidDataException("授权仓库过大。");
            var proofs = JsonSerializer.Deserialize<List<PluginAuthorizationProof>>(File.ReadAllText(_path))
                ?? throw new InvalidDataException("授权仓库为空结构。");
            if (proofs.Any(p => p is null || p.FileTableDigest.Length != 64 || p.PackageSha256.Length != 64) ||
                proofs.GroupBy(p => p.PluginId).Any(g => g.Count() != 1)) throw new InvalidDataException("授权证明结构无效。");
            return proofs;
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
    }

    private void ValidatePath()
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(_path)!); directory is not null; directory = directory.Parent)
        {
            if (!directory.Exists) continue;
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("授权路径含重解析目录。");
        }
        try
        {
            if ((File.GetAttributes(_path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new IOException("授权路径不是普通文件。");
            if (_machine) CheckSecurity(FileSystemAclExtensions.GetAccessControl(new FileInfo(_path)));
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        var parent = new DirectoryInfo(Path.GetDirectoryName(_path)!);
        if (_machine && parent.Exists) CheckSecurity(FileSystemAclExtensions.GetAccessControl(parent));
        if (_machine && parent.Parent is { Exists: true } anchor)
            CheckSecurity(FileSystemAclExtensions.GetAccessControl(anchor));
    }

    private static void CheckSecurity(FileSystemSecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
            ?? throw new UnauthorizedAccessException("无法确认授权证明属主。");
        if (!owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) && !owner.IsWellKnown(WellKnownSidType.LocalSystemSid))
            throw new UnauthorizedAccessException("授权证明属主不可信。");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            var sid = (SecurityIdentifier)rule.IdentityReference;
            var writes = rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
                FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership);
            if (rule.AccessControlType == AccessControlType.Allow && writes != 0 && !sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) && !sid.IsWellKnown(WellKnownSidType.LocalSystemSid))
                throw new UnauthorizedAccessException("授权证明可被非管理员修改。");
        }
    }

    private void Write(List<PluginAuthorizationProof> proofs)
    {
        ValidatePath();
        var parent = Path.GetDirectoryName(_path)!;
        if (_machine && Directory.GetParent(parent) is { Exists: false } anchor)
        {
            Directory.CreateDirectory(anchor.FullName);
            var security = new DirectorySecurity();
            security.SetSecurityDescriptorSddlForm("O:BAG:BAD:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)");
            FileSystemAclExtensions.SetAccessControl(anchor, security);
        }
        if (!Directory.Exists(parent))
        {
            Directory.CreateDirectory(parent);
            if (_machine)
            {
                var security = new DirectorySecurity();
                security.SetSecurityDescriptorSddlForm("O:BAG:BAD:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)");
                FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(parent), security);
            }
        }
        ValidatePath();
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(proofs), new UTF8Encoding(false));
            if (_machine)
            {
                var security = new FileSecurity();
                security.SetSecurityDescriptorSddlForm("O:BAG:BAD:P(A;;FA;;;BA)(A;;FA;;;SY)");
                FileSystemAclExtensions.SetAccessControl(new FileInfo(temp), security);
            }
            File.Move(temp, _path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
