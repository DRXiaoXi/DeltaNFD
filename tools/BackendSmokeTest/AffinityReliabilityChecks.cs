using System.Diagnostics;
using System.Numerics;
using DeltaNFD.Services;

internal static class AffinityReliabilityChecks
{
    public static void Run()
    {
        var topology = new CpuTopologyService().GetTopologyAsync().GetAwaiter().GetResult();
        if (topology.AllMask == 0 || BitOperations.PopCount(topology.AllMask) < 2)
        {
            Console.WriteLine("亲和性实测跳过：当前处理器拓扑不支持单组硬锁核。");
            return;
        }

        var dll = typeof(AffinityReliabilityChecks).Assembly.Location;
        using var child = Process.Start(new ProcessStartInfo("dotnet", $"\"{dll}\" --affinity-test-child")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new Exception("Unable to launch affinity test child");

        try
        {
            var original = (ulong)child.ProcessorAffinity;
            var available = original & topology.AllMask;
            if (BitOperations.PopCount(available) < 2)
                throw new Exception("Test child has fewer than two available processors");

            var first = 1UL << BitOperations.TrailingZeroCount(available);
            var second = available & ~first;
            second = 1UL << BitOperations.TrailingZeroCount(second);
            Process[] Target() => [Process.GetProcessById(child.Id)];

            CpuTopologyService.ApplyAffinityRuleTick(first, Target);
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

            Console.WriteLine("亲和性可靠性检查通过：实际锁定、幂等、改掩码、外部覆盖补锁、非法掩码恢复及 50 次启停循环");
        }
        finally
        {
            CpuTopologyService.RestoreAffinityRule();
            if (!child.HasExited) child.Kill();
            child.WaitForExit();
        }
    }

    private static void AssertMask(Process process, ulong expected, string step)
    {
        process.Refresh();
        if ((ulong)process.ProcessorAffinity != expected)
            throw new Exception($"{step}: expected 0x{expected:X}, got 0x{(ulong)process.ProcessorAffinity:X}");
    }
}
