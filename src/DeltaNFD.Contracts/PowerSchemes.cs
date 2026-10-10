namespace DeltaNFD.Services;

/// <summary>
/// 电源计划相关静态常量与数据（前端只读展示 + 后端执行共用，放在合同程序集避免两端漂移）。
/// </summary>
public static class PowerSchemes
{
    /// <summary>Windows 内置「卓越性能」电源计划模板 GUID。</summary>
    public const string UltimateSchemeTemplateGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    public const string HighPerformanceSchemeGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    public const string BalancedSchemeGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";

    public const string PowerSaverSchemeGuid = "a1841308-3541-4fab-bc81-f71556f20b4a";

    /// <summary>无省电电源计划显示名。</summary>
    public const string NoPowerSaveSchemeName = "无省电释放模式";

    /// <summary>无省电电源计划描述。</summary>
    public const string NoPowerSaveSchemeDescription = "针对延迟与性能优化的无省电电源计划（游戏优化工具导入）";

    /// <summary>历史上出现过的旧显示名：.pow 内嵌的 Atlas 名 / 旧版中文名，统一迁移到 NoPowerSaveSchemeName。</summary>
    public static readonly string[] LegacyNoPowerSaveNames = { "Atlas Power Scheme", "无省电极限模式" };

    /// <summary>
    /// 异类调度策略（HKLM\...\PowerSettings 下的定义为准）。
    /// </summary>
    public static readonly (string Guid, string Title)[] HeteroPolicySettings =
    [
        ("7f2f5cfa-f10c-4823-b5e1-e93ae85f46b5", "生效的异类策略"),
        ("93b8b6dc-0698-4d1c-9ee4-0644e900c85d", "异类线程调度策略"),
        ("bae08b81-2d5e-4688-ad6a-13243356654b", "异类短线程调度策略"),
    ];
}
