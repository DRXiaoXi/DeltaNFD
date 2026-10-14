using System.Text.Json;
using Microsoft.Win32;

namespace DeltaNFD.Services;

internal sealed record UpdateChannelInstallationInfo(string Id, string DisplayVersion, string AlphaInstallationId);

internal static class UpdateChannelInstallation
{
    internal static void ApplyCurrentInstallation()
    {
        try
        {
            using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var installed = registry.OpenSubKey(UpdateService.UninstallSubKey);
            if (installed is null || !UpdateService.IsInstalledCopyAt(installed.GetValue("InstallLocation") as string, AppContext.BaseDirectory)) return;
            var id = installed.GetValue("UpdateChannelInstallationId") as string ?? "";
            var display = installed.GetValue("UpdateChannelInstallationVersion") as string ?? "";
            var alphaId = installed.GetValue("UpdateChannelAlphaInstallationId") as string ?? "";
            if (!display.Equals(AppVersion.Text, StringComparison.OrdinalIgnoreCase)) return;
            Apply(AppSettingsStore.DefaultPath, new(id, display, alphaId));
        }
        catch (Exception ex) { Log.Warn("更新通道：安装默认值未能应用，下次启动将重试：" + ex.Message); }
    }

    internal static void Apply(string settingsPath, UpdateChannelInstallationInfo installation)
    {
        var kind = UpdateChannelPolicy.Prefix(installation.DisplayVersion);
        if (kind == UpdateReleaseKind.Unknown || string.IsNullOrWhiteSpace(installation.Id) || installation.Id.Length > 4096
            || installation.AlphaInstallationId.Length > 4096) return;
        if (AppSettingsStore.Read(settingsPath).UpdateChannelInstallationId == installation.Id) return;
        AppSettingsStore.Update(settingsPath, settings =>
        {
            var existing = File.Exists(settingsPath);
            if (existing)
            {
                // Do not replace a corrupt settings file with defaults during installation initialization.
                using var stream = File.OpenRead(settingsPath);
                _ = JsonSerializer.Deserialize<AppSettings>(stream) ?? throw new InvalidDataException("设置文件为空。");
            }
            if (settings.UpdateChannelInstallationId == installation.Id) return;
            var pendingAlpha = existing && installation.AlphaInstallationId.Length != 0
                && installation.AlphaInstallationId != settings.UpdateChannelAlphaInstallationId;
            if (kind == UpdateReleaseKind.Local)
            {
                // Local builds preserve an existing channel; a fresh development installation defaults internal.
                if (!existing) settings.UpdateChannel = UpdateChannel.Internal;
            }
            else if (kind == UpdateReleaseKind.Alpha || pendingAlpha)
                settings.UpdateChannel = UpdateChannel.Internal;
            else if (!existing)
                settings.UpdateChannel = UpdateChannel.Public; // Fresh Beta/Release; reinstall preserves an existing selection.
            settings.UpdateChannelInstallationId = installation.Id;
            settings.UpdateChannelAlphaInstallationId = installation.AlphaInstallationId;
        });
        if (AppSettingsStore.Read(settingsPath).UpdateChannelInstallationId != installation.Id)
            throw new IOException("安装通道设置未保存，未标记为已处理。");
    }
}
