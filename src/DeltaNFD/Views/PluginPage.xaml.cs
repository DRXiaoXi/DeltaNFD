using System.Collections.ObjectModel;
using DeltaNFD.Services;
using DeltaNFD.Services.Plugins;
using Microsoft.UI.Xaml.Media;
using DeltaNFD.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace DeltaNFD.Views;

/// <summary>插件列表行（x:Bind 数据类型，页面内声明）。</summary>
public sealed class PluginRowItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string IdLine { get; init; }
    public required string StateText { get; init; }
}

public sealed class PluginHistoryRow
{
    public required PluginLogEntry Entry { get; init; }
    public string Heading => $"{Entry.TimeUtc.LocalDateTime:MM-dd HH:mm:ss} · {PluginHistoryQuery.StatusText(Entry)} · {Entry.Phase}";
    public string Summary => $"{(Entry.PluginId.Length == 0 ? "宿主" : Entry.PluginId)} {Entry.PluginVersion} · {Entry.OperationId} {Entry.ErrorCode}";
}

/// <summary>
/// 插件管理、显式操作与只读运行历史；历史刷新不执行插件操作。
/// </summary>
public sealed partial class PluginPage : Page
{
    public PluginViewModel ViewModel { get; } = new();

    public PluginPage()
    {
        InitializeComponent();
        TrustBoundaryText.Text = ViewModel.TrustBoundaryNotice;
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    private ObservableCollection<PluginRowItem> Rows { get; } = [];

    private readonly ObservableCollection<PluginHistoryRow> _historyRows = [];
    private PluginHistorySnapshot _history = new([], "", false);
    private IReadOnlyList<PluginLogEntry> _filteredHistory = [];
    private CancellationTokenSource? _historyCancellation;
    private int _historyGeneration;
    private bool _historyActive;
    private bool _suppressHistoryFilter;
    private bool _suppressPluginSelection;

    /// <summary>共享声明式页面当前渲染的条目身份（ID+版本+哈希+状态）；未变则不重渲染，保留操作结果文本。</summary>
    private string? _renderedFormKey;

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _historyActive = true;
        RefreshAll();
        HistoryList.ItemsSource = _historyRows;
        await RefreshHistoryAsync();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _historyActive = false;
        _historyGeneration++;
        _historyCancellation?.Cancel();
        _historyCancellation = null;
        _history = new([], "", false);
        _filteredHistory = [];
        _historyRows.Clear();
        HistoryList.ItemsSource = null;
        HistoryDetailText.Text = "";
        // 释放共享页面：释放文件锁、丢弃控件引用；不停止持续插件、不撤销授权、不恢复系统设置。
        DeclarativeView.Clear();
        _renderedFormKey = null;
    }

    private async Task RefreshHistoryAsync()
    {
        _historyCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _historyCancellation = cancellation;
        var generation = ++_historyGeneration;
        HistoryRefreshButton.IsEnabled = false;
        HistoryStatusText.Text = "正在读取历史…";
        var directory = Path.Combine(ServiceLocator.Plugins.DataRoot, "diagnostics");
        try
        {
            var snapshot = await Task.Run(() =>
            {
                using var scope = PluginDiagnostics.UseDirectory(directory);
                return PluginDiagnostics.ReadSnapshot();
            }, cancellation.Token);
            if (!_historyActive || cancellation.IsCancellationRequested || generation != _historyGeneration) return;
            _history = snapshot;
            var previous = HistoryPluginFilter.SelectedItem as string;
            _suppressHistoryFilter = true;
            try
            {
                var plugins = snapshot.Entries.Select(i => i.PluginId).Concat(ViewModel.Plugins.Select(p => p.Id))
                    .Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).Prepend("全部插件").ToArray();
                HistoryPluginFilter.ItemsSource = plugins;
                HistoryPluginFilter.SelectedItem = previous is not null && plugins.Contains(previous) ? previous : plugins[0];
            }
            finally { _suppressHistoryFilter = false; }
            ApplyHistoryFilter();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_historyActive && generation == _historyGeneration)
                HistoryStatusText.Text = "历史读取未完成：" + ex.GetType().Name;
        }
        finally
        {
            if (ReferenceEquals(_historyCancellation, cancellation)) _historyCancellation = null;
            if (_historyActive && generation == _historyGeneration) HistoryRefreshButton.IsEnabled = true;
        }
    }

    private void ApplyHistoryFilter()
    {
        var plugin = HistoryPluginFilter.SelectedItem as string;
        var status = (HistoryStatusFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        _filteredHistory = PluginHistoryQuery.Filter(_history, plugin == "全部插件" ? null : plugin, status);
        _historyRows.Clear();
        HistoryDetailText.Text = "";
        LoadMoreHistory();
    }

    private void LoadMoreHistory()
    {
        foreach (var entry in PluginHistoryQuery.Page(_filteredHistory, _historyRows.Count, 50))
            _historyRows.Add(new() { Entry = entry });
        HistoryMoreButton.Visibility = _historyRows.Count < _filteredHistory.Count ? Visibility.Visible : Visibility.Collapsed;
        HistoryStatusText.Text = $"历史事件 {_historyRows.Count} / {_filteredHistory.Count}" +
            (_history.Truncated ? " · 仅保留最近 1000 条" : "") +
            (_history.Warning.Length > 0 ? "\n" + _history.Warning : "");
    }

    private void HistoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (_historyActive && !_suppressHistoryFilter) ApplyHistoryFilter(); }

    private async void HistoryRefresh_Click(object sender, RoutedEventArgs e) => await RefreshHistoryAsync();
    private void HistoryMore_Click(object sender, RoutedEventArgs e) => LoadMoreHistory();

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is not PluginHistoryRow row) { HistoryDetailText.Text = ""; return; }
        var entry = row.Entry;
        HistoryDetailText.Text = $"{entry.TimeUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss}\n" +
            $"插件：{entry.PluginId} {entry.PluginVersion}\n阶段：{entry.Phase}\n历史结果：{PluginHistoryQuery.StatusText(entry)}\n" +
            $"操作：{entry.OperationId}\n会话：{entry.SessionId}\n请求：{entry.RequestId}\n备份引用：{entry.BackupId}\n" +
            $"耗时：{(entry.ElapsedMs.HasValue ? entry.ElapsedMs + " ms" : "未记录")}\n错误类别：{entry.ErrorCode} {entry.ExceptionType}\n{entry.Detail}";
    }

    private void RefreshAll()
    {
        var selectedId = ViewModel.Selected?.Id;
        _suppressPluginSelection = true;
        try
        {
        ViewModel.Refresh();
        Rows.Clear();
        foreach (var plugin in ViewModel.Plugins)
            Rows.Add(new PluginRowItem
            {
                Id = plugin.Id,
                Name = plugin.Name,
                IdLine = $"{plugin.Id} · v{plugin.Version} · {plugin.Author}",
                StateText = plugin.StateText,
            });
        PluginList.ItemsSource = Rows;
        PluginList.SelectedItem = Rows.FirstOrDefault(p => p.Id == selectedId);
        ViewModel.Selected = ViewModel.Plugins.FirstOrDefault(p => p.Id == selectedId);
        PluginListStatusText.Text = ViewModel.StatusText;
        ImportStatusText.Text = "";
        Bindings.Update();
        }
        finally { _suppressPluginSelection = false; }
        RefreshDetail();
    }

    private void PluginList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPluginSelection) return;
        if (PluginList.SelectedItem is PluginRowItem row)
        {
            var match = ViewModel.Plugins.FirstOrDefault(p => p.Id == row.Id);
            if (match is not null) ViewModel.Selected = match;
        }
        else
        {
            ViewModel.Selected = null;
        }
        RefreshDetail();
    }

    private void RefreshDetail()
    {
        var selected = ViewModel.Selected;
        Bindings.Update();
        if (selected is null)
        {
            DeclarativeView.Clear();
            _renderedFormKey = null;
            PluginFormHost.Visibility = Visibility.Collapsed;
            return;
        }
        if (!ServiceLocator.Plugins.Index.TryGet(selected.Id, out var entry, out var error) || entry is null)
        {
            DeclarativeView.Clear();
            _renderedFormKey = null;
            PluginFormHost.Visibility = Visibility.Collapsed;
            DetailStatusText.Text = "读取插件详情失败：" + error;
            return;
        }
        // 共享声明式页面：仅在条目身份/状态变化时重渲染，避免刷新列表时清掉最近一次操作结果。
        var formKey = $"{entry.Id}|{entry.Version}|{entry.PackageSha256}|{entry.State}";
        if (_renderedFormKey != formKey)
        {
            DeclarativeView.SetEntry(entry);
            _renderedFormKey = formKey;
        }
        PluginFormHost.Visibility = DeclarativeView.PluginId is null ? Visibility.Collapsed : Visibility.Visible;
        DetailNameText.Text = $"{entry.Name} v{entry.Version}";
        DetailMetaText.Text =
            $"作者：{entry.Author}\n" +
            $"完整包 SHA256：{entry.PackageSha256}\n" +
            $"安装位置：{entry.InstallDirectory}\n" +
            $"文件数：{entry.Files.Count} · 导入时间：{entry.ImportedUtc.LocalDateTime:yyyy-MM-dd HH:mm}\n" +
            (entry.AuthorizedUtc is { } at ? $"授权时间：{at.LocalDateTime:yyyy-MM-dd HH:mm}" : "状态：未授权");
        var permissions = entry.Permissions is { Count: > 0 }
            ? string.Join("\n", entry.Permissions.Select(p => $"· {p.Id} —— {p.Purpose}"))
            : "（清单未声明权限）";
        DetailPermissionsText.Text = "权限用途：\n" + permissions;
        DetailStatusText.Text = "";
        if (!ServiceLocator.Plugins.Backups.TryListPending(entry.Id, out var pendingBackups, out var backupError))
            DetailStatusText.Text = backupError;
        else if (pendingBackups.Count > 0)
            DetailStatusText.Text = $"存在 {pendingBackups.Count} 条待恢复备份（卸载与替换已阻止；请先恢复或人工处理）。";
        var runtimeStatus = ServiceLocator.PluginRuntime.QueryStatus(entry.Id);
        if (!string.IsNullOrWhiteSpace(runtimeStatus.Detail))
            DetailStatusText.Text += (DetailStatusText.Text.Length == 0 ? "" : "\n") + runtimeStatus.Detail;
        var logError = PluginDiagnostics.GetWriteError(Path.Combine(ServiceLocator.Plugins.DataRoot, "diagnostics"));
        if (logError.Length > 0) DetailStatusText.Text += "\n" + logError;
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is null) return;
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add(".dnfdplugin");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        // 替换/升级导入前统一拦截（待做清单第 9 项）。
        var importBlocker = await PluginLifecycleGuard.PrepareHandoverAsync(
            ServiceLocator.Plugins, ServiceLocator.PluginRuntime, "插件替换导入");
        if (importBlocker.Length > 0)
        {
            ImportStatusText.Text = importBlocker;
            await ShowDialogAsync("导入被阻止", importBlocker);
            return;
        }
        var result = ViewModel.Import(file.Path);
        ImportStatusText.Text = ViewModel.StatusText;
        RefreshAll();
        if (result.Succeeded && result.Entry is { } entry)
            await ShowDialogAsync("导入完成", $"插件“{entry.Name}”已导入并保持禁用。\n启用前需完成授权确认。");
        else if (!result.Succeeded)
            await ShowDialogAsync("导入失败", result.Error);
    }

    private async void AuthorizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } selected)
        {
            DetailStatusText.Text = "请先在列表中选择插件。";
            return;
        }
        // 授权确认清单：逐条展示，必须全部确认（规范第 5 节）。
        var checklist = ViewModel.AuthorizationChecklist;
        if (checklist.Count == 0)
        {
            DetailStatusText.Text = "无法生成授权清单（读取插件信息失败）。";
            return;
        }
        var dialog = new ContentDialog
        {
            Title = $"启用插件：{selected.Name}",
            Content = new ScrollViewer
            {
                Content = new StackPanel { Spacing = 8, Children = { BuildChecklistPanel(checklist) } },
                MaxHeight = 420,
            },
            PrimaryButtonText = "确认全部并启用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        var answer = await dialog.ShowAsync();
        if (answer != ContentDialogResult.Primary) return;
        // 用户点“确认全部”即视为逐项确认；文本原样传给管理服务做内容比对。
        if (ViewModel.TryAuthorize(checklist, out var error))
        {
            DetailStatusText.Text = "已授权并启用，尚未运行。";
        }
        else
        {
            DetailStatusText.Text = "启用失败：" + error;
        }
        RefreshAll();
    }

    private static StackPanel BuildChecklistPanel(IReadOnlyList<string> checklist)
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (var line in checklist)
        {
            panel.Children.Add(new TextBlock
            {
                Text = line,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            });
        }
        var notice = new TextBlock
        {
            Text = "以上每一项你都已阅读并确认。授权绑定当前版本与包哈希；插件更新或替换后需重新确认。",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0),
        };
        panel.Children.Add(notice);
        return panel;
    }

    private async void DisableButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is null) return;
        if (ViewModel.TryDisable(out var error))
        {
            DetailStatusText.Text = "已停用并撤回授权。再次启用需重新确认授权清单。";
        }
        else
        {
            DetailStatusText.Text = "停用失败：" + error;
        }
        RefreshAll();
        await Task.CompletedTask;
    }

    /// <summary>continuous 插件驻留启动（规范第 5 节：明确启用持续运行后驻留；崩溃不自动重启）。</summary>
    private async void PersistentButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } selected) return;
        if (!ServiceLocator.Plugins.Index.TryGet(selected.Id, out var entry, out var error) || entry is null)
        {
            DetailStatusText.Text = "读取插件信息失败：" + error;
            return;
        }
        // 声明资源冲突先检（持续驻留会执行修改操作前仍会在 invoke 时再检）。
        var dialog = new ContentDialog
        {
            Title = $"持续运行：{entry.Name}",
            Content = new TextBlock
            {
                Text = "该插件声明支持持续运行。驻留期间宿主每 5 秒心跳，15 秒无响应标记失联" +
                       "（提示人工处理，不自动强杀）；插件崩溃不会自动重启。" + Environment.NewLine +
                       "关闭本工具前会先停止插件；脱机自主运行需另行授权（当前未实现）。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "开始持续运行",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var result = await ServiceLocator.PluginRuntime.StartPersistentAsync(entry);
        DetailStatusText.Text = result.Length == 0 ? "已持续运行（心跳监控中）。" : result;
    }

    private async void StopPersistentButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } selected) return;
        var stopped = await ServiceLocator.PluginRuntime.StopAsync(selected.Id);
        DetailStatusText.Text = stopped ? "已停止持续运行。" : "停止未确认（进程可能仍在；请人工检查）。";
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } selected) return;
        if (!ServiceLocator.Plugins.Index.TryGet(selected.Id, out var entry, out var error) || entry is null)
        {
            DetailStatusText.Text = "读取插件信息失败：" + error;
            return;
        }
        if (!ServiceLocator.Plugins.Backups.TryListPending(entry.Id, out var pending, out var listError))
        {
            DetailStatusText.Text = "读取恢复记录失败：" + listError;
            return;
        }
        if (pending.Count == 0)
        {
            DetailStatusText.Text = "该插件没有待恢复的备份。";
            return;
        }
        var dialog = new ContentDialog
        {
            Title = $"恢复插件“{entry.Name}”的备份",
            Content = new TextBlock
            {
                Text = $"将启动插件后端并恢复 {pending.Count} 条备份（幂等，可重试）。" + Environment.NewLine +
                       "恢复结果由后端报告；失败或身份未知会保留记录与证据。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "开始恢复",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        DetailStatusText.Text = "正在恢复…";
        try
        {
            var summary = await ServiceLocator.PluginRuntime.RestoreAsync(entry);
            if (!ServiceLocator.Plugins.TryConfirmRestored(entry.Id, out var confirmError)) summary += "\n" + confirmError;
            RefreshAll();
            DetailStatusText.Text = summary;
        }
        catch (Exception ex)
        {
            DetailStatusText.Text = "恢复未确认：" + ex.GetType().Name + "；原记录保留。";
            Log.Error("插件恢复入口异常", ex);
        }
    }

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } selected) return;
        // 卸载前统一拦截：停止/恢复/排空失败则阻止卸载（待做清单第 9 项）。
        var blocker = await PluginLifecycleGuard.PrepareHandoverAsync(
            ServiceLocator.Plugins, ServiceLocator.PluginRuntime, "卸载");
        if (blocker.Length > 0)
        {
            DetailStatusText.Text = blocker;
            await ShowDialogAsync("卸载被阻止", blocker);
            return;
        }
        var dialog = new ContentDialog
        {
            Title = $"卸载插件：{selected.Name}",
            Content = new TextBlock
            {
                Text = "将删除插件安装目录、配置文件并从索引移除。\n" +
                       "插件自身对系统的直接改动（如有）不在宿主卸载范围内，需由插件恢复或人工处理。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "卸载",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (ViewModel.TryUninstall(out var error))
        {
            DetailStatusText.Text = "已卸载。";
        }
        else
        {
            DetailStatusText.Text = "卸载失败：" + error;
        }
        RefreshAll();
    }

    private async Task ShowDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            CloseButtonText = "知道了",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
