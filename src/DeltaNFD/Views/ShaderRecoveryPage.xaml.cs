using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace DeltaNFD.Views;

public sealed partial class ShaderRecoveryPage : Page
{
    private readonly DeltaNFD.Services.Backends.PageBackendLease? _pageOwner = DeltaNFD.Services.Backends.PageBackendSessions.Manager.Current;
    private readonly INvProfileService _nv = ServiceLocator.NvProfile;
    private readonly BackendCaller _system = new(BackendModules.System, PageBackendSessions.Manager.Current);
    private bool _busy, _ready, _canClean;
    private string _returnTag = "nvsettings";

    public ShaderRecoveryPage()
    {
        InitializeComponent();
        GlobalSize.ItemsSource = GameSize.ItemsSource = NvShaderCacheChoices.Labels;
        GlobalSize.SelectedIndex = GameSize.SelectedIndex = 0;
        Loaded += async (_, _) => await RunAsync(RefreshAsync);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _returnTag = e.Parameter switch { "shader" => "shader", "dashboard" => "dashboard", _ => "nvsettings" };
    }

    private void SyncButtons()
    {
        RefreshButton.IsEnabled = VerifyButton.IsEnabled = !_busy;
        ApplyGlobalButton.IsEnabled = ApplyGameButton.IsEnabled = RestoreButton.IsEnabled = !_busy && _ready;
        CleanButton.IsEnabled = !_busy && _canClean;
        GlobalSize.IsEnabled = GameSize.IsEnabled = !_busy && _ready;
        GameCacheChoice.IsEnabled = NvidiaCacheChoice.IsEnabled = !_busy;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true; SyncButtons();
        try { await action(); }
        catch (Exception ex) { StatusBar.Severity = InfoBarSeverity.Error; StatusBar.Title = "操作未完成"; StatusBar.Message = ex.Message; }
        finally { _busy = false; SyncButtons(); }
    }

    private async Task RefreshAsync()
    {
        _ready = _canClean = false;
        var env = await _nv.GetEnvironmentAsync();
        var game = await ServiceLocator.Shader.GetStatusAsync();
        _ready = env.IsNvidia && env.NvApiAvailable && !game.IsGameRunning;
        // 清理不依赖 N 卡：AMD/Intel 也可清理游戏 PSOCache 与 D3DSCache；缓存大小设置仍由 _ready 控制。
        _canClean = !game.IsGameRunning;
        CurrentText.Text = env.ShaderCache?.Details ?? "后端未返回缓存大小，无法确认。";
        StatusBar.Severity = env.ShaderCache?.HasNonDefault == true ? InfoBarSeverity.Error
            : env.ShaderCache?.Complete != true || game.IsGameRunning || env.DrsFilesLocked ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        StatusBar.Title = game.IsGameRunning ? "请先完全退出三角洲" : env.ShaderCache?.HasNonDefault == true ? "缓存大小非默认，请恢复默认" : "缓存设置检测";
        StatusBar.Message = (env.ShaderCache?.Advice ?? "无法确认缓存大小，请重新检测。")
            + (env.DrsFilesLocked ? "\n数据库只读锁定：请先返回 N 卡设置页解除只读，再应用大小。" : "");
        if (env.DrsFilesLocked) _ready = false;
    }

    private async Task<bool> ConfirmAsync(string title, string message, string button) =>
        await new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = message, PrimaryButtonText = button,
            CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close }.ShowWithBackendRecoveryAsync(pageOwner: _pageOwner) == ContentDialogResult.Primary;

    private async Task ApplyAsync(string scope, ComboBox combo)
    {
        var index = combo.SelectedIndex;
        if (index < 0 || index >= NvShaderCacheChoices.Values.Length) throw new InvalidOperationException("请选择缓存大小。");
        var value = NvShaderCacheChoices.Values[index];
        if (!await ConfirmAsync("应用着色器缓存大小？", (scope == "global" ? "将修改全局及基础配置，会影响其他游戏。" : "仅修改三角洲程序配置。")
            + "\n选择：" + NvShaderCacheChoices.Labels[index]
            + (value == 0 ? "\n暂时关闭后，请清理缓存并恢复驱动默认，再进入游戏预热。" : value is not null ? "\n非默认大小有三角洲抽帧反馈，建议选择驱动默认。" : "")
            + "\n只修改缓存大小，不重置其他驱动设置；操作前原值会记录到工具日志。", "应用")) return;
        var result = await _nv.SetShaderCacheSizeAsync(scope, value);
        await RefreshAsync();
        CleanupText.Text = result.Message;
        if (!result.Success) { StatusBar.Severity = InfoBarSeverity.Error; StatusBar.Title = "设置未确认生效"; StatusBar.Message = result.Message; }
    }

    private async void ApplyGlobal_Click(object sender, RoutedEventArgs e) => await RunAsync(() => ApplyAsync("global", GlobalSize));
    private async void ApplyGame_Click(object sender, RoutedEventArgs e) => await RunAsync(() => ApplyAsync("game", GameSize));
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(RefreshAsync);
    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Frame.Navigate(_returnTag switch
        {
            "shader" => typeof(ShaderPage),
            "dashboard" => typeof(DashboardPage),
            _ => typeof(NvSettingsPage),
        });
    }
    private void Size_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DisabledWarning is not null) DisabledWarning.IsOpen = GlobalSize.SelectedIndex == 1 || GameSize.SelectedIndex == 1;
    }
    private async void Restore_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!await ConfirmAsync("恢复缓存大小默认？", "仅恢复全局、基础与三角洲的着色器缓存大小，不重置其他驱动选项。全局变化会影响其他游戏。缓存不会在此步删除。", "恢复默认")) return;
        var global = await _nv.SetShaderCacheSizeAsync("global", null);
        var game = global.Success ? await _nv.SetShaderCacheSizeAsync("game", null) : OperationResult.Fail("全局未确认恢复，未继续修改三角洲。");
        await RefreshAsync();
        CleanupText.Text = "全局：" + global.Message + "\n三角洲：" + game.Message;
        if (global.Success && game.Success) GlobalSize.SelectedIndex = GameSize.SelectedIndex = 0;
        else { StatusBar.Severity = InfoBarSeverity.Error; StatusBar.Title = "默认恢复未全部完成"; StatusBar.Message = CleanupText.Text; }
    });
    private async void Clean_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var game = GameCacheChoice.IsChecked == true; var nvidia = NvidiaCacheChoice.IsChecked == true;
        if (!game && !nvidia) throw new InvalidOperationException("请至少选择一种缓存。");
        if (!await ConfirmAsync("删除所选旧缓存？", (game ? "· 已检测到的三角洲安装目录内 PSOCache\n" : "")
            + (nvidia ? "· 当前用户 NVIDIA DXCache 与 Windows D3DSCache，其他游戏也将重新编译\n" : "")
            + "请退出三角洲和其他图形应用。删除无法撤销；占用或链接项会跳过并报告。清理后恢复驱动默认，再进入游戏重新预热。", "删除缓存")) return;
        var response = await _system.InvokeAsync(BackendCommands.SystemShaderRecoveryClean,
            new ShaderRecoveryCleanArgs { GameCache = game, NvidiaCache = nvidia }, null, CancellationToken.None);
        var result = response.Data is { } data ? BackendJson.Deserialize<OperationResult>(data)
            : OperationResult.Fail(response.Message);
        CleanupText.Text = result.Message;
        StatusBar.Title = result.Success ? "所选缓存已处理，请恢复默认并重新预热" : "部分缓存未完成，请核对后重试";
        StatusBar.Severity = result.Success ? InfoBarSeverity.Informational : InfoBarSeverity.Warning;
        StatusBar.Message = result.Message;
    });
    private async void Verify_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        await RefreshAsync();
        var report = (await _nv.GetEnvironmentAsync()).ShaderCache;
        WarmupText.Text = report?.Complete == true && !report.HasNonDefault
            ? "缓存大小检测通过。现在可以手动启动游戏，等待着色器编译结束，并在训练或非排位场景逐步加载地图。首次加载可能卡顿；工具不能检测游戏内预热是否完成。"
            : "尚不能确认全局和三角洲都已恢复默认。请先恢复默认并重新检测；不要在缓存仍关闭时预热。";
    });
}
