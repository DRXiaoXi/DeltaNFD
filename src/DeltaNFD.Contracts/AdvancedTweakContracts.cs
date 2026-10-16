namespace DeltaNFD.Services;

/// <summary>风险等级（供 UI 徽章展示）。</summary>
public enum AdvancedRisk
{
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>高级优化分组类别。</summary>
public enum AdvancedCategory
{
    /// <summary>遥测与隐私。</summary>
    Telemetry,

    /// <summary>安全开关（高危）。</summary>
    Security,

    /// <summary>Windows 更新。</summary>
    Updates,
}

/// <summary>一个高级优化分组（如「遥测与数据采集」「Defender 实时防护」）。</summary>
public sealed class AdvancedTweakGroup
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string Description { get; init; }

    public required AdvancedCategory Category { get; init; }

    public required AdvancedRisk Risk { get; init; }

    /// <summary>风险/副作用说明（确认对话框中展示）。</summary>
    public required string RiskNote { get; init; }

    /// <summary>true = 该组优化已应用。</summary>
    public required bool IsApplied { get; init; }

    /// <summary>状态补充说明（如「3 个计划任务因权限跳过」）。</summary>
    public string Detail { get; init; } = "";
}

/// <summary>
/// 高级（高危）优化服务：遥测/隐私、安全开关、Windows 更新三大类的分组开关。
/// 每组：注册表项（备份原值）、服务 Start（备份原值后写 4）、计划任务（schtasks 禁用）。
/// 全部可还原。实现等效 扩展优化库 的 WindowsTelemetry / Security / Defender / Updates 模块。
/// </summary>
public interface IAdvancedTweakService
{
    /// <summary>读取全部分组及当前应用状态（纯只读）。</summary>
    Task<List<AdvancedTweakGroup>> GetGroupsAsync();

    /// <summary>应用一组优化（写注册表 / 禁用服务与计划任务）。</summary>
    Task<OperationResult> ApplyAsync(string groupId);

    /// <summary>还原一组优化（按备份写回注册表与服务，重新启用计划任务）。</summary>
    Task<OperationResult> RevertAsync(string groupId);
}
