using System.ComponentModel;

namespace DeltaNFD.Services;

/// <summary>旧版帧格显卡伪装设置的生效方式，仅为读取旧配置保留。</summary>
public enum GpuSpoofApplyMode
{
    /// <summary>旧版重启模式：保留用于读取历史设置。</summary>
    Reboot,

    /// <summary>旧版设备重载模式：保留用于读取历史设置。</summary>
    DeviceRestart,
}

/// <summary>
/// 帧格服务：游戏运行期间的自动化帧数保障功能。
/// "一键帧格模式" = 激活已选择的帧格功能（主界面总开关，与帧格选项页共享同一份设置）。
/// 总开关的开启/关闭请走 <see cref="ActivateFrameModeAsync"/> / <see cref="DeactivateFrameModeAsync"/> 流程
/// （由 UI 层负责确认对话框）。
/// </summary>
public interface IFrameService : INotifyPropertyChanged
{
    /// <summary>一键帧格模式（只读）= 当前是否处于激活状态（激活状态跨重启保持）。</summary>
    bool FrameModeEnabled { get; }

    /// <summary>帧格功能：检测到三角洲进程启动后自动结束（重启）DWM。</summary>
    bool DwmRestartOnGameStart { get; set; }

    /// <summary>帧格功能：开启一键帧格模式时锁定电源计划（游戏期间防止被其他程序修改），退出时解除。</summary>
    bool PowerPlanLockEnabled { get; set; }

    /// <summary>电源锁定要锁定的目标计划 GUID；空 = 锁定开启帧格时的当前计划（不切换）。</summary>
    string FramePowerLockTargetGuid { get; set; }

    /// <summary>电源计划锁定功能的说明文字（供 UI 展示）。</summary>
    string PowerPlanLockText { get; }

    /// <summary>电源锁定目标计划当前是否配置为指定计划（非「当前计划」模式）。</summary>
    bool HasPowerLockTarget => !string.IsNullOrWhiteSpace(FramePowerLockTargetGuid);

    // ---- 旧版帧格显卡伪装设置（启动迁移兼容；当前界面不再配置此功能） ----

    /// <summary>旧版兼容字段：帧格伪装是否启用；升级时会关闭并迁移还原。</summary>
    bool GpuSpoofEnabled { get; set; }

    /// <summary>旧版兼容字段：是否保存过帧格伪装配置。</summary>
    bool GpuSpoofFrameConfigured { get; set; }

    /// <summary>旧版兼容判定；新的用户界面不再显示此帧格选项。</summary>
    bool IsGpuSpoofInFrame { get; }

    /// <summary>清除旧版帧格伪装配置。</summary>
    void ClearFrameSpoofConfig();

    // ---- 帧格功能：双CCD 专属调度（实验室页登记为「帧格生效」后参与帧格模式） ----

    /// <summary>实验室是否已登记帧格生效的双CCD调度（登记存在时帧格页显示该选项）。</summary>
    bool IsDualCcdArmed { get; }

    /// <summary>从设置重读双CCD登记状态（实验室页登记/撤销后，进入帧格页时刷新）。</summary>
    void RefreshDualCcdArmState();

    /// <summary>帧格功能：双CCD调度是否随帧格模式自动应用/撤销（登记存在时可关）。</summary>
    bool DualCcdSchedulingEnabled { get; set; }

    // ---- 帧格功能：加速系统响应模式（不改电源计划，总开关 + 三个子开关） ----

    /// <summary>加速系统响应模式总开关（关 = 全部机制不生效）。</summary>
    bool ResponseBoostEnabled { get; set; }

    /// <summary>子开关①：核心常驻（防核心停放，PDH 轮询 + 亲和性轮转；帧格激活全程）。</summary>
    bool ResponseBoostCoreParkingEnabled { get; set; }

    /// <summary>子开关②：游戏进程禁用电源限制 EcoQoS（游戏进程存在时逐个应用）。</summary>
    bool ResponseBoostEcoQosEnabled { get; set; }

    /// <summary>子开关③：系统定时器 0.5ms（帧格激活且游戏运行时）。</summary>
    bool ResponseBoostTimerEnabled { get; set; }

    // ---- 帧格功能：前台加速响应（调度参数临时改写，退出帧格自动还原） ----

    /// <summary>前台加速响应总开关（关 = 全部机制不生效）。</summary>
    bool ForegroundBoostEnabled { get; set; }

    /// <summary>子开关①：系统后台资源预留 20%→10%（SystemResponsiveness=0x0A）。</summary>
    bool ForegroundResponsivenessEnabled { get; set; }

    /// <summary>子开关②：前台游戏优先调度（Win32PrioritySeparation=0x1A）。</summary>
    bool ForegroundPriorityEnabled { get; set; }

    // ---- 帧格功能：降低省电延迟（网卡/USB 省电临时关闭，退出帧格自动还原） ----

    /// <summary>降低省电延迟总开关（关 = 全部机制不生效）。</summary>
    bool PowerSaveLatencyEnabled { get; set; }
    Task<IReadOnlyList<FrameIncompletePowerBackup>> GetIncompletePowerBackupsAsync() =>
        Task.FromResult<IReadOnlyList<FrameIncompletePowerBackup>>([]);
    Task<OperationResult> PreserveCurrentPowerValuesAsync(IReadOnlyList<FrameIncompletePowerBackup> confirmed) =>
        Task.FromResult(OperationResult.Fail("当前后端不支持旧电源备份处理。"));

    /// <summary>子开关①：网卡省电全禁（EnablePowerManagement=0 + PnPCapabilities=24）。</summary>
    bool NicPowerSavingOffEnabled { get; set; }

    /// <summary>子开关②：关闭 USB 选择性暂停（全局注册表 + 电源计划 AC/DC）。</summary>
    bool UsbSuspendOffEnabled { get; set; }

    /// <summary>子开关③：关闭 PCIe 省电（电源计划「PCI Express → 链接状态电源管理」ASPM 设为关闭）。</summary>
    bool PcieAspmOffEnabled { get; set; }

    /// <summary>
    /// 本机是否为笔记本形态（三态探测的缓存结果：无法确定按非笔记本处理）。
    /// true 时帧格页对「网卡省电全禁」「关闭 PCIe 省电」显示「笔记本不适用」角标并置灰，
    /// 后端应用时也一并不做这两项。探测为异步，完成前恒为 false，完成后触发 PropertyChanged。
    /// </summary>
    bool IsLaptopChassis { get; }

    // ---- 帧格功能：内存清理（实验性；清空待备内存列表） ----

    /// <summary>内存清理（实验性）：清空待备内存列表释放可用内存；帧格激活期间自动触发 + 可手动执行。</summary>
    bool MemoryCleanEnabled { get; set; }

    /// <summary>内存清理状态文字（上次清理时间与释放量）。</summary>
    string MemoryCleanStatusText { get; }

    /// <summary>游戏内自动清理开关：三角洲运行期间内存占用达到阈值时自动清理（默认开）。</summary>
    bool MemoryCleanGameAutoEnabled { get; set; }

    /// <summary>游戏内自动清理阈值（内存占用百分比，30–95），供文本框绑定（非法输入自动回退旧值）。</summary>
    string MemoryCleanThresholdText { get; set; }

    /// <summary>手动立即执行一次内存清理（不等帧格/游戏状态）。</summary>
    Task<OperationResult> CleanMemoryNowAsync();

    /// <summary>旧版帧格伪装设置的生效方式，保留用于读取既有设置。</summary>
    GpuSpoofApplyMode GpuSpoofApplyMode { get; set; }

    /// <summary>伪装目标显卡的注册表键路径（HKLM 相对路径）。</summary>
    string GpuSpoofRegistryPath { get; set; }

    /// <summary>伪装目标型号。</summary>
    string GpuSpoofFakeName { get; set; }

    /// <summary>伪装功能是否已完成配置（有目标显卡与型号）。</summary>
    bool IsGpuSpoofConfigured { get; }

    /// <summary>伪装功能的配置摘要文字（供 UI 展示）。</summary>
    string GpuSpoofConfigText { get; }

    /// <summary>
    /// 判断指定显卡（HKLM 相对注册表路径）是否正在驱动显示器。
    /// 返回 true=正在驱动（重载会黑屏）；false=确认安全；null=无法确认（按不可重载处理）。
    /// </summary>
    Task<bool?> IsPathDrivingDisplayAsync(string registryPath);

    /// <summary>帧格模式当前是否处于激活状态（激活期间显卡可能处于伪装状态）。</summary>
    bool FrameModeActive { get; }

    /// <summary>帧格功能开关是否允许编辑（帧格模式激活期间锁定，需先退出）。</summary>
    bool FeaturesEditable { get; }

    /// <summary>三角洲进程当前是否在运行。</summary>
    bool IsGameRunning { get; }

    /// <summary>状态文字（帧格功能的最近动作 / 等待状态）。</summary>
    string StatusText { get; }

    /// <summary>开启一键帧格模式：按帧格页配置启用帧格专属功能。旧版显卡伪装配置由启动迁移清理。</summary>
    Task<OperationResult> ActivateFrameModeAsync();

    /// <summary>退出一键帧格模式：关闭帧格专属功能并移除帧格登录任务。</summary>
    Task<OperationResult> DeactivateFrameModeAsync();

    /// <summary>立即重启电脑（5 秒倒计时，强制关闭未保存的应用）。</summary>
    OperationResult RebootNow();

    /// <summary>手动立即重启 DWM（不等待游戏启动；重启瞬间屏幕闪一下属正常现象）。</summary>
    OperationResult RestartDwmNow();

    /// <summary>
    /// 立即重载指定显卡（显卡伪装临时模式用）：按黑屏保护闸门执行——
    /// 未驱动显示器直接重载；台式机驱动中先启动 45 秒自动恢复再重载；笔记本拒绝重载。
    /// </summary>
    Task<OperationResult> ReloadGpuNowAsync(string registryPath);

    /// <summary>创建登录自启动任务（临时伪装的重启还原流程复用；任务名与帧格模式相同）。</summary>
    Task<OperationResult> EnableLogonAutostartAsync();

    /// <summary>删除登录自启动任务。</summary>
    Task<OperationResult> DisableLogonAutostartAsync();
}
