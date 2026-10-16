using System.Diagnostics;
using DeltaNFD.Backends;
using DeltaNFD.Services;

internal static class ProcessMaintenanceChecks
{
    public static async Task RunAsync()
    {
        var testId = Guid.NewGuid();
        var dataRoot = Path.Combine(Path.GetTempPath(), "DeltaNFD_ProcessMaintenance_" + testId.ToString("N"));
        Directory.CreateDirectory(dataRoot);
        File.WriteAllText(Path.Combine(dataRoot, "isolated-test.marker"), testId.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(dataRoot, testId);
        var passed = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            passed++;
        }
        using var self = Process.GetCurrentProcess();
        var path = GameTargetService.CanonicalPath(Environment.ProcessPath!);
        var identity = new BackgroundProcessDto(self.Id, self.StartTime.ToUniversalTime().Ticks, path,
            self.ProcessName, 999999, "测试");
        Check(ProcessMaintenanceService.IdentityMatches(self, identity), "真实身份匹配");
        Check(ProcessMaintenanceService.IdentityMatches(self, identity with { ExecutablePath = path.ToUpperInvariant() }), "路径忽略大小写");
        Check(!ProcessMaintenanceService.IdentityMatches(self, identity with { StartTimeUtcTicks = 1 }), "PID 复用/启动时间变化拒绝");
        Check(!ProcessMaintenanceService.IdentityMatches(self, identity with { Pid = 0 }), "非法 PID 拒绝");
        Check(!ProcessMaintenanceService.IdentityMatches(self, identity with { ExecutablePath = "same-name.exe" }), "不得降为同名匹配");
        Check(!ProcessMaintenanceService.IdentityMatches(self, identity with { ExecutablePath = Path.Combine(Path.GetDirectoryName(path)!, "other", Path.GetFileName(path)) }), "同名不同路径拒绝");

        var service = new ProcessMaintenanceService();
        foreach (var trim in new[] { false, true })
        {
            // Both inputs must be rejected before any mutation, even under an elevated runner.
            var stale = await service.ApplyAsync(new() { Processes = [identity with { StartTimeUtcTicks = 1 }] }, trim, default);
            Check(stale.Completed == 0 && stale.MemoryMb == 0 && stale.Items[0].Status == "failed", "陈旧身份不得操作或报告释放量");
            var protectedSelf = await service.ApplyAsync(new() { Processes = [identity] }, trim, default);
            Check(protectedSelf.Completed == 0 && protectedSelf.Items[0].Message.Contains("受保护"), "自身受保护");
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledReport = await service.ApplyAsync(new() { Processes = [identity] }, false, cancelled.Token);
        Check(cancelledReport.Cancelled && cancelledReport.Items.Count == 0 && cancelledReport.MemoryMb == 0, "取消不执行");
        foreach (var batch in new List<BackgroundProcessDto>[] { [], [identity, identity], Enumerable.Repeat(identity, 257).ToList() })
        {
            var rejected = false;
            try { await service.ApplyAsync(new() { Processes = batch }, false, default); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "空批次/重复 PID/超限必须拒绝");
        }
        Check(BackendModules.IsReadOnly(BackendModules.System, BackendCommands.SystemProcessesScan), "扫描只读");
        Check(BackendModules.RequiresWriteIntent(BackendModules.System, BackendCommands.SystemProcessesKill), "结束进程登记写前意图");
        Check(BackendModules.RequiresWriteIntent(BackendModules.System, BackendCommands.SystemProcessesTrim), "工作集整理登记写前意图");
        Console.WriteLine($"进程维护安全检查通过：{passed} 项；未结束或裁剪任何进程。");
    }
}
