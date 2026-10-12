using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Services;

internal static class RuntimeEnvironmentChecks
{
    internal static void Run()
    {
        var count = 0;
        void Check(bool pass, string name) { if (!pass) throw new Exception(name); count++; Console.WriteLine("PASS: " + name); }
        var normal = new RuntimePreflightSnapshot([new("msiserver", 3, 1), new("TrustedInstaller", 3, 1)], false, false);
        var input = new RuntimeRepairReadinessInput(true, true, true, normal, false, null, null,
            6L * 1024 * 1024 * 1024, 6L * 1024 * 1024 * 1024, "", "");
        RuntimeRepairEnvironmentReport Report(RuntimeRepairReadinessInput value) => RuntimeRepairReadiness.Evaluate(value);
        Check(Report(input).CanProceed && !Report(input).HasWarnings, "基础环境正常时允许进入确认，按需服务停止也正常");
        Check(!Report(input with { Elevated = false }).CanProceed, "没有管理员权限不能进入卸载");
        Check(!Report(input with { Windows = false }).CanProceed && !Report(input with { X64 = false }).CanProceed, "不支持的平台或架构不开始修复");
        Check(!Report(input with { Preflight = normal with { Services = [] } }).CanProceed, "服务查询不完整属于未知，禁止继续");
        Check(!Report(input with { Preflight = normal with { Services = [new("msiserver", 4, 1), new("TrustedInstaller", 3, 1)] } }).CanProceed, "安装服务被禁用时阻止，不自动改配置");
        Check(!Report(input with { Preflight = normal with { Services = [new("msiserver", 3, 4), new("TrustedInstaller", -1, -1, "拒绝访问")] } }).CanProceed, "服务权限不可读时明确无法确认");
        foreach (var state in new[] { 2, 3, 5, 6, 7 })
            Check(!Report(input with { Preflight = normal with { Services = [new("msiserver", 3, state), new("TrustedInstaller", 3, 1)] } }).CanProceed,
                "服务切换或暂停状态阻止修复：" + state);
        Check(!Report(input with { Preflight = normal with { InstallerBusy = true } }).CanProceed, "已有 MSI 事务时阻止");
        Check(!Report(input with { PendingOperation = "上次安装身份尚未确认" }).CanProceed, "持久化未知安装事务阻止，不删除证据");
        var rename = Report(input with { Preflight = normal with { RebootPending = true } });
        Check(rename.CanProceed && rename.HasWarnings, "一般文件重命名待重启仅提醒，不一概误判为失败");
        Check(!Report(input with { Preflight = normal with { RebootPending = true, ServicingRebootPending = true } }).CanProceed,
            "系统组件/Windows Update 要求重启时先处理再卸载");
        Check(!Report(input with { Preflight = normal with { WinSxSWarning = "访问被拒绝" } }).CanProceed, "WinSxS 基础访问失败不强行开始");
        Check(!Report(input with { Preflight = normal with { Error = "注册表不可读" } }).CanProceed, "环境查询失败不报告适合修复");
        Check(!Report(input with { ProtectionBlocker = "IFEO 正在拦截安装器" }).CanProceed, "运行库防护未解除时阻止");
        Check(!Report(input with { GameRunning = true }).CanProceed && !Report(input with { GameRunning = null }).CanProceed,
            "游戏运行或状态未知时不卸载");
        Check(!Report(input with { SystemFreeBytes = RuntimeRepairReadiness.MinimumFreeBytes - 1 }).CanProceed, "系统盘不足预留空间时阻止");
        Check(!Report(input with { TempFreeBytes = RuntimeRepairReadiness.MinimumFreeBytes - 1 }).CanProceed, "TEMP 在其他盘也独立检查空间");
        Check(!Report(input with { TempFreeBytes = null }).CanProceed, "磁盘空间未知不能当作通过");
        Check(!Report(input with { TempWriteError = "写入被拒绝" }).CanProceed, "临时文件写入失败先处理");
        Check(!Report(input with { DiagnosticWriteError = "诊断目录只读" }).CanProceed, "无法保存诊断时不执行不可回滚卸载");
        Check(!Report(input with { QueryError = "防护状态无法读取" }).CanProceed, "额外状态查询失败保留未知分类");
        Check(Report(input with { ProtectionKnown = false }).Items.Single(i => i.Name == "运行库防护").Status == "unknown",
            "防护查询失败对应项目显示未知，不误显示通过");
        Check(Report(input with { PendingKnown = false }).Items.Single(i => i.Name == "安装事务").Status == "unknown",
            "安装事务查询失败对应项目显示未知");
        Check(Report(input with { EndedUnknown = true }).CanProceed && Report(input with { EndedUnknown = true }).HasWarnings,
            "旧安装器确已退出时保留提醒，不制造停止与核对的自锁");
        Check(normal.Blocker is null && (normal with { ServicingRebootPending = true }).Blocker is not null,
            "底层执行前也识别系统维护重启门禁");
        Check(!new RuntimeRepairEnvironmentReport([]).CanProceed, "空报告不能报告适合修复");
        var restored = JsonSerializer.Deserialize<RuntimeRepairEnvironmentReport>(JsonSerializer.Serialize(Report(input)))!;
        Check(restored.CanProceed && restored.Items.Count == Report(input).Items.Count, "后端报告序列化后仍按检查项计算能否继续");
        Check(BackendModules.IsAllowed(BackendModules.Runtime, BackendCommands.RuntimeRepairEnvironment)
            && BackendModules.IsReadOnly(BackendModules.Runtime, BackendCommands.RuntimeRepairEnvironment)
            && !BackendModules.RequiresWriteIntent(BackendModules.Runtime, BackendCommands.RuntimeRepairEnvironment),
            "预检命令只读，不因检测失败建立系统修改恢复义务");
        Check(Report(input).Details.Contains("不能保证安装一定成功") && Report(input).Details.Contains("自动修改服务"),
            "清楚区分基础环境检查和安装成功保证");
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-readiness-records-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var ioRoot = Path.Combine(root, "io"); Directory.CreateDirectory(ioRoot);
            var sentinel = Path.Combine(ioRoot, "unrelated.txt"); File.WriteAllText(sentinel, "preserve");
            Check(RuntimeRepairReadiness.ProbeWrite(ioRoot).Length == 0 && Directory.GetFiles(ioRoot).Length == 1
                && File.ReadAllText(sentinel) == "preserve", "真实专用临时文件写入回读后清理，保留无关文件");
            Check(RuntimeRepairReadiness.ProbeWrite(sentinel).Length > 0 && File.ReadAllText(sentinel) == "preserve",
                "目录被普通文件占用时明确失败，不删除无关文件");
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            var recordPath = Path.Combine(root, "runtime-repair-active.json");
            void Write(string stage, int pid, long ticks) => File.WriteAllText(recordPath, JsonSerializer.Serialize(new
            {
                Id = Guid.NewGuid().ToString("D"), Stage = stage, Pid = pid, StartTicks = ticks, Executable = Environment.ProcessPath,
                OwnerPid = self.Id, OwnerStartTicks = self.StartTime.ToUniversalTime().Ticks, CreatedUtc = DateTimeOffset.UtcNow,
            }));
            var runner = new RuntimeRepairProcessRunner(root);
            Write("running", int.MaxValue, 1);
            var original = File.ReadAllBytes(recordPath);
            Check(runner.InspectPending(false, out var ended) is null && ended && File.ReadAllBytes(recordPath).SequenceEqual(original)
                && !Directory.Exists(Path.Combine(root, "runtime-repair-history")), "只读预检不归档、不修改旧安装事务证据");
            Check(runner.InspectPending(true, out ended) is not null && File.ReadAllBytes(recordPath).SequenceEqual(original),
                "即使原进程退出，MSI 仍忙时继续阻止并保留记录");
            Check(runner.CheckPending(false) is null && !File.Exists(recordPath)
                && File.ReadAllBytes(Directory.GetFiles(Path.Combine(root, "runtime-repair-history")).Single()).SequenceEqual(original),
                "正式执行阶段才沿用既有归档流程，保留原始字节");
            Write("prepared", 0, 0); original = File.ReadAllBytes(recordPath);
            Check(runner.InspectPending(false, out ended) is not null && !ended && File.ReadAllBytes(recordPath).SequenceEqual(original),
                "准备阶段启动结果未知时预检阻止，不误认为已退出");
            Write("running", self.Id, self.StartTime.ToUniversalTime().Ticks);
            Check(runner.InspectPending(false, out ended) is not null && !ended, "实际在途夹具进程仍存活时阻止重复安装");
        }
        finally
        {
            var target = Path.GetFullPath(root);
            if (target.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(target).StartsWith("DeltaNFD-readiness-records-", StringComparison.Ordinal)) Directory.Delete(target, true);
        }
        Console.WriteLine($"Runtime environment checks: {count} passed. Pure simulated snapshots, no service/registry/installer writes.");
    }
}
