using DeltaNFD.ViewModels;
using DeltaNFD.Services;
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
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
        ActualThemeChanged += (_, _) => Bindings.Update();
    }

    private bool _attached;
    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_attached) return;
        _attached = true;
        ServiceLocator.GameTarget.Changed += OnTargetChanged;
        ViewModel.RefreshTargetDisplay();
        ViewModel.StartOptimizationScan();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _attached = false;
        ServiceLocator.GameTarget.Changed -= OnTargetChanged;
        ViewModel.DetachScanListener();
    }

    private void OnTargetChanged(GameTarget target) => DispatcherQueue.TryEnqueue(() =>
    { if (_attached) ViewModel.RefreshTargetDisplay(); });

    public string StateLabel(DashboardHealthState state) => DashboardHealthPresentation.Label(state);

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
    public Brush StatusBrush(DashboardHealthState state)
    {
        if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
            return new SolidColorBrush(new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground));
        var light = ActualTheme == ElementTheme.Light;
        var color = state switch
        {
            DashboardHealthState.Normal => light ? (22, 114, 69) : (112, 218, 157),
            DashboardHealthState.Information => light ? (21, 101, 170) : (126, 192, 255),
            DashboardHealthState.Warning => light ? (138, 91, 0) : (246, 200, 95),
            DashboardHealthState.Abnormal or DashboardHealthState.Failed => light ? (169, 35, 35) : (255, 146, 146),
            _ => light ? (82, 96, 107) : (183, 189, 197),
        };
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)color.Item1, (byte)color.Item2, (byte)color.Item3));
    }

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
