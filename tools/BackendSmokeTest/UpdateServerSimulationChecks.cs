using System.Diagnostics;
using System.Text.Json;
using DeltaNFD.Services;

/// <summary>显式按需联网的模拟更新：使用隔离设置和较旧测试程序集，真实下载并校验，绝不执行安装器。</summary>
internal static class UpdateServerSimulationChecks
{
    internal static async Task RunAsync(string manifestUrl, string outputDirectory)
    {
        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var uri)
            || !UpdateUrlRewriter.IsSelfHostedUrl(manifestUrl) || uri.Query.Length != 0
            || !uri.AbsolutePath.StartsWith("/updates/packages/simulation/", StringComparison.Ordinal)
            || !uri.AbsolutePath.EndsWith("/update.json", StringComparison.Ordinal))
            throw new InvalidDataException("只允许自建服务器独立 simulation 目录的更新清单。");
        var installerPrefix = manifestUrl[..^"update.json".Length];
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidDataException("模拟目录必须为空，不能覆盖现有数据。");
        Directory.CreateDirectory(root);
        var testId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), testId.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, testId);
        AppSettingsStore.Update(s =>
        {
            s.UpdateChannel = UpdateChannel.Public;
            s.UpdateServer = UpdateServer.Automatic;
            s.UpdateMode = UpdateMode.FullInstaller;
            s.AutoUpdateCheckEnabled = false;
            s.EmergencyPolicyEnabled = false;
        });
        var config = UpdateService.LoadConfig(null);
        config.ManifestUrls = [manifestUrl];
        config.InternalManifestUrls = [manifestUrl];
        var service = new UpdateService(config, downloadRoot: Path.Combine(root, "download"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var settings = AppSettingsStore.Read();
        var check = await service.CheckSourcesAsync(config, settings, service.CurrentVersion, true,
            (source, token) => UpdateService.FetchUpdateSourceAsync(source, config, token), timeout.Token);
        if (!check.HasUpdate || check.Manifest is not { } manifest)
            throw new InvalidDataException("旧版发现更新模拟失败：" + check.Message
                + "；模拟程序集当前版本=" + service.CurrentVersionText);
        if (manifest.Installers is not { Count: > 0 } || manifest.Installers.Any(i =>
            !i.Url.StartsWith(installerPrefix, StringComparison.Ordinal) || i.SizeBytes is <= 0 or > 1073741824))
            throw new InvalidDataException("模拟安装包必须位于同一测试目录，大小须为 1 字节至 1GiB。");
        Console.WriteLine($"模拟旧客户端 {service.CurrentVersionText} 发现更新 {check.AvailableVersionText}。");
        var stopwatch = Stopwatch.StartNew();
        var downloaded = await service.DownloadAsync(manifest, cancellationToken: timeout.Token);
        if (!downloaded.Success) throw new InvalidDataException(downloaded.Message);
        var selected = manifest.Installers.Single(i =>
            new FileInfo(downloaded.InstallerPath).Length == i.SizeBytes);
        if (await UpdateService.DescribeMismatchAsync(downloaded.InstallerPath, selected, timeout.Token) is { } mismatch)
            throw new InvalidDataException(mismatch);
        var metadata = FileVersionInfo.GetVersionInfo(downloaded.InstallerPath);
        if (!UpdateService.TryParseVersion(metadata.FileVersion, out var installerVersion)
            || !UpdateService.TryParseVersion(manifest.Version, out var manifestVersion)
            || UpdateService.CompareVersions(installerVersion, manifestVersion) != 0)
            throw new InvalidDataException("安装包 PE 版本与清单不符。");
        var report = new
        {
            checkedUtc = DateTimeOffset.UtcNow,
            manifestUrl,
            simulatedCurrentVersion = service.CurrentVersionText,
            availableVersion = check.AvailableVersionText,
            status = check.Status.ToString(),
            downloadSucceeded = downloaded.Success,
            downloaded.InstallerPath,
            sizeBytes = new FileInfo(downloaded.InstallerPath).Length,
            sha256 = selected.Sha256,
            installerFileVersion = metadata.FileVersion,
            elapsedSeconds = stopwatch.Elapsed.TotalSeconds,
            installerExecuted = false,
        };
        File.WriteAllText(Path.Combine(root, "simulation-result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"真实下载、大小、SHA256 和 PE 版本核验通过；耗时 {stopwatch.Elapsed.TotalSeconds:F1}s。未执行安装器。");
    }
}
