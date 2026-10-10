using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>
/// 机箱形态三态判定：旧版 IsDesktopPcAsync 把「笔记本」和「无法确定」都归为 false，
/// 对黑屏拦截够用，但界面角标必须区分二者（角标只说「笔记本不适用」，不能把不确定说成笔记本）。
/// </summary>
internal enum ChassisKind
{
    /// <summary>无法确定（查询失败 / 机箱类型未定义）：按非笔记本处理 —— 不显示角标，也不拦截功能。</summary>
    Unknown,

    /// <summary>桌面形态（含机架 / 密封机箱）。</summary>
    Desktop,

    /// <summary>便携形态（笔记本 / 平板 / 一体机等）。</summary>
    Laptop,
}

/// <summary>
/// 帧格服务的真实实现（T4 后：编排/展示，执行已迁入 CPU/帧格后端）：
/// - 游戏监控、优先级、亲和性/CPU Sets、双单 CCD、DWM、计时器、加速响应、临时优化、
///   帧格电源守护与内存清理均由后端进程执行；前端只持久化偏好、下发命令并展示状态快照。
/// - 保留在前端：显卡伪装的编排与黑屏兜底（重载执行归显卡后端）、登录自启动、机箱形态探测、
///   玩家亮屏确认。
/// 激活状态与全部开关持久化在 %APPDATA%\Delta NFD\settings.json，跨重启保持。
/// </summary>
public sealed class FrameService : IFrameService
{
    public const string GameProcessName = "DeltaForceClient-Win64-Shipping";

    private const string AutostartTaskName = "DeltaNFD_FrameMode";
    private const string LegacyAutostartTaskName = "DeltaOptimizer_FrameMode";

    private readonly object _gate = new();
    private readonly IGpuSpoofService _spoof = ServiceLocator.GpuSpoof;
    private readonly SemaphoreSlim _modeTransition = new(1, 1);

    /// <summary>构造时若在 UI 线程（ServiceLocator 首次访问发生在页面构造）则捕获派发器，
    /// 保证 PropertyChanged 在 UI 线程触发，x:Bind 才能安全更新。</summary>
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;

    private bool _dwmRestartOnGameStart;
    private bool _powerPlanLockEnabled;
    private string _powerLockTargetGuid = "";
    private bool _gpuSpoofEnabled;
    private bool _gpuSpoofFrameConfigured;
    private bool _dualCcdArmed;
    private bool _dualCcdEnabled = true;
    private bool _responseBoostEnabled;
    private bool _responseBoostCoreParking = true;
    private bool _responseBoostEcoQos = true;
    private bool _responseBoostTimer = true;
    private bool _memoryCleanEnabled;
    private bool _foregroundBoostEnabled;
    private bool _foregroundResponsiveness = true;
    private bool _foregroundPriority = true;
    private bool _powerSaveLatencyEnabled;
    private bool _nicPowerSavingOff = true;
    private bool _usbSuspendOff = true;
    private bool _pcieAspmOff = true;
    private bool _isLaptopChassis;
    private GpuSpoofApplyMode _gpuSpoofApplyMode = GpuSpoofApplyMode.Reboot;
    private string _gpuSpoofRegistryPath = "";
    private string _gpuSpoofFakeName = "";
    private bool _frameModeActive;
    private bool _isGameRunning;
    private string _statusText = "";

    /// <summary>本进程内是否已成功完成过一次帧格激活（区分「本次刚激活」与「上次异常退出遗留的陈旧激活」）。</summary>
    private bool _appliedThisSession;

    private string _memoryCleanStatusText = "尚未清理";
    private bool _memoryCleanGameAutoEnabled = true;
    private int _memoryCleanThresholdPercent = 80;
    private string _memoryCleanThresholdText = "80";

    public event PropertyChangedEventHandler? PropertyChanged;

    public FrameService()
    {
        try
        {
            _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        }
        catch
        {
            _dispatcher = null; // 无 UI 上下文（如控制台测试）时直接在当前线程触发事件
        }

        LoadSettings();
        var automationBlocked = OfflineModeGuard.BlocksNormalAutomation;
        if (_frameModeActive && _powerPlanLockEnabled && !string.IsNullOrWhiteSpace(AppSettingsStore.Read().FramePowerPreviousSchemeGuid))
            AppSettingsStore.Update(s => s.FramePowerRestorePending = true);
        if (!_frameModeActive && AppSettingsStore.Read().FramePowerRestorePending)
            GameTargetService.Default.SetRestoreFailure("帧格电源计划", true);
        if (!automationBlocked) MigrateLegacyFrameGpuSpoof();
        _statusText = automationBlocked ? "脱机模式或恢复流程中；普通自动化已暂停" : DescribeStatus();
        RefreshDualCcdArmState();

        if (_frameModeActive && !automationBlocked)
        {
            _ = MigrateLegacyAutostartTaskAsync();
            _ = SelfHealSpoofAsync();
        }
        var startupSettings = AppSettingsStore.Read();
        if (startupSettings.TempSpoofRestorePending && !automationBlocked)
        {
            // 临时重启生效伪装的收尾：登录后还原原显卡型号
            _ = RestoreTempSpoofAtLogonAsync();
        }
        else if (!_frameModeActive && !automationBlocked)
        {
            // 清理还原/退出流程中断后遗留的一次性登录任务。
            _ = DisableLogonAutostartAsync();
        }

        ServiceLocator.GameMonitor.Tick += OnGameMonitorTick;
        // 后端状态：显示实际生效状态（游戏运行、内存清理状态文字）。
        Backends.BackendCpuFrameService.Instance.StatusUpdated += OnCpuFrameStatus;
        if (!automationBlocked) _ = RestorePersistentCpuSchedulingAsync();
        // 帧格模式跨重启保持：重新向后端下发加速响应/临时优化/电源锁定的期望。
        if (_frameModeActive && !automationBlocked) _ = SyncBackendExecutionAsync();

        // 机箱形态探测一次：供帧格页角标与后端跳过网卡/PCIe 省电的编排依据。
        _ = ProbeLaptopChassisAsync();
    }

    /// <summary>后端状态快照到达（后台线程）：更新实际生效状态文字与游戏运行态。</summary>
    private void OnCpuFrameStatus(CpuFrameStatusSnapshot snapshot)
    {
        void Apply()
        {
            _memoryCleanStatusText = string.IsNullOrEmpty(snapshot.MemoryCleanStatus) ? _memoryCleanStatusText : snapshot.MemoryCleanStatus;
            if (snapshot.GameRunning != _isGameRunning)
            {
                _isGameRunning = snapshot.GameRunning;
                OnPropertyChanged(nameof(IsGameRunning));
            }
            OnPropertyChanged(nameof(MemoryCleanStatusText));
            UpdateStatus();
        }

        if (_dispatcher is null || _dispatcher.HasThreadAccess) Apply();
        else _dispatcher.TryEnqueue(Apply);
    }

    /// <summary>应用启动后重新登记 CPU 亲和性规则及已保存的立即生效调度（交由 CPU/帧格后端执行）。</summary>
    private async Task RestorePersistentCpuSchedulingAsync()
    {
        try
        {
            if (OfflineModeGuard.BlocksNormalAutomation) return;
            var cpuFrame = Backends.BackendCpuFrameService.Instance;
            var settings = AppSettingsStore.Read();
            var frameDualWillRestore = settings.FrameModeActive
                && settings.DualCcdArmed
                && settings.DualCcdFrameEnabled;

            OperationResult? result = null;
            if (!frameDualWillRestore && settings.DualCcdImmediateEnabled)
            {
                result = await cpuFrame.ApplyDualCcdAsync(settings.DualCcdGameCcdIndex);
            }
            else if (!frameDualWillRestore && settings.SingleCcdExcludeCpu0Enabled)
            {
                result = await cpuFrame.ApplySingleCcdAsync();
            }

            if (result is not null)
            {
                Log.Info(result.Success
                    ? $"CPU调度：已按启动设置恢复（后端）。{result.Message}"
                    : $"CPU调度：启动恢复失败，保留设置待下次重试。{result.Message}");
            }

            settings = AppSettingsStore.Read();
            if (settings.GameAffinityRuleEnabled && settings.GameAffinityRuleMask != 0)
            {
                await cpuFrame.ApplyAffinityAsync(settings.GameAffinityRuleMask);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"CPU调度：启动恢复异常：{ex.Message}");
        }
    }

    // ---------------- 开关与配置（持久化） ----------------

    public bool DwmRestartOnGameStart
    {
        get => _dwmRestartOnGameStart;
        set
        {
            if (value && OfflineModeGuard.BlocksNormalAutomation)
            {
                Log.Warn("脱机模式或恢复流程中，拒绝启用自动 DWM 重启。");
                return;
            }
            if (_dwmRestartOnGameStart == value) return;
            _dwmRestartOnGameStart = value;
            SaveSettings();
            UpdateStatus();
            OnPropertyChanged();
        }
    }

    public bool PowerPlanLockEnabled
    {
        get => _powerPlanLockEnabled;
        set
        {
            if (_powerPlanLockEnabled == value) return;
            _powerPlanLockEnabled = value;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(PowerPlanLockText));
            _ = SyncBackendExecutionAsync();
        }
    }

    public string FramePowerLockTargetGuid
    {
        get => _powerLockTargetGuid;
        set
        {
            var normalized = (value ?? "").Trim();
            if (_powerLockTargetGuid == normalized) return;
            _powerLockTargetGuid = normalized;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(PowerPlanLockText));
        }
    }

    public string PowerPlanLockText =>
        string.IsNullOrWhiteSpace(_powerLockTargetGuid)
            ? "开启一键帧格模式时记住并锁定你当前的电源计划（不切换别的计划），并快照该计划的全部设置；游戏期间每 30 秒检查一次，既防止被其他程序改成省电方案，也会把被改动的计划内容（含 CPU实验室 的「异类调度策略」）按快照自动还原。帧格自己临时改写的省电项（USB 选择性暂停 / PCIE 链接状态电源管理）不在保护范围内。退出帧格模式时解除锁定并还原。"
            : "开启一键帧格模式时切换并锁定到你选择的电源计划，并快照该计划的全部设置；游戏期间每 30 秒检查一次，被改走会自动切回，被改动的计划内容（含 CPU实验室 的「异类调度策略」）也会按快照自动还原。帧格自己临时改写的省电项（USB 选择性暂停 / PCIE 链接状态电源管理）不在保护范围内。退出帧格模式时还原为开启前的原计划。";

    public bool GpuSpoofEnabled
    {
        get => _gpuSpoofEnabled;
        set
        {
            if (_gpuSpoofEnabled == value) return;
            _gpuSpoofEnabled = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    public bool GpuSpoofFrameConfigured
    {
        get => _gpuSpoofFrameConfigured;
        set
        {
            if (_gpuSpoofFrameConfigured == value) return;
            _gpuSpoofFrameConfigured = value;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsGpuSpoofInFrame));
            OnPropertyChanged(nameof(GpuSpoofConfigText));
            UpdateStatus();
        }
    }

    /// <summary>旧版显卡伪装兼容判定；新界面不再提供帧格伪装。</summary>
    public bool IsGpuSpoofInFrame => IsGpuSpoofConfigured && _gpuSpoofFrameConfigured;

    /// <summary>清除旧版帧格伪装配置。</summary>
    public void ClearFrameSpoofConfig()
    {
        _gpuSpoofRegistryPath = "";
        _gpuSpoofFakeName = "";
        _gpuSpoofEnabled = false;
        _gpuSpoofFrameConfigured = false;
        SaveSettings();
        OnPropertyChanged(nameof(GpuSpoofRegistryPath));
        OnPropertyChanged(nameof(GpuSpoofFakeName));
        OnPropertyChanged(nameof(IsGpuSpoofConfigured));
        OnPropertyChanged(nameof(GpuSpoofConfigText));
        OnPropertyChanged(nameof(IsGpuSpoofInFrame));
        UpdateStatus();
    }

    /// <summary>实验室是否已登记帧格生效的双CCD调度（登记存在时帧格页显示该选项）。</summary>
    public bool IsDualCcdArmed
    {
        get => _dualCcdArmed;
        private set
        {
            if (_dualCcdArmed == value) return;
            _dualCcdArmed = value;
            OnPropertyChanged();
            UpdateStatus();
        }
    }

    /// <summary>从设置重读双CCD登记状态（实验室页登记/撤销后由帧格页进入时刷新）。</summary>
    public void RefreshDualCcdArmState() => IsDualCcdArmed = AppSettingsStore.Read().DualCcdArmed;

    /// <summary>帧格功能：双CCD调度是否随帧格模式自动应用/撤销（登记存在时可关，默认开）。</summary>
    public bool DualCcdSchedulingEnabled
    {
        get => _dualCcdEnabled;
        set
        {
            if (_dualCcdEnabled == value) return;
            _dualCcdEnabled = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    // ---------------- 加速系统响应模式（执行在后端） ----------------

    /// <summary>加速系统响应模式总开关（关 = 全部机制不生效）。</summary>
    public bool ResponseBoostEnabled
    {
        get => _responseBoostEnabled;
        set
        {
            if (_responseBoostEnabled == value) return;
            _responseBoostEnabled = value;
            SaveSettings();
            OnPropertyChanged();
            UpdateStatus();
            _ = SyncBackendExecutionAsync();
        }
    }

    public bool ResponseBoostCoreParkingEnabled
    {
        get => _responseBoostCoreParking;
        set
        {
            if (_responseBoostCoreParking == value) return;
            _responseBoostCoreParking = value;
            SaveSettings();
            OnPropertyChanged();
            _ = SyncBackendExecutionAsync();
        }
    }

    public bool ResponseBoostEcoQosEnabled
    {
        get => _responseBoostEcoQos;
        set
        {
            if (_responseBoostEcoQos == value) return;
            _responseBoostEcoQos = value;
            SaveSettings();
            OnPropertyChanged();
            _ = SyncBackendExecutionAsync();
        }
    }

    public bool ResponseBoostTimerEnabled
    {
        get => _responseBoostTimer;
        set
        {
            if (_responseBoostTimer == value) return;
            _responseBoostTimer = value;
            SaveSettings();
            OnPropertyChanged();
            _ = SyncBackendExecutionAsync();
        }
    }

    /// <summary>内存清理（实验性）：清空待备内存列表释放可用内存；帧格激活期间自动触发，可随时手动执行。</summary>
    public bool MemoryCleanEnabled
    {
        get => _memoryCleanEnabled;
        set
        {
            if (_memoryCleanEnabled == value) return;
            _memoryCleanEnabled = value;
            SaveSettings();
            OnPropertyChanged();
            UpdateStatus();
            if (value && _frameModeActive) _ = Backends.BackendCpuFrameService.Instance.CleanMemoryNowAsync();
        }
    }

    // ---------------- 前台加速响应（执行在后端，退出帧格自动还原） ----------------

    public bool ForegroundBoostEnabled
    {
        get => _foregroundBoostEnabled;
        set
        {
            if (_foregroundBoostEnabled == value) return;
            _foregroundBoostEnabled = value;
            SaveSettings();
            OnPropertyChanged();
            UpdateStatus();
            _ = SyncBackendExecutionAsync();
        }
    }

    public bool ForegroundResponsivenessEnabled
    {
        get => _foregroundResponsiveness;
        set
        {
            if (_foregroundResponsiveness == value) return;
            _foregroundResponsiveness = value;
            SaveSettings();
            OnPropertyChanged();
            _ = SyncBackendExecutionAsync();
        }
    }

    public bool ForegroundPriorityEnabled
    {
        get => _foregroundPriority;
        set
        {
            if (_foregroundPriority == value) return;
            _foregroundPriority = value;
            SaveSettings();
            OnPropertyChanged();
            _ = SyncBackendExecutionAsync();
        }
    }

    // ---------------- 降低省电延迟（执行在后端，退出帧格自动还原） ----------------

    public bool PowerSaveLatencyEnabled
    {
        get => _powerSaveLatencyEnabled;
        set
        {
            if (_powerSaveLatencyEnabled == value) return;
            _powerSaveLatencyEnabled = value;
            SaveSettings();
            OnPropertyChanged();
            UpdateStatus();
            _ = SyncBackendExecutionAsync();
        }
    }

    public bool NicPowerSavingOffEnabled
    {
        get => _nicPowerSavingOff;
        set
        {
            if (_nicPowerSavingOff == value) return;
            _nicPowerSavingOff = value;
            SaveSettings();
            OnPropertyChanged();
            _ = SyncBackendExecutionAsync();
        }
    }

    public bool UsbSuspendOffEnabled
    {
        get => _usbSuspendOff;
        set
        {
            if (_usbSuspendOff == value) return;
            _usbSuspendOff = value;
            SaveSettings();
            OnPropertyChanged();
            _ = SyncBackendExecutionAsync();
        }
    }

    public bool PcieAspmOffEnabled
    {
        get => _pcieAspmOff;
        set
        {
            if (_pcieAspmOff == value) return;
            _pcieAspmOff = value;
            SaveSettings();
            OnPropertyChanged();
            _ = SyncBackendExecutionAsync();
        }
    }

    /// <summary>本机是否为笔记本形态（惰性探测的缓存结果，进程内最多探测一次）。</summary>
    public bool IsLaptopChassis => _isLaptopChassis;

    /// <summary>内存清理状态文字（来自后端实际执行结果）。</summary>
    public string MemoryCleanStatusText => _memoryCleanStatusText;

    public bool MemoryCleanGameAutoEnabled
    {
        get => _memoryCleanGameAutoEnabled;
        set
        {
            if (_memoryCleanGameAutoEnabled == value) return;
            _memoryCleanGameAutoEnabled = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    public string MemoryCleanThresholdText
    {
        get => _memoryCleanThresholdText;
        set
        {
            if (int.TryParse((value ?? "").Trim(), out var pct) && pct is >= 30 and <= 95
                && pct != _memoryCleanThresholdPercent)
            {
                _memoryCleanThresholdPercent = pct;
                _memoryCleanThresholdText = pct.ToString();
                SaveSettings();
                Log.Info($"内存清理：游戏内自动清理阈值调整为 {pct}%");
            }

            OnPropertyChanged();
        }
    }

    public GpuSpoofApplyMode GpuSpoofApplyMode
    {
        get => _gpuSpoofApplyMode;
        set
        {
            if (_gpuSpoofApplyMode == value) return;
            _gpuSpoofApplyMode = value;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(GpuSpoofConfigText));
        }
    }

    public string GpuSpoofRegistryPath
    {
        get => _gpuSpoofRegistryPath;
        set
        {
            if (_gpuSpoofRegistryPath == value) return;
            _gpuSpoofRegistryPath = value ?? "";
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsGpuSpoofConfigured));
            OnPropertyChanged(nameof(IsGpuSpoofInFrame));
            OnPropertyChanged(nameof(GpuSpoofConfigText));
        }
    }

    public string GpuSpoofFakeName
    {
        get => _gpuSpoofFakeName;
        set
        {
            if (_gpuSpoofFakeName == value) return;
            _gpuSpoofFakeName = value ?? "";
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsGpuSpoofConfigured));
            OnPropertyChanged(nameof(IsGpuSpoofInFrame));
            OnPropertyChanged(nameof(GpuSpoofConfigText));
        }
    }

    public bool IsGpuSpoofConfigured =>
        !string.IsNullOrWhiteSpace(_gpuSpoofRegistryPath) && !string.IsNullOrWhiteSpace(_gpuSpoofFakeName);

    public string GpuSpoofConfigText =>
        !IsGpuSpoofConfigured
            ? "当前不支持将显卡伪装加入帧格模式"
            : $"伪装为「{_gpuSpoofFakeName}」 · {(GpuSpoofApplyMode == GpuSpoofApplyMode.Reboot ? "重启生效" : "不重启生效（重载显卡）")}";

    public bool FrameModeEnabled => _frameModeActive;

    public bool FrameModeActive
    {
        get => _frameModeActive;
        private set
        {
            if (_frameModeActive == value) return;
            _frameModeActive = value;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(FrameModeEnabled));
            OnPropertyChanged(nameof(FeaturesEditable));
            RefreshDualCcdArmState();
        }
    }

    public bool FeaturesEditable => !_frameModeActive;

    public bool IsGameRunning
    {
        get => _isGameRunning;
        private set
        {
            if (_isGameRunning == value) return;
            _isGameRunning = value;
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value) return;
            _statusText = value;
            OnPropertyChanged();
        }
    }

    // ---------------- 帧格模式激活 / 退出（编排 + 后端执行） ----------------

    public Task<IReadOnlyList<FrameIncompletePowerBackup>> GetIncompletePowerBackupsAsync() =>
        Backends.BackendCpuFrameService.Instance.GetIncompletePowerBackupsAsync();

    public async Task<OperationResult> PreserveCurrentPowerValuesAsync(IReadOnlyList<FrameIncompletePowerBackup> confirmed)
    {
        if (!ElevationHelper.IsElevated || OfflineModeGuard.BlocksNormalAutomation)
            return OperationResult.Fail("管理员权限不足或脱机恢复正在运行，未处理旧记录。");
        await _modeTransition.WaitAsync();
        try
        {
            if (_frameModeActive) return OperationResult.Fail("帧格正在运行，禁止隔离恢复记录。");
            return await Backends.BackendCpuFrameService.Instance.PreserveCurrentPowerValuesAsync(confirmed);
        }
        finally { _modeTransition.Release(); }
    }

    public async Task<OperationResult> ActivateFrameModeAsync()
    {
        if (OfflineModeGuard.BlocksNormalAutomation)
            return OperationResult.Fail("脱机模式或恢复流程正在运行，普通帧格模式已锁定。");
        await _modeTransition.WaitAsync();
        try { return await Backends.FrameBackendReadiness.RunAsync(ActivateFrameModeCoreAsync, "开启", IsGpuSpoofInFrame && _gpuSpoofEnabled); }
        finally { _modeTransition.Release(); }
    }

    private async Task<OperationResult> ActivateFrameModeCoreAsync()
    {
        using var targetOperation = GameTargetService.Default.BeginOperation();
        if (!ElevationHelper.IsElevated)
        {
            Log.Warn("帧格激活失败：未以管理员运行");
            return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        }

        Log.Info("── 开启一键帧格模式 ──");
        if (_frameModeActive && _appliedThisSession)
            return OperationResult.Ok("帧格模式已激活，未重复应用。");

        // 陈旧激活状态：上次异常退出把 FrameModeActive 留在 true，但本进程从未真正应用过。
        // 不短路为「已激活」，先按旧记录幂等收敛（UnlockPower + RevertTweaks），再继续走完整激活流程。
        // 这保证异常退出后无需先成功关闭一次即可重新开启；恢复失败仍拒绝，不覆盖未知原值。
        if (_frameModeActive && !_appliedThisSession)
            Log.Info("帧格模式：检测到上次异常退出遗留的激活标记，先收敛旧记录再重新应用。");

        // 上次退出未完成时先按旧记录重试恢复，失败则拒绝重新应用，避免覆盖原值。
        var cpuFrame = Backends.BackendCpuFrameService.Instance;
        var pendingPower = await cpuFrame.UnlockPowerAsync();
        var pendingTweaks = await cpuFrame.RevertTweaksAsync();
        
        // 区分"无待恢复记录（幂等成功）"与"恢复失败（有原值但还原失败）"
        var powerBlocked = !pendingPower.Success && pendingPower.Message.Contains("失败");
        var tweaksBlocked = !pendingTweaks.Success && pendingTweaks.Message.Contains("失败");
        
        if (powerBlocked || tweaksBlocked)
        {
            var details = new List<string>();
            if (powerBlocked) 
            {
                details.Add($"电源锁定恢复失败：{pendingPower.Message}");
                Log.Error($"帧格激活前恢复：电源锁定恢复失败，{pendingPower.Message}");
            }
            if (tweaksBlocked) 
            {
                details.Add($"临时优化恢复失败：{pendingTweaks.Message}");
                Log.Error($"帧格激活前恢复：临时优化恢复失败，{pendingTweaks.Message}");
            }
            
            return OperationResult.Fail(
                "帧格激活前的旧记录恢复失败，为避免覆盖原值，已阻止重新开启。" +
                "请在「帧格」页面查看恢复状态，手动解决失败项后再试。" +
                Environment.NewLine + string.Join(Environment.NewLine, details));
        }
        
        // 幂等成功（无待恢复记录）或已成功还原，可以继续
        if (pendingPower.Success && pendingTweaks.Success)
        {
            Log.Info($"帧格激活前恢复：旧记录已清理完成。{pendingPower.Message} {pendingTweaks.Message}");
        }

        var useSpoof = IsGpuSpoofInFrame && _gpuSpoofEnabled;
        var appliedImmediately = false;
        var needsDisplayConfirm = false;

        if (useSpoof)
        {
            var spoofResult = await _spoof.SpoofByPathAsync(GpuSpoofRegistryPath, GpuSpoofFakeName);
            if (!spoofResult.Success)
            {
                return OperationResult.Fail($"帧格模式开启失败（显卡伪装未写入）：{spoofResult.Message}");
            }

            if (GpuSpoofApplyMode == GpuSpoofApplyMode.Reboot)
            {
                var autostart = await EnableAutostartAsync();
                if (!autostart.Success)
                {
                    return OperationResult.Fail($"显卡已伪装，但创建登录自启动任务失败：{autostart.Message}");
                }
            }
            else
            {
                if (await IsPathDrivingDisplayAsync(GpuSpoofRegistryPath) == false)
                {
                    var reload = await RestartGpuDeviceAsync(GpuSpoofRegistryPath);
                    if (!reload.Success)
                    {
                        return OperationResult.Fail($"显卡已伪装，但重载显卡失败：{reload.Message}");
                    }

                    appliedImmediately = true;
                }
                else if (await IsDesktopPcAsync())
                {
                    var instanceId = ToDeviceInstanceId(GpuSpoofRegistryPath);
                    if (string.IsNullOrWhiteSpace(instanceId))
                    {
                        return OperationResult.Fail("无法从注册表路径解析设备实例 ID。");
                    }

                    StartRecoveryProcess(instanceId, 45);
                    var core = await RestartGpuDeviceCoreAsync(instanceId);
                    if (!core.Success)
                    {
                        return OperationResult.Fail(
                            $"显卡已伪装，但重载显卡失败：{core.Message}（已启动 45 秒自动恢复）");
                    }

                    appliedImmediately = true;
                    needsDisplayConfirm = true;
                }
                else
                {
                    var autostart = await EnableAutostartAsync();
                    if (!autostart.Success)
                    {
                        return OperationResult.Fail($"显卡已伪装，但创建登录自启动任务失败：{autostart.Message}");
                    }
                }
            }
        }

        FrameModeActive = true;
        _appliedThisSession = true;

        // 双CCD 专属调度（帧格生效模式）：帧格开启时自动应用，退出时自动撤销。
        var dualNote = await ApplyArmedDualCcdAsync();

        // 加速响应 / 临时优化 / 电源锁定：由后端执行。
        var notes = dualNote + await SyncBackendExecutionAsync();

        // 内存清理（实验性）：开启帧格时立即执行一次，给游戏腾出干净的可用内存
        if (_memoryCleanEnabled)
        {
            _ = cpuFrame.CleanMemoryNowAsync();
        }

        if (!useSpoof)
        {
            Log.Info("帧格模式已开启（无伪装配置）");
            return OperationResult.Ok("帧格模式已开启（各功能按帧格页开关执行）。" + notes);
        }

        if (GpuSpoofApplyMode == GpuSpoofApplyMode.Reboot || !appliedImmediately)
        {
            return OperationResult.Ok(
                $"帧格模式已开启：显卡已伪装为「{GpuSpoofFakeName}」，重启电脑后生效。" +
                "已创建登录自启动任务，重启登录后本程序自动运行并保持帧格模式。" + notes,
                requiresReboot: true);
        }

        if (needsDisplayConfirm)
        {
            return OperationResult.Ok(
                $"帧格模式已开启：显卡已伪装为「{GpuSpoofFakeName}」并重载立即生效。" +
                "请在 1 分钟内确认显示器处于亮屏状态，超时未确认电脑将自动重启以恢复显示。" + notes,
                requiresDisplayConfirm: true);
        }

        return OperationResult.Ok($"帧格模式已开启：显卡已伪装为「{GpuSpoofFakeName}」并重载立即生效。" + notes);
    }

    public async Task<OperationResult> DeactivateFrameModeAsync()
    {
        await _modeTransition.WaitAsync();
        try { return await Backends.FrameBackendReadiness.RunAsync(DeactivateFrameModeCoreAsync, "退出", IsGpuSpoofInFrame); }
        finally { _modeTransition.Release(); }
    }

    private async Task<OperationResult> DeactivateFrameModeCoreAsync()
    {
        using var targetOperation = GameTargetService.Default.BeginOperation();
        if (!ElevationHelper.IsElevated)
        {
            Log.Warn("帧格退出失败：未以管理员运行");
            return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        }

        Log.Info("── 退出一键帧格模式 ──");
        var useSpoof = IsGpuSpoofInFrame;
        var skippedReload = false;
        var needsDisplayConfirm = false;
        if (useSpoof)
        {
            var restore = await _spoof.RestoreByPathAsync(GpuSpoofRegistryPath);
            if (!restore.Success)
            {
                return OperationResult.Fail($"帧格模式退出失败（显卡未还原，模式保持开启）：{restore.Message}");
            }

            if (GpuSpoofApplyMode == GpuSpoofApplyMode.DeviceRestart)
            {
                var drivesDisplay = await IsPathDrivingDisplayAsync(GpuSpoofRegistryPath);
                if (drivesDisplay == false)
                {
                    var reload = await RestartGpuDeviceAsync(GpuSpoofRegistryPath);
                    if (!reload.Success)
                    {
                        return OperationResult.Fail(
                            $"显卡已还原，但重载显卡失败：{reload.Message}（原始型号将在重启电脑后生效）");
                    }
                }
                else if (drivesDisplay == true && await IsDesktopPcAsync())
                {
                    var instanceId = ToDeviceInstanceId(GpuSpoofRegistryPath);
                    if (string.IsNullOrWhiteSpace(instanceId))
                    {
                        return OperationResult.Fail("无法从注册表路径解析设备实例 ID。");
                    }

                    StartRecoveryProcess(instanceId, 45);
                    var core = await RestartGpuDeviceCoreAsync(instanceId);
                    if (!core.Success)
                    {
                        return OperationResult.Fail(
                            $"显卡已还原，但重载显卡失败：{core.Message}（已启动 45 秒自动恢复）");
                    }

                    needsDisplayConfirm = true;
                }
                else
                {
                    // 笔记本/未知形态：跳过重载，靠重启恢复原始型号
                    skippedReload = true;
                }
            }
        }

        var needsReboot = useSpoof && (GpuSpoofApplyMode == GpuSpoofApplyMode.Reboot || skippedReload);

        await DisableAutostartAsync();

        FrameModeActive = false;
        GpuSpoofEnabled = false;

        var dualNote = await RevertArmedDualCcdAsync();

        // 退出帧格：后端撤销加速响应、临时优化与电源锁定；失败时如实报告并保留恢复记录。
        var cpuFrame = Backends.BackendCpuFrameService.Instance;
        await cpuFrame.ApplyBoostAsync(false, false, false);
        var tweaksNote = await cpuFrame.RevertTweaksAsync();
        var powerNote = await cpuFrame.UnlockPowerAsync();
        var notes = dualNote + " " + tweaksNote.Message + " " + powerNote.Message;

        if (!powerNote.Success || !tweaksNote.Success)
            return new OperationResult
            {
                Success = false,
                RequiresReboot = needsReboot,
                Message = "帧格已停用，但部分设置恢复未完成，已保留恢复记录，请再次执行退出恢复。" + notes,
            };

        if (!useSpoof) return OperationResult.Ok("帧格模式已退出。" + notes);

        if (needsReboot)
        {
            return OperationResult.Ok("已还原显卡型号并退出帧格模式；重启电脑后恢复原始型号显示。" + notes, requiresReboot: true);
        }

        if (needsDisplayConfirm)
        {
            return OperationResult.Ok(
                "已还原显卡原始型号并重载立即生效，帧格模式已退出。" +
                "请在 1 分钟内确认显示器处于亮屏状态，超时未确认电脑将自动重启以恢复显示。" + notes,
                requiresDisplayConfirm: true);
        }

        return OperationResult.Ok("已还原显卡原始型号并重载立即生效，帧格模式已退出。" + notes);
    }

    /// <summary>
    /// 按当前设置与帧格状态把加速响应/临时优化/电源锁定的期望下发给后端（后端执行）。
    /// 返回可拼接的状态说明。
    /// </summary>
    private async Task<string> SyncBackendExecutionAsync()
    {
        try
        {
            var settings = AppSettingsStore.Read();
            var active = _frameModeActive && !OfflineModeGuard.BlocksNormalAutomation;
            var cpuFrame = Backends.BackendCpuFrameService.Instance;
            if (!active)
            {
                await cpuFrame.ApplyBoostAsync(false, false, false);
                await cpuFrame.RevertTweaksAsync();
                await cpuFrame.UnlockPowerAsync();
                return "";
            }

            var boost = settings.FrameResponseBoostEnabled;
            await cpuFrame.ApplyBoostAsync(
                boost && settings.FrameResponseBoostCoreParkingEnabled,
                boost && settings.FrameResponseBoostEcoQosEnabled,
                boost && settings.FrameResponseBoostTimerEnabled);

            var notes = new List<string>();
            var laptop = await GetChassisKindAsync() == ChassisKind.Laptop;
            if (settings.FrameForegroundBoostEnabled || settings.FramePowerSaveLatencyEnabled)
            {
                var tweaks = await cpuFrame.ApplyTweaksAsync(new CpuFrameTempArgs
                {
                    ForegroundResponsiveness = settings.FrameForegroundBoostEnabled && settings.FrameForegroundResponsivenessEnabled,
                    ForegroundPriority = settings.FrameForegroundBoostEnabled && settings.FrameForegroundPriorityEnabled,
                    NicPowerOff = settings.FramePowerSaveLatencyEnabled && settings.FrameNicPowerSavingOffEnabled,
                    UsbSuspendOff = settings.FramePowerSaveLatencyEnabled && settings.FrameUsbSuspendOffEnabled,
                    PcieAspmOff = settings.FramePowerSaveLatencyEnabled && settings.FramePcieAspmOffEnabled,
                    LaptopExcluded = laptop,
                });
                if (tweaks.Message.Length > 0) notes.Add(tweaks.Message.TrimEnd('。'));
            }
            else
            {
                await cpuFrame.RevertTweaksAsync();
            }

            if (settings.FramePowerLockEnabled)
            {
                var power = await cpuFrame.SetPowerLockAsync(true, _powerLockTargetGuid);
                if (!power.Success) notes.Add("电源锁定：" + power.Message);
            }
            else
            {
                await cpuFrame.UnlockPowerAsync();
            }

            await cpuFrame.EnsureMonitorAsync();
            return notes.Count == 0 ? "" : " " + string.Join("；", notes) + "。";
        }
        catch (Exception ex)
        {
            Log.Warn("帧格执行同步失败：" + ex.Message);
            return " 帧格执行同步失败：" + ex.Message;
        }
    }

    // ---------------- 双CCD 专属调度（帧格生效模式，执行在后端） ----------------

    private async Task<string> ApplyArmedDualCcdAsync()
    {
        try
        {
            var s = AppSettingsStore.Read();
            if (!s.DualCcdArmed || !s.DualCcdFrameEnabled) return "";
            var result = await Backends.BackendCpuFrameService.Instance.ApplyDualCcdAsync(s.DualCcdGameCcdIndex);
            return result.Success
                ? $"双CCD调度已应用：游戏独占 CCD{s.DualCcdGameCcdIndex}，其他进程在另一个 CCD。"
                : $"双CCD调度应用失败：{result.Message}";
        }
        catch (Exception ex)
        {
            return $"双CCD调度应用失败：{ex.Message}";
        }
    }

    private async Task<string> RevertArmedDualCcdAsync()
    {
        try
        {
            var s = AppSettingsStore.Read();
            if (!s.DualCcdArmed || !s.DualCcdFrameEnabled) return "";

            var cpuFrame = Backends.BackendCpuFrameService.Instance;
            var result = await cpuFrame.RevertDualCcdAsync();
            if (!result.Success) return $"双CCD调度撤销失败：{result.Message}";

            var note = "双CCD调度已撤销，所有进程恢复全部核心。";
            if (s.SingleCcdExcludeCpu0Enabled)
            {
                var singleCcd = await cpuFrame.ApplySingleCcdAsync();
                note += singleCcd.Success
                    ? "已恢复先前保存的单CCD排除 CPU0 规则。"
                    : $"单CCD规则恢复失败：{singleCcd.Message}";
            }

            return note;
        }
        catch (Exception ex)
        {
            return $"双CCD调度撤销失败：{ex.Message}";
        }
    }

    // ---------------- 内存清理（执行在后端） ----------------

    /// <summary>手动立即执行一次内存清理（交由 CPU/帧格后端执行）。</summary>
    public async Task<OperationResult> CleanMemoryNowAsync()
        => await Backends.BackendCpuFrameService.Instance.CleanMemoryNowAsync();

    // ---------------- 后端状态同步（游戏运行态） ----------------

    private void OnGameMonitorTick(bool gameRunning, int? unusedProcessId)
    {
        if (OfflineModeGuard.BlocksNormalAutomation) return;
        if (gameRunning != _isGameRunning)
        {
            IsGameRunning = gameRunning;
            UpdateStatus();
        }
    }

    private void UpdateStatus() => StatusText = DescribeStatus();

    private string DescribeStatus()
    {
        if (!_frameModeActive && !_dwmRestartOnGameStart) return "帧格功能未启用";

        if (_frameModeActive)
        {
            var spoofPart = IsGpuSpoofInFrame && _gpuSpoofEnabled
                ? $" · 显卡伪装为「{_gpuSpoofFakeName}」" : "";
            var powerPart = _powerPlanLockEnabled
                ? (string.IsNullOrWhiteSpace(_powerLockTargetGuid) ? " · 电源计划锁定" : " · 电源计划锁定（指定计划）") : "";
            var dualPart = _dualCcdArmed && _dualCcdEnabled ? " · 双CCD调度" : "";
            var boostPart = _responseBoostEnabled ? " · 加速响应" : "";
            var foregroundPart = _foregroundBoostEnabled ? " · 前台加速" : "";
            var powerSavePart = _powerSaveLatencyEnabled ? " · 省电延迟" : "";
            var memCleanPart = _memoryCleanEnabled ? " · 内存清理" : "";
            var target = GameTargetService.Default.Current.DisplayName;
            var gamePart = _isGameRunning ? $" · {target}运行中" : $" · 等待 {target} 启动";
            return $"帧格模式运行中{spoofPart}{powerPart}{dualPart}{boostPart}{foregroundPart}{powerSavePart}{memCleanPart}{gamePart}";
        }

        return GameTargetService.Default.Status;
    }

    // ---------------- DWM 重启（执行在显卡/系统外，由 CPU/帧格后端执行） ----------------

    /// <summary>手动立即重启 DWM（交由 CPU/帧格后端执行）。</summary>
    public async Task<OperationResult> RestartDwmNowAsync()
        => await Backends.BackendCpuFrameService.Instance.RestartDwmAsync();

    // ---------------- 帧格模式开机自愈（显卡伪装部分保留前端编排） ----------------

    private async Task SelfHealSpoofAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            if (!_frameModeActive || !IsGpuSpoofInFrame || !_gpuSpoofEnabled) return;

            var current = ReadDeviceDesc(GpuSpoofRegistryPath);
            if (current is not null && !current.Equals(GpuSpoofFakeName, StringComparison.OrdinalIgnoreCase))
            {
                await _spoof.SpoofByPathAsync(GpuSpoofRegistryPath, GpuSpoofFakeName);
                UpdateStatus();
            }
        }
        catch
        {
            // 自愈失败不影响其他功能
        }
    }

    /// <summary>
    /// 临时重启生效伪装的收尾：登录自启动拉起本工具后，
    /// 把伪装的显卡型号还原为原始型号（备份值），并移除登录自启动任务、清除登记。
    /// </summary>
    private async Task RestoreTempSpoofAtLogonAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2));

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var settings = AppSettingsStore.Read();
                if (!settings.TempSpoofRestorePending) return;

                var path = settings.TempSpoofRestorePath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    var retryTask = await EnableLogonAutostartAsync();
                    StatusText = retryTask.Success
                        ? "临时显卡伪装自动还原失败：缺少显卡路径；待还原登记仍保留，下次登录会重试。"
                        : $"临时显卡伪装自动还原失败：缺少显卡路径，且无法重建登录重试任务：{retryTask.Message}";
                    Log.Warn("临时显卡伪装自动还原失败：待还原登记中没有显卡路径");
                    return;
                }

                var restore = await _spoof.RestoreByPathAsync(path);
                if (restore.Success)
                {
                    var autostartCleanup = OperationResult.Ok("无需清理登录自启动任务。");
                    if (!_frameModeActive) autostartCleanup = await DisableLogonAutostartAsync();

                    AppSettingsStore.Update(s =>
                    {
                        s.TempSpoofRestorePending = false;
                        s.TempSpoofRestorePath = "";
                    });

                    StatusText = autostartCleanup.Success
                        ? "临时显卡伪装已自动还原：注册表已恢复原始型号（Windows 可能要到下次重启才刷新显示名称）。"
                        : $"临时显卡伪装已自动还原，但登录自启动任务未能移除：{autostartCleanup.Message}";
                    Log.Info(StatusText);
                    return;
                }

                Log.Warn($"临时显卡伪装自动还原失败（第 {attempt} 次）：{restore.Message}");
                if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(2));
                else
                {
                    var retryTask = await EnableLogonAutostartAsync();
                    StatusText = retryTask.Success
                        ? $"临时显卡伪装自动还原失败：{restore.Message}。待还原登记仍保留，下次登录会重试。"
                        : $"临时显卡伪装自动还原失败：{restore.Message}。待还原登记仍保留，但登录重试任务创建失败：{retryTask.Message}";
                }
            }
        }
        catch (Exception ex)
        {
            StatusText = $"临时显卡伪装自动还原异常：{ex.Message}。待还原登记仍保留。";
            Log.Error("临时显卡伪装自动还原异常", ex);
        }
    }

    private void MigrateLegacyFrameGpuSpoof()
    {
        if (!_gpuSpoofFrameConfigured) return;

        var legacyPath = _gpuSpoofRegistryPath;
        var shouldRestore = false;
        if (!string.IsNullOrWhiteSpace(legacyPath))
        {
            try
            {
                var current = ReadDeviceDesc(legacyPath);
                shouldRestore = (_gpuSpoofEnabled && _frameModeActive) ||
                    (!string.IsNullOrWhiteSpace(_gpuSpoofFakeName) &&
                     string.Equals(current, _gpuSpoofFakeName, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                Log.Warn($"检查旧版帧格显卡伪装状态失败，将尝试还原：{ex.Message}");
                shouldRestore = _gpuSpoofEnabled && _frameModeActive;
            }
        }

        _gpuSpoofEnabled = false;
        _gpuSpoofFrameConfigured = false;
        _gpuSpoofRegistryPath = "";
        _gpuSpoofFakeName = "";
        SaveSettings();

        if (shouldRestore)
        {
            AppSettingsStore.Update(s =>
            {
                s.TempSpoofRestorePending = true;
                s.TempSpoofRestorePath = legacyPath;
            });
            Log.Info("旧版临时（帧格）显卡伪装已迁移到启动时自动还原");
        }
        else
        {
            Log.Info("已清除旧版临时（帧格）显卡伪装配置");
        }
    }

    private static string? ReadDeviceDesc(string registryPath)
    {
        using var key = Registry.LocalMachine.OpenSubKey(registryPath);
        return key?.GetValue("DeviceDesc") as string;
    }

    // ---------------- 显卡设备重载（执行归显卡后端；黑屏兜底与亮屏确认留前端） ----------------

    public async Task<bool?> IsPathDrivingDisplayAsync(string registryPath)
    {
        var target = ToDeviceInstanceId(registryPath);
        if (string.IsNullOrWhiteSpace(target)) return false;

        var driving = await GetDisplayDrivingInstanceIdsAsync();
        if (driving is null) return null; // 查询失败：fail-safe，调用方按不可重载处理
        return driving.Any(id => id.Equals(target, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<List<string>?> GetDisplayDrivingInstanceIdsAsync()
    {
        var (code, stdout, _) = await RunPowerShellAsync(
            "Get-CimInstance Win32_VideoController | Where-Object { $_.CurrentHorizontalResolution -gt 0 } | ForEach-Object { $_.PNPDeviceID }",
            TimeSpan.FromSeconds(20));

        if (code != 0 || stdout is null) return null;

        return stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    /// <summary>从 HKLM 相对注册表路径提取设备实例 ID（…\Enum\ 之后的部分）。</summary>
    internal static string? ToDeviceInstanceId(string registryPath)
    {
        const string marker = "\\Enum\\";
        var index = registryPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? null : registryPath[(index + marker.Length)..];
    }

    /// <summary>
    /// 临时伪装的立即重载：按黑屏保护闸门执行——
    /// 未驱动显示器直接重载；台式机驱动中则先启动 45 秒自动恢复再重载；笔记本拒绝重载。
    /// 重载本身由显卡后端执行。
    /// </summary>
    public async Task<OperationResult> ReloadGpuNowAsync(string registryPath)
    {
        var driving = await IsPathDrivingDisplayAsync(registryPath);
        if (driving == false) return await RestartGpuDeviceAsync(registryPath);

        if (driving == true && await IsDesktopPcAsync())
        {
            var instanceId = ToDeviceInstanceId(registryPath);
            if (string.IsNullOrWhiteSpace(instanceId))
                return OperationResult.Fail("无法从注册表路径解析设备实例 ID。");

            StartRecoveryProcess(instanceId, 45);
            return await RestartGpuDeviceCoreAsync(instanceId);
        }

        return OperationResult.Fail(
            "该显卡正在驱动显示器，且本机为笔记本 / 未知形态——立即重载有不可恢复的黑屏风险，已拒绝。请改用「重启生效」方式。");
    }

    private static async Task<OperationResult> RestartGpuDeviceAsync(string registryPath)
    {
        var instanceId = ToDeviceInstanceId(registryPath);
        if (string.IsNullOrWhiteSpace(instanceId)) return OperationResult.Fail("无法从注册表路径解析设备实例 ID。");

        // 黑屏保险网：启动 45 秒后自动重新启用该显卡的独立恢复进程（不依赖本程序存活）。
        StartRecoveryProcess(instanceId, 45);
        return await RestartGpuDeviceCoreAsync(instanceId);
    }

    /// <summary>核心重载：禁用再启用指定设备实例（执行在显卡后端）。</summary>
    private static async Task<OperationResult> RestartGpuDeviceCoreAsync(string instanceId)
        => await Backends.BackendGpuReload.ReloadAsync(instanceId);

    /// <summary>
    /// 启动独立的延迟恢复进程：delaySeconds 后自动重新启用指定显卡。
    /// 不依赖本程序存活（本程序崩溃/被杀后仍会执行），用于黑屏兜底。
    /// </summary>
    private static void StartRecoveryProcess(string instanceId, int delaySeconds)
    {
        try
        {
            var arguments =
                $"-NoProfile -WindowStyle Hidden -Command \"Start-Sleep -Seconds {delaySeconds}; " +
                $"Enable-PnpDevice -InstanceId '{instanceId}' -Confirm:$false\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch
        {
            // 恢复进程启动失败不影响主流程（用户仍有 1 分钟亮屏确认/重启兜底）
        }
    }

    private static async Task<bool> IsDesktopPcAsync() => await GetChassisKindAsync() == ChassisKind.Desktop;

    private static readonly object ChassisProbeGate = new();
    private static Task<ChassisKind>? _chassisProbe;

    private static readonly int[] LaptopChassisTypes = [8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32];
    private static readonly int[] DesktopChassisTypes = [3, 4, 5, 6, 7, 15, 16, 23, 24];

    internal static Task<ChassisKind> GetChassisKindAsync()
    {
        lock (ChassisProbeGate)
        {
            return _chassisProbe ??= ProbeChassisKindCoreAsync();
        }
    }

    private static async Task<ChassisKind> ProbeChassisKindCoreAsync()
    {
        try
        {
            var (code, stdout, _) = await RunPowerShellAsync(
                "(Get-CimInstance Win32_SystemEnclosure).ChassisTypes -join ','", TimeSpan.FromSeconds(20));
            if (code != 0 || string.IsNullOrWhiteSpace(stdout)) return ChassisKind.Unknown;

            var chassisTypes = stdout
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var v) ? v : 0)
                .Where(v => v > 0)
                .ToList();

            if (chassisTypes.Any(LaptopChassisTypes.Contains)) return ChassisKind.Laptop;
            return chassisTypes.Any(DesktopChassisTypes.Contains) ? ChassisKind.Desktop : ChassisKind.Unknown;
        }
        catch
        {
            return ChassisKind.Unknown;
        }
    }

    private async Task ProbeLaptopChassisAsync()
    {
        try
        {
            if (await GetChassisKindAsync() != ChassisKind.Laptop) return;
            _isLaptopChassis = true;
            OnPropertyChanged(nameof(IsLaptopChassis));
            Log.Info("帧格：检测到笔记本形态 —— 「网卡省电全禁」「关闭 PCIe 省电」按笔记本不适用处理");
        }
        catch (Exception ex)
        {
            Log.Warn("帧格：机箱形态探测失败：" + ex.Message);
        }
    }

    // ---------------- 登录自启动任务（应用级，前端保留） ----------------

    public async Task<OperationResult> EnableLogonAutostartAsync() => await EnableAutostartAsync();

    private static async Task<OperationResult> EnableAutostartAsync()
    {
        try
        {
            if (OfflineModeGuard.BlocksNormalAutomation)
                return OperationResult.Fail("脱机模式或恢复流程正在运行，已禁止创建帧格登录任务。");
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath)) return OperationResult.Fail("无法确定本程序路径。");

            var created = await OfflineTaskOwnership.EnsureOwnedTaskAsync(AutostartTaskName, exePath);
            if (!created.Success) return created;

            var legacy = await OfflineTaskOwnership.RemoveOwnedTaskAsync(LegacyAutostartTaskName, exePath);
            if (!legacy.Success)
            {
                await OfflineTaskOwnership.RemoveOwnedTaskAsync(AutostartTaskName, exePath);
                return OperationResult.Fail("新登录任务已创建，但旧版任务无法安全移除；已尝试回滚新任务：" + legacy.Message);
            }

            return OperationResult.Ok("登录自启动任务已创建。");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"创建登录自启动任务失败：{ex.Message}");
        }
    }

    public async Task<OperationResult> DisableLogonAutostartAsync() => await DisableAutostartAsync();

    private static async Task<OperationResult> DisableAutostartAsync()
    {
        try
        {
            var current = await OfflineTaskOwnership.RemoveOwnedTaskAsync(AutostartTaskName);
            if (!current.Success) return current;
            var legacy = await OfflineTaskOwnership.RemoveOwnedTaskAsync(LegacyAutostartTaskName);
            return legacy.Success ? OperationResult.Ok("本工具登录自启动任务已移除或不存在。") : legacy;
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"删除登录自启动任务失败：{ex.Message}");
        }
    }

    private async Task MigrateLegacyAutostartTaskAsync()
    {
        var legacy = await OfflineTaskOwnership.InspectAsync(LegacyAutostartTaskName);
        if (legacy.State == OfflineScheduledTaskState.Missing) return;
        if (legacy.State != OfflineScheduledTaskState.Owned)
        {
            Log.Warn("帧格登录任务迁移跳过：旧任务归属无法确认，未修改计划任务。");
            return;
        }

        var result = await EnableAutostartAsync();
        if (!result.Success) Log.Warn($"帧格登录任务迁移失败：{result.Message}");
        else Log.Info("帧格登录任务已从旧产品名称迁移到 Delta NFD。");
    }

    public OperationResult RebootNow()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = "/r /t 5 /f",
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null) return OperationResult.Fail("无法启动 shutdown.exe，请手动重启电脑。");
            if (!process.WaitForExit(5000))
                return OperationResult.Fail("无法确认系统已接受重启命令，请手动重启电脑。");

            return process.ExitCode == 0
                ? OperationResult.Ok("电脑将在 5 秒后重启（未保存的文件会被强制关闭）。")
                : OperationResult.Fail($"系统拒绝了重启命令（退出代码 {process.ExitCode}），请手动重启电脑。");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"重启失败：{ex.Message}（可手动重启）");
        }
    }

    // ---------------- 设置持久化（共享 AppSettingsStore） ----------------

    private void LoadSettings()
    {
        var s = AppSettingsStore.Read();
        _dwmRestartOnGameStart = s.DwmRestartOnGameStart;
        _powerPlanLockEnabled = s.FramePowerLockEnabled;
        _powerLockTargetGuid = s.FramePowerLockTargetGuid ?? "";
        _gpuSpoofEnabled = s.GpuSpoofEnabled;
        _gpuSpoofFrameConfigured = s.GpuSpoofFrameConfigured;
        _dualCcdArmed = s.DualCcdArmed;
        _dualCcdEnabled = s.DualCcdFrameEnabled;
        _responseBoostEnabled = s.FrameResponseBoostEnabled;
        _responseBoostCoreParking = s.FrameResponseBoostCoreParkingEnabled;
        _responseBoostEcoQos = s.FrameResponseBoostEcoQosEnabled;
        _responseBoostTimer = s.FrameResponseBoostTimerEnabled;
        _memoryCleanEnabled = s.FrameMemoryCleanEnabled;
        _memoryCleanGameAutoEnabled = s.FrameMemoryCleanGameAutoEnabled;
        _memoryCleanThresholdPercent = Math.Clamp(s.FrameMemoryCleanThresholdPercent, 30, 95);
        _memoryCleanThresholdText = _memoryCleanThresholdPercent.ToString();
        _foregroundBoostEnabled = s.FrameForegroundBoostEnabled;
        _foregroundResponsiveness = s.FrameForegroundResponsivenessEnabled;
        _foregroundPriority = s.FrameForegroundPriorityEnabled;
        _powerSaveLatencyEnabled = s.FramePowerSaveLatencyEnabled;
        _nicPowerSavingOff = s.FrameNicPowerSavingOffEnabled;
        _usbSuspendOff = s.FrameUsbSuspendOffEnabled;
        _pcieAspmOff = s.FramePcieAspmOffEnabled;
        _gpuSpoofApplyMode = s.GpuSpoofApplyMode == nameof(GpuSpoofApplyMode.DeviceRestart)
            ? GpuSpoofApplyMode.DeviceRestart
            : GpuSpoofApplyMode.Reboot;
        _gpuSpoofRegistryPath = s.GpuSpoofRegistryPath;
        _gpuSpoofFakeName = s.GpuSpoofFakeName;
        _frameModeActive = s.FrameModeActive;
    }

    private void SaveSettings()
    {
        lock (_gate)
        {
            AppSettingsStore.Update(s =>
            {
                s.DwmRestartOnGameStart = _dwmRestartOnGameStart;
                s.FramePowerLockEnabled = _powerPlanLockEnabled;
                s.FramePowerLockTargetGuid = _powerLockTargetGuid;
                s.GpuSpoofEnabled = _gpuSpoofEnabled;
                s.GpuSpoofFrameConfigured = _gpuSpoofFrameConfigured;
                s.DualCcdFrameEnabled = _dualCcdEnabled;
                s.FrameResponseBoostEnabled = _responseBoostEnabled;
                s.FrameResponseBoostCoreParkingEnabled = _responseBoostCoreParking;
                s.FrameResponseBoostEcoQosEnabled = _responseBoostEcoQos;
                s.FrameResponseBoostTimerEnabled = _responseBoostTimer;
                s.FrameMemoryCleanEnabled = _memoryCleanEnabled;
                s.GpuSpoofApplyMode = _gpuSpoofApplyMode.ToString();
                s.GpuSpoofRegistryPath = _gpuSpoofRegistryPath;
                s.GpuSpoofFakeName = _gpuSpoofFakeName;
                s.FrameMemoryCleanGameAutoEnabled = _memoryCleanGameAutoEnabled;
                s.FrameMemoryCleanThresholdPercent = _memoryCleanThresholdPercent;
                s.FrameForegroundBoostEnabled = _foregroundBoostEnabled;
                s.FrameForegroundResponsivenessEnabled = _foregroundResponsiveness;
                s.FrameForegroundPriorityEnabled = _foregroundPriority;
                s.FramePowerSaveLatencyEnabled = _powerSaveLatencyEnabled;
                s.FrameNicPowerSavingOffEnabled = _nicPowerSavingOff;
                s.FrameUsbSuspendOffEnabled = _usbSuspendOff;
                s.FramePcieAspmOffEnabled = _pcieAspmOff;
                s.FrameModeActive = _frameModeActive;
            });
        }
    }

    // ---------------- 进程调用（机箱形态 / 亮屏检测） ----------------

    private static async Task<(int ExitCode, string? StdOut, string? StdErr)> RunPowerShellAsync(
        string command, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments =
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " +
                $"\"[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; {command}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        using var process = Process.Start(psi);
        if (process is null) return (-1, null, "无法启动 powershell.exe");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-1, null, "PowerShell 执行超时");
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        var handler = PropertyChanged;
        if (handler is null) return;

        var args = new PropertyChangedEventArgs(propertyName ?? "");
        if (_dispatcher is null || _dispatcher.HasThreadAccess) handler.Invoke(this, args);
        else _dispatcher.TryEnqueue(() => handler.Invoke(this, args));
    }
}
