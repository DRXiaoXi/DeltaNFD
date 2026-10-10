using DeltaNFD.Backends;
using DeltaNFD.Services;

internal static class RuntimeStrongRepairChecks
{
    internal static Task<OperationResult> WithSystemPhaseAsync(string root,
        Func<IProgress<string>?, CancellationToken, Task<OperationResult>> verify,
        Func<IProgress<string>?, CancellationToken, Task<OperationResult>> repair)
        => new RuntimeStrongRepair(new Fake(), new RuntimeRepairDiagnostics(root)).RunAsync(verify, repair, null, default);

    internal static async Task RunAsync()
    {
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-strong-" + id.ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        async Task<OperationResult> Run(Fake fake, bool verified = true, CancellationToken token = default)
            => await new RuntimeStrongRepair(fake, new RuntimeRepairDiagnostics(root)).RunAsync(
                (_, ct) => { ct.ThrowIfCancellationRequested(); fake.Events.Add("verify"); return Task.FromResult(verified ? OperationResult.Ok("verified") : OperationResult.Fail("bad resource")); },
                (_, ct) => { ct.ThrowIfCancellationRequested(); fake.Events.Add("original"); return Task.FromResult(fake.Original); }, null, token);

        var healthy = new Fake(); var healthyResult = await Run(healthy);
        Check(healthyResult.Success && healthy.Events.SequenceEqual(new[] { "verify", "ready", "probe", "ComponentStore", "SystemFiles", "probe", "original" }),
            "校验资源后串行执行 DISM、SFC、复检和原修复，原流程只执行一次");
        Check(healthy.Timeouts[0] <= TimeSpan.FromMinutes(35) && healthy.Timeouts[1] <= TimeSpan.FromMinutes(25), "每个系统步骤服从共享预算");
        var cannotStartBefore = new Fake { BeforeProbeThrows = true };
        Check((await Run(cannotStartBefore)).Success && cannotStartBefore.Events.Contains("ComponentStore"),
            "修复前探测因运行库损坏无法启动时，仍允许系统修复，修复后必须再次探测通过");
        var resource = new Fake(); Check(!(await Run(resource, false)).Success && resource.Events.SequenceEqual(new[] { "verify" }), "资源不完整时不执行系统修复或卸载");
        var blocked = new Fake { Ready = false }; Check(!(await Run(blocked)).Success && !blocked.Events.Contains("ComponentStore"), "系统服务预检阻止任何修改");
        var dism = new Fake { Dism = new(1603, "cannot restore") };
        Check(!(await Run(dism)).Success && !dism.Events.Contains("SystemFiles") && !dism.Events.Contains("original"), "DISM 失败时停止，不卸载 C++");
        var sfc = new Fake { Sfc = new(0, "unrepaired", PostcheckPassed: false) };
        Check(!(await Run(sfc)).Success && !sfc.Events.Contains("original"), "SFC 退出零但结果核验失败也不能继续");
        var unknown = new Fake { Dism = new(null, "still running", OutcomeUnknown: true) };
        Check((await Run(unknown)).OutcomeUnknown && !unknown.Events.Contains("SystemFiles"), "超时／取消后保留未知事务，不启动下一阶段");
        var reboot = new Fake { Dism = new(3010) }; var rebootResult = await Run(reboot);
        Check(!rebootResult.Success && rebootResult.RequiresReboot && !reboot.Events.Contains("original"), "系统组件要求重启时不伪报完整修复完成");
        var newRestart = new Fake { RestartAfter = new(true, false, []) };
        var newRestartResult = await Run(newRestart);
        Check(!newRestartResult.Success && newRestartResult.RequiresReboot && !newRestart.Events.Contains("SystemFiles")
            && !newRestart.Events.Contains("original"), "DISM 返回零但新增 CBS 重启状态时停止后续修改");
        var priorComponent = new Fake { RestartBefore = new(true, false, []) };
        Check((await Run(priorComponent)).RequiresReboot && !priorComponent.Events.Contains("ComponentStore"), "已有组件维护待重启时不启动系统修复");
        var unrelated = new RuntimeSystemRestartState(false, true, ["old-source\nold-destination"]);
        var unchanged = new Fake { RestartBefore = unrelated, RestartAfter = unrelated };
        Check((await Run(unchanged)).Success, "其他软件已有的重启标记不永久阻止强力修复");
        var addedRename = new Fake { RestartBefore = unrelated,
            RestartAfter = unrelated with { PendingRenames = ["old-source\nold-destination", "new-source\nnew-destination"] } };
        Check((await Run(addedRename)).RequiresReboot && !addedRename.Events.Contains("original"), "已有文件重命名记录时仍识别新增重启义务");
        var update = new Fake { RestartAfter = new(false, true, []) };
        Check((await Run(update)).RequiresReboot && !update.Events.Contains("original"), "新增 Windows Update 重启标记不遗漏");
        var rebootImmediate = new Fake { Sfc = new(1641) };
        Check((await Run(rebootImmediate)).RequiresReboot && !rebootImmediate.Events.Contains("original"), "发起重启停止后续卸载");
        var broken = new Fake { After = [new("ucrt", false, "corrupt")] };
        Check(!(await Run(broken)).Success && !broken.Events.Contains("original"), "系统修复后契约抽查失败仍阻止卸载");
        var missing = new Fake { After = [] }; Check(!(await Run(missing)).Success, "空抽查结果不可作为通过");
        var changed = new Fake { After = [new("wrong identity", true, "")] }; Check(!(await Run(changed)).Success, "抽查项目漂移不可通过");
        var duplicate = new Fake { After = [new("ucrt", true, ""), new("ucrt", true, "")] }; Check(!(await Run(duplicate)).Success, "重复抽查项目不可通过");
        var normalFailure = new Fake { Original = new() { Success = false, IsPartial = true, RequiresReboot = true, Message = "cpp failed" } };
        var failed = await Run(normalFailure);
        Check(!failed.Success && failed.IsPartial && failed.RequiresReboot && failed.Message.Contains("cpp failed"), "保留原修复的部分失败和重启语义");
        var normalUnknown = new Fake { Original = new() { OutcomeUnknown = true, Message = "live MSI" } };
        Check((await Run(normalUnknown)).OutcomeUnknown, "保留原安装器的未知结果");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); var fake = new Fake();
            Check(!(await Run(fake, token: cancelled.Token)).Success && fake.Events.Count == 0, "取消请求不启动系统修复");
        }
        Check(RuntimeStrongRepair.TotalBudget < BackendModules.TimeoutFor(BackendModules.Runtime, BackendCommands.RuntimeRepair)
            && RuntimeStrongRepair.TotalBudget < BackendModules.TimeoutFor(BackendModules.Runtime, BackendCommands.RuntimeCompatibilityRepair), "两种后端超时均覆盖强力模式总预算");
        Check(!BackendJson.Deserialize<RuntimeRepairArgs>(System.Text.Json.JsonSerializer.SerializeToElement(new { })).StrongRepairMode
            && !BackendJson.Deserialize<RuntimeCompatibilityRepairArgs>(System.Text.Json.JsonSerializer.SerializeToElement(new { })).StrongRepairMode, "旧请求缺省关闭强力修复");
        Check(BackendJson.Deserialize<RuntimeRepairArgs>(System.Text.Json.JsonSerializer.SerializeToElement(new { strongRepairMode = true })).StrongRepairMode
            && BackendJson.Deserialize<RuntimeCompatibilityRepairArgs>(System.Text.Json.JsonSerializer.SerializeToElement(new { strongRepairMode = true })).StrongRepairMode, "两个 DTO 均传递同一开关");
        foreach (var output in new[] { "Windows Resource Protection did not find any integrity violations.",
            "Windows Resource Protection found corrupt files and successfully repaired them.",
            "Windows 资源保护未找到任何完整性冲突。", "Windows 资源保护找不到任何完整性冲突。",
            "Windows 资源保护找到了损坏文件并成功修复了它们。", "Windows 資源保護找不到任何完整性違規。",
            "W\0i\0n\0d\0o\0w\0s\0 资源保护找不到任何完整性冲突。" })
            Check(RuntimeStrongRepairEnvironment.SfcSummaryPassed(output), "支持的 SFC 完整性结果可核验");
        foreach (var output in new[] { "", "verification 100% complete", "Windows Resource Protection found corrupt files but was unable to fix some of them.",
            "Windows 资源保护找到了损坏文件，但无法修复其中某些文件。", "unknown localized result" })
            Check(!RuntimeStrongRepairEnvironment.SfcSummaryPassed(output), "空／未完成／失败／未识别 SFC 结果不伪报成功");
        var dismCommand = RuntimeStrongRepairEnvironment.Command(RuntimeSystemRepairStep.ComponentStore, root);
        var sfcCommand = RuntimeStrongRepairEnvironment.Command(RuntimeSystemRepairStep.SystemFiles, root);
        Check(Path.GetDirectoryName(dismCommand.FileName) == Environment.SystemDirectory && !dismCommand.UseShellExecute
            && dismCommand.ArgumentList.Contains("/NoRestart") && dismCommand.ArgumentList.Contains("/RestoreHealth"), "DISM 固定系统路径和参数，无自动重启");
        Check(Path.GetDirectoryName(sfcCommand.FileName) == Environment.SystemDirectory && sfcCommand.ArgumentList.SequenceEqual(new[] { "/scannow" })
            && !sfcCommand.UseShellExecute, "SFC 固定命令，无外部 DX 可执行文件或任意 DLL 替换");
        Check(!RuntimeStrongRepairEnvironment.ProbeUcrt(Path.Combine(root, "missing.dll"), "x86").Passed, "UCRT 物理文件缺失可检出");
        File.WriteAllText(Path.Combine(root, "corrupt.dll"), "fixture");
        Check(!RuntimeStrongRepairEnvironment.ProbeUcrt(Path.Combine(root, "corrupt.dll"), "x64").Passed, "损坏 PE 头可检出");
        var actual = await new RuntimeStrongRepairEnvironment(new RuntimeRepairDiagnostics(root)).ProbeAsync(default);
        Check(actual.Count == 2 + RuntimeStrongRepairEnvironment.Contracts.Length && actual.All(p => p.Passed),
            "只读验证本机 x86/x64 UCRT 文件与 x64 API Sets 契约能被原生加载器正确检查：" + string.Join(";", actual.Where(p => !p.Passed)));
        var probeCommand = RuntimeSystemProbeProcess.Command(Environment.ProcessPath!, Guid.NewGuid());
        using (var child = System.Diagnostics.Process.Start(probeCommand)!)
        {
            var output = child.StandardOutput.ReadToEndAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            using var reply = System.Text.Json.JsonDocument.Parse(await output);
            Check(child.ExitCode == 0 && reply.RootElement.GetProperty("Pid").GetInt32() == child.Id && child.Id != Environment.ProcessId,
                "修复后的原生探测实际运行在独立新进程，不复用父进程模块缓存");
        }
        Check(RuntimeSystemProbeProcess.Run(["--runtime-system-probe", "invalid"]) == 2, "只读入口拒绝无效参数，不进入其他功能");
        Console.WriteLine($"强力修复隔离检查通过：{checks} 项；未运行 DISM、SFC 或真实运行库安装器。");
    }
    private sealed class Fake : IRuntimeStrongRepairEnvironment
    {
        internal List<string> Events { get; } = [];
        internal List<TimeSpan> Timeouts { get; } = [];
        internal bool Ready { get; init; } = true;
        internal RuntimeStepResult Dism { get; init; } = new(0);
        internal RuntimeStepResult Sfc { get; init; } = new(0);
        internal OperationResult Original { get; init; } = OperationResult.Ok("original repaired");
        internal RuntimeSystemRestartState RestartBefore { get; init; } = new(false, false, []);
        internal RuntimeSystemRestartState RestartAfter { get; init; } = new(false, false, []);
        private int _restartChecks;
        internal IReadOnlyList<RuntimeSystemProbe> After { get; init; } = [new("ucrt", true, "")];
        internal bool BeforeProbeThrows { get; init; }
        private int _probes;
        public Task<OperationResult> CheckReadyAsync(CancellationToken token)
        { Events.Add("ready"); return Task.FromResult(Ready ? OperationResult.Ok("ready") : OperationResult.Fail("blocked")); }
        public Task<RuntimeSystemRestartState> ReadRestartStateAsync(CancellationToken token)
            => Task.FromResult(_restartChecks++ == 0 ? RestartBefore : RestartAfter);
        public Task<IReadOnlyList<RuntimeSystemProbe>> ProbeAsync(CancellationToken token)
        {
            Events.Add("probe");
            if (++_probes == 1 && BeforeProbeThrows) throw new IOException("fixture: UCRT child cannot start");
            return Task.FromResult(_probes == 1 ? (IReadOnlyList<RuntimeSystemProbe>)[new("ucrt", true, "")] : After);
        }
        public Task<RuntimeStepResult> ExecuteAsync(RuntimeSystemRepairStep step, TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
        { Events.Add(step.ToString()); Timeouts.Add(timeout); return Task.FromResult(step == RuntimeSystemRepairStep.ComponentStore ? Dism : Sfc); }
    }
}
