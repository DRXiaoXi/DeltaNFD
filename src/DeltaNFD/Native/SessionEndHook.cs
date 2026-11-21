using System.Runtime.InteropServices;
using DeltaNFD.Services;

namespace DeltaNFD.Native;

/// <summary>Handle Windows logoff/shutdown while the frontend can still drain its workers.</summary>
internal sealed class SessionEndHook : IDisposable
{
    private const uint QueryEndSession = 0x0011, EndSession = 0x0016, NcDestroy = 0x0082;
    private const nuint SubclassId = 0xD31FA11;
    private const uint EndSessionCritical = 0x40000000, EndSessionLogoff = 0x80000000, EndSessionCloseApp = 1;
    private static readonly TimeSpan QueryBudget = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(20);
    private readonly Func<bool> _needsWait;
    private long? _queryStarted;
    private bool _reasonRegistered;
    private bool _waitingForFrame;
    private readonly SessionEndDrain _drain;
    private readonly Action<bool> _ended;
    private readonly SubclassProc _callback;
    private nint _window;

    internal SessionEndHook(nint window, SessionEndDrain drain, Action<bool> ended, Func<bool>? needsWait = null)
    {
        _drain = drain;
        _ended = ended;
        _needsWait = needsWait ?? (() => false);
        _callback = WindowProc;
        if (!SetWindowSubclass(window, _callback, SubclassId, 0))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法登记 Windows 会话收尾通知。");
        _window = window;
        ProcessShutdownOrder.Configure(frontend: true);
    }

    private nint WindowProc(nint window, uint message, nint wparam, nint lparam, nuint id, nuint data)
    {
        try
        {
            if (message == QueryEndSession)
            {
                Log.Info($"Windows 会话即将结束：flags=0x{unchecked((nuint)lparam):X}；先停止新操作并还原后端。");
                RegisterReason(window, lparam);
                _queryStarted ??= System.Diagnostics.Stopwatch.GetTimestamp();
                _drain.Query(Remaining(QueryBudget));
                // Never veto or reissue shutdown; WAIT is on the same drain in both messages.
                return 1;
            }
            if (message == EndSession)
            {
                if (wparam == 0)
                {
                    ClearReason(window);
                    _queryStarted = null;
                    _waitingForFrame = false;
                    _drain.Cancel();
                    Log.Info("Windows 已取消会话结束；等待当前收尾完成后恢复接收操作，不自动重放已停止的帧格请求。");
                }
                else
                {
                    RegisterReason(window, lparam);
                    _queryStarted ??= System.Diagnostics.Stopwatch.GetTimestamp();
                    // Keep the window in Windows' shutdown progress until confirmed, up to a single total budget.
                    var budget = IsNormalShutdown(lparam) && _waitingForFrame ? ShutdownBudget : QueryBudget;
                    if (!_drain.Succeeded) _drain.Query(Remaining(budget));
                    ClearReason(window);
                    _ended(_drain.Succeeded);
                }
                return 0;
            }
            if (message == NcDestroy) Dispose();
        }
        catch (Exception ex) { Log.Error("Windows 会话收尾通知处理失败，保留核对保护", ex); }
        return DefSubclassProc(window, message, wparam, lparam);
    }

    private TimeSpan Remaining(TimeSpan budget)
    {
        var remaining = budget - System.Diagnostics.Stopwatch.GetElapsedTime(_queryStarted!.Value);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    internal static bool IsNormalShutdown(nint flags) =>
        (unchecked((uint)flags) & (EndSessionCritical | EndSessionLogoff | EndSessionCloseApp)) == 0;

    private void RegisterReason(nint window, nint flags)
    {
        if (!IsNormalShutdown(flags)) return;
        _waitingForFrame |= _needsWait();
        if (!_reasonRegistered && _waitingForFrame)
            _reasonRegistered = ShutdownBlockReasonCreate(window, "正在停止帧格并恢复临时设置，请稍候。");
    }

    private void ClearReason(nint window)
    {
        if (!_reasonRegistered) return;
        ShutdownBlockReasonDestroy(window);
        _reasonRegistered = false;
    }

    public void Dispose()
    {
        var window = Interlocked.Exchange(ref _window, 0);
        if (window != 0)
        {
            ClearReason(window);
            RemoveWindowSubclass(window, _callback, SubclassId);
        }
        _drain.Dispose();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonCreate(nint window, string reason);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonDestroy(nint window);

    private delegate nint SubclassProc(nint window, uint message, nint wparam, nint lparam, nuint id, nuint data);
    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint window, uint message, nint wparam, nint lparam);
}
