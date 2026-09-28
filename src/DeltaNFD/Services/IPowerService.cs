namespace DeltaNFD.Services;

/// <summary>电源计划。</summary>
public sealed class PowerSchemeInfo
{
    /// <summary>powercfg GUID。</summary>
    public required string Guid { get; init; }

    /// <summary>显示名（卓越性能 / 高性能 / 平衡 / 节能 / 自定义名）。</summary>
    public required string Name { get; init; }

    /// <summary>是否为当前激活计划。</summary>
    public bool IsActive { get; init; }
}

/// <summary>一个异类调度策略设置的当前状态（AC/DC 分离）。</summary>
public sealed class HeteroPolicyInfo
{
    /// <summary>设置 GUID（SUB_PROCESSOR 下）。</summary>
    public required string SettingGuid { get; init; }

    /// <summary>显示名（生效的异类策略 / 异类线程调度策略 / 异类短线程调度策略）。</summary>
    public required string Title { get; init; }

    /// <summary>属性是否可用（极少数平台没有该属性）。</summary>
    public bool Supported { get; init; }

    /// <summary>交流（插电）当前值；null = 未知。</summary>
    public int? AcValue { get; init; }

    /// <summary>直流（电池）当前值；null = 未知。</summary>
    public int? DcValue { get; init; }
}

/// <summary>
/// 电源管理服务：查询 / 切换电源计划（powercfg）、休眠开关。
/// 卓越性能计划系统默认隐藏，首次切换时用 powercfg -duplicatescheme 动态导入并缓存 GUID。
/// </summary>
public interface IPowerService
{
    /// <summary>列出本机电源计划（含当前激活项）。</summary>
    Task<List<PowerSchemeInfo>> GetSchemesAsync();

    /// <summary>切换到指定电源计划（立即生效，无需重启）。</summary>
    Task<OperationResult> SetSchemeAsync(string schemeGuid);

    /// <summary>
    /// 导入内置的无省电电源计划（Assets\\NoPowerSave-Scheme.pow，基于卓越性能模板的延迟/性能优化方案，
    /// 导入后显示名为「无省电释放模式」）。已导入（按缓存 GUID 或计划名识别）时不重复导入。
    /// </summary>
    Task<OperationResult> ImportNoPowerSaveSchemeAsync();

    /// <summary>旧计划显示名迁移：把仍叫旧名（Atlas Power Scheme / 无省电极限模式）的计划改名为「无省电释放模式」（幂等）。</summary>
    Task<OperationResult> EnsureNoPowerSaveSchemeNameAsync();

    /// <summary>查询休眠当前是否启用。</summary>
    Task<bool> IsHibernateEnabledAsync();

    /// <summary>开启 / 关闭休眠。</summary>
    Task<OperationResult> SetHibernateAsync(bool enable);

    /// <summary>
    /// 读取当前电源计划的三个异类调度策略（隐藏属性，读取前自动取消隐藏）：
    /// 生效的异类策略 / 异类线程调度策略 / 异类短线程调度策略。
    /// </summary>
    Task<List<HeteroPolicyInfo>> GetHeteroPoliciesAsync();

    /// <summary>设置一个异类调度策略（交流/直流同时设置并激活当前计划，立即生效）。</summary>
    Task<OperationResult> SetHeteroPolicyAsync(string settingGuid, int value);

    /// <summary>按交流/直流分别设置一个异类策略值（还原原值时两者可能不同）。</summary>
    Task<OperationResult> SetHeteroPolicyValuesAsync(string settingGuid, int acValue, int dcValue);
}
