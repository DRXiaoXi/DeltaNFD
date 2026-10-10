using System.Diagnostics;
using System.Security.AccessControl;
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
                var arch = System.Text.RegularExpressions.Regex.IsMatch(displayName, @"\bx64\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "x64"
                    : displayName.Contains("(arm64)", StringComparison.OrdinalIgnoreCase) ? "arm64"
                    : "x86";
                var uninstall = sub.GetValue("UninstallString") as string ?? "";
                var quiet = sub.GetValue("QuietUninstallString") as string ?? "";

                var kind = RuntimeUninstallCommand.Classify(keyName, uninstall, quiet);

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
                ? $"检测到 {suboptimal.Count} 项运行库版本提醒（低于基线或安装记录缺失）；建议先修复缺失/过旧分支，不能据此认定 DLL 一定缺失。"
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

    // ---------------- 受控运行库修复（分支安装 / 高级全量重装） ----------------

    /// <summary>解包后的 AIO 清单和安装脚本 SHA256（锁定经过核验的完整安装内容）。</summary>
    private const string RepairPayloadManifestSha256 = "6C3CDC91281CFAF0A5BB23F511A5E4914BA7A6520CC84E73286693AE69DE7D0A";
    private const string RepairInstallerCmdSha256 = "2AA2A7EA0C0FE74044FC6B521B98A107127012E684105D8BC4762690AAF9F7F6";

    /// <summary>内置官方 2015-2022 运行库安装包（x64/x86）的 SHA256（防篡改校验）。</summary>
    private static readonly (string File, string Sha256)[] V14InstallerSha256 =
    [
        ("VCRedist2015-2022-x64.exe", "1821577409C35B2B9505AC833E246376CC68A8262972100444010B57226F0940"),
        ("VCRedist2015-2022-x86.exe", "DD1A8BE03398367745A87A5E35BEBDAB00FDAD080CF42AF0C3F20802D08C25D4"),
    ];

    public Task<OperationResult> RepairVcRedistAsync(IProgress<string>? progress = null,
        RuntimeRepairMode mode = RuntimeRepairMode.FullReinstall, CancellationToken cancellationToken = default, bool strongRepairMode = false) =>
        Task.Run(() => RuntimeGuardProtection.Exclusive(() =>
            RepairVcRedistCoreAsync(progress, mode, cancellationToken, strongRepairMode).GetAwaiter().GetResult()));

    private async Task<OperationResult> RepairVcRedistCoreAsync(IProgress<string>? progress,
        RuntimeRepairMode mode, CancellationToken token, bool strongRepairMode)
    {
        if (!ElevationHelper.IsElevated) return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        if (!Enum.IsDefined(mode)) return OperationResult.Fail("修复模式无效，未操作运行库。");
        if (mode != RuntimeRepairMode.FullReinstall) return OperationResult.Fail("运行库修复已恢复 Beta0.9 全量 AIO 机制；请在新版界面确认完整修复范围，未执行局部分支请求。");
        token.ThrowIfCancellationRequested();
        var blocker = _protection.RepairBlocker(await FindUe4PrereqAsync());
        if (blocker is not null) return OperationResult.Fail(blocker);
        RuntimeRepairDiagnostics diagnostics;
        try { diagnostics = new RuntimeRepairDiagnostics(Log.LogDirectory); }
        catch (Exception ex) { return OperationResult.Fail("无法创建诊断目录，未更改运行库：" + ex.Message); }
        Log.Info($"运行库修复：Beta0.9 完整 AIO 机制；诊断目录={diagnostics.DirectoryPath}");
        var environment = new RuntimeBetaRepairEnvironment(this, diagnostics);
        var repair = new RuntimeBetaRepair(environment, diagnostics);
        diagnostics.Save("repair-options.json", new { StrongRepairMode = strongRepairMode, Repair = "high-fps" });
        return strongRepairMode
            ? await new RuntimeStrongRepair(new RuntimeStrongRepairEnvironment(diagnostics), diagnostics)
                .RunAsync(environment.VerifyAsync, repair.RunAsync, progress, token)
            : await repair.RunAsync(progress, token);
    }

    internal static OperationResult VerifyRepairPackage(
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
                return OperationResult.Fail("未找到完整的 VCRedistRepair 安装目录、安装脚本或文件清单。");
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

    public Task<OperationResult> RepairCompatibilityAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default, bool strongRepairMode = false)
        => Task.Run(() => RuntimeGuardProtection.Exclusive(() => RepairCompatibilityCoreAsync(progress, cancellationToken, strongRepairMode).GetAwaiter().GetResult()));

    private async Task<OperationResult> RepairCompatibilityCoreAsync(IProgress<string>? progress, CancellationToken token, bool strongRepairMode)
    {
        if (!ElevationHelper.IsElevated) return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        token.ThrowIfCancellationRequested();
        var blocker = _protection.RepairBlocker(await FindUe4PrereqAsync());
        if (blocker is not null) return OperationResult.Fail(blocker);
        var diagnostics = new RuntimeRepairDiagnostics(Log.LogDirectory);
        var environment = new RuntimeCompatibilityEnvironment(diagnostics, strongRepairMode);
        var repair = new RuntimeCompatibilityRepair(environment, diagnostics);
        diagnostics.Save("repair-options.json", new { StrongRepairMode = strongRepairMode, Repair = "compatibility" });
        return strongRepairMode
            ? await new RuntimeStrongRepair(new RuntimeStrongRepairEnvironment(diagnostics), diagnostics)
                .RunAsync(environment.VerifyAsync, repair.RunAsync, progress, token)
            : await repair.RunAsync(progress, token);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private static async Task<OperationResult> UninstallVcRedistEntryAsync(VcRedistEntry entry, string? diagnosticDirectory = null)
    {
        var diagnostics = diagnosticDirectory is null ? new RuntimeRepairDiagnostics(Log.LogDirectory) : new RuntimeRepairDiagnostics(diagnosticDirectory);
        var environment = new RuntimeRepairEnvironment(new RuntimeGuardService(), diagnostics);
        var preflight = await environment.PreflightAsync(default);
        if (preflight.Blocker is { } blocker) return OperationResult.Fail(blocker);
        var result = await environment.ExecuteAsync(new RuntimeRepairStep("uninstall-one", entry.DisplayName, "", "uninstall", "", entry),
            TimeSpan.FromMinutes(3), null, default);
        return new OperationResult { Success = result.Success, IsSkipped = result.Skipped,
            OutcomeUnknown = result.OutcomeUnknown, RequiresReboot = result.RequiresReboot,
            Message = result.Success ? result.Skipped ? result.Details : "静默卸载及复核通过。"
                : RuntimeRepairErrorClassifier.Describe(result.ExitCode, result.Details) + "\n诊断目录：" + diagnostics.DirectoryPath };
    }

    [System.Runtime.InteropServices.DllImport("msi.dll", EntryPoint = "MsiQueryProductStateW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    internal static extern int QueryMsiProductState(string productCode);

    internal static bool IsUninstallEntryPresent(string keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName) || keyName.IndexOfAny(['\\', '/']) >= 0)
            throw new InvalidOperationException("卸载条目键名无效。");
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + keyName);
            if (key is not null) return true;
        }
        return false;
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

    public Task<OperationResult> UninstallV14Async(IProgress<string>? progress = null) =>
        Task.Run(() => RuntimeGuardProtection.Exclusive(() => UninstallV14CoreAsync(progress).GetAwaiter().GetResult()));

    private async Task<OperationResult> UninstallV14CoreAsync(IProgress<string>? progress)
    {
        Log.Info("运行库：单独卸载 v14 命名运行库（仅名字带 v14 的条目）");
        if (!ElevationHelper.IsElevated)
        {
            return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        }

        var blocker = _protection.RepairBlocker(await FindUe4PrereqAsync());
        if (blocker is not null) return OperationResult.Fail(blocker);

        var targets = await GetV14EntriesAsync();
        if (targets.Count == 0)
        {
            return OperationResult.Ok("未检测到名字带「v14」的运行库条目，无需卸载。");
        }

        var uninstalled = 0;
        var reboot = false;
        foreach (var entry in targets)
        {
            progress?.Report($"正在卸载：{entry.DisplayName}");
            var result = await UninstallVcRedistEntryAsync(entry);
            reboot |= result.RequiresReboot;
            if (result.Success)
            {
                if (!result.IsSkipped) uninstalled++;
                Log.Info($"运行库：已卸载 {entry.DisplayName}");
                if (result.RequiresReboot)
                    return new OperationResult { Success = false, RequiresReboot = true,
                        Message = $"已卸载 {uninstalled}/{targets.Count} 个 v14 条目；安装器要求重启，后续卸载已停止。" };
            }
            else
            {
                Log.Warn($"运行库：卸载 {entry.DisplayName} 失败 —— {result.Message}");
                return new OperationResult { Success = false, RequiresReboot = reboot, OutcomeUnknown = result.OutcomeUnknown,
                    Message = $"已卸载 {uninstalled}/{targets.Count} 个 v14 条目。后续卸载已停止：{entry.DisplayName}：{result.Message}" };
            }
        }

        progress?.Report("正在重新检测运行库状态…");
        return OperationResult.Ok(
            $"已单独卸载 {uninstalled} 个名字带「v14」的运行库条目（其它分支 / 年份命名条目未动）。"
            + "注意：Steam 启动游戏时可能自动重装 VC++ 运行库（Steamworks 共享运行库机制）；"
            + "游戏 / 启动器侧的重装可用「运行库防护模式」拦截。", requiresReboot: reboot);
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
