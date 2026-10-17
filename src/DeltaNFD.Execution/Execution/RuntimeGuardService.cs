using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>
/// 运行库保护：基础 V14 三项 IFEO，完全防护十一项 IFEO（含 UE4 前置包，按文件名拦截）。
/// 开关按目标状态逐项收敛；强制清理不依赖记录，兼容本工具各版本及独立防护工具写入的拦截。
/// 无论来源是游戏、启动器还是手动安装，IFEO 一律按文件名全系统拦截。
/// </summary>
public sealed class RuntimeGuardService : IRuntimeGuardService
{
    private readonly RuntimeGuardProtection _protection;
    public RuntimeGuardService() : this(TweakBackupStore.Default) { }
    public RuntimeGuardService(TweakBackupStore backups) => _protection = new RuntimeGuardProtection(backups);
    internal RuntimeGuardService(RuntimeGuardProtection protection) => _protection = protection;


    public async Task<RuntimeGuardStatus> GetStatusAsync()
    {
        var redists = await Task.Run(GetInstalledRedists);
        var paths = await FindUe4PrereqPathsAsync();
        var path = paths.FirstOrDefault() ?? "";
        var state = _protection.Probe(path);
        return new RuntimeGuardStatus
        {
            InstalledRedists = redists, IfeoCount = state.Blocked, IfeoTotal = RuntimeGuardProtection.Scope(state.Mode).Length,
            IfeoApplied = state.ScopeManaged == RuntimeGuardProtection.Scope(state.Mode).Length,
            IfeoManagedCount = state.Managed, IfeoExternalCount = state.External,
            Mode = state.Mode, ScopeManagedCount = state.ScopeManaged, ScopeBlockedCount = state.ScopeBlocked,
            LegacyIfeo = state.LegacyIfeo, IfeoRestorePending = state.PendingIfeo,
            Ue4PrereqFound = path.Length > 0, Ue4PrereqPath = path,
            Ue4PrereqDenied = state.FileDenied, Ue4RestorePending = state.PendingAcl,
            CanRepairLegacyUe4Acl = _protection.CanRepairLegacyAcl(path),
            GuardIssue = state.Issue,
        };
    }
    public Task<OperationResult> EnableAsync() => Task.Run(() => RuntimeGuardProtection.Exclusive(() =>
    {
        if (!ElevationHelper.IsElevated) return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        var busy = RuntimeInstallationReview.InstallerBusy();
        var pending = new RuntimeInstallationReview(AppDataPaths.Root).InspectPending(busy, out var ended);
        if (busy || pending is not null || ended) return OperationResult.Fail("安装事务仍运行或结果待核对，暂不能开启安装器拦截。" + pending);
        var result = _protection.Enable(null, _protection.Probe(null).Mode);
        PersistGuardState("");
        Log.Info("运行库防护开启结果：" + result.Message);
        return result;
    }));

    public Task<OperationResult> SelectModeAsync(RuntimeGuardMode mode) => Task.Run(() => RuntimeGuardProtection.Exclusive(() =>
    {
        if (!ElevationHelper.IsElevated) return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        return _protection.SelectMode(mode, null);
    }));

    public Task<OperationResult> CleanupLegacyIfeoAsync(IReadOnlyList<RuntimeGuardLegacyEntry> confirmedEntries)
    {
        var names = confirmedEntries.Select(e => e.Name).ToArray();
        return Task.Run(() => RuntimeGuardProtection.Exclusive(() =>
        {
            if (!ElevationHelper.IsElevated) return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
            var paths = FindUe4PrereqPathsOrEmpty();
            var result = _protection.ForceCleanup(names, paths);
            PersistGuardState(paths.FirstOrDefault() ?? "");
            Log.Info("强制清理拦截结果：" + result.Message);
            return result;
        }));
    }
    public Task<OperationResult> DisableAsync() => Task.Run(() => RuntimeGuardProtection.Exclusive(() =>
    {
        if (!ElevationHelper.IsElevated) return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        var paths = FindUe4PrereqPathsOrEmpty();
        var result = _protection.Disable(paths);
        PersistGuardState(paths.FirstOrDefault() ?? "");
        Log.Info("运行库防护恢复结果：" + result.Message);
        return result;
    }));

    private void PersistGuardState(string path)
    {
        try
        {
            var state = _protection.Probe(path);
            AppSettingsStore.Update(s => s.RuntimeGuardEnabled = state.Managed > 0 || state.FileDenied || state.PendingAcl || state.PendingIfeo);
        }
        catch (Exception ex) { Log.Error("运行库防护状态无法复核", ex); }
    }

    public Task<OperationResult> RepairLegacyUe4AclAsync(string expectedPath) => Task.Run(() => RuntimeGuardProtection.Exclusive(() =>
    {
        if (!ElevationHelper.IsElevated) return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        var path = FindUe4PrereqAsync().GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(path) || !Path.GetFullPath(path).Equals(Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
            return OperationResult.Fail("游戏前置包路径已变化，未修改权限，请重新确认。");
        var result = _protection.RepairLegacyAcl(path);
        PersistGuardState(path);
        Log.Info("旧 UE4 权限修复结果：" + result.Message);
        return result;
    }));

    // ---------------- 已装运行库枚举 ----------------

    private static List<VcRedistInfo> GetInstalledRedists()
    {
        var result = new List<VcRedistInfo>();
        var roots = (ValueTuple<string, string>[])
        [
            ("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            ("HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        ];

        foreach (var (hive, rootPath) in roots)
        {
            using var root = Registry.LocalMachine.OpenSubKey(rootPath);
            if (root is null)
            {
                continue;
            }

            foreach (var subName in root.GetSubKeyNames())
            {
                using var sub = root.OpenSubKey(subName);
                if (sub?.GetValue("DisplayName") is not string displayName ||
                    !displayName.Contains("Visual C++", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // VC++ x64 redist 由 32 位引导器写入 WOW6432Node，不能按 Hive 判断架构，以显示名为准
                var arch = System.Text.RegularExpressions.Regex.IsMatch(displayName, @"\bx64\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "x64"
                    : displayName.Contains("arm64", StringComparison.OrdinalIgnoreCase) ? "arm64" : "x86";
                if (result.Any(r => r.DisplayName == displayName))
                {
                    continue;
                }

                result.Add(new VcRedistInfo
                {
                    DisplayName = displayName,
                    Version = sub.GetValue("DisplayVersion") as string ?? "未知",
                    Architecture = arch,
                });
            }
        }

        return result.OrderBy(r => r.Architecture).ThenBy(r => r.DisplayName).ToList();
    }

    // ---------------- 三角洲 UE4 前置包定位 ----------------

    private static Task<string> FindUe4PrereqAsync() => DeltaForceLocator.FindUe4PrereqAsync();

    private static Task<IReadOnlyList<string>> FindUe4PrereqPathsAsync() => DeltaForceLocator.FindUe4PrereqPathsAsync();

    private static string FindUe4PrereqOrEmpty()
    {
        try { return FindUe4PrereqAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { Log.Warn("运行库防护：定位 UE4 前置包失败，仅处理 IFEO 与已记录路径：" + ex.Message); return ""; }
    }

    private static IReadOnlyList<string> FindUe4PrereqPathsOrEmpty()
    {
        try { return FindUe4PrereqPathsAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { Log.Warn("运行库防护：定位 UE4 前置包失败，仅处理 IFEO 与已记录路径：" + ex.Message); return []; }
    }

}
