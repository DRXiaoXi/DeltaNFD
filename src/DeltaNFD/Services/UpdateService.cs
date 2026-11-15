using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>
/// 自动更新服务：查询已发布 Release（或备用清单）→ 比对版本 → 校验 SHA256 → Inno Setup 安装。
///
/// 安全约定（因为当前没有代码签名，见 HANDOFF §12）：
///   1) 只允许 https，且主机必须命中 Assets\UpdateConfig.json 的 allowedHosts 白名单（支持子域后缀匹配）；
///   2) 清单必须给出 64 位十六进制 SHA256，下载后长度与哈希都一致才允许执行；
///   3) 绝不自动下载 / 自动安装——下载与安装都必须用户确认；
///   4) 不是安装包安装的运行实例（开发构建 / 手动解压）一律不提供自动更新。
///
/// 本类只依赖 BCL（不引用 WinUI 类型），因此可被 tools\BackendSmokeTest 直接编译做只读校验。
/// </summary>
public sealed partial class UpdateService : IUpdateService
{
    internal const int SupportedSchemaVersion = 1;

    /// <summary>Inno Setup 的 AppId（与 installer\DeltaNFD.iss 保持一致，改动必须同步两处）。</summary>
    internal const string ProductGuid = "{7A3E9C4D-52B8-4E1F-9A6C-D0F1B2E3A4C5}";

    internal const string UninstallSubKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + ProductGuid + "_is1";

    internal const string ExeName = "DeltaNFD.exe";

    private const int BufferSize = 81920;
    private const int MaxManifestChars = 256 * 1024;
    internal const int MaxGitHubResponseBytes = 8 * 1024 * 1024;
    private const int MaxReleaseListChars = 16 * 1024 * 1024;
    private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan InitialInstallObservation = TimeSpan.FromMilliseconds(1500);

    internal const string OfficialManifestUrl = "https://deltaxiaoxi.xin/updates/update.json";
    internal const string OfficialInternalManifestUrl = "https://deltaxiaoxi.xin/updates/internal/update.json";
    internal static string SelfHostedManifest(UpdateChannel channel) => UpdateChannelPolicy.Normalize(channel) == UpdateChannel.Internal
        ? OfficialInternalManifestUrl : OfficialManifestUrl;

    /// <summary>优先 GitHub，失败后尝试自建服务器。</summary>
    internal static readonly string[] DefaultManifestUrls =
    [
        GitHubReleaseSource.ReleasesUrl,
        OfficialManifestUrl,
    ];

    // 两个通道共用回退顺序；仍按通道筛选 Alpha / Beta / Release。
    internal static readonly string[] DefaultInternalManifestUrls = [GitHubReleaseSource.ReleasesUrl, OfficialInternalManifestUrl];

    /// <summary>默认允许的下载/清单主机（镜像主机请追加到 Assets\UpdateConfig.json 的 allowedHosts）。</summary>
    internal static readonly string[] DefaultAllowedHosts =
    [
        "deltaxiaoxi.xin",
        "githubusercontent.com",
        "github.com",
        "api.github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
    ];

    /// <summary>清单不可用 / 未安装版时的手动下载兜底地址。</summary>
    internal const string DefaultReleasePageUrl = "https://github.com/DRXiaoXi/DeltaNFD/releases";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static readonly Lazy<HttpClient> SharedClient = new(CreateClient, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>随程序输出的默认更新源配置。</summary>
    internal static readonly string ShippedConfigPath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "UpdateConfig.json");

    /// <summary>
    /// 用户级更新源配置模板（首次点「更新源配置」时生成到 %APPDATA%\Delta NFD\update-source.json）。
    /// 该文件优先级高于程序自带配置，且升级安装不会覆盖。官方来源统一按 GitHub、自建服务器排序。
    /// </summary>
    internal const string UserConfigTemplate = """
    {
      // Delta NFD 更新源配置（用户级，优先级高于程序目录里的 Assets\UpdateConfig.json，
      // 并且升级安装不会覆盖本文件）。
      //
      // 两个通道均先查 GitHub，失败后才查自建服务器。
      // 历史官方来源/代理配置会在内存中迁移，不覆盖此文件。
      // 完全自定义来源须同时配置 allowedHosts；下载仍须校验大小和 SHA256。
      "schemaVersion": 1,
      "manifestUrls": [
        "https://api.github.com/repos/DRXiaoXi/DeltaNFD/releases?per_page=100",
        "https://deltaxiaoxi.xin/updates/update.json"
      ],
      "internalManifestUrls": [
        "https://api.github.com/repos/DRXiaoXi/DeltaNFD/releases?per_page=100",
        "https://deltaxiaoxi.xin/updates/update.json"
      ],
      "allowedHosts": [
        "deltaxiaoxi.xin",
        "githubusercontent.com",
        "github.com",
        "api.github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com"
      ],
      // Legacy field; startup checks no longer use this interval.
      "autoCheckThrottleHours": 6,
      "checkTimeoutSeconds": 15,
      "downloadTimeoutMinutes": 60,
      "releasePageUrl": "https://github.com/DRXiaoXi/DeltaNFD/releases"
    }
    """;

    private readonly UpdateConfig _config;
    private readonly HttpClient _httpClient;
    private readonly string? _downloadRoot;
    private readonly string? _installRoot;

    public UpdateService()
    {
        _config = LoadEffectiveConfig(
            ShippedConfigPath,
            Path.Combine(AppDataPaths.Root, "update-source.json"),
            out var source);
        ConfigSourceDescription = source;
        _httpClient = SharedClient.Value;
    }

    internal UpdateService(UpdateConfig config, HttpClient? httpClient = null, string? downloadRoot = null, string? installRoot = null)
    {
        _config = Normalize(config);
        ConfigSourceDescription = "测试注入配置";
        _httpClient = httpClient ?? SharedClient.Value;
        _downloadRoot = downloadRoot;
        _installRoot = installRoot;
    }

    internal UpdateConfig Config => _config;

    /// <summary>当前生效的更新源来自哪里（用户配置 / 程序内置配置 / 内置默认值）。</summary>
    public string ConfigSourceDescription { get; }

    /// <summary>用户级更新源配置文件路径。</summary>
    public string UserConfigPath => Path.Combine(AppDataPaths.Root, "update-source.json");

    public Version CurrentVersion => AppVersion.Current;

    public string CurrentVersionText => AppVersion.Text;

    public string InstallDirectory => _installRoot ?? AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    public string UpdateRootDirectory =>
        _downloadRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Delta NFD", "update");

    public string FallbackReleasePageUrl =>
        !string.IsNullOrWhiteSpace(_config.ReleasePageUrl) && IsAllowedUrl(_config.ReleasePageUrl, _config, out _)
            ? _config.ReleasePageUrl
            : DefaultReleasePageUrl;

    public bool IsInstalledCopy => IsInstalledCopyAt(ReadInstalledLocation(), AppContext.BaseDirectory);

    public event Action<bool>? AvailabilityChanged;

    private UpdateCheckResult? _lastResult;
    private readonly object _resultGate = new();
    private long _checkGeneration;

    public UpdateCheckResult? LastResult { get { lock (_resultGate) return _lastResult; } }

    public void ResetCheckResult()
    {
        ClearFullFallbackPermit();
        lock (_resultGate)
        {
            _checkGeneration++;
            _lastResult = Simple(UpdateCheckStatus.UpToDate, "更新通道或服务器已变更，请重新检查更新。");
            AvailabilityChanged?.Invoke(false);
        }
    }

    // ---------------------------------------------------------------- 检查

    public async Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken cancellationToken = default)
    {
        var settings = AppSettingsStore.Read();
        return await CheckAndPublishAsync(settings, () => CheckCoreAsync(manual, cancellationToken, settings)).ConfigureAwait(false);
    }

    internal async Task<UpdateCheckResult> CheckAndPublishAsync(AppSettings settings, Func<Task<UpdateCheckResult>> check)
    {
        long generation;
        lock (_resultGate) generation = ++_checkGeneration;
        var channel = UpdateChannelPolicy.Normalize(settings.UpdateChannel);
        var server = UpdateServerPolicy.Normalize(settings.UpdateServer);
        var mode = UpdateModePolicy.Normalize(settings.UpdateMode);
        var result = await check().ConfigureAwait(false);
        lock (_resultGate)
        {
            var current = AppSettingsStore.Read();
            if (generation != _checkGeneration || channel != UpdateChannelPolicy.Normalize(current.UpdateChannel)
                || server != UpdateServerPolicy.Normalize(current.UpdateServer) || mode != UpdateModePolicy.Normalize(current.UpdateMode))
                return _lastResult ?? Simple(UpdateCheckStatus.UpToDate, "更新通道或服务器已变更，请重新检查更新。");
            _lastResult = result;
            AvailabilityChanged?.Invoke(result.HasUpdate);
            return result;
        }
    }

    private async Task<UpdateCheckResult> CheckCoreAsync(bool manual, CancellationToken cancellationToken, AppSettings settings)
    {
        var config = _config;
        if (manual)
            EmergencyPolicyUpdateService.RequestCheck(IsInstalledCopy);

        if (!manual)
        {
            if (!settings.AutoUpdateCheckEnabled)
            {
                return Simple(UpdateCheckStatus.Disabled, "已关闭启动时自动检查更新。");
            }
        }

        return await CheckForSessionAsync(settings, IsInstalledCopy, manual,
            (url, ct) => FetchUpdateSourceAsync(url, config, ct,
                (source, cfg, token) => FetchStringAsync(source, cfg, token, _httpClient, settings.UpdateServer)),
            time => AppSettingsStore.Update(s => s.LastUpdateCheckUtc = time), cancellationToken).ConfigureAwait(false);
    }

    internal async Task<UpdateCheckResult> CheckForSessionAsync(AppSettings settings, bool installed, bool manual,
        Func<string, CancellationToken, Task<string>> fetch, Action<DateTime> recordCheck,
        CancellationToken cancellationToken = default)
    {
        if (!manual && !settings.AutoUpdateCheckEnabled)
            return Simple(UpdateCheckStatus.Disabled, "已关闭启动时自动检查更新。");

        if (!installed)
        {
            var message = "当前运行的不是安装包安装的版本（开发构建或手动解压运行），不提供自动更新。请前往发布页面手动下载安装包。";
            Log.Info("更新：" + message);
            return Simple(UpdateCheckStatus.NotInstalled, message);
        }

        // 仅保留诊断时间，不以它阻止本次或下次启动的联网检查。
        recordCheck(DateTime.UtcNow);
        Log.Info($"更新：{(manual ? "手动" : "启动自动")}检查开始（每次启动检查，不使用旧节流/跳过记录）。");

        return await CheckSourcesAsync(_config, settings, CurrentVersion, manual, fetch, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<UpdateCheckResult> CheckSourcesAsync(UpdateConfig config, AppSettings settings, Version current,
        bool manual, Func<string, CancellationToken, Task<string>> fetch, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var invalidSource = false;

        var sources = UpdateServerPolicy.ManifestSources(config, settings);
        foreach (var url in sources)
        {
            string json;
            try
            {
                json = await fetch(url, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{HostOf(url)}：{ex.Message}");
                Log.Warn($"更新：清单获取失败（{url}）：{ex.Message}");
                continue;
            }

            if (!TryParseUpdateSource(json, config, out var manifest, out var reason, settings.UpdateChannel) || manifest is null)
            {
                if (reason == GitHubReleaseSource.NoMatchingReleaseReason)
                    return Simple(UpdateCheckStatus.UpToDate, UpdateChannelPolicy.Name(settings.UpdateChannel) + "暂无可用更新。");
                Log.Warn($"更新：清单不可用（{url}）：{reason}");
                // 已读到有效 JSON，但版本/安装包身份校验失败时停止，不能用旧备用清单掩盖。
                // 空响应、HTML 或损坏 JSON 属于来源不可读，可继续尝试自建服务器。
                try
                {
                    using var document = JsonDocument.Parse(json);
                    return Simple(UpdateCheckStatus.ManifestInvalid, "更新源数据不可用：" + reason);
                }
                catch (JsonException) { }
                invalidSource = true;
                errors.Add($"{HostOf(url)}：{reason}");
                continue;
            }

            manifest.Installers = (manifest.Installers ?? []).Where(i => UpdateServerPolicy.Allows(UpdateServerPolicy.EffectiveServer(settings), i.Url)).ToList();
            if (manifest.Installers.Count == 0 && !(UpdateModePolicy.Normalize(settings.UpdateMode) == UpdateMode.ChangedFiles
                && IsFileReferenceUsable(manifest, config) && UpdateServerPolicy.Allows(settings.UpdateServer, manifest.FileUpdate!.Url)))
                return Simple(UpdateCheckStatus.ManifestInvalid, "所选服务器的清单未提供该服务器的安装包，请切换服务器或稍后重试。");
            return Evaluate(manifest, settings, current, ignoreSkipped: true);
        }

        var status = invalidSource ? UpdateCheckStatus.ManifestInvalid : UpdateCheckStatus.NetworkError;
        var detail = errors.Count == 0 ? "未配置更新清单地址。" : string.Join("；", errors);
        Log.Warn("更新：检查失败（" + status + "）：" + detail);
        return Simple(status, "检查更新失败：" + detail);
    }

    /// <summary>版本比对 + 跳过版本判定（纯逻辑，冒烟测试直接调用）。</summary>
    internal UpdateCheckResult Evaluate(UpdateManifest manifest, AppSettings settings, Version current, bool ignoreSkipped = false)
    {
        if (!TryParseVersion(manifest.Version, out var remote))
        {
            return Simple(UpdateCheckStatus.ManifestInvalid, $"更新清单版本号非法：{manifest.Version}");
        }

        if (!UpdateChannelPolicy.Allows(settings.UpdateChannel, manifest))
            return Simple(UpdateCheckStatus.UpToDate, $"{UpdateChannelPolicy.Name(settings.UpdateChannel)}不接收此版本，暂无符合通道的新版本。");

        var mandatory = manifest.Mandatory || IsBelowMinimum(current, manifest.MinimumSupportedVersion);
        var display = string.IsNullOrWhiteSpace(manifest.DisplayVersion) ? manifest.Version : manifest.DisplayVersion;

        if (CompareVersions(remote, current) <= 0)
        {
            return new UpdateCheckResult
            {
                Status = UpdateCheckStatus.UpToDate,
                Message = $"已是最新版本（{AppVersion.Text}）。",
                CurrentVersionText = AppVersion.Text,
                Manifest = manifest,
            };
        }

        if (!ignoreSkipped && !mandatory && string.Equals(settings.SkippedUpdateVersion, manifest.Version, StringComparison.OrdinalIgnoreCase))
        {
            return new UpdateCheckResult
            {
                Status = UpdateCheckStatus.Skipped,
                Message = $"已跳过 {display}（可在设置页点「检查更新」重新提示）。",
                CurrentVersionText = AppVersion.Text,
                AvailableVersionText = display,
                Notes = manifest.Notes,
                ReleasePageUrl = manifest.ReleasePageUrl,
                Mandatory = mandatory,
                Manifest = manifest,
            };
        }

        Log.Info($"更新：发现新版本 {display}（本机 {AppVersion.Text}，强制={mandatory}）");
        return new UpdateCheckResult
        {
            Status = UpdateCheckStatus.UpdateAvailable,
            Message = mandatory ? $"发现必须更新的新版本 {display}。" : $"发现新版本 {display}。",
            CurrentVersionText = AppVersion.Text,
            AvailableVersionText = display,
            Notes = manifest.Notes,
            ReleasePageUrl = manifest.ReleasePageUrl,
            Mandatory = mandatory,
            Manifest = manifest,
        };
    }

    // ---------------------------------------------------------------- 下载

    private async Task<UpdateDownloadResult> DownloadFullAsync(
        UpdateManifest manifest,
        IProgress<UpdateProgressInfo>? progress = null,
        CancellationToken cancellationToken = default, bool fallback = false)
    {
        var settings = AppSettingsStore.Read();
        var server = UpdateServerPolicy.EffectiveServer(settings);
        if (!fallback && UpdateModePolicy.Normalize(settings.UpdateMode) != UpdateMode.FullInstaller)
            return UpdateDownloadResult.Fail("更新方式已变化，请重新检查。");
        if (!UpdateChannelPolicy.Allows(settings.UpdateChannel, manifest))
            return UpdateDownloadResult.Fail("该版本不属于当前更新通道，请重新检查更新。");
        if (!TryParseVersion(manifest.Version, out var remote))
        {
            return UpdateDownloadResult.Fail($"更新清单版本号非法：{manifest.Version}");
        }
        if (CompareVersions(remote, CurrentVersion) <= 0)
        {
            return UpdateDownloadResult.Fail("远端版本不高于当前版本，不下载或安装。");
        }

        var installers = (manifest.Installers ?? [])
            .OfType<UpdateInstallerInfo>()
            .Select(i => new UpdateInstallerInfo
            {
                Url = UpdateUrlRewriter.RestoreOfficialUrl(i.Url),
                SizeBytes = i.SizeBytes,
                Sha256 = i.Sha256,
            })
            .Where(i => IsInstallerUsable(i, _config, out _) && UpdateServerPolicy.Allows(server, i.Url))
            .OrderBy(i => UpdateUrlRewriter.IsGitHubUrl(i.Url) ? 0
                : UpdateUrlRewriter.IsSelfHostedUrl(i.Url) ? 1 : 2)
            .DistinctBy(i => i.Url, StringComparer.OrdinalIgnoreCase).ToList();
        if (installers.Count == 0)
            return UpdateDownloadResult.Fail("更新清单里没有可用的下载地址（需 https + 白名单主机 + 64 位 SHA256）。");

        var githubInstallers = installers.Where(i => UpdateUrlRewriter.IsGitHubUrl(i.Url)).ToList();
        // 不允许备用 URL 携带与 GitHub 不同的安装包身份。
        if (githubInstallers.Count > 0)
            installers.RemoveAll(i => !UpdateUrlRewriter.IsGitHubUrl(i.Url)
                && !githubInstallers.Any(g => SameInstaller(g, i)));
        var fallbackResolved = server != UpdateServer.Automatic || githubInstallers.Count == 0
            || installers.Any(i => UpdateUrlRewriter.IsSelfHostedUrl(i.Url))
            || !(_config.ManifestUrls ?? []).Concat(_config.InternalManifestUrls)
                .Contains(SelfHostedManifest(settings.UpdateChannel), StringComparer.OrdinalIgnoreCase);

        var versionTag = SanitizeSegment(AppVersion.Format(remote));
        var versionDir = Path.Combine(UpdateRootDirectory, versionTag);
        var finalPath = Path.Combine(versionDir, $"DeltaNFD_Setup_{versionTag}_x64.exe");

        try
        {
            Directory.CreateDirectory(versionDir);
        }
        catch (Exception ex)
        {
            Log.Error("更新：创建下载目录失败", ex);
            return UpdateDownloadResult.Fail("无法创建下载目录：" + ex.Message);
        }

        // 已经下载并校验通过 → 直接复用（重复点击不会重复下载 140MB）
        if (File.Exists(finalPath) &&
            await MatchesAnyAsync(finalPath, installers, cancellationToken).ConfigureAwait(false) is not null)
        {
            Log.Info($"更新：复用已下载的安装包 {finalPath}");
            if (!UpdateModePolicy.SameSelection(settings, AppSettingsStore.Read())) return UpdateDownloadResult.Fail("更新选项已变化，请重新检查。");
            return UpdateDownloadResult.Ok(finalPath, "已复用之前下载好的安装包。");
        }

        var errors = new List<string>();

        for (var index = 0; index <= installers.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!UpdateModePolicy.SameSelection(settings, AppSettingsStore.Read())) return UpdateDownloadResult.Fail("更新选项已变化，请重新检查。");
            if (!fallbackResolved && (index == installers.Count || !UpdateUrlRewriter.IsGitHubUrl(installers[index].Url)))
            {
                fallbackResolved = true;
                try
                {
                    var backupInstallers = await FetchSelfHostedInstallersAsync(manifest, githubInstallers, cancellationToken).ConfigureAwait(false);
                    installers.InsertRange(index, backupInstallers.Where(i => !installers.Any(existing =>
                        string.Equals(existing.Url, i.Url, StringComparison.OrdinalIgnoreCase))));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { errors.Add("自建服务器：" + ex.Message); }
            }
            if (index == installers.Count) break;
            var installer = installers[index];
            var partPath = finalPath + ".part";
            TryDelete(partPath);

            try
            {
                await DownloadOneAsync(installer, partPath, progress, server, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryDelete(partPath);
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"#{index + 1} {HostOf(installer.Url)}：{ex.Message}");
                Log.Warn($"更新：下载失败（{installer.Url}）：{ex.Message}");
                TryDelete(partPath);
                continue;
            }

            var mismatch = await DescribeMismatchAsync(partPath, installer, cancellationToken).ConfigureAwait(false);
            if (mismatch is not null)
            {
                errors.Add($"#{index + 1} {HostOf(installer.Url)}：{mismatch}");
                Log.Warn($"更新：安装包校验不通过（{installer.Url}）：{mismatch}");
                TryDelete(partPath);
                continue;
            }

            if (!UpdateModePolicy.SameSelection(settings, AppSettingsStore.Read()))
            { TryDelete(partPath); return UpdateDownloadResult.Fail("更新选项已变化，请重新检查。"); }
            try
            {
                File.Move(partPath, finalPath, overwrite: true);
            }
            catch (Exception ex)
            {
                errors.Add($"#{index + 1} 落盘失败：{ex.Message}");
                TryDelete(partPath);
                continue;
            }

            Log.Info($"更新：安装包下载完成并通过校验 {finalPath}");
            CleanupOtherVersions(versionTag);
            return UpdateDownloadResult.Ok(finalPath, "下载完成，安装包校验通过。");
        }

        return UpdateDownloadResult.Fail("全部下载地址均失败：" + string.Join("；", errors));
    }

    private static bool SameInstaller(UpdateInstallerInfo first, UpdateInstallerInfo second) =>
        first.SizeBytes == second.SizeBytes
        && string.Equals(first.Sha256, second.Sha256, StringComparison.OrdinalIgnoreCase);

    private async Task<List<UpdateInstallerInfo>> FetchSelfHostedInstallersAsync(UpdateManifest original,
        IReadOnlyList<UpdateInstallerInfo> githubInstallers, CancellationToken token)
    {
        Log.Info("更新：GitHub 安装包下载失败，核对自建服务器备用清单。");
        var json = await FetchStringAsync(SelfHostedManifest(AppSettingsStore.Read().UpdateChannel), _config, token, _httpClient).ConfigureAwait(false);
        if (!TryParseManifest(json, _config, out var fallback, out var reason) || fallback is null)
            throw new InvalidDataException("备用清单不可用：" + reason);
        if (!TryParseVersion(original.Version, out var version)
            || !TryParseVersion(fallback.Version, out var fallbackVersion)
            || CompareVersions(version, fallbackVersion) != 0
            || UpdateChannelPolicy.Classify(original.Version, original.DisplayVersion, original.Channel)
                != UpdateChannelPolicy.Classify(fallback.Version, fallback.DisplayVersion, fallback.Channel)
            || !UpdateChannelPolicy.Allows(AppSettingsStore.Read().UpdateChannel, fallback))
            throw new InvalidDataException("备用清单版本或通道与当前更新不一致，请稍后重试。");
        var matches = (fallback.Installers ?? []).Where(i => UpdateUrlRewriter.IsSelfHostedUrl(i.Url)
            && githubInstallers.Any(g => SameInstaller(g, i))).ToList();
        if (matches.Count == 0)
            throw new InvalidDataException("备用安装包的大小或 SHA256 与 GitHub 不一致，已拒绝回退。");
        return matches;
    }

    private async Task DownloadOneAsync(
        UpdateInstallerInfo installer,
        string partPath,
        IProgress<UpdateProgressInfo>? progress,
        UpdateServer server,
        CancellationToken cancellationToken)
    {
        if (!IsAllowedUrl(installer.Url, _config, out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(DownloadIdleTimeout);

        using var response = await SendGetAsync(_httpClient, installer.Url, _config, server, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? installer.SizeBytes;
        if (installer.SizeBytes > 0 && total > 0 && total != installer.SizeBytes)
        {
            throw new InvalidDataException($"远端文件大小 {total} 字节与清单声明的 {installer.SizeBytes} 字节不一致");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        await using var target = new FileStream(
            partPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

        var buffer = new byte[BufferSize];
        var stopwatch = Stopwatch.StartNew();
        var lastReportAt = TimeSpan.Zero;
        var lastBytes = 0L;
        var speed = 0d;
        var received = 0L;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
            received += read;
            if (installer.SizeBytes > 0 && received > installer.SizeBytes)
                throw new InvalidDataException("下载内容超过清单声明的大小。");

            // 每读一块重置“无数据”超时：慢速但持续推进的下载不会被误杀
            cts.CancelAfter(DownloadIdleTimeout);

            if (stopwatch.Elapsed - lastReportAt >= TimeSpan.FromMilliseconds(200))
            {
                var delta = (stopwatch.Elapsed - lastReportAt).TotalSeconds;
                if (delta > 0)
                {
                    speed = (received - lastBytes) / delta;
                }

                lastReportAt = stopwatch.Elapsed;
                lastBytes = received;
                progress?.Report(new UpdateProgressInfo
                {
                    BytesReceived = received,
                    TotalBytes = total,
                    BytesPerSecond = speed,
                });
            }
        }

        await target.FlushAsync(cts.Token).ConfigureAwait(false);
        progress?.Report(new UpdateProgressInfo
        {
            BytesReceived = received,
            TotalBytes = total,
            BytesPerSecond = speed,
        });
    }

    // ---------------------------------------------------------------- 安装交接

    public async Task<UpdateLaunchResult> LaunchInstallerAsync(UpdateManifest manifest, string installerPath)
    {
        var launchSelection = AppSettingsStore.Read();
        var fileUpdate = Path.GetFileName(installerPath).Equals("files.json", StringComparison.OrdinalIgnoreCase);
        var fallback = !fileUpdate && HasFullFallbackPermit(manifest, installerPath, launchSelection);
        if (!fallback && fileUpdate != (UpdateModePolicy.Normalize(launchSelection.UpdateMode) == UpdateMode.ChangedFiles))
            return new() { Message = "更新方式已变化，请按当前方式重新下载。" };
        if (fallback)
        {
            try { RequireSafeFallback(null); }
            catch (Exception ex) { return new() { Message = "自动回退已停止：" + ex.Message }; }
        }
        if (Path.GetFileName(installerPath).Equals("files.json", StringComparison.OrdinalIgnoreCase))
            return await LaunchFileUpdateAsync(manifest, installerPath).ConfigureAwait(false);
        if (!UpdateChannelPolicy.Allows(AppSettingsStore.Read().UpdateChannel, manifest))
            return new UpdateLaunchResult { Success = false, Message = "该版本不属于当前更新通道，请重新检查更新。" };
        var logPath = Path.Combine(UpdateRootDirectory, $"install-{SanitizeSegment(manifest.Version)}.log");
        if (!TryParseVersion(manifest.Version, out var remote) || CompareVersions(remote, CurrentVersion) <= 0)
        {
            return new UpdateLaunchResult { Success = false, Message = "只允许安装高于当前版本的更新。", LogPath = logPath };
        }

        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
        {
            return new UpdateLaunchResult { Success = false, Message = "安装包不存在，请重新下载。", LogPath = logPath };
        }

        if (DescribeInstallBlocker() is { } blocker)
        {
            return new UpdateLaunchResult { Success = false, Message = blocker, LogPath = logPath };
        }

        // Downloading can take minutes. Confirm the selected release is still the highest published version.
        var latest = await CheckAsync(manual: true).ConfigureAwait(false);
        if (!latest.HasUpdate || latest.Manifest is not { } latestManifest ||
            !TryParseVersion(latestManifest.Version, out var latestVersion) || CompareVersions(remote, latestVersion) != 0)
        {
            return new UpdateLaunchResult
            {
                Success = false,
                Message = "无法确认已下载版本仍为可用的最新版，请重新检查更新。" + latest.Message,
                LogPath = logPath,
            };
        }
        manifest = latestManifest;

        try
        {
            Directory.CreateDirectory(UpdateRootDirectory);
        }
        catch (Exception ex)
        {
            Log.Error("更新：创建更新目录失败", ex);
            return new UpdateLaunchResult { Success = false, Message = "无法创建更新目录：" + ex.Message, LogPath = logPath };
        }

        var elevated = ElevationHelper.IsElevated;
        var arguments = BuildInstallerArguments(InstallDirectory, logPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? UpdateRootDirectory,
            UseShellExecute = !elevated,
        };
        if (!elevated)
        {
            // 理论上到不了这里（app.manifest 要求管理员），兜底让系统弹 UAC 而不是静默失败
            startInfo.Verb = "runas";
        }

        try
        {
            // Keep the verified file open without write/delete sharing until the installer has started.
            using var verifiedInstaller = await OpenVerifiedInstallerAsync(
                installerPath, manifest, UpdateRootDirectory, _config).ConfigureAwait(false);
            if (!UpdateChannelPolicy.Allows(AppSettingsStore.Read().UpdateChannel, manifest)
                || !UpdateModePolicy.SameSelection(launchSelection, AppSettingsStore.Read()))
                return new UpdateLaunchResult { Success = false, Message = "更新通道或服务器已变更，请重新检查更新。", LogPath = logPath };
            if (DescribeInstallBlocker() is { } changedBlocker)
            {
                return new UpdateLaunchResult { Success = false, Message = changedBlocker, LogPath = logPath };
            }

            // The same policy is used by manual setup, uninstall and automatic update.
            using var deployment = await DeploymentHandover.PrepareAsync(uninstall: false).ConfigureAwait(false);
            if (!deployment.Ready)
                return new UpdateLaunchResult { Success = false, Message = deployment.Detail, LogPath = logPath };
            if (!UpdateChannelPolicy.Allows(AppSettingsStore.Read().UpdateChannel, manifest)
                || !UpdateModePolicy.SameSelection(launchSelection, AppSettingsStore.Read()))
                return new UpdateLaunchResult { Success = false, Message = "更新通道已变更，已取消启动安装器。", LogPath = logPath };

            // The permission only covers this download, release and unchanged selection.
            if (fallback && !HasFullFallbackPermit(manifest, installerPath, launchSelection))
                return new() { Message = "全量回退的版本或下载身份已变化，请重新检查更新。", LogPath = logPath };

            // The installer can terminate this process as soon as it starts.
            AppSettingsStore.Update(s =>
            {
                s.PendingUpdateVersion = manifest.Version;
                s.PendingUpdateDisplayVersion = string.IsNullOrWhiteSpace(manifest.DisplayVersion)
                    ? manifest.Version
                    : manifest.DisplayVersion;
                s.PendingUpdateInstallerPath = installerPath;
            });
            ClearFullFallbackPermit();
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                ClearPendingUpdate();
                return new UpdateLaunchResult { Success = false, Message = "无法启动安装程序。", LogPath = logPath };
            }

            Log.Info($"更新：已启动安装程序 PID={process.Id}，参数 {arguments}");

            // 观察一秒多：被杀软拦截 / 参数被拒时能当场给出可读原因，而不是"点了没反应"
            try
            {
                using var observation = new CancellationTokenSource(InitialInstallObservation);
                await process.WaitForExitAsync(observation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 仍在运行 = 正常进入安装流程
                return new UpdateLaunchResult
                {
                    Success = true,
                    Message = "安装程序已启动，正在等待安全交接；本工具退出后，请在安装完成时从快捷方式重新打开。",
                    LogPath = logPath,
                };
            }

            if (process.ExitCode != 0)
            {
                ClearPendingUpdate();
                var message = $"安装程序启动后立即退出（退出码 {process.ExitCode}），可能被杀软拦截或安装条件不满足。日志：{logPath}";
                Log.Error("更新：" + message);
                return new UpdateLaunchResult { Success = false, Message = message, LogPath = logPath };
            }

            return new UpdateLaunchResult
            {
                Success = true,
                Message = "安装程序已结束，本工具即将退出。",
                LogPath = logPath,
            };
        }
        catch (Exception ex)
        {
            ClearPendingUpdate();
            Log.Error("更新：启动安装程序失败", ex);
            return new UpdateLaunchResult
            {
                Success = false,
                Message = "启动安装程序失败：" + ex.Message,
                LogPath = logPath,
            };
        }
    }

    internal static string BuildInstallerArguments(string installDirectory, string logPath)
    {
        if (string.IsNullOrWhiteSpace(installDirectory) || string.IsNullOrWhiteSpace(logPath) ||
            installDirectory.IndexOfAny(['"', '\r', '\n', '\0']) >= 0 ||
            logPath.IndexOfAny(['"', '\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("安装目录或日志路径无效。");
        }

        var directory = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar);
        return $"/SILENT /NORESTART /CLOSEAPPLICATIONS /DIR=\"{directory}\" /LOG=\"{Path.GetFullPath(logPath)}\"";
    }

    internal static async Task<FileStream> OpenVerifiedInstallerAsync(
        string path, UpdateManifest manifest, string updateRoot, UpdateConfig config)
    {
        if (!IsUnderUpdateRoot(path, updateRoot))
        {
            throw new InvalidDataException("安装包不在更新缓存目录中。");
        }

        // Refuse symlinks/junctions in the cache path rather than execute their targets elevated.
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current);
             current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("安装包路径包含重解析点，请重新下载到普通目录。");
            }
        }

        var candidates = (manifest.Installers ?? [])
            .Where(i => IsInstallerUsable(i, config, out _)).ToList();
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, useAsync: true);
        try
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
            if (!candidates.Any(i => (i.SizeBytes <= 0 || i.SizeBytes == stream.Length) &&
                string.Equals(i.Sha256, hash, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("安装前复检失败：安装包大小或 SHA256 与清单不符，请重新下载。");
            }

            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>安装前的条件复检（等价 Inno 脚本 PrepareToInstall 的拒绝条件），把失败前置成可读提示。</summary>
    internal string? DescribeInstallBlocker()
    {
        var installLocation = ReadInstalledLocation();
        if (string.IsNullOrWhiteSpace(installLocation))
        {
            return "未在系统中找到本工具的安装记录（注册表卸载项），无法自动更新。" +
                   "如果是从压缩包手动解压运行的，请前往发布页面手动下载安装包。";
        }

        if (!IsInstalledCopyAt(installLocation, AppContext.BaseDirectory))
        {
            return $"检测到本工具安装在 {installLocation}，但当前运行的是 {AppContext.BaseDirectory}。" +
                   "为避免留下两套程序和失效的卸载记录，自动更新仅支持「运行中的版本与安装位置一致」的情况；" +
                   "请先在 Windows「已安装的应用」中卸载旧版，或直接从开始菜单启动已安装的版本。";
        }

        return null;
    }

    public void CleanupPendingInstaller()
    {
        try
        {
            var settings = AppSettingsStore.Read();
            var installerPath = settings.PendingUpdateInstallerPath;

            if (!string.IsNullOrWhiteSpace(installerPath) && IsUnderUpdateRoot(installerPath, UpdateRootDirectory))
            {
                TryDelete(installerPath);
                TryDelete(installerPath + ".part");

                var directory = Path.GetDirectoryName(installerPath);
                if (directory is not null &&
                    IsUnderUpdateRoot(directory, UpdateRootDirectory) &&
                    Directory.Exists(directory) &&
                    !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    TryDeleteDirectory(directory);
                }
            }

            AppSettingsStore.Update(s =>
            {
                s.PendingUpdateVersion = "";
                s.PendingUpdateDisplayVersion = "";
                s.PendingUpdateInstallerPath = "";
            });
        }
        catch (Exception ex)
        {
            Log.Warn("更新：清理安装包失败：" + ex.Message);
        }
    }

    private void ClearPendingUpdate() =>
        AppSettingsStore.Update(s =>
        {
            s.PendingUpdateVersion = "";
            s.PendingUpdateDisplayVersion = "";
            s.PendingUpdateInstallerPath = "";
        });

    // ---------------------------------------------------------------- 清单解析与校验（纯逻辑）

    /// <summary>读取并规范化一份更新源配置；文件缺失/损坏时退回内置默认值。</summary>
    internal static UpdateConfig LoadConfig(string? path)
    {
        TryReadConfigText(ReadFileText(path), out var config, out _);
        return Normalize(config);
    }

    /// <summary>
    /// 生效配置的解析顺序：<c>%APPDATA%\Delta NFD\update-source.json</c>（用户级，升级不覆盖）
    /// → 程序目录 <c>Assets\UpdateConfig.json</c>（随程序发布）→ 内置默认值。
    /// 用户文件只要存在且能解析出至少一个清单地址就整份生效；坏文件只记日志、退回下一级。
    /// </summary>
    internal static UpdateConfig LoadEffectiveConfig(string? shippedPath, string? userPath, out string source)
    {
        if (TryReadConfigText(ReadFileText(userPath), out var userConfig, out var userReason))
        {
            source = "用户配置：" + userPath;
            Log.Info("更新：使用用户级更新源配置 " + userPath);
            return Normalize(userConfig);
        }

        if (!string.IsNullOrWhiteSpace(userPath) && File.Exists(userPath) && userReason.Length > 0)
        {
            Log.Warn($"更新：用户级更新源配置不可用（{userPath}）：{userReason}；改用程序内置配置");
        }

        if (TryReadConfigText(ReadFileText(shippedPath), out var shippedConfig, out _))
        {
            source = "程序内置配置：" + shippedPath;
            return Normalize(shippedConfig);
        }

        source = "内置默认值";
        return Normalize(new UpdateConfig());
    }

    private static string? ReadFileText(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                return File.ReadAllText(path);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"更新：读取更新配置失败（{path}）：{ex.Message}");
        }

        return null;
    }

    /// <summary>解析配置文本；返回 false 表示文本缺失、坏 JSON 或没有任何清单地址。</summary>
    internal static bool TryReadConfigText(string? json, out UpdateConfig config, out string reason)
    {
        reason = "";
        config = new UpdateConfig();

        if (string.IsNullOrWhiteSpace(json))
        {
            reason = "文件不存在或为空";
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<UpdateConfig>(json, JsonOpts);
            if (parsed is null)
            {
                reason = "内容为空";
                return false;
            }

            config = parsed;
        }
        catch (Exception ex)
        {
            reason = "JSON 解析失败：" + ex.Message;
            return false;
        }

        if (config.ManifestUrls is null || config.ManifestUrls.Count == 0)
        {
            reason = "没有配置 manifestUrls";
            return false;
        }

        return true;
    }

    /// <summary>补齐缺失字段，迁移历史官方源和代理配置，再检查主机白名单。</summary>
    internal static UpdateConfig Normalize(UpdateConfig? config)
    {
        config ??= new UpdateConfig();

        if (config.ManifestUrls is null || config.ManifestUrls.Count == 0)
        {
            config.ManifestUrls = [.. DefaultManifestUrls];
            if (config.InternalManifestUrls is not { Count: > 0 })
                config.InternalManifestUrls = [.. DefaultInternalManifestUrls];
        }
        config.ManifestUrls = NormalizeSources(config.ManifestUrls);
        config.InternalManifestUrls = NormalizeSources(config.InternalManifestUrls ?? [], internalChannel: true);
        if (config.AllowedHosts is null || config.AllowedHosts.Count == 0)
            config.AllowedHosts = [.. DefaultAllowedHosts];
        config.AllowedHosts.RemoveAll(h => h is not null
            && h.Trim().Trim('.').Equals("gh-proxy.com", StringComparison.OrdinalIgnoreCase));
        if (config.ManifestUrls.Concat(config.InternalManifestUrls).Any(IsManagedSource))
            config.AllowedHosts = config.AllowedHosts.Concat(DefaultAllowedHosts)
                .Where(host => !UpdateUrlRewriter.IsLegacyProxyHost(host))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        config.ReleasePageUrl = UpdateUrlRewriter.RestoreOfficialUrl(config.ReleasePageUrl);

        if (config.AutoCheckThrottleHours <= 0)
        {
            config.AutoCheckThrottleHours = 6;
        }

        if (config.CheckTimeoutSeconds <= 0)
        {
            config.CheckTimeoutSeconds = 15;
        }

        if (config.DownloadTimeoutMinutes <= 0)
        {
            config.DownloadTimeoutMinutes = 60;
        }

        if (string.IsNullOrWhiteSpace(config.ReleasePageUrl))
        {
            config.ReleasePageUrl = DefaultReleasePageUrl;
        }

        // 除官方来源外，自定义主机仍须显式声明在 allowedHosts。
        foreach (var url in config.ManifestUrls.Concat(config.InternalManifestUrls))
        {
            if (!IsAllowedUrl(url, config, out var reason))
            {
                Log.Warn($"更新：清单地址未通过自身白名单校验（{url}）：{reason}");
            }
        }

        return config;
    }

    private static bool IsManagedSource(string url) => GitHubReleaseSource.IsRepositoryReleasesApi(url)
        || string.Equals(url, OfficialManifestUrl, StringComparison.OrdinalIgnoreCase)
        || string.Equals(url, OfficialInternalManifestUrl, StringComparison.OrdinalIgnoreCase);

    private static List<string> NormalizeSources(IEnumerable<string> urls, bool internalChannel = false)
    {
        var sources = urls.Select(UpdateUrlRewriter.RestoreOfficialUrl).Select(url =>
            string.Equals(url, "https://raw.githubusercontent.com/DRXiaoXi/DeltaNFD/main/update.json", StringComparison.OrdinalIgnoreCase)
                || GitHubReleaseSource.IsRepositoryReleasesApi(url) ? GitHubReleaseSource.ReleasesUrl : url)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // 只迁移本项目官方配置；完全自定义的来源仍由用户配置负责。
        return sources.Any(IsManagedSource)
            ? new[] { GitHubReleaseSource.ReleasesUrl, internalChannel ? OfficialInternalManifestUrl : OfficialManifestUrl }.Concat(sources.Where(u => !IsManagedSource(u))).ToList()
            : sources;
    }

    internal static UpdateConfig LoadConfigOrDefault() =>
        LoadEffectiveConfig(ShippedConfigPath, null, out _);

    /// <summary>写入用户级更新源配置模板（供 UI「更新源配置」按钮与测试复用）。</summary>
    internal static bool WriteUserConfigTemplate(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, UserConfigTemplate + Environment.NewLine, new UTF8Encoding(false));
            Log.Info("更新：已生成用户级更新源配置模板 " + path);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("更新：生成用户级更新源配置模板失败", ex);
            return false;
        }
    }

    /// <summary>用户级配置不存在时生成模板（已存在则不动，避免覆盖用户改过的镜像）。</summary>
    public void EnsureUserConfigTemplate()
    {
        var path = UserConfigPath;
        if (!File.Exists(path))
        {
            WriteUserConfigTemplate(path);
        }
    }

    /// <summary>安装包地址是否可用（https + 白名单主机 + 合法 SHA256）。</summary>
    internal static bool IsInstallerUsable(UpdateInstallerInfo? installer, UpdateConfig config, out string reason)
    {
        reason = "";
        if (installer is null)
        {
            reason = "空条目";
            return false;
        }

        if (!IsAllowedUrl(installer.Url, config, out reason))
        {
            return false;
        }

        if (!IsValidSha256(installer.Sha256))
        {
            reason = $"SHA256 缺失或格式非法（{installer.Url}）";
            return false;
        }

        if (installer.SizeBytes < 0)
        {
            reason = $"sizeBytes 非法（{installer.Url}）";
            return false;
        }

        return true;
    }

    internal static bool IsAllowedUrl(string? url, UpdateConfig config, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(url))
        {
            reason = "地址为空";
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            reason = $"地址格式非法：{url}";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"只允许 https 地址：{url}";
            return false;
        }

        if (UpdateUrlRewriter.IsLegacyProxyHost(uri.Host))
        {
            reason = "已取消 GitHub 加速，请使用官方直连地址。";
            return false;
        }
        var allowed = config.AllowedHosts ?? [];
        if (allowed.Count > 0 && !IsAllowedHost(uri.Host, allowed))
        {
            reason = $"主机 {uri.Host} 不在允许列表（Assets\\UpdateConfig.json 的 allowedHosts）";
            return false;
        }

        return true;
    }

    /// <summary>主机白名单匹配：精确相等或为其子域。</summary>
    internal static bool IsAllowedHost(string host, IEnumerable<string> allowedHosts)
    {
        var candidate = host.Trim().Trim('.').ToLowerInvariant();
        foreach (var entry in allowedHosts)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var allowed = entry.Trim().Trim('.').TrimStart('.').ToLowerInvariant();
            if (allowed.Length == 0)
            {
                continue;
            }

            if (candidate == allowed || candidate.EndsWith("." + allowed, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsValidSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (!Uri.IsHexDigit(ch))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool TryParseManifest(string json, UpdateConfig config, out UpdateManifest? manifest, out string reason)
    {
        manifest = null;
        reason = "";

        try
        {
            var parsed = JsonSerializer.Deserialize<UpdateManifest>(json, JsonOpts);
            if (parsed is null)
            {
                reason = "清单内容为空";
                return false;
            }

            if (parsed.SchemaVersion != SupportedSchemaVersion)
            {
                reason = $"清单 schemaVersion={parsed.SchemaVersion} 不受支持（本版本要求 {SupportedSchemaVersion}）";
                return false;
            }

            if (!TryParseVersion(parsed.Version, out var remoteVersion))
            {
                reason = $"版本号非法：{parsed.Version}";
                return false;
            }

            if (remoteVersion.Major == 0 && remoteVersion.Minor == 0 && Normalize(remoteVersion).Build == 0)
            {
                reason = $"版本号非法：{parsed.Version}";
                return false;
            }

            var usable = new List<UpdateInstallerInfo>();
            var rejected = new List<string>();
            foreach (var installer in parsed.Installers ?? [])
            {
                if (installer is null) { rejected.Add("空条目"); continue; }
                installer.Url = UpdateUrlRewriter.RestoreOfficialUrl(installer.Url);
                if (IsInstallerUsable(installer, config, out var why))
                {
                    usable.Add(installer);
                }
                else
                {
                    rejected.Add(why);
                }
            }

            if (usable.Count == 0 && !IsFileReferenceUsable(parsed, config))
            {
                reason = "清单里没有可用的下载地址" +
                         (rejected.Count > 0 ? "（" + string.Join("；", rejected) + "）" : "");
                return false;
            }

            // 发布页面只是给用户点开的链接：不合规就清空，不影响更新本身
            if (!string.IsNullOrWhiteSpace(parsed.ReleasePageUrl) &&
                !IsAllowedUrl(parsed.ReleasePageUrl, config, out _))
            {
                Log.Warn($"更新：清单的 releasePageUrl 主机不在白名单，已忽略（{parsed.ReleasePageUrl}）");
                parsed.ReleasePageUrl = "";
            }

            parsed.Installers = usable;
            manifest = parsed;
            return true;
        }
        catch (JsonException ex)
        {
            reason = "JSON 解析失败：" + ex.Message;
            return false;
        }
    }

    // ---------------------------------------------------------------- 版本比较

    /// <summary>解析版本号：容忍 v / OpenAlphaV 前缀与多段数字。</summary>
    internal static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var buffer = new StringBuilder();
        foreach (var ch in text.Trim())
        {
            if (char.IsDigit(ch))
            {
                buffer.Append(ch);
            }
            else if (ch == '.')
            {
                if (buffer.Length > 0 && buffer[^1] != '.')
                {
                    buffer.Append('.');
                }
            }
            else if (char.IsLetter(ch) && buffer.Length > 0 && buffer[^1] != '.')
            {
                // "OpenAlphaV0.83" 之类的写法：字母只作为分隔符，不参与版本解析
                buffer.Append('.');
            }
        }

        var candidate = buffer.ToString().Trim('.');
        if (candidate.Length == 0)
        {
            return false;
        }

        var parts = candidate.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count == 1)
        {
            parts.Add("0");
        }

        if (parts.Count > 4)
        {
            parts = parts.Take(4).ToList();
        }

        if (!Version.TryParse(string.Join('.', parts), out var parsed))
        {
            return false;
        }

        version = parsed;
        return true;
    }

    /// <summary>补齐到 4 段后按数值比较（-1 = 缺失段按 0）。</summary>
    internal static int CompareVersions(Version a, Version b) => Normalize(a).CompareTo(Normalize(b));

    internal static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    internal static bool IsBelowMinimum(Version current, string? minimumSupportedVersion)
    {
        if (!TryParseVersion(minimumSupportedVersion, out var minimum))
        {
            return false;
        }

        return CompareVersions(current, minimum) < 0;
    }

    internal static bool IsThrottled(DateTime? lastCheckUtc, int throttleHours, DateTime nowUtc)
    {
        if (lastCheckUtc is not { } last)
        {
            return false;
        }

        // 时钟被回拨时不节流，避免永久不再检查
        if (last > nowUtc)
        {
            return false;
        }

        return nowUtc - last < TimeSpan.FromHours(Math.Max(1, throttleHours));
    }

    // ---------------------------------------------------------------- 安装位置

    internal static string? ReadInstalledLocation()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(UninstallSubKey);
            return key?.GetValue("InstallLocation") as string;
        }
        catch (Exception ex)
        {
            Log.Warn("更新：读取安装位置失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>当前基目录是否就是注册表记录的安装目录（且程序文件在位）。</summary>
    internal static bool IsInstalledCopyAt(string? installLocation, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(installLocation) || string.IsNullOrWhiteSpace(baseDirectory))
        {
            return false;
        }

        try
        {
            var a = Path.GetFullPath(installLocation).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var b = Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
                   && File.Exists(Path.Combine(a, ExeName));
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- 下载辅助

    internal static bool TryParseUpdateSource(string json, UpdateConfig config, out UpdateManifest? manifest, out string reason, UpdateChannel? channel = null)
    {
        if (json.AsSpan().TrimStart().StartsWith("["))
            return GitHubReleaseSource.TryParse(json, config, out manifest, out reason, channel);
        return TryParseManifest(json, config, out manifest, out reason);
    }

    internal static async Task<string> FetchUpdateSourceAsync(string url, UpdateConfig config, CancellationToken cancellationToken,
        Func<string, UpdateConfig, CancellationToken, Task<string>>? fetch = null)
    {
        fetch ??= (source, cfg, token) => FetchStringAsync(source, cfg, token);
        var isRepositoryApi = GitHubReleaseSource.IsRepositoryReleasesApi(url);
        var sourceUrl = isRepositoryApi ? GitHubReleaseSource.ReleasesUrl : UpdateUrlRewriter.RestoreOfficialUrl(url);
        var json = await fetch(sourceUrl, config, cancellationToken)
            .ConfigureAwait(false);
        if (!isRepositoryApi) return json;

        var releases = new List<JsonElement>();
        long totalChars = 0;
        for (var page = 1; page <= 10; page++)
        {
            totalChars += json.Length;
            if (totalChars > MaxReleaseListChars)
                throw new InvalidDataException("GitHub 发布列表累计体积超过检查上限。");
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("GitHub Release 列表格式无效。");
            releases.AddRange(document.RootElement.EnumerateArray().Select(r => r.Clone()));
            if (document.RootElement.GetArrayLength() < 100) return JsonSerializer.Serialize(releases);
            if (page == 10) throw new InvalidDataException("发布记录超出检查上限，不能可靠确定最高版本。");
            json = await fetch(sourceUrl + "&page=" + (page + 1), config, cancellationToken)
                .ConfigureAwait(false);
        }
        throw new InvalidDataException("无法读取完整发布列表。");
    }

    internal static async Task<string> FetchStringAsync(string url, UpdateConfig config, CancellationToken cancellationToken,
        HttpClient? client = null, UpdateServer server = UpdateServer.Automatic)
    {
        if (!IsAllowedUrl(url, config, out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(config.CheckTimeoutSeconds));

        using var response = await SendGetAsync(client ?? SharedClient.Value, url, config, server, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // Release lists include every asset's metadata; they are much larger than update.json.
        // Bound the decoded response stream before buffering, including chunked/decompressed bodies.
        var isReleaseList = GitHubReleaseSource.IsRepositoryReleasesApi(url)
            && (response.RequestMessage?.RequestUri is not { } finalUri
                || GitHubReleaseSource.IsRepositoryReleasesApi(finalUri.AbsoluteUri));
        var maxBytes = isReleaseList ? MaxGitHubResponseBytes : MaxManifestChars * 4;
        var maxChars = isReleaseList ? MaxGitHubResponseBytes : MaxManifestChars;
        if (response.Content.Headers.ContentLength is { } length && length > maxBytes)
            throw new InvalidDataException($"更新响应超过体积上限（{maxBytes} 字节）。");
        await using var source = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var bounded = new MemoryStream();
        var buffer = new byte[16384];
        while (true)
        {
            var remaining = maxBytes - (int)bounded.Length;
            var read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)), cts.Token).ConfigureAwait(false);
            if (read == 0) break;
            if (read > remaining)
                throw new InvalidDataException($"更新响应超过体积上限（{maxBytes} 字节）。");
            bounded.Write(buffer, 0, read);
        }
        bounded.Position = 0;
        using var reader = new StreamReader(bounded, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync(cts.Token).ConfigureAwait(false);
        if (text.Length == 0)
        {
            throw new InvalidDataException("清单内容为空");
        }

        if (text.Length > maxChars)
        {
            throw new InvalidDataException($"清单体积异常（{text.Length} 字符）");
        }

        return text;
    }

    private static async Task<HttpResponseMessage> SendGetAsync(HttpClient client, string url, UpdateConfig config,
        UpdateServer server, CancellationToken token)
    {
        var source = new Uri(url, UriKind.Absolute);
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            token.ThrowIfCancellationRequested();
            if (!IsAllowedUrl(source.AbsoluteUri, config, out var reason))
                throw new InvalidOperationException(reason);
            if (!UpdateServerPolicy.Allows(server, source.AbsoluteUri))
                throw new InvalidDataException("更新地址或重定向不属于所选服务器，已停止请求。");
            using var request = new HttpRequestMessage(HttpMethod.Get, source);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.PermanentRedirect or HttpStatusCode.SeeOther))
                return response;
            using (response)
            {
                source = response.Headers.Location is { } location ? new Uri(source, location)
                    : throw new InvalidDataException("更新重定向缺少地址。");
            }
        }
        throw new InvalidDataException("更新重定向次数过多。");
    }

    private static async Task<string?> MatchesAnyAsync(
        string path,
        IReadOnlyList<UpdateInstallerInfo> installers,
        CancellationToken cancellationToken)
    {
        foreach (var installer in installers)
        {
            if (await DescribeMismatchAsync(path, installer, cancellationToken).ConfigureAwait(false) is null)
            {
                return installer.Url;
            }
        }

        return null;
    }

    /// <summary>校验文件；返回 null = 通过，否则返回中文原因。</summary>
    internal static async Task<string?> DescribeMismatchAsync(
        string path,
        UpdateInstallerInfo installer,
        CancellationToken cancellationToken = default)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
            {
                return "文件不存在";
            }
        }
        catch (Exception ex)
        {
            return "无法读取文件：" + ex.Message;
        }

        if (installer.SizeBytes > 0 && info.Length != installer.SizeBytes)
        {
            return $"大小不符（实际 {info.Length} 字节，清单 {installer.SizeBytes} 字节）";
        }

        var actual = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
        if (actual is null)
        {
            return "无法计算 SHA256";
        }

        return string.Equals(actual, installer.Sha256, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"SHA256 不符（实际 {actual}，清单 {installer.Sha256.ToLowerInvariant()}）";
    }

    internal static async Task<string?> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            using var sha = SHA256.Create();
            var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"更新：计算 SHA256 失败（{path}）：{ex.Message}");
            return null;
        }
    }

    private void CleanupOtherVersions(string keepVersionTag)
    {
        try
        {
            if (!Directory.Exists(UpdateRootDirectory))
            {
                return;
            }

            foreach (var directory in Directory.EnumerateDirectories(UpdateRootDirectory))
            {
                if (string.Equals(Path.GetFileName(directory), keepVersionTag, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Log.Info("更新：清理历史下载目录 " + directory);
                TryDeleteDirectory(directory);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("更新：清理历史下载失败：" + ex.Message);
        }
    }

    internal static bool IsUnderUpdateRoot(string path, string updateRoot)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(updateRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string SanitizeSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            builder.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? '_' : ch);
        }

        var result = builder.ToString();
        return result.Length == 0 ? "unknown" : result;
    }

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    private static UpdateCheckResult Simple(UpdateCheckStatus status, string message) => new()
    {
        Status = status,
        Message = message,
        CurrentVersionText = AppVersion.Text,
    };

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"DeltaNFD/{AppVersion.NumericText}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, application/octet-stream, */*");
        return client;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"更新：删除文件失败（{path}）：{ex.Message}");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"更新：删除目录失败（{path}）：{ex.Message}");
        }
    }
}
