using Microsoft.Win32;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DeltaNFD.Services;

internal sealed record GuardRegistryValue(string? Debugger, RegistryValueKind Kind, string? Owner, bool ForeignOwner);
internal sealed record GuardFileValue(string Path, string Identity, string Dacl);
internal interface IGuardRegistry
{
    GuardRegistryValue Read(string name);
    void SetDebugger(string name, string? value, RegistryValueKind kind);
    void SetOwner(string name, string? value);
    void RemoveEmptyKey(string name);
}
internal interface IGuardFiles
{
    GuardFileValue Read(string path);
    void SetDacl(string path, string identity, string expected, string sddl);
}
internal interface IGuardStateStore
{
    GuardProtectionState Load();
    void Save(GuardProtectionState state);
}
internal sealed class GuardProtectionState
{
    public int Version { get; set; } = 1;
    public List<GuardIfeoRecord> Ifeo { get; set; } = [];
    public List<GuardAclRecord> Acls { get; set; } = [];
    public List<GuardLegacyAclRepair> LegacyAclRepairs { get; set; } = [];
}
internal sealed class GuardLegacyAclRepair
{
    public string Path { get; set; } = "";
    public string Identity { get; set; } = "";
    public string Before { get; set; } = "";
    public string After { get; set; } = "";
    public bool Completed { get; set; }
}
internal sealed class GuardIfeoRecord
{
    public string Name { get; set; } = "";
    public string Marker { get; set; } = "";
    public string? Original { get; set; }
    public RegistryValueKind OriginalKind { get; set; }
}
internal sealed class GuardAclRecord
{
    public string Path { get; set; } = "";
    public string Identity { get; set; } = "";
    public string Original { get; set; } = "";
    public string Applied { get; set; } = "";
}
internal sealed record GuardProtectionStatus(int Blocked, int Managed, int External, bool FileDenied, bool PendingAcl);

/// <summary>Transactional protection and restore; does not claim identical values written by another tool.</summary>
internal sealed class RuntimeGuardProtection
{
    internal const string IfeoRoot = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    internal const string Debugger = @"%windir%\System32\taskkill.exe";
    internal const string OwnerName = "DeltaNFD_RuntimeGuard_Owner";
    internal const string OwnerPrefix = "DeltaNFD.RuntimeGuard/1/";
    internal static readonly string[] Names = ["vc_redist.x64.exe", "vc_redist.x86.exe", "vc_redist.arm64.exe",
        "vcredist_x64.exe", "vcredist_x86.exe", "vcredist_ia64.exe", "vcredist.exe", "DXSETUP.exe", "dxwebsetup.exe",
        "UE4PrereqSetup_x64.exe", "UE4PrereqSetup_x86.exe"];
    private readonly IGuardRegistry _registry;
    private readonly IGuardFiles _files;
    private readonly IGuardStateStore _store;
    private readonly TweakBackupStore _legacy;
    internal RuntimeGuardProtection(TweakBackupStore legacy) : this(new GuardRegistryBackend(), new GuardFileBackend(), new GuardStateStore(), legacy) { }
    internal RuntimeGuardProtection(IGuardRegistry registry, IGuardFiles files, IGuardStateStore store, TweakBackupStore legacy)
    { _registry = registry; _files = files; _store = store; _legacy = legacy; }
    internal static OperationResult Exclusive(Func<OperationResult> action)
    {
        try
        {
            using var mutex = new Mutex(false, @"Global\DeltaNFD_RuntimeGuard_1");
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(30)); } catch (AbandonedMutexException) { acquired = true; }
                return acquired ? action() : OperationResult.Fail("另一项运行库操作仍在执行，请稍后再试。");
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
        catch (Exception ex) { Log.Error("运行库防护操作失败", ex); return OperationResult.Fail(ex.Message); }
    }
    private static bool Signature(GuardRegistryValue value) =>
        value.Kind is RegistryValueKind.String or RegistryValueKind.ExpandString &&
        string.Equals(value.Debugger, Debugger, StringComparison.OrdinalIgnoreCase);
    private RegistryValueBackup? Legacy(string name) => _legacy.GetStrict("HKLM", IfeoRoot + "\\" + name, "Debugger");
    private bool CanAdoptLegacy(string name, GuardRegistryValue current)
    {
        if (!Signature(current) || current.ForeignOwner || current.Owner is not null) return false;
        var backup = Legacy(name);
        // Roaming backups are user-writable: never elevate an arbitrary saved Debugger command.
        return backup?.ValueKind == RegistryValueKind.None;
    }
    private static bool Owned(GuardRegistryValue value, GuardIfeoRecord entry) => !value.ForeignOwner && value.Owner == entry.Marker;

    internal GuardProtectionStatus Probe(string? currentFile)
    {
        var state = _store.Load();
        int blocked = 0, managed = 0, external = 0;
        foreach (var name in Names)
        {
            var value = _registry.Read(name);
            if (string.IsNullOrWhiteSpace(value.Debugger)) continue;
            blocked++;
            var record = state.Ifeo.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (Signature(value) && ((record is not null && Owned(value, record)) ||
                (!value.ForeignOwner && value.Owner?.StartsWith(OwnerPrefix, StringComparison.Ordinal) == true) || CanAdoptLegacy(name, value))) managed++;
            else external++;
        }
        var paths = state.Acls.Select(a => a.Path).Concat(state.LegacyAclRepairs.Where(r => !r.Completed).Select(r => r.Path)).ToList();
        if (!string.IsNullOrWhiteSpace(currentFile)) paths.Add(currentFile);
        var denied = false;
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase)) denied |= HasExecuteDeny(_files.Read(path).Dacl);
        return new(blocked, managed, external, denied, state.Acls.Count > 0 || state.LegacyAclRepairs.Any(r => !r.Completed));
    }
    internal string? RepairBlocker(string? currentFile)
    {
        try
        {
            var status = Probe(currentFile);
            return status.Blocked > 0 || status.FileDenied || status.PendingAcl
                ? "仍存在运行库安装器拦截或待恢复权限。请先关闭防护（包括其他工具的防护），再修复运行库；尚未卸载任何运行库。" : null;
        }
        catch (Exception ex) { return "无法确认运行库防护已关闭，拒绝修复，尚未卸载任何运行库：" + ex.Message; }
    }
    internal OperationResult Enable(string? file)
    {
        var state = _store.Load();
        if (state.LegacyAclRepairs.Any(r => !r.Completed))
            return OperationResult.Fail("旧 UE4 权限修复尚未完成，请先完成修复或关闭防护。");
        if (!string.IsNullOrWhiteSpace(file))
        {
            try
            {
                var current = _files.Read(file);
                var owned = state.Acls.FirstOrDefault(a => a.Path.Equals(current.Path, StringComparison.OrdinalIgnoreCase));
                if (owned is null && HasExecuteDeny(current.Dacl))
                    return OperationResult.Fail("UE4 存在未记录的执行拒绝，请先确认修复旧权限残留；未新增 IFEO 拦截。");
                if (owned is not null && (owned.Identity != current.Identity || NormalizeDacl(current.Dacl) != NormalizeDacl(owned.Applied)))
                    return OperationResult.Fail("已记录的 UE4 文件或权限已变化，请先恢复；未新增 IFEO 拦截。");
            }
            catch (Exception ex) { return OperationResult.Fail("UE4 权限预检失败，未新增拦截：" + ex.Message); }
        }
        // Preflight every name before the first mutation.
        foreach (var name in Names)
        {
            var value = _registry.Read(name);
            var owned = state.Ifeo.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var prepared = owned is not null && value.Owner is null && value.Debugger == owned.Original && value.Kind == owned.OriginalKind;
            if (value.ForeignOwner || (owned is not null && !Owned(value, owned) && !prepared) ||
                (owned is null && value.Owner is not null) ||
                (owned is null && !string.IsNullOrEmpty(value.Debugger) && !CanAdoptLegacy(name, value)))
                return OperationResult.Fail("检测到外部或归属不明的 IFEO，未覆盖：" + name);
        }
        var added = new List<string>();
        try
        {
            foreach (var name in Names)
            {
                var current = _registry.Read(name);
                var record = state.Ifeo.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (record is null)
                {
                    var legacy = CanAdoptLegacy(name, current) ? Legacy(name) : null;
                    record = new GuardIfeoRecord { Name = name, Original = legacy is null ? current.Debugger : legacy.ValueKind == RegistryValueKind.None ? null : legacy.Data,
                        OriginalKind = legacy?.ValueKind ?? current.Kind };
                    record.Marker = OwnerPrefix + Guid.NewGuid().ToString("N") + (record.OriginalKind == RegistryValueKind.None ? "/absent" : "/backed");
                    state.Ifeo.Add(record);
                    _store.Save(state);
                    if (!Signature(current)) added.Add(name);
                    _registry.SetOwner(name, record.Marker);
                }
                else if (current.Owner is null && current.Debugger == record.Original && current.Kind == record.OriginalKind)
                {
                    _registry.SetOwner(name, record.Marker);
                    if (!Signature(current)) added.Add(name);
                }
                // Recheck after persistence; another administrator may have changed the entry.
                current = _registry.Read(name);
                if (!Owned(current, record) || (!Signature(current) && current.Debugger != record.Original))
                    throw new IOException("IFEO 写入前发生外部修改：" + name);
                _registry.SetDebugger(name, Debugger, RegistryValueKind.String);
                var actual = _registry.Read(name);
                if (!Owned(actual, record) || !Signature(actual)) throw new IOException("IFEO 写后复核失败：" + name);
            }
        }
        catch (Exception ex)
        {
            var errors = RestoreIfeo(state, added);
            return OperationResult.Fail("防护开启失败：" + ex.Message + (errors.Count == 0 ? "；本次新增拦截已回滚。" : "；回滚未完成：" + string.Join("；", errors)));
        }
        if (!string.IsNullOrWhiteSpace(file))
        {
            try { ApplyAcl(state, file); }
            catch (Exception ex)
            {
                var rollback = RestoreIfeo(state, added);
                return OperationResult.Fail("UE4 权限开启失败：" + ex.Message + "；权限恢复记录保留。" +
                    (rollback.Count == 0 ? "本次新增 IFEO 已回滚。" : "IFEO 回滚未完成：" + string.Join("；", rollback)));
            }
        }
        return OperationResult.Ok("IFEO 防护已开启，已找到的 UE4 权限写入通过复核。");
    }
    private List<string> RestoreIfeo(GuardProtectionState state, IEnumerable<string> names)
    {
        var errors = new List<string>();
        foreach (var name in names)
        {
            try
            {
                var current = _registry.Read(name);
                var record = state.Ifeo.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (record is null)
                {
                    if (current.ForeignOwner) continue;
                    if (current.Owner?.StartsWith(OwnerPrefix, StringComparison.Ordinal) == true && current.Owner.EndsWith("/absent", StringComparison.Ordinal))
                        record = new() { Name = name, Marker = current.Owner, OriginalKind = RegistryValueKind.None };
                    else if (CanAdoptLegacy(name, current))
                    {
                        var backup = Legacy(name)!;
                        record = new() { Name = name, Original = backup.ValueKind == RegistryValueKind.None ? null : backup.Data, OriginalKind = backup.ValueKind,
                            Marker = OwnerPrefix + Guid.NewGuid().ToString("N") + "/absent" };
                        state.Ifeo.Add(record);
                        _store.Save(state);
                        _registry.SetOwner(name, record.Marker);
                        current = _registry.Read(name);
                    }
                    else
                    {
                        if (Signature(current) || current.Owner?.StartsWith(OwnerPrefix, StringComparison.Ordinal) == true)
                            throw new IOException("缺少可靠归属/原值，未删除 IFEO：" + name);
                        continue; // Unrelated debuggers are not ours to remove.
                    }
                }
                if (current.Owner is null && !current.ForeignOwner && current.Debugger == record.Original && current.Kind == record.OriginalKind)
                {
                    state.Ifeo.RemoveAll(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    _store.Save(state);
                    continue; // Persistence succeeded but the registry write never happened.
                }
                if (!Owned(current, record)) throw new IOException("IFEO 属主已变化，未还原：" + name);
                if (current.Debugger == record.Original && current.Kind == record.OriginalKind)
                { /* Prepared write or already restored; only our metadata remains. */ }
                else if (Signature(current)) _registry.SetDebugger(name, record.Original, record.OriginalKind);
                else throw new IOException("IFEO 当前值已被外部修改，未覆盖：" + name);
                var restored = _registry.Read(name);
                if (restored.Debugger != record.Original || restored.Kind != record.OriginalKind) throw new IOException("IFEO 恢复复核失败：" + name);
                _registry.SetOwner(name, null);
                _registry.RemoveEmptyKey(name);
                state.Ifeo.RemoveAll(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                _store.Save(state);
                try { _legacy.RemoveStrict("HKLM", IfeoRoot + "\\" + name, "Debugger"); }
                catch (Exception ex) { Log.Warn("IFEO 已恢复，但旧备份清理失败，原文件保留：" + ex.Message); }
            }
            catch (Exception ex) { errors.Add(name + "：" + ex.Message); }
        }
        return errors;
    }
    internal OperationResult Disable(string? currentFile)
    {
        var state = _store.Load();
        var errors = RestoreIfeo(state, Names);
        foreach (var repair in state.LegacyAclRepairs.Where(r => !r.Completed))
        {
            try { CompleteLegacyRepair(state, repair); }
            catch (Exception ex) { errors.Add("旧 UE4 修复：" + ex.Message); }
        }
        foreach (var record in state.Acls.ToArray())
        {
            try
            {
                var current = _files.Read(record.Path);
                if (current.Identity != record.Identity) throw new IOException("原文件已被替换，拒绝对新文件恢复旧权限。");
                var dacl = NormalizeDacl(current.Dacl);
                if (dacl != NormalizeDacl(record.Original))
                {
                    if (dacl != NormalizeDacl(record.Applied)) throw new IOException("权限发生外部修改，未覆盖现有 DACL。");
                    _files.SetDacl(record.Path, record.Identity, record.Applied, record.Original);
                }
                if (NormalizeDacl(_files.Read(record.Path).Dacl) != NormalizeDacl(record.Original)) throw new IOException("DACL 恢复复核失败。");
                state.Acls.Remove(record);
                _store.Save(state);
            }
            catch (Exception ex) { errors.Add("UE4 权限：" + ex.Message); }
        }
        if (!string.IsNullOrWhiteSpace(currentFile) && !state.Acls.Any(a => a.Path.Equals(currentFile, StringComparison.OrdinalIgnoreCase)))
        {
            try { if (HasExecuteDeny(_files.Read(currentFile).Dacl)) errors.Add("UE4 存在未记录的执行拒绝，缺少原始权限备份，未盲目移除。"); }
            catch (Exception ex) { errors.Add("UE4 权限无法复核：" + ex.Message); }
        }
        return errors.Count == 0 ? OperationResult.Ok("本工具防护已恢复；第三方拦截不作修改。")
            : OperationResult.Fail("防护恢复未完成：" + string.Join("；", errors.Take(5)));
    }
    private void ApplyAcl(GuardProtectionState state, string path)
    {
        var current = _files.Read(path);
        if (!Path.GetFileName(current.Path).StartsWith("UE4PrereqSetup_", StringComparison.OrdinalIgnoreCase) || !current.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("拒绝修改非 UE4 前置包文件权限。");
        var existing = state.Acls.FirstOrDefault(a => a.Path.Equals(current.Path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (existing.Identity != current.Identity || NormalizeDacl(current.Dacl) != NormalizeDacl(existing.Applied))
                throw new IOException("已记录的 UE4 文件或权限发生变化，请先恢复。");
            return;
        }
        if (HasExecuteDeny(current.Dacl)) throw new IOException("已有执行拒绝，未接管第三方或旧版权限。");
        if (new RawSecurityDescriptor(current.Dacl).DiscretionaryAcl is null) throw new IOException("不修改空 DACL 文件。");
        var security = new FileSecurity();
        security.SetSecurityDescriptorSddlForm(current.Dacl, AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ExecuteFile, AccessControlType.Deny));
        var record = new GuardAclRecord { Path = current.Path, Identity = current.Identity, Original = current.Dacl,
            Applied = security.GetSecurityDescriptorSddlForm(AccessControlSections.Access) };
        state.Acls.Add(record);
        _store.Save(state); // Crash recovery record must exist before changing the file.
        _files.SetDacl(record.Path, record.Identity, record.Original, record.Applied);
        if (NormalizeDacl(_files.Read(record.Path).Dacl) != NormalizeDacl(record.Applied)) throw new IOException("UE4 权限写后复核失败。");
    }
    internal static string NormalizeDacl(string sddl) => new RawSecurityDescriptor(sddl).GetSddlForm(AccessControlSections.Access);
    internal bool CanRepairLegacyAcl(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var state = _store.Load();
        var file = _files.Read(path);
        if (state.Acls.Any(a => a.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase))) return false;
        var pending = state.LegacyAclRepairs.FirstOrDefault(r => !r.Completed && r.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
        return pending is not null
            ? pending.Identity == file.Identity && (NormalizeDacl(file.Dacl) == NormalizeDacl(pending.Before) || NormalizeDacl(file.Dacl) == NormalizeDacl(pending.After))
            : TryRemoveLegacyExecuteRule(file.Path, file.Dacl, out _);
    }

    internal OperationResult RepairLegacyAcl(string path)
    {
        try
        {
            var state = _store.Load();
            var file = _files.Read(path);
            if (state.Acls.Any(a => a.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase)))
                return OperationResult.Fail("该文件有正常防护恢复记录，请使用关闭防护，不接管旧权限。");
            var repair = state.LegacyAclRepairs.FirstOrDefault(r => !r.Completed && r.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
            if (repair is null)
            {
                if (!TryRemoveLegacyExecuteRule(file.Path, file.Dacl, out var after))
                    return OperationResult.Fail("不是可确认的单条旧 Everyone 拒绝执行规则，未修改权限。");
                repair = new() { Path = file.Path, Identity = file.Identity, Before = file.Dacl, After = after };
                state.LegacyAclRepairs.Add(repair);
                _store.Save(state);
            }
            CompleteLegacyRepair(state, repair);
            return OperationResult.Ok("旧 UE4 单条拒绝执行规则已移除；其余权限保留，修复前快照保存在机器级防护记录中。");
        }
        catch (Exception ex) { return OperationResult.Fail("旧 UE4 权限修复失败，记录保留供重试：" + ex.Message); }
    }

    private void CompleteLegacyRepair(GuardProtectionState state, GuardLegacyAclRepair repair)
    {
        var current = _files.Read(repair.Path);
        if (current.Identity != repair.Identity) throw new IOException("文件已替换，拒绝向新文件应用旧修复。");
        if (!TryRemoveLegacyExecuteRule(repair.Path, repair.Before, out var expected) || NormalizeDacl(expected) != NormalizeDacl(repair.After))
            throw new IOException("旧权限修复记录不符合最小修改规则。");
        var actual = NormalizeDacl(current.Dacl);
        if (actual != NormalizeDacl(repair.After))
        {
            if (actual != NormalizeDacl(repair.Before)) throw new IOException("权限已被外部修改，未覆盖。");
            _files.SetDacl(repair.Path, repair.Identity, repair.Before, repair.After);
        }
        if (NormalizeDacl(_files.Read(repair.Path).Dacl) != NormalizeDacl(repair.After)) throw new IOException("修复后权限复核失败。");
        repair.Completed = true;
        _store.Save(state);
        Log.Info("UE4 旧权限修复完成：Path=" + repair.Path + "；Identity=" + repair.Identity + "；仅移除显式 Everyone ExecuteFile 拒绝。");
    }

    internal static bool TryRemoveLegacyExecuteRule(string path, string sddl, out string after)
    {
        after = "";
        if (!new[] { "UE4PrereqSetup_x64.exe", "UE4PrereqSetup_x86.exe" }.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)) return false;
        var descriptor = new RawSecurityDescriptor(sddl);
        var acl = descriptor.DiscretionaryAcl;
        if (acl is null) return false;
        var indexes = Enumerable.Range(0, acl.Count).Where(i => acl[i] is CommonAce a && !a.IsCallback &&
            a.AceFlags == AceFlags.None && a.AceQualifier == AceQualifier.AccessDenied &&
            a.SecurityIdentifier.IsWellKnown(WellKnownSidType.WorldSid) && a.AccessMask == (int)FileSystemRights.ExecuteFile).ToArray();
        if (indexes.Length != 1) return false;
        acl.RemoveAce(indexes[0]);
        after = descriptor.GetSddlForm(AccessControlSections.Access);
        return !HasExecuteDeny(after) && acl.Count > 0;
    }
    internal static bool HasExecuteDeny(string sddl)
    {
        var dacl = new RawSecurityDescriptor(sddl).DiscretionaryAcl;
        return dacl is not null && dacl.OfType<CommonAce>().Any(a => a.AceQualifier == AceQualifier.AccessDenied &&
            a.SecurityIdentifier.IsWellKnown(WellKnownSidType.WorldSid) && (a.AccessMask & (int)FileSystemRights.ExecuteFile) != 0);
    }
}
