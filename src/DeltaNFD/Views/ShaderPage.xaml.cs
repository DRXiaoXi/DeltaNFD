using DeltaNFD.Services;
using DeltaNFD.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class ShaderPage : Page
{
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
    {
        await ViewModel.LoadAsync();
    }

    private void OpenShaderRecovery_Click(object sender, RoutedEventArgs e)
        => Frame.Navigate(typeof(ShaderRecoveryPage), "shader");

    private async void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "清除旧着色器文件？",
            Content = "将删除三角洲的 PSOCache 着色器缓存。\n\n"
                      + ShaderCacheCleanupPolicy.SystemCacheNotice + "\n\n"
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
    {
        await ViewModel.DiagnoseAsync();
    }

    private async void CleanSystemButton_Click(object sender, RoutedEventArgs e)
    {
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
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await confirm.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner) != ContentDialogResult.Primary)
        {
            return;
        }

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
