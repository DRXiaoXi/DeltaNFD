using DeltaNFD.Services;
using DeltaNFD.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class ShaderPage : Page
{
    private bool _selectionFlowBusy;
    private async void StartManualAsd_Click(object sender, RoutedEventArgs e) => await SelectionFlowAsync(async () =>
    {
        if (!ViewModel.CanStartManualAsd) return;
        var selection = await GameInstallationPicker.PickAsync(XamlRoot, "开始手动 ASD 判定", sharedOnly: true);
        if (selection is null || selection.Roots.Length == 0) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "清理缓存并开始手动 ASD 判定？",
            Content = GameInstallationPicker.Describe(selection)
                + "将删除以上安装的 PSOCache 及当前用户系统 D3DSCache 中的缓存文件，保留目录本身。删除无法撤销，其他游戏首次启动也可能重新编译。\n\n"
                + "请完全退出三角洲并关闭其他图形应用。清理全部完成后，再进入游戏预热；预热结束点击“预热完成”。D3DSCache 中出现一个大于100MB的NVPH即按本规则判定ASD已激活。",
            PrimaryButtonText = "清理并开始", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner) != ContentDialogResult.Primary) return;
        await ViewModel.StartManualAsdAsync(GameInstallationPicker.ConfirmShared(selection));
    });
    private async void FinishManualAsd_Click(object sender, RoutedEventArgs e) => await SelectionFlowAsync(ViewModel.FinishManualAsdAsync);
    private async void CancelManualAsd_Click(object sender, RoutedEventArgs e) => await SelectionFlowAsync(ViewModel.CancelManualAsdAsync);
    private async Task SelectionFlowAsync(Func<Task> action)
    {
        if (_selectionFlowBusy) return;
        _selectionFlowBusy = true;
        try { await GameInstallationPicker.RunAsync(XamlRoot, action); }
        finally { _selectionFlowBusy = false; }
    }
    private readonly DeltaNFD.Services.Backends.PageBackendLease? _pageOwner = DeltaNFD.Services.Backends.PageBackendSessions.Manager.Current;
    public ShaderViewModel ViewModel { get; } = new();
    public string WarmupLaunchArgument => "-WarmAllPSOWhenLaunch";
    public Microsoft.UI.Xaml.Media.Brush DiagnosisBrush(ShaderDiagLevel level)
    {
        if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground));
        var light = ActualTheme == ElementTheme.Light;
        var color = level switch
        {
            ShaderDiagLevel.Normal => light ? (22, 114, 69) : (112, 218, 157),
            ShaderDiagLevel.Warning => light ? (138, 91, 0) : (246, 200, 95),
            ShaderDiagLevel.Abnormal or ShaderDiagLevel.AbnormalDriverIssue => light ? (169, 35, 35) : (255, 146, 146),
            _ => light ? (82, 96, 107) : (183, 189, 197),
        };
        return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)color.Item1, (byte)color.Item2, (byte)color.Item3));
    }

    private async void OpenVersionGuide_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is { } window) await window.ShowGameVersionGuideAsync();
    }

    private void CopyWarmupArgument_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = new Windows.ApplicationModel.DataTransfer.DataPackage();
            content.SetText(WarmupLaunchArgument);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(content);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
            WarmupCopyStatus.Text = "已复制。请按下方图示粘贴到启动器的「启动命令行」。";
        }
        catch
        {
            WarmupCopyStatus.Text = "剪贴板暂时不可用，请在上方输入框中选择并复制指令。";
        }
    }


    public ShaderPage()
    {
        InitializeComponent();

        // 首次进入页面即自动体检（驱动 + 着色器缓存），无需手动点「重新检测」
        _ = ViewModel.LoadAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await SelectionFlowAsync(RefreshSelectedAsync);
    private async Task RefreshSelectedAsync()
    {
        var selection = await GameInstallationPicker.PickAsync(XamlRoot, "刷新着色器状态");
        if (selection is null) return;
        using var scope = GameInstallationScope.Enter(selection);
        await ViewModel.LoadAsync();
    }

    private void OpenShaderRecovery_Click(object sender, RoutedEventArgs e)
        => Frame.Navigate(typeof(ShaderRecoveryPage), "shader");

    private async void ClearButton_Click(object sender, RoutedEventArgs e)
        => await SelectionFlowAsync(ClearSelectedAsync);
    private async Task ClearSelectedAsync()
    {
        var selection = await GameInstallationPicker.PickAsync(XamlRoot, "清理旧着色器");
        if (selection is null) return;
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "清除旧着色器文件？",
            Content = GameInstallationPicker.Describe(selection) + "将删除所选安装的 PSOCache 着色器缓存。\n\n"
                      + (selection.AllInstallations ? ShaderCacheCleanupPolicy.SystemCacheNotice
                          : "单目录操作仅清理本地 PSOCache，保留系统 D3DSCache 和显卡驱动共享缓存。") + "\n\n"
                      + "请先退出三角洲和其他图形应用。删除无法撤销；链接及占用等未完成项目会报告。"
                      + "清理后请进入游戏完成着色器预热，初次加载可能出现卡顿。",
            PrimaryButtonText = "清除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await confirm.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner) != ContentDialogResult.Primary)
        {
            return;
        }

        using var scope = GameInstallationScope.Enter(GameInstallationPicker.ConfirmShared(selection));
        var result = await ViewModel.ClearAsync();
        if (result is { Success: false })
        {
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "清理失败",
                Content = result.Message,
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Close,
            }.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner);
        }
        else if (result is { Success: true })
        {
            // 成功提醒：清除后必须重新预热着色器
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = result.IsPartial ? "清理部分完成" : "清理完成",
                Content = result.Message + "\n\n"
                          + "⚠ 请记得：进入游戏后重新预热着色器——\n"
                          + "请等待预热完成；初次加载可能需要重新编译着色器。",
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Primary,
            }.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner);
        }
    }

    private async void DiagnoseButton_Click(object sender, RoutedEventArgs e)
        => await SelectionFlowAsync(DiagnoseSelectedAsync);
    private async Task DiagnoseSelectedAsync()
    {
        var selection = await GameInstallationPicker.PickAsync(XamlRoot, "检测着色器");
        if (selection is null) return;
        using var scope = GameInstallationScope.Enter(selection);
        await ViewModel.DiagnoseAsync();
    }

    private async void CleanSystemButton_Click(object sender, RoutedEventArgs e)
        => await SelectionFlowAsync(CleanSystemSelectedAsync);
    private async Task CleanSystemSelectedAsync()
    {
        var selection = await GameInstallationPicker.PickAsync(XamlRoot, "清理系统着色器", sharedOnly: true);
        if (selection is null) return;
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "清理系统级着色器缓存？",
            Content = "将清空以下目录的内容（保留目录本身；存在时清理）：\n"
                      + "· %LocalAppData%\\D3DSCache\n"
                      + "· %LocalAppData%\\NVIDIA\\DXCache / GLCache\n"
                      + "· %LocalAppData%\\AMD\\DxCache / DxcCache / GLCache\n"
                      + "· %LocalAppData%\\Intel\\ShaderCache\n\n"
                      + "清理后所有游戏首次启动都会重新编译着色器，首局可能出现卡顿，属正常现象。"
                      + "链接/重解析项会跳过；被占用或无法删除的项目会逐项报告。确定继续？",
            PrimaryButtonText = "清理",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await confirm.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner) != ContentDialogResult.Primary)
        {
            return;
        }

        using var scope = GameInstallationScope.Enter(GameInstallationPicker.ConfirmShared(selection));
        var result = await ViewModel.CleanSystemShadersAsync();
        if (result is null)
        {
            return;
        }

        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = result.IsPartial ? "清理部分完成" : result.Success ? "清理完成" : "清理失败",
            Content = result.Message,
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
        }.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner);
    }
}
