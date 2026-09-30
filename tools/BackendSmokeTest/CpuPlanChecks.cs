using DeltaNFD.Services;

/// <summary>
/// CPU 优化方案（txt 保存 / 分享 / 导入）回归测试。
///
/// 重点覆盖「导入必须 CPU 型号 + 核心数 + 线程数完全一致」这条硬约束——
/// 因为亲和性掩码里的核心序号与本机拓扑绑定，换 CPU 套用会把游戏锁到不存在的核上。
/// </summary>
internal static class CpuPlanChecks
{
    private const string TestCpu = "AMD Ryzen 9 7945HX with Radeon Graphics";

    public static void Run()
    {
        VerifyRoundTrip();
        VerifyCpuMismatchRejected();
        VerifyMalformedRejected();
        VerifyGarbageRejected();
        VerifyRealisticShareFlow();
        Console.WriteLine("  CPU 优化方案：往返 / 型号不符 / 核心数不符 / 线程数不符 / 格式错误 / 非本工具文件 / 分享流程 全部通过");
    }

    /// <summary>序列化 → 解析 必须完全还原（含异类策略的交流/直流值与亲和性核心清单）。</summary>
    private static void VerifyRoundTrip()
    {
        var plan = BuildPlan();
        var text = CpuPlanService.Serialize(plan);

        if (!CpuPlanService.TryParse(text, out var parsed, out var error))
        {
            throw new Exception($"方案往返解析失败：{error}");
        }

        Check(parsed!.CpuName == plan.CpuName, "往返：CPU 型号不一致");
        Check(parsed.PhysicalCores == plan.PhysicalCores, "往返：核心数不一致");
        Check(parsed.LogicalProcessors == plan.LogicalProcessors, "往返：线程数不一致");
        Check(parsed.AffinityRuleEnabled == plan.AffinityRuleEnabled, "往返：亲和性规则开关不一致");
        Check(parsed.AffinityMask == plan.AffinityMask, "往返：亲和性掩码不一致");
        Check(parsed.AffinityCores.SequenceEqual(plan.AffinityCores),
            $"往返：勾选核心不一致（{string.Join(",", parsed.AffinityCores)} vs {string.Join(",", plan.AffinityCores)}）");
        Check(parsed.Hetero.Count == plan.Hetero.Count, "往返：异类策略条数不一致");

        for (var i = 0; i < plan.Hetero.Count; i++)
        {
            Check(parsed.Hetero[i].SettingGuid == plan.Hetero[i].SettingGuid, $"往返：异类策略[{i}] GUID 不一致");
            Check(parsed.Hetero[i].AcValue == plan.Hetero[i].AcValue, $"往返：异类策略[{i}] 交流值不一致");
            Check(parsed.Hetero[i].DcValue == plan.Hetero[i].DcValue, $"往返：异类策略[{i}] 直流值不一致");
        }

        // 文件头必须存在（导入时要靠它识别"是不是本工具的文件"）
        Check(text.StartsWith(CpuPlanService.FileHeader, StringComparison.Ordinal),
            "序列化结果缺少文件标识行");
    }

    /// <summary>CPU 型号不同必须拒绝，且错误信息要指出差异。</summary>
    private static void VerifyCpuMismatchRejected()
    {
        var plan = BuildPlan();

        var r1 = CpuPlanService.ValidateForLocalMachine(plan, "Intel Core i9-14900K", 32, 32);
        Check(!r1.Success, "CPU 型号不同却通过了校验");
        Check(r1.Message.Contains("CPU 型号"), "型号不符的错误信息未指出 CPU 型号");

        // 核心数不同（线程数相同）
        var r2 = CpuPlanService.ValidateForLocalMachine(plan, TestCpu, 8, plan.LogicalProcessors);
        Check(!r2.Success, "核心数不同却通过了校验");
        Check(r2.Message.Contains("核心数"), "核心数不符的错误信息未指出核心数");

        // 线程数不同（核心数相同）—— 同款 CPU 关掉 SMT/超线程的情形
        var r3 = CpuPlanService.ValidateForLocalMachine(plan, TestCpu, plan.PhysicalCores, 16);
        Check(!r3.Success, "线程数不同却通过了校验");
        Check(r3.Message.Contains("线程数"), "线程数不符的错误信息未指出线程数");

        // 三者全同 → 必须通过
        var ok = CpuPlanService.ValidateForLocalMachine(plan, TestCpu, plan.PhysicalCores, plan.LogicalProcessors);
        Check(ok.Success, $"完全一致却拒绝导入：{ok.Message}");

        // 大小写/多余空格差异应视为同一型号（避免分享时被无意义的空格差异挡住）
        var spaced = CpuPlanService.ValidateForLocalMachine(
            plan, "  amd   ryzen 9 7945hx WITH radeon graphics  ", plan.PhysicalCores, plan.LogicalProcessors);
        Check(spaced.Success, "仅大小写/空格不同却被判为不同 CPU");
    }

    /// <summary>缺少关键字段的文件必须给出明确原因，而不是当成 0 核静默通过。</summary>
    private static void VerifyMalformedRejected()
    {
        var noHeader = "[处理器]\nCPU型号=X\n核心数=16\n线程数=32\n";
        Check(!CpuPlanService.TryParse(noHeader, out _, out var e1) && e1.Contains("标识行"),
            "缺少文件标识行的文件未被拒绝");

        var noCores = CpuPlanService.FileHeader + "\n[处理器]\nCPU型号=X\n";
        Check(!CpuPlanService.TryParse(noCores, out _, out var e2) && e2.Contains("核心数"),
            "缺少核心数/线程数的文件未被拒绝");

        var noCpu = CpuPlanService.FileHeader + "\n[处理器]\n核心数=16\n线程数=32\n";
        Check(!CpuPlanService.TryParse(noCpu, out _, out var e3) && e3.Contains("CPU 型号"),
            "缺少 CPU 型号的文件未被拒绝");

        Check(!CpuPlanService.TryParse("", out _, out _), "空文件未被拒绝");
    }

    /// <summary>随手一个 txt 不能当方案导入。</summary>
    private static void VerifyGarbageRejected()
    {
        Check(!CpuPlanService.TryParse("这是一个普通的记事本文件\n里面没有任何方案内容", out _, out _),
            "普通文本被误认为方案文件");
    }

    /// <summary>模拟真实分享流程：A 机导出 → B 机（完全同款）导入通过 → C 机（不同 CPU）被拒。</summary>
    private static void VerifyRealisticShareFlow()
    {
        var source = BuildPlan();
        var fileText = CpuPlanService.Serialize(source);

        // B 机：同款 CPU，解析 + 校验都该过
        Check(CpuPlanService.TryParse(fileText, out var forB, out _), "分享流程：B 机解析失败");
        Check(CpuPlanService.ValidateForLocalMachine(forB!, TestCpu, 16, 32).Success,
            "分享流程：同款 CPU 的 B 机被拒");

        // C 机：同品牌但不同型号（核心/线程也可能同）——型号不同就必须拒
        var forC = CpuPlanService.ValidateForLocalMachine(forB!, "AMD Ryzen 9 7940HX with Radeon Graphics", 16, 32);
        Check(!forC.Success, "分享流程：不同型号但核心/线程相同的 C 机未被拒绝");

        // 掩码与勾选核心必须自洽（导入时以勾选核心重建掩码，二者不一致会误导用户）
        ulong rebuilt = 0;
        foreach (var core in forB!.AffinityCores)
        {
            rebuilt |= 1UL << core;
        }

        Check(rebuilt == Convert.ToUInt64(forB.AffinityMask, 16),
            $"方案自身不自洽：掩码 {forB.AffinityMask} 与勾选核心重建值 0x{rebuilt:X} 不一致");
    }

    private static CpuPlanService.CpuPlan BuildPlan() => new()
    {
        CpuName = TestCpu,
        PhysicalCores = 16,
        LogicalProcessors = 32,
        AffinityRuleEnabled = true,
        AffinityMask = "0xFFFF",
        AffinityCores = Enumerable.Range(0, 16).ToList(),
        SavedAt = "2026-09-30 15:00:00",
        MachineName = "TEST-PC",
        AppVersion = "OpenAlphaV0.83",
        Hetero = new List<CpuPlanService.HeteroEntry>
        {
            new("7f2f5cfa-f10c-4823-b5e1-e93ae85f46b5", "生效的异类策略", "4", "4"),
            new("93b8b6dc-0698-4d1c-9ee4-0644e900c85d", "异类线程调度策略", "5", "0"),
            new("bae08b81-2d5e-4688-ad6a-13243356654b", "异类短线程调度策略", "5", "5"),
        },
    };

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception("CPU 优化方案检查失败：" + message);
        }
    }
}
