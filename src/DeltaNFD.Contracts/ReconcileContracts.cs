namespace DeltaNFD.Services;

/// <summary>
/// 未知操作恢复闭环（T1）的合同类型。
/// 后端 `模块.reconcile` 只读返回「当前实际生效状态 + 可用恢复范围」；
/// 前端据此让玩家选择「继续核对 / 按可信备份恢复 / 保留当前状态」，
/// 恢复成功必须再次调用 reconcile 复核，并把复核结果作为持久化证据。
/// 后端只暴露自身已知的恢复范围 id，不接受前端传入的任意命令、注册表路径或脚本。
/// </summary>
public sealed class ReconcileItem
{
    /// <summary>稳定的条目标识（如 tweak 枚举名、页文件盘符）。</summary>
    public required string Id { get; init; }

    /// <summary>条目分类（如 tweak / pagefile / spoof / guard）。</summary>
    public required string Kind { get; init; }

    /// <summary>当前实际状态文字（来自只读读取，不是偏好或缓存）。</summary>
    public required string State { get; init; }

    /// <summary>该条目是否存在可回退的原值备份。</summary>
    public bool HasBackup { get; init; }

    public string Detail { get; init; } = "";
}

/// <summary>一个可用的恢复范围：后端把范围 id 解析为自身的恢复动作，不接收任意指令。</summary>
public sealed class RecoveryScope
{
    /// <summary>范围 id（后端白名单内的动作名，如 spoof / guard / pagefile / tweaks）。</summary>
    public required string Id { get; init; }

    public required string Description { get; init; }

    /// <summary>当前是否可以执行（无可信备份时为 false，禁止伪装成可恢复）。</summary>
    public required bool Available { get; init; }

    /// <summary>不可用原因；可用时为空。</summary>
    public string Reason { get; init; } = "";

    /// <summary>恢复该范围预计影响的条目数（0 表示后端未提供明细）。</summary>
    public int AffectedCount { get; init; }
}

/// <summary>只读状态核对快照。</summary>
public sealed class ReconcileSnapshot
{
    public required string Module { get; init; }

    /// <summary>面向玩家的整体说明（含读取失败或状态未知时的如实说明）。</summary>
    public required string Summary { get; init; }

    /// <summary>读取是否完整；false 表示存在无法确认的状态，玩家不能据此判定「已恢复」。</summary>
    public required bool Complete { get; init; }

    public IReadOnlyList<ReconcileItem> Items { get; init; } = [];

    public IReadOnlyList<RecoveryScope> RecoveryScopes { get; init; } = [];
}

/// <summary>恢复请求：只允许后端已知的范围 id。</summary>
public sealed class RecoverArgs
{
    public string Scope { get; init; } = "";
}

/// <summary>恢复结果：成功与否 + 复核后的最新快照。复核失败不得报告为恢复成功。</summary>
public sealed class RecoverOutcome
{
    public required string Scope { get; init; }

    public required bool Success { get; init; }

    public required bool Verified { get; init; }

    public string Message { get; init; } = "";

    public ReconcileSnapshot? VerifiedSnapshot { get; init; }
}
