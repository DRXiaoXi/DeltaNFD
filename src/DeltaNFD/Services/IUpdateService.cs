namespace DeltaNFD.Services;

/// <summary>一次更新检查的结论。</summary>
public enum UpdateCheckStatus
{
    /// <summary>远端版本不高于本机版本。</summary>
    UpToDate,

    /// <summary>发现新版本，可以下载安装。</summary>
    UpdateAvailable,

    /// <summary>发现新版本，但用户对该版本选择了“跳过此版本”（强制更新时不会走到这里）。</summary>
    Skipped,

    /// <summary>当前不是安装包安装的运行实例（开发构建 / 手动解压），不提供自动更新。</summary>
    NotInstalled,

    /// <summary>设置里关闭了启动时自动检查（仅自动检查会返回）。</summary>
    Disabled,

    /// <summary>距上次检查未达到节流间隔（仅自动检查会返回）。</summary>
    Throttled,

    /// <summary>网络失败 / 全部清单地址都取不到。</summary>
    NetworkError,

    /// <summary>清单可下载但内容非法（JSON 坏、字段缺失、地址不合规、哈希格式错）。</summary>
    ManifestInvalid,
}

/// <summary>更新清单里的一个安装包下载地址。</summary>
public sealed class UpdateInstallerInfo
{
    public string Url { get; set; } = "";

    /// <summary>期望字节数；0 = 清单未声明（仍会强制校验 SHA256）。</summary>
    public long SizeBytes { get; set; }

    /// <summary>SHA256（64 位十六进制，大小写不限）。</summary>
    public string Sha256 { get; set; } = "";
}

/// <summary>更新清单（update.json）模型。</summary>
public sealed class UpdateManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Version { get; set; } = "";
    public string DisplayVersion { get; set; } = "";
    public string Channel { get; set; } = "";
    public bool Mandatory { get; set; }
    public string MinimumSupportedVersion { get; set; } = "";
    public string PublishedAtUtc { get; set; } = "";
    public string ReleasePageUrl { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<UpdateInstallerInfo>? Installers { get; set; }
}

/// <summary>更新来源与网络参数（Assets\UpdateConfig.json，可随安装目录直接修改）。</summary>
public sealed class UpdateConfig
{
    public int SchemaVersion { get; set; } = 1;
    public List<string>? ManifestUrls { get; set; }
    public List<string>? AllowedHosts { get; set; }
    public int AutoCheckThrottleHours { get; set; } = 6;
    public int CheckTimeoutSeconds { get; set; } = 15;
    public int DownloadTimeoutMinutes { get; set; } = 60;

    /// <summary>清单不可用/未配置时的发布页面兜底地址（手动下载用）。</summary>
    public string ReleasePageUrl { get; set; } = "";
}

/// <summary>更新检查结果（不抛异常，失败原因走 <see cref="Message"/>）。</summary>
public sealed class UpdateCheckResult
{
    public UpdateCheckStatus Status { get; init; } = UpdateCheckStatus.UpToDate;

    /// <summary>面向用户/日志的中文说明。</summary>
    public string Message { get; init; } = "";

    public string CurrentVersionText { get; init; } = "";
    public string AvailableVersionText { get; init; } = "";
    public bool Mandatory { get; init; }
    public string Notes { get; init; } = "";
    public string ReleasePageUrl { get; init; } = "";
    public UpdateManifest? Manifest { get; init; }

    public bool HasUpdate => Status == UpdateCheckStatus.UpdateAvailable;
}

/// <summary>下载进度快照。</summary>
public sealed class UpdateProgressInfo
{
    public long BytesReceived { get; init; }
    public long TotalBytes { get; init; }
    public double BytesPerSecond { get; init; }

    public double Fraction => TotalBytes <= 0
        ? 0
        : Math.Clamp((double)BytesReceived / TotalBytes, 0, 1);
}

/// <summary>下载结果：只有校验通过才会给出 <see cref="InstallerPath"/>。</summary>
public sealed class UpdateDownloadResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string InstallerPath { get; init; } = "";

    public static UpdateDownloadResult Ok(string path, string message) =>
        new() { Success = true, InstallerPath = path, Message = message };

    public static UpdateDownloadResult Fail(string message) =>
        new() { Success = false, Message = message };
}

/// <summary>启动安装程序的结果。</summary>
public sealed class UpdateLaunchResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string LogPath { get; init; } = "";
}

/// <summary>
/// 自动更新服务：读取远端更新清单、比对版本、下载并校验安装包、把安装交给 Inno Setup。
/// 注意：实现只依赖 BCL（不引用 WinUI 类型），以便只读冒烟测试直接编译验证。
/// </summary>
public interface IUpdateService
{
    /// <summary>本机数值版本。</summary>
    Version CurrentVersion { get; }

    /// <summary>本机显示版本文本（如 OpenAlphaV0.82）。</summary>
    string CurrentVersionText { get; }

    /// <summary>当前目录是否就是安装包注册的安装目录（否则不提供自动更新）。</summary>
    bool IsInstalledCopy { get; }

    /// <summary>安装目录（当前进程基目录）。</summary>
    string InstallDirectory { get; }

    /// <summary>下载根目录（%LOCALAPPDATA%\Delta NFD\update）。</summary>
    string UpdateRootDirectory { get; }

    /// <summary>手动下载兜底的发布页面地址（清单取不到时给用户点）。</summary>
    string FallbackReleasePageUrl { get; }

    /// <summary>当前生效的更新源来自哪里（用户配置 / 程序内置配置 / 内置默认值）。</summary>
    string ConfigSourceDescription { get; }

    /// <summary>用户级更新源配置路径（%APPDATA%\Delta NFD\update-source.json，升级不覆盖）。</summary>
    string UserConfigPath { get; }

    /// <summary>用户级更新源配置不存在时生成一份带注释的模板（已存在则不动）。</summary>
    void EnsureUserConfigTemplate();

    /// <summary>可用性变化（有新版本 / 新版本消失）。<b>可能在后台线程触发</b>，UI 层自行切回 UI 线程。</summary>
    event Action<bool>? AvailabilityChanged;

    /// <summary>最近一次检查结果（尚未检查时为 null）；页面重新打开时据此回显，不重复联网。</summary>
    UpdateCheckResult? LastResult { get; }

    /// <summary>检查更新。<paramref name="manual"/> = 用户主动点击（忽略开关与节流）。</summary>
    Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken cancellationToken = default);

    /// <summary>下载并校验清单里的安装包；逐个地址回退，校验不过不落盘。</summary>
    Task<UpdateDownloadResult> DownloadAsync(
        UpdateManifest manifest,
        IProgress<UpdateProgressInfo>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>登记待更新标记并以静默模式启动安装程序（调用方随后应退出应用）。</summary>
    Task<UpdateLaunchResult> LaunchInstallerAsync(UpdateManifest manifest, string installerPath);

    /// <summary>清理已下载的安装包与历史版本目录（升级成功后调用）。</summary>
    void CleanupPendingInstaller();
}
