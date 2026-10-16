namespace DeltaNFD.Services;

internal static class GpuSpoofPendingRecovery
{
    internal sealed record Preview(GpuSpoofRestoreState.Snapshot Pending, string DeviceDesc, string Name);

    internal static async Task<Preview?> PreviewAsync(IGpuSpoofService service, string? settingsPath = null)
    {
        var pending = GpuSpoofRestoreState.Capture(AppSettingsStore.ReadRequired(settingsPath));
        if (!pending.Pending) return null;
        var adapter = (await service.GetAdaptersAsync()).SingleOrDefault(a => a.RegistryPath.Equals(pending.Path, StringComparison.OrdinalIgnoreCase));
        if (!GpuSpoofRestoreState.CanReconcile(pending, adapter))
            throw new IOException("尚不能确认目标已恢复：显卡不存在、仍有伪装/备份或无法核对原生驱动描述。请先恢复原始型号；未清除待还原记录。");
        return new(pending, adapter!.DeviceDesc, adapter.DisplayName);
    }

    internal static async Task CompleteAsync(IGpuSpoofService service, Preview preview, string? settingsPath = null)
    {
        var current = await PreviewAsync(service, settingsPath);
        if (current is null) return;
        if (!GpuSpoofRestoreState.Same(current.Pending, preview.Pending) || current.DeviceDesc != preview.DeviceDesc)
            throw new IOException("待还原任务或显卡描述已经变化，请重新核对；未清除记录。");
        GpuSpoofRestoreState.Complete(preview.Pending, preview.Pending.Path, settingsPath);
    }
}
