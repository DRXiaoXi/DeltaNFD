using System.Diagnostics;
using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

internal static class RuntimeRepairFlowChecks
{
    internal static async Task RunAsync()
    {
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD_RuntimeFlow_" + id.ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        async Task<OperationResult> Run(Fake env, RuntimeRepairMode mode = RuntimeRepairMode.FullReinstall,
            CancellationToken token = default, TimeSpan? budget = null)
            => await new RuntimeRepairCoordinator(env, new RuntimeRepairDiagnostics(root), budget).RunAsync(mode, null, token);

        var stopped = Fake.Healthy();
        Check(stopped.Blocker is null, "Stopped 的按需服务不是失败");
        foreach (var bad in new[] {
            stopped with { Services = [new("msiserver",4,1),new("TrustedInstaller",3,1)] },
            stopped with { Services = [new("msiserver",3,1),new("TrustedInstaller",4,1)] },
            stopped with { InstallerBusy = true }, stopped with { Error = "read denied" },
            stopped with { Services = [] } })
        {
            var fake = new Fake { Status = bad };
            var result = await Run(fake);
            Check(!result.Success && fake.Calls.Count == 0 && fake.Verifications == 0, "预检拒绝后无安装/卸载");
        }
        var invalidMode = new Fake();
        Check(!(await Run(invalidMode, (RuntimeRepairMode)99)).Success && invalidMode.Calls.Count == 0, "未知模式拒绝");
        var badPayload = new Fake { Verified = false };
        Check(!(await Run(badPayload)).Success && badPayload.Calls.Count == 0, "损坏安装资源不卸载");
        var badUninstallPlan = new Fake { PrepareFailed = true };
        Check(!(await Run(badUninstallPlan)).Success && badUninstallPlan.Calls.Count == 0, "关联产品预检失败先于任何卸载");
        var residue = new Fake { KeepInstalled = true };
        Check(!(await Run(residue)).Success && residue.Calls.Count == 1 && residue.Calls[0].Kind == "uninstall",
            "内部受控修复也必须通过卸载复检才能启动安装");
        var scan = Fake.Missing();
        await RuntimeBetaRepairChecks.RunAsync(root, Check);
        RuntimeScanClassificationChecks.Run(Check);
        var full = RuntimeRepairPlanner.Build(scan, RuntimeRepairMode.FullReinstall);
        Check(full[0].Kind == "uninstall" && full.Any(s => s.Kind == "msi"), "全量模式保留卸载与全系列安装");
        var legacy = RuntimeRepairPlanner.Build(scan, RuntimeRepairMode.Legacy2005And2008);
        Check(legacy[0].Kind == "uninstall" && legacy.Where(s => s.Kind != "uninstall").All(s => s.Branch.StartsWith("2005") || s.Branch.StartsWith("2008")) && legacy.Count(s => s.Kind == "msi") == 4, "旧分支内部流程也先卸载选定分支，再安装四个包");
        await RuntimeMsiInstallerChecks.RunAsync(root, Check);
        var preexistingReboot = new Fake { Status = stopped with { RebootPending = true } };
        var preexistingResult = await Run(preexistingReboot);
        Check(preexistingResult.Success && !preexistingResult.RequiresReboot, "既有系统待重启记录不冒充本次组件安装重启请求");
        var missing = RuntimeRepairPlanner.Build(scan, RuntimeRepairMode.MissingOrOutdated);
        Check(missing[0].Kind == "uninstall" && missing.Where(s => s.Kind != "uninstall").All(s => s.Branch == "2005 (x86)"), "缺失模式先卸载目标分支，不触碰其他分支");
        var none = new Fake { Before = Fake.Clean() };
        Check((await Run(none, RuntimeRepairMode.MissingOrOutdated)).IsSkipped && none.Calls.Count == 0, "无缺失不执行");
        foreach (var code in new[] { 1618, 1603, 1612, 1638 })
        {
            var fail = new Fake { Next = new(code, "failure") };
            Check(!(await Run(fail)).Success && fail.Calls.Count == 1, "卸载关键失败停止后续：" + code);
        }
        var unknown = new Fake { Next = new(null, "still running", OutcomeUnknown: true) };
        var unknownResult = await Run(unknown);
        Check(!unknownResult.Success && unknownResult.OutcomeUnknown && unknown.Calls.Count == 1, "超时未知不启动后续并保留状态");
        var skip = new Fake { Next = new(1605, Skipped: true) };
        Check((await Run(skip)).Success && skip.Calls.Count > 1, "不存在的 MSI 安全跳过");
        var reboot = new Fake { Next = new(3010) };
        Check((await Run(reboot)).RequiresReboot, "重启标志聚合");
        var immediate = new Fake { Next = new(1641) };
        Check(!(await Run(immediate)).Success && immediate.Calls.Count == 1, "已发起重启停止后续");
        var version = new Fake { After = Fake.Missing() };
        var versionResult = await Run(version, RuntimeRepairMode.Legacy2005And2008);
        Check(!versionResult.Success && versionResult.Message.Contains("检测基线"), "退出 0 与检测不达标分开提示");
        var unrelated = new Fake { After = new() { All = [], Abnormal = [], Advice = "", Suboptimal = [new() { Branch = "2013 (x64)", RecommendedVersion = "12.0.40664.0" }] } };
        Check((await Run(unrelated, RuntimeRepairMode.Legacy2005And2008)).Success, "分支验收不冒充全局或失败于未选分支");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var cancelFake = new Fake();
        Check(!(await Run(cancelFake, token: cancelled.Token)).Success && cancelFake.Calls.Count == 0, "起始取消不执行");
        var timeoutFake = new Fake { Wait = true };
        Check(!(await Run(timeoutFake, budget: TimeSpan.FromMilliseconds(30))).Success && timeoutFake.Calls.Count == 1, "总预算到期无后续");
        Check(RuntimeRepairCoordinator.TotalBudget < BackendModules.TimeoutFor(BackendModules.Runtime, BackendCommands.RuntimeRepair), "内部预算小于 IPC 上限");
        Check(RuntimeRepairErrorClassifier.Describe(1603,"1935 HRESULT: 0x80070422").Contains("服务"), "服务错误不误报权限");
        Check(RuntimeRepairErrorClassifier.Describe(1603,"1935 HRESULT: 0x80070005").Contains("访问被拒绝"), "权限错误独立分类");
        Check(RuntimeRepairErrorClassifier.Describe(1603,"1935").Contains("底层 HRESULT"), "1935 不自动等同权限");
        Check(RuntimeRepairErrorClassifier.Describe(1603,"HRESULT: 0x80073712").Contains("组件存储"), "组件损坏独立诊断");
        Check(RuntimeRepairErrorClassifier.Describe(1620,"").Contains("安装包"), "包损坏独立诊断");
        Check(RuntimeRepairErrorClassifier.IsVersionConflict(unchecked((int)0x80070666)), "支持 EXE 返回的 HRESULT 版本冲突");
        var conflict = new Fake { BaselineResult = new(unchecked((int)0x80070666)) };
        Check((await Run(conflict)).Success && conflict.Calls.Count > 1, "基线版本冲突仍要求最终复检而非立即伪报成功");
        var conflictMissing = new Fake { BaselineResult = new(unchecked((int)0x80070666)), After = Fake.Missing() };
        Check(!(await Run(conflictMissing)).Success, "基线冲突且复检缺失必须失败");
        foreach (var code in new[] { "backend.timeout", "backend.cancel-unconfirmed", "backend.transport-unconfirmed", "backend.disconnected" })
            Check(BackendRuntimeGuardService.Map(BackendResult.Failed(code,"unknown"), "repair").OutcomeUnknown, "前端保留传输未知状态：" + code);
        var wire = System.Text.Json.JsonSerializer.SerializeToElement(new OperationResult { Success = false, OutcomeUnknown = true, RequiresReboot = true, Message = "fixture" }, BackendJson.SerializerOptions);
        Check(BackendRuntimeGuardService.Map(new BackendResult(BackendResultStatuses.Failed, "runtime.repair", "fixture", [], true, wire), "repair")
            is { Success: false, OutcomeUnknown: true, RequiresReboot: true }, "失败结果的未知与重启标志通过合同回读");
        Check(!BackendRuntimeGuardService.Map(BackendResult.Ok("fixture", "", OperationResult.Ok("fixture")) with { Status = BackendResultStatuses.Failed }, "repair").Success,
            "前端不把矛盾的失败状态与成功 data 映射为成功");
        var diagnosticFixture = new RuntimeRepairDiagnostics(root);
        var diagnosticLog = Path.Combine(diagnosticFixture.DirectoryPath, "fixture.log");
        File.WriteAllLines(diagnosticLog, new[] { "Error 1935 HRESULT: 0x80070422" }.Concat(Enumerable.Repeat("MainEngineThread returning 0x00000000", 40)));
        Check(diagnosticFixture.ReadErrorDetails(diagnosticLog).Contains("0x80070422"), "关键程序集错误不被后续状态行挤出摘要");
        var source = Path.Combine(Directory.GetCurrentDirectory(), "src", "DeltaNFD", "Assets", "VCRedistRepair");
        foreach (var year in new[] { "2005", "2008", "2010", "2012", "2013", "2026" })
        foreach (var arch in new[] { "x64", "x86" })
        {
            var directory = Path.Combine(source, year, arch);
            if (!Directory.Exists(directory)) continue;
            var inf = Directory.GetFiles(directory, "*.inf").Single();
            var expected = RuntimeUninstallCommand.ReadQuietInfProducts(inf);
            foreach (var msi in Directory.GetFiles(directory, "*.msi"))
            {
                var product = RuntimeMsiMetadata.ReadProductCode(msi);
                Check(expected.Contains(product, StringComparer.OrdinalIgnoreCase), "只读 MSI 产品码与对应 INF 相符：" + msi);
                var package = RuntimeMsiMetadata.ReadPackage(msi);
                Check(package.Product.Equals(product, StringComparison.OrdinalIgnoreCase) && package.Architecture == arch
                    && package.Publisher.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
                    && Version.TryParse(package.Version, out _) && package.Files.Count > 0,
                    "只读 MSI 包身份、架构和可核对 DLL 元数据完整：" + msi);
            }
        }
        var changedEnvironment = new Fake { ChangeStatusAfterStep = true };
        Check(!(await Run(changedEnvironment)).Success && changedEnvironment.Calls.Count == 1, "运行中出现事务冲突停止后续");

        // Real process lifecycle test: only this smoke executable, never an installer.
        var activeRoot = Path.Combine(root, "owned-process");
        var runner = new RuntimeRepairProcessRunner(activeRoot, () => false);
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--runtime-flow-child");
        var processResult = await runner.RunAsync(info, "owned fixture", TimeSpan.FromMilliseconds(60), null, default);
        Check(processResult.OutcomeUnknown && File.Exists(Path.Combine(activeRoot, "runtime-repair-active.json")), "实际测试进程超时不强杀且记录保留");
        Check(runner.CheckPending(false) is not null, "仍存活时拒绝重试");
        using (var activity = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(activeRoot, "runtime-repair-active.json"))))
        {
            try
            {
                using var owned = Process.GetProcessById(activity.RootElement.GetProperty("Pid").GetInt32());
                await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (ArgumentException) { /* Already exited; never terminate it to satisfy the test. */ }
        }
        Check(runner.CheckPending(true) is not null, "进程退出但 Installer 忙仍阻止");
        var pendingAfterExit = runner.CheckPending(false);
        Check(pendingAfterExit is null && Directory.GetFiles(Path.Combine(activeRoot, "runtime-repair-history")).Length == 1,
            "确认退出归档未知证据，不伪报旧操作成功：" + pendingAfterExit);
        File.WriteAllText(Path.Combine(activeRoot, "runtime-repair-active.json"), "{broken");
        Check(runner.CheckPending(false) is not null, "损坏记录不覆盖");
        var page = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "src", "DeltaNFD", "Views", "RuntimePage.xaml"));
        var model = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "src", "DeltaNFD", "ViewModels", "RuntimeViewModel.cs"));
        var serviceText = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "src", "DeltaNFD.Execution", "Execution", "RuntimeGuardService.cs"));
        var runnerText = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "src", "DeltaNFD.Execution", "Execution", "RuntimeRepairProcessRunner.cs"));
        Check(!page.Contains("RepairScopeIndex") && page.Contains("完整 AIO") && page.Contains("CancelRepairButton_Click"), "UI 保留完整 AIO 单一入口与取消入口");
        Check(model.Contains("cancellation.Token") && model.Contains("!RepairCancelRequested"), "取消传入后端且迟到进度不覆盖取消提示");
        Check(!runnerText.Contains(".Kill("), "受控安装器不强杀事务");
        Check(serviceText.Contains("new RuntimeBetaRepair(") && !serviceText.Contains("new RuntimeRepairCoordinator("), "产品修复入口恢复 Beta 完整 AIO，不再走分支逐包安装");
        Console.WriteLine($"运行库受控流程检查通过：{count} 项；系统改写均为模拟，真实进程仅为本测试子进程。");
    }

    private sealed class Fake : IRuntimeRepairEnvironment
    {
        internal static RuntimePreflightSnapshot Healthy() => new([new("msiserver",3,1),new("TrustedInstaller",3,1)],false,false);
        internal static VcRedistScanReport Clean() => new() { All = [], Abnormal = [], Suboptimal = [], Advice = "" };
        internal static VcRedistScanReport Missing() => new() { All = [new() { DisplayName = "Microsoft Visual C++ 2005 Redistributable", Architecture="x86", KeyName="key", UninstallKind="Msi" }],
            Abnormal=[], Suboptimal=[new() { Branch="2005 (x86)", RecommendedVersion="8.0.61187", InstalledVersion="" }], Advice="" };
        internal RuntimePreflightSnapshot Status = Healthy();
        internal VcRedistScanReport Before = Missing(), After = Clean();
        internal bool Verified = true, Wait, ChangeStatusAfterStep, PrepareFailed, KeepInstalled;
        internal int Verifications, Scans;
        internal RuntimeStepResult? Next;
        internal RuntimeStepResult? BaselineResult;
        internal List<RuntimeRepairStep> Calls = [];
        public Task<RuntimePreflightSnapshot> PreflightAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Status); }
        public Task<IReadOnlyList<RuntimeRepairStep>> PrepareStepsAsync(IReadOnlyList<RuntimeRepairStep> steps, CancellationToken token)
        { if (PrepareFailed) throw new InvalidDataException("关联无法确认"); return Task.FromResult(steps); }
        public Task<VcRedistScanReport> ScanAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Scans++ switch { 0 => Before, 1 => KeepInstalled ? Before : Clean(), _ => After }); }
        public Task<OperationResult> VerifyAsync(IProgress<string>? progress, CancellationToken token) { Verifications++; return Task.FromResult(Verified ? OperationResult.Ok("verified") : OperationResult.Fail("bad payload")); }
        public async Task<RuntimeStepResult> ExecuteAsync(RuntimeRepairStep step, TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
        { Calls.Add(step); if (Wait) await Task.Delay(Timeout.Infinite, token); if (ChangeStatusAfterStep) Status = Status with { InstallerBusy = true }; var result = Next ?? (step.Kind == "exe" ? BaselineResult : null) ?? new(0); Next = null; return result; }
    }
}
