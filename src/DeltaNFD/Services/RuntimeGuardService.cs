using System.Diagnostics;
using System.Security.AccessControl;
using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>
/// 运行库保护的真实实现（IFEO 劫持 + UE4Prereq Deny-Execute ACL，全部可还原）。
/// 拦截范围 = 所有运行库安装器：VC++ 全系列（2005~2022）、DirectX 运行库、UE4/UE5 前置包等，
/// 无论来源是游戏、启动器还是手动安装，一律按文件名拦截。
/// </summary>
public sealed class RuntimeGuardService : IRuntimeGuardService
{
    private const string IfeoRoot = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string DebuggerValue = @"%windir%\System32\taskkill.exe";

    /// <summary>IFEO 拦截的运行库安装器名单（注册表键名不区分大小写，大小写变体无需重复建键）。</summary>
    private static readonly string[] IfeoInstallerNames =
    [
        // VC++ 2015-2022 统一安装器
        "vc_redist.x64.exe",
        "vc_redist.x86.exe",
        "vc_redist.arm64.exe",
        // VC++ 2005 / 2008 / 2010 / 2012 / 2013
        "vcredist_x64.exe",
        "vcredist_x86.exe",
        "vcredist_ia64.exe",
        "vcredist.exe",
        // DirectX 运行库
        "DXSETUP.exe",
        "dxwebsetup.exe",
        // UE4 / UE5 前置包（游戏自带运行库合集）
        "UE4PrereqSetup_x64.exe",
        "UE4PrereqSetup_x86.exe",
    ];

    /// <summary>UE4 前置包在游戏目录内的相对路径。</summary>
    private const string Ue4PrereqRelativePath = @"Engine\Extras\Redist\en-us";
    private readonly TweakBackupStore _backups;

    public RuntimeGuardService() : this(TweakBackupStore.Default)
    {
    }

    public RuntimeGuardService(TweakBackupStore backups)
    {
        _backups = backups;
    }

    // ---------------- 状态 ----------------

    public async Task<RuntimeGuardStatus> GetStatusAsync()
    {
        var redists = await Task.Run(GetInstalledRedists);
        var ue4Path = await FindUe4PrereqAsync();

        var ifeoApplied = CountIfeoApplied();
        var denied = ue4Path.Length > 0 && await IsExecuteDeniedAsync(ue4Path);

        return new RuntimeGuardStatus
        {
            InstalledRedists = redists,
            IfeoApplied = ifeoApplied == IfeoInstallerNames.Length,
            IfeoCount = ifeoApplied,
            IfeoTotal = IfeoInstallerNames.Length,
            Ue4PrereqFound = ue4Path.Length > 0,
            Ue4PrereqPath = ue4Path,
            Ue4PrereqDenied = denied,
        };
    }

    private static int CountIfeoApplied()
    {
        var ifeoApplied = 0;
        foreach (var name in IfeoInstallerNames)
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{IfeoRoot}\{name}");
            if (key?.GetValue("Debugger") as string == DebuggerValue)
            {
                ifeoApplied++;
            }
        }

        return ifeoApplied;
    }

    private static (string? Value, RegistryValueKind Kind) ReadDebugger(string keyPath)
    {
        using var key = Registry.LocalMachine.OpenSubKey(keyPath);
        if (key is null || !key.GetValueNames().Contains("Debugger", StringComparer.OrdinalIgnoreCase))
            return (null, RegistryValueKind.None);

        var kind = key.GetValueKind("Debugger");
        var value = key.GetValue("Debugger", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString) || value is not string text)
            throw new InvalidDataException($"IFEO Debugger 原值不是可安全备份的字符串：HKLM\\{keyPath}");
        return (text, kind);
    }

    private static RegistryKey? OpenIfeoForWrite(string name, bool createIfMissing)
    {
        var keyPath = $@"{IfeoRoot}\{name}";
        using var readKey = Registry.LocalMachine.OpenSubKey(keyPath);
        if (readKey is not null)
        {
            return Registry.LocalMachine.OpenSubKey(keyPath,
                RegistryKeyPermissionCheck.ReadWriteSubTree,
                RegistryRights.SetValue)
                ?? throw new UnauthorizedAccessException($"无权写入 IFEO 键：HKLM\\{keyPath}");
        }

        if (!createIfMissing)
            return null;

        using var parent = Registry.LocalMachine.OpenSubKey(IfeoRoot,
            RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.CreateSubKey);
        if (parent is null)
            throw new UnauthorizedAccessException($"无权在 IFEO 下创建子键：HKLM\\{IfeoRoot}");
        return parent.CreateSubKey(name, RegistryKeyPermissionCheck.ReadWriteSubTree)
            ?? throw new UnauthorizedAccessException($"无法创建 IFEO 键：HKLM\\{keyPath}");
    }

    private List<string> RestoreIfeoEntries(IEnumerable<string> names)
    {
        var problems = new List<string>();
        foreach (var name in names)
        {
            var keyPath = $@"{IfeoRoot}\{name}";
            var stage = "读取备份";
            try
            {
                var backup = _backups.GetStrict("HKLM", keyPath, "Debugger");
                if (backup is null)
                    continue;

                stage = "还原注册表";
                using (var key = OpenIfeoForWrite(name, backup.ValueKind != RegistryValueKind.None))
                {
                    if (backup.ValueKind == RegistryValueKind.None)
                        key?.DeleteValue("Debugger", throwOnMissingValue: false);
                    else
                        key!.SetValue("Debugger", backup.Data, backup.ValueKind);
                }

                stage = "清理备份";
                _backups.RemoveStrict("HKLM", keyPath, "Debugger");
            }
            catch (Exception ex)
            {
                Log.Error($"运行库防护：IFEO {name} {stage}失败", ex);
                problems.Add($"IFEO {name} {stage}：{DescribeError(ex)}");
            }
        }

        return problems;
    }

    private static string DescribeError(Exception ex) =>
        $"{ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}";

    private static string SummarizeProblems(IReadOnlyList<string> problems) =>
        string.Join("；", problems.Take(3)) +
        (problems.Count > 3 ? $"；另有 {problems.Count - 3} 项，详见日志" : "");

    private async Task<RuntimeGuardStatus?> PersistActualGuardStateAsync()
    {
        try
        {
            var status = await GetStatusAsync();
            AppSettingsStore.Update(s => s.RuntimeGuardEnabled = status.IfeoCount > 0 || status.Ue4PrereqDenied);
            return status;
        }
        catch (Exception ex)
        {
            Log.Error("运行库防护：复核实际状态失败", ex);
            return null;
        }
    }

    // ---------------- 开启 / 关闭 ----------------

    public async Task<OperationResult> EnableAsync()
    {
        Log.Info("运行库防护：开启（IFEO + ACL）");
        if (!ElevationHelper.IsElevated)
        {
            return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        }

        var problems = new List<string>();
        var writtenNames = new List<string>();

        // 1. IFEO 劫持（备份原 Debugger 值后写入 taskkill）
        foreach (var name in IfeoInstallerNames)
        {
            var stage = "读取原值";
            try
            {
                var keyPath = $@"{IfeoRoot}\{name}";
                var (current, kind) = ReadDebugger(keyPath);
                stage = "保存备份";
                _backups.SaveStrict(new RegistryValueBackup
                {
                    Hive = "HKLM",
                    Id = TweakBackupStore.MakeId("HKLM", keyPath, "Debugger"),
                    KeyPath = keyPath,
                    ValueName = "Debugger",
                    ValueKind = kind,
                    Data = current ?? "",
                    CreatedAt = DateTimeOffset.Now,
                });

                stage = "写入注册表";
                using var key = OpenIfeoForWrite(name, createIfMissing: true);
                key!.SetValue("Debugger", DebuggerValue, RegistryValueKind.String);
                writtenNames.Add(name);
            }
            catch (Exception ex)
            {
                Log.Error($"运行库防护：IFEO {name} {stage}失败", ex);
                problems.Add($"IFEO {name} {stage}：{DescribeError(ex)}");
                if (stage == "保存备份")
                    break;
            }
        }

        if (problems.Count == 0)
        {
            try
            {
                var count = CountIfeoApplied();
                if (count != IfeoInstallerNames.Length)
                    problems.Add($"IFEO 写入后复核仅生效 {count}/{IfeoInstallerNames.Length} 项");
            }
            catch (Exception ex)
            {
                Log.Error("运行库防护：IFEO 写入后复核失败", ex);
                problems.Add($"IFEO 复核：{DescribeError(ex)}");
            }
        }

        if (problems.Count > 0)
        {
            var rollbackProblems = RestoreIfeoEntries(writtenNames);
            var status = await PersistActualGuardStateAsync();
            var rollbackText = rollbackProblems.Count == 0 ? "本次已写入的 IFEO 项已回滚"
                : $"回滚仍有 {rollbackProblems.Count} 项失败：{SummarizeProblems(rollbackProblems)}";
            var stateText = status is null ? "无法复核最终状态" : $"当前 IFEO {status.IfeoCount}/{status.IfeoTotal}";
            return OperationResult.Fail($"拦截开启失败：{SummarizeProblems(problems)}；{rollbackText}；{stateText}。");
        }

        // 2. UE4Prereq 文件 ACL 是额外防护；失败不撤销已完整生效的 IFEO。
        var ue4Note = "未在游戏目录找到 UE4 前置包。";
        try
        {
            var ue4Path = await FindUe4PrereqAsync();
            if (ue4Path.Length > 0)
            {
                var acl = await SetExecuteDenyAsync(ue4Path, deny: true);
                ue4Note = acl.Success ? "UE4 前置包已拒绝执行。" : $"UE4 前置包未能单独拦截：{acl.Message}";
                if (!acl.Success) Log.Warn("运行库防护：" + ue4Note);
            }
        }
        catch (Exception ex)
        {
            Log.Error("运行库防护：UE4 前置包 ACL 处理失败", ex);
            ue4Note = $"UE4 前置包未能单独拦截：{DescribeError(ex)}";
        }

        var finalStatus = await PersistActualGuardStateAsync();
        if (finalStatus is null || !finalStatus.IfeoApplied)
            return OperationResult.Fail("IFEO 写入完成，但无法确认全部防护仍在生效；请查看日志并重新检测状态。");

        return OperationResult.Ok($"运行库拦截已开启：{IfeoInstallerNames.Length} 个安装器名已劫持。{ue4Note}");
    }

    public async Task<OperationResult> DisableAsync()
    {
        Log.Info("运行库防护：关闭（移除 IFEO + ACL）");
        if (!ElevationHelper.IsElevated)
        {
            return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        }

        var problems = RestoreIfeoEntries(IfeoInstallerNames);

        // 2. 还原 UE4Prereq ACL
        try
        {
            var ue4Path = await FindUe4PrereqAsync();
            if (ue4Path.Length > 0 && await IsExecuteDeniedAsync(ue4Path))
            {
                var acl = await SetExecuteDenyAsync(ue4Path, deny: false);
                if (!acl.Success)
                    problems.Add(acl.Message);
            }
        }
        catch (Exception ex)
        {
            Log.Error("运行库防护：UE4 前置包 ACL 还原失败", ex);
            problems.Add($"UE4 前置包 ACL 还原：{DescribeError(ex)}");
        }

        var finalStatus = await PersistActualGuardStateAsync();
        if (finalStatus is null)
            problems.Add("无法复核最终防护状态");
        else if (finalStatus.IfeoCount > 0 || finalStatus.Ue4PrereqDenied)
            problems.Add($"仍有拦截残留：IFEO {finalStatus.IfeoCount}/{finalStatus.IfeoTotal}" +
                (finalStatus.Ue4PrereqDenied ? "，UE4 前置包仍被拒绝执行" : ""));

        if (problems.Count > 0)
        {
            return OperationResult.Fail($"拦截关闭未完成：{SummarizeProblems(problems)}");
        }

        return OperationResult.Ok("运行库拦截已关闭，三角洲将可以正常安装运行库。");
    }

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
                    !displayName.Contains("Visual C++", StringComparison.OrdinalIgnoreCase) ||
                    !displayName.Contains("2015", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // VC++ x64 redist 由 32 位引导器写入 WOW6432Node，不能按 Hive 判断架构，以显示名为准
                var arch = displayName.Contains("(x64)", StringComparison.OrdinalIgnoreCase) ? "x64" : "x86";
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

    // ---------------- Visual C++ 运行库分类扫描 ----------------

    /// <summary>不完善运行库的显示名标记（v14 Redistributable，三角洲强制安装的版本）。</summary>
    private const string AbnormalRedistMarker = "Visual C++ v14";

    /// <summary>
    /// 各分支推荐（最适）版本：低于该版本或未安装 → 判定「运行库非最适版本」（黄色提醒）。
    /// v14 分支基准 = 作者实机的 2015-2022 运行库（14.42.34433，与内置官方安装包一致，2026-09 核对）；
    /// 其余分支与内置 AIO 修复包所装官方版本一致。
    /// </summary>
    private static readonly (string Branch, string Arch, string MinVersion)[] RecommendedRedists =
    [
        ("2005", "x64", "8.0.61186"),
        ("2005", "x86", "8.0.61187"),
        ("2008", "x64", "9.0.30729.7523"),
        ("2008", "x86", "9.0.30729.7523"),
        ("2010", "x64", "10.0.40219.473"),
        ("2010", "x86", "10.0.40219.473"),
        ("2012", "x64", "11.0.61135.400"),
        ("2012", "x86", "11.0.61135.400"),
        ("2013", "x64", "12.0.40664.0"),
        ("2013", "x86", "12.0.40664.0"),
        ("2015-2026", "x64", "14.42.34433.0"),
        ("2015-2026", "x86", "14.42.34433.0"),
    ];

    public Task<VcRedistScanReport> ScanVcRedistsAsync() => Task.Run(() =>
    {
        var abnormal = new List<VcRedistEntry>();
        var all = new List<VcRedistEntry>();

        var roots = (string[])
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        ];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rootPath in roots)
        {
            using var root = Registry.LocalMachine.OpenSubKey(rootPath);
            if (root is null)
            {
                continue;
            }

            foreach (var keyName in root.GetSubKeyNames())
            {
                if (!seen.Add(keyName))
                {
                    continue;
                }

                using var sub = root.OpenSubKey(keyName);
                if (sub?.GetValue("DisplayName") is not string displayName ||
                    !displayName.Contains("Visual C++", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var version = sub.GetValue("DisplayVersion") as string ?? "";
                var arch = displayName.Contains("(x64)", StringComparison.OrdinalIgnoreCase) ? "x64"
                    : displayName.Contains("(arm64)", StringComparison.OrdinalIgnoreCase) ? "arm64"
                    : "x86";
                var uninstall = sub.GetValue("UninstallString") as string ?? "";
                var quiet = sub.GetValue("QuietUninstallString") as string ?? "";

                // 分类以卸载命令内容为准：VC++ 2015-2026 系（Burn Bundle）的键名也是 {GUID}，
                // 若先按键名判 MSI 会对 Bundle 跑 msiexec /x {GUID}（1605 静默失败，实际没卸载）
                var kind = "Unknown";
                if (uninstall.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
                {
                    kind = "Msi";
                }
                else if (!string.IsNullOrWhiteSpace(quiet))
                {
                    kind = "Bundle";
                }
                else if (keyName.StartsWith('{') && keyName.EndsWith('}'))
                {
                    kind = "Msi"; // GUID 键且无静默卸载命令：按 MSI 兜底
                }
                else if (uninstall.Contains("advpack.dll", StringComparison.OrdinalIgnoreCase))
                {
                    kind = "Inf";
                }

                var entry = new VcRedistEntry
                {
                    DisplayName = displayName,
                    DisplayVersion = version,
                    Architecture = arch,
                    KeyName = keyName,
                    UninstallKind = kind,
                    UninstallString = uninstall,
                    QuietUninstallString = quiet,
                };

                all.Add(entry);
                if (displayName.Contains(AbnormalRedistMarker, StringComparison.OrdinalIgnoreCase))
                {
                    abnormal.Add(entry);
                }
            }
        }

        // 分支版本核对：低于推荐版本 / 未安装 → 非最适版本（黄色提醒）
        var suboptimal = new List<VcRedistSuboptimal>();
        foreach (var (branch, arch, minVersion) in RecommendedRedists)
        {
            VcRedistEntry? installed = null;
            foreach (var entry in all)
            {
                if (entry.Architecture == arch && ResolveRedistBranch(entry.DisplayName) == branch)
                {
                    // 同分支多条目（异常 v14 包除外）取版本最高的一条参与判定
                    if (installed is null || CompareVersions(entry.DisplayVersion, installed.DisplayVersion) > 0)
                    {
                        installed = entry;
                    }
                }
            }

            if (installed is null)
            {
                suboptimal.Add(new VcRedistSuboptimal
                {
                    Branch = $"{branch} ({arch})",
                    RecommendedVersion = minVersion,
                    InstalledVersion = "",
                });
            }
            else if (installed.DisplayVersion.Length == 0 || CompareVersions(installed.DisplayVersion, minVersion) < 0)
            {
                suboptimal.Add(new VcRedistSuboptimal
                {
                    Branch = $"{branch} ({arch})",
                    RecommendedVersion = minVersion,
                    InstalledVersion = installed.DisplayVersion,
                    DisplayName = installed.DisplayName,
                });
            }
        }

        // 2026 子判定（§24.1）：v14 基线达标但缺少「2026 Redistributable」命名条目时也提醒（黄色）。
        // 分支整体未安装/低于基线时分支行已覆盖，不重复提醒。修复运行库（AIO 内含官方 2026 版）可补齐。
        foreach (var arch in (string[])["x64", "x86"])
        {
            var branchOk = !suboptimal.Any(s => s.Branch == $"2015-2026 ({arch})");
            var has2026 = all.Any(e => e.Architecture == arch && IsRedist2026Named(e.DisplayName));
            if (branchOk && !has2026)
            {
                suboptimal.Add(new VcRedistSuboptimal
                {
                    Branch = $"2026 ({arch})",
                    RecommendedVersion = Redist2026RecommendedVersion,
                    InstalledVersion = "",
                });
            }
        }

        var advice = abnormal.Count > 0
            ? "检测到 v14 运行库（不完善版本，可能导致部分游戏/软件异常）——建议执行「修复运行库」。"
            : suboptimal.Count > 0
                ? $"检测到 {suboptimal.Count} 项运行库非最适版本（低于推荐版本或未安装）——建议执行「重装运行库」升级到官方最适全系列。"
                : "运行库均为最适版本。如需确保 C++ 库纯净，可执行「重装运行库」——将卸载全部现有 C++ 运行库后重装官方全系列。";

        return new VcRedistScanReport
        {
            Abnormal = abnormal,
            Suboptimal = suboptimal,
            All = all,
            Advice = advice,
        };
    });

    /// <summary>从显示名解析运行库分支；返回 null = 无法归入任何分支（含 v14 问题包）。</summary>
    private static string? ResolveRedistBranch(string displayName)
    {
        if (displayName.Contains(AbnormalRedistMarker, StringComparison.OrdinalIgnoreCase))
        {
            return null; // 三角洲强装的问题包：走红色（Abnormal）判定，不参与黄色判定
        }

        if (displayName.Contains("2005")) return "2005";
        if (displayName.Contains("2008")) return "2008";
        if (displayName.Contains("2010")) return "2010";
        if (displayName.Contains("2012")) return "2012";
        if (displayName.Contains("2013")) return "2013";

        // v14 统一分支：2015-2022 起随年份滚动改名（2015/2017/2019/2022/2026）
        foreach (var year in (string[])["2015", "2017", "2019", "2022", "2026"])
        {
            if (displayName.Contains(year))
            {
                return "2015-2026";
            }
        }

        return null;
    }

    /// <summary>「2026 Redistributable」命名条目的推荐版本（AIO 修复包所装官方 2026 版，修复运行库后自动补齐）。</summary>
    private const string Redist2026RecommendedVersion = "14.51.36247.0";

    /// <summary>
    /// 显示名是否为「2026 Redistributable」命名的官方 v14 条目（微软 2026 年起的产品命名，见 §20.6 取证）。
    /// v14 问题包与 MSI 子组件名同样含 "2026"，均按存在性判定即可（装了 2026 包就必有含 2026 的条目）。
    /// </summary>
    private static bool IsRedist2026Named(string displayName) =>
        !displayName.Contains(AbnormalRedistMarker, StringComparison.OrdinalIgnoreCase) &&
        displayName.Contains("Visual C++", StringComparison.OrdinalIgnoreCase) &&
        displayName.Contains("2026", StringComparison.Ordinal);

    /// <summary>点分版本号比较（"9.0.30729.7523" 风格；不可解析段按 0 处理）。</summary>
    private static int CompareVersions(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var va = i < pa.Length && int.TryParse(pa[i], out var x) ? x : 0;
            var vb = i < pb.Length && int.TryParse(pb[i], out var y) ? y : 0;
            if (va != vb)
            {
                return va.CompareTo(vb);
            }
        }

        return 0;
    }

    // ---------------- 修复/重装运行库（卸载全部 C++ → AIO 重装 → 官方 2015-2022 钉装） ----------------

    /// <summary>解包后的 AIO 清单和安装脚本 SHA256（锁定经过核验的完整安装内容）。</summary>
    private const string RepairPayloadManifestSha256 = "D9F991DD9E3C37A9968FD3558884B03F96CD85DBFF5226F26F05B3C31C3FCE94";
    private const string RepairInstallerCmdSha256 = "2C7FC692833C2B1263DDE90882C3D0E4C569B1F59666C48FDEE9B4FE77009901";

    /// <summary>内置官方 2015-2022 运行库安装包（x64/x86）的 SHA256（防篡改校验）。</summary>
    private static readonly (string File, string Sha256)[] V14InstallerSha256 =
    [
        ("VCRedist2015-2022-x64.exe", "1821577409C35B2B9505AC833E246376CC68A8262972100444010B57226F0940"),
        ("VCRedist2015-2022-x86.exe", "DD1A8BE03398367745A87A5E35BEBDAB00FDAD080CF42AF0C3F20802D08C25D4"),
    ];

    public async Task<OperationResult> RepairVcRedistAsync(IProgress<string>? progress = null)
    {
        Log.Info("运行库修复：开始（卸载全部 → AIO 重装）");
        if (!ElevationHelper.IsElevated)
        {
            return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        }

        var assetsRoot = Path.Combine(AppContext.BaseDirectory, "Assets");
        var repairPackageRoot = Path.Combine(assetsRoot, "VCRedistRepair_unpacked");
        progress?.Report("正在校验完整运行库安装包…");
        OperationResult packageCheck;
        try
        {
            packageCheck = await Task.Run(() => VerifyRepairPackage(assetsRoot, repairPackageRoot, progress));
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"运行库安装包校验失败，尚未更改现有运行库：{ex.Message}");
        }

        if (!packageCheck.Success)
        {
            Log.Error($"运行库修复：安装包校验失败，未开始卸载：{packageCheck.Message}");
            return OperationResult.Fail($"运行库安装包校验失败，尚未更改现有运行库：{packageCheck.Message}");
        }

        Log.Info(packageCheck.Message);

        // 1) 卸载系统上全部 Visual C++ 运行库条目（确保 C++ 库纯净）
        var scan = await ScanVcRedistsAsync();
        var all = scan.All;
        var uninstalled = 0;
        var failed = new List<string>();

        foreach (var entry in all)
        {
            progress?.Report($"正在卸载：{entry.DisplayName}");
            var result = await UninstallVcRedistEntryAsync(entry);
            if (result.Success)
            {
                uninstalled++;
            }
            else
            {
                failed.Add($"{entry.DisplayName}：{result.Message}");
            }
        }

        progress?.Report($"已卸载 {uninstalled}/{all.Count} 个运行库条目。");

        // 2) 使用解包后的完整 AIO 安装脚本；安装数据已在卸载前完成逐文件校验。
        var installerCmdPath = Path.Combine(repairPackageRoot, "Installer.cmd");
        progress?.Report("正在通过完整 AIO 安装脚本静默重装 2005–2026 全系列运行库（请勿关闭电脑）…");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = $"/d /s /c \"\"{installerCmdPath}\" /quiet\"",
                WorkingDirectory = repairPackageRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                return OperationResult.Fail("修复程序启动失败。");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(20));
            }
            catch (TimeoutException)
            {
                // 超时：尽力结束安装器，避免「报告失败但安装器仍在后台跑」导致的状态错乱/并发重装
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 提权进程可能杀不掉：如实告知用户
                }

                return OperationResult.Fail(
                    "修复包运行超过 20 分钟未结束，已停止等待并尝试结束安装器。" +
                    "请确认没有游戏/安装程序占用后重新执行修复。");
            }

            LogInstallerOutput("标准输出", await stdoutTask);
            LogInstallerOutput("错误输出", await stderrTask);

            var exitCode = process.ExitCode;
            if (exitCode is not (0 or 3010))
            {
                return OperationResult.Fail($"AIO 安装脚本退出码 {exitCode}——请关闭所有游戏/安装程序后重试。");
            }

            var rebootNeeded = exitCode == 3010;

            // 3) 官方 2015-2022 运行库（x64/x86）静默钉装，保证 v14 分支达到基线 14.42.34433
            var v14Notes = new List<string>();
            foreach (var (file, sha256) in V14InstallerSha256)
            {
                var label = file.EndsWith("x64.exe", StringComparison.OrdinalIgnoreCase) ? "2015-2022 (x64)" : "2015-2022 (x86)";
                var v14Path = Path.Combine(AppContext.BaseDirectory, "Assets", file);
                if (!File.Exists(v14Path))
                {
                    v14Notes.Add($"{label} 未找到内置安装包");
                    continue;
                }

                progress?.Report($"正在校验并安装官方运行库：{label}…");
                string hash;
                try
                {
                    using var stream = File.OpenRead(v14Path);
                    using var sha = System.Security.Cryptography.SHA256.Create();
                    hash = Convert.ToHexString(sha.ComputeHash(stream));
                }
                catch (Exception ex)
                {
                    v14Notes.Add($"{label} 校验失败（{ex.Message}）");
                    continue;
                }

                if (!hash.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                {
                    v14Notes.Add($"{label} SHA256 校验失败，已跳过安装");
                    continue;
                }

                var v14Psi = new ProcessStartInfo
                {
                    FileName = v14Path,
                    Arguments = "/install /quiet /norestart",
                    UseShellExecute = true,
                    Verb = "runas",
                };
                try
                {
                    using var v14Proc = Process.Start(v14Psi);
                    if (v14Proc is null)
                    {
                        v14Notes.Add($"{label} 启动失败");
                        continue;
                    }

                    try
                    {
                        await v14Proc.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(10));
                    }
                    catch (TimeoutException)
                    {
                        try
                        {
                            v14Proc.Kill(entireProcessTree: true);
                        }
                        catch
                        {
                            // 提权进程可能杀不掉
                        }

                        v14Notes.Add($"{label} 安装超时");
                        continue;
                    }

                    // 0=安装成功 1638=同版本或更高版本已存在 3010=需要重启
                    switch (v14Proc.ExitCode)
                    {
                        case 0:
                            v14Notes.Add($"{label} 已安装");
                            break;
                        case 1638:
                            v14Notes.Add($"{label} 已有同版本或更高版本");
                            break;
                        case 3010:
                            v14Notes.Add($"{label} 已安装（需重启）");
                            rebootNeeded = true;
                            break;
                        default:
                            v14Notes.Add($"{label} 退出码 {v14Proc.ExitCode}");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    v14Notes.Add($"{label} 安装异常（{ex.Message}）");
                }
            }

            var failNote = failed.Count > 0 ? $"（{failed.Count} 个旧条目卸载失败：{string.Join("；", failed.Take(2))}）" : "";
            var summary =
                $"运行库重装完成：已卸载 {uninstalled} 个旧条目{failNote}，重装 2005–2026 全系列官方运行库；" +
                $"2015-2022 基线（14.42.34433）——{string.Join("；", v14Notes)}。";
            return rebootNeeded
                ? OperationResult.Ok(summary + "部分组件需要重启电脑后完全生效。", requiresReboot: true)
                : OperationResult.Ok(summary);
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"修复运行库失败：{ex.Message}");
        }
    }

    private static OperationResult VerifyRepairPackage(
        string assetsRoot,
        string packageRoot,
        IProgress<string>? progress)
    {
        try
        {
            var manifestPath = Path.Combine(packageRoot, "payload_manifest_sha256.csv");
            var installerPath = Path.Combine(packageRoot, "Installer.cmd");
            if (!Directory.Exists(packageRoot) || !File.Exists(manifestPath) || !File.Exists(installerPath))
            {
                return OperationResult.Fail("未找到完整的 VCRedistRepair_unpacked 安装目录、安装脚本或文件清单。");
            }

            var manifestHash = ComputeSha256(manifestPath);
            if (!manifestHash.Equals(RepairPayloadManifestSha256, StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult.Fail("运行库文件清单 SHA256 不匹配，安装包可能已损坏或被修改。");
            }

            var installerHash = ComputeSha256(installerPath);
            if (!installerHash.Equals(RepairInstallerCmdSha256, StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult.Fail("AIO 安装脚本 SHA256 不匹配，安装包可能已损坏或被修改。");
            }

            // 先核对将要在卸载旧运行库后使用的官方 v14 安装包，缺失或损坏时提前中止。
            foreach (var (file, expectedHash) in V14InstallerSha256)
            {
                var path = Path.Combine(assetsRoot, file);
                if (!File.Exists(path))
                {
                    return OperationResult.Fail($"缺少官方运行库安装包：{file}");
                }

                if (!ComputeSha256(path).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    return OperationResult.Fail($"官方运行库安装包 SHA256 不匹配：{file}");
                }
            }

            var lines = File.ReadAllLines(manifestPath);
            if (lines.Length < 2 || !string.Equals(lines[0], "relative_path,size_bytes,sha256", StringComparison.Ordinal))
            {
                return OperationResult.Fail("运行库文件清单格式无效。");
            }

            var entries = lines.Skip(1).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
            if (entries.Length != 791)
            {
                return OperationResult.Fail($"运行库文件清单条目数异常：预期 791，实际 {entries.Length}。");
            }

            var fullRoot = Path.GetFullPath(packageRoot);
            var rootPrefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            for (var index = 0; index < entries.Length; index++)
            {
                var line = entries[index];
                var firstComma = line.IndexOf(',');
                var lastComma = line.LastIndexOf(',');
                if (firstComma <= 0 || lastComma <= firstComma || lastComma == line.Length - 1)
                {
                    return OperationResult.Fail($"运行库文件清单第 {index + 1} 项格式无效。");
                }

                var relativePath = line[..firstComma]
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar);
                var sizeText = line[(firstComma + 1)..lastComma];
                var expectedHash = line[(lastComma + 1)..];
                if (Path.IsPathRooted(relativePath)
                    || !long.TryParse(sizeText, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var expectedSize))
                {
                    return OperationResult.Fail($"运行库文件清单第 {index + 1} 项路径或大小无效。");
                }

                var filePath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
                if (!filePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return OperationResult.Fail($"运行库文件清单路径越界：{relativePath}");
                }

                if (!File.Exists(filePath))
                {
                    return OperationResult.Fail($"运行库安装内容缺失：{relativePath}");
                }

                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length != expectedSize)
                {
                    return OperationResult.Fail($"运行库安装内容大小不匹配：{relativePath}");
                }

                if (!ComputeSha256(filePath).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    return OperationResult.Fail($"运行库安装内容 SHA256 不匹配：{relativePath}");
                }

                if ((index + 1) % 100 == 0 || index + 1 == entries.Length)
                {
                    progress?.Report($"正在校验运行库安装内容 {index + 1}/{entries.Length}…");
                }
            }

            return OperationResult.Ok($"已通过 SHA256 校验：AIO 安装脚本及 {entries.Length} 个清单文件，官方 x86/x64 v14 安装包也有效。");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"无法读取或校验运行库安装内容：{ex.Message}");
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private static void LogInstallerOutput(string streamName, string output)
    {
        var text = output.Trim();
        if (text.Length == 0)
        {
            return;
        }

        const int maxCharacters = 4000;
        if (text.Length > maxCharacters)
        {
            text = text[^maxCharacters..];
        }

        Log.Info($"运行库 AIO 安装器{streamName}：{text}");
    }

    private static async Task<OperationResult> UninstallVcRedistEntryAsync(VcRedistEntry entry)
    {
        try
        {
            switch (entry.UninstallKind)
            {
                case "Msi":
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "msiexec.exe",
                        Arguments = $"/x {entry.KeyName} /qn /norestart",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using var p = Process.Start(psi);
                    if (p is null)
                    {
                        return OperationResult.Fail("msiexec 启动失败");
                    }

                    await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
                    return p.ExitCode is 0 or 3010 or 1605
                        ? OperationResult.Ok("已卸载。")
                        : OperationResult.Fail($"msiexec 退出码 {p.ExitCode}");
                }
                case "Inf":
                case "Bundle":
                {
                    var command = entry.UninstallKind == "Bundle" && !string.IsNullOrWhiteSpace(entry.QuietUninstallString)
                        ? entry.QuietUninstallString
                        : entry.UninstallString;
                    if (string.IsNullOrWhiteSpace(command))
                    {
                        return OperationResult.Fail("无卸载命令");
                    }

                    var psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/d /c " + command,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using var p2 = Process.Start(psi);
                    if (p2 is null)
                    {
                        return OperationResult.Fail("卸载命令启动失败");
                    }

                    await p2.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
                    return OperationResult.Ok("已卸载。");
                }
                default:
                    return OperationResult.Fail("未知卸载机制");
            }
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }

    // ---------------- V14 单独卸载 ----------------

    /// <summary>卸载目标判定：显示名包含「Visual C++ v14」的条目（可卸载机制 Bundle/INF）。
    /// 年份命名的条目（2015/2017/2019/2022/2026 Redistributable 等）一律不算——即使同属 v14 分支也不动。</summary>
    private static bool IsV14BranchEntry(VcRedistEntry entry) =>
        entry.UninstallKind is "Bundle" or "Inf" &&
        entry.DisplayName.Contains(AbnormalRedistMarker, StringComparison.OrdinalIgnoreCase);

    public async Task<List<VcRedistEntry>> GetV14EntriesAsync()
    {
        var scan = await ScanVcRedistsAsync();
        return scan.All.Where(IsV14BranchEntry)
            .OrderBy(e => e.Architecture)
            .ThenBy(e => e.DisplayName)
            .ToList();
    }

    /// <summary>IFEO 拦截是否处于生效状态（任一安装器名已劫持即视为生效——VC_redist 卸载器同受拦截）。</summary>
    private static bool IsIfeoGuardActive()
    {
        foreach (var name in IfeoInstallerNames)
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{IfeoRoot}\{name}");
            if (key?.GetValue("Debugger") as string == DebuggerValue)
            {
                return true;
            }
        }

        return false;
    }

    public async Task<OperationResult> UninstallV14Async(IProgress<string>? progress = null)
    {
        Log.Info("运行库：单独卸载 v14 命名运行库（仅名字带 v14 的条目）");
        if (!ElevationHelper.IsElevated)
        {
            return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        }

        // IFEO 拦截按文件名劫持 VC_redist.x64.exe 等——卸载器自身同受拦截，必须先关防护
        if (IsIfeoGuardActive())
        {
            return OperationResult.Fail(
                "「运行库防护模式」当前开启——VC_redist 卸载器自身也会被 IFEO 拦截。请先关闭防护，卸载完成后再重新开启。");
        }

        var targets = await GetV14EntriesAsync();
        if (targets.Count == 0)
        {
            return OperationResult.Ok("未检测到名字带「v14」的运行库条目，无需卸载。");
        }

        var uninstalled = 0;
        var failed = new List<string>();
        foreach (var entry in targets)
        {
            progress?.Report($"正在卸载：{entry.DisplayName}");
            var result = await UninstallVcRedistEntryAsync(entry);
            if (result.Success)
            {
                uninstalled++;
                Log.Info($"运行库：已卸载 {entry.DisplayName}");
            }
            else
            {
                failed.Add($"{entry.DisplayName}：{result.Message}");
                Log.Warn($"运行库：卸载 {entry.DisplayName} 失败 —— {result.Message}");
            }
        }

        progress?.Report("正在重新检测运行库状态…");
        if (failed.Count > 0)
        {
            return OperationResult.Fail(
                $"已卸载 {uninstalled}/{targets.Count} 个 v14 条目。失败：{string.Join("；", failed)}");
        }

        return OperationResult.Ok(
            $"已单独卸载 {uninstalled} 个名字带「v14」的运行库条目（其它分支 / 年份命名条目未动）。"
            + "注意：Steam 启动游戏时可能自动重装 VC++ 运行库（Steamworks 共享运行库机制）；"
            + "游戏 / 启动器侧的重装可用「运行库防护模式」拦截。");
    }

    // ---------------- 三角洲 UE4 前置包定位 ----------------

    private static Task<string> FindUe4PrereqAsync() => DeltaForceLocator.FindUe4PrereqAsync();

    // ---------------- ACL ----------------

    private const string EveryoneSid = "*S-1-1-0";

    private static async Task<OperationResult> SetExecuteDenyAsync(string filePath, bool deny)
    {
        var arguments = deny
            ? $"/deny \"{EveryoneSid}:(X)\""
            : $"/remove:d \"{EveryoneSid}\"";

        var (code, stdout, stderr) = await RunCaptureAsync(
            "icacls.exe", $"\"{filePath}\" {arguments}", TimeSpan.FromSeconds(20));
        if (code != 0)
        {
            var reason = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            return OperationResult.Fail(
                $"icacls 操作失败（{FirstLine(reason)}）——{filePath}");
        }

        return OperationResult.Ok(deny ? "已拒绝执行" : "已恢复执行权限");
    }

    private static async Task<bool> IsExecuteDeniedAsync(string filePath)
    {
        var (code, stdout, _) = await RunCaptureAsync("icacls.exe", $"\"{filePath}\"", TimeSpan.FromSeconds(20));
        return code == 0 && stdout.Contains("(DENY)", StringComparison.OrdinalIgnoreCase) &&
               stdout.Contains("(X)", StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- 注册表 / 进程工具 ----------------

    private static async Task<(int Code, string StdOut, string StdErr)> RunCaptureAsync(
        string fileName, string arguments, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            return (-1, "", $"无法启动 {fileName}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 进程可能已自行退出
            }

            return (-1, "", "执行超时");
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string FirstLine(string text)
    {
        var trimmed = text.Trim();
        var lineBreak = trimmed.IndexOfAny(['\r', '\n']);
        return lineBreak > 0 ? trimmed[..lineBreak] : trimmed;
    }
}
