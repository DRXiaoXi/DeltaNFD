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
    private async void ReviewPowerBackup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (FrameSrv.FrameModeActive)
            {
                await new ContentDialog { XamlRoot = XamlRoot, Title = "先停用帧格", Content = "请先关闭一键帧格模式，再核对恢复记录，避免守护继续写入。", CloseButtonText = "知道了" }.ShowWithBackendRecoveryAsync();
                return;
            }
            var recovered = await FrameSrv.DeactivateFrameModeAsync();
            var unresolved = await FrameSrv.GetIncompletePowerBackupsAsync();
            if (unresolved.Count == 0)
            {
                await new ContentDialog { XamlRoot = XamlRoot, Title = recovered.Success ? "电源记录已核对" : "恢复未完成", Content = recovered.Message, CloseButtonText = "知道了" }.ShowWithBackendRecoveryAsync();
                return;
            }
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "无法精确恢复的电源记录",
                Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = string.Join("\n", unresolved.Select(r => r.Path + "\n" + r.Reason))
                    + "\n\n先前恢复已尽力处理可读取项。若确认当前电源设置可以保留，可将列出的原记录归档并关闭“降低省电延迟”；若包含内容快照，也会关闭电源计划锁定。这不是恢复成功或恢复默认，不会在此步骤写入电源值或重建已删除的计划、原值；归档后原文件仍可用于排查。" },
                PrimaryButtonText = "保留当前值并归档", CloseButtonText = "继续保留待恢复", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowWithBackendRecoveryAsync() != ContentDialogResult.Primary) return;
            var result = await FrameSrv.PreserveCurrentPowerValuesAsync(unresolved);
            await new ContentDialog { XamlRoot = XamlRoot, Title = result.Success ? "已归档证据" : "未完成", Content = result.Message, CloseButtonText = "知道了" }.ShowWithBackendRecoveryAsync();
        }
        catch (Exception ex)
        {
            Log.Error("电源记录核对失败，证据保留", ex);
            if (await new ContentDialog { XamlRoot = XamlRoot, Title = "电源记录尚未处理",
                Content = ex.Message + "\n\n若已有 CPU/帧格待核对记录，请先进入核对流程。无法恢复的旧电源备份可在其中选择保留当前值并归档，再完整复核其他状态。",
                PrimaryButtonText = "核对未确认操作", CloseButtonText = "暂不处理", DefaultButton = ContentDialogButton.Close,
            }.ShowWithBackendRecoveryAsync() == ContentDialogResult.Primary)
                await BackendReviewFlow.RunAsync(XamlRoot, [DeltaNFD.Backends.BackendModules.CpuFrame]);
        }
    }
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
    private bool _memoryGuidanceRequested;

    public void ShowAutomaticMemorySettings()
    {
        _memoryGuidanceRequested = true;
        ShowMemoryGuidanceIfReady();
    }

    private void ShowMemoryGuidanceIfReady()
    {
        if (!_memoryGuidanceRequested || !IsLoaded) return;
        _memoryGuidanceRequested = false;
        MemoryRecoveryGuidanceInfo.IsOpen = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded) MemoryRecoveryGuidanceInfo.StartBringIntoView(new BringIntoViewOptions
            { AnimationDesired = false, VerticalAlignmentRatio = 0 });
        });
    }

    /// <summary>下拉选项填充期间的程序化 SelectionChanged 不写回服务。</summary>
    private bool _suppressPowerTargetSelection;

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

    public string DwmStatusText => FrameSrv.DwmStatusText;
    public string PowerLockHelpText => FrameSrv.PowerPlanLockText
        + "\n\n亮度相关设置保留用户调整；其他受保护计划内容被改动时会按快照还原。需要保留其他修改时，请先关闭锁定或停用帧格。";
    public string GamePriorityStatusText => FrameSrv.GamePriorityStatusText;
    public bool GamePriorityOptionsEditable => FrameSrv.FeaturesEditable && FrameSrv.GameProcessPriorityEnabled;
    private static readonly FrameCpuPriority[] CpuPriorityChoices = [FrameCpuPriority.RealTime, FrameCpuPriority.High, FrameCpuPriority.AboveNormal, FrameCpuPriority.Normal, FrameCpuPriority.BelowNormal, FrameCpuPriority.Idle];
    private static readonly FrameIoPriority[] IoPriorityChoices = [FrameIoPriority.High, FrameIoPriority.Normal, FrameIoPriority.Low, FrameIoPriority.VeryLow];
    private static readonly FrameMemoryPriority[] MemoryPriorityChoices = [FrameMemoryPriority.Normal, FrameMemoryPriority.BelowNormal, FrameMemoryPriority.Medium, FrameMemoryPriority.Low, FrameMemoryPriority.VeryLow];
    public int GameCpuPriorityIndex => Array.IndexOf(CpuPriorityChoices, FrameSrv.GameCpuPriority);
    public int GameIoPriorityIndex => Array.IndexOf(IoPriorityChoices, FrameSrv.GameIoPriority);
    public int GameMemoryPriorityIndex => Array.IndexOf(MemoryPriorityChoices, FrameSrv.GameMemoryPriority);
    private void GamePrioritySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !GamePriorityOptionsEditable || sender is not ComboBox { IsLoaded: true } box) return;
        var index = box.SelectedIndex;
        if (box.Tag as string == "cpu" && index >= 0 && index < CpuPriorityChoices.Length && index != GameCpuPriorityIndex) FrameSrv.GameCpuPriority = CpuPriorityChoices[index];
        else if (box.Tag as string == "io" && index >= 0 && index < IoPriorityChoices.Length && index != GameIoPriorityIndex) FrameSrv.GameIoPriority = IoPriorityChoices[index];
        else if (box.Tag as string == "memory" && index >= 0 && index < MemoryPriorityChoices.Length && index != GameMemoryPriorityIndex) FrameSrv.GameMemoryPriority = MemoryPriorityChoices[index];
    }
    public string GamePowerStatusText => FrameSrv.GamePowerHasError ? FrameSrv.GamePowerStatusText
        : FrameSrv.FrameModeActive && FrameSrv.GamePowerThrottlingDisabled
            ? (string.IsNullOrWhiteSpace(FrameSrv.GamePowerStatusText) ? "正在等待后端核对" : FrameSrv.GamePowerStatusText)
            : FrameSrv.GamePowerThrottlingDisabled ? "已选择 · 开启一键帧格模式后生效" : "未启用";
    public bool GamePowerSelected
    {
        get => FrameSrv.GamePowerThrottlingDisabled;
        set { if (FrameSrv.FeaturesEditable) FrameSrv.GamePowerThrottlingDisabled = value; }
    }
    public string GameTimerIgnoreStatusText => FrameSrv.GameTimerIgnoreStatusText;
    public bool GameTimerIgnoreSelected
    {
        get => FrameSrv.GameTimerIgnoreEnabled;
        set { if (GameTimerIgnoreEditable) FrameSrv.GameTimerIgnoreEnabled = value; }
    }
    public bool GameTimerIgnoreEditable => FrameSrv.FeaturesEditable;
    public string ForegroundStatusText => FrameFeatureState.Describe(FrameSrv.ForegroundBoostEnabled,
        FrameSrv.ForegroundResponsivenessEnabled || FrameSrv.ForegroundPriorityEnabled, FrameSrv.FrameModeEnabled);
    public string PowerSaveStatusText => FrameFeatureState.Describe(FrameSrv.PowerSaveLatencyEnabled,
        FrameSrv.NicPowerSavingOffEnabled || FrameSrv.UsbSuspendOffEnabled || FrameSrv.PcieAspmOffEnabled,
        FrameSrv.FrameModeEnabled);

    // 绑定回写可能由界面刷新触发；父功能关闭或帧格激活时不覆盖保存的子选项。
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
        nameof(DwmStatusText), nameof(PowerLockHelpText), nameof(ForegroundStatusText), nameof(PowerSaveStatusText),
        nameof(GamePowerSelected), nameof(GamePowerStatusText),
        nameof(GameTimerIgnoreSelected), nameof(GameTimerIgnoreStatusText), nameof(GameTimerIgnoreEditable),
        nameof(GamePriorityStatusText), nameof(GamePriorityOptionsEditable), nameof(GameCpuPriorityIndex), nameof(GameIoPriorityIndex), nameof(GameMemoryPriorityIndex),
        nameof(ResponsivenessSelected), nameof(PrioritySelected), nameof(NicSelected), nameof(UsbSelected),
        nameof(PcieSelected),
        nameof(ForegroundSubTogglesEditable), nameof(PowerSaveSubTogglesEditable),
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
            _ = LoadFrameBackgroundAsync();
            ShowMemoryGuidanceIfReady();
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
            or nameof(IFrameService.IsGameRunning) or nameof(IFrameService.DwmRestartOnGameStart) or nameof(IFrameService.DwmStatusText)
            or nameof(IFrameService.GamePowerThrottlingDisabled) or nameof(IFrameService.GamePowerStatusText) or nameof(IFrameService.GamePowerHasError)
            or nameof(IFrameService.GameTimerIgnoreEnabled) or nameof(IFrameService.GameTimerIgnoreStatusText) or nameof(IFrameService.GameTimerIgnoreSupported) or nameof(IFrameService.GameTimerIgnoreHasError)
            or nameof(IFrameService.GameProcessPriorityEnabled) or nameof(IFrameService.GameCpuPriority) or nameof(IFrameService.GameIoPriority) or nameof(IFrameService.GameMemoryPriority) or nameof(IFrameService.GamePriorityStatusText)
            or nameof(IFrameService.ForegroundBoostEnabled) or nameof(IFrameService.ForegroundResponsivenessEnabled)
            or nameof(IFrameService.PowerPlanLockText) or nameof(IFrameService.PowerPlanLockEnabled) or nameof(IFrameService.FramePowerLockTargetGuid)
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

            // 旧设置可能保存的是卓越性能“模板 GUID”，而实际导入的是随机副本 GUID。
            // 这里先归一化到副本/缓存 GUID，避免列表匹配不到后静默回落到“当前计划”。
            var saved = FrameSrv.FramePowerLockTargetGuid;
            if (!string.IsNullOrWhiteSpace(saved) &&
                saved.Equals(PowerSchemes.UltimateSchemeTemplateGuid, StringComparison.OrdinalIgnoreCase))
            {
                var cachedUltimate = AppSettingsStore.Read().UltimatePowerSchemeGuid;
                if (!string.IsNullOrWhiteSpace(cachedUltimate) &&
                    schemes.Any(s => s.Guid.Equals(cachedUltimate, StringComparison.OrdinalIgnoreCase)))
                {
                    saved = cachedUltimate;
                }
                else
                {
                    var byName = schemes.FirstOrDefault(s => s.Name == "卓越性能");
                    if (byName is not null) saved = byName.Guid;
                }
                if (!saved.Equals(FrameSrv.FramePowerLockTargetGuid, StringComparison.OrdinalIgnoreCase))
                    FrameSrv.FramePowerLockTargetGuid = saved;
            }

            var selected = 0;
            var matched = false;
            foreach (var scheme in schemes)
            {
                var label = scheme.Name + (scheme.IsActive ? "（当前激活）" : "");
                PowerLockTargets.Add(new PowerLockTargetItem { Label = label, Guid = scheme.Guid });
                if (!string.IsNullOrEmpty(saved) && scheme.Guid.Equals(saved, StringComparison.OrdinalIgnoreCase))
                {
                    selected = PowerLockTargets.Count - 1;
                    matched = true;
                }
            }

            if (!matched && !string.IsNullOrWhiteSpace(saved))
            {
                // 不静默把用户选择改成“当前计划”：保留原 GUID 并明确提示重新选择。
                PowerLockTargets.Add(new PowerLockTargetItem
                {
                    Label = "已保存的计划（当前列表中未找到，请重新选择）",
                    Guid = saved,
                });
                selected = PowerLockTargets.Count - 1;
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
            await LoadFrameBackgroundAsync();
        }
    }

    private void UpdateGameDot() => GameDotBrush = FrameSrv.IsGameRunning ? GameRunningBrush : GameStoppedBrush;

    private static SolidColorBrush MakeBrush(byte r, byte g, byte b) =>
        new(Windows.UI.Color.FromArgb(255, r, g, b));

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

        if (await confirm.ShowWithBackendRecoveryAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = await FrameSrv.RestartDwmNowAsync();
        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = result.Success ? "完成" : "操作失败",
            Content = result.Message,
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
        }.ShowWithBackendRecoveryAsync();
    }
}
