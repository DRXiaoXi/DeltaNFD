using System.Diagnostics;
using System.Text.Json;
using DeltaNFD.Updates;

namespace DeltaNFD.Services;

public sealed partial class UpdateService
{
    private sealed record FallbackPermit(string Path, string Version, string Channel,
        string[] Identities, AppSettings Selection);
    private FallbackPermit? _fullFallbackPermit;

    private void ClearFullFallbackPermit() => _fullFallbackPermit = null;

    internal bool HasFullFallbackPermit(UpdateManifest manifest, string path, AppSettings selection)
    {
        var permit = _fullFallbackPermit;
        return permit is not null && Path.GetFullPath(path).Equals(permit.Path, StringComparison.OrdinalIgnoreCase)
            && permit.Version == manifest.Version && permit.Channel == manifest.Channel
            && UpdateModePolicy.SameSelection(selection, permit.Selection)
            && (manifest.Installers ?? []).Any(i => IsInstallerUsable(i, _config, out _)
                && UpdateServerPolicy.Allows(selection.UpdateServer, i.Url)
                && permit.Identities.Contains(InstallerIdentity(i), StringComparer.OrdinalIgnoreCase));
    }

    private static string InstallerIdentity(UpdateInstallerInfo installer) => installer.SizeBytes + ":" + installer.Sha256;

    public async Task<UpdateDownloadResult> DownloadFullFallbackAsync(UpdateManifest manifest,
        IProgress<UpdateProgressInfo>? progress = null, CancellationToken cancellationToken = default)
    {
        var selection = AppSettingsStore.Read();
        ClearFullFallbackPermit();
        cancellationToken.ThrowIfCancellationRequested();
        try { RequireSafeFallback(null); }
        catch (Exception ex) { return UpdateDownloadResult.Fail("自动回退已停止：" + ex.Message); }
        Log.Warn("更新：文件更新失败，正在按当前服务器选择回退完整安装包。");
        progress?.Report(new() { Stage = "文件更新失败，正在回退全量更新" });
        var result = await DownloadFullAsync(manifest, new ForwardProgress(p => progress?.Report(new()
        { Stage = "文件更新失败，正在回退全量更新", BytesReceived = p.BytesReceived,
            TotalBytes = p.TotalBytes, BytesPerSecond = p.BytesPerSecond })), cancellationToken, fallback: true).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!UpdateModePolicy.SameSelection(selection, AppSettingsStore.Read()))
            return UpdateDownloadResult.Fail("更新方式、服务器或通道已变化，已停止自动回退。");
        if (!result.Success) return UpdateDownloadResult.Fail("文件更新失败，全量回退也未完成：" + result.Message);
        _fullFallbackPermit = new(Path.GetFullPath(result.InstallerPath), manifest.Version, manifest.Channel,
            (manifest.Installers ?? []).Select(InstallerIdentity).ToArray(), selection);
        return UpdateDownloadResult.Ok(result.InstallerPath, "已自动回退全量更新。" + result.Message);
    }

    private void RequireSafeFallback(string? session)
    {
        // Opening (without owning) the existing gate also catches workers that have no journal yet.
        if (Mutex.TryOpenExisting(@"Local\DeltaNFD_7A3E9C4D_Deployment", out var deployment))
        {
            deployment.Dispose();
            throw new IOException("独立更新器或安装器尚未退出，暂不能启动全量回退。");
        }
        if (session is not null)
        {
            ProtectedSessions.Validate(session);
            FileUpdateFallback.RequireSafeTransaction(session, InstallDirectory);
        }
        var active = FileUpdateFallback.ActivePath(InstallDirectory);
        if (!File.Exists(active)) return;
        var previous = JsonSerializer.Deserialize<string>(File.ReadAllBytes(active))
            ?? throw new InvalidDataException("上次文件更新会话未知。");
        ProtectedSessions.Validate(previous);
        var journalPath = FileUpdateRules.Under(previous, "transaction/journal.json");
        if (File.Exists(journalPath))
        {
            var journal = JsonSerializer.Deserialize<FileJournal>(File.ReadAllBytes(journalPath), FileUpdateRules.Json);
            if (journal?.State == "Committed" && FileUpdateFallback.SameRoot(journal.Root, InstallDirectory)) return;
        }
        FileUpdateFallback.RequireSafeTransaction(previous, InstallDirectory);
    }

    public async Task<UpdateManifest> ResumeFullFallbackAsync(string session, string nonce, CancellationToken cancellationToken = default)
    {
        ProtectedSessions.Validate(session);
        if (!IsInstalledCopy) throw new InvalidOperationException("当前不是登记的安装版本，不能自动续接更新。");
        var intent = FileUpdateFallback.ReadIntent(session, InstallDirectory, nonce);
        var ready = FileUpdateFallback.Claim(session, nonce);
        // The worker releases its deployment gate before starting us. It must also exit before full setup.
        Process? worker = null;
        try { worker = Process.GetProcessById(ready.WorkerPid); } catch (ArgumentException) { }
        using (worker)
        {
            if (worker is not null && !worker.HasExited && worker.StartTime.ToUniversalTime().Ticks == ready.WorkerStartUtcTicks)
                await worker.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
        RequireSafeFallback(session);
        var selection = AppSettingsStore.Read();
        if (intent.Mode != (int)UpdateModePolicy.Normalize(selection.UpdateMode)
            || intent.Server != (int)UpdateServerPolicy.Normalize(selection.UpdateServer)
            || intent.Channel != (int)UpdateChannelPolicy.Normalize(selection.UpdateChannel))
            throw new InvalidOperationException("更新选择已变化，已停止自动回退，请重新检查。");
        var release = JsonSerializer.Deserialize<UpdateManifest>(intent.ReleaseJson, JsonOpts)
            ?? throw new InvalidDataException("未保存完整更新版本信息。");
        if (release.FileUpdate?.Sha256 != intent.ManifestHash) throw new InvalidDataException("回退记录与文件更新身份不一致。");
        var latest = await CheckAsync(manual: true, cancellationToken).ConfigureAwait(false);
        if (!latest.HasUpdate || latest.Manifest is not { } current || current.Version != release.Version
            || current.Channel != release.Channel || !UpdateModePolicy.SameSelection(selection, AppSettingsStore.Read())
            || !(current.Installers ?? []).Any(i => (release.Installers ?? []).Any(old => SameInstaller(old, i))))
            throw new InvalidDataException("无法确认原更新的完整安装包仍可用，请重新检查。");
        Log.Warn("更新：替换失败后已确认回滚，续接一次全量更新：" + ready.Reason);
        return current;
    }
}
