using DeltaNFD.Models;

namespace DeltaNFD.Services;

/// <summary>系统优化的演示实现：全部数据为 mock，仅模拟延迟与进度。</summary>
public class MockSystemOptimizer : ISystemOptimizer
{
    private static readonly string[] Steps =
    {
        "正在扫描系统状态…",
        "已开启系统游戏模式",
        "已将游戏进程优先级提升至 High",
        "已清理 12 个非必要后台进程",
        "已整理工作集内存，释放 1843 MB",
        "电源计划已切换至「卓越性能」",
        "已应用显卡推荐设置",
        "优化完成，预计帧数提升 10% ~ 15%",
    };

    public int FullOptimizeStepCount => Steps.Length;

    public async Task<SystemOverview> GetSystemOverviewAsync()
    {
        await Task.Delay(500);
        return new SystemOverview
        {
            CpuName = "Intel i5-12490F",
            GpuName = "NVIDIA RTX 4070",
            CpuUsage = 34,
            GpuUsage = 58,
            RamUsedGb = 9.9,
            RamTotalGb = 32,
            IsGameRunning = false,
            PingMs = 32,
            PowerPlan = "平衡",
        };
    }

    public Task<List<OptimizeItem>> GetOptimizeItemsAsync()
    {
        List<OptimizeItem> items =
        [
            new()
            {
                Name = "游戏模式",
                Description = "让 Windows 优先为游戏分配 CPU 与 GPU 资源",
                IsEnabled = true,
                IsRecommended = true,
            },
            new()
            {
                Name = "进程优先级提升",
                Description = "将游戏进程优先级提升至 High，减少卡顿尖峰",
                IsEnabled = true,
                IsRecommended = true,
            },
            new()
            {
                Name = "后台进程自动清理",
                Description = "自动结束浏览器、通讯软件等非必要后台进程",
                IsEnabled = true,
                IsRecommended = true,
            },
            new()
            {
                Name = "内存整理",
                Description = "整理工作集并清理系统缓存，释放可用内存",
                IsEnabled = false,
                IsRecommended = false,
            },
            new()
            {
                Name = "卓越性能电源计划",
                Description = "切换至卓越性能（Ultimate Performance）电源计划",
                IsEnabled = false,
                IsRecommended = true,
            },
        ];
        return Task.FromResult(items);
    }

    public Task<List<ProcessInfo>> GetCleanableProcessesAsync()
    {
        List<ProcessInfo> processes =
        [
            new() { Name = "chrome.exe", Pid = 8214, MemoryMb = 862, Category = "浏览器" },
            new() { Name = "WeChat.exe", Pid = 3320, MemoryMb = 415, Category = "通讯" },
            new() { Name = "QQ.exe", Pid = 5128, MemoryMb = 386, Category = "通讯" },
            new() { Name = "OBS64.exe", Pid = 9104, MemoryMb = 341, Category = "直播" },
            new() { Name = "cloudmusic.exe", Pid = 7744, MemoryMb = 288, Category = "娱乐" },
            new() { Name = "OneDrive.exe", Pid = 4412, MemoryMb = 176, Category = "云同步" },
            new() { Name = "steam.exe", Pid = 2210, MemoryMb = 158, Category = "游戏平台" },
            new() { Name = "DingTalk.exe", Pid = 6650, MemoryMb = 132, Category = "办公" },
        ];
        return Task.FromResult(processes);
    }

    public async Task<OptimizeReport> RunFullOptimizeAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        var report = new OptimizeReport();
        foreach (var step in Steps)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(Random.Shared.Next(350, 650), ct);
            progress?.Report(step);
            report.AppliedSteps.Add(step);
        }

        report.ReclaimedMemoryMb = 1843;
        return report;
    }

    public async Task<double> KillProcessAsync(ProcessInfo process)
    {
        await Task.Delay(250);
        return process.MemoryMb;
    }
}
