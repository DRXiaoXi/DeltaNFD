using System.Runtime.InteropServices;
using System.Text;

namespace DeltaNFD.Execution.OptimalClock;

internal sealed record DpcWindowInfo(nint Handle, uint Pid, nint Owner, string Title, bool MainControls, bool Visible, bool Enabled);
internal interface IDpcWindowApi
{
    IReadOnlyList<DpcWindowInfo> Windows(uint pid);
    DpcWindowInfo? Read(nint handle, uint pid);
    bool Show(nint handle, bool visible);
    bool Close(nint handle);
}

/// <summary>Only the verified process's original main frame, identified by title and its known child controls.</summary>
internal sealed class DpcWindowPolicy(IDpcWindowApi api, uint pid, Func<bool> identityAlive)
{
    private readonly HashSet<nint> _hidden = [];
    internal static bool IsMain(DpcWindowInfo w, uint expectedPid) => w.Pid == expectedPid && w.Owner == 0 && w.MainControls
        && (w.Title == "DPC Latency Checker" || w.Title == "DPC Latency Checker V1.4.0");
    private DpcWindowInfo? ReadMain(nint handle)
    {
        if (!identityAlive()) return null;
        var w = api.Read(handle, pid);
        return w is not null && IsMain(w, pid) ? w : null;
    }
    internal void Maintain(bool hidden)
    {
        if (!identityAlive()) return;
        var windows = api.Windows(pid);
        foreach (var w in windows.Where(w => IsMain(w, pid)))
        {
            var current = ReadMain(w.Handle);
            if (current is null) continue;
            // Do not bury a modal error/About dialog behind its owner.
            var modal = !current.Enabled || windows.Any(x => x.Pid == pid && x.Owner == w.Handle && x.Visible);
            if (hidden && !modal && current.Visible)
            {
                if (api.Show(w.Handle, false)) _hidden.Add(w.Handle);
            }
            else if ((!hidden && !current.Visible) || (modal && _hidden.Contains(w.Handle)))
            {
                if (api.Show(w.Handle, true)) _hidden.Remove(w.Handle);
            }
        }
        _hidden.RemoveWhere(h => ReadMain(h) is null);
    }
    internal bool CloseNormally()
    {
        if (!identityAlive()) return false;
        var posted = false;
        foreach (var w in api.Windows(pid).Where(w => IsMain(w, pid)))
        {
            var current = ReadMain(w.Handle);
            if (current is null) continue;
            if (!current.Enabled) { api.Show(w.Handle, true); continue; }
            // Hidden windows are excluded by Process.MainWindowHandle; send normal WM_CLOSE explicitly.
            posted |= api.Close(w.Handle);
        }
        return posted;
    }
    internal void Reveal() => Maintain(false);
}

internal sealed class NativeDpcWindowApi(uint? expectedPid = null) : IDpcWindowApi
{
    private readonly uint _expectedPid = expectedPid ?? (uint)Environment.ProcessId;
    public IReadOnlyList<DpcWindowInfo> Windows(uint pid)
    {
        var result = new List<DpcWindowInfo>();
        EnumWindows((h, _) => { var info = Read(h, pid); if (info is not null) result.Add(info); return result.Count < 32; }, 0);
        return result;
    }
    public DpcWindowInfo? Read(nint handle, uint pid)
    {
        if (!IsWindow(handle) || GetWindowThreadProcessId(handle, out var actual) == 0 || actual != pid) return null;
        var title = new StringBuilder(256); GetWindowText(handle, title, title.Capacity);
        // These IDs are the original dialog's Reset/Exit/Stop controls, confirmed from RT_DIALOG 129.
        var ids = new HashSet<int>();
        EnumChildWindows(handle, (child, _) => {
            if (GetWindowThreadProcessId(child, out var childPid) != 0 && childPid == pid) ids.Add(GetDlgCtrlID(child));
            return ids.Count < 128;
        }, 0);
        return new(handle, actual, GetWindow(handle, 4), title.ToString(), ids.Contains(1000) && ids.Contains(1001) && ids.Contains(1007),
            IsWindowVisible(handle), IsWindowEnabled(handle));
    }
    private bool StillOwned(nint handle) => GetWindowThreadProcessId(handle, out var pid) != 0 && pid == _expectedPid;
    public bool Show(nint handle, bool visible) => StillOwned(handle) && ShowWindowAsync(handle, visible ? 4 : 0); // SHOWNOACTIVATE / HIDE
    public bool Close(nint handle) => StillOwned(handle) && PostMessage(handle, 0x10, 0, 0);
    private delegate bool EnumProc(nint window, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumProc callback, nint data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
