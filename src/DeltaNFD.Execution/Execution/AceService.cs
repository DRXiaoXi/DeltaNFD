using System.Diagnostics;
using DeltaNFD.Backends;
using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>ACE 清理的真实实现（等效 ACE 官方卸载程序 + 游戏目录 AntiCheatExpert 清理）。</summary>
public sealed class AceService : IAceService
{
    private readonly TweakBackupStore _backups;
    private readonly GameTargetService _target;

    /// <summary>「三角洲进程是否在运行」的判定。默认查真实进程；冒烟测试可注入以覆盖暂停分支。</summary>
    private readonly Func<bool> _deltaRunning;

    /// <summary>可注入的清理执行环境（进程/服务/注册表/文件）；生产为真实系统，模拟验收为内存夹具。</summary>
    private readonly IAceCleanupEnvironment _environment;

    public AceService() : this(TweakBackupStore.Default)
    {
    }

    public AceService(TweakBackupStore backups, GameTargetService? target = null)
        : this(backups, target, static () => IsProcessRunning(DeltaForceLocator.GameProcessName))
    {
    }

    internal AceService(TweakBackupStore backups, GameTargetService? target, Func<bool> deltaRunning)
        : this(backups, target, deltaRunning, new RealAceCleanupEnvironment())
    {
    }

    internal AceService(TweakBackupStore backups, GameTargetService? target, Func<bool> deltaRunning, IAceCleanupEnvironment environment)
    {
        _backups = backups;
        _target = target ?? GameTargetService.Default;
        _deltaRunning = deltaRunning;
        _environment = environment;
    }

    // ---------------- 检测闸门 ----------------

    /// <summary>
    /// 三角洲进程运行中时的"已暂停检测"占位结果：**不含任何 ACE 读取**。
    /// 把三角洲同时列入 Running/Blocking，使 <see cref="AceScanResult.CanClean"/> 为 false——
    /// 检测被暂停时绝不能让 UI 误判为"可清理"。
    /// </summary>
    private static AceScanResult BlockedScan() => new()
    {
        BlockedByGame = true,
        RunningProcesses = [DeltaForceLocator.GameProcessName],
        BlockingProcesses = [DeltaForceLocator.GameProcessName],
        Services = [],
        Paths = [],
        RegistryKeys = [],
    };

    /// <summary>三角洲进程运行中时的"已跳过 ACE-CORE 检测"占位结果（未读取 ACE 目录）。</summary>
    private static AceCoreCheckResult BlockedCore() => new()
    {
        Level = AceCoreCheckLevel.Blocked,
        SysCount = 0,
        Files = [],
        Summary = "三角洲进程正在运行——已暂停 ACE 检测，未读取 ACE 安装目录。",
        Advice = "请完全退出三角洲后再重新检测；检测与清理会在游戏退出后自动恢复。",
    };

    /// <summary>ACE 服务清单（与官方卸载程序一致）。</summary>
    private static readonly string[] AceServices =
    [
        "ACE-BASE",
        "ACE-GAME",
        "ACE-BOOT",
        "ACE-CORE",
        "ACE-SSC-DRV64",
        "ACE-ADVT",
    ];

    /// <summary>清理前强制结束的 ACE 进程。</summary>
    private static readonly string[] AceProcesses =
    [
        "ACE-Tray",
        "ACE-Service64",
        "ACE-Service32",
        "ACE-Setup64",
        "ACE-Setup32",
    ];

    /// <summary>阻止清理的进程：反作弊守卫与游戏本体。</summary>
    private static readonly string[] BlockingProcesses =
    [
        "sguard64",
        "sguard32",
        DeltaForceLocator.GameProcessName,
    ];

    /// <summary>ACE 官方卸载项 GUID。</summary>
    private const string UninstallKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{141E7813-148F-4451-A632-2DA347859F5D}";

    /// <summary>自保护接管用 SDDL（授予 Administrators 停止/删除权限，与官方卸载程序一致）。</summary>
    private const string TakeoverSddl =
        "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)";

    // ---------------- 扫描 ----------------

    public Task<AceScanResult> ScanAsync() => Task.Run(() =>
    {
        using var operation = _target.BeginOperation();
        if (_target.IsCustom) return new AceScanResult
        {
            BlockedByGame = false,
            RunningProcesses = [], BlockingProcesses = [GameTargetService.DeltaOnlyMessage], Services = [], Paths = [], RegistryKeys = [],
        };

        // 需求：三角洲进程运行时禁止对 ACE 组件做检测——直接返回，不读取任何 ACE 信息。
        if (_deltaRunning())
        {
            Log.Info("ACE 检测：跳过组件扫描（三角洲进程运行中）");
            return BlockedScan();
        }

        var running = new List<string>();
        var blocking = new List<string>();

        foreach (var name in BlockingProcesses)
        {
            if (IsProcessRunning(name))
            {
                running.Add(name);
                blocking.Add(name);
            }
        }

        foreach (var name in AceProcesses)
        {
            if (IsProcessRunning(name))
            {
                running.Add(name);
            }
        }

        var services = AceServices.Select(name =>
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
            return new AceServiceInfo
            {
                Name = name,
                Exists = key is not null,
                StartMode = key is null ? "未安装" : key.GetValue("Start") switch
                {
                    0 => "引导启动",
                    1 => "系统启动",
                    2 => "自动",
                    3 => "手动/按需",
                    4 => "已禁用",
                    _ => "存在",
                },
            };
        }).ToList();

        // 上面只读了 HKLM\SYSTEM 的服务项，不碰 ACE 文件；但接下来要真正遍历 ACE 安装目录
        // （GetDirectorySizeMb 递归统计数百 MB），所以在动手前再确认一次：若三角洲在枚举期间
        // 启动，立刻收手，保证游戏运行时绝不读取 ACE 目录。
        if (_deltaRunning())
        {
            Log.Info("ACE 检测：扫描途中三角洲进程启动，已中止 ACE 目录读取");
            return BlockedScan();
        }

        var installDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AntiCheatExpert");
        var paths = new List<AcePathInfo>();
        void AddPath(string name, string path)
        {
            var exists = Directory.Exists(path);
            paths.Add(new AcePathInfo
            {
                Name = name,
                Path = path,
                Exists = exists,
                SizeMb = exists ? _environment.GetDirectorySizeMb(path) : 0,
            });
        }

        AddPath("系统安装目录", installDir);
        foreach (var gameAceDirectory in DeltaForceLocator.FindGameAceDirectories())
        {
            AddPath("游戏目录 ACE", gameAceDirectory);
        }

        var registryKeys = new List<string>();
        if (_environment.RegistryKeyExists(RegistryHive.LocalMachine, UninstallKeyPath))
        {
            registryKeys.Add(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{141E7813-...}（卸载项）");
        }

        foreach (var (hive, label, root) in AceRegistryPolicy.Roots)
        {
            foreach (var path in AceRegistryPolicy.Candidates(p => _environment.EnumerateSubKeyNames(hive, p), root))
                registryKeys.Add($"{label}\\{path}" + (_environment.HasAceRegistryOwnership(hive, path) ? "" : "（归属未确认，将保留）"));
        }

        return new AceScanResult
        {
            BlockedByGame = false,
            RunningProcesses = running,
            BlockingProcesses = blocking,
            Services = services,
            Paths = paths,
            RegistryKeys = registryKeys,
        };
    });

    // ---------------- 一键清除（分阶段 + 每阶段阻止条件复核） ----------------

    /// <summary>一键清除 ACE（接口版，供 IAceService 消费方使用）。执行细节见 <see cref="CleanPhasedAsync"/>。</summary>
    public async Task<OperationResult> CleanAsync(IProgress<string>? progress = null)
    {
        var wire = await CleanPhasedAsync(progress, CancellationToken.None);
        return AceCleanupResults.ToOperationResult(wire);
    }

    /// <summary>
    /// 分阶段清除 ACE。每个阶段开始前都重新复核「管理员 / 默认游戏模式 / 游戏与守卫状态」：
    /// 检测失败或状态未知一律拒绝；发现游戏/守卫启动立即停止后续步骤并报告已完成项。
    /// 只由玩家明确确认触发，不启动自愈、登录清理或后台重试；完成/取消/失败后由后端自行退出。
    /// </summary>
    public async Task<BackendResult> CleanPhasedAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        using var operation = _target.BeginOperation();

        if (_target.IsCustom)
            return BackendResult.Failed("ace.custom-target", GameTargetService.DeltaOnlyMessage);
        if (!_environment.IsElevated)
            return BackendResult.Failed("ace.not-elevated", ElevationHelper.NotElevatedMessage);
        Log.Info("ACE清理：后端已自行复核管理员权限与默认游戏模式。");

        var done = new List<BackendResultItem>();
        var problems = new List<string>();

        var guard = new AceCleanupGuard(() => _target.IsCustom, () => _environment.IsElevated, () =>
        {
            if (!TryGetBlockers(out var blockers, out var error)) throw new IOException(error);
            return blockers;
        });
        BackendResult? Gate(string stage)
        {
            var result = guard.Check(stage, done, cancellationToken);
            if (result is not null) Log.Warn($"ACE清理已中止：stage={stage}；code={result.Code}；{result.Message}");
            return result;
        }

        // 阶段 1：结束 ACE 残留进程
        if (Gate("terminate-processes") is { } gateKill) return gateKill;
        progress?.Report("正在结束 ACE 进程…");
        foreach (var name in AceProcesses)
        {
            if (cancellationToken.IsCancellationRequested) return CancelledResult(done, "清理已取消。");
            foreach (var pid in _environment.FindProcesses(name))
            {
                if (Gate("terminate:" + name) is { } gateProcess) return gateProcess;
                if (_environment.TerminateProcessTree(pid))
                    done.Add(new BackendResultItem($"kill:{name}", BackendResultStatuses.Ok, $"已结束 {name}"));
            }
        }

        // 阶段 2：停止并删除服务（自保护服务先 SDDL 接管再停止删除）
        foreach (var serviceName in AceServices)
        {
            if (Gate("service:" + serviceName) is { } gateService) return gateService;
            progress?.Report($"正在处理服务 {serviceName}…");
            if (!_environment.ServiceKeyExists(serviceName))
            {
                continue;
            }

            // 2a. SDDL 接管（对抗自保护）
            var (sdCode, _, sdErr) = await _environment.RunCommandAsync(
                "sc.exe", $"sdset {serviceName} \"{TakeoverSddl}\"", TimeSpan.FromSeconds(20));
            if (sdCode != 0)
            {
                problems.Add($"{serviceName} 接管失败（{FirstLine(sdErr)}）");
            }
            else done.Add(new BackendResultItem($"acl:{serviceName}", BackendResultStatuses.Ok, $"服务 {serviceName} 权限已修改"));

            // 2b. 停止（失败不阻塞，删除服务不要求已停止）
            if (Gate("stop-service:" + serviceName) is { } gateStop) return gateStop;
            await _environment.RunCommandAsync("sc.exe", $"stop {serviceName}", TimeSpan.FromSeconds(20));

            // 2c. 删除
            if (Gate("delete-service:" + serviceName) is { } gateDelete) return gateDelete;
            var (delCode, _, delErr) = await _environment.RunCommandAsync(
                "sc.exe", $"delete {serviceName}", TimeSpan.FromSeconds(20));
            if (delCode == 0)
            {
                done.Add(new BackendResultItem($"service:{serviceName}", BackendResultStatuses.Ok, $"服务 {serviceName} 已删除"));
            }
            else
            {
                problems.Add($"{serviceName} 删除失败（{FirstLine(delErr)}）");
            }
        }

        // 阶段 3：ACE-ADVT 注册表键显式删除（DeleteService 等价物）
        if (Gate("registry-advt") is { } gateAdvt) return gateAdvt;
        try
        {
            _environment.DeleteRegistryTree(
                Microsoft.Win32.RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\ACE-ADVT", throwOnMissingSubKey: false);
        }
        catch
        {
            // 权限不足或已删除
        }

        // 阶段 4：注册表清理
        if (Gate("registry") is { } gateRegistry) return gateRegistry;
        progress?.Report("正在清理注册表…");
        var registryTargets = new List<(string Label, RegistryHive Hive, string Path)>
        { ("HKLM", RegistryHive.LocalMachine, UninstallKeyPath) };
        foreach (var (hive, label, root) in AceRegistryPolicy.Roots)
        {
            try
            {
                foreach (var path in AceRegistryPolicy.Candidates(p => _environment.EnumerateSubKeyNames(hive, p), root))
                {
                    if (Gate("registry-backup:" + path) is { } gateBackup) return gateBackup;
                    if (!_environment.HasAceRegistryOwnership(hive, path))
                    { problems.Add($"注册表 {label}\\{path} 归属未确认，已保留"); continue; }
                    var backup = await _environment.BackupRegistryKeyAsync(hive, path);
                    if (!backup.Success) { problems.Add($"注册表 {label}\\{path} 备份未核验，已保留：{backup.Message}"); continue; }
                    Log.Info(backup.Message);
                    registryTargets.Add((label, hive, path));
                }
            }
            catch (Exception ex) { problems.Add($"注册表 {label}\\{root} 枚举或备份未完成，保留未确认项：{ex.Message}"); }
        }

        foreach (var (label, hive, keyPath) in registryTargets)
        {
            if (Gate("registry:" + keyPath) is { } gateKey) return gateKey;
            try
            {
                if (AceRegistryPolicy.IsSharedPath(keyPath) && !_environment.HasAceRegistryOwnership(hive, keyPath))
                { problems.Add($"注册表 {label}\\{keyPath} 备份后归属变化，已保留"); continue; }
                _environment.DeleteRegistryTree(hive, keyPath, throwOnMissingSubKey: false);
                done.Add(new BackendResultItem($"registry:{keyPath}", BackendResultStatuses.Ok, $"注册表 {label}\\{keyPath} 已删除"));
            }
            catch (Exception ex)
            {
                problems.Add($"注册表 {keyPath}: {ex.Message}");
            }
        }

        // 阶段 5：删除文件目录（被占用文件延迟到重启后删除）
        var rebootPending = 0;
        double freedMb = 0;

        var allowedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _environment.AceInstallDirectory };
        foreach (var gameAceDirectory in _environment.GameAceDirectories()) allowedRoots.Add(gameAceDirectory);
        var deleteTargets = allowedRoots.ToList();

        foreach (var dir in deleteTargets.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_environment.DirectoryExists(dir))
            {
                continue;
            }

            // 只允许删除已知 ACE 目标（目录名必须为 AntiCheatExpert）；重解析点可能被改道到任意位置，一律拒绝。
            if (!IsAllowedAceDirectory(dir) || _environment.IsReparsePoint(dir))
            {
                problems.Add($"{dir} 不是已知 ACE 目录或含重解析点，已拒绝删除");
                continue;
            }

            if (Gate("files:" + dir) is { } gateFiles) return gateFiles;
            progress?.Report($"正在删除 {dir}…");
            var before = _environment.GetDirectorySizeMb(dir);
            var (freed, pending) = _environment.DeleteDirectoryTree(dir);
            rebootPending += pending;
            freedMb += Math.Max(0, before - _environment.GetDirectorySizeMb(dir));

            if (_environment.DirectoryExists(dir))
            {
                // 仍有被占用文件：不得显示为已删除。
                problems.Add($"{dir} 仍有被占用文件（{pending} 个已登记重启后删除）");
            }
            else
            {
                done.Add(new BackendResultItem($"files:{dir}", BackendResultStatuses.Ok, $"已删除 {dir}（释放 {freed:0.0} MB）"));
            }
        }

        var summary =
            $"ACE 清理{(problems.Count > 0 ? "部分完成" : "完成")}：{done.Count} 步操作，释放 {freedMb:0.0} MB。";
        if (rebootPending > 0)
        {
            summary += $"有 {rebootPending} 个被占用文件将在重启电脑后自动删除。";
        }

        Log.Info($"ACE清理：{summary}（跳过 {problems.Count} 项）");

        var rebootNote = "下次启动游戏时 ACE 会自动重新安装。请用 WeGame / 游戏启动器对三角洲执行一次「修复」" +
                        "（普通修复即可，不要使用深度修复），以重新安装干净的 ACE 组件。";

        if (problems.Count > 0)
        {
            var message = $"{summary}部分项目跳过（{string.Join("；", problems.Take(2))}{(problems.Count > 2 ? " 等" : "")}）。" + rebootNote;
            return BackendResult.PartialResult("ace.clean-partial", message, done,
                data: new OperationResult { Success = false, IsPartial = true, Message = message, RequiresReboot = rebootPending > 0 });
        }

        var okMessage = $"{summary}{rebootNote}";
        return BackendResult.Ok("ace.clean", okMessage,
            data: OperationResult.Ok(okMessage, requiresReboot: rebootPending > 0));
    }

    /// <summary>只允许删除名为 AntiCheatExpert 的绝对路径目录，其余越界路径一律拒绝。</summary>
    private static bool IsAllowedAceDirectory(string directory)
    {
        try
        {
            if (!Path.IsPathFullyQualified(directory)) return false;
            var trimmed = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(Path.GetFileName(trimmed), "AntiCheatExpert", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>复核阻止条件；无法确认进程状态时返回 false（调用方必须拒绝执行，绝不能当作「无阻止」）。</summary>
    private bool TryGetBlockers(out List<string> blockers, out string error)
    {
        blockers = [];
        error = "";
        try
        {
            foreach (var name in BlockingProcesses)
            {
                if (_environment.IsProcessRunning(name))
                {
                    blockers.Add(name);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            blockers = [];
            error = ex.Message;
            return false;
        }
    }

    private static BackendResult CancelledResult(List<BackendResultItem> done, string message) => done.Count == 0
        ? BackendResult.Cancelled("ace.cancelled", message)
        : BackendResult.PartialResult("ace.cancelled", message, done.ToArray());

    private static BackendResult AbortUnknown(List<BackendResultItem> done, string stage, string error)
    {
        var message = $"清理前状态复核失败（阶段 {stage}）：{error}。为避免在状态未知时改动系统，已停止清理" +
                      $"（已完成 {done.Count} 项）。";
        return done.Count == 0
            ? BackendResult.Failed("ace.state-unknown", message)
            : BackendResult.PartialResult("ace.state-unknown", message, done);
    }

    private static BackendResult AbortBlocked(List<BackendResultItem> done, string stage, List<string> blockers)
    {
        var message = $"检测到反作弊守卫/游戏进程正在运行（{string.Join("、", blockers)}），已在阶段 {stage} 停止后续清理" +
                      $"（已完成 {done.Count} 项）。请完全退出游戏（含托盘与后台的 sguard64 进程）后重新执行。";
        return done.Count == 0
            ? BackendResult.Failed("ace.blocked", message)
            : BackendResult.PartialResult("ace.blocked", message, done);
    }

    // ---------------- ACE-CORE 冗余检测 ----------------

    public Task<AceCoreCheckResult> CheckCoreFilesAsync() => Task.Run(() =>
    {
        using var operation = _target.BeginOperation();
        if (_target.IsCustom) return new AceCoreCheckResult
        {
            Level = AceCoreCheckLevel.None, SysCount = 0, Files = [], Summary = GameTargetService.DeltaOnlyMessage, Advice = "",
        };

        // 需求：三角洲进程运行时禁止对 ACE 组件做检测——ACE-CORE 检测要递归枚举
        // C:\Program Files\AntiCheatExpert 下全部 *.sys，是纯粹"碰"ACE 的操作，必须先拦。
        if (_deltaRunning())
        {
            Log.Info("ACE 检测：跳过 ACE-CORE 冗余检测（三角洲进程运行中）");
            return BlockedCore();
        }

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AntiCheatExpert");
        if (!Directory.Exists(root))
        {
            return new AceCoreCheckResult
            {
                Level = AceCoreCheckLevel.None,
                SysCount = 0,
                Files = new List<string>(),
                Summary = "未安装 ACE（未找到 C:\\Program Files\\AntiCheatExpert），无需检测。",
                Advice = "无需处理。",
            };
        }

        List<(string RelPath, long Size)> sysFiles;
        try
        {
            sysFiles = new DirectoryInfo(root)
                .EnumerateFiles("*.sys", SearchOption.AllDirectories)
                .Where(f => f.Name.Contains("ACE-CORE", StringComparison.OrdinalIgnoreCase))
                .Select(f => { try { return (RelPath: f.FullName[(root.Length + 1)..], Size: f.Length); } catch { return (RelPath: f.Name, Size: -1L); } })
                .OrderBy(f => f.RelPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            return new AceCoreCheckResult
            {
                Level = AceCoreCheckLevel.Failed,
                SysCount = 0,
                Files = new List<string> { $"扫描失败：{ex.Message}" },
                Summary = "ACE-CORE 文件扫描失败。",
                Advice = "可重试检测，或手动检查 C:\\Program Files\\AntiCheatExpert 目录。",
            };
        }

        var details = sysFiles
            .Select(f => "· " + f.RelPath + "（" + (f.Size < 0 ? "未知大小" : $"{f.Size / 1024.0 / 1024.0:0.0} MB") + "）")
            .ToList();

        if (sysFiles.Count == 0)
        {
            return new AceCoreCheckResult
            {
                Level = AceCoreCheckLevel.None,
                SysCount = 0,
                Files = details,
                Summary = "未找到 ACE-CORE 系列 sys 文件（ACE 未安装或已被一键清除）。",
                Advice = "无需处理——下次启动游戏时 ACE 会自动重新安装。",
            };
        }

        if (sysFiles.Count == 1)
        {
            return new AceCoreCheckResult
            {
                Level = AceCoreCheckLevel.Normal,
                SysCount = 1,
                Files = details,
                Summary = "ACE-CORE 驱动文件正常（只有 1 个）。",
                Advice = "无需处理。",
            };
        }

        return new AceCoreCheckResult
        {
            Level = AceCoreCheckLevel.Abnormal,
            SysCount = sysFiles.Count,
            Files = details,
            Summary = $"检测到 {sysFiles.Count} 个 ACE-CORE 系列 sys 文件（冗余）——多版本残留会导致反作弊异常。",
            Advice = "建议：使用上方「一键清除 ACE」，然后用 WeGame / 游戏启动器对三角洲执行一次「修复」"
                     + "（普通修复即可，不要使用深度修复），修复会重新安装干净的 ACE 组件。",
        };
    });

    // ---------------- ACE 虚拟化环境检测 ----------------

    /// <summary>
    /// ACE 虚拟化环境检测（纯只读）。只查固件/系统虚拟化状态（CIM、bcdedit、注册表、可选功能），
    /// **不读取任何 ACE 组件**，因此不套用「三角洲运行时暂停」闸门——游戏运行中也可安全调用。
    /// </summary>
    public Task<AceVirtualizationCheckResult> CheckVirtualizationAsync()
        => AceVirtualizationCheck.RunAsync();

    // ---------------- 工具 ----------------

    private static bool IsProcessRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var process in processes)
        {
            process.Dispose();
        }

        return processes.Length > 0;
    }

    private static string FirstLine(string text)
    {
        var trimmed = text.Trim();
        var lineBreak = trimmed.IndexOfAny(['\r', '\n']);
        return lineBreak > 0 ? trimmed[..lineBreak] : trimmed;
    }
}
