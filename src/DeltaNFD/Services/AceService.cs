using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>ACE 清理的真实实现（等效 ACE 官方卸载程序 + 游戏目录 AntiCheatExpert 清理）。</summary>
public sealed class AceService : IAceService
{
    private readonly TweakBackupStore _backups;
    private readonly GameTargetService _target;

    /// <summary>「三角洲进程是否在运行」的判定。默认查真实进程；冒烟测试可注入以覆盖暂停分支。</summary>
    private readonly Func<bool> _deltaRunning;

    public AceService() : this(TweakBackupStore.Default)
    {
    }

    public AceService(TweakBackupStore backups, GameTargetService? target = null)
        : this(backups, target, static () => IsProcessRunning(DeltaForceLocator.GameProcessName))
    {
    }

    internal AceService(TweakBackupStore backups, GameTargetService? target, Func<bool> deltaRunning)
    {
        _backups = backups;
        _target = target ?? GameTargetService.Default;
        _deltaRunning = deltaRunning;
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

    private const int MoveFileDelayUntilReboot = 0x4;

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
                SizeMb = exists ? GetDirectorySizeMb(path) : 0,
            });
        }

        AddPath("系统安装目录", installDir);
        foreach (var gameAceDirectory in DeltaForceLocator.FindGameAceDirectories())
        {
            AddPath("游戏目录 ACE", gameAceDirectory);
        }

        var registryKeys = new List<string>();
        if (Registry.LocalMachine.OpenSubKey(UninstallKeyPath) is not null)
        {
            registryKeys.Add(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{141E7813-...}（卸载项）");
        }

        if (Registry.LocalMachine.OpenSubKey(@"SOFTWARE\AppDataLow") is not null)
        {
            registryKeys.Add(@"HKLM\SOFTWARE\AppDataLow");
        }

        if (Registry.Users.OpenSubKey(@".DEFAULT\SOFTWARE\AppDataLow") is not null)
        {
            registryKeys.Add(@"HKU\.DEFAULT\SOFTWARE\AppDataLow");
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

    // ---------------- 一键清除 ----------------

    /// <summary>一键清除 ACE。整体跑在线程池：删除注册表/数百 MB 目录等重活
    /// 若留在调用方（UI）上下文执行会长时间冻结界面。</summary>
    public async Task<OperationResult> CleanAsync(IProgress<string>? progress = null)
        => await Task.Run(async () =>
        {
            using var operation = _target.BeginOperation();
            if (_target.IsCustom) return OperationResult.Fail(GameTargetService.DeltaOnlyMessage);
            var result = await CleanCoreAsync(progress);
            return result;
        });

    private async Task<OperationResult> CleanCoreAsync(IProgress<string>? progress)
    {
        if (!ElevationHelper.IsElevated)
        {
            return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        }

        // 守卫：sguard64 / 游戏本体运行时拒绝执行
        var blockers = BlockingProcesses.Where(IsProcessRunning).ToList();
        if (blockers.Count > 0)
        {
            Log.Warn($"ACE清理：拒绝执行（阻止进程运行中：{string.Join("、", blockers)}）");
            return OperationResult.Fail(
                $"检测到反作弊守卫/游戏进程正在运行（{string.Join("、", blockers)}），已拒绝清除。" +
                "请完全退出游戏（含托盘与后台的 sguard64 进程）后重试。");
        }

        var done = new List<string>();
        var problems = new List<string>();

        // 1. 结束 ACE 残留进程
        progress?.Report("正在结束 ACE 进程…");
        foreach (var name in AceProcesses)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                        done.Add($"已结束 {name}");
                    }
                    catch
                    {
                        // 自保护或已退出，忽略
                    }
                }
            }
        }

        // 2. 停止并删除服务（自保护服务先 SDDL 接管再停止删除）
        foreach (var serviceName in AceServices)
        {
            progress?.Report($"正在处理服务 {serviceName}…");
            var keyPath = $@"SYSTEM\CurrentControlSet\Services\{serviceName}";
            if (Registry.LocalMachine.OpenSubKey(keyPath) is null)
            {
                continue;
            }

            // 2a. SDDL 接管（对抗自保护）
            var (sdCode, _, sdErr) = await RunCaptureAsync(
                "sc.exe", $"sdset {serviceName} \"{TakeoverSddl}\"", TimeSpan.FromSeconds(20));
            if (sdCode != 0)
            {
                problems.Add($"{serviceName} 接管失败（{FirstLine(sdErr)}）");
            }

            // 2b. 停止（失败不阻塞，删除服务不要求已停止）
            await RunCaptureAsync("sc.exe", $"stop {serviceName}", TimeSpan.FromSeconds(20));

            // 2c. 删除
            var (delCode, _, delErr) = await RunCaptureAsync(
                "sc.exe", $"delete {serviceName}", TimeSpan.FromSeconds(20));
            if (delCode == 0)
            {
                done.Add($"服务 {serviceName} 已删除");
            }
            else
            {
                problems.Add($"{serviceName} 删除失败（{FirstLine(delErr)}）");
            }
        }

        // 3. ACE-ADVT 注册表键显式删除（DeleteService 等价物）
        try
        {
            Registry.LocalMachine.DeleteSubKeyTree(
                @"SYSTEM\CurrentControlSet\Services\ACE-ADVT", throwOnMissingSubKey: false);
        }
        catch
        {
            // 权限不足或已删除
        }

        // 4. 注册表清理
        progress?.Report("正在清理注册表…");
        var registryTargets = (ValueTuple<string, RegistryKey, string>[])
        [
            ("HKLM", Registry.LocalMachine, UninstallKeyPath),
            ("HKLM", Registry.LocalMachine, @"SOFTWARE\AppDataLow"),
        ];

        foreach (var (label, root, keyPath) in registryTargets)
        {
            try
            {
                root.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
                done.Add($"注册表 {label}\\{keyPath} 已删除");
            }
            catch (Exception ex)
            {
                problems.Add($"注册表 {keyPath}: {ex.Message}");
            }
        }

        try
        {
            Registry.Users.DeleteSubKeyTree(@".DEFAULT\SOFTWARE\AppDataLow", throwOnMissingSubKey: false);
            done.Add(@"注册表 HKU\.DEFAULT\SOFTWARE\AppDataLow 已删除");
        }
        catch
        {
            // 不存在或已删除
        }

        // 5. 删除文件目录（被占用文件延迟到重启后删除）
        var rebootPending = 0;
        double freedMb = 0;

        var installDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AntiCheatExpert");
        var deleteTargets = new List<string> { installDir };
        deleteTargets.AddRange(DeltaForceLocator.FindGameAceDirectories());

        foreach (var dir in deleteTargets.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            progress?.Report($"正在删除 {dir}…");
            var before = GetDirectorySizeMb(dir);
            var (freed, pending) = DeleteDirectoryTree(dir);
            rebootPending += pending;
            freedMb += Math.Max(0, before - GetDirectorySizeMb(dir));

            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // 目录内还有被锁定文件则保留目录，重启后可再清
            }

            done.Add($"已删除 {dir}（释放 {Math.Max(0, before - GetDirectorySizeMb(dir)):0.0} MB）");
            _ = freed;
        }

        var summary =
            $"ACE 清理完成：{done.Count} 步操作，释放 {freedMb:0.0} MB。";
        if (rebootPending > 0)
        {
            summary += $"有 {rebootPending} 个被占用文件将在重启电脑后自动删除。";
        }

        Log.Info($"ACE清理：{summary}（跳过 {problems.Count} 项）");

        var rebootNote = "下次启动游戏时 ACE 会自动重新安装。请用 WeGame / 游戏启动器对三角洲执行一次「修复」" +
                        "（普通修复即可，不要使用深度修复），以重新安装干净的 ACE 组件。";

        if (problems.Count > 0)
        {
            return OperationResult.Ok(
                $"{summary}部分项目跳过（{string.Join("；", problems.Take(2))}{(problems.Count > 2 ? " 等" : "")}）。" +
                rebootNote, requiresReboot: rebootPending > 0);
        }

        return OperationResult.Ok($"{summary}{rebootNote}", requiresReboot: rebootPending > 0);
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

    private static double GetDirectorySizeMb(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return 0;
            }

            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(file => { try { return file.Length; } catch { return 0; } }) / 1024.0 / 1024.0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>递归删除目录内容；被占用文件注册为重启后删除。返回 (释放 MB, 待重启删除数)。</summary>
    private static (double FreedMb, int RebootPending) DeleteDirectoryTree(string path)
    {
        var rebootPending = 0;
        double freedMb = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var length = new FileInfo(file).Length;
                    File.Delete(file);
                    freedMb += length / 1024.0 / 1024.0;
                }
                catch
                {
                    // 被占用：注册重启后删除
                    if (MoveFileEx(file, null, MoveFileDelayUntilReboot))
                    {
                        rebootPending++;
                    }
                }
            }

            foreach (var dir in Directory.EnumerateDirectories(path).OrderByDescending(d => d.Length))
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch
                {
                    // 内含重启后删除的文件，目录保留
                }
            }
        }
        catch
        {
            // 目录访问失败整体跳过
        }

        return (freedMb, rebootPending);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);

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
