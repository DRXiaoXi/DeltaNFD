namespace DeltaNFD.Services.Plugins;

/// <summary>
/// 侧栏入口目录项：只读导航声明，不含后端身份或运行状态。
/// PluginId 是唯一路由键；Label 仅用于显示，不得作为执行路径（规范第 4.1 节）。
/// </summary>
public sealed record PluginNavigationEntry(
    string PluginId,
    string Name,
    string Author,
    string Version,
    string PackageSha256,
    string Label,
    string Icon,
    PluginPackageState State,
    bool IsCompatible,
    string StatusText);

/// <summary>
/// 宿主动态菜单目录（规范第 4.1 节）：从已提交索引读取带 navigation 的插件，
/// 生成“拓展插件”节点下的子入口。本层不引用 <see cref="PluginRuntimeService"/>，
/// 构建菜单不启动、不重连、不调用任何后端；损坏/缺失 manifest 的包不生成入口（无幽灵菜单）。
/// </summary>
public static class PluginNavigationCatalog
{
    /// <summary>子入口 Tag 前缀；路由只使用插件 ID，同名插件按 ID 区分、不合并。</summary>
    public const string TagPrefix = "plugin:";

    public static string TagFor(string pluginId) => TagPrefix + pluginId;

    /// <summary>从导航 Tag 解析插件 ID；非插件入口返回 null。</summary>
    public static string? ResolvePluginId(string? tag) =>
        tag is not null && tag.StartsWith(TagPrefix, StringComparison.Ordinal) && tag.Length > TagPrefix.Length
            ? tag[TagPrefix.Length..]
            : null;

    /// <summary>
    /// 构建入口列表。hostVersion 由调用方传入宿主数字版本（不在本层读取，便于测试与避免静态耦合）。
    /// 读取索引或包清单失败时不抛异常：索引失败返回空列表并给出 error，单包损坏只跳过该包。
    /// </summary>
    public static IReadOnlyList<PluginNavigationEntry> Build(PluginManagerService manager, Version hostVersion, out string error)
    {
        ArgumentNullException.ThrowIfNull(manager);
        error = "";
        if (!manager.TryList(out var entries, out error))
            return [];

        var result = new List<PluginNavigationEntry>();
        foreach (var entry in entries)
        {
            PluginManifest manifest;
            try
            {
                using var snapshot = PluginPackageIntegrity.VerifyAndLock(entry);
                manifest = PluginManifestParser.Parse(snapshot.ReadJson("manifest.json"));
                if (manifest.Id != entry.Id || manifest.Version != entry.Version) continue;
            }
            catch (PluginContractException) { continue; }
            catch (InvalidDataException) { continue; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            if (manifest.Navigation is not { } navigation) continue;

            var compatible = manifest.IsCompatible(hostVersion);
            result.Add(new PluginNavigationEntry(
                entry.Id, entry.Name, entry.Author, entry.Version, entry.PackageSha256,
                navigation.Label, navigation.Icon, entry.State, compatible,
                DescribeStatus(entry.State, compatible)));
        }

        return result
            .OrderBy(e => e.Name, StringComparer.CurrentCulture)
            .ThenBy(e => e.PluginId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>入口状态提示：菜单存在不代表启用或运行。</summary>
    public static string DescribeStatus(PluginPackageState state, bool compatible) =>
        !compatible ? "宿主版本不兼容"
        : state switch
        {
            PluginPackageState.Authorized => "已启用",
            PluginPackageState.PendingRestore => "待恢复/未知记录",
            _ => "已导入（未授权）",
        };
}
