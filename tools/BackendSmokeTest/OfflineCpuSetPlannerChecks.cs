using DeltaNFD.Services;

internal static class OfflineCpuSetPlannerChecks
{
    public static void Run()
    {
        var topology = MakeDualCcdTopology();
        var prefs = new OfflineModePreferences { DualCcdImmediateEnabled = true, DualCcdGameCcdIndex = 0 };
        Require(OfflineCpuSetPlanner.TryCreate(topology, prefs, out var plan, out var error), error);
        Require(plan is { TargetMask: 0x0E, OtherProcessMask: 0xF0, IsDualCcd: true },
            "CCD0 game mask should leave CPU0 out and put other work on CCD1");

        prefs.DualCcdGameCcdIndex = 1;
        Require(OfflineCpuSetPlanner.TryCreate(topology, prefs, out plan, out error), error);
        Require(plan is { TargetMask: 0xF0, OtherProcessMask: 0x0E },
            "CCD1 game mask should leave CPU0 out of the other-process mask");

        prefs = new OfflineModePreferences { SingleCcdExcludeCpu0Enabled = true };
        Require(OfflineCpuSetPlanner.TryCreate(topology, prefs, out plan, out error), error);
        Require(plan is { TargetMask: 0xFE, OtherProcessMask: 0, IsDualCcd: false },
            "single-CCD rule should be a target-only soft mask excluding CPU0");

        prefs = new OfflineModePreferences { GameAffinityRuleEnabled = true, GameAffinityRuleMask = 0x55 };
        Require(OfflineCpuSetPlanner.TryCreate(topology, prefs, out plan, out error), error);
        Require(plan is { TargetMask: 0x55, OtherProcessMask: 0 }, "saved rule should be target-only");

        prefs = new OfflineModePreferences { DualCcdArmed = true, DualCcdFrameEnabled = true, DualCcdGameCcdIndex = 1 };
        Require(OfflineCpuSetPlanner.TryCreate(topology, prefs, out plan, out error), error);
        Require(plan is null, "armed frame scheduling must not run when frame mode was not active");
        prefs.FrameModeWasActive = true;
        Require(OfflineCpuSetPlanner.TryCreate(topology, prefs, out plan, out error), error);
        Require(plan is { TargetMask: 0xF0, OtherProcessMask: 0x0E }, "previously active frame CCD preference should be used offline");

        var unsupported = MakeDualCcdTopology(groupCount: 2);
        Require(!OfflineCpuSetPlanner.TryCreate(unsupported,
                new OfflineModePreferences { SingleCcdExcludeCpu0Enabled = true }, out _, out _),
            "multi-group topology must be rejected");
        Require(!OfflineCpuSetPlanner.TryCreate(topology,
                new OfflineModePreferences { GameAffinityRuleEnabled = true, GameAffinityRuleMask = 0x100 }, out _, out _),
            "mask bits outside the detected topology must be rejected");

        Console.WriteLine("脱机 CPU Sets 调度规划检查通过。");
    }

    private static CpuTopology MakeDualCcdTopology(int groupCount = 1) => new()
    {
        Vendor = "AMD",
        CpuName = "Synthetic dual CCD",
        PhysicalCores = 8,
        LogicalProcessors = 8,
        GroupCount = groupCount,
        IsHybrid = false,
        PCoreCount = 0,
        ECoreCount = 0,
        HasHyperThreading = false,
        IsAmd = true,
        IsAmdMultiCcd = true,
        CcdCount = 2,
        Ccds =
        [
            new CcdInfo { Index = 0, Group = 0, CoreCount = 4, LogicalCount = 4, Mask = 0x0F },
            new CcdInfo { Index = 1, Group = 0, CoreCount = 4, LogicalCount = 4, Mask = 0xF0 },
        ],
        AllMask = 0xFF,
        PCoreMask = 0,
        ECoreMask = 0,
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
