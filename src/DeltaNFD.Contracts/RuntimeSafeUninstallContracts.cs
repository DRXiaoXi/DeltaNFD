namespace DeltaNFD.Services;

/// <summary>安全模式全量卸载 C++ 时的逐项计划（仅显示已核验可处理的登记）。</summary>
public sealed record RuntimeSafeUninstallEntry(string Name, string Architecture, string Version, string Kind);

public sealed record RuntimeSafeUninstallPreview(
    bool CanRun,
    string Blocker,
    string Fingerprint,
    IReadOnlyList<RuntimeSafeUninstallEntry> Entries)
{
    /// <summary>是否必须在当前安全模式临时启用 Windows Installer 才能执行。</summary>
    public bool RequiresTemporaryInstaller { get; init; }

    /// <summary>临时启用是否具备条件；false 时 Blockers 说明原因。</summary>
    public bool TemporaryInstallerCanEnable { get; init; }

    public string TemporaryInstallerDetail { get; init; } = "";
}

public sealed class RuntimeSafeUninstallArgs
{
    public string Fingerprint { get; set; } = "";
    public bool AllowTemporaryInstaller { get; set; }
}
