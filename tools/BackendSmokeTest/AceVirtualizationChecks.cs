using DeltaNFD.Services;
using static DeltaNFD.Services.AceVirtualizationCheck;

internal static class AceVirtualizationChecks
{
    internal static void Run()
    {
        var count = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new Exception(message); count++; Console.WriteLine("PASS: " + message); }
        AceVirtualizationCheckResult Snapshot(bool? dma = true, int? vbs = 0, bool? hvci = false,
            bool? hypervisor = false, bool? cpu = true, bool knownLaunch = true, bool launchOff = true,
            bool policy = false, FeatureState feature = FeatureState.Disabled)
            => Evaluate(new(cpu, "fixture", hypervisor), new(vbs, hvci, false, dma, knownLaunch, launchOff, policy, []),
                [new("VirtualMachinePlatform", "虚拟机平台", feature)]);
        AceVirtStatus State(AceVirtualizationCheckResult result, string id)
            => result.Hardware.Concat(result.System).Single(i => i.Id == id).Status;

        Check(Snapshot().AllPass, "完整健康快照通过");
        var configured = Snapshot(launchOff: false, policy: true, feature: FeatureState.Enabled);
        Check(!configured.HasFailure && configured.HasWarnings && !configured.AllPass, "Auto、已安装组件、VBS 策略不能误判当前运行冲突");
        Check(!configured.HasFixableConflict, "只有配置提醒时禁止系统一键修复");
        Check(!Snapshot(vbs: 1).HasFailure && Snapshot(vbs: 1).HasWarnings, "VBS 已配置但未运行只提醒");
        Check(Snapshot(vbs: 2, hvci: true, hypervisor: true).HasFixableConflict, "真实运行冲突可以进入系统修复");
        Check(State(Snapshot(cpu: false, hypervisor: true), "hardware.cpu-virt") == AceVirtStatus.Unknown, "Hypervisor 隐藏固件字段时不误判 BIOS 关闭");
        Check(State(Snapshot(cpu: false), "hardware.cpu-virt") == AceVirtStatus.Fail, "无运行掩蔽时保留固件关闭证据");
        Check(State(Snapshot(dma: false), "hardware.iommu") == AceVirtStatus.Fail, "当前内核 DMA 保护关闭仍不满足硬件标准");
        Check(!Snapshot(dma: false).HasFixableConflict, "DMA 硬件未达标不能触发关闭 Windows 安全功能");
        Check(State(Snapshot(dma: null), "hardware.iommu") == AceVirtStatus.Unknown && !Snapshot(dma: null).HasFailure, "DMA 查询失败保持未知而非未开启");
        Check(Snapshot(dma: null).HasUnknown && !Snapshot(dma: null).AllPass, "DMA 未知不能认证环境达标");
        Check(!Snapshot(knownLaunch: false).HasFailure && Snapshot(knownLaunch: false).HasUnknown, "BCD 权限不足不是环境冲突");
        Check(State(Snapshot(vbs: 99), "system.vbs") == AceVirtStatus.Unknown, "未知 VBS 枚举不归类为运行");
        Check(State(Snapshot(feature: FeatureState.Pending), "system.feature.VirtualMachinePlatform") == AceVirtStatus.Warn, "等待重启的组件不能断言关闭");
        Check(State(Snapshot(feature: FeatureState.NotPresent), "system.feature.VirtualMachinePlatform") == AceVirtStatus.Pass, "完整枚举确认未提供组件无需修复");
        Check(State(Snapshot(hypervisor: false, vbs: 2), "system.hypervisor-runtime") == AceVirtStatus.Unknown, "运行状态矛盾明确标为无法确认");
        Check(!Snapshot(hypervisor: false, vbs: 2, hvci: true).HasFixableConflict, "运行证据矛盾时不触发系统安全功能变更");

        Check(ParseDmaResult(0, 1, 1) == true, "原生 DMA 成功开启响应");
        Check(ParseDmaResult(0, 0, 1) == false, "原生 DMA 成功关闭响应");
        Check(ParseDmaResult(unchecked((int)0xC0000003), 1, 1) is null, "不支持原生信息类保持未知");
        Check(ParseDmaResult(unchecked((int)0xC0000022), 1, 1) is null, "原生查询拒绝访问保持未知");
        Check(ParseDmaResult(0, 1, 0) is null && ParseDmaResult(0, 1, 4) is null, "原生 DMA 响应长度异常不能通过");
        Check(ParseDmaResult(0, 2, 1) is null, "原生 DMA 布尔字段异常不能通过");

        var none = ParseGuard("{\"vbs\":0,\"running\":[0]}");
        Check(none.Vbs == 0 && none.Hvci == false && none.Credential == false, "本机 Device Guard 快照准确解析");
        var empty = ParseGuard("{\"vbs\":0,\"running\":[]}");
        Check(empty.Vbs == 0 && empty.Hvci == false, "空服务集合不会导致字段错位");
        var active = ParseGuard("{\"vbs\":2,\"running\":[1,2]}");
        Check(active.Vbs == 2 && active.Hvci == true && active.Credential == true, "保留 HVCI 和 Credential Guard 运行证据");
        foreach (var json in new[] { "", "ERR", "{}", "{\"vbs\":null}", "{\"vbs\":\"0\"}", "[]" })
            Check(ParseGuard(json).Vbs is null && ParseGuard(json).Hvci is null, "查询缺失或损坏不伪装健康：" + json);
        Check(ParseGuard("{\"vbs\":0,\"running\":null}").Hvci is null, "缺失服务字段不是未运行");
        Check(ParseGuard("{\"vbs\":2,\"running\":[\"2\"]}").Hvci is null, "服务字段类型异常保持未知");
        Check(ParseGuard("{\"vbs\":2,\"running\":[8]}").Hvci is null, "未知服务枚举保持未知");
        Check(ParseGuard("{\"vbs\":2,\"running\":[0,2]}").Vbs is null, "无服务与运行服务矛盾保持未知");
        Check(ParseGuard("{\"vbs\":0,\"running\":[2]}").Vbs is null, "VBS 停止与 HVCI 运行矛盾保持未知");
        foreach (var json in new[] { "", "[]", "{\"complete\":false,\"items\":[]}", "{\"complete\":true,\"items\":[{}]}" })
            Check(ParseFeatures(json).All(f => f.State == FeatureState.Unknown), "组件查询未完成不伪装关闭：" + json);
        Check(ParseFeatures("{\"complete\":true,\"items\":[]}").All(f => f.State == FeatureState.NotPresent), "完整查询确认当前版本未提供组件");
        foreach (var pending in new[] { "EnablePending", "DisablePending" })
            Check(ParseFeatures("{\"complete\":true,\"items\":[{\"FeatureName\":\"VirtualMachinePlatform\",\"State\":\"" + pending + "\"}]}")
                .Single(f => f.Id == "VirtualMachinePlatform").State == FeatureState.Pending, "保留待完成状态：" + pending);
        var noResults = new AceVirtualizationCheckResult { Hardware = [], System = [] };
        Check(noResults.HasUnknown && !noResults.AllPass, "空结果不能认证合格");
        Check(configured.OverallSummary.Contains("提醒") && !configured.OverallSummary.Contains("符合 ACE"), "汇总保留配置提醒并避免反作弊通过保证");
        Console.WriteLine($"ACE virtualization checks: {count} passed. Simulated observations; no system settings modified.");
    }
}
