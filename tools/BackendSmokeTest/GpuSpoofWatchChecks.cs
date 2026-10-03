using DeltaNFD.Services;

internal static class GpuSpoofWatchChecks
{
    public static void Run()
    {
        var entry = new GpuSpoofWatchEntry("fixture", "假型号", "1 | oem1.inf | date");
        void Check(bool value, string name) { if (!value) throw new Exception(name); }
        Check(!GpuSpoofWatch.IsInvalidated(entry, entry.DriverStamp, entry.ExpectedName), "伪装正常不提醒");
        Check(!GpuSpoofWatch.IsInvalidated(entry, entry.DriverStamp, "原型号"), "名称变化不能冒充驱动变化");
        Check(!GpuSpoofWatch.IsInvalidated(entry, "2 | oem2.inf | date", entry.ExpectedName), "驱动变化但伪装仍在不提醒失效");
        Check(GpuSpoofWatch.IsInvalidated(entry, "2 | oem2.inf | date", "@oem2.inf;原型号"), "驱动变更且名称被还原需要提醒");
        Check(!GpuSpoofWatch.IsInvalidated(entry, null, "原型号"), "读取失败不猜驱动变化");
        Check(!GpuSpoofWatch.IsInvalidated(entry, "2", null), "名称读取失败不猜失效");
        Check(!GpuSpoofWatch.IsInvalidated(entry with { DriverStamp = "" }, "2", "原型号"), "旧数据无基准不冒充监测结果");
        Check(!GpuSpoofWatch.IsInvalidated(entry with { DriverStamp = "2" }, "2", entry.ExpectedName), "重新应用后使用新驱动基准");
        Check(GpuSpoofWatch.IsInvalidated(entry, "1 | oem2.inf | date", "原型号"), "版本相同但 INF 变化也能识别");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src/DeltaNFD/MainWindow.xaml.cs"))) root = root.Parent;
        if (root is null) throw new Exception("找不到首页提醒接线源码。");
        var source = File.ReadAllText(Path.Combine(root.FullName, "src/DeltaNFD/MainWindow.xaml.cs"));
        Check(source.Contains("ContentFrame.Content is DashboardPage"), "提醒仅在首页显示");
        Check(source.Contains("await CheckGpuSpoofWatchAsync();") && source.Contains("GpuWatch_Activated"), "启动与前台激活接线");
        Check(source.Contains("_gpuWatchDismissed.Add") && !source.Contains("GpuSpoofWatch.Dismiss"), "关闭提醒不跨启动永久隐藏");
        Check(new AppSettings().GpuSpoofWatchJson == "[]", "旧设置默认不虚构伪装记录");
        Console.WriteLine("GPU spoof watch: 13 checks passed (including source wiring); no real registry or driver changes. Not a GUI validation.");
    }
}
