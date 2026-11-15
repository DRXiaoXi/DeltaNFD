using System.Runtime.InteropServices;
using System.Text.Json;
using DeltaNFD.Execution.OptimalClock;
using DeltaNFD.Services;

internal static class DpcWindowChecks
{
    internal static void Run()
    {
        int n = 0;
        void Check(bool value, string text) { if (!value) throw new Exception(text); n++; }
        Check(new AppSettings().OptimalClockDpcWindowHidden && JsonSerializer.Deserialize<AppSettings>("{}")!.OptimalClockDpcWindowHidden,
            "Hidden preference defaults on for new and old settings");
        Check(!JsonSerializer.Deserialize<AppSettings>("{\"OptimalClockDpcWindowHidden\":false}")!.OptimalClockDpcWindowHidden, "Explicit visible preference preserved");
        var api = new Fake(); var alive = true;
        var policy = new DpcWindowPolicy(api, 55, () => alive);
        policy.Maintain(true);
        Check(!api.Items[1].Visible && api.Actions.SequenceEqual(["hide:1"]), "Only own original main frame hidden");
        Check(api.Items[2].Visible && api.Items[3].Visible && api.Items[4].Visible, "Foreign, unrecognized and unmarked windows untouched");
        policy.Maintain(true); Check(api.Actions.Count == 1, "Already hidden window causes no repeated native action");
        api.Items[1] = api.Items[1] with { Visible = true }; policy.Maintain(true);
        Check(api.Actions.Count == 2 && api.Actions[^1] == "hide:1", "A reshown original window is hidden again");
        policy.Maintain(false); Check(api.Items[1].Visible && api.Actions[^1] == "show:1", "User unhide restores only the tracked main window");
        policy.Maintain(true); api.Items[1] = api.Items[1] with { Enabled = false };
        api.Items[5] = new(5, 55, 1, "Driver error", false, true, true);
        policy.Maintain(true);
        Check(api.Items[1].Visible && api.Items[5].Visible && !api.Actions.Any(x => x.EndsWith(":5")), "Modal error owner restored, error not hidden or closed");
        var before = api.Actions.Count; Check(!policy.CloseNormally() && !api.Actions.Skip(before).Any(x => x.StartsWith("close:")), "Disabled main frame isn't forced closed");
        api.Items.Remove(5); api.Items[1] = api.Items[1] with { Enabled = true };
        policy.Maintain(true); Check(!api.Items[1].Visible && policy.CloseNormally() && api.Actions[^1] == "close:1", "Normal close reaches verified hidden main frame");
        alive = false; before = api.Actions.Count;
        policy.Maintain(true); policy.Reveal(); policy.CloseNormally();
        Check(api.Actions.Count == before, "Exited/reused process identity cannot cause window writes");
        alive = true; api.Items[1] = api.Items[1] with { Pid = 66 };
        before = api.Actions.Count; policy.Maintain(true); policy.CloseNormally();
        Check(api.Actions.Count == before, "Reused HWND with different PID is rejected on re-read");
        api.Items[1] = api.Items[1] with { Pid = 55, Visible = false };
        new DpcWindowPolicy(api, 55, () => true).Reveal();
        Check(api.Items[1].Visible, "Recovery controller can reveal a verified hidden frame without previous in-memory history");
        foreach (var w in new[] { new DpcWindowInfo(1, 55, 0, "DPC Latency Checker V1.4.0", true, true, true),
            new DpcWindowInfo(1, 55, 0, "DPC Latency Checker", true, true, true) })
            Check(DpcWindowPolicy.IsMain(w, 55), "Pinned original main title recognized");

        // Native contract test: our own initially hidden fixture only. Never show/activate any window or run DPC.
        var windows = new List<nint>();
        nint Create(string cls, string text, uint style, nint parent = 0, int id = 0)
        {
            var h = CreateWindowEx(0, cls, text, style, 0, 0, 32, 32, parent, id, 0, 0);
            if (h == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            windows.Add(h); return h;
        }
        try
        {
            var main = Create("STATIC", "DPC Latency Checker V1.4.0", 0);
            foreach (var id in new[] { 1000, 1001, 1007 }) Create("BUTTON", "owned fixture", 0x40000000, main, id);
            var other = Create("STATIC", "unrelated owned fixture", 0);
            var native = new NativeDpcWindowApi(); var pid = (uint)Environment.ProcessId;
            var info = native.Read(main, pid);
            Check(info is not null && DpcWindowPolicy.IsMain(info, pid) && !info.Visible, "Actual hidden HWND and recursive original control markers recognized");
            Check(native.Read(main, pid + 1) is null, "Actual foreign-PID read rejected before title/control reads");
            Check(!new NativeDpcWindowApi(pid + 1).Close(main), "Native write rechecks PID and rejects a mismatched owner");
            var owned = new DpcWindowPolicy(native, pid, () => true);
            Check(owned.CloseNormally(), "WM_CLOSE queued for our already-hidden test frame");
            for (var i = 0; i < 100 && IsWindow(main); i++)
                if (PeekMessage(out var message, 0, 0, 0, 1)) { TranslateMessage(ref message); DispatchMessage(ref message); }
            Check(!IsWindow(main) && IsWindow(other), "Native normal close destroys only the matched hidden fixture");
            Check(!IsWindowVisible(other), "Unrelated native fixture stayed hidden throughout");
        }
        finally { foreach (var h in windows.AsEnumerable().Reverse()) if (IsWindow(h)) DestroyWindow(h); }
        Console.WriteLine($"DPC window policy: {n} checks passed; fake windows plus own initially hidden Win32 fixture only. No DPC/driver/timer or user-window changes.");
    }
    private sealed class Fake : IDpcWindowApi
    {
        public Dictionary<nint, DpcWindowInfo> Items = new() {
            [1] = new(1, 55, 0, "DPC Latency Checker V1.4.0", true, true, true),
            [2] = new(2, 66, 0, "DPC Latency Checker V1.4.0", true, true, true),
            [3] = new(3, 55, 0, "Other", true, true, true),
            [4] = new(4, 55, 0, "DPC Latency Checker", false, true, true) };
        public List<string> Actions = [];
        public IReadOnlyList<DpcWindowInfo> Windows(uint pid) => Items.Values.ToArray();
        public DpcWindowInfo? Read(nint h, uint pid) => Items.TryGetValue(h, out var w) && w.Pid == pid ? w : null;
        public bool Show(nint h, bool visible) { Actions.Add((visible ? "show:" : "hide:") + h); Items[h] = Items[h] with { Visible = visible }; return true; }
        public bool Close(nint h) { Actions.Add("close:" + h); return true; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] private static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref Message message);
}
