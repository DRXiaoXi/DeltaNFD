using System.Diagnostics;
using System.Security.AccessControl;
using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>
/// 运行库保护：IFEO 与 UE4 ACL 写前备份、写后复核，外部修改或旧版权限缺少备份时拒绝盲目还原。
/// 拦截范围 = 所有运行库安装器：VC++ 全系列（2005~2022）、DirectX 运行库、UE4/UE5 前置包等，
/// 无论来源是游戏、启动器还是手动安装，一律按文件名拦截。
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
        var path = await FindUe4PrereqAsync();
        var state = _protection.Probe(path);
        return new RuntimeGuardStatus
        {
            InstalledRedists = redists, IfeoCount = state.Blocked, IfeoTotal = RuntimeGuardProtection.Names.Length,
            IfeoApplied = state.Managed == RuntimeGuardProtection.Names.Length && state.External == 0,
            IfeoManagedCount = state.Managed, IfeoExternalCount = state.External,
            Ue4PrereqFound = path.Length > 0 || state.PendingAcl, Ue4PrereqPath = path,
            Ue4PrereqDenied = state.FileDenied, Ue4RestorePending = state.PendingAcl,
        };
    }
    public Task<OperationResult> EnableAsync() => Task.Run(() => RuntimeGuardProtection.Exclusive(() =>
    {
        if (!ElevationHelper.IsElevated) return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        var path = FindUe4PrereqAsync().GetAwaiter().GetResult();
        var result = _protection.Enable(path);
        PersistGuardState(path);
        Log.Info("运行库防护开启结果：" + result.Message);
        return result;
    }));
    public Task<OperationResult> DisableAsync() => Task.Run(() => RuntimeGuardProtection.Exclusive(() =>
    {
        if (!ElevationHelper.IsElevated) return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        var path = FindUe4PrereqAsync().GetAwaiter().GetResult();
        var result = _protection.Disable(path);
        PersistGuardState(path);
        Log.Info("运行库防护恢复结果：" + result.Message);
        return result;
    }));

    private void PersistGuardState(string path)
    {
        try
        {
            var state = _protection.Probe(path);
            AppSettingsStore.Update(s => s.RuntimeGuardEnabled = state.Managed > 0 || state.FileDenied || state.PendingAcl);
        }
        catch (Exception ex) { Log.Error("运行库防护状态无法复核", ex); }
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
                var arch = System.Text.RegularExpressions.Regex.IsMatch(displayName, @"\bx64\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "x64"
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
    private const string RepairPayloadManifestSha256 = "6C3CDC91281CFAF0A5BB23F511A5E4914BA7A6520CC84E73286693AE69DE7D0A";
    private const string RepairInstallerCmdSha256 = "2AA2A7EA0C0FE74044FC6B521B98A107127012E684105D8BC4762690AAF9F7F6";

    /// <summary>内置官方 2015-2022 运行库安装包（x64/x86）的 SHA256（防篡改校验）。</summary>
    private static readonly (string File, string Sha256)[] V14InstallerSha256 =
    [
        ("VCRedist2015-2022-x64.exe", "1821577409C35B2B9505AC833E246376CC68A8262972100444010B57226F0940"),
        ("VCRedist2015-2022-x86.exe", "DD1A8BE03398367745A87A5E35BEBDAB00FDAD080CF42AF0C3F20802D08C25D4"),
    ];

    public Task<OperationResult> RepairVcRedistAsync(IProgress<string>? progress = null) =>
        Task.Run(() =>
        {
            try { return RuntimeGuardProtection.Exclusive(() => RepairVcRedistCoreAsync(progress).GetAwaiter().GetResult()); }
            catch (Exception ex)
            {
                Log.Error("运行库修复：流程未正常完成", ex);
                return OperationResult.Fail("运行库修复未完成：" + ex.Message + "。请查看日志，不要立即重复卸载重装。");
            }
        });

    private async Task<OperationResult> RepairVcRedistCoreAsync(IProgress<string>? progress)
    {
        var blocker = _protection.RepairBlocker(await FindUe4PrereqAsync());
        if (blocker is not null)
        {
            Log.Warn("运行库修复：预检拒绝，尚未卸载运行库；" + blocker);
            return OperationResult.Fail(blocker);
        }
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
            Log.Error("运行库修复：安装内容校验异常，尚未卸载运行库", ex);
            return OperationResult.Fail($"运行库安装包校验失败，尚未更改现有运行库：{ex.Message}");
        }

        if (!packageCheck.Success)
        {
            Log.Error($"运行库修复：安装包校验失败，未开始卸载：{packageCheck.Message}");
            return OperationResult.Fail($"运行库安装包校验失败，尚未更改现有运行库：{packageCheck.Message}");
        }

        Log.Info(packageCheck.Message);

        RuntimeRepairDiagnostics diagnostics;
        try { diagnostics = new RuntimeRepairDiagnostics(Log.LogDirectory); }
        catch (Exception ex)
        {
            Log.Error("运行库修复：诊断目录创建失败，尚未卸载运行库", ex);
            return OperationResult.Fail($"无法创建修复诊断目录，尚未更改运行库：{ex.Message}");
        }
        Log.Info($"运行库修复：诊断目录={diagnostics.DirectoryPath}；开始UTC={diagnostics.StartedUtc:O}");
        var installationIssues = new List<string>();

        // 1) 卸载系统上全部 Visual C++ 运行库条目（确保 C++ 库纯净）
        var scan = await ScanVcRedistsAsync();
        diagnostics.Save("before-scan.json", scan);
        Log.Info($"运行库修复：卸载前条目={scan.All.Count}；异常={scan.Abnormal.Count}；非最适={scan.Suboptimal.Count}");
        var all = scan.All;
        var uninstalled = 0;
        var failed = new List<string>();
        var uninstallRebootNeeded = false;

        foreach (var entry in all)
        {
            progress?.Report($"正在卸载：{entry.DisplayName}");
            Log.Info($"运行库修复·卸载开始：名称={entry.DisplayName}；架构={entry.Architecture}；版本={entry.DisplayVersion}；类型={entry.UninstallKind}；键={entry.KeyName}");
            var result = await UninstallVcRedistEntryAsync(entry, diagnostics.DirectoryPath);
            uninstallRebootNeeded |= result.RequiresReboot;
            Log.Info($"运行库修复·卸载结果：名称={entry.DisplayName}；成功={result.Success}；需重启={result.RequiresReboot}；说明={result.Message}");
            if (!result.Success && result.Message.StartsWith("卸载超时", StringComparison.Ordinal))
            {
                diagnostics.Save("uninstall-timeout.json", new { Entry = entry, Result = result, Uninstalled = uninstalled });
                return new OperationResult { Success = false, RequiresReboot = uninstallRebootNeeded,
                    Message = $"{entry.DisplayName}：{result.Message}；停止后续卸载及重装，已有部分旧条目可能被卸载。诊断目录：{diagnostics.DirectoryPath}" };
            }
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
        diagnostics.Save("uninstall-failures.json", failed);
        installationIssues.AddRange(failed.Select(f => "旧条目卸载未确认成功：" + f));

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
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            psi.Environment["DELTANFD_REPAIR_LOG_DIR"] = diagnostics.DirectoryPath;
            Log.Info($"运行库修复·AIO 启动：程序={psi.FileName}；参数={psi.Arguments}；工作目录={psi.WorkingDirectory}；超时=20分钟");
            using var process = Process.Start(psi);
            if (process is null)
            {
                return OperationResult.Fail("修复程序启动失败；旧运行库可能已卸载。诊断目录：" + diagnostics.DirectoryPath);
            }

            using var outputCancellation = new CancellationTokenSource();
            var stdoutTask = diagnostics.CaptureOutputAsync(process.StandardOutput, "aio-stdout.log", outputCancellation.Token);
            var stderrTask = diagnostics.CaptureOutputAsync(process.StandardError, "aio-stderr.log", outputCancellation.Token);

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
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    Log.Warn("运行库修复：超时后未确认安装器已结束，禁止立即重复修复。");
                }

                outputCancellation.Cancel();
                await Task.WhenAll(stdoutTask, stderrTask);
                diagnostics.CollectInstallerEvents();
                diagnostics.Save("aio-timeout.json", new { Pid = process.Id, TimeUtc = DateTime.UtcNow, Uninstalled = uninstalled });
                Log.Error($"运行库修复：AIO 超时；PID={process.Id}；诊断目录={diagnostics.DirectoryPath}");
                return OperationResult.Fail(
                    "修复包运行超过 20 分钟未结束，已停止等待并尝试结束安装器。" +
                    "旧运行库可能已卸载，当前修复未完成；请确认安装器已结束，不要立即重复修复。诊断目录：" + diagnostics.DirectoryPath);
            }

            outputCancellation.CancelAfter(TimeSpan.FromSeconds(5));
            await Task.WhenAll(stdoutTask, stderrTask);

            var exitCode = process.ExitCode;
            Log.Info($"运行库修复·AIO 结束：PID={process.Id}；退出码={exitCode}；需重启={exitCode == 3010}");
            var packages = new List<RuntimePackageResult>();
            try { packages = diagnostics.ReadPackageResults(); }
            catch (Exception ex) { installationIssues.Add("逐包结果未确认：" + ex.Message); Log.Error("运行库修复：逐包结果读取失败", ex); }

            var rebootNeeded = exitCode == 3010 || uninstallRebootNeeded;

            // 3) 官方 2015-2022 运行库（x64/x86）静默钉装，保证 v14 分支达到基线 14.42.34433
            var v14Notes = new List<string>();
            foreach (var (file, sha256) in V14InstallerSha256)
            {
                var label = file.EndsWith("x64.exe", StringComparison.OrdinalIgnoreCase) ? "2015-2022 (x64)" : "2015-2022 (x86)";
                var v14Path = Path.Combine(AppContext.BaseDirectory, "Assets", file);
                if (!File.Exists(v14Path))
                {
                    v14Notes.Add($"{label} 未找到内置安装包");
                    installationIssues.Add(v14Notes[^1]);
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
                    installationIssues.Add(v14Notes[^1]);
                    continue;
                }

                if (!hash.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                {
                    v14Notes.Add($"{label} SHA256 校验失败，已跳过安装");
                    installationIssues.Add(v14Notes[^1]);
                    continue;
                }

                var v14Psi = new ProcessStartInfo
                {
                    FileName = v14Path,
                    Arguments = $"/install /quiet /norestart /log \"{Path.Combine(diagnostics.DirectoryPath, file + ".log")}\"",
                    UseShellExecute = true,
                    Verb = "runas",
                };
                try
                {
                    Log.Info($"运行库修复·v14 启动：名称={label}；程序={v14Path}；参数={v14Psi.Arguments}；超时=10分钟");
                    using var v14Proc = Process.Start(v14Psi);
                    if (v14Proc is null)
                    {
                        v14Notes.Add($"{label} 启动失败");
                        installationIssues.Add(v14Notes[^1]);
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
                            await v14Proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                        }
                        catch
                        {
                            // 提权进程可能杀不掉
                        }

                        v14Notes.Add($"{label} 安装超时");
                        installationIssues.Add(v14Notes[^1]);
                        continue;
                    }

                    Log.Info($"运行库修复·v14 结束：名称={label}；退出码={v14Proc.ExitCode}");
                    // 1638 只代表版本冲突，最终必须通过分支复检，不能直接视为成功。
                    switch (v14Proc.ExitCode)
                    {
                        case 0:
                            v14Notes.Add($"{label} 已安装");
                            break;
                        case 1638:
                            v14Notes.Add($"{label} 返回版本冲突 1638，等待复检确认");
                            break;
                        case 3010:
                        case 1641:
                            v14Notes.Add($"{label} 已安装（需重启）");
                            rebootNeeded = true;
                            break;
                        default:
                            v14Notes.Add($"{label} 退出码 {v14Proc.ExitCode}");
                            installationIssues.Add(v14Notes[^1]);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    v14Notes.Add($"{label} 安装异常（{ex.Message}）");
                    installationIssues.Add(v14Notes[^1]);
                }
            }

            diagnostics.Save("v14-results.json", v14Notes);
            progress?.Report("正在复检所有运行库分支并归档安装诊断…");
            VcRedistScanReport? after = null;
            try { after = await ScanVcRedistsAsync(); diagnostics.Save("after-scan.json", after); }
            catch (Exception ex) { installationIssues.Add("修复后扫描异常：" + ex.Message); Log.Error("运行库修复：复检失败", ex); }
            if (after is not null)
            {
                Log.Info($"运行库修复·复检：条目={after.All.Count}；异常={after.Abnormal.Count}；非最适={after.Suboptimal.Count}");
                foreach (var item in after.Suboptimal)
                    Log.Warn($"运行库修复·未达标分支：{item.Branch}；检测版本={item.InstalledVersion}；要求={item.RecommendedVersion}");
            }
            var assemblyErrors = diagnostics.CollectInstallerEvents();
            var finalResult = RuntimeRepairDiagnostics.BuildResult(exitCode, packages, installationIssues, after,
                rebootNeeded, diagnostics.DirectoryPath, assemblyErrors);
            var diagnosticWarnings = diagnostics.Issues;
            if (diagnosticWarnings.Length > 0)
                finalResult = new OperationResult { Success = finalResult.Success, RequiresReboot = finalResult.RequiresReboot,
                    Message = finalResult.Message + "\n诊断采集警告（不等于安装失败）：" + string.Join("；", diagnosticWarnings.Take(3)) };
            diagnostics.Save("summary.json", new { Result = finalResult, Packages = packages, Issues = installationIssues,
                DiagnosticWarnings = diagnosticWarnings, Uninstalled = uninstalled });
            Log.Info($"运行库修复·最终结果：成功={finalResult.Success}；需重启={finalResult.RequiresReboot}；{finalResult.Message}");
            progress?.Report(finalResult.Success ? "修复流程及分支复检通过。" : "修复未完成，请查看失败明细及诊断目录。");
            return finalResult;
        }
        catch (Exception ex)
        {
            Log.Error("运行库修复：安装流程异常", ex);
            diagnostics.Save("fatal-error.json", new { Exception = ex.ToString(), TimeUtc = DateTime.UtcNow, Uninstalled = uninstalled });
            diagnostics.CollectInstallerEvents();
            return OperationResult.Fail($"修复运行库失败，旧运行库可能已卸载：{ex.Message}。诊断目录：{diagnostics.DirectoryPath}");
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

    private static async Task<OperationResult> UninstallVcRedistEntryAsync(VcRedistEntry entry, string? diagnosticDirectory = null)
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
                        Arguments = $"/x {entry.KeyName} /qn /norestart" + (diagnosticDirectory is null ? ""
                            : $" /L*V \"{Path.Combine(diagnosticDirectory, "uninstall-" + Guid.NewGuid().ToString("N") + ".log")}\""),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    Log.Info($"运行库卸载：程序={psi.FileName}；参数={psi.Arguments}；名称={entry.DisplayName}");
                    using var p = Process.Start(psi);
                    if (p is null)
                    {
                        return OperationResult.Fail("msiexec 启动失败");
                    }

                    await WaitForUninstallExitAsync(p);
                    Log.Info($"运行库卸载：名称={entry.DisplayName}；PID={p.Id}；退出码={p.ExitCode}");
                    return p.ExitCode is 0 or 3010 or 1641 or 1605
                        ? OperationResult.Ok(p.ExitCode == 1605 ? "未登记此 MSI 产品（1605），未执行实际卸载。" : "已卸载。", requiresReboot: p.ExitCode is 3010 or 1641)
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
                    Log.Info($"运行库卸载：程序={psi.FileName}；参数={psi.Arguments}；名称={entry.DisplayName}");
                    using var p2 = Process.Start(psi);
                    if (p2 is null)
                    {
                        return OperationResult.Fail("卸载命令启动失败");
                    }

                    await WaitForUninstallExitAsync(p2);
                    Log.Info($"运行库卸载：名称={entry.DisplayName}；PID={p2.Id}；退出码={p2.ExitCode}");
                    return p2.ExitCode is 0 or 3010 or 1641
                        ? OperationResult.Ok("卸载命令正常结束。", requiresReboot: p2.ExitCode is 3010 or 1641)
                        : OperationResult.Fail($"卸载命令退出码 {p2.ExitCode}");
                }
                default:
                    return OperationResult.Fail("未知卸载机制");
            }
        }
        catch (TimeoutException ex)
        {
            Log.Error("运行库卸载：超时", ex);
            return OperationResult.Fail("卸载超时；已尝试结束进程，请确认安装器已停止，不要立即重复操作。");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }

    private static async Task WaitForUninstallExitAsync(Process process)
    {
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3)); }
        catch (TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) { Log.Error($"运行库卸载：无法确认超时进程已结束；PID={process.Id}", ex); }
            throw;
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
