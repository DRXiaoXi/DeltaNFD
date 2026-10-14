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
    private async void ReviewBackendOperations_Click(object sender, RoutedEventArgs e)
    {
        try { await BackendReviewFlow.RunAsync(XamlRoot); }
        catch (Exception ex) { Log.Error("显示后端核对窗口失败，证据保留", ex); }
    }
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
    private bool _legacyRecoveryBusy;

    public SettingsPage()
    {
        InitializeComponent();
        RefreshLegacyRecoveryControls();

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
            return await dialog.ShowWithBackendRecoveryAsync() == ContentDialogResult.Primary;
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
            RefreshLegacyRecoveryControls();
            RefreshFsoStatus();

        };
        Unloaded += (_, _) =>
        {
            ServiceLocator.GameTarget.Changed -= OnGameTargetChanged;
            BackgroundManager.Changed -= OnBackgroundChanged;
            Update.Detach();
        };

        BuildAccentSwatches();

        // 三角洲安装目录：回显旧手动指定值 + 当前识别列表
        var overridePath = AppSettingsStore.Read().GameRootOverride;
        GamePathBox.Text = overridePath ?? "";
        RefreshGameRootsList();
    }

    private void RefreshLegacyRecoveryControls()
    {
        var pending = OfflineModeRetirement.NeedsRecovery;
        LegacyRecoveryCard.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        LegacyRecoveryStatusText.Text = OfflineModeRetirement.LastError.Length > 0 ? OfflineModeRetirement.LastError
            : "旧版本仍有助手或 CPU 集合恢复记录。恢复完成前暂停自动功能，原始记录会保留。";
        LegacyRecoveryButton.IsEnabled = !_legacyRecoveryBusy;
        ViewModel.RefreshAutomationControls();
        SetTargetControlsEnabled(!pending && !_legacyRecoveryBusy);
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
        AddGamePathButton.IsEnabled = enabled && !target.IsCustom && !_targetSelectionBusy;
        SearchGameFoldersButton.IsEnabled = enabled && !target.IsCustom && !_targetSelectionBusy;
        FullScanGameFoldersButton.IsEnabled = enabled && !target.IsCustom && !_targetSelectionBusy;
        ScopedScanGameFoldersButton.IsEnabled = enabled && !target.IsCustom && !_targetSelectionBusy;
    }

    private async void LegacyRecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_legacyRecoveryBusy) return;
        _legacyRecoveryBusy = true;
        RefreshLegacyRecoveryControls();
        var result = await OfflineModeRetirement.RecoverAsync();
        _legacyRecoveryBusy = false;
        RefreshLegacyRecoveryControls();
        if (result.Success) await Services.Backends.BackendProcmanService.Instance.ActivateSavedRulesAsync();
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
        AddGamePathButton.IsEnabled = normalAutomationEnabled && !target.IsCustom;
        SearchGameFoldersButton.IsEnabled = normalAutomationEnabled && !target.IsCustom;
        FullScanGameFoldersButton.IsEnabled = normalAutomationEnabled && !target.IsCustom;
        ScopedScanGameFoldersButton.IsEnabled = normalAutomationEnabled && !target.IsCustom;
        CustomGameModeToggle.IsOn = target.IsCustom;
        CustomGameModeToggle.IsEnabled = normalAutomationEnabled && !_targetSelectionBusy;
        SelectGameExeButton.IsEnabled = normalAutomationEnabled && !_targetSelectionBusy;
        TargetFileNameText.Text = "当前主进程：" + target.DisplayName;
        TargetFilePathText.Text = target.IsCustom ? target.ExecutablePath : "三角洲默认主进程";
        var blocker = ServiceLocator.GameTarget.GetSwitchBlocker();
        TargetModeStatusText.Text = blocker.Length == 0 ? ServiceLocator.GameTarget.Status : blocker;
        _updatingTargetControls = false;
        RefreshGameRootsList();
        RefreshFsoStatus();
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
            TargetModeStatusText.Text = "旧版设置恢复完成前，暂不能切换游戏目标。";
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
                if (await dialog.ShowWithBackendRecoveryAsync() == ContentDialogResult.Primary) navigateAfter = cpuRelated ? "lab" : "frame";
                return;
            }
            var existing = AppSettingsStore.Read().CustomGameExecutablePath;
            string? path = null;
            if (choose || (enabled && !System.IO.File.Exists(existing)))
            {
                path = await PickGameExeAsync();
                if (path is null) return;
            }
            // 前后端分离 C5：目标切换前排空官方后端，未确认退出时阻止切换。
            using var backendGate = Services.Backends.BackendOperationGate.BlockNewOperations();
            var backendFailures = await Services.Backends.BackendLifecycle.PrepareHandoverAsync("目标切换");
            if (backendFailures.Count > 0)
            {
                var backendBlocker = Services.Backends.BackendLifecycle.DescribeBlocker(backendFailures, "目标切换");
                Log.Warn(backendBlocker);
                await new ContentDialog
                {
                    Title = "暂不能更换游戏目标",
                    Content = new TextBlock { Text = backendBlocker, TextWrapping = TextWrapping.Wrap },
                    CloseButtonText = "知道了",
                    XamlRoot = XamlRoot,
                }.ShowWithBackendRecoveryAsync();
                return;
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
                    CloseButtonText = "知道了", XamlRoot = XamlRoot }.ShowWithBackendRecoveryAsync();
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

        if (await dialog.ShowWithBackendRecoveryAsync() == ContentDialogResult.Primary)
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

    // ---------------- 三角洲安装目录管理（多安装） ----------------

    private sealed class GameRootItem
    {
        public string Path { get; set; } = "";
        public string Source { get; set; } = "";
        public bool CanRemove { get; set; }
    }

    private void RefreshGameRootsList()
    {
        if (ServiceLocator.GameTarget.IsCustom)
        {
            GamePathStatusText.Text = GameTargetService.DeltaOnlyMessage;
            GameRootsList.ItemsSource = null;
            return;
        }

        var registered = new HashSet<string>(AppSettingsStore.Read().GameInstallRoots ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var roots = DeltaForceLocator.FindRoots().ToList();
        var items = roots.Select(r => new GameRootItem
        {
            Path = r,
            Source = registered.Contains(r) ? "用户登记" : "自动识别",
            CanRemove = registered.Contains(r),
        }).ToList();
        GameRootsList.ItemsSource = items;

        var overridePath = AppSettingsStore.Read().GameRootOverride ?? "";
        GamePathStatusText.Text = (string.IsNullOrWhiteSpace(overridePath) ? "旧手动指定：未设置" : "旧手动指定：" + overridePath)
            + "\n当前识别 " + roots.Count + " 个安装目录";
    }

    private void PickGameFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (OfflineModeGuard.BlocksNormalAutomation) { GamePathStatusText.Text = "旧版设置恢复完成前，游戏目录不可更改。"; return; }
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
            GamePathStatusText.Text = "系统选择器不可用，请直接在上方输入框粘贴游戏目录后点添加。";
            Services.Log.Error("设置：游戏目录选择器失败", ex);
        }
    }

    private void AddGamePathButton_Click(object sender, RoutedEventArgs e)
    {
        if (OfflineModeGuard.BlocksNormalAutomation) { GamePathStatusText.Text = "旧版设置恢复完成前，游戏目录不可更改。"; return; }
        if (ServiceLocator.GameTarget.IsCustom) { GamePathStatusText.Text = GameTargetService.DeltaOnlyMessage; return; }
        var path = GamePathBox.Text?.Trim() ?? "";
        if (path.Length == 0) { GamePathStatusText.Text = "请先输入或浏览选择游戏目录。"; return; }
        if (!DeltaForceLocator.TryGetValidGameRoot(path, out var normalizedPath))
        {
            GamePathStatusText.Text = "所选目录中未找到三角洲游戏文件，请选择游戏安装目录（包含三角洲游戏程序或 Content/Paks）。";
            return;
        }
        if (!DeltaForceLocator.AddRegisteredRoot(normalizedPath))
        {
            GamePathStatusText.Text = "登记失败：目录无效或已存在。";
            return;
        }
        GamePathBox.Text = "";
        RefreshGameRootsList();
    }

    private void ClearGamePathButton_Click(object sender, RoutedEventArgs e)
    {
        if (OfflineModeGuard.BlocksNormalAutomation) { GamePathStatusText.Text = "旧版设置恢复完成前，游戏目录不可更改。"; return; }
        if (ServiceLocator.GameTarget.IsCustom) { GamePathStatusText.Text = GameTargetService.DeltaOnlyMessage; return; }
        GamePathBox.Text = "";
        AppSettingsStore.Update(s => s.GameRootOverride = "");
        RefreshGameRootsList();
    }

    private void RemoveGameRoot_Click(object sender, RoutedEventArgs e)
    {
        if (OfflineModeGuard.BlocksNormalAutomation) { GamePathStatusText.Text = "旧版设置恢复完成前，游戏目录不可更改。"; return; }
        if (ServiceLocator.GameTarget.IsCustom) { GamePathStatusText.Text = GameTargetService.DeltaOnlyMessage; return; }
        if (sender is not Button { Tag: string path }) return;
        DeltaForceLocator.RemoveRegisteredRoot(path);
        RefreshGameRootsList();
    }

    private void SearchGameFoldersButton_Click(object sender, RoutedEventArgs e) =>
        _ = RunGameFolderSearchAsync(null);

    private void ScopedScanGameFoldersButton_Click(object sender, RoutedEventArgs e)
    {
        if (OfflineModeGuard.BlocksNormalAutomation) { GamePathStatusText.Text = "旧版设置恢复完成前，游戏目录不可更改。"; return; }
        if (ServiceLocator.GameTarget.IsCustom) { GamePathStatusText.Text = GameTargetService.DeltaOnlyMessage; return; }
        if (App.MainWindow is null) return;
        try
        {
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            var root = Native.FolderPickerDialog.Pick(handle, null);
            if (!string.IsNullOrWhiteSpace(root)) _ = RunGameFolderSearchAsync(root);
        }
        catch (Exception ex)
        {
            GamePathStatusText.Text = "无法打开目录选择器：" + ex.Message;
        }
    }

    private void FullScanGameFoldersButton_Click(object sender, RoutedEventArgs e) =>
        _ = RunGameFolderSearchAsync(null);

    private async Task RunGameFolderSearchAsync(string? scopedRoot)
    {
        if (OfflineModeGuard.BlocksNormalAutomation) { GamePathStatusText.Text = "旧版设置恢复完成前，游戏目录不可更改。"; return; }
        if (ServiceLocator.GameTarget.IsCustom) { GamePathStatusText.Text = GameTargetService.DeltaOnlyMessage; return; }

        var cts = new CancellationTokenSource();
        var statusText = new TextBlock { Text = "准备搜索…", TextWrapping = TextWrapping.Wrap };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "正在搜索三角洲安装目录",
            Content = new StackPanel { Spacing = 8, Children = { new ProgressBar { IsIndeterminate = true }, statusText } },
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        dialog.CloseButtonClick += (_, _) => cts.Cancel();

        var results = new List<string>();
        var progress = new Progress<string>(p => statusText.Text = p);
        var searchTask = Task.Run(async () =>
        {
            var source = scopedRoot is null
                ? DeltaForceSearcher.SearchAllFixedDrivesAsync(progress, cts.Token)
                : DeltaForceSearcher.SearchDirectoryAsync(scopedRoot, progress, cts.Token);
            await foreach (var hit in source.WithCancellation(cts.Token)) results.Add(hit);
        });

        _ = dialog.ShowWithBackendRecoveryAsync();
        try { await searchTask; }
        catch (OperationCanceledException) { }
        dialog.Hide();
        if (cts.IsCancellationRequested) return;

        if (results.Count == 0)
        {
            GamePathStatusText.Text = "搜索完成，未找到三角洲安装目录。";
            return;
        }

        await MainWindow.ConfirmAndRegisterGameRootsAsync(results, XamlRoot);
        RefreshGameRootsList();
    }

    // ---------------- 三角帧本体设置 ----------------

    private bool _suppressFsoToggle;

    /// <summary>全屏优化改为对所有登记的三角洲安装同时生效；UI 开关反映总体状态。</summary>
    private void RefreshFsoStatus()
    {
        if (FsoDisableToggle is null) return;
        var exes = GameCompatibilityService.ResolveGameExePaths();
        if (exes.Count == 0)
        {
            _suppressFsoToggle = true;
            FsoDisableToggle.IsOn = false;
            FsoDisableToggle.IsEnabled = false;
            _suppressFsoToggle = false;
            FsoStatusText.Text = "未找到三角洲游戏主程序，请先在上方「三角洲安装目录」中指定游戏目录。";
            return;
        }

        var disabledCount = exes.Count(p => GameCompatibilityService.IsFullscreenOptimizationsDisabled(p));
        _suppressFsoToggle = true;
        FsoDisableToggle.IsOn = disabledCount == exes.Count;
        FsoDisableToggle.IsEnabled = true;
        _suppressFsoToggle = false;

        FsoStatusText.Text = disabledCount switch
        {
            0 => $"当前状态：{exes.Count} 个安装均未禁用全屏优化",
            var c when c == exes.Count => $"当前状态：全部 {exes.Count} 个安装已禁用全屏优化",
            _ => $"当前状态：{disabledCount}/{exes.Count} 个安装已禁用全屏优化（开启后补全禁用，关闭后全部恢复）",
        };
    }

    private void FsoDisableToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressFsoToggle) return;

        var exes = GameCompatibilityService.ResolveGameExePaths();
        if (exes.Count == 0)
        {
            RefreshFsoStatus();
            return;
        }

        var enable = FsoDisableToggle.IsOn;
        var ok = 0;
        var skip = 0;
        var fail = new List<string>();
        foreach (var exe in exes)
        {
            var result = enable
                ? GameCompatibilityService.DisableFullscreenOptimizations(exe)
                : GameCompatibilityService.RestoreFullscreenOptimizations(exe);
            if (result.Success && result.IsSkipped) skip++;
            else if (result.Success) ok++;
            else fail.Add(result.Message);
        }

        var parts = new List<string>();
        if (ok > 0) parts.Add($"成功 {ok} 个");
        if (skip > 0) parts.Add($"跳过 {skip} 个");
        if (fail.Count > 0) parts.Add($"失败 {fail.Count} 个");
        FsoStatusText.Text = (parts.Count > 0 ? string.Join("，", parts) + "。" : "")
            + (fail.Count > 0 ? string.Join("；", fail.Take(2)) : "");
        RefreshFsoStatus();
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
