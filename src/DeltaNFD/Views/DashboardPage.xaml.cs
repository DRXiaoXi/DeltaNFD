using DeltaNFD.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace DeltaNFD.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardViewModel ViewModel { get; } = new();

    public DashboardPage()
    {
        InitializeComponent();
        Unloaded += (_, _) => ViewModel.DetachScanListener();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (App.MainWindow?.StartupGameDirectoryCheckCompleted == true)
        {
            _ = ViewModel.RefreshHealthAsync();
        }
    }

    private async void RefreshHealthButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshHealthAsync();
    }

    /// <summary>优化分数颜色语义：≥80 绿色 / ≥50 黄色 / 低于 50 红色（供 x:Bind 函数绑定）。</summary>
    public Brush ScoreBrush(bool good, bool warn) => good
        ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 34, 197, 94))
        : warn
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 250, 204, 21))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 248, 113, 113));

    /// <summary>主页状态颜色：检查中/不适用为灰色，信息为蓝色，提醒为黄色，异常/失败为红色。</summary>
    public Brush StatusBrush(DashboardHealthState state) => state switch
    {
        DashboardHealthState.Normal => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 34, 197, 94)),
        DashboardHealthState.Information => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 94, 176, 255)),
        DashboardHealthState.Warning => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 250, 204, 21)),
        DashboardHealthState.Abnormal or DashboardHealthState.Failed =>
            new SolidColorBrush(Windows.UI.Color.FromArgb(255, 248, 113, 113)),
        _ => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 148, 163, 184)),
    };

    public Windows.UI.Text.FontWeight WeightFor(DashboardHealthState state) =>
        state is DashboardHealthState.Warning or DashboardHealthState.Abnormal or DashboardHealthState.Failed
            ? Microsoft.UI.Text.FontWeights.Bold
            : Microsoft.UI.Text.FontWeights.Normal;

    public Visibility ActionVisibility(DashboardHealthState state) =>
        state is DashboardHealthState.Warning or DashboardHealthState.Abnormal or DashboardHealthState.Failed
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>异常项「去处理」：按按钮 Tag 跳转到对应功能页。</summary>
    private void JumpButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
        {
            App.MainWindow?.NavigateByTag(tag);
        }
    }

    /// <summary>程序化回写总开关时置 true，防止 Toggled 处理器重复弹窗。</summary>
    private bool _suppressFrameToggle;

    private async void FrameModeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressFrameToggle)
        {
            return;
        }

        if (sender is not ToggleSwitch toggle)
        {
            return;
        }

        // 程序化回写（激活/退出成功后绑定刷新）时开关与状态一致，直接忽略
        if (toggle.IsOn == ViewModel.Frame.FrameModeEnabled)
        {
            return;
        }

        _suppressFrameToggle = true;
        try
        {
            await FrameModeFlow.RunToggleAsync(ViewModel.Frame, toggle);
        }
        finally
        {
            // 流程取消或失败：把开关拨回真实状态
            if (toggle.IsOn != ViewModel.Frame.FrameModeEnabled)
            {
                toggle.IsOn = ViewModel.Frame.FrameModeEnabled;
            }

            _suppressFrameToggle = false;
        }
    }
}
