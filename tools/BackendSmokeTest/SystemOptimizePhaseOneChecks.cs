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
        Check(BxState.Mixed, BxTaskState.Disabled, BxTaskState.Missing);
        Check(BxState.NotApplicable, BxTaskState.Missing, BxTaskState.Missing);
        Check(BxState.Unknown, BxTaskState.Unknown, BxTaskState.Disabled);

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
