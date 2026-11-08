using DeltaNFD.Services;

namespace DeltaNFD.Execution.Procman;

/// <summary>
/// 进程管理后端持有的单/双 CCD 调度（原 CPU/帧格后端实现迁移至此）。
/// 实际锁核与异类策略联动仍由 <see cref="CpuTopologyService"/> 执行；本类负责：
/// 命令与维持 tick 串行、目标同步、按设置维持（游戏晚启动补锁、新进程补软锁），以及停止时撤销。
/// 维持循环只在调度生效或设置要求调度时运行，二者都不成立时自行退出，不新增常驻轮询。
/// </summary>
public sealed class ProcmanCcdScheduler : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);
    // 前端在命令成功后才写入对应设置；该窗口内 tick 不按设置撤销/补应用，避免与刚完成的命令互相覆盖。
    private static readonly TimeSpan CommandGrace = TimeSpan.FromSeconds(5);

    private readonly CpuTopologyService _cpu;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _loopGate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTime _lastCommandUtc = DateTime.MinValue;

    public ProcmanCcdScheduler() : this(new CpuTopologyService(TweakBackupStore.Default, new PowerService())) { }

    internal ProcmanCcdScheduler(CpuTopologyService cpu) => _cpu = cpu;

    public static bool DualActive => CpuTopologyService.DualCcdSchedulingActive;
    public static bool SingleActive => CpuTopologyService.SingleCcdExcludeActive;
    public static bool Active => DualActive || SingleActive;

    /// <summary>按设置判断应生效的调度（双 CCD 优先于单 CCD）。</summary>
    public static (bool Dual, bool Single) Wanted(AppSettings s)
    {
        var dual = s.DualCcdImmediateEnabled || (s.FrameModeActive && s.DualCcdArmed && s.DualCcdFrameEnabled);
        return (dual, !dual && s.SingleCcdExcludeCpu0Enabled);
    }

    /// <summary>设置要求单/双 CCD 调度接管目标游戏亲和性（供其他后端在自身进程内让位）。</summary>
    public static bool OwnsGameAffinity(AppSettings s)
    {
        var (dual, single) = Wanted(s);
        return dual || single;
    }

    public Task<OperationResult> ApplyDualAsync(int gameCcdIndex) =>
        CommandAsync(() => _cpu.ApplyDualCcdSchedulingAsync(gameCcdIndex));

    public Task<OperationResult> RevertDualAsync() => CommandAsync(_cpu.RevertDualCcdSchedulingAsync);

    public Task<OperationResult> ApplySingleAsync() => CommandAsync(_cpu.ExcludeCpu0FromGameAsync);

    public Task<OperationResult> RevertSingleAsync() => CommandAsync(_cpu.RestoreGameFullCoresAsync);

    /// <summary>停止维持并撤销生效中的调度（退出/全部还原/恢复流程）；未生效时不写任何系统状态。</summary>
    public async Task<OperationResult> RevertAllAsync()
    {
        await StopLoopAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!Active) return OperationResult.Ok("没有生效中的 CCD 调度。");
            var messages = new List<string>();
            if (DualActive) messages.Add("双CCD：" + (await _cpu.RevertDualCcdSchedulingAsync().ConfigureAwait(false)).Message);
            if (SingleActive) messages.Add("单CCD：" + (await _cpu.RestoreGameFullCoresAsync().ConfigureAwait(false)).Message);
            var text = string.Join("；", messages);
            return Active ? OperationResult.Fail("CCD 调度还原未完成：" + text) : OperationResult.Ok(text);
        }
        finally { _gate.Release(); }
    }

    /// <summary>后端启动或规则激活时：设置仍要求调度则启动维持循环（是否应用由 tick 按设置决定）。</summary>
    public void ResumeIfWanted()
    {
        if (OfflineModeGuard.BlocksNormalAutomation) return;
        var (dual, single) = Wanted(AppSettingsStore.Read());
        if (dual || single) EnsureLoop();
    }

    private async Task<OperationResult> CommandAsync(Func<Task<OperationResult>> operation)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await SyncTargetAsync().ConfigureAwait(false);
            var result = await operation().ConfigureAwait(false);
            _lastCommandUtc = DateTime.UtcNow;
            return result;
        }
        finally
        {
            _gate.Release();
            EnsureLoop();
        }
    }

    private void EnsureLoop()
    {
        lock (_loopGate)
        {
            if (_loop is { IsCompleted: false }) return;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => RunLoopAsync(token));
        }
    }

    private async Task StopLoopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_loopGate) { cts = _cts; loop = _loop; _cts = null; _loop = null; }
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        if (loop is not null) { try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        cts.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(TickInterval, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await _gate.WaitAsync(token).ConfigureAwait(false);
            bool keepRunning;
            try { keepRunning = await TickAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("CCD 调度维持 tick 异常：" + ex.Message); keepRunning = true; }
            finally { _gate.Release(); }
            if (keepRunning) continue;
            lock (_loopGate)
            {
                // 退出前在锁内复核：并发命令可能刚刚启用了调度。
                if (Active || OwnsGameAffinity(AppSettingsStore.Read())) continue;
                _loop = null;
                return;
            }
        }
    }

    /// <summary>一次维持：按设置撤销不再需要的、补应用需要的，再维持锁核。返回是否仍需继续循环。</summary>
    private async Task<bool> TickAsync()
    {
        if (OfflineModeGuard.BlocksNormalAutomation) return true;
        await SyncTargetAsync().ConfigureAwait(false);
        var settings = AppSettingsStore.Read();
        var (dualWanted, singleWanted) = Wanted(settings);
        if (DateTime.UtcNow - _lastCommandUtc >= CommandGrace)
        {
            if (!dualWanted && DualActive) await _cpu.RevertDualCcdSchedulingAsync().ConfigureAwait(false);
            if (!singleWanted && SingleActive) await _cpu.RestoreGameFullCoresAsync().ConfigureAwait(false);
            if (dualWanted && !DualActive) await _cpu.ApplyDualCcdSchedulingAsync(settings.DualCcdGameCcdIndex).ConfigureAwait(false);
            else if (singleWanted && !SingleActive) await _cpu.ExcludeCpu0FromGameAsync().ConfigureAwait(false);
        }
        CpuTopologyService.ApplyCpuSchedulingTick();
        return Active || dualWanted || singleWanted;
    }

    /// <summary>目标代次在本后端进程内独立维护：操作前按共享设置同步（与 CPU/帧格后端同一规则）。</summary>
    private static async Task SyncTargetAsync()
    {
        try
        {
            var settings = AppSettingsStore.Read();
            var current = GameTargetService.Default.Current;
            var wantPath = settings.CustomGameModeEnabled ? settings.CustomGameExecutablePath ?? "" : "";
            if (current.IsCustom == settings.CustomGameModeEnabled && SamePath(current.ExecutablePath, wantPath)) return;
            var result = await GameTargetService.Default.ChangeAsync(settings.CustomGameModeEnabled,
                settings.CustomGameModeEnabled ? wantPath : null).ConfigureAwait(false);
            if (!result.Success) Log.Warn("进程管理后端：CCD 调度目标同步被拒绝：" + result.Message);
        }
        catch (Exception ex) { Log.Warn("进程管理后端：CCD 调度目标同步失败：" + ex.Message); }
    }

    private static bool SamePath(string left, string right)
    {
        if (string.IsNullOrEmpty(left) && string.IsNullOrEmpty(right)) return true;
        try
        {
            return Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public void Dispose()
    {
        StopLoopAsync().GetAwaiter().GetResult();
        _gate.Dispose();
    }
}
