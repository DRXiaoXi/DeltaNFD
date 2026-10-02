using System.Collections.ObjectModel;
using System.ComponentModel;
using DeltaNFD.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeltaNFD.Views;

/// <summary>电源锁定目标下拉的一行：显示名 + GUID（空 GUID = 当前计划模式）。</summary>
public sealed class PowerLockTargetItem
{
    public string Label { get; set; } = "";
    public string Guid { get; set; } = "";

    public override string ToString() => Label;
}

public sealed partial class FramePage : Page, INotifyPropertyChanged
{
    public string TargetLabel => "当前主进程：" + DeltaNFD.Services.GameTargetService.Default.Current.DisplayName;
    public bool NormalModeControlsEnabled => !OfflineModeGuard.BlocksNormalAutomation;
    private static readonly SolidColorBrush GameRunningBrush =
        MakeBrush(0x22, 0xC5, 0x5E);

    private static readonly SolidColorBrush GameStoppedBrush =
        MakeBrush(0x9C, 0xA3, 0xAF);

    /// <summary>帧格服务（命名为 FrameSrv 以避免与 Page.Frame 冲突）。</summary>
    public IFrameService FrameSrv { get; } = ServiceLocator.Frame;

    /// <summary>电源锁定目标计划下拉选项。</summary>
    public ObservableCollection<PowerLockTargetItem> PowerLockTargets { get; } = new();

    private Brush gameDotBrush = GameStoppedBrush;

    /// <summary>游戏运行状态指示点颜色（三角洲运行 = 绿色）。</summary>
    public Brush GameDotBrush
    {
        get => gameDotBrush;
        private set
        {
            gameDotBrush = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GameDotBrush)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>程序化回写总开关时置 true，防止 Toggled 处理器重复弹窗。</summary>
    private bool _suppressFrameModeToggle;

    /// <summary>下拉选项填充期间的程序化 SelectionChanged 不写回服务。</summary>
    private bool _suppressPowerTargetSelection;

    /// <summary>加速系统响应模式子开关可用性：帧格未激活 && 总开关已开。</summary>
    public bool SubTogglesEditable => FrameSrv.FeaturesEditable && FrameSrv.ResponseBoostEnabled;

    /// <summary>前台加速响应子开关可用性：帧格未激活 && 总开关已开。</summary>
    public bool ForegroundSubTogglesEditable => FrameSrv.FeaturesEditable && FrameSrv.ForegroundBoostEnabled;

    /// <summary>降低省电延迟子开关可用性：帧格未激活 && 总开关已开。</summary>
    public bool PowerSaveSubTogglesEditable => FrameSrv.FeaturesEditable && FrameSrv.PowerSaveLatencyEnabled;

    /// <summary>
    /// 「笔记本不适用」的两个子开关（网卡省电全禁 / 关闭 PCIe 省电）是否可编辑：
    /// 在原有可用性基础上，笔记本形态一律置灰（只贴角标不置灰等于骗人）。
    /// </summary>
    public bool LaptopExcludedSubTogglesEditable => PowerSaveSubTogglesEditable && !FrameSrv.IsLaptopChassis;

    /// <summary>「笔记本不适用」角标可见性（仅机箱形态确认为笔记本时为 true；无法确定不显示）。</summary>
    public bool LaptopExcludedBadgeVisible => FrameSrv.IsLaptopChassis;

    public string DwmStatusText => !FrameSrv.DwmRestartOnGameStart ? "未启用"
        : FrameSrv.IsGameRunning ? "已选择 · 目标正在运行" : "已启用 · 等待目标启动";
    public string ResponseStatusText => FrameFeatureState.Describe(FrameSrv.ResponseBoostEnabled,
        FrameSrv.ResponseBoostCoreParkingEnabled || FrameSrv.ResponseBoostEcoQosEnabled || FrameSrv.ResponseBoostTimerEnabled,
        FrameSrv.FrameModeEnabled);
    public string ForegroundStatusText => FrameFeatureState.Describe(FrameSrv.ForegroundBoostEnabled,
        FrameSrv.ForegroundResponsivenessEnabled || FrameSrv.ForegroundPriorityEnabled, FrameSrv.FrameModeEnabled);
    public string PowerSaveStatusText => FrameFeatureState.Describe(FrameSrv.PowerSaveLatencyEnabled,
        FrameSrv.NicPowerSavingOffEnabled || FrameSrv.UsbSuspendOffEnabled || FrameSrv.PcieAspmOffEnabled,
        FrameSrv.FrameModeEnabled);

    // 绑定回写可能由界面刷新触发；父功能关闭或帧格激活时不覆盖保存的子选项。
    public bool CoreParkingSelected
    {
        get => FrameFeatureState.IsSelected(FrameSrv.ResponseBoostEnabled, FrameSrv.ResponseBoostCoreParkingEnabled);
        set { if (SubTogglesEditable) FrameSrv.ResponseBoostCoreParkingEnabled = value; }
    }
    public bool EcoQosSelected
    {
        get => FrameFeatureState.IsSelected(FrameSrv.ResponseBoostEnabled, FrameSrv.ResponseBoostEcoQosEnabled);
        set { if (SubTogglesEditable) FrameSrv.ResponseBoostEcoQosEnabled = value; }
    }
    public bool TimerSelected
    {
        get => FrameFeatureState.IsSelected(FrameSrv.ResponseBoostEnabled, FrameSrv.ResponseBoostTimerEnabled);
        set { if (SubTogglesEditable) FrameSrv.ResponseBoostTimerEnabled = value; }
    }
    public bool ResponsivenessSelected
    {
        get => FrameFeatureState.IsSelected(FrameSrv.ForegroundBoostEnabled, FrameSrv.ForegroundResponsivenessEnabled);
        set { if (ForegroundSubTogglesEditable) FrameSrv.ForegroundResponsivenessEnabled = value; }
    }
    public bool PrioritySelected
    {
        get => FrameFeatureState.IsSelected(FrameSrv.ForegroundBoostEnabled, FrameSrv.ForegroundPriorityEnabled);
        set { if (ForegroundSubTogglesEditable) FrameSrv.ForegroundPriorityEnabled = value; }
    }
    public bool NicSelected
    {
        get => FrameFeatureState.IsSelected(FrameSrv.PowerSaveLatencyEnabled, FrameSrv.NicPowerSavingOffEnabled);
        set { if (LaptopExcludedSubTogglesEditable) FrameSrv.NicPowerSavingOffEnabled = value; }
    }
    public bool UsbSelected
    {
        get => FrameFeatureState.IsSelected(FrameSrv.PowerSaveLatencyEnabled, FrameSrv.UsbSuspendOffEnabled);
        set { if (PowerSaveSubTogglesEditable) FrameSrv.UsbSuspendOffEnabled = value; }
    }
    public bool PcieSelected
    {
        get => FrameFeatureState.IsSelected(FrameSrv.PowerSaveLatencyEnabled, FrameSrv.PcieAspmOffEnabled);
        set { if (LaptopExcludedSubTogglesEditable) FrameSrv.PcieAspmOffEnabled = value; }
    }

    private static readonly string[] FeaturePresentationProperties =
    {
        nameof(DwmStatusText), nameof(ResponseStatusText), nameof(ForegroundStatusText), nameof(PowerSaveStatusText),
        nameof(CoreParkingSelected), nameof(EcoQosSelected), nameof(TimerSelected),
        nameof(ResponsivenessSelected), nameof(PrioritySelected), nameof(NicSelected), nameof(UsbSelected),
        nameof(PcieSelected),
        nameof(SubTogglesEditable), nameof(ForegroundSubTogglesEditable), nameof(PowerSaveSubTogglesEditable),
        nameof(LaptopExcludedSubTogglesEditable), nameof(LaptopExcludedBadgeVisible),
    };

    private void RefreshFeaturePresentation()
    {
        foreach (var property in FeaturePresentationProperties)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    public FramePage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            FrameSrv.PropertyChanged -= OnFramePropertyChanged;
            FrameSrv.PropertyChanged += OnFramePropertyChanged;
            FrameSrv.RefreshDualCcdArmState();
            UpdateGameDot();
            RefreshFeaturePresentation();
        };
        Unloaded += (_, _) => FrameSrv.PropertyChanged -= OnFramePropertyChanged;
        UpdateGameDot();

        _ = LoadPowerLockTargetsAsync();
    }

    private void OnFramePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IFrameService.IsGameRunning))
        {
            UpdateGameDot();
        }

        if (e.PropertyName is null or "" or nameof(IFrameService.FeaturesEditable) or nameof(IFrameService.FrameModeEnabled)
            or nameof(IFrameService.IsGameRunning) or nameof(IFrameService.DwmRestartOnGameStart)
            or nameof(IFrameService.ResponseBoostEnabled) or nameof(IFrameService.ResponseBoostCoreParkingEnabled)
            or nameof(IFrameService.ResponseBoostEcoQosEnabled) or nameof(IFrameService.ResponseBoostTimerEnabled)
            or nameof(IFrameService.ForegroundBoostEnabled) or nameof(IFrameService.ForegroundResponsivenessEnabled)
            or nameof(IFrameService.ForegroundPriorityEnabled) or nameof(IFrameService.PowerSaveLatencyEnabled)
            or nameof(IFrameService.NicPowerSavingOffEnabled) or nameof(IFrameService.UsbSuspendOffEnabled)
            or nameof(IFrameService.PcieAspmOffEnabled) or nameof(IFrameService.IsLaptopChassis))
        {
            RefreshFeaturePresentation();
        }
    }

    /// <summary>加载电源计划列表填充「锁定计划」下拉（首项 = 当前计划模式）。</summary>
    private async Task LoadPowerLockTargetsAsync()
    {
        try
        {
            var schemes = await ServiceLocator.Power.GetSchemesAsync();

            _suppressPowerTargetSelection = true;
            PowerLockTargets.Clear();
            PowerLockTargets.Add(new PowerLockTargetItem
            {
                Label = "当前计划（锁定时不切换）",
                Guid = "",
            });

            var selected = 0;
            foreach (var scheme in schemes)
            {
                var label = scheme.Name + (scheme.IsActive ? "（当前激活）" : "");
                PowerLockTargets.Add(new PowerLockTargetItem { Label = label, Guid = scheme.Guid });
                if (!string.IsNullOrEmpty(FrameSrv.FramePowerLockTargetGuid) &&
                    scheme.Guid.Equals(FrameSrv.FramePowerLockTargetGuid, StringComparison.OrdinalIgnoreCase))
                {
                    selected = PowerLockTargets.Count - 1;
                }
            }

            PowerLockTargetCombo.ItemsSource = PowerLockTargets;
            PowerLockTargetCombo.SelectedIndex = selected;
        }
        catch
        {
            // 计划列表读取失败时保留「当前计划」单项，不阻断页面
        }
        finally
        {
            _suppressPowerTargetSelection = false;
        }
    }

    private void PowerLockTargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPowerTargetSelection || PowerLockTargetCombo.SelectedItem is not PowerLockTargetItem item)
        {
            return;
        }

        FrameSrv.FramePowerLockTargetGuid = item.Guid;
    }

    private async void FrameModeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressFrameModeToggle)
        {
            return;
        }

        if (sender is not ToggleSwitch toggle)
        {
            return;
        }

        // 程序化回写（激活/退出成功后绑定刷新）时开关与状态一致，直接忽略
        if (toggle.IsOn == FrameSrv.FrameModeEnabled)
        {
            return;
        }

        _suppressFrameModeToggle = true;
        try
        {
            await FrameModeFlow.RunToggleAsync(FrameSrv, toggle);
        }
        finally
        {
            // 流程取消或失败：把开关拨回真实状态
            if (toggle.IsOn != FrameSrv.FrameModeEnabled)
            {
                toggle.IsOn = FrameSrv.FrameModeEnabled;
            }

            _suppressFrameModeToggle = false;
        }
    }

    private void UpdateGameDot() => GameDotBrush = FrameSrv.IsGameRunning ? GameRunningBrush : GameStoppedBrush;

    private static SolidColorBrush MakeBrush(byte r, byte g, byte b) =>
        new(Windows.UI.Color.FromArgb(255, r, g, b));

    private async void MemoryCleanButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            var result = await FrameSrv.CleanMemoryNowAsync();
            if (!result.Success)
            {
                await new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "内存清理失败",
                    Content = result.Message,
                    CloseButtonText = "知道了",
                    DefaultButton = ContentDialogButton.Close,
                }.ShowAsync();
            }
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void RestartDwmButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "立即重启 DWM？",
            Content = "DWM 会被结束并由系统立即自动重启，屏幕会闪烁一下（全屏应用会短暂切回桌面），属正常现象。",
            PrimaryButtonText = "重启",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = FrameSrv.RestartDwmNow();
        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = result.Success ? "完成" : "操作失败",
            Content = result.Message,
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
        }.ShowAsync();
    }
}
