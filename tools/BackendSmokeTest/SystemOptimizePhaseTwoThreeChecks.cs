using DeltaNFD.Services;
using DeltaNFD.Services.TweakDb;

internal static class SystemOptimizePhaseTwoThreeChecks
{
    public static void Run()
    {
        var sections = BxCatalog.Sections.Where(s => s.JsonFile is not null).ToList();
        var items = sections.SelectMany(s => BxCatalog.Database[s.Id]).ToList();
        Assert(items.Count > 140, "catalog loaded");
        var invalid = items.Where(i => i.ValidationError is not null).ToList();
        foreach (var item in invalid) Console.WriteLine($"数据校验异常：{item.Name}: {item.ValidationError}");
        Assert(invalid.Count == 0, "catalog values valid");

        var news = BxCatalog.Database["custom"].Single(i => i.Name == "NewsAndInterests");
        Assert(news.Tweaks.Any(t => t.ValueFormat == "HEX" && t.Key == "EnShellFeedsTaskbarViewMode"), "NewsAndInterests hex format");
        var invalidDword = new BxItem { Name = "Invalid", Tweaks =
            [new BxTweak { TweakType = "REG", Path = "HKCU\\Software\\Test", Key = "Value",
                Values = [new BxValueEntry { Value = "not-a-number", ValueTypes = ["ON"] }] }] };
        Assert(BxCatalog.ValidateItem(invalidDword) is not null, "invalid DWORD rejected");
        var hags = BxCatalog.Database["basic"].Single(i => i.Name == "HAGS");
        var disableHags = BxCatalog.Database["pending"].Single(i => i.Name == "UpDisableHags");
        Assert(BxCatalog.IsHagsItem(hags) && BxCatalog.IsHagsItem(disableHags), "HAGS shared setting identification");
        Assert(hags.Tweaks.Single(t => t.Key == "HwSchMode").Values.Any(v => v.Value == "2" && v.ValueTypes.Contains("ON")), "HAGS enable value");
        Assert(disableHags.Tweaks.Single(t => t.Key == "HwSchMode").Values.Any(v => v.Value == "1" && v.ValueTypes.Contains("ON")), "HAGS disable value");
        foreach (var name in new[] { "setssmartscreen", "CodeIntegrity", "SecurityHealthService" })
            Assert(BxCatalog.Database["security"].Single(i => i.Name == name).Risk == "High", name + " risk");

        var partial = BxService.SummarizeServiceGroup(3, 2, ["ServiceB"]);
        Assert(!partial.Success && partial.Message.Contains("ServiceB"), "service partial failure");
        Assert(BxService.SummarizeServiceGroup(2, 2, []).Success, "service complete success");
        var protectedOnly = BxService.SummarizeServiceGroup(1, 0, [], ["rdyboost"]);
        Assert(protectedOnly.Success && protectedOnly.IsSkipped && protectedOnly.Message.Contains("没有修改"), "protected-only group is skipped, not applied or failed");
        var guardedPartial = BxService.SummarizeServiceGroup(2, 1, [], ["rdyboost"]);
        Assert(guardedPartial.Success && !guardedPartial.IsSkipped && guardedPartial.Message.Contains("安全跳过"), "completed eligible services with guard skip");
        Assert(!BxService.SummarizeServiceGroup(3, 1, ["Denied"], ["rdyboost"]).Success, "real write failure is not hidden by safety skip");
        Assert(!BxService.SummarizeServiceGroup(1, 0, []).Success, "unexplained incomplete service operation remains failure");
        Assert(!BxService.SummarizeServiceGroup(0, 0, []).Success, "empty service group not claimed applied");

        var sysMain = BxCatalog.ServiceGroups.Single(g => g.Id == "svc-hdd");
        Assert(sysMain.Services.SequenceEqual(new[] { "SysMain" }), "ReadyBoost removed from batch disable group");
        Assert(sysMain.Name.Contains("SysMain") && sysMain.Desc.Contains("rdyboost"), "SysMain metadata accurately describes scope");
        var uwp = BxCatalog.Database["basic"].Single(i => i.Name == "setsuwpwork");
        Assert(uwp.Tweaks.Count == 2 && uwp.Tweaks.All(t => t.Path.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase)), "UWP entry writes only current-user settings");
        Assert(uwp.Tweaks.Select(t => t.Key).Order().SequenceEqual(new[] { "BackgroundAppGlobalToggle", "GlobalUserDisabled" }), "UWP background switches preserved");

        var backup = BxService.MakeUsbPowerBackup("00000000-0000-0000-0000-000000000001", 0, null);
        Assert(backup.Count == 3 && backup[1].Data == "0" && backup[2].ValueKind == 0, "USB power original values");
        Assert(PagefileService.HasEnoughSpace(32L * 1024 * 1024 * 1024, 32768), "pagefile enough space");
        Assert(!PagefileService.HasEnoughSpace(8L * 1024 * 1024 * 1024, 32768), "pagefile insufficient space");
        Console.WriteLine($"系统优化第二/三阶段合成检查通过（{items.Count} 条目）。");
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
    }
}
