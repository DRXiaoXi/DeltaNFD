using DeltaNFD.Services;
using DeltaNFD.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace DeltaNFD.Views;

/// <summary>主题色预设（设置页色板）。</summary>
public sealed class AccentOption
{
    public required string Name { get; init; }
    public required string Hex { get; init; }
    public required Windows.UI.Color Color { get; init; }
    public bool IsSelected { get; set; }
}

public sealed partial class SettingsPage : Page
{
    public string[] ThemeOptions { get; } = ["浅色", "深色", "跟随系统"];

    public SettingsViewModel ViewModel { get; } = new();

    /// <summary>「软件更新」卡的 ViewModel（检查 / 下载 / 安装交接）。</summary>
    public UpdateViewModel Update { get; } = new();

    /// <summary>主题色预设（含默认天空蓝与历史用色）。</summary>
    private static readonly (string Name, string Hex)[] AccentPresets =
    [
        ("天空蓝", "#5EB0FF"),
        ("军事橙", "#F97316"),
        ("薄荷绿", "#0FF796"),
        ("紫罗兰", "#A78BFA"),
        ("玫粉", "#F472B6"),
        ("琥珀", "#FBBF24"),
        ("青碧", "#22D3EE"),
        ("石墨", "#94A3B8"),
    ];

    /// <summary>程序化回写滑条时置 true，避免触发遮罩重复写设置。</summary>
    private bool _suppressDimSlider;
    private readonly OfflineModeCoordinator _offlineMode = new();
    private bool _suppressOfflineModeToggle;
    private bool _offlineModeBusy;

    public SettingsPage()
    {
        InitializeComponent();
        RefreshOfflineModeControls();

        // 关于卡 logo：ms-appx 资源加载失败时回退到绝对路径文件
        AboutLogoImage.ImageFailed += (_, _) =>
        {
            try
            {
                var logoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "DeltaNFD.png");
                AboutLogoImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(logoPath));
            }
            catch
            {
                // logo 加载失败不影响页面功能
            }
        };

        // 初始滑条位置 = 当前遮罩浓度（程序化赋值期间抑制事件）
        _suppressDimSlider = true;
        DimSlider.Value = BackgroundManager.DimOpacity * 100;
        _suppressDimSlider = false;

        // 更新确认框（ViewModel 不能直接弹 ContentDialog，通过钩子注入，见 HANDOFF §3.2）
        Update.ConfirmHook = async (title, message, primaryText) =>
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = primaryText,
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        };

        var current = BackgroundManager.ResolveCurrent();
        BackgroundStatusText.Text = DescribeBackground(current);
        // 背景图片模式开关切换后状态文字跟随刷新（否则显示与现实相反）
        Loaded += (_, _) =>
        {
            ServiceLocator.GameTarget.Changed -= OnGameTargetChanged;
            ServiceLocator.GameTarget.Changed += OnGameTargetChanged;
            RefreshTargetControls();
            BackgroundManager.Changed -= OnBackgroundChanged;
            BackgroundManager.Changed += OnBackgroundChanged;
            OnBackgroundChanged();
            Update.Attach();
            RefreshOfflineModeControls();
        };
        Unloaded += (_, _) =>
        {
            ServiceLocator.GameTarget.Changed -= OnGameTargetChanged;
            BackgroundManager.Changed -= OnBackgroundChanged;
            Update.Detach();
        };

        BuildAccentSwatches();

        // 三角洲目录识别：回显手动指定值 + 当前识别结果
        var overridePath = AppSettingsStore.Read().GameRootOverride;
        GamePathBox.Text = overridePath ?? "";
        UpdateGamePathStatus();
    }

    private void RefreshOfflineModeControls()
    {
        if (!_offlineMode.TryReadState(out var state, out var error))
        {
            _suppressOfflineModeToggle = true;
            OfflineModeToggle.IsOn = true;
            OfflineModeToggle.IsEnabled = false;
            _suppressOfflineModeToggle = false;
            OfflineModeStartButton.IsEnabled = false;
            OfflineModeStatusText.Text = error;
            ViewModel.RefreshOfflineModeControls();
            SetTargetControlsEnabled(false);
            return;
        }

        _suppressOfflineModeToggle = true;
        OfflineModeToggle.IsOn = state.OfflineModeEnabled ||
            state.Status is OfflineModeStatus.Preparing or OfflineModeStatus.RestorePending or OfflineModeStatus.Failed;
        OfflineModeToggle.IsEnabled = !_offlineModeBusy;
        _suppressOfflineModeToggle = false;
        OfflineModeStartButton.IsEnabled = !_offlineModeBusy && state.OfflineModeEnabled &&
            state.SavedPreferences is not null &&
            (state.Status is OfflineModeStatus.Ready or OfflineModeStatus.Kept or OfflineModeStatus.Failed);
        // 插件脱机开关状态与脱机就绪文案（规范第 8 节：开启即“非零进程”）。
        if (AllowPluginsOfflineToggle is not null)
        {
            _suppressOfflineModeToggle = true;
            AllowPluginsOfflineToggle.IsOn = AppSettingsStore.Read().AllowPluginsInOfflineMode;
            _suppressOfflineModeToggle = false;
        }
        if (AppSettingsStore.Read().AllowPluginsInOfflineMode && !state.OfflineModeEnabled)
            OfflineModeStatusText.Text = state.Message +
                Environment.NewLine + "注意：已允许插件在脱机模式下运行——移交后为“非零进程”，不适用零进程验收。";
        else
            OfflineModeStatusText.Text = state.Message;
        ViewModel.RefreshOfflineModeControls();
        SetTargetControlsEnabled(!_offlineModeBusy && !state.BlocksNormalAutomation);
    }

    private void SetTargetControlsEnabled(bool enabled)
    {
        if (CustomGameModeToggle is null) return;
        var target = ServiceLocator.GameTarget.Current;
        CustomGameModeToggle.IsEnabled = enabled && !_targetSelectionBusy;
        SelectGameExeButton.IsEnabled = enabled && !_targetSelectionBusy;
        GamePathBox.IsEnabled = enabled && !target.IsCustom && !_targetSelectionBusy;
        PickGameFolderButton.IsEnabled = enabled && !target.IsCustom && !_targetSelectionBusy;
        ClearGamePathButton.IsEnabled = enabled && !target.IsCustom && !_targetSelectionBusy;
        SaveGamePathButton.IsEnabled = enabled && !target.IsCustom && !_targetSelectionBusy;
    }

    private async void OfflineModeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressOfflineModeToggle || _offlineModeBusy) return;
        var enable = OfflineModeToggle.IsOn;
        if (!await ConfirmOfflineModeChangeAsync(enable))
        {
            RefreshOfflineModeControls();
            return;
        }

        // 模式切换前统一插件拦截（待做清单第 9 项）：停止/恢复/排空失败阻止切换。
        _offlineModeBusy = true;
        RefreshOfflineModeControls();
        var pluginBlocker = await PluginLifecycleGuard.PrepareHandoverAsync(
            ServiceLocator.Plugins, ServiceLocator.PluginRuntime, "脱机模式切换");
        if (pluginBlocker.Length > 0)
        {
            _offlineModeBusy = false;
            OfflineModeStatusText.Text = pluginBlocker;
            RefreshOfflineModeControls();
            return;
        }
        OperationResult result;
        try
        {
            result = enable
                ? await _offlineMode.EnableAsync()
                : await _offlineMode.DisableAsync();
        }
        catch (Exception ex)
        {
            result = OperationResult.Fail("脱机模式切换异常：" + ex.Message);
            Services.Log.Error("脱机模式切换异常", ex);
        }
        finally { _offlineModeBusy = false; }

        RefreshOfflineModeControls();
        OfflineModeStatusText.Text = result.Message;
    }

    private async Task<bool> ConfirmOfflineModeChangeAsync(bool enable)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = enable ? "启用脱机模式" : "关闭脱机模式",
            Content = new TextBlock
            {
                Text = enable
                    ? "启用前会尝试还原普通帧格、锁核与调度，并关闭本工具的自启动和托盘驻留。原偏好会保存；恢复不完整时会拒绝进入。助手只处理原游戏平台启动的目标进程，不启动、不注入游戏。"
                    : "将先还原脱机 CPU Sets，再恢复普通参数；不会自动开启帧格、锁核、自启动或托盘，原启用偏好会另存。进程已退出或 PID 已复用时不会触碰新进程；身份无法确认或还原失败时保留记录。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = enable ? "启用" : "关闭并恢复",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void AllowPluginsOfflineToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (AllowPluginsOfflineToggle is null) return;
        var desired = AllowPluginsOfflineToggle.IsOn;
        var current = AppSettingsStore.Read().AllowPluginsInOfflineMode;
        if (desired == current) return;

        if (desired)
        {
            // 开启：只保存偏好（不自动启动/移交任何插件）；移交发生在准备脱机启动时。
            AppSettingsStore.Update(s => s.AllowPluginsInOfflineMode = true);
            OfflineModeStatusText.Text = "已允许插件在脱机模式下运行。准备脱机启动时会先移交已单独授权的插件（标记为非零进程）。";
            return;
        }

        // 关闭：立即排空所有已移交插件（重连→身份复核→stop→确认退出）；失败不宣称零进程。
        AllowPluginsOfflineToggle.IsEnabled = false;
        try
        {
            var failures = await ServiceLocator.PluginRuntime.DrainOfflinePluginsAsync();
            AppSettingsStore.Update(s => s.AllowPluginsInOfflineMode = false);
            OfflineModeStatusText.Text = failures.Count == 0
                ? "已停止所有已移交插件，恢复默认零进程脱机。"
                : "部分插件未能确认停止（记录保留）：" + Environment.NewLine +
                  string.Join(Environment.NewLine, failures) + Environment.NewLine +
                  "不宣称零进程就绪，请人工处理。";
        }
        finally
        {
            AllowPluginsOfflineToggle.IsEnabled = true;
            RefreshOfflineModeControls();
        }
    }

    private async void OfflineModeStartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_offlineModeBusy) return;
        _offlineModeBusy = true;
        RefreshOfflineModeControls();
        OperationResult result;
        try
        {
            // 插件脱机移交（规范第 8 节）：总开关开启时，先移交已单独授权的插件，再启动官方脱机助手。
            if (AppSettingsStore.Read().AllowPluginsInOfflineMode)
            {
                var manager = ServiceLocator.Plugins;
                var candidates = manager.ListOfflineHandoffCandidates(true, out var listError);
                if (listError.Length > 0)
                {
                    result = OperationResult.Fail("读取插件列表失败，未开始脱机准备：" + listError);
                    OfflineModeStatusText.Text = result.Message;
                    RefreshOfflineModeControls();
                    return;
                }
                foreach (var candidate in candidates)
                {
                    var handoffError = await ServiceLocator.PluginRuntime.HandoffToOfflineAsync(candidate);
                    if (handoffError.Length > 0)
                    {
                        result = OperationResult.Fail($"插件“{candidate.Id}”移交失败，未开始脱机准备：{handoffError}");
                        OfflineModeStatusText.Text = result.Message;
                        RefreshOfflineModeControls();
                        return;
                    }
                }
            }
            result = await _offlineMode.StartHelperAsync();
        }
        catch (Exception ex)
        {
            result = OperationResult.Fail("启动脱机助手异常：" + ex.Message);
            Services.Log.Error("启动脱机助手异常", ex);
        }
        finally { _offlineModeBusy = false; }

        OfflineModeStatusText.Text = result.Message;
        RefreshOfflineModeControls();
        if (result.Success)
            App.MainWindow?.ExitAfterOfflineHelper();
    }

    private void OnBackgroundChanged() =>
        BackgroundStatusText.Text = DescribeBackground(BackgroundManager.ResolveCurrent());

    private bool _updatingTargetControls;
    private bool _targetSelectionBusy;
    private void OnGameTargetChanged(GameTarget target) => DispatcherQueue.TryEnqueue(RefreshTargetControls);
    private void RefreshTargetControls()
    {
        if (CustomGameModeToggle is null) return;
        _updatingTargetControls = true;
        var target = ServiceLocator.GameTarget.Current;
        var normalAutomationEnabled = !OfflineModeGuard.BlocksNormalAutomation;
        GamePathBox.IsEnabled = normalAutomationEnabled && !target.IsCustom;
        PickGameFolderButton.IsEnabled = normalAutomationEnabled && !target.IsCustom;
        ClearGamePathButton.IsEnabled = normalAutomationEnabled && !target.IsCustom;
        SaveGamePathButton.IsEnabled = normalAutomationEnabled && !target.IsCustom;
        CustomGameModeToggle.IsOn = target.IsCustom;
        CustomGameModeToggle.IsEnabled = normalAutomationEnabled && !_targetSelectionBusy;
        SelectGameExeButton.IsEnabled = normalAutomationEnabled && !_targetSelectionBusy;
        TargetFileNameText.Text = "当前主进程：" + target.DisplayName;
        TargetFilePathText.Text = target.IsCustom ? target.ExecutablePath : "三角洲默认主进程";
        var blocker = ServiceLocator.GameTarget.GetSwitchBlocker();
        TargetModeStatusText.Text = blocker.Length == 0 ? ServiceLocator.GameTarget.Status : blocker;
        _updatingTargetControls = false;
        UpdateGamePathStatus();
    }
    private Task<string?> PickGameExeAsync()
    {
        if (App.MainWindow is null) throw new InvalidOperationException("主窗口尚未就绪，请重新进入设置后选择。");
        var owner = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        Log.Info("自定义游戏目标：显示原生 EXE 选择窗口。");
        return Task.FromResult(Native.FolderPickerDialog.PickExecutable(owner, AppSettingsStore.Read().CustomGameExecutablePath));
    }
    private async void CustomGameMode_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingTargetControls || _targetSelectionBusy) return;
        await ChangeGameTargetAsync(CustomGameModeToggle.IsOn, false);
    }
    private async void SelectGameExe_Click(object sender, RoutedEventArgs e) => await ChangeGameTargetAsync(true, true);
    private async Task ChangeGameTargetAsync(bool enabled, bool choose)
    {
        if (OfflineModeGuard.BlocksNormalAutomation)
        {
            TargetModeStatusText.Text = "脱机模式或恢复流程中，暂不能切换游戏目标。";
            RefreshTargetControls();
            return;
        }
        if (_targetSelectionBusy) return;
        _targetSelectionBusy = true;
        CustomGameModeToggle.IsEnabled = SelectGameExeButton.IsEnabled = false;
        string? message = null;
        string? navigateAfter = null;
        try
        {
            var blocker = ServiceLocator.GameTarget.GetSwitchBlocker();
            if (blocker.Length > 0)
            {
                message = blocker;
                Log.Info("自定义游戏目标：选择被阻止；" + blocker);
                var cpuRelated = blocker.Contains("CCD", StringComparison.OrdinalIgnoreCase) || blocker.Contains("CPU", StringComparison.OrdinalIgnoreCase);
                var dialog = new ContentDialog
                {
                    Title = "暂不能更换游戏目标",
                    Content = new TextBlock { Text = blocker, TextWrapping = TextWrapping.Wrap },
                    PrimaryButtonText = cpuRelated ? "CPU 实验室" : "帧格",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot,
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary) navigateAfter = cpuRelated ? "lab" : "frame";
                return;
            }
            var existing = AppSettingsStore.Read().CustomGameExecutablePath;
            string? path = null;
            if (choose || (enabled && !System.IO.File.Exists(existing)))
            {
                path = await PickGameExeAsync();
                if (path is null) return;
            }
            var result = await ServiceLocator.GameTarget.ChangeAsync(enabled, path);
            message = result.Message;
        }
        catch (Exception ex)
        {
            message = "目标选择失败：" + ex.Message;
            Log.Error(message, ex);
            try
            {
                await new ContentDialog { Title = "目标选择失败", Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    CloseButtonText = "知道了", XamlRoot = XamlRoot }.ShowAsync();
            }
            catch { /* Another dialog or window shutdown must not crash the settings page. */ }
        }
        finally
        {
            _targetSelectionBusy = false;
            RefreshTargetControls();
            if (message is not null) TargetModeStatusText.Text = message;
            if (navigateAfter is not null) App.MainWindow?.NavigateByTag(navigateAfter);
        }
    }

    private static string DescribeBackground(string? current) => current is null
        ? "当前未使用背景图（Mica 云母背景）"
        : "当前背景：" + current;

    // ---------------- 主题色 ----------------

    private void BuildAccentSwatches()
    {
        var current = AppSettingsStore.Read().AccentColor ?? "";
        AccentSwatchList.Items.Clear();
        foreach (var (name, hex) in AccentPresets)
        {
            var color = App.TryParseColor(hex, out var c) ? c : App.DefaultAccent;
            AccentSwatchList.Items.Add(new AccentOption
            {
                Name = name,
                Hex = hex,
                Color = color,
                IsSelected = hex.Equals(current, StringComparison.OrdinalIgnoreCase),
            });
        }
    }

    private void AccentSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex } && App.TryParseColor(hex, out var color))
        {
            ApplyAccent(color);
        }
    }

    private async void CustomAccentButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ColorPicker
        {
            IsAlphaEnabled = false,
            IsColorSliderVisible = true,
            IsColorPreviewVisible = true,
            MinWidth = 260,
            Color = App.TryParseColor(AppSettingsStore.Read().AccentColor, out var current)
                ? current
                : App.DefaultAccent,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "自定义主题色",
            Content = picker,
            PrimaryButtonText = "应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ApplyAccent(picker.Color);
        }
    }

    private void ApplyAccent(Windows.UI.Color color)
    {
        App.SetAccentColor(color);

        // 色板勾选状态刷新 + 延迟重建当前页面（让 ToggleSwitch 等默认控件取到新强调色）
        BuildAccentSwatches();
        DispatcherQueue.TryEnqueue(() => App.MainWindow?.RefreshCurrentPage());
    }

    // ---------------- 三角洲目录识别 ----------------

    private void PickGameFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (OfflineModeGuard.BlocksNormalAutomation) { GamePathStatusText.Text = "脱机模式或恢复流程中，游戏目录不可更改。"; return; }
        if (ServiceLocator.GameTarget.IsCustom) { GamePathStatusText.Text = GameTargetService.DeltaOnlyMessage; return; }
        if (App.MainWindow is null)
        {
            return;
        }

        try
        {
            // 管理员进程里 WinUI FolderPicker 会抛 COMException（系统限制），
            // 改用 COM IFileOpenDialog（提权/非提权均可用）
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            var folder = Native.FolderPickerDialog.Pick(handle, GamePathBox.Text?.Trim());
            if (string.IsNullOrEmpty(folder))
            {
                return;
            }

            GamePathBox.Text = folder;
        }
        catch (Exception ex)
        {
            GamePathStatusText.Text = "系统选择器不可用，请直接在上方输入框粘贴游戏目录后保存。";
            Services.Log.Error("设置：游戏目录选择器失败", ex);
        }
    }

    private void SaveGamePathButton_Click(object sender, RoutedEventArgs e)
    {
        if (OfflineModeGuard.BlocksNormalAutomation) { GamePathStatusText.Text = "脱机模式或恢复流程中，游戏目录不可更改。"; return; }
        if (ServiceLocator.GameTarget.IsCustom) { GamePathStatusText.Text = GameTargetService.DeltaOnlyMessage; return; }
        var path = GamePathBox.Text?.Trim() ?? "";
        if (path.Length > 0)
        {
            if (!DeltaForceLocator.TryGetValidGameRoot(path, out var normalizedPath))
            {
                GamePathStatusText.Text = "所选目录中未找到三角洲游戏文件，请选择游戏安装目录（包含三角洲游戏程序或 Content/Paks）。";
                return;
            }

            path = normalizedPath;
            GamePathBox.Text = normalizedPath;
        }

        AppSettingsStore.Update(s => s.GameRootOverride = path);
        UpdateGamePathStatus();
    }

    private void ClearGamePathButton_Click(object sender, RoutedEventArgs e)
    {
        if (OfflineModeGuard.BlocksNormalAutomation) { GamePathStatusText.Text = "脱机模式或恢复流程中，游戏目录不可更改。"; return; }
        if (ServiceLocator.GameTarget.IsCustom) { GamePathStatusText.Text = GameTargetService.DeltaOnlyMessage; return; }
        GamePathBox.Text = "";
        AppSettingsStore.Update(s => s.GameRootOverride = "");
        UpdateGamePathStatus();
    }

    private void UpdateGamePathStatus()
    {
        if (ServiceLocator.GameTarget.IsCustom) { GamePathStatusText.Text = GameTargetService.DeltaOnlyMessage; return; }
        var overridePath = AppSettingsStore.Read().GameRootOverride ?? "";
        var roots = DeltaForceLocator.FindRoots().ToList();

        string manualPart = string.IsNullOrWhiteSpace(overridePath)
            ? "手动指定：未设置（自动识别）"
            : $"手动指定：{overridePath}";

        string detectPart = roots.Count == 0
            ? "当前识别：未找到游戏目录"
            : "当前识别：" + string.Join("；", roots);

        GamePathStatusText.Text = manualPart + "\n" + detectPart;
    }

    // ---------------- 背景 ----------------

    private async void PickBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is null)
        {
            return;
        }

        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".webp");

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            BackgroundManager.SetCustomImage(file.Path);
            BackgroundStatusText.Text = "背景已更新：" + file.Name;
        }
        catch (Exception ex)
        {
            BackgroundStatusText.Text = "设置背景失败：" + ex.Message;
        }
    }

    private void ResetBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        BackgroundManager.ResetToDefault();
        var current = BackgroundManager.ResolveCurrent();
        BackgroundStatusText.Text = current is null
            ? "已恢复默认（Mica 云母背景）"
            : "已恢复内置背景：" + current;
    }

    private void DimSlider_ValueChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressDimSlider)
        {
            return;
        }

        BackgroundManager.SetDimOpacity(DimSlider.Value / 100);
    }

    private void OpenLogFolderButton_Click(object sender, RoutedEventArgs e)
    {
        Services.Log.OpenFolderInExplorer();
    }

    // ---------------- 软件更新 ----------------

    private void CheckUpdateButton_Click(object sender, RoutedEventArgs e) =>
        Update.CheckCommand.Execute(null);

    private void PrimaryUpdateAction_Click(object sender, RoutedEventArgs e) =>
        Update.PrimaryActionCommand.Execute(null);

    private void SkipUpdateVersion_Click(object sender, RoutedEventArgs e) =>
        Update.SkipVersionCommand.Execute(null);

    private void CancelUpdateDownload_Click(object sender, RoutedEventArgs e) =>
        Update.CancelDownloadCommand.Execute(null);

    private void OpenUpdateReleasePage_Click(object sender, RoutedEventArgs e) =>
        Update.OpenReleasePageCommand.Execute(null);

    private void OpenUpdateDownloadFolder_Click(object sender, RoutedEventArgs e) =>
        Update.OpenDownloadFolderCommand.Execute(null);

    private void OpenUpdateSourceConfig_Click(object sender, RoutedEventArgs e) =>
        Update.OpenUpdateSourceConfigCommand.Execute(null);
}
