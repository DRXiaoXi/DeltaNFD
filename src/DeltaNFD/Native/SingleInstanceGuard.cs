using System.Diagnostics;
using System.Runtime.InteropServices;
using DeltaNFD.Services;

namespace DeltaNFD.Native;

/// <summary>
/// 每个 Windows 会话只保留一个应用进程；再次启动时通知主进程恢复窗口。
/// </summary>
internal static class SingleInstanceGuard
{
    private const string MutexName = @"Local\DeltaNFD_7A3E9C4D_52B8_4E1F_9A6C_D0F1B2E3A4C5_SingleInstance";
    private const string ActivationEventName = @"Local\DeltaNFD_7A3E9C4D_52B8_4E1F_9A6C_D0F1B2E3A4C5_Activate";
    private const string LegacyMutexName = @"Local\DeltaOptimizer_7A3E9C4D_52B8_4E1F_9A6C_D0F1B2E3A4C5_SingleInstance";
    private const string LegacyActivationEventName = @"Local\DeltaOptimizer_7A3E9C4D_52B8_4E1F_9A6C_D0F1B2E3A4C5_Activate";

    private static Mutex? _primaryMutex;
    private static Mutex? _legacyMutex;
    private static EventWaitHandle? _activationEvent;
    private static EventWaitHandle? _legacyActivationEvent;

    /// <summary>首个进程返回 true；后续进程通知主进程恢复窗口后退出。</summary>
    public static bool TryBecomePrimary()
    {
        var activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        var mutex = new Mutex(false, MutexName);
        if (!TryAcquire(mutex))
        {
            GrantForegroundPermissionToPrimary();
            activationEvent.Set();
            activationEvent.Dispose();
            mutex.Dispose();
            return false;
        }

        var legacyActivationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, LegacyActivationEventName);
        var legacyMutex = new Mutex(false, LegacyMutexName);
        if (!TryAcquire(legacyMutex))
        {
            legacyMutex.Dispose();
            legacyActivationEvent.Dispose();
            mutex.ReleaseMutex();
            mutex.Dispose();
            activationEvent.Dispose();
            _ = MessageBoxW(nint.Zero, "检测到旧版三角帧不掉洲仍在运行。请从托盘退出旧版后，再启动 Delta NFD。", "三角帧不掉洲", 0x30);
            return false;
        }

        _primaryMutex = mutex;
        _legacyMutex = legacyMutex;
        _activationEvent = activationEvent;
        _legacyActivationEvent = legacyActivationEvent;
        return true;
    }

    private static bool TryAcquire(Mutex mutex)
    {
        try { return mutex.WaitOne(0); }
        catch (AbandonedMutexException) { return true; }
    }

    /// <summary>启动主进程的再次启动请求监听器。</summary>
    public static void StartActivationListener(Action onActivationRequested)
    {
        if (_primaryMutex is null || _legacyMutex is null ||
            _activationEvent is null || _legacyActivationEvent is null)
        {
            return;
        }

        WaitHandle[] activationEvents = [_activationEvent, _legacyActivationEvent];
        var listener = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    WaitHandle.WaitAny(activationEvents);
                    onActivationRequested();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Error("单实例：处理再次启动请求失败", ex);
                }
            }
        })
        {
            IsBackground = true,
            Name = "Delta NFD single-instance listener",
        };
        listener.Start();
    }

    private static void GrantForegroundPermissionToPrimary()
    {
        try
        {
            var processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath);
            if (string.IsNullOrWhiteSpace(processName))
            {
                return;
            }

            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId)
                    {
                        continue;
                    }

                    _ = AllowSetForegroundWindow((uint)process.Id);
                    return;
                }
            }
        }
        catch
        {
            // 主进程仍会尝试恢复并激活窗口。
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);
}
