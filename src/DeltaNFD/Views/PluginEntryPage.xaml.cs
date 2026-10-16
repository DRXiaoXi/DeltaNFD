using DeltaNFD.Services;
using DeltaNFD.Services.Plugins;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace DeltaNFD.Views;

/// <summary>
/// 插件侧栏独立入口（规范第 4.1 节）：按插件 ID 定位已导入包，展示作者/ID/状态并渲染
/// 共享声明式界面。仅导航与读取——不启动、不重连、不移交、不恢复、不调用后端；
/// 需要查询真实后端时使用显式操作按钮，不把查询藏在页面加载里。
/// </summary>
public sealed partial class PluginEntryPage : Page
{
    /// <summary>当前入口对应的插件 ID（路由身份）；未收到参数时为空。</summary>
    public string PluginId { get; private set; } = "";

    /// <summary>本页渲染时对应包的哈希与版本，供宿主判定升级后是否需退出本页（B4）。</summary>
    public string LoadedPackageSha256 { get; private set; } = "";
    public string LoadedVersion { get; private set; } = "";

    public PluginEntryPage()
    {
        InitializeComponent();
        // 离页释放共享视图（释放文件锁、丢弃控件引用）；不停止持续插件、不撤销授权。
        Unloaded += (_, _) => DeclarativeView.Clear();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        PluginId = e.Parameter as string ?? "";
        RefreshEntry();
    }

    /// <summary>宿主在插件集合变更后调用：条目已被移除或身份变化时本页须退出（由宿主决定导航）。</summary>
    public bool IsStale()
    {
        if (PluginId.Length == 0) return true;
        if (!ServiceLocator.Plugins.Index.TryGet(PluginId, out var entry, out _) || entry is null) return true;
        return !string.Equals(entry.PackageSha256, LoadedPackageSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(entry.Version, LoadedVersion, StringComparison.Ordinal);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshEntry();

    private void OpenManagement_Click(object sender, RoutedEventArgs e) =>
        App.MainWindow?.NavigateByTag("plugins");

    private void RefreshEntry()
    {
        string error = "";
        PluginIndexEntry? entry = null;
        if (PluginId.Length == 0 ||
            !ServiceLocator.Plugins.Index.TryGet(PluginId, out entry, out error) || entry is null)
        {
            EntryTitleText.Text = "插件不可用";
            EntrySubtitleText.Text = "该入口对应的插件已不存在或索引不可读。";
            EntryMetaText.Text = error.Length > 0 ? "宿主诊断：" + error : "";
            EntryStateText.Text = "请到插件管理页查看最新状态。";
            LoadedPackageSha256 = "";
            LoadedVersion = "";
            DeclarativeView.SetEntry(null);
            return;
        }

        LoadedPackageSha256 = entry.PackageSha256;
        LoadedVersion = entry.Version;
        EntryTitleText.Text = entry.Name;
        EntrySubtitleText.Text = $"作者 {entry.Author} · 插件 ID {entry.Id} · v{entry.Version}";
        EntryMetaText.Text =
            $"安装位置：{entry.InstallDirectory}\n" +
            $"完整包 SHA256：{entry.PackageSha256}\n" +
            (entry.AuthorizedUtc is { } at ? $"授权时间：{at.LocalDateTime:yyyy-MM-dd HH:mm}" : "状态：未授权");

        var compatible = TryIsCompatible(entry);
        EntryStateText.Text = PluginNavigationCatalog.DescribeStatus(entry.State, compatible) +
            "。菜单存在不代表已启用或正在运行。";

        var runtimeStatus = ServiceLocator.PluginRuntime.QueryStatus(entry.Id);
        if (!string.IsNullOrWhiteSpace(runtimeStatus.Detail))
            EntryStateText.Text += "\n" + runtimeStatus.Detail;

        DeclarativeView.SetEntry(entry);
    }

    private static bool TryIsCompatible(PluginIndexEntry entry)
    {
        try
        {
            using var snapshot = PluginPackageIntegrity.VerifyAndLock(entry);
            return PluginManifestParser.Parse(snapshot.ReadJson("manifest.json")).IsCompatible(AppVersion.Current);
        }
        catch (PluginContractException) { return false; }
        catch (InvalidDataException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
