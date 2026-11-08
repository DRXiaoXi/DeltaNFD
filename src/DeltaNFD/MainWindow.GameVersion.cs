using DeltaNFD.Services;
using DeltaNFD.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD;

public sealed partial class MainWindow
{
    private readonly GameVersionTracker _gameVersions = new(Path.Combine(AppDataPaths.Root, "game-versions.json"));
    private GameVersionCheckResult _gameVersionResult = new([], []);
    private DispatcherQueueTimer? _gameVersionTimer;
    private bool _gameVersionChecking;
    private bool _gameVersionClosed;
    private long _gameVersionLastCheck = long.MinValue;
    private ContentDialog? _gameVersionDialog;
    private bool _gameVersionGuideOpening;

    private void InitializeGameVersionWatch()
    {
        _gameVersionTimer = DispatcherQueue.CreateTimer();
        _gameVersionTimer.Interval = TimeSpan.FromSeconds(60);
        _gameVersionTimer.Tick += GameVersionTimer_Tick;
        _gameVersionTimer.Start();
        Activated += GameVersionWindow_Activated;
        Closed += (_, _) =>
        {
            _gameVersionClosed = true;
            _gameVersionTimer.Stop();
            _gameVersionTimer.Tick -= GameVersionTimer_Tick;
            Activated -= GameVersionWindow_Activated;
            _gameVersionDialog?.Hide();
        };
    }

    private async void GameVersionTimer_Tick(DispatcherQueueTimer sender, object args) => await CheckGameVersionsAsync();
    private async void GameVersionWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated) await CheckGameVersionsAsync();
    }

    private async Task CheckGameVersionsAsync(bool force = false)
    {
        if (_gameVersionClosed || _gameVersionChecking || !StartupGameDirectoryCheckCompleted || OfflineModeGuard.BlocksNormalAutomation) return;
        var tick = Environment.TickCount64;
        if (!force && _gameVersionLastCheck != long.MinValue && tick - _gameVersionLastCheck < 10000) return;
        _gameVersionChecking = true;
        _gameVersionLastCheck = tick;
        try
        {
            var result = await Task.Run(() => _gameVersions.Refresh(DeltaForceLocator.FindRoots()));
            if (!_gameVersionClosed) _gameVersionResult = result;
        }
        catch (Exception ex)
        {
            Log.Error("游戏版本记录未能读取或保存，保留原记录", ex);
            if (!_gameVersionClosed) _gameVersionResult = new(_gameVersionResult.Entries,
                ["版本记录未能读取或保存，请检查工具数据目录后重试。" + ex.Message]);
        }
        finally
        {
            _gameVersionChecking = false;
            if (!_gameVersionClosed) UpdateGameVersionNotice();
        }
    }

    private void UpdateGameVersionNotice()
    {
        if (GameVersionNotice is null) return;
        var pending = _gameVersionResult.Entries.Count(e => e.MaintenancePending);
        GameVersionNotice.IsOpen = !_gameVersionClosed && !OfflineModeGuard.BlocksNormalAutomation && !ServiceLocator.GameTarget.IsCustom &&
            (pending > 0 || _gameVersionResult.Errors.Count > 0);
        GameVersionNotice.Title = pending > 0 ? "三角洲版本已变化，建议完成更新维护" : "三角洲版本检测待确认";
        GameVersionNotice.Message = pending > 0
            ? $"{pending} 份安装检测到变化。请在更新完成并退出游戏后，清理 PSOCache、清理并修复 ACE，再完整预热。"
                + (_gameVersionResult.Errors.Count > 0 ? " 部分版本暂未读全，原提醒保留。" : "")
            : "暂未确认当前游戏版本，原记录保留。可打开指引查看详情，稍后会重新检测。";
    }

    private async void OpenGameVersionGuide_Click(object sender, RoutedEventArgs e) => await ShowGameVersionGuideAsync();

    internal async Task ShowGameVersionGuideAsync()
    {
        if (_gameVersionGuideOpening || _gameVersionClosed) return;
        _gameVersionGuideOpening = true;
        try
        {
            await CheckGameVersionsAsync(force: true);
            if (_gameVersionClosed) return;
            var displayed = _gameVersionResult;
            var pending = displayed.Entries.Where(e => e.MaintenancePending).ToArray();
            var expected = pending.Select(e => (e.Current.GameRoot, e.ChangeToken)).ToArray();
            var panel = new StackPanel { Spacing = 12 };
            void Text(string value) => panel.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            Text("工具按每份安装记录启动器版本与构建号，变化时提醒维护。资源配置仅供查看，其变化不会单独触发更新提醒。检测只读取文件，不执行维护操作。");
            foreach (var entry in displayed.Entries)
                Text(entry.Current.GameRoot + "\n" + (entry.MaintenancePending
                    ? entry.PreviousVersion + "\n→ " + entry.Current.DisplayVersion
                    : "当前记录：" + entry.Current.DisplayVersion));
            if (displayed.Entries.Any(e => e.Current.LauncherVersion.Length == 0))
                Text("部分安装只有资源配置记录，尚无可识别的启动器构建；工具不会仅凭资源配置判断这些安装发生更新。");
            foreach (var error in displayed.Errors) Text("待确认：" + error);
            if (displayed.Entries.Count == 0) Text("尚未记录可识别的游戏版本。请先在设置中登记安装目录，等待启动器更新完成后重试。");
            Text("1. 等待启动器完成更新，完全退出三角洲及反作弊守卫，再到「着色器」清理 PSOCache。清理将删除缓存，原页面会再次确认。");
            string route = "";
            var shader = new Button { Content = "打开着色器维护", IsEnabled = !ServiceLocator.GameTarget.IsCustom && CanEnterGamePage("shader") };
            shader.Click += (_, _) => { route = "shader"; _gameVersionDialog?.Hide(); };
            panel.Children.Add(shader);
            Text("2. 到「ACE相关」检查并手动确认清理，再用 WeGame / 游戏启动器执行普通「修复」，补齐 ACE 组件；不要使用深度修复。如清理结果要求重启，先重启再修复。共享 ACE 清理可能影响其他游戏，请按原页面说明处理。");
            var ace = new Button { Content = "打开ACE相关", IsEnabled = !ServiceLocator.GameTarget.IsCustom && CanEnterGamePage("ace") };
            ace.Click += (_, _) => { route = "ace"; _gameVersionDialog?.Hide(); };
            panel.Children.Add(ace);
            Text("3. 在启动器「启动命令行」加入 -WarmAllPSOWhenLaunch（着色器页可复制并查看图示），启动游戏，等待完整预热完成后再进入对局。");
            Text("游戏运行中或状态未确认时，维护入口暂不可用。工具无法验证启动器修复及游戏内预热是否完成；打开页面、部分成功或关闭窗口均不会自动消除提醒。");
            var confirmed = new CheckBox { Content = new TextBlock { Text = "我已完成上述维护（自行确认）", TextWrapping = TextWrapping.Wrap } };
            if (pending.Length > 0) panel.Children.Add(confirmed);
            var dialog = new ContentDialog
            {
                XamlRoot = ContentFrame.XamlRoot,
                Title = pending.Length > 0 ? "三角洲更新后的维护指引" : "游戏版本记录与维护指引",
                Content = new ScrollViewer { Content = panel, MaxHeight = 420, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
                PrimaryButtonText = pending.Length > 0 ? "关闭本次版本提醒" : "",
                IsPrimaryButtonEnabled = false,
                CloseButtonText = "稍后处理",
                DefaultButton = ContentDialogButton.Close,
            };
            confirmed.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
            confirmed.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;
            _gameVersionDialog = dialog;
            var result = await dialog.ShowWithBackendRecoveryAsync();
            if (_gameVersionClosed) return;
            if (result == ContentDialogResult.Primary && confirmed.IsChecked == true)
            {
                var acknowledged = await Task.Run(() => _gameVersions.Acknowledge(expected));
                await CheckGameVersionsAsync(force: true);
                if (acknowledged != expected.Length)
                    ReportPageOperationResult("部分提醒仍保留", "游戏版本可能再次变化或暂不可读，请重新检查维护指引。");
            }
            if (route.Length > 0)
            {
                if (!ServiceLocator.GameTarget.IsCustom && CanEnterGamePage(route)) NavigateByTag(route);
                else ReportPageOperationResult("维护入口暂不可用", "请切回三角洲目标并完全退出游戏，确认状态后再操作。");
            }
        }
        catch (Exception ex)
        {
            Log.Error("游戏版本维护指引未完成", ex);
            if (!_gameVersionClosed) ReportPageOperationResult("版本提醒保留", ex.Message);
        }
        finally { _gameVersionDialog = null; _gameVersionGuideOpening = false; }
    }
}
