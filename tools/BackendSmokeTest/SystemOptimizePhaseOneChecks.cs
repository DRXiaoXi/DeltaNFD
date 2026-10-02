using DeltaNFD.Services.TweakDb;

internal static class SystemOptimizePhaseOneChecks
{
    public static void Run()
    {
        var service = new BxService();
        var item = new BxItem
        {
            Name = "SyntheticTasks",
            Tweaks =
            [
                new BxTweak { TweakType = "TASK", Path = @"\Test\A" },
                new BxTweak { TweakType = "TASK", Path = @"\Test\B" },
            ],
        };

        Check(BxState.Off, BxTaskState.Enabled, BxTaskState.Enabled);
        Check(BxState.On, BxTaskState.Disabled, BxTaskState.Disabled);
        Check(BxState.Mixed, BxTaskState.Enabled, BxTaskState.Disabled);
        Check(BxState.On, BxTaskState.Disabled, BxTaskState.Missing);
        Check(BxState.Off, BxTaskState.Enabled, BxTaskState.Missing);
        Check(BxState.On, BxTaskState.Missing, BxTaskState.Disabled);
        Check(BxState.NotApplicable, BxTaskState.Missing, BxTaskState.Missing);
        Check(BxState.Unknown, BxTaskState.Unknown, BxTaskState.Disabled);
        Check(BxState.Unknown, BxTaskState.Unknown, BxTaskState.Missing);
        Check(BxState.Unknown, BxTaskState.Missing, BxTaskState.Unknown);

        var mixedItem = new BxItem
        {
            Name = "SyntheticRegAndMissingTask",
            Tweaks = [new BxTweak
            {
                TweakType = "REG", Path = @"HKCU\Software\DeltaNFD\MissingTest-" + Guid.NewGuid().ToString("N"), Key = "Value",
                Values = [new BxValueEntry { ValueTypes = ["DEFAULT"], Value = "Null" }, new BxValueEntry { ValueTypes = ["OPTIMAL"], Value = "1" }],
            }, new BxTweak { TweakType = "TASK", Path = @"\Test\Missing" }],
        };
        Assert(service.EvaluateItemForSnapshot(mixedItem, _ => BxTaskState.Missing) == BxState.Off,
            "missing task must not hide a non-optimized registry value");

        var variants = new[]
        {
            new BxItem { Name = "same", NoSupport = ["W11"] },
            new BxItem { Name = "same", NoSupport = ["W10"] },
        };
        Assert(ReferenceEquals(BxCatalog.SelectCompatibleItems(variants, 19045).Single(), variants[0]), "Win10 variant");
        Assert(ReferenceEquals(BxCatalog.SelectCompatibleItems(variants, 22631).Single(), variants[1]), "Win11 variant");
        var hddItem = new BxItem { Name = "Sysmain", NoSupport = ["HDD"] };
        Assert(BxCatalog.GetUnsupportedReason(hddItem, 22631, true) is not null, "HDD blocked");
        Assert(BxCatalog.GetUnsupportedReason(hddItem, 22631, null) is not null, "unknown media blocked");
        Assert(BxCatalog.GetUnsupportedReason(hddItem, 22631, false) is null, "SSD allowed");

        var hpet = new BxItem { Name = "HPETName" };
        Assert(BxCatalog.GetCpuRestrictionReason(hpet, "GenuineIntel") is not null, "Intel HPET blocked");
        Assert(BxCatalog.GetCpuRestrictionReason(hpet, " genuineintel ") is not null, "Intel vendor case/space normalization");
        Assert(BxCatalog.GetCpuRestrictionReason(hpet, "AuthenticAMD") is null, "AMD HPET unchanged");
        Assert(BxCatalog.GetCpuRestrictionReason(hpet, "") is null, "unknown vendor not misidentified as Intel");
        Assert(BxCatalog.GetCpuRestrictionReason(hddItem, "GenuineIntel") is null, "other Intel options unchanged");
        var intelService = new BxService(() => throw new InvalidOperationException("must not create a write backend"), () => "GenuineIntel");
        var hpetRefused = intelService.ApplyItemAsync(hpet, true).GetAwaiter().GetResult();
        Assert(!hpetRefused.Success && hpetRefused.Message.Contains("Intel"), "direct Intel HPET apply refused before writes");
        var restoreValidation = intelService.ApplyItemAsync(hpet, false).GetAwaiter().GetResult();
        Assert(!restoreValidation.Message.Contains("Intel"), "Intel restore not blocked by CPU policy (empty fixture has no operations)");

        Assert(BxService.MatchesAppxPattern(["Microsoft.XboxApp"], "Microsoft.Xbox*"), "APPX wildcard");
        Assert(!BxService.MatchesAppxPattern(["Microsoft.XboxApp"], "Microsoft.YourPhone"), "APPX absent");
        Console.WriteLine("系统优化第一阶段合成检查通过（任务、兼容性、APPX 匹配）。");

        void Check(BxState expected, BxTaskState first, BxTaskState second)
        {
            var actual = service.EvaluateItemForSnapshot(item, path => path.EndsWith('A') ? first : second);
            Assert(actual == expected, $"task {first}/{second}: expected {expected}, actual {actual}");
        }
    }

    public static async Task RunLiveAsync()
    {
        var service = new BxService();
        var items = new[]
        {
            new BxItem { Name = "TaskSample", Tweaks = [new BxTweak { TweakType = "TASK", Path = @"\Microsoft\Windows\Defrag\ScheduledDefrag" }] },
            new BxItem { Name = "AppxSample", Tweaks = [new BxTweak { TweakType = "APPX", Path = "Microsoft.WindowsCalculator" }] },
            new BxItem { Name = "OneDrive", Tweaks = [new BxTweak { TweakType = "CMD", Path = "OneDriveUninstall" }] },
        };
        var states = await service.GetItemStatesAsync(items);
        foreach (var item in items) Console.WriteLine($"{item.Name}: {states[item]}");
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
    }
}
