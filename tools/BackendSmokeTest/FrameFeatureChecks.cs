using System.Xml.Linq;
using DeltaNFD.Services;

internal static class FrameFeatureChecks
{
    internal static void Run()
    {
        for (var mask = 0; mask < 8; mask++)
        {
            var response = (mask & 1) != 0;
            var foreground = (mask & 2) != 0;
            var powerSave = (mask & 4) != 0;
            Assert(FrameFeatureState.IsSelected(response, true) == response, "响应子项必须仅服从响应父开关");
            Assert(FrameFeatureState.IsSelected(foreground, true) == foreground, "前台子项必须仅服从前台父开关");
            Assert(FrameFeatureState.IsSelected(powerSave, true) == powerSave, "省电子项必须仅服从省电父开关");
            foreach (var parent in new[] { response, foreground, powerSave })
            {
                Assert(!FrameFeatureState.IsSelected(parent, false), "未选择子项不得显示开启");
                Assert(parent || FrameFeatureState.Describe(parent, true, true) == "未启用", "其他功能激活不得污染关闭项状态");
            }
        }
        Assert(FrameFeatureState.Describe(true, true, false) == "已配置 · 等待开启帧格", "配置与激活必须分开");
        Assert(FrameFeatureState.Describe(true, true, true) == "已选择 · 帧格已激活", "配置状态不得冒充执行成功");
        Assert(FrameFeatureState.Describe(true, false, true) == "未选择子功能 · 不会执行", "空子项不得显示启用");

        var dir = Path.Combine(Path.GetTempPath(), "DeltaNFD_FrameFeatures_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        try
        {
            AppSettingsStore.Update(path, s => s.DwmRestartOnGameStart = true);
            var settings = AppSettingsStore.Read(path);
            Assert(settings.DwmRestartOnGameStart && !settings.FrameResponseBoostEnabled &&
                !settings.FrameForegroundBoostEnabled && !settings.FramePowerSaveLatencyEnabled &&
                !settings.FramePowerLockEnabled, "仅启用 DWM 后其他父功能必须保持关闭");
            Assert(settings.FrameResponseBoostCoreParkingEnabled, "子选项偏好应保留");
            Assert(!FrameFeatureState.IsSelected(settings.FrameResponseBoostEnabled, settings.FrameResponseBoostCoreParkingEnabled),
                "保留的子选项不等于已启用功能");
            AppSettingsStore.Update(path, s => s.FrameResponseBoostEnabled = true);
            AppSettingsStore.Update(path, s => s.FrameResponseBoostEnabled = false);
            settings = AppSettingsStore.Read(path);
            Assert(settings.FrameResponseBoostCoreParkingEnabled && settings.DwmRestartOnGameStart &&
                !settings.FrameForegroundBoostEnabled && !settings.FramePowerSaveLatencyEnabled,
                "父开关往返不得丢失子偏好或改变其他功能");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            Directory.Delete(dir);
        }

        // 静态绑定回归，不启动会执行优化的 GUI。
        var source = Path.Combine(Directory.GetCurrentDirectory(), "src", "DeltaNFD", "Views", "FramePage.xaml");
        var doc = XDocument.Load(source);
        var texts = doc.Descendants().Where(e => e.Name.LocalName == "TextBlock")
            .Select(e => (string?)e.Attribute("Text")).ToArray();
        Assert(texts.Count(t => t == "{x:Bind FrameSrv.StatusText, Mode=OneWay}") == 1,
            "全局状态只允许出现在帧格总开关");
        foreach (var status in new[] { "DwmStatusText", "ResponseStatusText", "ForegroundStatusText", "PowerSaveStatusText" })
            Assert(texts.Contains($"{{x:Bind {status}, Mode=OneWay}}"), "缺少独立状态：" + status);
        var toggles = doc.Descendants().Where(e => e.Name.LocalName == "ToggleSwitch")
            .Select(e => (string?)e.Attribute("IsOn")).ToArray();
        foreach (var option in new[] { "CoreParkingSelected", "EcoQosSelected", "TimerSelected", "ResponsivenessSelected",
            "PrioritySelected", "NicSelected", "UsbSelected" })
            Assert(toggles.Contains($"{{x:Bind {option}, Mode=TwoWay}}"), "缺少父子有效状态绑定：" + option);
        Console.WriteLine("帧格独立状态检查通过：8 组父开关组合、子项关闭、配置/激活区分、DWM 单项持久化、偏好保留、XAML 绑定。");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
