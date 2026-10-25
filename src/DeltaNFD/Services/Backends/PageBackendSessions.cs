using System.Collections.Concurrent;
using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;

internal sealed class PageBackendUnavailableException(string message) : InvalidOperationException(message);

internal interface IPageBackendProcess
{
    string ModuleId { get; }
    bool ExitConfirmed { get; }
    bool RequiresAttention { get; }
    Task StopForPageAsync();
}

/// <summary>A page owns admission, not the lifetime of an already submitted system transaction.</summary>
internal sealed class PageBackendLease(string route)
{
    private readonly object _gate = new();
    private readonly HashSet<IPageBackendProcess> _processes = [];
    private readonly Dictionary<Type, object> _services = [];
    private TaskCompletionSource _idle = Completed();
    private Task _stopping = Task.CompletedTask;
    private CancellationTokenSource _queued = new();
    private int _active;
    private bool _left;
    private bool _stopFailed;
    private string _suspension = "";
    public string Route { get; } = route;
    public Guid Id { get; } = Guid.NewGuid();
    public string Status { get; private set; } = "";
    public event Action? Changed;
    internal CancellationToken QueueToken { get { lock (_gate) return _queued.Token; } }
    internal Task Stopping { get { lock (_gate) return _stopping; } }
    internal bool IsAvailable { get { lock (_gate) return !_left && !_stopFailed && _suspension.Length == 0; } }
    private static TaskCompletionSource Completed() { var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); t.SetResult(); return t; }

    internal T Service<T>(Func<T> factory) where T : class
    {
        lock (_gate)
        {
            if (_services.TryGetValue(typeof(T), out var value)) return (T)value;
            using var constructing = PageBackendSessions.ConstructFor(this);
            var created = factory(); _services.Add(typeof(T), created); return created;
        }
    }

    internal async Task<IDisposable> BeginStartAsync(CancellationToken token)
    {
        while (true)
        {
            Task stopping;
            lock (_gate)
            {
                Check(); stopping = _stopping;
                if (stopping.IsCompleted)
                {
                    // A fresh queue token is needed after a game suspension has fully drained.
                    if (_queued.IsCancellationRequested) _queued = new();
                    return AdmitLocked();
                }
            }
            await stopping.WaitAsync(token);
        }
    }

    internal IDisposable BeginCommand(IPageBackendProcess process)
    {
        lock (_gate)
        {
            Check();
            if (!_stopping.IsCompleted || !_processes.Contains(process))
                throw new PageBackendUnavailableException("栏目后端正在收尾，请等待退出确认后重试。");
            return AdmitLocked();
        }
    }

    private IDisposable AdmitLocked()
    {
        if (_active++ == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return new Admission(this);
    }
    private sealed class Admission(PageBackendLease owner) : IDisposable
    {
        private PageBackendLease? _owner = owner;
        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _owner, null); if (value is null) return;
            lock (value._gate) if (--value._active == 0) value._idle.TrySetResult();
        }
    }
    private void Check()
    {
        if (_left) throw new PageBackendUnavailableException("已离开栏目，未执行新的后台操作。");
        if (_suspension.Length > 0) throw new PageBackendUnavailableException(_suspension);
        if (_stopFailed) throw new PageBackendUnavailableException("上次栏目后端未能确认退出，请先核对未确认操作。");
    }
    internal void Attach(IPageBackendProcess process) { lock (_gate) _processes.Add(process); }
    internal void RefreshReviewedExit()
    {
        lock (_gate)
        {
            if (!_stopFailed || !_stopping.IsCompleted || _processes.Any(p => !p.ExitConfirmed || p.RequiresAttention)) return;
            _stopFailed = false; _processes.Clear(); Status = "栏目后端已关闭";
        }
        if (_left) BackendCaller.ReleasePage(Id);
    }

    internal void Suspend(string reason, bool leaving = false)
    {
        lock (_gate)
        {
            _left |= leaving; _suspension = reason;
            _queued.Cancel();
            Status = _active > 0 ? "当前任务结束后关闭后端" : "正在关闭栏目后端";
            if (_stopping.IsCompleted) _stopping = Task.Run(StopAsync);
            PageBackendSessions.NoteDrain(this, _stopping);
        }
        NotifyChanged();
    }
    internal void Resume()
    {
        lock (_gate) { if (_left) return; _suspension = ""; }
        NotifyChanged(); // Opening the page again or an explicit action starts a worker; resuming never does.
    }
    private async Task StopAsync()
    {
        Task idle; lock (_gate) idle = _idle.Task;
        await idle;
        IPageBackendProcess[] processes; lock (_gate) processes = _processes.ToArray();
        var failures = new List<string>();
        foreach (var process in processes)
        {
            try
            {
                var moduleGate = PageBackendSessions.ModuleGate(process.ModuleId);
                await moduleGate.WaitAsync();
                try { await process.StopForPageAsync(); } finally { moduleGate.Release(); }
                if (!process.ExitConfirmed || process.RequiresAttention)
                { failures.Add(process.ModuleId); PageBackendSessions.RetainFailedStop(process); }
            }
            catch (Exception ex) { failures.Add(process.ModuleId); PageBackendSessions.RetainFailedStop(process); Log.Warn("栏目后端退出未确认：" + process.ModuleId + "；" + ex.Message); }
        }
        lock (_gate)
        {
            _processes.RemoveWhere(p => p.ExitConfirmed && !p.RequiresAttention);
            _stopFailed = failures.Count > 0;
            Status = failures.Count == 0 ? "栏目后端已关闭" : "后端退出或结果待核对：" + string.Join("、", failures.Distinct());
        }
        NotifyChanged();
        if (_left && failures.Count == 0) BackendCaller.ReleasePage(Id);
    }
    private void NotifyChanged() => PageBackendSessions.Notify(Changed);
}

internal interface IPageBackendSessionManager
{
    PageBackendLease? Current { get; }
    void Navigate(string route);
    void ObserveDelta(bool? running);
}

/// <summary>Shared binaries, separate owners. Backend identity, recovery records and stop protocol stay intact.</summary>
internal sealed class PageBackendSessionManager : IPageBackendSessionManager
{
    public PageBackendLease? Current { get; private set; }
    public bool? DeltaRunning { get; private set; }
    public event Action? Changed;
    internal static readonly HashSet<string> Restricted = ["system", "spoof", "shader", "ace", "runtime"];
    public void Navigate(string route)
    {
        Current?.Suspend("已离开栏目。", leaving: true);
        Current = Restricted.Contains(route) || route == "nvsettings" ? new PageBackendLease(route) : null;
        if (Current is not null) { Current.Changed += NotifyChanged; ApplyState(); }
        NotifyChanged();
    }
    public void ObserveDelta(bool? running)
    {
        if (DeltaRunning == running) return;
        DeltaRunning = running; ApplyState(); NotifyChanged();
    }
    private void NotifyChanged() => PageBackendSessions.Notify(Changed);
    private void ApplyState()
    {
        var current = Current;
        if (current is null || !Restricted.Contains(current.Route)) return;
        if (DeltaRunning == false) current.Resume();
        else current.Suspend(DeltaRunning == true ? "三角洲正在运行，请完全退出游戏后执行此操作。" : "无法确认三角洲运行状态，请等待监控恢复后重试。");
    }
}

internal static class PageBackendSessions
{
    internal static readonly PageBackendSessionManager Manager = new();
    private static readonly ConcurrentDictionary<IPageBackendProcess, byte> FailedStops = new();
    internal static void Notify(Action? changed)
    {
        foreach (Action handler in changed?.GetInvocationList() ?? [])
            try { handler(); } catch (Exception ex) { Log.Warn("栏目会话状态订阅失败：" + ex.Message); }
    }
    internal static void RetainFailedStop(IPageBackendProcess process) => FailedStops.TryAdd(process, 0);
    internal static void CheckBeforeStart(string module, PageBackendLease? owner)
    {
        foreach (var process in FailedStops.Keys)
        {
            if (process.ExitConfirmed && !process.RequiresAttention) { FailedStops.TryRemove(process, out _); continue; }
            if (process.ModuleId == module && (!process.ExitConfirmed || owner is not null))
                throw new PageBackendUnavailableException("上次栏目后端退出或结果尚未确认，请在设置中核对未确认操作；未启动第二份后端。");
        }
        if (owner is not null && BackendUnconfirmedStore.PendingModules().Contains(module))
            throw new PageBackendUnavailableException("该模块有待核对记录，请先在设置中核对与恢复；未自动启动栏目后端。");
    }
    private static readonly AsyncLocal<PageBackendLease?> Constructing = new();
    internal static PageBackendLease? ConstructionOwner => Constructing.Value;
    internal static void RecheckPage(PageBackendLease? owner, Func<bool>? read = null)
    {
        if (owner is null || !owner.IsAvailable || !PageBackendSessionManager.Restricted.Contains(owner.Route)) return;
        bool? running;
        try { running = (read ?? DeltaForceLocator.IsGameRunning)(); }
        catch (Exception ex) { running = null; Log.Warn("三角洲运行状态复核失败：" + ex.Message); }
        if (running == false) return;
        owner.Suspend(running == true ? "三角洲正在运行，栏目操作未执行。" : "无法确认三角洲运行状态，栏目操作未执行。");
        Manager.ObserveDelta(running);
        throw new PageBackendUnavailableException(running == true ? "请完全退出三角洲后再执行此操作。" : "游戏运行状态无法确认，未执行操作。");
    }
    internal static bool? ReadDeltaObservation(CpuFrameStatusSnapshot snapshot, DateTimeOffset now) =>
        GameObservationState.ReadDelta(snapshot, now);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ModuleGates = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<(string Module, Guid Owner), Task> Drains = new();
    internal static SemaphoreSlim ModuleGate(string module) => ModuleGates.GetOrAdd(module, _ => new(1, 1));
    internal static void NoteDrain(PageBackendLease owner, Task drain)
    {
        var modules = owner.Route switch { "shader" => new[] { BackendModules.System, BackendModules.Gpu },
            "system" => [BackendModules.System], "runtime" => [BackendModules.Runtime], "ace" => [BackendModules.Ace, BackendModules.System], _ => [BackendModules.Gpu] };
        foreach (var module in modules) Drains[(module, owner.Id)] = drain;
    }
    private static Task[] PendingDrains(string module, PageBackendLease? owner)
    {
        foreach (var item in Drains.Where(p => p.Value.IsCompleted)) Drains.TryRemove(item.Key, out _);
        return Drains.Where(p => p.Key.Module == module && p.Key.Owner != owner?.Id).Select(p => p.Value).ToArray();
    }
    internal static async Task WaitGateAsync(SemaphoreSlim gate, PageBackendLease? owner, CancellationToken token, string? module = null)
    {
        using var waiting = owner is null ? null : await owner.BeginStartAsync(token);
        using var queued = CancellationTokenSource.CreateLinkedTokenSource(token, owner?.QueueToken ?? CancellationToken.None);
        try
        {
            while (true)
            {
                if (module is not null) await Task.WhenAll(PendingDrains(module, owner)).WaitAsync(queued.Token);
                await gate.WaitAsync(queued.Token);
                if (module is null || PendingDrains(module, owner).Length == 0) return;
                gate.Release();
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new PageBackendUnavailableException("栏目已离开或游戏状态变化，排队操作未执行。"); }
    }
    internal static IDisposable ConstructFor(PageBackendLease? owner)
    {
        var old = Constructing.Value; Constructing.Value = owner; return new Restore(() => Constructing.Value = old);
    }
    private sealed class Restore(Action restore) : IDisposable { public void Dispose() => restore(); }
    internal static T Service<T>(Lazy<T> fallback, Func<T> factory, params string[] routes) where T : class
    {
        var page = Manager.Current;
        if (page is not null && routes.Contains(page.Route)) return page.Service(factory);
        using var unowned = ConstructFor(null); return fallback.Value;
    }
    internal static string? SystemWriteBlocker => Manager.DeltaRunning == false ? null
        : Manager.DeltaRunning == true ? "三角洲正在运行，系统优化仅供查看；请退出游戏后执行修改、还原或清理。"
        : "无法确认三角洲运行状态，系统优化暂时仅供查看。";
    internal static async Task FinishUnownedAsync(BackendClient? client, PageBackendLease? owner)
    {
        if (owner is not null || client is null || client.RequiresAttention) return;
        if (client.ModuleId is not (BackendModules.System or BackendModules.Gpu or BackendModules.Runtime or BackendModules.Ace)) return;
        await client.QuiesceAsync(BackendSessions.StopTimeoutFor(client.ModuleId));
    }
}
