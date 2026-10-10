namespace DeltaNFD.Services;

/// <summary>
/// 跨主程序和脱机助手串行化“应用 / 还原”阶段。独占文件句柄随进程退出自动释放，
/// 可安全跨 await 使用；等待游戏期间不持锁。
/// </summary>
public sealed class OfflineModeExecutionLock : IDisposable
{
    private readonly FileStream _stream;
    private OfflineModeExecutionLock(FileStream stream) => _stream = stream;

    public static bool TryAcquire(OfflineModeStateStore store, out OfflineModeExecutionLock? lease, out string error) =>
        TryAcquireNamed(store, "offline-operation.lock", out lease, out error);

    private static bool TryAcquireNamed(OfflineModeStateStore store, string fileName, out OfflineModeExecutionLock? lease, out string error)
    {
        lease = null;
        error = "";
        try
        {
            var directory = Path.GetDirectoryName(store.PathName);
            if (string.IsNullOrWhiteSpace(directory)) { error = "脱机状态目录无效。"; return false; }
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, fileName);
            lease = new OfflineModeExecutionLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.WriteThrough));
            return true;
        }
        catch (IOException)
        {
            error = "另一个脱机操作正在应用或还原 CPU Sets。";
            return false;
        }
        catch (Exception ex)
        {
            error = "无法取得脱机操作锁：" + ex.Message;
            return false;
        }
    }

    public static async Task<OfflineModeExecutionLock?> WaitAcquireAsync(
        OfflineModeStateStore store, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        await WaitAcquireNamedAsync(store, "offline-operation.lock", timeout, cancellationToken);

    public static Task<OfflineModeExecutionLock?> WaitControlAsync(
        OfflineModeStateStore store, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        WaitAcquireNamedAsync(store, "offline-control.lock", timeout, cancellationToken);

    private static async Task<OfflineModeExecutionLock?> WaitAcquireNamedAsync(
        OfflineModeStateStore store, string fileName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryAcquireNamed(store, fileName, out var lease, out _)) return lease;
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>短时脱机助手的单实例文件租约；进程退出时由 OS 自动释放。</summary>
public sealed class OfflineModeHelperInstanceLease : IDisposable
{
    private readonly FileStream _stream;
    private OfflineModeHelperInstanceLease(FileStream stream) => _stream = stream;

    public static OfflineModeHelperInstanceLease? TryAcquire(OfflineModeStateStore store, out string error)
    {
        error = "";
        try
        {
            var directory = Path.GetDirectoryName(store.PathName);
            if (string.IsNullOrWhiteSpace(directory)) { error = "脱机状态目录无效。"; return null; }
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "offline-helper.instance.lock");
            return new OfflineModeHelperInstanceLease(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.WriteThrough));
        }
        catch (IOException) { error = "脱机助手已有实例正在运行。"; return null; }
        catch (Exception ex) { error = "无法取得脱机助手单实例锁：" + ex.Message; return null; }
    }

    public void Dispose() => _stream.Dispose();
}
