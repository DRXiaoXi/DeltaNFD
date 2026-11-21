using System.Runtime.InteropServices;

namespace DeltaNFD.Native;

/// <summary>Observes native minimize/restore independently of AppWindow size or visibility changes.</summary>
internal sealed class WindowPresentationHook : IDisposable
{
    private const uint Size = 0x0005, ShowWindow = 0x0018, WindowPosChanged = 0x0047, NcDestroy = 0x0082;
    private const nuint SubclassId = 0xD31FA12;
    private readonly SubclassProc _callback;
    private readonly Action _changed;
    private nint _window;
    private bool _wasPresented;

    private WindowPresentationHook(nint window, Action changed)
    {
        _window = window;
        _wasPresented = IsPresented(window);
        _changed = changed;
        _callback = WindowProc;
    }

    internal static WindowPresentationHook? Attach(nint window, Action changed)
    {
        var hook = new WindowPresentationHook(window, changed);
        return SetWindowSubclass(window, hook._callback, SubclassId, 0) ? hook : null;
    }

    internal static bool IsPresented(nint window) => window != 0 && IsWindowVisible(window) && !IsIconic(window);

    private nint WindowProc(nint window, uint message, nint wparam, nint lparam, nuint id, nuint data)
    {
        if (message == NcDestroy) Dispose();
        else if (message is Size or ShowWindow or WindowPosChanged)
        {
            var presented = IsPresented(window);
            if (_wasPresented != presented)
            {
                _wasPresented = presented;
                _changed(); // Enqueue only; leave native message handling unchanged.
            }
        }
        return DefSubclassProc(window, message, wparam, lparam);
    }

    public void Dispose()
    {
        if (_window == 0) return;
        RemoveWindowSubclass(_window, _callback, SubclassId);
        _window = 0;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint window, uint message, nint wparam, nint lparam, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint window, uint message, nint wparam, nint lparam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);
}
