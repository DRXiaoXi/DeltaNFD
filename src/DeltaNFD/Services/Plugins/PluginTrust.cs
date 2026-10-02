namespace DeltaNFD.Services.Plugins;

/// <summary>
/// 授权与信任文案（规范第 1、5 节）。授权绑定 ID + 版本 + 包 SHA256；
/// 首次启用、替换或升级必须逐项确认；不得宣传反作弊零风险或“安全认证”。
/// </summary>
public static class PluginTrust
{
    /// <summary>启用/替换前的确认清单；调用方逐项展示，用户全部同意才写入授权。</summary>
    public static IReadOnlyList<string> AuthorizationChecklist(PluginManifest manifest, string packageSha256, bool signed) =>
    [
        $"插件：{manifest.Name}（{manifest.Id} v{manifest.Version}）",
        $"作者：{manifest.Author}（来源可信度由你自行判断；{(signed ? "包含有效签名，仅标识签名者，不等于安全认证" : "未签名包，来源未验证")}）",
        $"完整包 SHA256：{packageSha256}",
        "后端以管理员身份运行，是完全受信任的第三方程序，可直接修改系统；分离架构不是权限沙箱。",
        "权限用途：" + string.Join("；", manifest.Permissions.Select(p => $"{p.Id}——{p.Purpose}")),
        $"能力：{(manifest.Capabilities.Continuous ? "允许宿主存活期间持续运行" : "按操作启动，完成后停止")}" +
            (manifest.Capabilities.OfflineAutonomous ? "；声明支持脱机自主运行（需单独授权）" : ""),
        "宿主不能保证备份真实、改动可恢复、反作弊兼容或插件不逃逸；不能据此宣传“零封号风险”。",
    ];

    /// <summary>详情页固定展示的信任边界说明（规范第 1 节）。</summary>
    public static string TrustBoundaryNotice() =>
        "插件后端是完全受信任的管理员程序。能力声明、确认弹窗、协议校验和独立进程都不是权限沙箱；" +
        "有效签名仅标识签名者。协议禁止游戏注入与反作弊干预，但宿主无法强制阻止管理员代码；" +
        "宿主不能保证插件报告的备份真实或改动可撤销，也不代表反作弊厂商认可。";

    /// <summary>脱机授权确认（规范第 8 节）：总开关 + 逐插件授权 + 能力声明缺一不可。</summary>
    public static string OfflineAuthorizationNotice(PluginManifest manifest) =>
        $"授权“{manifest.Name}”在脱机模式自主运行：主程序与官方助手退出后，该插件进程继续驻留（非零进程），" +
        "自行管理重连与恢复；宿主不提供后台管理、游戏退出恢复或自动停止。此授权绑定当前版本与包哈希。";
}
