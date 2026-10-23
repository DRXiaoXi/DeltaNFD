using System.Diagnostics;
using DeltaNFD.Backends;
using Microsoft.Win32;
using DeltaNFD.Services;
using DeltaNFD.Services.TweakDb;

namespace DeltaNFD.Inspection;
/// <summary>Read-only inspection shared by the UI and backend. No system mutations.</summary>
public sealed class AceInspection
{
    private readonly GameTargetService _target;
    private readonly Func<bool> _deltaRunning;
    private readonly AceReadEnvironment _environment;
    private static AceScanResult BlockedScan() => new()
    {
        BlockedByGame = true,
        RunningProcesses = [DeltaForceLocator.GameProcessName],
        BlockingProcesses = [DeltaForceLocator.GameProcessName],
        Services = [],
        Paths = [],
        RegistryKeys = [],
    };
    private static AceCoreCheckResult BlockedCore() => new()
    {
        Level = AceCoreCheckLevel.Blocked,
        SysCount = 0,
        Files = [],
        Summary = "三角洲进程正在运行——已暂停 ACE 检测，未读取 ACE 安装目录。",
        Advice = "请完全退出三角洲后再重新检测；检测与清理会在游戏退出后自动恢复。",
    };
    private static readonly string[] AceServices = ["ACE-BASE", "ACE-GAME", "ACE-BOOT", "ACE-CORE", "ACE-SSC-DRV64", "ACE-ADVT", ];
    private static readonly string[] AceProcesses = ["ACE-Tray", "ACE-Service64", "ACE-Service32", "ACE-Setup64", "ACE-Setup32", ];
    private static readonly string[] BlockingProcesses = ["sguard64", "sguard32", DeltaForceLocator.GameProcessName, ];
    private const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{141E7813-148F-4451-A632-2DA347859F5D}";
    public Task<AceScanResult> ScanAsync() => DeltaNFD.Inspection.InspectionScheduler.Run(() =>
    {
        using var operation = _target.BeginOperation();
        if (_target.IsCustom)
            return new AceScanResult
            {
                BlockedByGame = false,
                RunningProcesses = [],
                BlockingProcesses = [GameTargetService.DeltaOnlyMessage],
                Services = [],
                Paths = [],
                RegistryKeys = [],
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

        var installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AntiCheatExpert");
        var paths = new List<AcePathInfo>();
        void AddPath(string name, string path)
        {
            var exists = Directory.Exists(path);
            paths.Add(new AcePathInfo { Name = name, Path = path, Exists = exists, SizeMb = exists ? _environment.GetDirectorySizeMb(path) : 0, });
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

        foreach (var(hive, label, root)in AceRegistryInspectionPolicy.Roots)
        {
            foreach (var path in AceRegistryInspectionPolicy.Candidates(p => _environment.EnumerateSubKeyNames(hive, p), root))
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
    public Task<AceCoreCheckResult> CheckCoreFilesAsync() => DeltaNFD.Inspection.InspectionScheduler.Run(() =>
    {
        using var operation = _target.BeginOperation();
        if (_target.IsCustom)
            return new AceCoreCheckResult
            {
                Level = AceCoreCheckLevel.None,
                SysCount = 0,
                Files = [],
                Summary = GameTargetService.DeltaOnlyMessage,
                Advice = "",
            };
        // 需求：三角洲进程运行时禁止对 ACE 组件做检测——ACE-CORE 检测要递归枚举
        // C:\Program Files\AntiCheatExpert 下全部 *.sys，是纯粹"碰"ACE 的操作，必须先拦。
        if (_deltaRunning())
        {
            Log.Info("ACE 检测：跳过 ACE-CORE 冗余检测（三角洲进程运行中）");
            return BlockedCore();
        }

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AntiCheatExpert");
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
            sysFiles = new DirectoryInfo(root).EnumerateFiles("*.sys", SearchOption.AllDirectories).Where(f => f.Name.Contains("ACE-CORE", StringComparison.OrdinalIgnoreCase)).Select(f =>
            {
                try
                {
                    return (RelPath: f.FullName[(root.Length + 1)..], Size: f.Length);
                }
                catch
                {
                    return (RelPath: f.Name, Size: -1L);
                }
            }).OrderBy(f => f.RelPath, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            return new AceCoreCheckResult
            {
                Level = AceCoreCheckLevel.Failed,
                SysCount = 0,
                Files = new List<string>
                {
                    $"扫描失败：{ex.Message}"},
                Summary = "ACE-CORE 文件扫描失败。",
                Advice = "可重试检测，或手动检查 C:\\Program Files\\AntiCheatExpert 目录。",
            };
        }

        var details = sysFiles.Select(f => "· " + f.RelPath + "（" + (f.Size < 0 ? "未知大小" : $"{f.Size / 1024.0 / 1024.0:0.0} MB") + "）").ToList();
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
            Advice = "建议：使用上方「一键清除 ACE」，然后用 WeGame / 游戏启动器对三角洲执行一次「修复」" + "（普通修复即可，不要使用深度修复），修复会重新安装干净的 ACE 组件。",
        };
    });
    private static bool IsProcessRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var process in processes)
        {
            process.Dispose();
        }

        return processes.Length > 0;
    }

    public AceInspection(GameTargetService? target = null, Func<bool>? deltaRunning = null)
    {
        _target = target ?? GameTargetService.Default;
        _deltaRunning = deltaRunning ?? DeltaForceLocator.IsGameRunning;
        _environment = new AceReadEnvironment();
    }
}
