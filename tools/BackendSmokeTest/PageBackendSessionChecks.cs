using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using DeltaNFD.Backends;
using DeltaNFD.Inspection;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using DeltaNFD.Services.TweakDb;

internal static class PageBackendSessionChecks
{
    private sealed class Worker(string module) : IPageBackendProcess
    {
        public string ModuleId => module;
        public bool ExitConfirmed { get; private set; }
        public bool RequiresAttention { get; set; }
        public bool FailStop { get; set; }
        public int Stops { get; private set; }
        public TaskCompletionSource? HoldStop { get; set; }
        public async Task StopForPageAsync()
        {
            Stops++;
            if (HoldStop is not null) await HoldStop.Task;
            if (FailStop) throw new IOException("isolated stop failure");
            ExitConfirmed = true;
        }
    }
    private static int _count;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); _count++; }
    private static async Task Rejected(Func<Task> action, string message)
    {
        try { await action(); } catch (PageBackendUnavailableException) { Check(true, message); return; }
        throw new Exception(message);
    }
    internal static async Task RunAsync()
    {
        _count = 0;
        var id = Guid.NewGuid(); var isolated = Path.Combine(FindRepo(), "_buildcheck", "page-lifecycle-20261022", "session-fixture-" + id.ToString("N"));
        Directory.CreateDirectory(isolated); File.WriteAllText(Path.Combine(isolated, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(isolated, id);
        await PageBusyStatusChecks.RunAsync(isolated, id);
        var stream = System.Threading.Channels.Channel.CreateUnbounded<BackendStatus>();
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = BackendCpuFrameService.ConsumeStatusAsync(stream.Reader.ReadAllAsync(), _ => accepted.TrySetResult(),
            () => stalled.TrySetResult(), TimeSpan.FromMilliseconds(30), CancellationToken.None);
        await stalled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(!accepted.Task.IsCompleted, "监控静默超时先标记状态未知，不凭旧结果解锁");
        await stream.Writer.WriteAsync(new BackendStatus("ready", null));
        await accepted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        stream.Writer.Complete(); await pump.WaitAsync(TimeSpan.FromSeconds(3));
        Check(true, "静默恢复沿用同一状态流，不重复启动监控");
        var manager = new PageBackendSessionManager();
        Check(manager.Current is null, "默认没有栏目会话");
        manager.Navigate("system");
        var unknown = manager.Current!;
        await Rejected(async () => { using var _ = await unknown.BeginStartAsync(default); }, "未知游戏状态不启动系统栏目后端");
        await unknown.Stopping;
        manager.ObserveDelta(false);
        using (await unknown.BeginStartAsync(default)) { }
        Check(unknown.IsAvailable, "确认未运行后允许显式启动");
        manager.Navigate("ace");
        var ace = manager.Current!; var worker = new Worker(BackendModules.Ace);
        using (await ace.BeginStartAsync(default)) ace.Attach(worker);
        var command = ace.BeginCommand(worker);
        manager.ObserveDelta(true);
        Check(!ace.IsAvailable && ace.QueueToken.IsCancellationRequested, "游戏启动立即关闭新请求和排队准入");
        Check(worker.Stops == 0, "不取消或强杀已提交任务");
        await Rejected(() => { using var _ = ace.BeginCommand(worker); return Task.CompletedTask; }, "游戏中拒绝第二条命令");
        command.Dispose(); await ace.Stopping.WaitAsync(TimeSpan.FromSeconds(3));
        Check(worker.Stops == 1 && worker.ExitConfirmed, "当前请求完成后恰好停止一次");
        manager.ObserveDelta(false);
        Check(worker.Stops == 1, "游戏退出不会自动重启或停止新的执行者");
        manager.Navigate("shader");
        await Rejected(async () => { using var _ = await ace.BeginStartAsync(default); }, "旧页面迟到回调无法重启");

        var shader = manager.Current!; var shaderWorker = new Worker(BackendModules.System);
        using (await shader.BeginStartAsync(default)) shader.Attach(shaderWorker);
        var gate = PageBackendSessions.ModuleGate(BackendModules.System);
        await gate.WaitAsync();
        var queued = PageBackendSessions.WaitGateAsync(gate, shader, default, BackendModules.System);
        manager.Navigate("dashboard");
        await Rejected(() => queued, "离页撤销正在等待资源的排队请求");
        gate.Release(); await shader.Stopping.WaitAsync(TimeSpan.FromSeconds(3));
        Check(shaderWorker.ExitConfirmed, "取消排队请求后仍能排空");

        var a = new PageBackendLease("system"); var slow = new Worker(BackendModules.System) { HoldStop = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using (await a.BeginStartAsync(default)) a.Attach(slow);
        a.Suspend("left", true);
        var b = new PageBackendLease("shader");
        var second = PageBackendSessions.WaitGateAsync(gate, b, default, BackendModules.System);
        await Task.Delay(30);
        Check(!second.IsCompleted, "新栏目等待旧会话恢复和退出完成");
        slow.HoldStop.SetResult(); await a.Stopping.WaitAsync(TimeSpan.FromSeconds(3));
        await second.WaitAsync(TimeSpan.FromSeconds(3)); gate.Release();
        Check(slow.Stops == 1, "旧会话排空不与新会话并发");

        var gpu = new PageBackendLease("spoof"); var spoofWorker = new Worker(BackendModules.Gpu);
        var nvidia = new PageBackendLease("nvsettings"); var nvWorker = new Worker(BackendModules.Gpu);
        using (await gpu.BeginStartAsync(default)) gpu.Attach(spoofWorker);
        using (await nvidia.BeginStartAsync(default)) nvidia.Attach(nvWorker);
        gpu.Suspend("game", true); await gpu.Stopping.WaitAsync(TimeSpan.FromSeconds(3));
        Check(spoofWorker.ExitConfirmed && nvWorker.Stops == 0, "关闭显卡伪装不关闭 NVIDIA 会话");
        using (nvidia.BeginCommand(nvWorker)) { }
        Check(nvidia.IsAvailable, "NVIDIA 页仍然可用");
        nvidia.Suspend("left", true); await nvidia.Stopping;

        var failed = new PageBackendLease("runtime"); var bad = new Worker(BackendModules.Runtime) { FailStop = true };
        using (await failed.BeginStartAsync(default)) failed.Attach(bad);
        failed.Suspend("game"); await failed.Stopping.WaitAsync(TimeSpan.FromSeconds(3)); failed.Resume();
        Check(failed.Status.Contains("待核对"), "退出失败不包装成已关闭");
        await Rejected(async () => { using var _ = await failed.BeginStartAsync(default); }, "退出失败禁止自动重启");
        await Rejected(() => { PageBackendSessions.CheckBeforeStart(BackendModules.Runtime, new PageBackendLease("runtime")); return Task.CompletedTask; }, "换页重进也不能绕过旧后端未退出");
        bad.FailStop = false; await bad.StopForPageAsync();
        failed.RefreshReviewedExit();
        Check(failed.IsAvailable && failed.Status == "栏目后端已关闭", "核对退出后清除旧提示，不重启后端");
        PageBackendSessions.CheckBeforeStart(BackendModules.Runtime, new PageBackendLease("runtime"));
        Check(true, "确认旧进程退出后可恢复新会话准入");
        var uncertain = new PageBackendLease("runtime"); var pending = new Worker(BackendModules.Runtime) { RequiresAttention = true };
        using (await uncertain.BeginStartAsync(default)) uncertain.Attach(pending);
        uncertain.Suspend("left", true); await uncertain.Stopping;
        Check(uncertain.Status.Contains("待核对") && pending.RequiresAttention, "进程退出不能抹掉未知结果");
        await Rejected(() => { PageBackendSessions.CheckBeforeStart(BackendModules.Runtime, new PageBackendLease("runtime")); return Task.CompletedTask; }, "未知结果阻止重新进页自动启动");
        PageBackendSessions.CheckBeforeStart(BackendModules.Runtime, null);
        Check(pending.RequiresAttention, "专用恢复通道可启动核对但不清除证据");
        pending.RequiresAttention = false;
        PageBackendSessions.CheckBeforeStart(BackendModules.Runtime, null);

        manager.ObserveDelta(false); manager.Navigate("system"); var inspect = manager.Current!;
        manager.ObserveDelta(true);
        Check(!inspect.IsAvailable && manager.Current == inspect, "系统优化保持当前页面并暂停后端");
        manager.ObserveDelta(null);
        Check(!inspect.IsAvailable, "监控断开不错误解锁");
        await inspect.Stopping; manager.ObserveDelta(false);
        Check(inspect.IsAvailable, "监控恢复后允许用户显式操作");
        manager.Navigate(""); await inspect.Stopping;
        AuditInspectionAssembly();
        Check(typeof(OptimizationScan).GetProperty("Score") is null, "已取消评分机制");
        var now = DateTimeOffset.UtcNow;
        Check(PageBackendSessions.ReadDeltaObservation(new() { UpdatedUtc = now, MonitorRunning = true, IsCustomTarget = true, GameRunning = false, DeltaForceRunning = true, DeltaForceObservedUtc = now }, now) == true, "自定义目标不能掩盖三角洲进程");
        Check(PageBackendSessions.ReadDeltaObservation(new() { UpdatedUtc = now, MonitorRunning = true, GameRunning = true, DeltaForceRunning = false, DeltaForceObservedUtc = now }, now) == false, "其他游戏不触发三角洲限制");
        Check(PageBackendSessions.ReadDeltaObservation(new() { UpdatedUtc = now, MonitorRunning = false, DeltaForceRunning = false, DeltaForceObservedUtc = now }, now) == false, "自动执行停止仍采信独立新鲜观测");
        Check(PageBackendSessions.ReadDeltaObservation(new() { UpdatedUtc = now, MonitorRunning = true, DeltaForceRunning = false, DeltaForceObservedUtc = now.AddSeconds(-11) }, now) is null, "过期状态不能解锁");
        Check(PageBackendSessions.ReadDeltaObservation(new() { UpdatedUtc = now, MonitorRunning = true, DeltaForceRunning = false, DeltaForceObservedUtc = now.AddSeconds(5) }, now) is null, "未来时间不能解锁");
        Check(PageBackendSessions.ReadDeltaObservation(new() { MonitorRunning = true }, now) is null, "旧后端缺字段不能冒充未运行");
        var immediate = new PageBackendLease("system");
        await Rejected(() => { PageBackendSessions.RecheckPage(immediate, () => true); return Task.CompletedTask; }, "发送前复核封住两次共享监控之间的启动窗口");
        await immediate.Stopping; PageBackendSessions.Manager.ObserveDelta(false);
        BackendUnconfirmedStore.SavePageStopFailure(BackendModules.Ace, int.MaxValue, 1, Environment.ProcessPath!, Guid.NewGuid());
        var stopRecord = BackendUnconfirmedStore.ReviewAll().Single(r => r.Module == BackendModules.Ace);
        Check(stopRecord.CanAcknowledge && !stopRecord.CanRestore && stopRecord.Description.Contains("退出未确认"), "只读会话退出失败有专用核对记录，不冒充系统恢复义务");
        BackendUnconfirmedStore.AcknowledgeCurrentState(stopRecord);
        Check(!BackendUnconfirmedStore.PendingModules().Contains(BackendModules.Ace), "只读退出记录核对后归档，未执行还原");
        Console.WriteLine($"栏目会话检查通过：{_count} 项；没有修改真实系统或结束外部进程。");
    }
    private static void AuditInspectionAssembly()
    {
        var assembly = typeof(BxInspection).Assembly;
        Check(!assembly.GetReferencedAssemblies().Any(a => a.Name is "DeltaNFD.Execution" or "DeltaNFD"), "只读库不引用前端或执行库");
        var opcodes = typeof(OpCodes).GetFields().Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => unchecked((ushort)o.Value));
        foreach (var type in assembly.GetTypes())
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly).Cast<MethodBase>().Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)))
        {
            if (method.GetCustomAttribute<System.Runtime.InteropServices.DllImportAttribute>() is { } import)
                Check(!new[] { "SetSecurityInfo", "RegSetValue", "MsiInstallProduct", "MsiConfigureProduct", "EmptyWorkingSet", "NtSetSystemInformation" }.Any(n => (import.EntryPoint ?? method.Name).StartsWith(n, StringComparison.Ordinal)), "只读库没有写入 native 导出：" + method.Name);
            var il = method.GetMethodBody()?.GetILAsByteArray(); if (il is null) continue;
            for (var i = 0; i < il.Length;)
            {
                ushort key = il[i++]; if (key == 0xfe) key = (ushort)(0xfe00 | il[i++]);
                var op = opcodes[key];
                var size = op.OperandType switch { OperandType.InlineNone => 0, OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1, OperandType.InlineVar => 2, OperandType.InlineI8 or OperandType.InlineR => 8, OperandType.InlineSwitch => 4 + BitConverter.ToInt32(il, i) * 4, _ => 4 };
                if (op.OperandType == OperandType.InlineMethod)
                {
                    MethodBase? called = null;
                    try { called = method.Module.ResolveMethod(BitConverter.ToInt32(il, i), type.GenericTypeArguments, method.IsGenericMethod ? method.GetGenericArguments() : null); } catch { }
                    if (called?.DeclaringType is { } declaring &&
                        (declaring == typeof(File) && new[] { "Delete", "Move", "Copy", "SetAttributes", "WriteAllText", "WriteAllBytes" }.Contains(called.Name)
                        || declaring == typeof(Microsoft.Win32.RegistryKey) && new[] { "SetValue", "CreateSubKey", "DeleteSubKey", "DeleteSubKeyTree", "DeleteValue" }.Contains(called.Name)))
                        throw new Exception("只读库意外调用写操作：" + declaring.FullName + "." + called.Name);
                }
                i += size;
            }
        }
        Check(true, "只读程序集全部方法 IL 未调用注册表/文件写操作");
    }

    internal static async Task RunLiveInspectionAsync()
    {
        _count = 0;
        var repo = FindRepo(); var id = Guid.NewGuid();
        var root = Path.Combine(repo, "_buildcheck", "page-lifecycle-20261022", "live-inspection-" + id.ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        BxCatalog.ConfigureAssetRoot(Path.Combine(repo, "src", "DeltaNFD"));
        var before = BackendSessions.Snapshot().Count;
        var timer = Stopwatch.StartNew();
        var first = OptimizationScan.RunIfNotScannedAsync();
        Check(ReferenceEquals(first, OptimizationScan.RunIfNotScannedAsync()), "启动预读去重");
        await first.WaitAsync(TimeSpan.FromMinutes(3));
        Check(OptimizationScan.CompletedTime is not null && !OptimizationScan.IsScanning, "启动预读完成且不阻塞调用者");
        foreach (var section in BxCatalog.Sections.Where(s => s.JsonFile is not null))
            foreach (var item in BxCatalog.Database.GetValueOrDefault(section.Id) ?? [])
                Check(OptimizationScan.TryGetItemState(section.Id, item.Name, out _), "已预读：" + section.Id + "/" + item.Name);
        var status = await new BackendSystemTweakService().GetStatusesAsync();
        Check(status.Count > 0 && status.Where(s => !s.StateKnown).All(s => !s.IsOptimized), "未知深度状态不会视为已优化");
        if (!DeltaForceLocator.IsGameRunning())
        {
            await new BackendShaderService().GetStatusAsync();
            await new BackendNvProfileService().GetEnvironmentAsync();
            await new BackendAceService().ScanAsync();
            var core = await new BackendAceService().CheckCoreFilesAsync();
            Check(core.Level != AceCoreCheckLevel.Abnormal, "ACE 游戏归属检测不因多个驱动文件报异常");
            await new BackendRuntimeGuardService().GetStatusAsync();
            await new BackendRuntimeGuardService().Scan14Async();
        }
        Check(BackendSessions.Snapshot().Count == before, "启动预读和组件体检未启动任何栏目后端");
        var unexpected = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(p => !p.EndsWith(".log", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(p) != "isolated-test.marker").ToArray();
        Check(unexpected.Length == 0, "只读检测未生成配置、备份或修复记录：" + string.Join(",", unexpected));
        AuditInspectionAssembly();
        Console.WriteLine($"真实前端只读检查通过：{_count} 项；预读与体检合计 {timer.Elapsed.TotalSeconds:0.00} 秒；证据 {root}");
    }
    private static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null) { if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")) && Directory.Exists(Path.Combine(dir.FullName, "src"))) return dir.FullName; dir = dir.Parent; }
        throw new DirectoryNotFoundException("repository");
    }
}
