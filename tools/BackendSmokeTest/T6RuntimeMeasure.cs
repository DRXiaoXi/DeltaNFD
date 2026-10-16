using System.Diagnostics;
using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

namespace BackendSmokeTest;

/// <summary>
/// T6 只读运行时测量：在隔离数据根中启动 CPU/帧格后端（唯一持久后端），
/// 空闲 5 秒采样其工作集与 CPU 占用，并统计同时存在的官方后端进程数。
/// 只做握手与状态订阅，不执行任何监控/写入命令；不启动 GUI、不改系统。
/// 前端 WinUI 的启动时间、真实 GUI/缩放/页面回收无法在此无头测量，另行记录为未验证。
/// </summary>
internal static class T6RuntimeMeasure
{
    public static async Task RunAsync(string[] args)
    {
        var testId = Guid.NewGuid();
        var isolated = Path.Combine(Path.GetTempPath(), "DeltaNFD-t6-measure", testId.ToString("N"));
        Directory.CreateDirectory(isolated);
        File.WriteAllText(Path.Combine(isolated, "isolated-test.marker"), testId.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(isolated, testId);
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT", isolated);
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ID", testId.ToString("D"));

        string exe;
        try
        {
            exe = BackendPaths.Resolve(BackendModules.CpuFrame);
        }
        catch (BackendContractException ex)
        {
            Console.WriteLine("SKIP: 未找到 CPU/帧格后端，无法测量空闲足迹：" + ex.Message);
            return;
        }

        Console.WriteLine($"后端 EXE：{exe}");
        Console.WriteLine($"体积（字节）：{new FileInfo(exe).Length}");

        await using var client = await BackendClient.StartAsync(BackendModules.CpuFrame, CancellationToken.None);
        await Task.Delay(1500); // 预热：等待握手与首次状态推送

        var processes = Process.GetProcessesByName("DeltaNFD.Backend.CpuFrame");
        if (processes.Length != 1)
        {
            foreach (var p in processes) p.Dispose();
            throw new Exception($"空闲时应恰好有 1 个 CPU/帧格后端进程，实际 {processes.Length} 个。");
        }

        var process = processes[0];
        using (process)
        {
            process.Refresh();
            var cpuA = process.TotalProcessorTime;
            var wsA = process.WorkingSet64;
            var sampleMs = 5000;
            await Task.Delay(sampleMs);
            process.Refresh();
            var cpuB = process.TotalProcessorTime;
            var wsB = process.WorkingSet64;

            var cpuPercent = (cpuB - cpuA).TotalMilliseconds / (sampleMs * (double)Environment.ProcessorCount) * 100.0;
            Console.WriteLine($"空闲采样：{sampleMs} ms");
            Console.WriteLine($"工作集：{wsA / 1024.0 / 1024.0:0.0} MB → {wsB / 1024.0 / 1024.0:0.0} MB（采样末值 {wsB / 1024.0 / 1024.0:0.0} MB）");
            Console.WriteLine($"空闲 CPU：{cpuPercent:0.000}%（按 {Environment.ProcessorCount} 逻辑处理器归一）");
            Console.WriteLine($"空闲官方后端进程数：1");
        }

        await client.DrainAsync(TimeSpan.FromSeconds(10));
        Console.WriteLine("测量完成；后端已排空退出。未执行任何监控/写入命令，未启动 GUI。");
        try { Directory.Delete(isolated, recursive: true); } catch { /* 临时目录清理失败不影响测量 */ }
    }
}
