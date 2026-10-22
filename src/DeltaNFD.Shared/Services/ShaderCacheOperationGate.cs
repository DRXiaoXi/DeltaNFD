namespace DeltaNFD.Services;

/// <summary>Cross-process shader warm/cleanup exclusion. Mutex ownership remains on one dedicated thread.</summary>
public sealed class ShaderCacheOperationGate : IDisposable
{
    public const string MutexName = @"Local\DeltaNFD.OperationGate.ShaderWarm";
    private readonly ManualResetEventSlim _release = new(false);
    private readonly Thread _owner;
    private int _disposed;

    private ShaderCacheOperationGate()
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _owner = new Thread(() =>
        {
            try
            {
                using var mutex = new Mutex(false, MutexName);
                bool held;
                try { held = mutex.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
                ready.TrySetResult(held);
                if (!held) return;
                try { _release.Wait(); } finally { mutex.ReleaseMutex(); }
            }
            catch (Exception ex) { ready.TrySetException(ex); }
        }) { IsBackground = true, Name = "Shader cache operation gate" };
        _owner.Start();
        try
        {
            if (!ready.Task.Wait(TimeSpan.FromSeconds(5)) || !ready.Task.GetAwaiter().GetResult())
                throw new InvalidOperationException("着色器预热或缓存清理正在运行，请等待完成后再试。");
        }
        catch { Dispose(); throw; }
    }

    public static ShaderCacheOperationGate Acquire() => new();
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _release.Set();
        if (_owner.Join(TimeSpan.FromSeconds(2))) _release.Dispose();
    }
}
