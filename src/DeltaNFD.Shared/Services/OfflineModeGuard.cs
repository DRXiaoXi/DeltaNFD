namespace DeltaNFD.Services;

/// <summary>普通模式自动化的 fail-closed 闸门；状态文件损坏时不自动恢复轮询。</summary>
public static class OfflineModeGuard
{
    public static bool BlocksNormalAutomation
    {
        get
        {
            try
            {
                return !OfflineModeStateStore.Default.TryRead(out var state, out _) || state.BlocksNormalAutomation;
            }
            catch { return true; }
        }
    }

    public static bool IsEnabled
    {
        get
        {
            try { return OfflineModeStateStore.Default.TryRead(out var state, out _) && state.OfflineModeEnabled; }
            catch { return false; }
        }
    }
}
