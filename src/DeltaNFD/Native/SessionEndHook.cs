using System.Runtime.InteropServices;
using DeltaNFD.Services;

namespace DeltaNFD.Native;

/// <summary>Handle Windows logoff/shutdown while the frontend can still drain its workers.</summary>
internal sealed class SessionEndHook : IDisposable
{
    private const uint QueryEndSession = 0x0011, EndSession = 0x0016, NcDestroy = 0x0082;
    private const nuint SubclassId = 0xD31FA11;
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(4);
    private readonly SessionEndDrain _drain;
    private readonly Action<bool> _ended;
    private readonly SubclassProc _callback;
    private nint _window;

    internal SessionEndHook(nint window, SessionEndDrain drain, Action<bool> ended)
    {
        _drain = drain;
        _ended = ended;
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
                _drain.Query(WaitBudget);
                // Do not veto the user's shutdown or turn an incomplete drain into success.
                return 1;
            }
            if (message == EndSession)
            {
                if (wparam == 0)
                {
                    _drain.Cancel();
                    Log.Info("Windows 已取消会话结束；等待当前收尾完成后恢复接收操作，不自动重放已停止的帧格请求。");
                }
                else
                {
                    // A second bounded chance also covers ENDSESSION without a prior query.
                    if (!_drain.Succeeded) _drain.Query(WaitBudget);
                    _ended(_drain.Succeeded);
                }
                return 0;
            }
            if (message == NcDestroy) Dispose();
        }
        catch (Exception ex) { Log.Error("Windows 会话收尾通知处理失败，保留核对保护", ex); }
        return DefSubclassProc(window, message, wparam, lparam);
    }

    public void Dispose()
    {
        var window = Interlocked.Exchange(ref _window, 0);
        if (window != 0) RemoveWindowSubclass(window, _callback, SubclassId);
        _drain.Dispose();
    }

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
