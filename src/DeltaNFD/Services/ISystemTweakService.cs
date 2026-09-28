namespace DeltaNFD.Services;

/// <summary>系统精简优化项。</summary>
public enum SystemTweak
{
    /// <summary>
    /// 彻底关闭 Hyper-V / VBS / 内核隔离 / Credential Guard（等效 HyperV-off 类工具）：
    /// bcdedit 关 hypervisor 启动 + DISM 禁用 Hyper-V/虚拟机平台功能 + 相关服务改手动 + DeviceGuard 注册表。
    /// </summary>
    HyperVAndVbs,

    /// <summary>内存压缩（MemCompression）。</summary>
    MemoryCompression,

    /// <summary>分页合并（Page Combining）。</summary>
    PageCombining,

    /// <summary>
    /// 禁用预读取三件套：应用启动预读取（Prefetcher）+ UWP 应用预启动（ApplicationPreLaunch）+ 操作记录 API（OperationAPI）。
    /// Windows Search 已单独列为 <see cref="SystemTweak.WSearchOff"/>。
    /// </summary>
    Prefetch,

    // ---- 游戏微调包（Toggle ON = 应用优化值 / OFF = 还原默认） ----

    /// <summary>鼠标加速关闭（立即生效）。</summary>
    MouseAccelerationOff,

    /// <summary>HAGS 硬件加速 GPU 调度开启（HwSchMode=2，需重启与显卡支持）。</summary>
    HardwareGpuScheduling,

    /// <summary>Windows Search 服务禁用（不动 SysMain：SysMain 承载内存压缩，禁用会连带关掉内存压缩）。</summary>
    WSearchOff,
}

/// <summary>单个优化项的当前状态。</summary>
public sealed class TweakStatus
{
    public required SystemTweak Tweak { get; init; }

    public required string DisplayName { get; init; }

    public required string Description { get; init; }

    /// <summary>true = 已处于优化状态（该功能已被关闭）。</summary>
    public required bool IsOptimized { get; init; }

    /// <summary>状态文字说明（如「已开启」「已关闭」「未配置」）。</summary>
    public required string Detail { get; init; }
}

/// <summary>
/// 系统精简优化服务：Hyper-V/VBS、内存压缩、分页合并、预读取 的状态查询、关闭与恢复。
/// 关闭操作均需重启生效；所有修改前会自动备份原值，可用 <see cref="RestoreAsync"/> 恢复。
/// </summary>
public interface ISystemTweakService
{
    /// <summary>读取全部优化项当前状态（读注册表 + bcdedit + MMAgent 查询，纯只读）。</summary>
    Task<List<TweakStatus>> GetStatusesAsync();

    /// <summary>
    /// 关闭指定功能（重启后生效）。
    /// 注意：<see cref="SystemTweak.HyperVAndVbs"/> 涉及 DISM 禁用功能，可能耗时 1~2 分钟。
    /// </summary>
    Task<OperationResult> DisableAsync(SystemTweak tweak);

    /// <summary>把指定功能恢复到被本工具修改前的状态（无备份记录时报错）。</summary>
    Task<OperationResult> RestoreAsync(SystemTweak tweak);

    /// <summary>枚举本工具留有恢复记录的深度优化项。</summary>
    IReadOnlyList<SystemTweak> GetBackedUpTweaks();

    /// <summary>
    /// 查询 VBS / 内核隔离（HVCI）当前的运行时状态（系统此刻是否真的在跑），
    /// 通过 PowerShell CIM 查询实现，较慢（约 1~2 秒）。改注册表后需重启才会刷新。
    /// </summary>
    Task<string> GetVbsRuntimeSummaryAsync();
}
