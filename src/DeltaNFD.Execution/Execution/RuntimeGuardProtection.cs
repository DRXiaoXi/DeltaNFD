using Microsoft.Win32;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DeltaNFD.Services;

internal sealed record GuardRegistryValue(string? Debugger, RegistryValueKind Kind, string? Owner, bool ForeignOwner)
{
    public string? ForeignMarker { get; init; }
}
internal sealed record GuardFileValue(string Path, string Identity, string Dacl);
internal interface IGuardRegistry
{
    GuardRegistryValue Read(string name);
    void SetDebugger(string name, string? value, RegistryValueKind kind);
    void SetOwner(string name, string? value);
    void RemoveEmptyKey(string name);
    /// <summary>删除独立防护工具（RuntimeGuardHotPatch）的属主标记值，不论其内容。</summary>
    void ClearForeignOwner(string name);
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
    public RuntimeGuardMode? Mode { get; set; }
    public List<GuardLegacyIfeoRepair> LegacyIfeoRepairs { get; set; } = [];
}
internal sealed class GuardLegacyIfeoRepair
{
    public string Name { get; set; } = "";
    public GuardRegistryValue Before { get; set; } = new(null, RegistryValueKind.None, null, false);
    public bool Completed { get; set; }
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
internal sealed record GuardProtectionStatus(int Blocked, int Managed, int External, bool FileDenied, bool PendingAcl)
{
    public RuntimeGuardMode Mode { get; init; }
    public int ScopeManaged { get; init; }
    public int ScopeBlocked { get; init; }
    /// <summary>独立防护工具写入的 taskkill 拦截：普通关闭不删除，只能强制清理。</summary>
    public IReadOnlyList<RuntimeGuardLegacyEntry> LegacyIfeo { get; init; } = [];
    public bool PendingIfeo { get; init; }
    public string Issue { get; init; } = "";
}

/// <summary>
/// 运行库防护：按目标状态收敛，逐项处理、逐项报告，不因单项或记录不一致整体失败。
/// 11 个安装器名上的 taskkill Debugger 一律视为运行库拦截（本工具各版本与独立防护工具写法相同）；
/// 其他程序的 Debugger 普通开关不动，只有强制清理在留存快照后移除。
/// UE4 前置包已在 IFEO 名单内按文件名拦截，不再写文件权限；旧版本留下的 Everyone 拒绝执行在关闭/清理时剥离。
/// </summary>
internal sealed class RuntimeGuardProtection
{
    internal const string IfeoRoot = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    internal const string Debugger = @"%windir%\System32\taskkill.exe";
    internal const string OwnerName = "DeltaNFD_RuntimeGuard_Owner";
    internal const string OwnerPrefix = "DeltaNFD.RuntimeGuard/1/";
    internal const string ForeignOwnerName = "RuntimeGuardHotPatch_Owner";
    internal static readonly string[] Names = ["vc_redist.x64.exe", "vc_redist.x86.exe", "vc_redist.arm64.exe",
        "vcredist_x64.exe", "vcredist_x86.exe", "vcredist_ia64.exe", "vcredist.exe", "DXSETUP.exe", "dxwebsetup.exe",
        "UE4PrereqSetup_x64.exe", "UE4PrereqSetup_x86.exe"];
    private const int FileExecute = 0x20;
    // SYNCHRONIZE | READ_CONTROL | FILE_READ_ATTRIBUTES：FILE_GENERIC_EXECUTE 去掉执行位后的剩余部分。
    private const int ExecuteResidue = 0x100000 | 0x20000 | 0x80;
    private const int MaxCleanupSnapshots = 66;
    private readonly IGuardRegistry _registry;
    private readonly IGuardFiles _files;
    private readonly IGuardStateStore _store;
    private readonly TweakBackupStore _legacy;
    /// <summary>强制清理后等待多久再复读，用来发现仍在运行、会重新写入的防护工具。</summary>
    internal TimeSpan RewriteProbeDelay { get; set; } = TimeSpan.FromSeconds(1);
    internal Action<TimeSpan> Wait { get; set; } = Thread.Sleep;

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

    internal static string[] Scope(RuntimeGuardMode mode) => mode switch
    {
        RuntimeGuardMode.BasicV14 => Names.Take(3).ToArray(),
        RuntimeGuardMode.Full => Names,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
    private static RuntimeGuardMode EffectiveMode(GuardProtectionState state) => state.Mode ??
        (state.Ifeo.Count > 0 || state.Acls.Count > 0 ? RuntimeGuardMode.Full : RuntimeGuardMode.BasicV14);

    /// <summary>Debugger 是否指向 taskkill（容忍环境变量展开、引号、大小写和附加参数）。</summary>
    internal static bool IsGuardDebugger(string? debugger)
    {
        if (string.IsNullOrWhiteSpace(debugger)) return false;
        var text = debugger.Trim();
        string image;
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            image = close > 0 ? text[1..close] : text[1..];
        }
        else
        {
            var space = text.IndexOf(' ');
            image = space > 0 ? text[..space] : text;
        }
        string file;
        try { file = Path.GetFileName(Environment.ExpandEnvironmentVariables(image).Trim()); }
        catch (ArgumentException) { return false; }
        return file.Equals("taskkill.exe", StringComparison.OrdinalIgnoreCase) || file.Equals("taskkill", StringComparison.OrdinalIgnoreCase);
    }
    private static bool IsGuard(GuardRegistryValue value) =>
        value.Kind is RegistryValueKind.String or RegistryValueKind.ExpandString && IsGuardDebugger(value.Debugger);
    private static bool HasDebugger(GuardRegistryValue value) => !string.IsNullOrWhiteSpace(value.Debugger);
    /// <summary>本工具任何版本写入的拦截（含旧版无属主、Beta0.9 属主记录丢失的情况）。</summary>
    private static bool IsOurs(GuardRegistryValue value) => IsGuard(value) && !value.ForeignOwner;
    private static bool IsToolOwned(GuardRegistryValue value) => IsGuard(value) && value.ForeignOwner;
    private static string Fingerprint(string name, GuardRegistryValue value) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(new { Name = name.ToLowerInvariant(), Value = value }))));

    private (GuardProtectionState State, string? Issue) LoadState()
    {
        try { return (_store.Load(), null); }
        catch (Exception ex)
        {
            Log.Warn("运行库防护状态文件无法读取，按空记录继续：" + ex.Message);
            return (new GuardProtectionState(), ex.Message);
        }
    }
    private static string? TrySave(IGuardStateStore store, GuardProtectionState state)
    {
        try { store.Save(state); return null; }
        catch (Exception ex) { Log.Warn("运行库防护状态保存失败：" + ex.Message); return ex.Message; }
    }

    internal GuardProtectionStatus Probe(string? currentFile)
    {
        var (state, issue) = LoadState();
        var mode = EffectiveMode(state);
        var scope = Scope(mode);
        int blocked = 0, managed = 0, external = 0, scopeManaged = 0, scopeBlocked = 0;
        var markerResidue = false;
        var toolOwned = new List<RuntimeGuardLegacyEntry>();
        foreach (var name in Names)
        {
            var value = _registry.Read(name);
            if (!HasDebugger(value))
            {
                markerResidue |= value.Owner is not null;
                continue;
            }
            blocked++;
            var inScope = scope.Contains(name);
            if (inScope) scopeBlocked++;
            if (IsOurs(value)) { managed++; if (inScope) scopeManaged++; }
            else external++;
            if (IsToolOwned(value)) toolOwned.Add(new(name, Fingerprint(name, value)));
        }
        var notes = new List<string>();
        if (issue is not null) notes.Add("防护状态文件无法读取：" + issue);
        var denied = false;
        foreach (var path in AclPaths(state, currentFile))
        {
            try { denied |= HasExplicitExecuteDeny(_files.Read(path).Dacl); }
            catch (Exception ex) when (IsMissing(ex)) { }
            catch (Exception ex) { notes.Add("UE4 前置包权限无法读取：" + ex.Message); }
        }
        return new(blocked, managed, external, denied, state.Acls.Count > 0 || state.LegacyAclRepairs.Any(r => !r.Completed))
        {
            Mode = mode, ScopeManaged = scopeManaged, ScopeBlocked = scopeBlocked, LegacyIfeo = toolOwned,
            PendingIfeo = markerResidue || state.LegacyIfeoRepairs.Any(r => !r.Completed),
            Issue = string.Join("；", notes),
        };
    }

    internal OperationResult SelectMode(RuntimeGuardMode mode, string? file)
    {
        _ = Scope(mode);
        if (Probe(file).Managed > 0) return OperationResult.Fail("防护开启中，请先关闭防护再选择防护范围。");
        var (state, _) = LoadState();
        state.Mode = mode;
        var error = TrySave(_store, state);
        return error is null ? OperationResult.Ok("防护范围已保存，尚未开启防护。")
            : OperationResult.Fail("防护范围保存失败：" + error + "。可先执行「强制清理拦截」重置防护记录。");
    }

    /// <summary>修复/卸载运行库前的阻塞检查：只看会劫持安装器的 IFEO，UE4 文件权限与修复无关。</summary>
    internal string? RepairBlocker(string? currentFile)
    {
        try
        {
            var status = Probe(currentFile);
            if (status.Managed > 0)
                return "运行库防护仍在开启，安装器会被拦截。请先关闭防护再修复运行库；尚未卸载任何运行库。";
            if (status.Blocked > 0)
                return $"有 {status.Blocked} 个运行库安装器被其他程序设置了 IFEO 拦截。请使用「强制清理拦截」后再修复；尚未卸载任何运行库。";
            return null;
        }
        catch (Exception ex) { return "无法读取安装器拦截状态，拒绝修复，尚未卸载任何运行库：" + ex.Message; }
    }

    internal OperationResult Enable(string? file, RuntimeGuardMode mode = RuntimeGuardMode.Full)
    {
        var names = Scope(mode);
        var (state, _) = LoadState();
        var notes = new List<string>();
        if (state.Mode != mode)
        {
            state.Mode = mode;
            if (TrySave(_store, state) is { } error) notes.Add("防护范围未能保存（" + error + "）");
        }
        foreach (var name in Names.Except(names, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var value = _registry.Read(name);
                if (IsOurs(value) || (!HasDebugger(value) && value.Owner is not null)) RemoveBlock(name, value, state, clearForeignOwner: false);
            }
            catch (Exception ex) { notes.Add(name + "（范围外旧拦截未移除）：" + ex.Message); }
        }
        var applied = 0;
        var skipped = new List<string>();
        foreach (var name in names)
        {
            try
            {
                var value = _registry.Read(name);
                if (HasDebugger(value) && !IsGuard(value)) { skipped.Add(name + "（已有其他程序的 Debugger，未覆盖）"); continue; }
                if (!value.ForeignOwner && value.Owner?.StartsWith(OwnerPrefix, StringComparison.Ordinal) != true)
                    _registry.SetOwner(name, OwnerPrefix + Guid.NewGuid().ToString("N") + "/absent");
                if (!IsGuard(value)) _registry.SetDebugger(name, Debugger, RegistryValueKind.String);
                if (!IsGuard(_registry.Read(name))) throw new IOException("写后复核未生效");
                applied++;
            }
            catch (Exception ex) { skipped.Add(name + "（" + ex.Message + "）"); }
        }
        var label = mode == RuntimeGuardMode.BasicV14 ? "基础 V14 防护" : "完全防护";
        var noteText = notes.Count == 0 ? "" : "；" + string.Join("；", notes);
        if (applied == names.Length)
            return OperationResult.Ok($"{label}已开启：{applied} 项安装器按文件名拦截" +
                (mode == RuntimeGuardMode.Full ? "（含 UE4 前置包）" : "") + "。" + noteText);
        if (applied == 0)
            return OperationResult.Fail($"{label}未能开启：" + string.Join("；", skipped) + noteText);
        return OperationResult.Fail($"{label}已部分开启（{applied}/{names.Length}），已生效的项保留。未生效：" +
            string.Join("；", skipped) + noteText + "。如需移除其他程序的拦截，可使用「强制清理拦截」后重新开启。");
    }

    internal OperationResult Disable(string? currentFile) => Disable(currentFile is null ? [] : [currentFile]);

    internal OperationResult Disable(IReadOnlyList<string> currentFiles)
    {
        var (state, issue) = LoadState();
        var errors = new List<string>();
        var kept = new List<string>();
        var toolOwned = new List<string>();
        foreach (var name in Names)
        {
            try
            {
                var value = _registry.Read(name);
                if (IsToolOwned(value))
                {
                    toolOwned.Add(name);
                    if (value.Owner is not null) _registry.SetOwner(name, null);
                    continue;
                }
                if (IsOurs(value) || value.Owner is not null) RemoveBlock(name, value, state, clearForeignOwner: false);
                else if (HasDebugger(value)) kept.Add(name);
            }
            catch (Exception ex) { errors.Add(name + "：" + ex.Message); }
        }
        var aclNotes = StripAclResidue(state, null, errors);
        foreach (var file in currentFiles)
            aclNotes.AddRange(StripAclResidue(state, file, errors));
        if (errors.Count == 0)
        {
            ForgetRecords(state, Names, clearAcls: true);
            if (issue is null && TrySave(_store, state) is { } error)
                aclNotes.Add("防护记录未能更新（" + error + "），不影响拦截已移除");
        }
        CleanupRoamingBackups(Names.Except(kept.Concat(toolOwned), StringComparer.OrdinalIgnoreCase));

        var notes = new List<string>(aclNotes);
        if (toolOwned.Count > 0) notes.Add($"另有 {toolOwned.Count} 项由独立防护工具写入的拦截未删除（{string.Join("、", toolOwned)}），如需移除请使用「强制清理拦截」");
        if (kept.Count > 0) notes.Add($"其他程序的 Debugger 未改动：{string.Join("、", kept)}");
        if (issue is not null) notes.Add("防护状态文件无法读取，已保留原文件；如需重置请使用「强制清理拦截」");
        var tail = notes.Count == 0 ? "" : "。" + string.Join("；", notes) + "。";
        return errors.Count == 0
            ? OperationResult.Ok("运行库防护已关闭，本工具（含旧版本）的拦截均已移除" + (tail.Length == 0 ? "。" : tail))
            : OperationResult.Fail("防护关闭未完成，以下项目写入失败：" + string.Join("；", errors.Take(6)) + tail +
                (tail.Length == 0 ? "。" : "") + "已处理的项目不会回滚，可直接重试或使用「强制清理拦截」。");
    }

    internal OperationResult ForceCleanup(IEnumerable<string>? names, string? currentFile) =>
        ForceCleanup(names, currentFile is null ? [] : [currentFile]);

    internal OperationResult ForceCleanup(IEnumerable<string>? names, IReadOnlyList<string> currentFiles)
    {
        var targets = (names ?? []).Where(n => Names.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Select(n => Names.First(x => x.Equals(n, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (targets.Length == 0) targets = Names;
        var (state, issue) = LoadState();
        var errors = new List<string>();
        var removed = new List<string>();
        var cleared = new List<string>();
        foreach (var name in targets)
        {
            try
            {
                var value = _registry.Read(name);
                if (!HasDebugger(value) && value.Owner is null && !value.ForeignOwner) continue;
                Log.Info($"强制清理拦截·快照：{name}；Debugger={value.Debugger ?? "（无）"}；类型={value.Kind}；属主={value.Owner ?? "（无）"}；独立工具标记={value.ForeignMarker ?? (value.ForeignOwner ? "（非字符串）" : "（无）")}");
                if (HasDebugger(value))
                    state.LegacyIfeoRepairs.Add(new GuardLegacyIfeoRepair { Name = name, Before = value, Completed = true });
                RemoveBlock(name, value, state, clearForeignOwner: true, removeAnyDebugger: true);
                if (HasDebugger(value))
                {
                    cleared.Add(name);
                    removed.Add(IsGuard(value) ? name : name + "（其他程序：" + value.Debugger + "）");
                }
            }
            catch (Exception ex) { errors.Add(name + "：" + ex.Message); }
        }
        var aclNotes = StripAclResidue(state, null, errors);
        foreach (var file in currentFiles)
            aclNotes.AddRange(StripAclResidue(state, file, errors));

        var rewritten = new List<string>();
        if (cleared.Count > 0)
        {
            Wait(RewriteProbeDelay);
            foreach (var name in cleared)
            {
                try { if (HasDebugger(_registry.Read(name))) rewritten.Add(name); }
                catch (Exception ex) { errors.Add(name + "（复核）：" + ex.Message); }
            }
        }

        var allTargets = targets.Length == Names.Length;
        ForgetRecords(state, targets, clearAcls: allTargets && errors.Count == 0);
        if (state.LegacyIfeoRepairs.Count > MaxCleanupSnapshots)
            state.LegacyIfeoRepairs.RemoveRange(0, state.LegacyIfeoRepairs.Count - MaxCleanupSnapshots);
        if (TrySave(_store, state) is { } saveError)
            aclNotes.Add("防护记录未能重置（" + saveError + "），拦截清理结果不受影响，快照已写入日志");
        else if (issue is not null)
            aclNotes.Add("无法读取的旧防护记录已重置");
        CleanupRoamingBackups(targets.Except(rewritten, StringComparer.OrdinalIgnoreCase));

        var notes = new List<string>(aclNotes);
        if (removed.Count > 0) notes.Add("已移除：" + string.Join("、", removed));
        var tail = notes.Count == 0 ? "" : "\n" + string.Join("；", notes) + "。";
        if (rewritten.Count > 0)
            return OperationResult.Fail($"清理后 {rewritten.Count} 项拦截又被重新写入（{string.Join("、", rewritten)}），说明仍有防护程序在后台运行。" +
                "请先退出独立运行库防护工具或其他安全软件的相关功能，再执行一次强制清理。" + tail);
        if (errors.Count > 0)
            return OperationResult.Fail("强制清理未全部完成：" + string.Join("；", errors.Take(6)) + "。已清理的项目保留结果，可直接重试。" + tail);
        return OperationResult.Ok((removed.Count == 0 && aclNotes.Count == 0 ? "未发现残留拦截，" : "") +
            "运行库安装器拦截已全部清理，可以正常安装或修复运行库。" + tail);
    }

    private void RemoveBlock(string name, GuardRegistryValue value, GuardProtectionState state, bool clearForeignOwner, bool removeAnyDebugger = false)
    {
        var expectGuard = false;
        if (IsGuard(value) || (removeAnyDebugger && HasDebugger(value)))
        {
            var record = state.Ifeo.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (!removeAnyDebugger && record?.Original is { Length: > 0 } original && !IsGuardDebugger(original) &&
                record.OriginalKind is RegistryValueKind.String or RegistryValueKind.ExpandString)
                _registry.SetDebugger(name, original, record.OriginalKind);
            else
                _registry.SetDebugger(name, null, RegistryValueKind.None);
        }
        else expectGuard = IsGuard(value);
        if (value.Owner is not null) _registry.SetOwner(name, null);
        if (clearForeignOwner && value.ForeignOwner) _registry.ClearForeignOwner(name);
        var after = _registry.Read(name);
        if (IsGuard(after) != expectGuard || (removeAnyDebugger && HasDebugger(after)))
            throw new IOException("移除后复读仍存在拦截，可能有程序在重新写入");
        if (after.Owner is not null || (clearForeignOwner && after.ForeignOwner))
            throw new IOException("属主标记移除后复读仍存在");
        _registry.RemoveEmptyKey(name);
    }

    private static void ForgetRecords(GuardProtectionState state, IReadOnlyCollection<string> names, bool clearAcls)
    {
        state.Ifeo.RemoveAll(r => names.Contains(r.Name, StringComparer.OrdinalIgnoreCase));
        foreach (var repair in state.LegacyIfeoRepairs.Where(r => names.Contains(r.Name, StringComparer.OrdinalIgnoreCase)))
            repair.Completed = true;
        if (!clearAcls) return;
        state.Acls.Clear();
        foreach (var repair in state.LegacyAclRepairs) repair.Completed = true;
    }

    private void CleanupRoamingBackups(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            try { _legacy.RemoveStrict("HKLM", IfeoRoot + "\\" + name, "Debugger"); }
            catch (Exception ex) { Log.Warn("旧版 IFEO 备份清理失败（不影响拦截状态）：" + name + "；" + ex.Message); }
        }
    }

    private static IEnumerable<string> AclPaths(GuardProtectionState state, string? currentFile)
    {
        var paths = state.Acls.Select(a => a.Path).Concat(state.LegacyAclRepairs.Where(r => !r.Completed).Select(r => r.Path)).ToList();
        if (!string.IsNullOrWhiteSpace(currentFile)) paths.Add(currentFile);
        return paths.Where(IsUe4Prereq).Distinct(StringComparer.OrdinalIgnoreCase);
    }
    private static bool IsUe4Prereq(string path)
    {
        var file = Path.GetFileName(path);
        return file.StartsWith("UE4PrereqSetup_", StringComparison.OrdinalIgnoreCase) && file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }
    private static bool IsMissing(Exception ex) => ex is FileNotFoundException or DirectoryNotFoundException ||
        ex is System.ComponentModel.Win32Exception { NativeErrorCode: 2 or 3 };

    /// <summary>剥离所有记录路径及当前 UE4 前置包上的显式 Everyone 拒绝执行；文件不存在视为已完成。返回说明，失败写入 errors。</summary>
    private List<string> StripAclResidue(GuardProtectionState state, string? currentFile, List<string> errors)
    {
        var notes = new List<string>();
        foreach (var path in AclPaths(state, currentFile))
        {
            try
            {
                switch (StripAcl(path))
                {
                    case AclOutcome.Stripped: notes.Add("已移除 UE4 前置包的旧版拒绝执行权限"); break;
                    case AclOutcome.InheritedOnly: notes.Add("UE4 前置包的拒绝执行继承自上级目录，非本工具写入，未改动"); break;
                }
            }
            catch (Exception ex) { errors.Add("UE4 前置包权限：" + ex.Message); }
        }
        return notes;
    }

    private enum AclOutcome { Missing, Clean, Stripped, InheritedOnly }
    private AclOutcome StripAcl(string path)
    {
        GuardFileValue current;
        try { current = _files.Read(path); }
        catch (Exception ex) when (IsMissing(ex)) { return AclOutcome.Missing; }
        if (!IsUe4Prereq(current.Path)) throw new IOException("拒绝修改非 UE4 前置包文件权限。");
        for (var attempt = 0; ; attempt++)
        {
            if (!StripExecuteDeny(current.Dacl, out var after))
                return HasExecuteDeny(current.Dacl) ? AclOutcome.InheritedOnly : AclOutcome.Clean;
            try { _files.SetDacl(current.Path, current.Identity, current.Dacl, after); }
            catch (IOException) when (attempt == 0) { current = _files.Read(path); continue; }
            if (HasExplicitExecuteDeny(_files.Read(path).Dacl)) throw new IOException("拒绝执行权限移除后复读仍存在。");
            Log.Info("UE4 前置包旧版拒绝执行已剥离：" + current.Path);
            return AclOutcome.Stripped;
        }
    }

    internal bool CanRepairLegacyAcl(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !IsUe4Prereq(path)) return false;
        try { return HasExplicitExecuteDeny(_files.Read(path).Dacl); }
        catch { return false; }
    }

    internal OperationResult RepairLegacyAcl(string path)
    {
        try
        {
            if (!IsUe4Prereq(path)) return OperationResult.Fail("拒绝修改非 UE4 前置包文件权限。");
            return StripAcl(path) switch
            {
                AclOutcome.Stripped => OperationResult.Ok("已移除 UE4 前置包上的 Everyone 拒绝执行规则，其余权限保留。"),
                AclOutcome.InheritedOnly => OperationResult.Fail("拒绝执行继承自上级目录，非本工具写入，未改动。"),
                AclOutcome.Missing => OperationResult.Ok("UE4 前置包已不存在，无需修复。"),
                _ => OperationResult.Ok("UE4 前置包没有拒绝执行规则，无需修复。"),
            };
        }
        catch (Exception ex) { return OperationResult.Fail("UE4 前置包权限修复失败：" + ex.Message); }
    }

    internal static string NormalizeDacl(string sddl) => new RawSecurityDescriptor(sddl).GetSddlForm(AccessControlSections.Access);

    /// <summary>移除显式（非继承）Everyone 拒绝 ACE 中的执行位；只剩执行附带位时整条删除。</summary>
    internal static bool StripExecuteDeny(string sddl, out string after)
    {
        var descriptor = new RawSecurityDescriptor(sddl);
        var acl = descriptor.DiscretionaryAcl;
        after = sddl;
        if (acl is null) return false;
        var changed = false;
        for (var i = acl.Count - 1; i >= 0; i--)
        {
            if (acl[i] is not CommonAce ace || ace.AceQualifier != AceQualifier.AccessDenied ||
                (ace.AceFlags & AceFlags.Inherited) != 0 || !ace.SecurityIdentifier.IsWellKnown(WellKnownSidType.WorldSid) ||
                (ace.AccessMask & FileExecute) == 0)
                continue;
            var rest = ace.AccessMask & ~FileExecute;
            acl.RemoveAce(i);
            if ((rest & ~ExecuteResidue) != 0)
                acl.InsertAce(i, new CommonAce(ace.AceFlags, AceQualifier.AccessDenied, rest, ace.SecurityIdentifier, ace.IsCallback, ace.GetOpaque()));
            changed = true;
        }
        if (changed) after = descriptor.GetSddlForm(AccessControlSections.Access);
        return changed;
    }

    internal static bool HasExplicitExecuteDeny(string sddl) => ExecuteDenies(sddl).Any(a => (a.AceFlags & AceFlags.Inherited) == 0);
    internal static bool HasExecuteDeny(string sddl) => ExecuteDenies(sddl).Any();
    private static IEnumerable<CommonAce> ExecuteDenies(string sddl)
    {
        var dacl = new RawSecurityDescriptor(sddl).DiscretionaryAcl;
        return dacl is null ? [] : dacl.OfType<CommonAce>().Where(a => a.AceQualifier == AceQualifier.AccessDenied &&
            a.SecurityIdentifier.IsWellKnown(WellKnownSidType.WorldSid) && (a.AccessMask & FileExecute) != 0).ToList();
    }
}
