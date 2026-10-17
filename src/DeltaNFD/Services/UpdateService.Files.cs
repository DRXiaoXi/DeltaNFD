using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using DeltaNFD.Updates;

namespace DeltaNFD.Services;

public sealed partial class UpdateService
{
    private static bool IsFileReferenceUsable(UpdateManifest manifest, UpdateConfig config) => manifest.FileUpdate is { } reference
        && reference.SizeBytes is > 0 and <= FileUpdateRules.MaximumManifestBytes
        && FileUpdateRules.IsHash(reference.Sha256) && IsAllowedUrl(reference.Url, config, out _);
    private readonly Dictionary<string, string> _fileCaches = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _fileCacheGate = new();

    private string FileCache(string hash)
    {
        if (_installRoot is not null) return Path.Combine(UpdateRootDirectory, "files", hash.ToLowerInvariant());
        lock (_fileCacheGate)
        {
            if (_fileCaches.TryGetValue(hash, out var existing) && !File.Exists(Path.Combine(existing, "status.json")))
            { ProtectedSessions.Validate(existing); return existing; }
            return _fileCaches[hash] = ProtectedSessions.Create();
        }
    }

    public async Task<UpdateDownloadResult> DownloadAsync(UpdateManifest manifest, IProgress<UpdateProgressInfo>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var initialSettings = AppSettingsStore.Read();
        ClearFullFallbackPermit();
        cancellationToken.ThrowIfCancellationRequested();
        if (!UpdateChannelPolicy.Allows(initialSettings.UpdateChannel, manifest)) return UpdateDownloadResult.Fail("该版本不属于当前更新通道，请重新检查更新。");
        if (!TryParseVersion(manifest.Version, out var version) || CompareVersions(version, CurrentVersion) <= 0)
            return UpdateDownloadResult.Fail("远端版本无效或不高于当前版本，不下载更新。");
        if (UpdateModePolicy.Normalize(initialSettings.UpdateMode) == UpdateMode.ChangedFiles)
        {
            try
            {
                if (manifest.FileUpdate is not null)
                {
                    var result = await DownloadFilesAsync(manifest, progress, cancellationToken).ConfigureAwait(false);
                    if (result is not null) return result;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var current = AppSettingsStore.Read();
                if (!UpdateModePolicy.SameSelection(current, initialSettings))
                    return UpdateDownloadResult.Fail("更新方式、通道或服务器已变化，请重新检查。");
                Log.Warn("更新：文件下载未完成：" + ex.Message);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!UpdateModePolicy.SameSelection(initialSettings, AppSettingsStore.Read()))
                return UpdateDownloadResult.Fail("更新方式、通道或服务器已变化，已停止自动回退。");
            return await DownloadFullFallbackAsync(manifest, progress, cancellationToken).ConfigureAwait(false);
        }
        return await DownloadFullAsync(manifest, new ForwardProgress(p => progress?.Report(new()
        { Stage = "完整安装包", BytesReceived = p.BytesReceived, TotalBytes = p.TotalBytes, BytesPerSecond = p.BytesPerSecond })), cancellationToken).ConfigureAwait(false);
    }

    private string? WorkerDirectory()
    {
        foreach (var relative in new[] { "Backends", "Backends/updater" })
        {
            var directory = Path.Combine(InstallDirectory, relative);
            if (File.Exists(Path.Combine(directory, "DeltaNFD.FileUpdater.exe"))
                && File.Exists(Path.Combine(directory, "DeploymentControl.ps1"))) return directory;
        }
        return null;
    }

    private async Task<UpdateDownloadResult?> DownloadFilesAsync(UpdateManifest release, IProgress<UpdateProgressInfo>? progress, CancellationToken token)
    {
        if (WorkerDirectory() is null) return null; // Older installations use the complete installer.
        var reference = release.FileUpdate!;
        var settings = AppSettingsStore.Read();
        var server = UpdateServerPolicy.Normalize(settings.UpdateServer);
        if (reference.SizeBytes is <= 0 or > FileUpdateRules.MaximumManifestBytes || !FileUpdateRules.IsHash(reference.Sha256)
            || !IsAllowedUrl(reference.Url, _config, out _) || !UpdateServerPolicy.Allows(server, reference.Url)) return null;
        // Product downloads are protected from the outset; the override is only for isolated tests.
        var cache = FileCache(reference.Sha256);
        Directory.CreateDirectory(cache);
        var manifestPath = Path.Combine(cache, "files.json");
        progress?.Report(new() { Stage = "正在读取文件清单" });
        if (!FileUpdateRules.Matches(manifestPath, reference.SizeBytes, reference.Sha256))
        {
            var temporary = manifestPath + ".part";
            try
            {
                await DownloadOneAsync(new() { Url = reference.Url, Sha256 = reference.Sha256, SizeBytes = reference.SizeBytes }, temporary, null, server, token).ConfigureAwait(false);
                if (!FileUpdateRules.Matches(temporary, reference.SizeBytes, reference.Sha256)) throw new InvalidDataException("文件清单校验失败。");
                File.Move(temporary, manifestPath, overwrite: true);
            }
            finally { TryDelete(temporary); }
        }
        var manifest = FileUpdateRules.Read(await File.ReadAllBytesAsync(manifestPath, token).ConfigureAwait(false), reference.Sha256);
        MatchFileRelease(release, manifest);
        progress?.Report(new() { Stage = "正在比较本地文件" });
        var changes = await Task.Run(() => FileTransaction.Changed(InstallDirectory, manifest, token), token).ConfigureAwait(false);
        var contents = changes.GroupBy(f => f.Sha256, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        if (contents.Any(f => !f.Urls.Any(u => IsAllowedUrl(u, _config, out _) && UpdateServerPolicy.Allows(server, u)))) return null;
        var total = contents.Sum(f => f.DownloadSize);
        var payload = Path.Combine(cache, "payload");
        Directory.CreateDirectory(payload);
        long completed = 0;
        foreach (var file in contents)
        {
            token.ThrowIfCancellationRequested();
            var raw = Path.Combine(payload, file.Sha256.ToLowerInvariant());
            if (FileUpdateRules.Matches(raw, file.SizeBytes, file.Sha256)) { completed += file.DownloadSize; continue; }
            var errors = new List<string>();
            var downloaded = false;
            foreach (var url in file.Urls.Where(u => IsAllowedUrl(u, _config, out _) && UpdateServerPolicy.Allows(server, u))
                .OrderBy(u => UpdateUrlRewriter.IsGitHubUrl(u) ? 0 : UpdateUrlRewriter.IsSelfHostedUrl(u) ? 1 : 2))
            {
                var packed = raw + ".packed";
                var temporary = raw + ".part";
                try
                {
                    await DownloadOneAsync(new() { Url = url, SizeBytes = file.DownloadSize, Sha256 = file.DownloadSha256 }, packed,
                        new ForwardProgress(p => progress?.Report(new() { Stage = $"仅更新 {changes.Count} 个文件", BytesReceived = completed + p.BytesReceived,
                            TotalBytes = total, BytesPerSecond = p.BytesPerSecond })), server, token).ConfigureAwait(false);
                    if (!FileUpdateRules.Matches(packed, file.DownloadSize, file.DownloadSha256)) throw new InvalidDataException("增量传输文件哈希不符。");
                    await InflateAsync(packed, temporary, file, token).ConfigureAwait(false);
                    if (!FileUpdateRules.Matches(temporary, file.SizeBytes, file.Sha256)) throw new InvalidDataException("增量解压内容哈希不符。");
                    File.Move(temporary, raw, overwrite: true);
                    downloaded = true;
                    break;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { errors.Add(ex.Message); }
                finally { TryDelete(packed); TryDelete(temporary); }
            }
            if (!downloaded) throw new IOException("文件下载失败：" + file.Path + "；" + string.Join("；", errors));
            completed += file.DownloadSize;
        }
        token.ThrowIfCancellationRequested();
        if (!UpdateModePolicy.SameSelection(settings, AppSettingsStore.Read())) throw new InvalidOperationException("更新方式、通道或服务器已变化，请重新检查。");
        FileTransaction.ValidatePayload(InstallDirectory, payload, manifest);
        progress?.Report(new() { Stage = "文件下载及校验完成", BytesReceived = total, TotalBytes = total });
        return new() { Success = true, IsIncremental = true, InstallerPath = manifestPath, Message = $"已核对全部文件，仅需替换 {changes.Count} 个文件。" };
    }

    private static void MatchFileRelease(UpdateManifest release, FileUpdateManifest manifest)
    {
        if (!TryParseVersion(release.Version, out var first) || !TryParseVersion(manifest.Version, out var second)
            || CompareVersions(first, second) != 0
            || UpdateChannelPolicy.Classify(release.Version, release.DisplayVersion, release.Channel)
                != UpdateChannelPolicy.Classify(manifest.Version, manifest.DisplayVersion, manifest.Channel))
            throw new InvalidDataException("文件清单版本或通道不一致。");
    }

    private static async Task InflateAsync(string packed, string raw, FileUpdateEntry file, CancellationToken token)
    {
        await using var input = File.OpenRead(packed);
        using Stream source = file.Compression == "gzip" ? new GZipStream(input, CompressionMode.Decompress, leaveOpen: true) : input;
        await using var output = new FileStream(raw, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, true);
        var buffer = new byte[BufferSize];
        long count = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            count += read;
            if (count > file.SizeBytes) throw new InvalidDataException("解压内容超过文件大小声明。");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        if (count != file.SizeBytes) throw new InvalidDataException("解压文件不完整。");
        await output.FlushAsync(token).ConfigureAwait(false);
    }

    private async Task<UpdateLaunchResult> LaunchFileUpdateAsync(UpdateManifest release, string manifestPath)
    {
        string? session = null;
        Process? process = null;
        var launchSettings = AppSettingsStore.Read();
        try
        {
            if (DescribeInstallBlocker() is { } blocker) throw new InvalidOperationException(blocker);
            if (UpdateModePolicy.Normalize(launchSettings.UpdateMode) != UpdateMode.ChangedFiles) throw new InvalidOperationException("更新方式已变化，请重新下载。");
            session = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
            ProtectedSessions.Validate(session);
            var latest = await CheckAsync(manual: true).ConfigureAwait(false);
            if (!latest.HasUpdate || latest.Manifest?.FileUpdate is not { } reference || release.FileUpdate is null
                || !reference.Sha256.Equals(release.FileUpdate.Sha256, StringComparison.OrdinalIgnoreCase)
                || !UpdateChannelPolicy.Allows(AppSettingsStore.Read().UpdateChannel, latest.Manifest)
                || !UpdateServerPolicy.Allows(AppSettingsStore.Read().UpdateServer, reference.Url))
                throw new InvalidDataException("无法确认文件更新仍为当前通道的最新版。");
            if (!FileUpdateRules.Matches(manifestPath, reference.SizeBytes, reference.Sha256))
                throw new InvalidDataException("已下载文件清单大小或哈希发生变化。");
            var manifest = FileUpdateRules.Read(await File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false), reference.Sha256);
            MatchFileRelease(latest.Manifest, manifest);
            var cachePayload = FileUpdateRules.Under(Path.GetDirectoryName(manifestPath)!, "payload");
            FileTransaction.ValidatePayload(InstallDirectory, cachePayload, manifest);
            var workerRoot = WorkerDirectory() ?? throw new IOException("此安装缺少独立更新器，请使用完整安装包。");
            var worker = Path.Combine(session, "worker");
            CopyTree(workerRoot, worker);
            var payload = Path.Combine(session, "payload");
            File.Copy(manifestPath, Path.Combine(session, "manifest.json"));
            FileUpdateRules.Read(File.ReadAllBytes(Path.Combine(session, "manifest.json")), reference.Sha256);
            FileTransaction.ValidatePayload(InstallDirectory, payload, manifest);
            FileUpdateRules.AtomicJson(Path.Combine(session, "full-fallback-intent.json"), new FileFallbackIntent
            {
                Root = InstallDirectory, ManifestHash = reference.Sha256,
                ReleaseJson = JsonSerializer.Serialize(latest.Manifest, JsonOpts),
                Mode = (int)UpdateModePolicy.Normalize(launchSettings.UpdateMode),
                Server = (int)UpdateServerPolicy.Normalize(launchSettings.UpdateServer),
                Channel = (int)UpdateChannelPolicy.Normalize(launchSettings.UpdateChannel),
            });
            var start = new ProcessStartInfo(Path.Combine(worker, "DeltaNFD.FileUpdater.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = worker, RedirectStandardError = true };
            foreach (var arg in new[] { "--apply", session, reference.Sha256, InstallDirectory }) start.ArgumentList.Add(arg);
            process = Process.Start(start) ?? throw new IOException("独立更新器未启动。");
            var workerErrors = process.StandardError.ReadToEndAsync();
            var status = Path.Combine(session, "status.json");
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(30))
            {
                if (File.Exists(status))
                {
                    var state = FileWorkerStatus.Read(status);
                    if (state.State == "Failed") throw new IOException(state.Detail);
                    if (state.State != "Ready") throw new IOException("独立更新器返回了意外状态：" + state.State);
                    var current = AppSettingsStore.Read();
                    if (!UpdateModePolicy.SameSelection(current, launchSettings))
                        throw new InvalidOperationException("更新方式、服务器或通道已变化，本次未批准替换。");
                    File.WriteAllText(Path.Combine(session, "apply-approved.txt"), reference.Sha256);
                    // Keep the caller busy (and source selectors disabled) throughout cooperative exit.
                    // Normally this process exits before the await returns. A refused handover is
                    // reported back to the still-running UI instead of leaving it locked indefinitely.
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(12)).ConfigureAwait(false);
                    var finished = FileWorkerStatus.Read(status);
                    if (process.ExitCode != 0 || finished.State != "Committed")
                        throw new IOException(FileWorkerStatus.Failure(finished, process.ExitCode, await workerErrors.ConfigureAwait(false)));
                    return new() { Success = true, Message = "文件更新已完成。", LogPath = status };
                }
                if (process.HasExited) throw new IOException(FileWorkerStatus.Failure(null, process.ExitCode, await workerErrors.ConfigureAwait(false)));
                await Task.Delay(100).ConfigureAwait(false);
            }
            throw new IOException("独立更新器尚未报告状态，请核对 " + session);
        }
        catch (Exception ex)
        {
            Log.Error("文件更新启动失败", ex);
            var safe = false;
            var detail = ex.Message;
            try
            {
                // No competing installer while a worker/coordinator might still be active. Never kill it.
                if (process is not null && !process.HasExited)
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35)).ConfigureAwait(false);
                RequireSafeFallback(session);
                safe = UpdateModePolicy.SameSelection(launchSettings, AppSettingsStore.Read()) && DescribeInstallBlocker() is null;
            }
            catch (Exception safetyError) { detail += "；自动回退已停止：" + safetyError.Message; }
            return new() { Success = false, CanFallbackToFull = safe,
                Message = "文件更新失败：" + detail + (safe ? "；将自动尝试全量更新。" : "。请核对保留的更新记录后重试。"), LogPath = session ?? "" };
        }
        finally { process?.Dispose(); }
    }

    private static void CopyTree(string source, string target)
    {
        FileUpdateRules.Under(source, "probe");
        Directory.CreateDirectory(target);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("更新器来源包含重解析点。");
            var destination = Path.Combine(target, Path.GetFileName(entry));
            if (Directory.Exists(entry)) CopyTree(entry, destination); else File.Copy(entry, destination);
        }
    }

    private sealed class ForwardProgress(Action<UpdateProgressInfo> report) : IProgress<UpdateProgressInfo>
    { public void Report(UpdateProgressInfo value) => report(value); }
}
