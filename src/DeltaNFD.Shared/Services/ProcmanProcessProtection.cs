namespace DeltaNFD.Services;

/// <summary>进程管理前后端共用的固定 ACE 保护名单，不依赖安装目录或用户排除项。</summary>
public static class ProcmanProcessProtection
{
    private static readonly HashSet<string> AceImages = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACE-Tray.exe", "SGuard64.exe", "SGuardSvc64.exe",
    };

    public const string AceReason = "ACE 反作弊进程已保护：仅支持查看，不允许修改属性、创建规则或普通／稳洲的逐进程回收；激进全局回收不受此保护限制。";

    public static bool IsAceProtected(string? executablePath)
        => !string.IsNullOrWhiteSpace(executablePath) && AceImages.Contains(Path.GetFileName(executablePath));
}
