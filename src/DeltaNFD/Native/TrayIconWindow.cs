using System.Diagnostics;
using System.Runtime.InteropServices;
using DeltaNFD.Services;

namespace DeltaNFD.Native;

/// <summary>
/// 系统托盘图标（原生 Shell_NotifyIconW 实现，不依赖第三方库）。
/// 后台线程持有一个隐藏顶层窗口接收托盘回调与 <c>TaskbarCreated</c> 广播
/// （Explorer / 任务栏重启后自动重加图标）；左键单击触发 <see cref="Clicked"/>，
/// 右键弹出菜单（打开主窗口 / 退出）。所有原生调用检查返回值并写日志，不再静默吞失败。
/// </summary>
internal sealed class TrayIconWindow : IDisposable
{
    private const int WmApp = 0x8000;
    private const int WmTrayCallback = WmApp + 1;
    private const int WmClose = 0x0010;

    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;

    private const int NimAdd = 0x0;
    private const int NimDelete = 0x2;
    private const int NifMessage = 0x1;
    private const int NifIcon = 0x2;
    private const int NifTip = 0x4;

    private const uint MfString = 0x0;
    private const uint TpmReturncmd = 0x0100;
    private const uint TpmRightbutton = 0x0002;

    private const uint PopupOpenItem = 1;
    private const uint PopupExitItem = 2;

    /// <summary>任务栏重建广播（Explorer 重启时发给所有顶层窗口）。</summary>
    private static readonly uint TaskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");

    private static readonly string ClassName = "DeltaNFD_Tray_" + Guid.NewGuid().ToString("N");

    private readonly string _tooltip;
    private readonly Action _clicked;
    private readonly Action _exitRequested;
    private readonly ManualResetEvent _windowReadyEvent = new(initialState: false);

    private nint _hwnd;
    private nint _hIcon;
    private volatile bool _iconAdded;
    private WndProcDelegate? _wndProcDelegate;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassW
    {
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconDataW
    {
        public int cbSize;
        public nint hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wparam, nint lparam);

    public TrayIconWindow(string tooltip, Action clicked, Action exitRequested)
    {
        _tooltip = tooltip;
        _clicked = clicked;
        _exitRequested = exitRequested;
    }

    public void Dispose() => Hide();

    /// <summary>
    /// 显示托盘图标（首次调用时启动后台消息窗口线程；重复调用无副作用）。
    /// 返回图标是否真正注册成功——调用方必须据此决定是否隐藏主窗口，
    /// 避免出现「窗口消失、进程还在、托盘无图标」的被困状态。
    /// </summary>
    public bool Show()
    {
        if (_iconAdded)
        {
            return true;
        }

        EnsureMessageWindow();
        return AddIconWithRetry();
    }

    /// <summary>移除托盘图标（消息窗口保留，供下次 Show 复用；Dispose 时窗口线程才退出）。</summary>
    public void Hide()
    {
        RemoveIcon();
    }

    // ---------------- 消息窗口线程 ----------------

    private void EnsureMessageWindow()
    {
        if (_hwnd != nint.Zero)
        {
            return;
        }

        _windowReadyEvent.Reset();
        var thread = new Thread(RunMessageLoop) { IsBackground = true };
        thread.Start();

        if (!_windowReadyEvent.WaitOne(TimeSpan.FromSeconds(5)))
        {
            Log.Error("托盘：消息窗口线程 5 秒内未就绪");
        }
    }

    private void RunMessageLoop()
    {
        _wndProcDelegate = WndProc;
        var hInstance = GetModuleHandleW(null);

        var wndClass = new WndClassW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            lpszClassName = ClassName,
            hInstance = hInstance,
        };
        if (RegisterClassW(ref wndClass) == 0 && Marshal.GetLastWin32Error() != 1410)
        {
            // 1410 = ERROR_CLASS_ALREADY_EXISTS（进程内已注册，可忽略）；其余失败记日志
            Log.Error($"托盘：RegisterClassW 失败（Win32Error {Marshal.GetLastWin32Error()}）");
        }

        // 普通隐藏顶层窗口（不能用 HWND_MESSAGE：消息专用窗口收不到 TaskbarCreated 广播，
        // Explorer 重启后托盘图标会永久丢失）
        _hwnd = CreateWindowExW(0, ClassName, "", 0, 0, 0, 0, 0, nint.Zero, nint.Zero, hInstance, nint.Zero);
        if (_hwnd == nint.Zero)
        {
            Log.Error($"托盘：CreateWindowExW 失败（Win32Error {Marshal.GetLastWin32Error()}）");
            _windowReadyEvent.Set();
            return;
        }

        _windowReadyEvent.Set();

        while (GetMessageW(out var msg, nint.Zero, 0, 0) > 0)
        {
            _ = TranslateMessage(ref msg);
            _ = DispatchMessageW(ref msg);
        }

        _hwnd = nint.Zero;
    }

    private nint WndProc(nint hwnd, uint msg, nint wparam, nint lparam)
    {
        if (msg == (uint)WmTrayCallback)
        {
            if (lparam == WmLButtonUp || lparam == WmLButtonDblClk)
            {
                InvokeUi(_clicked);
            }
            else if (lparam == WmRButtonUp)
            {
                ShowContextMenu(hwnd);
            }

            return nint.Zero;
        }

        // Explorer / 任务栏重建：广播会清掉所有托盘图标，此处立即重加（自愈）
        if (msg == TaskbarCreatedMessage && TaskbarCreatedMessage != 0 && _iconAdded)
        {
            Log.Info("托盘：检测到任务栏重建，重新注册托盘图标");
            RemoveIcon();
            AddIconWithRetry();
            return nint.Zero;
        }

        if (msg == WmClose)
        {
            RemoveIcon();
            _ = DestroyWindow(hwnd);
            PostQuitMessage(0);
            return nint.Zero;
        }

        return DefWindowProcW(hwnd, msg, wparam, lparam);
    }

    private void InvokeUi(Action action)
    {
        try
        {
            action?.Invoke();
        }
        catch
        {
            // 回调异常不影响托盘运行
        }
    }

    private void ShowContextMenu(nint hwnd)
    {
        // 经典模式：先 SetForegroundWindow 再 TrackPopupMenu，否则菜单不随点击外部关闭
        _ = SetForegroundWindow(hwnd);
        var menu = CreatePopupMenu();
        if (menu == nint.Zero)
        {
            return;
        }

        _ = AppendMenuW(menu, MfString, PopupOpenItem, "打开主窗口");
        _ = AppendMenuW(menu, MfString, PopupExitItem, "退出");

        if (!GetCursorPos(out var pt))
        {
            _ = DestroyMenu(menu);
            return;
        }

        var cmd = (uint)TrackPopupMenu(
            menu, TpmReturncmd | TpmRightbutton, pt.X, pt.Y, 0, hwnd, nint.Zero);
        _ = DestroyMenu(menu);

        switch (cmd)
        {
            case PopupOpenItem:
                InvokeUi(_clicked);
                break;
            case PopupExitItem:
                InvokeUi(_exitRequested);
                break;
        }
    }

    // ---------------- 托盘图标 ----------------

    /// <summary>注册托盘图标，失败重试 3 次（任务栏刚启动 / Explorer 忙时 NIM_ADD 可能瞬时失败）。</summary>
    private bool AddIconWithRetry()
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (AddNotifyIcon())
            {
                _iconAdded = true;
                Log.Info($"托盘：图标注册成功（第 {attempt} 次尝试）");
                return true;
            }

            Log.Error($"托盘：Shell_NotifyIconW(NIM_ADD) 第 {attempt} 次失败" +
                      $"（Win32Error {Marshal.GetLastWin32Error()}）");
            Thread.Sleep(400);
        }

        return false;
    }

    private bool AddNotifyIcon()
    {
        var exePath = Environment.ProcessPath ?? "";
        var smallIcons = new nint[1];
        if (ExtractIconExW(exePath, 0, null, smallIcons, 1) == 0)
        {
            Log.Error($"托盘：ExtractIconExW 未从 exe 提取到图标（{exePath}）");
        }
        else
        {
            _hIcon = smallIcons[0];
        }

        var data = new NotifyIconDataW
        {
            cbSize = Marshal.SizeOf<NotifyIconDataW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifMessage | NifTip | (_hIcon != nint.Zero ? NifIcon : 0),
            uCallbackMessage = WmTrayCallback,
            hIcon = _hIcon,
            szTip = _tooltip,
        };
        return _hwnd != nint.Zero && Shell_NotifyIconW(NimAdd, ref data);
    }

    private void RemoveIcon()
    {
        if (_hwnd == nint.Zero || !_iconAdded)
        {
            return;
        }

        var data = new NotifyIconDataW
        {
            cbSize = Marshal.SizeOf<NotifyIconDataW>(),
            hWnd = _hwnd,
            uID = 1,
        };
        if (!Shell_NotifyIconW(NimDelete, ref data))
        {
            Log.Error($"托盘：Shell_NotifyIconW(NIM_DELETE) 失败（Win32Error {Marshal.GetLastWin32Error()}）");
        }

        _iconAdded = false;

        if (_hIcon != nint.Zero)
        {
            _ = DestroyIcon(_hIcon);
            _hIcon = nint.Zero;
        }
    }

    // ---------------- 原生 API ----------------

    [DllImport("shell32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(int dwMessage, ref NotifyIconDataW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WndClassW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out Msg lpMsg, nint hwnd, int wMsgFilterMin, int wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint hwnd, uint msg, nint wparam, nint lparam);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hwnd, uint msg, nint wparam, nint lparam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string lpszFile, int nIconIndex, nint[]? phiconLarge, nint[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(nint hMenu, uint uFlags, nuint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(
        nint hMenu, uint uFlags, int x, int y, int nReserved, nint hwnd, nint prcRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point lpPoint);
}
