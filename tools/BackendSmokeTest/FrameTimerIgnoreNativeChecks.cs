using System.Diagnostics;
using System.Runtime.InteropServices;
using DeltaNFD.Execution.Procman;
using DeltaNFD.Native;

/// <summary>
/// 原生自检（Q5 已批准）：自建子进程用 ntdll NtSetTimerResolution 请求 0.5ms（游戏实际使用的系统级路径），
/// 父进程仅对该子进程设置/清除 0x4 位，核对系统读数随之回落/恢复。
/// 只操作本测试自己创建的临时子进程，不触碰真实游戏、反作弊或任何玩家进程。
/// </summary>
internal static class FrameTimerIgnoreNativeChecks
{
    private static int _checks;
    private static void Check(bool ok, string text) { if (!ok) throw new Exception(text); _checks++; }

    // Windows 10 2004 起 timeBeginPeriod 只影响调用进程，不改变系统读数；这里用真实系统级请求路径。
    [DllImport("ntdll.dll")]
    private static extern int NtSetTimerResolution(uint desiredResolution,
        [MarshalAs(UnmanagedType.U1)] bool setResolution, out uint currentResolution);

    /// <summary>子进程：请求 1ms 并保持，等待父进程结束它。</summary>
    internal static void Child()
    {
        var status = NtSetTimerResolution(5_000, true, out var actual);
        var current = ProcessPerf.QueryTimerResolutionMs();
        Console.Out.WriteLine("READY:" + status + ":" + actual + ":"
            + (current?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"));
        Console.Out.Flush();
        // 保持请求直到父进程结束本进程（进程退出时内核自动释放该请求）。
        Thread.Sleep(Timeout.Infinite);
    }

    internal static void Run()
    {
        _checks = 0;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            Console.WriteLine("定时器请求忽略原生自检跳过：本系统不支持 PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION（仅 Windows 11 起）。");
            return;
        }
        var baseline = ProcessPerf.QueryTimerResolutionMs();
        Check(baseline is not null, "可读取系统定时器分辨率基线");

        using var child = new Process
        {
            StartInfo = new ProcessStartInfo(Environment.ProcessPath!, "--frame-timer-ignore-native-child")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        Check(child.Start(), "自建子进程启动成功");
        try
        {
        var line = child.StandardOutput.ReadLine();
        Check(line is not null && line.StartsWith("READY:0:"), "子进程报告就绪（0.5ms 请求成功）：" + line);
        Thread.Sleep(400);
        var raised = ProcessPerf.QueryTimerResolutionMs();
        Check(raised is not null && raised <= 0.55, $"子进程请求后系统读数应变细：{raised:0.###} ms");

        var identity = ProcmanIdentity.TryIdentify(child.Id);
        Check(identity is not null, "可核验自建子进程身份（路径与启动时间）");
        var before = ProcmanCapabilities.ReadPowerThrottling(child.Id);
        Check(before.Ok, "可读取子进程电源节流状态：" + before.Error);
        var original = new FrameTimerPowerState(before.Control, before.State);
        Check(!original.Forced, "子进程初始未固定忽略位");

        // 只设置 0x4 位（保留其他位），模拟本功能对目标进程的动作。
        var applied = original.Apply();
        var write = ProcmanCapabilities.WritePowerThrottling(child.Id, applied.Control, applied.State);
        Check(write.Ok, "设置忽略定时器分辨率位成功：" + write.Error);
        var afterSet = ProcmanCapabilities.ReadPowerThrottling(child.Id);
        Check(afterSet.Ok && ((afterSet.State & 4u) != 0) && ((afterSet.Control & 4u) != 0), "回读确认 0x4 位已置位");
        Thread.Sleep(400);
        var fellBack = ProcessPerf.QueryTimerResolutionMs();
        Check(fellBack is not null && fellBack > 0.55, $"设置该位后系统读数应回落到默认：{fellBack:0.###} ms");

        // 撤销该位，读数应恢复。
        var restore = ProcmanCapabilities.WritePowerThrottling(child.Id, original.Control, original.State);
        Check(restore.Ok, "撤销该位成功：" + restore.Error);
        var afterClear = ProcmanCapabilities.ReadPowerThrottling(child.Id);
        Check(afterClear.Ok && (afterClear.State & 4u) == 0 && (afterClear.Control & 4u) == 0, "回读确认 0x4 位已撤销");
        Thread.Sleep(600);
        var reRaised = ProcessPerf.QueryTimerResolutionMs();
        Check(reRaised is not null && reRaised <= 0.55, $"撤销该位后子进程请求重新生效：{reRaised:0.###} ms");

        Console.WriteLine($"定时器请求忽略原生自检（位语义，{_checks} 项）：基线 {baseline:0.###} ms → 子进程 0.5ms 请求 {raised:0.###} ms → 置 0x4 位 {fellBack:0.###} ms → 撤销 {reRaised:0.###} ms。");
        }
        finally
        {
            // 无论断言成败都必须结束自建子进程，避免残留请求或残留进程。
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch { }
            try { child.WaitForExit(5000); } catch { }
            Thread.Sleep(600);
            var final = ProcessPerf.QueryTimerResolutionMs();
            Console.WriteLine($"结束自建子进程后系统定时器分辨率：{final:0.###} ms（基线 {baseline:0.###} ms）");
            if (final is not null && baseline is not null && Math.Abs(final.Value - baseline.Value) > 0.2)
                Console.Error.WriteLine("警告：结束后系统读数未回到基线，请核对是否有其他程序持有定时器请求。");
        }

        RunGateChecks(baseline);
    }

    /// <summary>
    /// 时钟可达性门禁的真实链路验证：
    /// 子进程自身粒度 → 置位后被饿到 15.6ms（证明必须门禁）→ 真实探针结论 → 门禁拒绝施加。
    /// </summary>
    private static void RunGateChecks(double? baseline)
    {
        var probe = new FrameTimerLegacyProbe();
        using var child = new GranularityChild();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-frame-timer-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Check(child.Start(), "粒度子进程启动成功");
            // 子进程自己请求 0.5ms（模拟三角洲），并读取回执。
            Check(child.Command("Q").StartsWith("ACK:Q:0:"), "子进程 0.5ms 请求被内核接受");
            var own = child.Measure();
            Check(own <= 3.0, $"子进程持有自己的请求时粒度应较细：{own:0.##} ms");

            // 先记录「无请求进程」基线（本测试进程自身不请求任何精度），用于核对置位后的回落目标。
            var requestless = ProcessPerf.MeasureWaitGranularityMedianMs(41);

            // 置位 → 该子进程不再贡献自己的请求，其计时应回落到「无请求进程」所见的全局 tick。
            var identity = ProcmanIdentity.TryIdentify(child.Pid);
            Check(identity is not null, "可核验粒度子进程身份");
            var before = ProcmanCapabilities.ReadPowerThrottling(child.Pid);
            Check(before.Ok, "可读取粒度子进程电源节流状态");
            var original = new FrameTimerPowerState(before.Control, before.State);
            var applied = original.Apply();
            Check(ProcmanCapabilities.WritePowerThrottling(child.Pid, applied.Control, applied.State).Ok, "置位成功");
            var starved = child.Measure();
            // 置位后该进程等价于「无请求进程」：其粒度应接近无请求基线（全局语义下约 1~2ms；按进程语义下约 15.6ms）。
            Check(Math.Abs(starved - requestless) <= 0.8,
                $"置位后子进程粒度应回落到无请求基线（无请求 {requestless:0.##} ms，置位后 {starved:0.##} ms）");
            Check(ProcmanCapabilities.WritePowerThrottling(child.Pid, original.Control, original.State).Ok, "撤销位成功");
            var recovered = child.Measure();
            Check(recovered <= 3.0, $"撤销位后子进程恢复自身精度（实测 {recovered:0.##} ms）");

            // 探针实测「无请求进程」粒度：决定门禁结论。
            var reachable = probe.Probe(force: true);
            Check(reachable is not null, "探针应给出明确结论：" + probe.Message);
            var granularity = probe.GranularityMs;
            Check(granularity is > 0.5, "探针应给出有效实测值");
            // 门禁结论必须与实测一致：够细（≤2.5ms）才允许施加，否则必须拒绝。
            Check(reachable == (granularity <= 5.0), "门禁阈值必须与实测值一致（5ms）");

            // 门禁端到端：真实探针 + 假进程 API。
            var ownership = new ProcessPropertyGate(Path.Combine(root, "owners.json"));
            var api = new RecordingApi();
            var controller = new FrameGameTimerIgnore(Path.Combine(root, "frame-game-timer-ignore.json"), ownership, api, probe);
            var deltaIdentity = new ProcmanProcessIdentity(4242, DateTime.UtcNow, @"C:\Games\DeltaForceClient-Win64-Shipping.exe");
            Check(controller.Sync([deltaIdentity], _ => true, "等待三角洲启动", true), "门禁同步应安全返回");
            if (reachable == true)
            {
                Check(api.Writes.Count == 1, "时钟够细时应写入一次忽略位");
                Check(controller.Snapshot.IgnoreApplied, "时钟够细时快照应记录已施加");
                Check(controller.Snapshot.Message.Contains("已忽略"), "状态文字应说明已忽略：" + controller.Snapshot.Message);
            }
            else
            {
                Check(api.Writes.Count == 0, "时钟过粗时必须零写入（三角洲不会被饿到 15.625ms）");
                Check(!controller.Snapshot.IgnoreApplied, "时钟过粗时不得声称已忽略");
                Check(controller.Snapshot.Message.Contains("未施加"), "状态文字应说明未施加：" + controller.Snapshot.Message);
            }
            Check(ProcessPerf.QueryTimerResolutionMs() is not null, "仍可读取系统读数");
            Console.WriteLine($"时钟门禁链路验证通过（{_checks} 项）：子进程自持粒度 {own:0.##} ms → 无请求基线 {requestless:0.##} ms → 置位 {starved:0.##} ms → 撤销 {recovered:0.##} ms → 探针 {probe.GranularityMs:0.##} ms → " + (reachable == true ? "施加忽略位" : "拒绝施加") + $"（全局语义={reachable == true}）。基线 {baseline:0.###} ms。");
        }
        finally
        {
            try { child.Dispose(); } catch { }
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>可交互的粒度子进程：Q=请求 0.5ms，M=测量，X=退出。</summary>
    private sealed class GranularityChild : IDisposable
    {
        private Process? _process;
        internal int Pid => _process!.Id;
        internal bool Start()
        {
            _process = new Process
            {
                StartInfo = new ProcessStartInfo(Environment.ProcessPath!, "--timer-granularity-child")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            if (!_process.Start()) return false;
            return _process.StandardOutput.ReadLine() == "READY";
        }
        internal void Send(string command)
        {
            _process!.StandardInput.WriteLine(command);
            _process.StandardInput.Flush();
        }
        internal string Command(string command)
        {
            Send(command);
            return _process!.StandardOutput.ReadLine() ?? "(null)";
        }
        internal double Measure()
        {
            var line = Command("M");
            if (!line.StartsWith("GRAN:", StringComparison.Ordinal))
                throw new Exception("粒度子进程返回异常：" + line);
            var parts = line.Split(':');
            return double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        }
        public void Dispose()
        {
            try { if (_process is not null && !_process.HasExited) { Send("X"); _process.WaitForExit(2000); } } catch { }
            try { if (_process is not null && !_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
            _process?.Dispose();
        }
    }

    private sealed class RecordingApi : IFrameTimerIgnoreApi
    {
        internal readonly List<FrameTimerPowerState> Writes = [];
        internal FrameTimerPowerState State = new(0, 0);
        public string IdentityState(ProcmanProcessIdentity identity) => "same";
        public IFrameTimerIgnoreHandle Open(ProcmanProcessIdentity identity) => new Handle(this);
        private sealed class Handle(RecordingApi api) : IFrameTimerIgnoreHandle
        {
            public FrameTimerPowerState Read() => api.State;
            public void Write(FrameTimerPowerState state) { api.Writes.Add(state); api.State = state; }
            public void Dispose() { }
        }
    }
}
