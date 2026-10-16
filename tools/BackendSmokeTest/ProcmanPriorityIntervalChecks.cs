using System.Diagnostics;
using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

internal static class ProcmanPriorityIntervalChecks
{
    internal static async Task RunAsync()
    {
        var id = Guid.NewGuid(); var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-procman-priority-" + id.ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT", root);
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ID", id.ToString("D"));
        using var parent = Process.GetCurrentProcess(); var parentPriority = parent.PriorityClass;
        var checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var client = await BackendClient.StartAsync(BackendModules.Procman, CancellationToken.None);
            var reply = await client.InvokeAsync(BackendCommands.ProcmanStatus, new { }, null, default);
            var status = BackendJson.Deserialize<ProcmanStatusSnapshot>(reply.Data!.Value);
            Check(status.BackendPid > 0 && status.BackendPid != Environment.ProcessId && status.BackendPriorityClass == "BelowNormal", "真实后端状态回显低于正常优先级");
            using var backend = Process.GetProcessById(status.BackendPid);
            Check(backend.PriorityClass == ProcessPriorityClass.BelowNormal, "Win32 回读后端自身优先级");
            var config = BackendJson.Deserialize<ProcmanBackgroundStatus>((await client.InvokeAsync(BackendCommands.ProcmanBackgroundGet, new { }, null, default)).Data!.Value);
            Check(config.Config.MemoryCooldownSeconds == (attempt == 0 ? 600 : 60), "默认十分钟，自定义一分钟跨后端重启持久保存");
            var saved = await client.InvokeAsync(BackendCommands.ProcmanBackgroundSave,
                config.Config with { MemoryCooldownSeconds = 60, MemoryBatchLimit = 5 }, null, default);
            Check(saved.Status == BackendResultStatuses.Ok, "低优先级后端仍可保存参数");
            var after = BackendJson.Deserialize<ProcmanBackgroundStatus>(saved.Data!.Value);
            Check(after.Config.MemoryCooldownSeconds == 60 && after.EffectiveMemoryCooldownSeconds == 60 && !after.AutomaticMemoryActive,
                "参数与实际间隔一致，保存不会启动帧格回收");
            foreach (var mode in new[] { ProcmanMemoryModes.Normal, ProcmanMemoryModes.DeltaStable, ProcmanMemoryModes.Aggressive })
            {
                var modeReply = await client.InvokeAsync(BackendCommands.ProcmanBackgroundSave,
                    after.Config with { MemoryMode = mode }, null, default);
                Check(modeReply.Status == BackendResultStatuses.Ok
                    && BackendJson.Deserialize<ProcmanBackgroundStatus>(modeReply.Data!.Value).Config.MemoryMode == mode,
                    "真实后端保存并回显模式：" + mode);
            }
            var previewReply = await client.InvokeAsync(BackendCommands.ProcmanMemoryPreview, new { }, null, default);
            var preview = BackendJson.Deserialize<ProcmanMemoryPreview>(previewReply.Data!.Value);
            Check(preview.Mode == ProcmanMemoryModes.Aggressive && preview.Candidates.Count == 0 && preview.Id.Length > 0,
                "真实全局模式预览不枚举进程，返回可确认令牌但测试不执行全局回收");
            await client.DrainAsync(TimeSpan.FromSeconds(15));
            Check(client.ExitConfirmed, "低优先级后端正常完成排空退出");
        }
        parent.Refresh(); Check(parent.PriorityClass == parentPriority, "后端优先级变化不修改宿主优先级");
        Console.WriteLine($"Procman priority/interval IPC: {checks} checks passed. Actual BelowNormal readback; isolated config only, no target processes modified. Evidence: {root}");
    }
}
