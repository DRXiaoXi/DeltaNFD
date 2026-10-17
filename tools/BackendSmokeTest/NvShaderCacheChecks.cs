using System.Text.Json;
using DeltaNFD.Native;
using DeltaNFD.Services;

internal static class NvShaderCacheChecks
{
    internal static void Run(bool readOnly = false)
    {
        if (readOnly)
        {
            Console.WriteLine(JsonSerializer.Serialize(NvApiDrs.ReadShaderCacheSizes(), new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("只读 NVAPI 会话已结束；未创建、重命名、保存配置或修改缓存大小。");
            return;
        }
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
        NvDrsSetting Setting(uint value, uint predefined = 0, int location = 0) => new()
        {
            settingType = 0, currentValue = BitConverter.GetBytes(value), settingLocation = location,
            isCurrentPredefined = predefined, isPredefinedValid = 1, predefinedValue = BitConverter.GetBytes(0x4000u),
        };
        NvShaderCacheScope Scope(string scope, NvDrsSetting setting, int status = 0) => NvApiDrs.ClassifyShaderCache(scope, status, setting);
        NvShaderCacheReport Report(params NvShaderCacheScope[] scopes) => new() { Scopes = scopes.ToList() };
        var normalGlobal = Scope("全局", Setting(0x4000, 1));
        var absentGame = new NvShaderCacheScope("三角洲", "inherited", null, "未设置，沿用全局");
        var normal = Report(normalGlobal, absentGame);
        Check(!normal.HasNonDefault && normal.Complete, "驱动预定义默认及未建立三角洲专用配置不报红");
        foreach (var value in new uint[] { 0, 128, 512, 5120, 10240, 102400, uint.MaxValue })
        {
            var global = Scope("全局", Setting(value));
            var game = Scope("三角洲", Setting(value));
            Check(Report(global, absentGame).HasNonDefault, "全局非默认且三角洲无配置也报红：" + value);
            Check(Report(normalGlobal, game).HasNonDefault, "仅三角洲非默认也报红：" + value);
        }
        Check(Report(Scope("全局", Setting(5120)), Scope("三角洲", Setting(0x4000, 1))).HasNonDefault,
            "三角洲默认不能掩盖全局非默认");
        Check(!Report(Scope("全局", Setting(0x4000)), absentGame).HasNonDefault,
            "显式保存的驱动默认值不能仅因覆盖标志而误报非默认");
        var newerDriverDefault = Setting(10240); newerDriverDefault.predefinedValue = BitConverter.GetBytes(10240u);
        Check(Scope("全局", newerDriverDefault).State == "default", "随驱动预定义值变化，不把 10GB 或 16GB 硬编码为默认");
        var inherited = Scope("三角洲", Setting(10240, 0, 1));
        Check(inherited.State == "custom" && inherited.Detail.Contains("继承"), "继承非默认全局值仍提示异常");
        Check(Scope("三角洲", Setting(0x4000, 1, 2)).State == "inherited", "继承预定义基础值保持默认");
        Check(Scope("三角洲", Setting(0x4000, 0, 3)).State == "default", "默认配置来源不误报手动覆盖");
        Check(Scope("全局", Setting(uint.MaxValue)).Detail.Contains("无限制"), "无限制不是默认，显示明确大小说明");
        Check(Scope("三角洲", Setting(5120)).Detail.Contains("5 GB"), "大小按驱动 MB 值格式化");
        foreach (var code in new[] { NvApiDrs.StatusError, NvApiDrs.StatusSettingNotFound, NvApiDrs.StatusIncompatibleStructVersion })
        {
            var unknown = Scope("全局", Setting(0x4000, 1), code);
            Check(unknown.State == "unknown" && !Report(unknown, absentGame).Complete,
                "读取失败/不支持不能当作默认：" + code);
            Check(!Report(unknown, absentGame).HasNonDefault, "读取失败不会无证据报非默认：" + code);
            var partial = Report(unknown, Scope("三角洲", Setting(10240)));
            Check(partial.HasNonDefault && !partial.Complete && partial.Advice.Contains("无法读取"),
                "一项读取失败不隐藏另一项已确认非默认：" + code);
        }
        var badType = Setting(5120); badType.settingType = 1;
        var badValue = Setting(5120); badValue.currentValue = [];
        var badLocation = Setting(5120); badLocation.settingLocation = 4;
        var badFlag = Setting(5120); badFlag.isCurrentPredefined = 2;
        foreach (var invalid in new[] { badType, badValue, badLocation, badFlag })
            Check(Scope("全局", invalid).State == "unknown", "无效类型/缓冲/来源/标志不能判断默认或异常");
        var both = Report(Scope("全局", Setting(5120)), Scope("三角洲", Setting(uint.MaxValue)));
        var roundTrip = JsonSerializer.Deserialize<NvShaderCacheReport>(JsonSerializer.Serialize(both))!;
        Check(roundTrip.HasNonDefault && roundTrip.Complete && roundTrip.Advice.Contains("全局")
            && roundTrip.Advice.Contains("三角洲") && roundTrip.Advice.Contains("驱动默认"), "IPC 往返保留两种范围及恢复指引");
        Check(!new NvShaderCacheReport().Complete, "旧后端缺少数据不伪报已默认");
        Console.WriteLine($"NVIDIA shader cache checks: {count} passed. Driver settings never modified.");
    }
}
