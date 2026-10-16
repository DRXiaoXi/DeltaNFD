using System.Collections.ObjectModel;
using System.ComponentModel;
using DeltaNFD.Services;
using DeltaNFD.Services.Plugins;

namespace DeltaNFD.ViewModels;

/// <summary>插件列表行的显示模型（页面代码后置负责构建，避免 VM 依赖 WinUI）。</summary>
public sealed class PluginListItem : INotifyPropertyChanged
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Author { get; init; }
    public required string Version { get; init; }
    public required string StateText { get; init; }
    public required bool IsAuthorized { get; init; }
    public required bool IsPendingRestore { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool _isEnabled;
    public bool IsEnabled { get => _isEnabled; set { _isEnabled = value; Raise(nameof(IsEnabled)); } }
}

/// <summary>
/// 拓展插件页 ViewModel：列表、授权确认清单、导入、卸载状态。
/// 纯状态与文案；ContentDialog 等 UI 交互由页面代码后置执行（沿用设置页模式）。
/// </summary>
public sealed class PluginViewModel : INotifyPropertyChanged
{
    private readonly PluginManagerService _manager;

    public PluginViewModel(PluginManagerService? manager = null)
    {
        _manager = manager ?? ServiceLocator.Plugins;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public ObservableCollection<PluginListItem> Plugins { get; } = [];

    private string _statusText = "";
    public string StatusText { get => _statusText; private set { _statusText = value; Raise(nameof(StatusText)); } }

    private PluginListItem? _selected;
    public PluginListItem? Selected
    {
        get => _selected;
        set { _selected = value; Raise(nameof(Selected)); Raise(nameof(HasSelection)); }
    }

    public bool HasSelection => Selected is not null;
    public bool HasListItems => Plugins.Count > 0;

    /// <summary>当前选中插件的授权确认清单（PluginTrust.AuthorizationChecklist）。</summary>
    public IReadOnlyList<string> AuthorizationChecklist =>
        Selected is { } item && _manager.Index.TryGet(item.Id, out var entry, out _) && entry is not null
            ? PluginTrust.AuthorizationChecklist(entry, entry.PackageSha256, signed: false)
            : [];

    /// <summary>固定信任边界说明（详情区常驻展示）。</summary>
    public string TrustBoundaryNotice => PluginTrust.TrustBoundaryNotice();

    /// <summary>刷新插件列表；失败时把错误写进状态并保持旧列表（不静默清空）。</summary>
    public void Refresh()
    {
        if (!_manager.TryList(out var entries, out var error))
        {
            StatusText = "读取插件列表失败：" + error;
            return;
        }
        var previous = Selected?.Id;
        Plugins.Clear();
        foreach (var entry in entries.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            Plugins.Add(new PluginListItem
            {
                Id = entry.Id,
                Name = entry.Name,
                Author = entry.Author,
                Version = entry.Version,
                StateText = entry.State switch
                {
                    PluginPackageState.Authorized => "已启用（已授权）",
                    PluginPackageState.PendingRestore => "待恢复/未知记录",
                    _ => "已导入（未授权）",
                },
                IsAuthorized = entry.State == PluginPackageState.Authorized,
                IsPendingRestore = entry.State == PluginPackageState.PendingRestore,
                IsEnabled = entry.State == PluginPackageState.Authorized,
            });
        }
        Selected = previous is null ? null : Plugins.FirstOrDefault(p => p.Id == previous);
        StatusText = Plugins.Count == 0 ? "尚未导入任何插件。导入 .dnfdplugin 包后在此管理。" : "";
        Raise(nameof(AuthorizationChecklist));
        Raise(nameof(HasListItems));
    }

    public void SetStatus(string text) => StatusText = text;

    /// <summary>授权（启用）：确认清单必须来自 UI 逐项确认（页面后置组装）。</summary>
    public bool TryAuthorize(IReadOnlyList<string> confirmations, out string error)
    {
        if (Selected is not { } item) { error = "请先选择一个插件。"; return false; }
        if (!_manager.TryAuthorize(item.Id, confirmations, out _, out error)) return false;
        Refresh();
        return true;
    }

    /// <summary>停用：撤回授权，再次启用需重新确认。</summary>
    public bool TryDisable(out string error)
    {
        if (Selected is not { } item) { error = "请先选择一个插件。"; return false; }
        if (!_manager.TrySetEnabled(item.Id, enabled: false, out _, out error)) return false;
        Refresh();
        return true;
    }

    public bool TryUninstall(out string error)
    {
        if (Selected is not { } item) { error = "请先选择一个插件。"; return false; }
        if (!_manager.TryUninstall(item.Id, out error)) return false;
        Refresh();
        return true;
    }

    /// <summary>导入 .dnfdplugin 包（页面后置负责取文件路径并调 importer）。</summary>
    public PluginImportResult Import(string packagePath)
    {
        var result = _manager.Importer.Import(packagePath, _manager.PluginsRoot);
        Refresh();
        if (result.Succeeded)
        {
            // 导入原子提交成功后才允许刷新侧栏入口；失败不留下幽灵菜单（规范第 4.1 节）。
            _manager.NotifyChanged();
            SetStatus(result.ReplacedExisting
                ? $"已替换导入插件“{result.Entry!.Name}” v{result.Entry.Version}。旧授权已失效，请重新确认授权。"
                : $"已导入插件“{result.Entry!.Name}” v{result.Entry.Version}（默认禁用）。");
        }
        else
            SetStatus("导入失败：" + result.Error);
        return result;
    }
}
