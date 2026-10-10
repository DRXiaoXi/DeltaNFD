using DeltaNFD.Services;

internal static class RuntimeBetaRepairChecks
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        async Task<OperationResult> Run(Fake fake, CancellationToken token = default, TimeSpan? budget = null) =>
            await new RuntimeBetaRepair(fake, new RuntimeRepairDiagnostics(root), budget).RunAsync(null, token);
        var normal = new Fake();
        check((await Run(normal)).Success && normal.Trace.SequenceEqual(new[] { "verify", "scan-before", "uninstall", "aio", "baseline-x64", "baseline-x86", "scan-after" }),
            "Beta0.9 主流程：完整校验→卸载条目→完整 AIO→两个官方基线→全量复检");
        var strong = new Fake();
        check((await RuntimeStrongRepairChecks.WithSystemPhaseAsync(root, strong.VerifyAsync,
            new RuntimeBetaRepair(strong, new RuntimeRepairDiagnostics(root)).RunAsync)).Success
            && strong.Trace.Count(s => s == "aio") == 1 && strong.Trace.Contains("scan-after"),
            "强力系统组件阶段与完整高帧性 AIO 流程集成后仍执行全量复检");
        var badResource = new Fake { Verified = false };
        check(!(await Run(badResource)).Success && badResource.Trace.SequenceEqual(new[] { "verify" }), "资源损坏时先拒绝，不卸载任何运行库");
        var failedUninstall = new Fake { Uninstall = new(1603, "fixture") };
        check(!(await Run(failedUninstall)).Success && failedUninstall.Trace.Contains("aio"), "沿用 Beta：普通卸载失败仍尝试 AIO，最终报告保留失败");
        var unknownUninstall = new Fake { Uninstall = new(null, "pending", OutcomeUnknown: true) };
        check((await Run(unknownUninstall)).OutcomeUnknown && !unknownUninstall.Trace.Contains("aio"), "未知卸载执行者未结束时不启动 AIO，不强杀");
        var unknownAio = new Fake { Aio = new(null, "pending", OutcomeUnknown: true) };
        check((await Run(unknownAio)).OutcomeUnknown && !unknownAio.Trace.Contains("baseline-x64"), "AIO 结果未知时停止后续基线并保留状态");
        var brokenAio = new Fake { Aio = new(1), Packages = [new("fixture.msi", 1603, "fixture.log", "failed")] };
        check(!(await Run(brokenAio)).Success && brokenAio.Trace.Contains("baseline-x86"), "Beta AIO 返回失败时仍核对基线，不能伪报整体成功");
        var missingCompletion = new Fake { MissingCompletion = true };
        check(!(await Run(missingCompletion)).Success, "AIO 完成记录缺失不能声称修复通过");
        var newerBaseline = new Fake { Baseline = new(1638) };
        check((await Run(newerBaseline)).Success, "官方基线较旧的版本冲突结合全量复检通过才可接受");
        var missing = new Fake { Baseline = new(1638), After = new() { All = [], Abnormal = [], Advice = "", Suboptimal = [new() { Branch = "2005 (x64)", RecommendedVersion = "8.0.61186" }] } };
        check(!(await Run(missing)).Success, "AIO 后全量分支仍缺失时失败，不能靠基线 1638 放行");
        var reboot = new Fake { Aio = new(3010) };
        check((await Run(reboot)).RequiresReboot, "Beta 流程聚合组件重启要求");
        var immediate = new Fake { Uninstall = new(1641) };
        check(!(await Run(immediate)).Success && !immediate.Trace.Contains("aio"), "安装器已发起重启时不继续改变系统");
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel(); var cancelled = new Fake();
            check(!(await Run(cancelled, cts.Token)).Success && cancelled.Trace.Count == 0, "起始取消不执行校验或安装");
        }
        var expired = new Fake();
        check(!(await Run(expired, budget: TimeSpan.Zero)).Success && !expired.Trace.Contains("aio"), "总预算耗尽不启动新的 AIO");

        var aio = RuntimeBetaRepairEnvironment.BuildAioCommand(Path.Combine(root, "payload 中文"), root);
        check(aio.Arguments.EndsWith("/quiet\"", StringComparison.Ordinal) && aio.Arguments.Contains("Installer.cmd")
            && aio.CreateNoWindow && !aio.UseShellExecute && aio.RedirectStandardOutput
            && aio.Environment["DELTANFD_REPAIR_LOG_DIR"] == root, "生产命令确实调用完整 Installer.cmd /quiet 并采集输出");
        var msi = RuntimeBetaRepairEnvironment.BuildUninstallCommand(new()
        {
            DisplayName = "Microsoft Visual C++ fixture", DisplayVersion = "1.0", Architecture = "x64",
            KeyName = "{12345678-1234-1234-1234-123456789ABC}", UninstallKind = "Msi",
            UninstallString = "msiexec /I{12345678-1234-1234-1234-123456789ABC}",
        }, Path.Combine(root, "uninstall.log"));
        check(msi.ArgumentList.Contains("/x") && msi.ArgumentList.Contains("/qn"), "Beta 卸载条目沿用 MSI 静默入口，不依赖新的缓存关联预检");
        var wrapper = RuntimeBetaRepairEnvironment.BuildUninstallCommand(new()
        {
            DisplayName = "Microsoft Visual C++ fixture", DisplayVersion = "1.0", Architecture = "x64", KeyName = "fixture",
            UninstallKind = "Bundle", UninstallString = "", QuietUninstallString = "\"C:\\ProgramData\\Package Cache\\fixture\\vc_redist.x64.exe\" /uninstall /quiet",
        }, "fixture.log");
        check(wrapper.FileName.EndsWith("vc_redist.x64.exe") && wrapper.ArgumentList.Contains("/quiet") && !wrapper.UseShellExecute,
            "Beta 包装卸载恢复登记的静默 EXE 入口，参数不交给命令 shell 执行");
        var source = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "src/DeltaNFD.Execution/Execution/RuntimeGuardService.cs"));
        var repair = source[source.IndexOf("private async Task<OperationResult> RepairVcRedistCoreAsync", StringComparison.Ordinal)..];
        repair = repair[..repair.IndexOf("internal static OperationResult VerifyRepairPackage", StringComparison.Ordinal)];
        check(repair.Contains("new RuntimeBetaRepair(") && !repair.Contains("new RuntimeRepairCoordinator(")
            && repair.Contains("mode != RuntimeRepairMode.FullReinstall"), "产品入口切到 Beta 引擎，旧局部请求不能被偷偷扩大为全量卸载");
        var protection = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "src/DeltaNFD.Execution/Execution/RuntimeGuardProtection.cs"));
        check(source.Contains("RuntimeGuardProtection.Exclusive") && protection.Contains("RepairBlocker"), "新版运行库防护与修复互斥保持原样");
    }
    private sealed class Fake : IRuntimeBetaRepairEnvironment
    {
        internal List<string> Trace = [];
        internal bool Verified = true, MissingCompletion;
        internal RuntimeStepResult Uninstall = new(0), Aio = new(0), Baseline = new(0);
        internal List<RuntimePackageResult> Packages = [new("fixture.msi", 0, "fixture.log", "")];
        internal VcRedistScanReport After = new() { All = [], Abnormal = [], Suboptimal = [], Advice = "" };
        private int _scans;
        public Task<OperationResult> VerifyAsync(IProgress<string>? progress, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Trace.Add("verify"); return Task.FromResult(Verified ? OperationResult.Ok("verified") : OperationResult.Fail("invalid")); }
        public Task<VcRedistScanReport> ScanAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Trace.Add(_scans == 0 ? "scan-before" : "scan-after");
            return Task.FromResult(_scans++ == 0 ? new VcRedistScanReport { Advice = "", Abnormal = [], Suboptimal = [], All =
                [new() { DisplayName = "fixture", DisplayVersion = "1.0", Architecture = "x64", KeyName = "fixture", UninstallKind = "Msi" }] } : After);
        }
        public Task<RuntimeStepResult> UninstallAsync(VcRedistEntry entry, TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
        { Trace.Add("uninstall"); return Task.FromResult(Uninstall); }
        public Task<RuntimeStepResult> InstallAioAsync(TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
        { Trace.Add("aio"); return Task.FromResult(Aio); }
        public Task<RuntimeStepResult> InstallBaselineAsync(string arch, TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
        { Trace.Add("baseline-" + arch); return Task.FromResult(Baseline); }
        public List<RuntimePackageResult> ReadAioResults() => MissingCompletion ? throw new InvalidDataException("missing completed marker") : Packages;
    }
}
