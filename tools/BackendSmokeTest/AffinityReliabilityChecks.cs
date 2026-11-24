using System.Diagnostics;
using System.Numerics;
using DeltaNFD.Execution.Procman;
using DeltaNFD.Services;

internal static class AffinityReliabilityChecks
{
    public static void Run()
    {
        var marker = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-independent-affinity-" + marker.ToString("N"));
        var childRoot = Path.Combine(root, "child");
        Directory.CreateDirectory(childRoot);
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), marker.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, marker);
        var topology = new CpuTopologyService().GetTopologyAsync().GetAwaiter().GetResult();
        if (topology.AllMask == 0 || BitOperations.PopCount(topology.AllMask) < 2)
        {
            Console.WriteLine("亲和性实测跳过：当前处理器拓扑不支持单组硬锁核。");
            return;
        }

        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
            File.Copy(file, Path.Combine(childRoot, Path.GetFileName(file)));
        var childPath = Path.Combine(childRoot, "BackendSmokeTest.exe");
        using var child = StartChild(childPath);

        try
        {
            Require(child.StandardOutput.ReadLine() == "ready", "isolated child ready");
            var original = (ulong)child.ProcessorAffinity;
            var available = original & topology.AllMask;
            if (BitOperations.PopCount(available) < 2)
                throw new Exception("Test child has fewer than two available processors");

            var first = 1UL << BitOperations.TrailingZeroCount(available);
            var second = available & ~first;
            second = 1UL << BitOperations.TrailingZeroCount(second);
            Process[] Target() => [Process.GetProcessById(child.Id)];

            var ownershipReads = 0;
            ProcessPropertyGate.SnapshotReadForTests = () => ownershipReads++;
            var firstApply = CpuTopologyService.ApplyAffinityRuleTick(first, Target);
            ProcessPropertyGate.SnapshotReadForTests = null;
            Require(firstApply.Success && firstApply.Message.Contains("1/1") && ownershipReads == 1,
                "compatibility rule reads back and obtains only one property lease");
            AssertMask(child, first, "first apply");
            CpuTopologyService.ApplyAffinityRuleTick(first, Target);
            AssertMask(child, first, "idempotent tick");
            CpuTopologyService.ApplyAffinityRuleTick(second, Target);
            AssertMask(child, second, "mask change");

            child.ProcessorAffinity = (nint)original;
            CpuTopologyService.ApplyAffinityRuleTick(second, Target);
            AssertMask(child, second, "external override recovery");

            CpuTopologyService.RestoreAffinityRule();
            AssertMask(child, original, "disable restore");

            CpuTopologyService.ApplyAffinityRuleTick(first, Target);
            AssertMask(child, first, "reapply");
            CpuTopologyService.ApplyAffinityRuleTick(0, Target);
            AssertMask(child, original, "zero-mask restore");

            if (topology.AllMask != ulong.MaxValue)
            {
                CpuTopologyService.ApplyAffinityRuleTick(first, Target);
                AssertMask(child, first, "before invalid mask");
                var invalidBit = 1UL << BitOperations.TrailingZeroCount(~topology.AllMask);
                CpuTopologyService.ApplyAffinityRuleTick(first | invalidBit, Target);
                AssertMask(child, original, "invalid-mask restore");
            }

            for (var i = 0; i < 50; i++)
            {
                CpuTopologyService.ApplyAffinityRuleTick((i & 1) == 0 ? first : second, Target);
                CpuTopologyService.RestoreAffinityRule();
                AssertMask(child, original, $"cycle {i + 1} restore");
            }

            VerifySavedRule(root, childPath, child, original, first, second);
            Console.WriteLine("亲和性可靠性检查通过：兼容规则单次获取许可、实际锁定、幂等、外部覆盖补锁、恢复及 50 次启停；持久规则验证原值保护、掩码回读和手动接管。");
        }
        finally
        {
            ProcessPropertyGate.SnapshotReadForTests = null;
            CpuTopologyService.RestoreAffinityRule();
            StopChild(child);
        }
    }

    private static Process StartChild(string path) => Process.Start(new ProcessStartInfo(path, "--affinity-test-child")
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardInput = true, RedirectStandardOutput = true,
    }) ?? throw new InvalidOperationException("Unable to launch affinity test child");

    private static void StopChild(Process child)
    {
        if (!child.HasExited)
        {
            child.StandardInput.WriteLine();
            child.StandardInput.Flush();
            if (!child.WaitForExit(5000)) child.Kill();
        }
        child.WaitForExit();
    }

    private static void VerifySavedRule(string root, string path, Process child, ulong original, ulong first, ulong second)
    {
        var logPath = Path.Combine(root, "changes.json");
        using var engine = new ProcmanEngine(logPath, Path.Combine(root, "rules.json")) { BackgroundLoopDisabledForTests = true };
        var value = $"g0:0x{first:X}";
        var rule = new ProcmanRuleEntry { ExecutablePath = path, Capability = "affinity", Value = value, Enabled = true };
        var identity = ProcmanIdentity.TryIdentify(child.Id) ?? throw new InvalidOperationException("child identity unavailable");
        ProcmanAffinityRuleReadback Tick()
        {
            engine.ApplyRulesOnceAsync().GetAwaiter().GetResult();
            return engine.Snapshot().AffinityRuleReadbacks.Single(r => r.Pid == child.Id);
        }

        child.ProcessorAffinity = (nint)first;
        Require(engine.SaveRule(rule).Success, "save exact-path affinity rule");
        Require(Tick() is { Confirmed: true, ActualMask: var matching } && matching == value && !File.Exists(logPath),
            "already matching mask is confirmed without creating a change record");
        child.ProcessorAffinity = (nint)original;
        Require(Tick() is { Confirmed: true, ActualMask: var written } && written == value,
            "saved rule applies and returns actual mask");
        AssertMask(child, first, "saved rule native readback");
        var journal = File.ReadAllText(logPath);
        var ownershipReads = 0;
        ProcessPropertyGate.SnapshotReadForTests = () => ownershipReads++;
        try { Require(Tick().Confirmed && ownershipReads == 1, "saved rule uses one ownership lease per tick"); }
        finally { ProcessPropertyGate.SnapshotReadForTests = null; }
        child.ProcessorAffinity = (nint)second;
        Require(Tick() is { Confirmed: true, ActualMask: var repaired } && repaired == value
            && File.ReadAllText(logPath) == journal, "drift is repaired without replacing the original journal");
        AssertMask(child, first, "drift repair native readback");

        var gate = ProcessPropertyGate.Default;
        using (var lease = gate.Enter(identity, "affinity")) lease.SetOwner("manual");
        child.ProcessorAffinity = (nint)second;
        Require(Tick() is { Confirmed: false, ActualMask: var blocked } && blocked == $"g0:0x{second:X}",
            "manual takeover blocks drift repair and reports observed mask");
        AssertMask(child, second, "manual takeover preserved");
        using (var lease = gate.Enter(identity, "affinity")) lease.SetOwner("rule:" + Path.GetFileName(path));
        Require(Tick().Confirmed && engine.DeleteRuleAsync(path, "affinity").GetAwaiter().GetResult().Success,
            "deleting the rule restores the original mask");
        AssertMask(child, original, "persisted rule original restored");
        engine.ApplyRulesOnceAsync().GetAwaiter().GetResult();
        Require(engine.Snapshot().AffinityRuleReadbacks.Count == 0, "deleted rule clears old observations");

        Require(engine.SaveRule(rule).Success && Tick().Confirmed, "rule active before process restart");
        StopChild(child);
        using var restarted = StartChild(path);
        try
        {
            Require(restarted.StandardOutput.ReadLine() == "ready", "replacement child ready");
            var initial = (ulong)restarted.ProcessorAffinity;
            engine.ApplyRulesOnceAsync().GetAwaiter().GetResult();
            var sample = engine.Snapshot().AffinityRuleReadbacks.Single();
            Require(sample.Pid == restarted.Id && sample.Confirmed && sample.ActualMask == value,
                "restart is observed as a new identity and the saved rule is applied");
            AssertMask(restarted, first, "restarted child native readback");
            Require(engine.DeleteRuleAsync(path, "affinity").GetAwaiter().GetResult().Success,
                "rule restores the new instance and clears old ended identity");
            AssertMask(restarted, initial, "restarted child original restored");
        }
        finally { StopChild(restarted); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertMask(Process process, ulong expected, string step)
    {
        process.Refresh();
        if ((ulong)process.ProcessorAffinity != expected)
            throw new Exception($"{step}: expected 0x{expected:X}, got 0x{(ulong)process.ProcessorAffinity:X}");
    }
}
