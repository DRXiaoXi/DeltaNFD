using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DeltaNFD.Native;
using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>
/// 帧格服务的真实实现：
/// - DWM 功能：后台每 2 秒检测三角洲进程；启用时游戏启动后自动结束 dwm.exe（系统立即自动拉起新 DWM）。
/// - 旧版帧格显卡伪装设置只作升级兼容：启动时清除旧配置，并还原仍处于伪装状态的显卡。
/// 激活状态与全部开关持久化在 %APPDATA%\Delta NFD\settings.json，跨重启保持。
/// </summary>
public sealed class FrameService : IFrameService
{
    public const string GameProcessName = "DeltaForceClient-Win64-Shipping";

    private const string DwmProcessName = "dwm";
    private const string AutostartTaskName = "DeltaNFD_FrameMode";
    private const string LegacyAutostartTaskName = "DeltaOptimizer_FrameMode";

    private readonly object _gate = new();
    private readonly IGpuSpoofService _spoof = ServiceLocator.GpuSpoof;
    private readonly FrameTweaksService _frameTweaks = new();

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
    private GpuSpoofApplyMode _gpuSpoofApplyMode = GpuSpoofApplyMode.Reboot;
    private string _gpuSpoofRegistryPath = "";
    private string _gpuSpoofFakeName = "";
    private bool _frameModeActive;
    private bool _isGameRunning;
    private string _statusText = "";
    private bool _dwmHandledForSession;

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
        MigrateLegacyFrameGpuSpoof();
        _statusText = DescribeStatus();
        RefreshDualCcdArmState();

        // 帧格模式跨重启保持：开机自启场景立即恢复加速系统响应模式（核心常驻环等）
        if (_frameModeActive)
        {
            UpdateResponseBoostState();
            _ = MigrateLegacyAutostartTaskAsync();
        }

        // 开机自愈：帧格模式激活状态下若伪装名被系统重置（如驱动重装），静默重写
        if (_frameModeActive)
        {
            _ = SelfHealSpoofAsync();
            _ = SelfHealDualCcdAsync();
            _ = SelfHealPowerLockAsync();
            _ = SelfHealFrameTweaksAsync();
        }
        var startupSettings = AppSettingsStore.Read();
        if (startupSettings.TempSpoofRestorePending)
        {
            // 临时重启生效伪装的收尾：登录后还原原显卡型号
            _ = RestoreTempSpoofAtLogonAsync();
        }
        else if (!_frameModeActive)
        {
            // 清理还原/退出流程中断后遗留的一次性登录任务。
            _ = DisableLogonAutostartAsync();
        }

        ServiceLocator.GameMonitor.Tick += OnGameMonitorTick;
        _ = RestorePersistentCpuSchedulingAsync();
    }

    /// <summary>应用启动后重新登记 CPU 亲和性规则及已保存的立即生效调度。</summary>
    private async Task RestorePersistentCpuSchedulingAsync()
    {
        try
        {
            var settings = AppSettingsStore.Read();
            var frameDualWillRestore = settings.FrameModeActive
                && settings.DualCcdArmed
                && settings.DualCcdFrameEnabled;

            OperationResult? result = null;
            if (!frameDualWillRestore && settings.DualCcdImmediateEnabled)
            {
                result = await ServiceLocator.Cpu.ApplyDualCcdSchedulingAsync(settings.DualCcdGameCcdIndex);
            }
            else if (!frameDualWillRestore && settings.SingleCcdExcludeCpu0Enabled)
            {
                result = await ServiceLocator.Cpu.ExcludeCpu0FromGameAsync();
            }

            if (result is not null)
            {
                Log.Info(result.Success
                    ? $"CPU调度：已按启动设置恢复。{result.Message}"
                    : $"CPU调度：启动恢复失败，保留设置待下次重试。{result.Message}");
            }

            // 亲和性规则本身已持久化；启动时主动补应用一次，后续仍由游戏监控持续保持。
            settings = AppSettingsStore.Read();
            if (settings.GameAffinityRuleEnabled && settings.GameAffinityRuleMask != 0)
            {
                CpuTopologyService.ApplyAffinityRuleTick(settings.GameAffinityRuleMask);
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
            if (_dwmRestartOnGameStart == value)
            {
                return;
            }

            _dwmRestartOnGameStart = value;
            SaveSettings();
            if (!value)
            {
                _dwmHandledForSession = false;
            }

            UpdateStatus();
            OnPropertyChanged();
        }
    }

    public bool PowerPlanLockEnabled
    {
        get => _powerPlanLockEnabled;
        set
        {
            if (_powerPlanLockEnabled == value)
            {
                return;
            }

            _powerPlanLockEnabled = value;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(PowerPlanLockText));
        }
    }

    public string FramePowerLockTargetGuid
    {
        get => _powerLockTargetGuid;
        set
        {
            var normalized = (value ?? "").Trim();
            if (_powerLockTargetGuid == normalized)
            {
                return;
            }

            _powerLockTargetGuid = normalized;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(PowerPlanLockText));
        }
    }

    public string PowerPlanLockText =>
        string.IsNullOrWhiteSpace(_powerLockTargetGuid)
            ? "开启一键帧格模式时记住并锁定你当前的电源计划（不切换别的计划），游戏期间每 30 秒检查一次，防止被其他程序改成省电方案；退出帧格模式时解除锁定并还原。"
            : "开启一键帧格模式时切换并锁定到你选择的电源计划，游戏期间每 30 秒检查一次，被改走会自动切回；退出帧格模式时还原为开启前的原计划。";

    public bool GpuSpoofEnabled
    {
        get => _gpuSpoofEnabled;
        set
        {
            if (_gpuSpoofEnabled == value)
            {
                return;
            }

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
            if (_gpuSpoofFrameConfigured == value)
            {
                return;
            }

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
            if (_dualCcdArmed == value)
            {
                return;
            }

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
            if (_dualCcdEnabled == value)
            {
                return;
            }

            _dualCcdEnabled = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    // ---------------- 加速系统响应模式（不改电源计划） ----------------

    private CoreParkingLoop? _parkingLoop;
    private readonly object _perfGate = new();
    private readonly HashSet<int> _ecoQosAppliedPids = [];
    private bool _perfTimerActive;
    private int _parkedCoreCount = -1;

    // ---------------- 内存清理（实验性；清空待备内存列表） ----------------

    /// <summary>自动清理的检查周期（轮询线程每 2 秒 tick，内部按此节流查内存）。</summary>
    private static readonly TimeSpan MemoryCleanCheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>两次清理之间的最小间隔（防止反复清空文件缓存反而拖慢加载）。</summary>
    private static readonly TimeSpan MemoryCleanCooldown = TimeSpan.FromMinutes(10);

    private int _memoryCleanRunning; // Interlocked 防重入（0 = 空闲）
    private DateTime _lastMemoryCleanCheck = DateTime.MinValue;
    private DateTime _lastMemoryClean = DateTime.MinValue;
    private string _memoryCleanStatusText = "尚未清理";
    private bool _memoryCleanGameAutoEnabled = true;
    private int _memoryCleanThresholdPercent = 80;
    private string _memoryCleanThresholdText = "80";

    /// <summary>加速系统响应模式总开关（关 = 全部机制不生效）。</summary>
    public bool ResponseBoostEnabled
    {
        get => _responseBoostEnabled;
        set
        {
            if (_responseBoostEnabled == value)
            {
                return;
            }

            _responseBoostEnabled = value;
            SaveSettings();
            OnPropertyChanged();
            UpdateStatus();
            UpdateResponseBoostState();
        }
    }

    /// <summary>子开关①：核心常驻（防核心停放；帧格激活全程轮询）。</summary>
    public bool ResponseBoostCoreParkingEnabled
    {
        get => _responseBoostCoreParking;
        set
        {
            if (_responseBoostCoreParking == value)
            {
                return;
            }

            _responseBoostCoreParking = value;
            SaveSettings();
            OnPropertyChanged();
            UpdateStatus();
            UpdateResponseBoostState();
        }
    }

    /// <summary>子开关②：游戏进程禁用电源限制 EcoQoS。</summary>
    public bool ResponseBoostEcoQosEnabled
    {
        get => _responseBoostEcoQos;
        set
        {
            if (_responseBoostEcoQos == value)
            {
                return;
            }

            _responseBoostEcoQos = value;
            SaveSettings();
            OnPropertyChanged();
            UpdateStatus();
            UpdateResponseBoostState();
        }
    }

    /// <summary>子开关③：系统定时器 0.5ms（帧格激活且游戏运行时）。</summary>
    public bool ResponseBoostTimerEnabled
    {
        get => _responseBoostTimer;
        set
        {
            if (_responseBoostTimer == value)
            {
                return;
            }

            _responseBoostTimer = value;
            SaveSettings();
            OnPropertyChanged();
            UpdateStatus();
            UpdateResponseBoostState();
        }
    }

    /// <summary>内存清理（实验性）：清空待备内存列表释放可用内存；帧格激活期间自动触发，可随时手动执行。</summary>
    public bool MemoryCleanEnabled
    {
        get => _memoryCleanEnabled;
        set
        {
            if (_memoryCleanEnabled == value)
            {
                return;
            }

            _memoryCleanEnabled = value;
            SaveSettings();
            OnPropertyChanged();
            UpdateStatus();
        }
    }

    // ---------------- 前台加速响应（调度参数临时改写，退出帧格还原） ----------------

    /// <summary>前台加速响应总开关（关 = 全部机制不生效）。</summary>
    public bool ForegroundBoostEnabled
    {
        get => _foregroundBoostEnabled;
        set
        {
            if (_foregroundBoostEnabled == value)
            {
                return;
            }

            _foregroundBoostEnabled = value;
            SaveSettings();
            OnPropertyChanged();
            UpdateStatus();
        }
    }

    /// <summary>子开关①：系统后台资源预留 20%→10%。</summary>
    public bool ForegroundResponsivenessEnabled
    {
        get => _foregroundResponsiveness;
        set
        {
            if (_foregroundResponsiveness == value)
            {
                return;
            }

            _foregroundResponsiveness = value;
            SaveSettings();
            OnPropertyChanged();
            UpdateStatus();
        }
    }

    /// <summary>子开关②：前台游戏优先调度（0x1A）。</summary>
    public bool ForegroundPriorityEnabled
    {
        get => _foregroundPriority;
        set
        {
            if (_foregroundPriority == value)
            {
                return;
            }

            _foregroundPriority = value;
            SaveSettings();
            OnPropertyChanged();
            UpdateStatus();
        }
    }

    // ---------------- 降低省电延迟（网卡/USB 省电临时关闭，退出帧格还原） ----------------

    /// <summary>降低省电延迟总开关（关 = 全部机制不生效）。</summary>
    public bool PowerSaveLatencyEnabled
    {
        get => _powerSaveLatencyEnabled;
        set
        {
            if (_powerSaveLatencyEnabled == value)
            {
                return;
            }

            _powerSaveLatencyEnabled = value;
            SaveSettings();
            OnPropertyChanged();
            UpdateStatus();
        }
    }

    /// <summary>子开关①：网卡省电全禁。</summary>
    public bool NicPowerSavingOffEnabled
    {
        get => _nicPowerSavingOff;
        set
        {
            if (_nicPowerSavingOff == value)
            {
                return;
            }

            _nicPowerSavingOff = value;
            SaveSettings();
            OnPropertyChanged();
            UpdateStatus();
        }
    }

    /// <summary>子开关②：关闭 USB 选择性暂停。</summary>
    public bool UsbSuspendOffEnabled
    {
        get => _usbSuspendOff;
        set
        {
            if (_usbSuspendOff == value)
            {
                return;
            }

            _usbSuspendOff = value;
            SaveSettings();
            OnPropertyChanged();
            UpdateStatus();
        }
    }

    /// <summary>内存清理状态文字（上次清理时间与释放量）。</summary>
    public string MemoryCleanStatusText => _memoryCleanStatusText;

    /// <summary>游戏内自动清理开关：三角洲运行期间内存占用达到阈值时自动清理。</summary>
    public bool MemoryCleanGameAutoEnabled
    {
        get => _memoryCleanGameAutoEnabled;
        set
        {
            if (_memoryCleanGameAutoEnabled == value)
            {
                return;
            }

            _memoryCleanGameAutoEnabled = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>游戏内自动清理阈值（内存占用百分比，30–95）的文本绑定；
    /// 非法或超范围输入自动回退旧值，合法输入立即持久化。</summary>
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

            OnPropertyChanged(); // 输入非法时由绑定把文本拉回当前值
        }
    }

    public GpuSpoofApplyMode GpuSpoofApplyMode
    {
        get => _gpuSpoofApplyMode;
        set
        {
            if (_gpuSpoofApplyMode == value)
            {
                return;
            }

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
            if (_gpuSpoofRegistryPath == value)
            {
                return;
            }

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
            if (_gpuSpoofFakeName == value)
            {
                return;
            }

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

    /// <summary>
    /// 一键帧格模式 = 当前是否处于激活状态。开启/关闭走 Activate/Deactivate 流程（UI 负责确认对话框）。
    /// </summary>
    public bool FrameModeEnabled => _frameModeActive;

    public bool FrameModeActive
    {
        get => _frameModeActive;
        private set
        {
            if (_frameModeActive == value)
            {
                return;
            }

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
            if (_isGameRunning == value)
            {
                return;
            }

            _isGameRunning = value;
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
        }
    }

    // ---------------- 帧格模式激活 / 退出 ----------------

    public async Task<OperationResult> ActivateFrameModeAsync()
    {
        if (!ElevationHelper.IsElevated)
        {
            Log.Warn("帧格激活失败：未以管理员运行");
            return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        }

        Log.Info("── 开启一键帧格模式 ──");
        // 旧版帧格伪装配置会在服务启动时迁移清除；此判定仅保留作兼容保护。
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
                // 不重启生效：目标显卡正在驱动显示器（或无法确认）时：
                // - 笔记本/未知形态：硬拦截重载，自动回退为重启生效流程（避免不可恢复的黑屏）
                // - 台式机：放开限制 —— 45 秒自动恢复进程 + 60 秒亮屏确认，超时未确认自动重启
                if (await IsPathDrivingDisplayAsync(GpuSpoofRegistryPath) == false)
                {
                    var reload = await RestartGpuDeviceAsync(GpuSpoofRegistryPath);
                    if (!reload.Success)
                    {
                        return OperationResult.Fail(
                            $"显卡已伪装，但重载显卡失败：{reload.Message}");
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
        // DWM 监控为独立开关：帧格模式不再强制开启，按用户自己的设置运行

        // 双CCD 专属调度（帧格生效模式）：帧格开启时自动应用，退出时自动撤销。
        // 注意必须在运行期取 ServiceLocator.Cpu（字段初始化时该属性尚未赋值）。
        var dualNote = await ApplyArmedDualCcdAsync();
        var powerNote = await ApplyPowerLockAsync();
        var notes = dualNote + powerNote;
        if (dualNote.Length > 0) Log.Info("帧格·双CCD：" + dualNote.Trim());
        if (powerNote.Length > 0) Log.Info("帧格·电源锁定：" + powerNote.Trim());

        // 加速系统响应模式：帧格激活后统一换挡（核心常驻环立即启动）
        UpdateResponseBoostState();

        // 前台加速响应 + 降低省电延迟：帧格激活时应用临时改写（退出时自动还原）
        var foregroundNote = await ApplyFrameTweaksAsync();

        // 内存清理（实验性）：开启帧格时立即执行一次（绕过冷却），给游戏腾出干净的可用内存
        if (_memoryCleanEnabled)
        {
            _ = Task.Run(() => MemoryCleanOnce(bypassCooldown: true));
        }

        if (!useSpoof)
        {
            Log.Info("帧格模式已开启（无伪装配置）");
            return OperationResult.Ok("帧格模式已开启（各功能按帧格页开关执行）。" + notes + foregroundNote);
        }

        if (GpuSpoofApplyMode == GpuSpoofApplyMode.Reboot || !appliedImmediately)
        {
            return OperationResult.Ok(
                $"帧格模式已开启：显卡已伪装为「{GpuSpoofFakeName}」，重启电脑后生效。" +
                "已创建登录自启动任务，重启登录后本程序自动运行并保持帧格模式。" + notes + foregroundNote,
                requiresReboot: true);
        }

        if (needsDisplayConfirm)
        {
            return OperationResult.Ok(
                $"帧格模式已开启：显卡已伪装为「{GpuSpoofFakeName}」并重载立即生效。" +
                "请在 1 分钟内确认显示器处于亮屏状态，超时未确认电脑将自动重启以恢复显示。" + notes + foregroundNote,
                requiresDisplayConfirm: true);
        }

        return OperationResult.Ok($"帧格模式已开启：显卡已伪装为「{GpuSpoofFakeName}」并重载立即生效。" + notes + foregroundNote);
    }

    public async Task<OperationResult> DeactivateFrameModeAsync()
    {
        if (!ElevationHelper.IsElevated)
        {
            Log.Warn("帧格退出失败：未以管理员运行");
            return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        }

        Log.Info("── 退出一键帧格模式 ──");
        // 显卡伪装仅在帧格配置存在时需要还原
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

        // 退出帧格模式：自启动任务一律移除（重启/设备模式都可能创建过）
        await DisableAutostartAsync();

        FrameModeActive = false;
        // DWM 监控为独立开关：退出帧格不改动用户设置（保持原状继续按开关工作）
        GpuSpoofEnabled = false;

        var dualNote = await RevertArmedDualCcdAsync();
        var powerNote = await RevertPowerLockAsync();
        var notes = dualNote + powerNote;
        if (dualNote.Length > 0) Log.Info("帧格退出·双CCD：" + dualNote.Trim());
        if (powerNote.Length > 0) Log.Info("帧格退出·电源锁定：" + powerNote.Trim());

        // 前台加速响应 + 降低省电延迟：退出帧格时还原全部临时改写
        var tweaksNote = await RevertFrameTweaksAsync();
        notes += tweaksNote;

        // 加速系统响应模式：帧格退出后全部还原（开关设置保留）
        UpdateResponseBoostState();

        if (!useSpoof)
        {
            return OperationResult.Ok("帧格模式已退出。" + notes);
        }

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

    // ---------------- 双CCD 专属调度（帧格生效模式） ----------------

    /// <summary>帧格开启时：若用户在「实验室」页登记了帧格生效的双CCD调度且帧格侧开关未关，自动应用。</summary>
    private async Task<string> ApplyArmedDualCcdAsync()
    {
        try
        {
            var s = AppSettingsStore.Read();
            if (!s.DualCcdArmed || !s.DualCcdFrameEnabled)
            {
                return "";
            }

            var result = await ServiceLocator.Cpu.ApplyDualCcdSchedulingAsync(s.DualCcdGameCcdIndex);
            return result.Success
                ? $"双CCD调度已应用：游戏独占 CCD{s.DualCcdGameCcdIndex}，其他进程在另一个 CCD。"
                : $"双CCD调度应用失败：{result.Message}";
        }
        catch (Exception ex)
        {
            return $"双CCD调度应用失败：{ex.Message}";
        }
    }

    /// <summary>帧格退出时：撤销帧格生效的双CCD调度（恢复所有进程到全部核心）。</summary>
    private async Task<string> RevertArmedDualCcdAsync()
    {
        try
        {
            var s = AppSettingsStore.Read();
            if (!s.DualCcdArmed || !s.DualCcdFrameEnabled)
            {
                return "";
            }

            var result = await ServiceLocator.Cpu.RevertDualCcdSchedulingAsync();
            if (!result.Success)
            {
                return $"双CCD调度撤销失败：{result.Message}";
            }

            var note = "双CCD调度已撤销，所有进程恢复全部核心。";
            if (s.SingleCcdExcludeCpu0Enabled)
            {
                var singleCcd = await ServiceLocator.Cpu.ExcludeCpu0FromGameAsync();
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

    // ---------------- 前台加速响应 + 降低省电延迟（帧格激活临时改写，退出还原） ----------------

    /// <summary>帧格开启时：按开关应用前台加速响应与降低省电延迟的临时改写。</summary>
    private async Task<string> ApplyFrameTweaksAsync()
    {
        try
        {
            if (!_foregroundBoostEnabled && !_powerSaveLatencyEnabled)
            {
                return "";
            }

            var result = await _frameTweaks.ApplyAsync(
                _foregroundBoostEnabled && _foregroundResponsiveness,
                _foregroundBoostEnabled && _foregroundPriority,
                _powerSaveLatencyEnabled && _nicPowerSavingOff,
                _powerSaveLatencyEnabled && _usbSuspendOff);
            Log.Info($"帧格·临时优化：应用结果={(result.Success ? "成功" : "失败")}，说明={result.Message}");
            return result.Success
                ? (result.Message.Length > 0 ? " " + result.Message.TrimEnd('。') + "。" : "")
                : $" {result.Message}";
        }
        catch (Exception ex)
        {
            Log.Error("帧格·临时优化：应用异常", ex);
            return $" 前台加速/省电延迟优化应用失败：{ex.Message}";
        }
    }

    /// <summary>帧格退出时：还原全部前台加速响应与降低省电延迟的临时改写。</summary>
    private async Task<string> RevertFrameTweaksAsync()
    {
        try
        {
            var result = await _frameTweaks.RevertAsync();
            Log.Info($"帧格·临时优化：还原结果={(result.Success ? "成功" : "失败")}，说明={result.Message}");
            if (!result.Success)
            {
                return " " + result.Message;
            }

            return result.Message.Length > 0 ? " " + result.Message.TrimEnd('。') + "。" : "";
        }
        catch (Exception ex)
        {
            Log.Error("帧格·临时优化：还原异常", ex);
            return $" 帧格临时优化还原失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 开机自愈：帧格激活状态下登录后，若临时优化注册表值被系统重置（驱动重装/组策略刷新等），
    /// 静默重新应用。备份记录仍在时只补写优化值（备份不覆盖，原值还原语义不变）。
    /// </summary>
    private async Task SelfHealFrameTweaksAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(12));
            if (!_frameModeActive)
            {
                return;
            }

            var foregroundWanted = _foregroundBoostEnabled;
            var powerSaveWanted = _powerSaveLatencyEnabled;
            if (!foregroundWanted && !powerSaveWanted)
            {
                return;
            }

            // 已有备份 = 上次已改写；值被重置时补写（备份保持首次的原值，不重复记录）
            if (foregroundWanted && !FrameTweaksService.IsForegroundBoostApplied() ||
                powerSaveWanted && !FrameTweaksService.IsPowerSaveLatencyApplied())
            {
                var result = await _frameTweaks.ApplyAsync(
                    foregroundWanted && _foregroundResponsiveness,
                    foregroundWanted && _foregroundPriority,
                    powerSaveWanted && _nicPowerSavingOff,
                    powerSaveWanted && _usbSuspendOff);
                Log.Info($"帧格·临时优化：开机自愈结果={(result.Success ? "成功" : "失败")}，说明={result.Message}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("帧格·临时优化：开机自愈失败", ex);
        }
    }

    // ---------------- 加速系统响应模式：状态机 ----------------

    /// <summary>按当前开关与游戏状态统一换挡三个机制（幂等；由开关 setter、帧格启停、游戏轮询触发）。</summary>
    private void UpdateResponseBoostState()
    {
        lock (_perfGate)
        {
            var boostOn = _frameModeActive && _responseBoostEnabled;

            // ① 核心常驻轮询环：帧格激活全程
            var parkingWanted = boostOn && _responseBoostCoreParking;
            if (parkingWanted && (_parkingLoop is null || !_parkingLoop.IsRunning))
            {
                _parkingLoop ??= new CoreParkingLoop();
                _parkingLoop.Start(OnParkedCountChanged);
                Log.Info("加速响应：核心常驻轮询环已启动（100ms + 亲和性轮转）");
            }
            else if (!parkingWanted && _parkingLoop is { IsRunning: true })
            {
                _parkingLoop.Stop();
                _parkedCoreCount = -1;
                Log.Info("加速响应：核心常驻轮询环已停止");
            }

            // ③ 定时器 0.5ms：帧格激活且游戏运行
            var timerWanted = boostOn && _responseBoostTimer && _isGameRunning;
            if (timerWanted && !_perfTimerActive)
            {
                _perfTimerActive = Native.ProcessPerf.TimerRequestHighResolution();
                Log.Info(_perfTimerActive
                    ? "加速响应：定时器分辨率请求 0.5ms 已生效"
                    : "加速响应：定时器分辨率请求失败（NtSetTimerResolution 返回非 0）");
            }
            else if (!timerWanted && _perfTimerActive)
            {
                Native.ProcessPerf.TimerReleaseResolution();
                _perfTimerActive = false;
                Log.Info("加速响应：定时器分辨率请求已释放");
            }

            // ② EcoQoS：帧格激活 + 子开关开时逐个游戏 PID 应用；条件不满足时恢复
            if (boostOn && _responseBoostEcoQos && _isGameRunning)
            {
                foreach (var process in Process.GetProcessesByName(GameProcessName))
                {
                    using (process)
                    {
                        if (_ecoQosAppliedPids.Add(process.Id))
                        {
                            var ok = Native.ProcessPerf.DisablePowerThrottling(process.Id);
                            Log.Info($"加速响应：游戏进程 PID {process.Id} 禁用电源限制（EcoQoS）{(ok ? "成功" : "失败（可能受保护）")}");
                        }
                    }
                }
            }
            else if (_ecoQosAppliedPids.Count > 0)
            {
                foreach (var pid in _ecoQosAppliedPids)
                {
                    Native.ProcessPerf.RestorePowerThrottling(pid);
                }

                Log.Info($"加速响应：已恢复 {_ecoQosAppliedPids.Count} 个游戏进程的电源限制系统控制");
                _ecoQosAppliedPids.Clear();
            }
        }
    }

    /// <summary>核心常驻轮询回调（轮询线程触发）：停靠核心数变化时刷新状态文字。</summary>
    private void OnParkedCountChanged(int? parked)
    {
        if (parked is null)
        {
            return;
        }

        var changed = _parkedCoreCount != parked.Value;
        _parkedCoreCount = parked.Value;
        if (changed)
        {
            UpdateStatus();
        }
    }

    // ---------------- 内存清理（实验性；清空待备内存列表） ----------------

    /// <summary>手动立即执行一次内存清理（不等帧格/游戏状态；冷却不生效——用户点了就清）。</summary>
    public async Task<OperationResult> CleanMemoryNowAsync()
    {
        var (success, message) = await Task.Run(() => MemoryCleanOnce(bypassCooldown: true, manual: true));
        return success ? OperationResult.Ok(message) : OperationResult.Fail(message);
    }

    /// <summary>轮询线程的自动清理检查（内部按 30 秒节流；游戏内自动清理开启 + 占用达阈值 + 冷却已过才真正清理）。</summary>
    private void MemoryCleanTick()
    {
        if (!_memoryCleanGameAutoEnabled)
        {
            return;
        }

        var now = DateTime.Now;
        if (now - _lastMemoryCleanCheck < MemoryCleanCheckInterval)
        {
            return;
        }

        _lastMemoryCleanCheck = now;
        if (now - _lastMemoryClean < MemoryCleanCooldown)
        {
            return;
        }

        // 只在内存有压力时清（待备缓存本身是有用的文件缓存，占用不高时清它反而拖慢加载）
        var memory = MemoryPurge.QueryMemory();
        if (memory.TotalPhys == 0)
        {
            return;
        }

        var threshold = Math.Clamp(_memoryCleanThresholdPercent, 30, 95);
        if (memory.LoadPercent < threshold)
        {
            return;
        }

        Log.Info($"内存清理：内存占用 {memory.LoadPercent}% 达到阈值 {threshold}%，自动清空待备内存列表");
        _ = Task.Run(() => MemoryCleanOnce());
    }

    /// <summary>
    /// 执行一次待备内存列表清理（幂等防重入；bypassCooldown 用于帧格开启时立即触发，
    /// manual 用于按钮触发——两者都不受 10 分钟冷却限制）。
    /// </summary>
    private (bool Success, string Message) MemoryCleanOnce(bool bypassCooldown = false, bool manual = false)
    {
        if (!bypassCooldown && DateTime.Now - _lastMemoryClean < MemoryCleanCooldown)
        {
            return (false, "距离上次清理太近，已跳过本次自动清理。");
        }

        if (Interlocked.CompareExchange(ref _memoryCleanRunning, 1, 0) != 0)
        {
            return (false, "内存清理正在执行中，请稍候。");
        }

        try
        {
            var result = MemoryPurge.PurgeStandbyList();
            _lastMemoryClean = DateTime.Now;
            if (result.Success)
            {
                _memoryCleanStatusText = result.FreedBytes > 0
                    ? $"上次清理（{DateTime.Now:HH:mm}）：释放 {MemoryPurge.FormatBytes((ulong)result.FreedBytes)} 可用内存"
                    : $"上次清理（{DateTime.Now:HH:mm}）：已优化到最佳状态（可用内存 {MemoryPurge.FormatBytes(MemoryPurge.QueryMemory().AvailPhys)}）";
                Log.Info($"内存清理：{(manual ? "手动" : "自动")}清理完成 —— {result.Message}（释放量 {MemoryPurge.FormatBytes((ulong)Math.Max(0, result.FreedBytes))}）");
            }
            else
            {
                _memoryCleanStatusText = $"上次清理（{DateTime.Now:HH:mm}）失败 —— {result.Message}";
                Log.Warn($"内存清理：{(manual ? "手动" : "自动")}清理失败 —— {result.Message}");
            }

            OnPropertyChanged(nameof(MemoryCleanStatusText));
            return (result.Success, result.Message);
        }
        catch (Exception ex)
        {
            Log.Error("内存清理：执行异常", ex);
            _memoryCleanStatusText = $"上次清理（{DateTime.Now:HH:mm}）异常 —— {ex.Message}";
            OnPropertyChanged(nameof(MemoryCleanStatusText));
            return (false, "内存清理异常：" + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _memoryCleanRunning, 0);
        }
    }

    // ---------------- 电源计划锁定（帧格功能） ----------------

    /// <summary>
    /// 帧格开启时：启用电源锁定则按用户选择锁定——
    /// 指定了目标计划就切换过去并锁定；未指定则记住并锁定当前计划（不切换）。
    /// 之后游戏期间周期性重新套用，防止省电/游戏优化程序擅自改电源计划。
    /// </summary>
    private async Task<string> ApplyPowerLockAsync()
    {
        try
        {
            if (!_powerPlanLockEnabled)
            {
                return "";
            }

            // 必须运行期取 ServiceLocator.Power（构造期初始化顺序未定）
            var power = ServiceLocator.Power;
            var schemes = await power.GetSchemesAsync();
            var active = schemes.FirstOrDefault(s => s.IsActive);
            Log.Info($"帧格·电源锁定：开始应用，当前 GUID={(active?.Guid ?? "(未知)")}，配置目标={(string.IsNullOrWhiteSpace(_powerLockTargetGuid) ? "当前计划" : _powerLockTargetGuid)}");

            if (active is null || string.IsNullOrEmpty(active.Guid))
            {
                Log.Warn("帧格·电源锁定：无法识别当前活动计划，未应用锁定");
                return "电源计划锁定失败：无法读取当前电源计划。";
            }

            // 指定目标计划：不存在（被删除）则回退为锁定当前计划
            var targetGuid = _powerLockTargetGuid;
            if (!string.IsNullOrWhiteSpace(targetGuid) &&
                schemes.All(s => !s.Guid.Equals(targetGuid, StringComparison.OrdinalIgnoreCase)))
            {
                Log.Warn($"帧格·电源锁定：配置目标 {targetGuid} 不在系统计划列表中，回退锁定当前计划 {active.Guid}");
                targetGuid = "";
            }

            // 记住开启前的原计划（退出时还原），再切到目标计划
            AppSettingsStore.Update(s => s.FramePowerPreviousSchemeGuid = active.Guid);
            var lockGuid = string.IsNullOrWhiteSpace(targetGuid) ? active.Guid : targetGuid;
            var result = await power.SetSchemeAsync(lockGuid);
            Log.Info($"帧格·电源锁定：应用结果={(result.Success ? "成功" : "失败")}，锁定 GUID={lockGuid}，原计划 GUID={active.Guid}，说明={result.Message}");
            if (!result.Success)
            {
                return $"电源计划锁定失败：{result.Message}";
            }

            return string.IsNullOrWhiteSpace(targetGuid)
                ? $"电源计划已锁定为当前计划「{active.Name}」（游戏期间防止被修改）。"
                : $"电源计划已切换并锁定为「{schemes.First(s => s.Guid.Equals(lockGuid, StringComparison.OrdinalIgnoreCase)).Name}」（游戏期间防止被修改）。";
        }
        catch (Exception ex)
        {
            Log.Error("帧格·电源锁定：应用异常", ex);
            return $"电源计划锁定失败：{ex.Message}";
        }
    }

    /// <summary>帧格退出时：电源锁定解除，还原到锁定时记录的用户计划（并停止周期保护）。</summary>
    private async Task<string> RevertPowerLockAsync()
    {
        try
        {
            if (!_powerPlanLockEnabled)
            {
                return "";
            }

            var previous = AppSettingsStore.Read().FramePowerPreviousSchemeGuid;
            if (string.IsNullOrWhiteSpace(previous))
            {
                Log.Warn("帧格·电源锁定：退出时没有记录原计划 GUID，无法自动还原");
                return "";
            }

            var result = await ServiceLocator.Power.SetSchemeAsync(previous);
            Log.Info($"帧格·电源锁定：退出还原结果={(result.Success ? "成功" : "失败")}，原计划 GUID={previous}，说明={result.Message}");
            return result.Success
                ? "电源计划已还原为用户原计划，锁定解除。"
                : $"电源计划还原失败：{result.Message}";
        }
        catch (Exception ex)
        {
            Log.Error("帧格·电源锁定：退出还原异常", ex);
            return $"电源计划还原失败：{ex.Message}";
        }
    }

    /// <summary>周期检查计数：帧格 + 电源锁定期间每 15 个轮询（约 30 秒）检查一次。</summary>
    private int _powerGuardPollCount;

    /// <summary>周期检查：帧格 + 电源锁定启用时，若激活计划被其他程序改走，自动切回用户计划。</summary>
    private async Task GuardPowerLockTickAsync()    {
        if (!_frameModeActive || !_powerPlanLockEnabled)
        {
            return;
        }

        if (++_powerGuardPollCount < 15)
        {
            return;
        }

        _powerGuardPollCount = 0;

        try
        {
            // 锁定期望计划：指定了目标计划用目标，否则用开启时记录的原（当前）计划
            var expected = !string.IsNullOrWhiteSpace(_powerLockTargetGuid)
                ? _powerLockTargetGuid
                : AppSettingsStore.Read().FramePowerPreviousSchemeGuid;
            if (string.IsNullOrWhiteSpace(expected))
            {
                return;
            }

            var power = ServiceLocator.Power;
            var schemes = await power.GetSchemesAsync();
            var active = schemes.FirstOrDefault(s => s.IsActive);
            if (active is not null && !active.Guid.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"电源锁定守护：计划被改走（{active.Guid}），重锁回 {expected}");
                var result = await power.SetSchemeAsync(expected);
                Log.Info($"电源锁定守护：重锁结果={(result.Success ? "成功" : "失败")}，目标 GUID={expected}，说明={result.Message}");
            }
            else if (active is null)
                Log.Warn($"电源锁定守护：未能识别当前活动计划，期望 GUID={expected}");
        }
        catch (Exception ex)
        {
            Log.Error("电源锁定守护：检查或重锁失败", ex);
        }
    }

    /// <summary>开机（登录自启动）且帧格模式保持激活时，重新锁定用户电源计划（防止被其他程序改回）。</summary>
    private async Task SelfHealPowerLockAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            if (!_frameModeActive || !_powerPlanLockEnabled)
            {
                return;
            }

            var note = await ApplyPowerLockAsync();
            if (!string.IsNullOrWhiteSpace(note))
                Log.Info("帧格·电源锁定：开机自愈结果=" + note);
        }
        catch (Exception ex)
        {
            Log.Error("帧格·电源锁定：开机自愈失败", ex);
        }
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

            if (process is null)
            {
                return OperationResult.Fail("无法启动 shutdown.exe，请手动重启电脑。");
            }

            if (!process.WaitForExit(5000))
            {
                return OperationResult.Fail("无法确认系统已接受重启命令，请手动重启电脑。");
            }

            return process.ExitCode == 0
                ? OperationResult.Ok("电脑将在 5 秒后重启（未保存的文件会被强制关闭）。")
                : OperationResult.Fail($"系统拒绝了重启命令（退出代码 {process.ExitCode}），请手动重启电脑。");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"重启失败：{ex.Message}（可手动重启）");
        }
    }

    // ---------------- 帧格模式开机自愈 ----------------

    /// <summary>帧格模式激活状态下，若伪装名被系统重置（驱动重装等），登录后静默重写（不做设备重载）。</summary>
    private async Task SelfHealSpoofAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            if (!_frameModeActive || !IsGpuSpoofInFrame || !_gpuSpoofEnabled)
            {
                return;
            }

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
    /// 帧格模式激活时不移除其共用的登录自启动任务。
    /// </summary>
    private async Task RestoreTempSpoofAtLogonAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2));

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var settings = AppSettingsStore.Read();
                if (!settings.TempSpoofRestorePending)
                {
                    return;
                }

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
                    if (!_frameModeActive)
                    {
                        autostartCleanup = await DisableLogonAutostartAsync();
                    }

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
                if (attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }
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
            // 还原失败不影响其他功能；待还原登记保留，下次启动再试。
            StatusText = $"临时显卡伪装自动还原异常：{ex.Message}。待还原登记仍保留。";
            Log.Error("临时显卡伪装自动还原异常", ex);
        }
    }

    /// <summary>
    /// 移除旧版「临时（帧格）」显卡伪装配置。若升级前帧格模式仍将伪装保持为激活状态，
    /// 将其并入登录还原队列，避免隐藏该功能后仍在后台长期伪装。
    /// </summary>
    private void MigrateLegacyFrameGpuSpoof()
    {
        if (!_gpuSpoofFrameConfigured)
        {
            return;
        }

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

    /// <summary>开机（登录自启动）且帧格模式保持激活时，重新应用帧格生效的双CCD调度。</summary>
    private async Task SelfHealDualCcdAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
            if (!_frameModeActive)
            {
                return;
            }

            var s = AppSettingsStore.Read();
            if (s.DualCcdArmed && s.DualCcdFrameEnabled)
            {
                var result = await ServiceLocator.Cpu.ApplyDualCcdSchedulingAsync(s.DualCcdGameCcdIndex);
                Log.Info(result.Success
                    ? $"CPU调度：已恢复帧格双CCD规则。{result.Message}"
                    : $"CPU调度：帧格双CCD恢复失败：{result.Message}");
            }
        }
        catch
        {
            // 自愈失败不影响其他功能
        }
    }

    // ---------------- 显卡设备重载（不重启生效） ----------------

    /// <summary>
    /// 判断指定显卡是否正在驱动显示器。
    /// true=正在驱动（重载必黑屏）；false=确认安全；null=查询失败无法确认（按不可重载处理）。
    /// </summary>
    public async Task<bool?> IsPathDrivingDisplayAsync(string registryPath)
    {
        var target = ToDeviceInstanceId(registryPath);
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        var driving = await GetDisplayDrivingInstanceIdsAsync();
        if (driving is null)
        {
            return null; // 查询失败：fail-safe，调用方按不可重载处理
        }

        return driving.Any(id => id.Equals(target, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 列出当前正在驱动显示器（有活动分辨率输出）的显卡设备实例 ID；查询失败返回 null。
    /// </summary>
    private static async Task<List<string>?> GetDisplayDrivingInstanceIdsAsync()
    {
        var (code, stdout, _) = await RunPowerShellAsync(
            "Get-CimInstance Win32_VideoController | Where-Object { $_.CurrentHorizontalResolution -gt 0 } | ForEach-Object { $_.PNPDeviceID }",
            TimeSpan.FromSeconds(20));

        if (code != 0 || stdout is null)
        {
            return null;
        }

        return stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
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
    /// </summary>
    public async Task<OperationResult> ReloadGpuNowAsync(string registryPath)
    {
        var driving = await IsPathDrivingDisplayAsync(registryPath);
        if (driving == false)
        {
            return await RestartGpuDeviceAsync(registryPath);
        }

        if (driving == true && await IsDesktopPcAsync())
        {
            var instanceId = ToDeviceInstanceId(registryPath);
            if (string.IsNullOrWhiteSpace(instanceId))
            {
                return OperationResult.Fail("无法从注册表路径解析设备实例 ID。");
            }

            StartRecoveryProcess(instanceId, 45);
            return await RestartGpuDeviceCoreAsync(instanceId);
        }

        return OperationResult.Fail(
            "该显卡正在驱动显示器，且本机为笔记本 / 未知形态——立即重载有不可恢复的黑屏风险，已拒绝。请改用「重启生效」方式。");
    }

    private static async Task<OperationResult> RestartGpuDeviceAsync(string registryPath)
    {
        var instanceId = ToDeviceInstanceId(registryPath);
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return OperationResult.Fail("无法从注册表路径解析设备实例 ID。");
        }

        // 黑屏保险网：启动 45 秒后自动重新启用该显卡的独立恢复进程（不依赖本程序存活）。
        StartRecoveryProcess(instanceId, 45);
        return await RestartGpuDeviceCoreAsync(instanceId);
    }

    /// <summary>核心重载：禁用再启用指定设备实例。</summary>
    private static async Task<OperationResult> RestartGpuDeviceCoreAsync(string instanceId)
    {
        var command =
            "try { Disable-PnpDevice -InstanceId '" + instanceId + "' -Confirm:$false -ErrorAction Stop; " +
            "Enable-PnpDevice -InstanceId '" + instanceId + "' -Confirm:$false -ErrorAction Stop; exit 0 } " +
            "catch { $_.Exception.Message; exit 1 }";

        var (exitCode, stdout, stderr) = await RunPowerShellAsync(command, TimeSpan.FromMinutes(2));
        if (exitCode != 0)
        {
            var reason = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            return OperationResult.Fail(
                $"重载显卡失败：{(string.IsNullOrWhiteSpace(reason) ? "PowerShell 执行出错" : reason.Trim())}。");
        }

        return OperationResult.Ok("显卡已禁用并重新启用。");
    }

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

    /// <summary>
    /// 判断是否台式机（机箱类型）：台式机允许对正在输出画面的显卡重载（有 45 秒恢复 + 60 秒亮屏确认双兜底）；
    /// 笔记本/未知形态一律硬拦截。
    /// </summary>
    private static async Task<bool> IsDesktopPcAsync()
    {
        var (code, stdout, _) = await RunPowerShellAsync(
            "(Get-CimInstance Win32_SystemEnclosure).ChassisTypes -join ','", TimeSpan.FromSeconds(20));
        if (code != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return false;
        }

        var chassisTypes = stdout
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var v) ? v : 0)
            .Where(v => v > 0)
            .ToList();

        // SMBIOS ChassisTypes：8/9/10/11/12/14/18/21/30/31/32 = 便携设备
        int[] laptopTypes = [8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32];
        // 3/4/5/6/7/15/16 = 桌面形态，23/24 等为机架/密封
        int[] desktopTypes = [3, 4, 5, 6, 7, 15, 16, 23, 24];

        if (chassisTypes.Any(laptopTypes.Contains))
        {
            return false;
        }

        return chassisTypes.Any(desktopTypes.Contains);
    }

    // ---------------- 登录自启动任务 ----------------

    /// <summary>创建登录自启动任务（帧格模式和临时显卡伪装的重启还原流程共用）。</summary>
    public async Task<OperationResult> EnableLogonAutostartAsync() => await EnableAutostartAsync();

    private static async Task<OperationResult> EnableAutostartAsync()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return OperationResult.Fail("无法确定本程序路径。");
            }

            var (code, stdout, stderr) = await RunProcessCaptureAsync(
                "schtasks.exe",
                $"/Create /TN {AutostartTaskName} /TR \"\\\"{exePath}\\\"\" /SC ONLOGON /RL HIGHEST /F",
                TimeSpan.FromSeconds(30));

            if (code != 0)
                return OperationResult.Fail(FirstLine(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr));

            if (!await DeleteTaskIfPresentAsync(LegacyAutostartTaskName))
            {
                await DeleteTaskIfPresentAsync(AutostartTaskName);
                return OperationResult.Fail("新登录任务已创建，但旧版任务无法移除；为避免重复启动，已回滚新任务。");
            }

            return OperationResult.Ok("登录自启动任务已创建。");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"创建登录自启动任务失败：{ex.Message}");
        }
    }

    /// <summary>删除登录自启动任务。</summary>
    public async Task<OperationResult> DisableLogonAutostartAsync() => await DisableAutostartAsync();

    private static async Task<OperationResult> DisableAutostartAsync()
    {
        try
        {
            var hadTask = await TaskExistsAsync(AutostartTaskName) || await TaskExistsAsync(LegacyAutostartTaskName);
            var currentRemoved = await DeleteTaskIfPresentAsync(AutostartTaskName);
            var legacyRemoved = await DeleteTaskIfPresentAsync(LegacyAutostartTaskName);
            return currentRemoved && legacyRemoved
                ? OperationResult.Ok(hadTask ? "登录自启动任务已移除。" : "登录自启动任务无需移除。")
                : OperationResult.Fail("删除登录自启动任务失败；请以管理员权限重试。");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"删除登录自启动任务失败：{ex.Message}");
        }
    }

    private async Task MigrateLegacyAutostartTaskAsync()
    {
        if (!await TaskExistsAsync(LegacyAutostartTaskName))
            return;

        var result = await EnableAutostartAsync();
        if (!result.Success)
            Log.Warn($"帧格登录任务迁移失败：{result.Message}");
        else
            Log.Info("帧格登录任务已从旧产品名称迁移到 Delta NFD。");
    }

    private static async Task<bool> TaskExistsAsync(string taskName)
    {
        var (code, _, _) = await RunProcessCaptureAsync("schtasks.exe", $"/Query /TN {taskName}", TimeSpan.FromSeconds(15));
        return code == 0;
    }

    private static async Task<bool> DeleteTaskIfPresentAsync(string taskName)
    {
        if (!await TaskExistsAsync(taskName))
            return true;

        var (code, stdout, stderr) = await RunProcessCaptureAsync(
            "schtasks.exe", $"/Delete /TN {taskName} /F", TimeSpan.FromSeconds(30));
        if (code != 0 && await TaskExistsAsync(taskName))
        {
            Log.Warn($"帧格登录任务删除失败：{taskName}；{FirstLine(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr)}");
            return false;
        }

        return true;
    }

    // ---------------- 手动重启 DWM ----------------

    public OperationResult RestartDwmNow() => KillDwm();

    // ---------------- 后台监控（DWM 功能） ----------------

    private void OnGameMonitorTick(bool gameRunning, int? unusedProcessId)
    {
        if (gameRunning != _isGameRunning)
        {
            IsGameRunning = gameRunning;
            if (!gameRunning)
            {
                _dwmHandledForSession = false;
            }

            UpdateStatus();
        }

        // 「游戏启动自动重启 DWM」：三角洲启动后延迟 5 秒再执行——进程刚拉起时集中加载资源，
        // 此刻重启 DWM 容易放大卡顿；等初始化窗口过去再重启（游戏早已运行超 5 秒则立即执行）
        if (gameRunning && _dwmRestartOnGameStart && !_dwmHandledForSession
            && GetGameUptime() >= DwmRestartDelayAfterGameStart)
        {
            var result = KillDwm();
            // 无论成败都标记本次会话已处理，避免每 2 秒反复杀 DWM；失败可用「立即重启 DWM」手动重试
            _dwmHandledForSession = true;
            Log.Info(result.Success
                ? $"DWM：游戏启动，已自动重启 DWM（{DateTime.Now:HH:mm:ss}）"
                : "DWM：游戏启动，自动重启 DWM 失败");

            StatusText = result.Success
                ? $"三角洲运行中 · 已自动重启 DWM（{DateTime.Now:HH:mm:ss}）"
                : $"三角洲运行中 · 自动重启 DWM 失败（可在帧格页手动重试）";
        }

        // 电源计划防改检查（内部自带约 30 秒一次的节流）
        _ = GuardPowerLockTickAsync();

        // CPU 亲和性规则（CPU实验室；仅三角洲进程，硬锁核持续保持，互斥让位见 CpuTopologyService）
        var affinitySettings = AppSettingsStore.Read();
        if (affinitySettings.GameAffinityRuleEnabled && affinitySettings.GameAffinityRuleMask != 0)
        {
            CpuTopologyService.ApplyAffinityRuleTick(affinitySettings.GameAffinityRuleMask);
        }
        else
        {
            CpuTopologyService.RestoreAffinityRule();
        }

        // CPU 调度持续维持（单CCD排除CPU0 / 双CCD游戏锁核与新进程软锁核；未激活时轻量返回）
        CpuTopologyService.ApplyCpuSchedulingTick();

        // 加速系统响应模式换挡（核心常驻持续 / EcoQoS 补应用新游戏 PID / 定时器随游戏启停）
        UpdateResponseBoostState();

        // 内存清理（实验性）：帧格激活 + 游戏运行期间按节流检查内存占用，达到阈值自动清空待备列表
        if (_memoryCleanEnabled && _frameModeActive && gameRunning)
        {
            MemoryCleanTick();
        }
    }

    private void UpdateStatus() => StatusText = DescribeStatus();

    private string DescribeStatus()
    {
        if (!_frameModeActive && !_dwmRestartOnGameStart)
        {
            return "帧格功能未启用";
        }

        if (_frameModeActive)
        {
            var spoofPart = IsGpuSpoofInFrame && _gpuSpoofEnabled
                ? $" · 显卡伪装为「{_gpuSpoofFakeName}」"
                : "";
            var powerPart = _powerPlanLockEnabled
                ? (string.IsNullOrWhiteSpace(_powerLockTargetGuid) ? " · 电源计划锁定" : " · 电源计划锁定（指定计划）")
                : "";
            var dualPart = _dualCcdArmed && _dualCcdEnabled ? " · 双CCD调度" : "";
            var boostPart = _responseBoostEnabled
                ? (_parkingLoop is { IsRunning: true }
                    ? (_parkedCoreCount >= 0 ? $" · 加速响应（停靠核心 {_parkedCoreCount}）" : " · 加速响应")
                    : " · 加速响应")
                : "";
            var foregroundPart = _foregroundBoostEnabled ? " · 前台加速" : "";
            var powerSavePart = _powerSaveLatencyEnabled ? " · 省电延迟" : "";
            var memCleanPart = _memoryCleanEnabled ? " · 内存清理" : "";
            var gamePart = _isGameRunning ? " · 三角洲运行中…" : "";
            return $"帧格模式运行中{spoofPart}{powerPart}{dualPart}{boostPart}{foregroundPart}{powerSavePart}{memCleanPart}{gamePart}";
        }

        return _isGameRunning ? "三角洲运行中…" : "已启用 · 等待三角洲启动…";
    }

    // ---------------- 进程操作（DWM） ----------------

    /// <summary>三角洲启动后延迟多久才自动重启 DWM（避开游戏初始化窗口）。</summary>
    private static readonly TimeSpan DwmRestartDelayAfterGameStart = TimeSpan.FromSeconds(5);


    /// <summary>
    /// 三角洲主进程已运行时长（按进程真实 StartTime 计算，不受 2 秒轮询粒度影响）。
    /// 进程已退出（竞态）或 StartTime 读取失败时返回超过延迟的值，视为"已过延迟"按原行为立即执行。
    /// </summary>
    private static TimeSpan GetGameUptime()
    {
        var processes = Process.GetProcessesByName(GameProcessName);
        if (processes.Length == 0)
        {
            return TimeSpan.FromMinutes(1);
        }

        try
        {
            using var main = processes[0];
            for (var i = 1; i < processes.Length; i++)
            {
                processes[i].Dispose();
            }

            return DateTime.Now - main.StartTime;
        }
        catch
        {
            foreach (var process in processes)
            {
                try { process.Dispose(); } catch { }
            }

            return TimeSpan.FromMinutes(1);
        }
    }

    private static OperationResult KillDwm()
    {
        try
        {
            var processes = Process.GetProcessesByName(DwmProcessName);
            if (processes.Length == 0)
            {
                return OperationResult.Fail("未找到 DWM 进程。");
            }

            var killed = 0;
            foreach (var process in processes)
            {
                using (process)
                {
                    try
                    {
                        process.Kill();
                        killed++;
                    }
                    catch
                    {
                        // 个别实例结束失败时忽略，只要有一个成功即可
                    }
                }
            }

            return killed > 0
                ? OperationResult.Ok("DWM 已结束，系统会立即自动重启它（屏幕会闪一下，属正常现象），重启后占用恢复。")
                : OperationResult.Fail("结束 DWM 失败，请确认以管理员身份运行本程序。");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"重启 DWM 失败：{ex.Message}");
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
                s.FrameModeActive = _frameModeActive;
            });
        }
    }

    // ---------------- 进程调用 ----------------

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessCaptureAsync(
        string fileName, string arguments, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            return (-1, "", $"无法启动 {fileName}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 进程可能已自行退出
            }

            return (-1, "", "执行超时");
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

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
        if (process is null)
        {
            return (-1, null, "无法启动 powershell.exe");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 进程可能已自行退出
            }

            return (-1, null, "PowerShell 执行超时");
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string FirstLine(string text)
    {
        var trimmed = text.Trim();
        var lineBreak = trimmed.IndexOfAny(['\r', '\n']);
        return lineBreak > 0 ? trimmed[..lineBreak] : trimmed;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        var handler = PropertyChanged;
        if (handler is null)
        {
            return;
        }

        var args = new PropertyChangedEventArgs(propertyName ?? "");
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            handler.Invoke(this, args);
        }
        else
        {
            _dispatcher.TryEnqueue(() => handler.Invoke(this, args));
        }
    }
}
