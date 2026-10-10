namespace DeltaNFD.Services;

/// <summary>Defender 预检引导的用户决策。</summary>
public enum DefenderGuardDecision
{
    /// <summary>防护已关闭（重新检测通过或本机无 Defender），继续应用。</summary>
    Proceed,

    /// <summary>用户选择跳过引导继续应用（相关条目可能被拦截而失败）。</summary>
    ProceedAnyway,

    /// <summary>用户取消应用。</summary>
    Cancel,
}

/// <summary>优化批次中待预检的单个条目（名称 + 拼接后的操作文本，仅用于安全类关键词匹配）。</summary>
public sealed class DefenderBatchItem
{
    public string Name { get; set; } = "";
    public string Ops { get; set; } = "";
}

/// <summary>优化批次的安全软件预检报告。</summary>
public sealed class DefenderPreflightReport
{
    /// <summary>批次中命中安全防护关键词的条目名。</summary>
    public List<string> SecurityItemNames { get; set; } = new();

    /// <summary>本机是否存在 Windows Defender。</summary>
    public bool DefenderPresent { get; set; }

    /// <summary>预检时实时保护是否开启（查询不到时按开启保守处理）。</summary>
    public bool InitialRealTimeProtectionOn { get; set; }

    /// <summary>预检时篡改防护是否开启。</summary>
    public bool TamperProtectionOn { get; set; }

    /// <summary>检测到的第三方杀软名（SecurityCenter2，排除 Defender 自身）。</summary>
    public List<string> ThirdPartyAvNames { get; set; } = new();

    /// <summary>批次是否包含「关闭 Defender 本体」的条目（应用成功后不做实时保护自动恢复）。</summary>
    public bool DefenderOffTweakInBatch { get; set; }

    /// <summary>建议加入排除项的目录（程序目录 + %APPDATA%\Delta NFD）。</summary>
    public List<string> ExclusionPaths { get; set; } = new();

    /// <summary>是否需要弹引导（批次含安全类条目，且 Defender 防护在位或存在第三方杀软）。</summary>
    public bool NeedsGuidance =>
        (DefenderPresent && (InitialRealTimeProtectionOn || TamperProtectionOn)) || ThirdPartyAvNames.Count > 0;
}

/// <summary>应用完成后的安全收尾结果。</summary>
public sealed class DefenderPostApplyResult
{
    public bool CleanupSucceeded { get; set; } = true;
    public string Message { get; set; } = "";
    /// <summary>是否需要提醒用户重新开启安全防护。</summary>
    public bool Remind { get; set; }

    /// <summary>Defender 将按批次条目的选择保持关闭（提醒文案需相应说明）。</summary>
    public bool DefenderLeftOff { get; set; }
}

/// <summary>实时保护/篡改防护的即时状态。</summary>
public sealed class DefenderProtectionState
{
    public bool RealTimeOn { get; set; }
    public bool TamperOn { get; set; }
}

/// <summary>
/// Defender 预检/收尾服务合同。执行（只读状态查询、会话排除项增删、恢复实时保护）由系统维护后端完成；
/// 用户决策与弹窗仍由前端负责（通过钩子注入）。
/// </summary>
public interface IDefenderGuardService
{
    /// <summary>应用前预检：返回 null = 批次无安全类条目，无需关注。</summary>
    Task<DefenderPreflightReport?> PreflightAsync(IReadOnlyList<DefenderBatchItem> batchItems);

    /// <summary>重新查询防护状态；返回 (实时保护仍开?, 篡改防护仍开?)。查询失败返回 (false,false)。</summary>
    Task<(bool RealTimeOn, bool TamperOn)> RefreshProtectionStateAsync();

    /// <summary>把后端自身解析的建议目录加入 Defender 排除列表（会话内记录）。</summary>
    Task AddSessionExclusionsAsync();

    /// <summary>应用完成后收尾：移除会话排除项；必要时尽力恢复实时保护。返回是否需要提醒。</summary>
    Task<DefenderPostApplyResult> PostApplyAsync();
}
