using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using DeltaNFD.Models;

namespace DeltaNFD.Services;

/// <summary>
/// 系统优化器的真实实现（替换 MockSystemOptimizer）：
/// - 总览：CPU 名称/占用（GetSystemTimes 采样）、内存（GlobalMemoryStatusEx）、
///   GPU 名称（真实枚举）、电源计划（powercfg）、游戏运行状态（进程检测）为真实值；
///   GPU 占用暂为演示值（无便捷 P/Invoke 通道）。
/// - 优化项开关持久化在共享 settings.json；一键优化执行真实步骤。
/// </summary>
public sealed class RealSystemOptimizer : ISystemOptimizer
{
    private const string GameProcessName = GameProcessService.GameProcessName;

    /// <summary>一键优化的步骤总数（进度条分母）。</summary>
    public int FullOptimizeStepCount => 5;

    // ---------------- 总览 ----------------

    public async Task<SystemOverview> GetSystemOverviewAsync()
    {
        var cpuName = ReadCpuName();
        var (cpuUsage, ramUsedGb, ramTotalGb) = ReadHardwareStats();
        var gpuName = await ReadGpuNameAsync();
        var powerPlan = await ReadActivePowerPlanAsync();
        var ping = await MeasurePingAsync();

        return new SystemOverview
        {
            CpuName = cpuName,
            GpuName = gpuName,
            CpuUsage = cpuUsage,
            GpuUsage = 0,
            RamUsedGb = ramUsedGb,
            RamTotalGb = ramTotalGb,
            IsGameRunning = IsProcessAlive(GameProcessName),
            PingMs = ping,
            PowerPlan = powerPlan,
        };
    }

    // ---------------- 优化项 ----------------

    private const string ItemGamePriority = "游戏进程优先级提升";
    private const string ItemCleanProcesses = "后台进程清理";
    private const string ItemTrimMemory = "内存整理";
    private const string ItemUltimatePower = "卓越性能电源计划";

    public Task<List<OptimizeItem>> GetOptimizeItemsAsync() => Task.FromResult(new List<OptimizeItem>
    {
        new()
        {
            Name = ItemGamePriority,
            Description = "检测到三角洲启动时自动把游戏进程优先级提升至 High",
            IsEnabled = AppSettingsStore.Read().GamePriorityEnabled,
            IsRecommended = true,
        },
        new()
        {
            Name = ItemCleanProcesses,
            Description = "一键优化时结束扫描到的非必要后台进程（浏览器/通讯/网盘等）",
            IsEnabled = ReadItemFlag(ItemCleanProcesses),
            IsRecommended = true,
        },
        new()
        {
            Name = ItemTrimMemory,
            Description = "一键优化时对后台进程做工作集整理，释放被缓存的物理内存",
            IsEnabled = ReadItemFlag(ItemTrimMemory),
            IsRecommended = true,
        },
        new()
        {
            Name = ItemUltimatePower,
            Description = "一键优化时切换至卓越性能电源计划（首次自动导入）",
            IsEnabled = ReadItemFlag(ItemUltimatePower),
            IsRecommended = true,
        },
    });

    /// <summary>供 ViewModel 在开关变化时持久化（按优化项名称）。</summary>
    public static void SaveItemEnabled(string itemName, bool enabled)
    {
        if (itemName == ItemGamePriority)
        {
            ServiceLocator.GameProcess.GamePriorityEnabled = enabled;
            return;
        }

        AppSettingsStore.Update(s =>
        {
            switch (itemName)
            {
                case ItemGamePriority:
                    s.GamePriorityEnabled = enabled;
                    break;
                case ItemCleanProcesses:
                    s.OptimizeCleanProcesses = enabled;
                    break;
                case ItemTrimMemory:
                    s.OptimizeTrimMemory = enabled;
                    break;
                case ItemUltimatePower:
                    s.OptimizeUltimatePower = enabled;
                    break;
            }
        });
    }

    private static bool ReadItemFlag(string itemName) => itemName switch
    {
        ItemCleanProcesses => AppSettingsStore.Read().OptimizeCleanProcesses,
        ItemTrimMemory => AppSettingsStore.Read().OptimizeTrimMemory,
        ItemUltimatePower => AppSettingsStore.Read().OptimizeUltimatePower,
        _ => false,
    };

    // ---------------- 进程 ----------------

    public Task<List<ProcessInfo>> GetCleanableProcessesAsync()
        => ServiceLocator.GameProcess.GetScannableProcessesAsync();

    public async Task<double> KillProcessAsync(ProcessInfo process)
    {
        var result = await ServiceLocator.GameProcess.KillProcessAsync(process);
        return result.Success ? process.MemoryMb : 0;
    }

    // ---------------- 一键优化（真实执行） ----------------

    public async Task<OptimizeReport> RunFullOptimizeAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        Log.Info("一键优化：开始");
        var report = new OptimizeReport();
        var settings = AppSettingsStore.Read();
        var gameProcess = ServiceLocator.GameProcess;

        progress?.Report("正在扫描系统状态…");
        report.AppliedSteps.Add("扫描系统状态");

        // 1. 游戏进程优先级
        if (settings.GamePriorityEnabled && IsProcessAlive(GameProcessName))
        {
            try
            {
                var processes = System.Diagnostics.Process.GetProcessesByName(GameProcessName);
                foreach (var process in processes)
                {
                    using (process)
                    {
                        process.PriorityClass = System.Diagnostics.ProcessPriorityClass.High;
                    }
                }

                progress?.Report("已将三角洲进程优先级提升至 High");
                report.AppliedSteps.Add("游戏进程优先级 High");
            }
            catch
            {
                progress?.Report("游戏优先级提升失败（权限不足）");
            }

            ct.ThrowIfCancellationRequested();
        }

        // 2. 后台进程清理（真实结束进程）
        if (settings.OptimizeCleanProcesses)
        {
            progress?.Report("正在扫描可清理的后台进程…");
            var processes = await gameProcess.GetScannableProcessesAsync();
            var (killed, freedMb) = await gameProcess.KillManyAsync(processes, progress);
            report.ReclaimedMemoryMb += freedMb;
            report.AppliedSteps.Add($"结束 {killed} 个后台进程，释放 {freedMb:0} MB");
            ct.ThrowIfCancellationRequested();
        }

        // 3. 内存整理（工作集裁剪）
        if (settings.OptimizeTrimMemory)
        {
            progress?.Report("正在整理工作集内存…");
            var remaining = await gameProcess.GetScannableProcessesAsync();
            var trimmed = await gameProcess.TrimWorkingSetAsync(remaining, progress);
            report.ReclaimedMemoryMb += trimmed;
            report.AppliedSteps.Add($"工作集整理释放 {trimmed:0} MB");
            ct.ThrowIfCancellationRequested();
        }

        // 4. 卓越性能电源计划
        if (settings.OptimizeUltimatePower)
        {
            progress?.Report("正在切换卓越性能电源计划…");
            var result = await ServiceLocator.Power.SetSchemeAsync(PowerService.UltimateSchemeTemplateGuid);
            progress?.Report(result.Success ? "已切换至卓越性能电源计划" : result.Message);
            if (result.Success)
            {
                report.AppliedSteps.Add("电源计划 → 卓越性能");
            }

            ct.ThrowIfCancellationRequested();
        }

        // 5. 游戏调度微调（鼠标加速未应用时补上）
        var tweaks = ServiceLocator.SystemTweaks;
        var statuses = await tweaks.GetStatusesAsync();

        var mouseAccel = statuses.FirstOrDefault(s => s.Tweak == SystemTweak.MouseAccelerationOff);
        if (mouseAccel is { IsOptimized: false })
        {
            var result = await tweaks.DisableAsync(SystemTweak.MouseAccelerationOff);
            if (result.Success)
            {
                report.AppliedSteps.Add("鼠标加速已关闭");
            }
        }

        progress?.Report("优化完成");
        Log.Info($"一键优化：完成，执行 {report.AppliedSteps.Count} 步（{string.Join("；", report.AppliedSteps)}），释放 {report.ReclaimedMemoryMb:0} MB");
        return report;
    }

    // ---------------- 硬件读取 ----------------

    private static string ReadCpuName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString") as string ?? "未知 CPU";
        }
        catch
        {
            return "未知 CPU";
        }
    }

    private static (double CpuUsage, double RamUsedGb, double RamTotalGb) ReadHardwareStats()
    {
        double ramUsedGb = 0, ramTotalGb = 0;
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status))
            {
                ramTotalGb = status.TotalPhys / 1024.0 / 1024.0 / 1024.0;
                ramUsedGb = (status.TotalPhys - status.AvailPhys) / 1024.0 / 1024.0 / 1024.0;
            }
        }
        catch
        {
            // 内存状态读取失败保持 0
        }

        var cpuUsage = SampleCpuUsage();
        return (cpuUsage, Math.Round(ramUsedGb, 1), Math.Round(ramTotalGb, 0));
    }

    /// <summary>GetSystemTimes 两次采样计算 CPU 占用（总时间 = 内核(含 idle) + 用户）。</summary>
    private static double SampleCpuUsage()
    {
        try
        {
            GetSystemTimes(out var idle1, out var kernel1, out var user1);
            Thread.Sleep(300);
            GetSystemTimes(out var idle2, out var kernel2, out var user2);

            var totalDelta = ToUInt64(kernel2) + ToUInt64(user2) - ToUInt64(kernel1) - ToUInt64(user1);
            var idleDelta = ToUInt64(idle2) - ToUInt64(idle1);
            if (totalDelta == 0)
            {
                return 0;
            }

            return Math.Round((1.0 - idleDelta / (double)totalDelta) * 100, 0);
        }
        catch
        {
            return 0;
        }

        static ulong ToUInt64(FILETIME time) => ((ulong)(uint)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;
    }

    private async Task<string> ReadGpuNameAsync()
    {
        try
        {
            var adapters = await ServiceLocator.GpuSpoof.GetAdaptersAsync();
            return adapters.Count > 0 ? adapters[0].DisplayName : "未检测到显卡";
        }
        catch
        {
            return "未检测到显卡";
        }
    }

    private static async Task<string> ReadActivePowerPlanAsync()
    {
        try
        {
            var schemes = await ServiceLocator.Power.GetSchemesAsync();
            return schemes.FirstOrDefault(s => s.IsActive)?.Name ?? "未知";
        }
        catch
        {
            return "未知";
        }
    }

    private static async Task<int> MeasurePingAsync()
    {
        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            var reply = await ping.SendPingAsync("223.5.5.5", 1500);
            return reply.Status == System.Net.NetworkInformation.IPStatus.Success
                ? (int)reply.RoundtripTime
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsProcessAlive(string name)
    {
        var processes = System.Diagnostics.Process.GetProcessesByName(name);
        foreach (var process in processes)
        {
            process.Dispose();
        }

        return processes.Length > 0;
    }

    // ---------------- P/Invoke ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public int dwLowDateTime;
        public int dwHighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);
}
