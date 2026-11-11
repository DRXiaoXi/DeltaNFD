using System.Diagnostics;
using System.Runtime.InteropServices;
using DeltaNFD.Backends;
using DeltaNFD.Native;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

internal static class SessionEndDrainChecks
{
    private static int _passed;
    private sealed class Lease(Action release) : IDisposable
    {
        private int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) release(); }
    }
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception("Session-end check failed: " + message);
        _passed++;
    }

    internal static async Task RunAsync()
    {
        _passed = 0;
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-session-end", id.ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        try
        {
            await CoordinatorAsync();
            NativeMessages();
            await OwnedBackendAsync(root, id);
            Console.WriteLine($"Session-end checks passed ({_passed} assertions): bounded/idempotent drain, cancelled logoff, native messages to owned window, real pipe restore/exit, failure evidence retained. No actual logoff/shutdown or system optimization writes.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static async Task CoordinatorAsync()
    {
        var release = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0; var blocks = 0;
        var messages = new List<string>();
        using var drain = new SessionEndDrain(() => { Interlocked.Increment(ref calls); return release.Task; },
            () => { blocks++; return new Lease(() => blocks--); }, messages.Add);
        var elapsed = Stopwatch.StartNew();
        Check(!drain.Query(TimeSpan.FromMilliseconds(20)), "incomplete recovery is not success");
        Check(elapsed.Elapsed < TimeSpan.FromSeconds(1), "query wait is bounded");
        Check(!drain.Succeeded && blocks == 1, "unknown recovery keeps the write gate closed");
        Check(!drain.Query(TimeSpan.FromMilliseconds(20)), "repeated query reuses the drain");
        Check(calls == 1 && blocks == 1, "query never overlaps or replays recovery");
        drain.Cancel();
        Check(blocks == 1, "cancelled logoff does not interrupt ongoing recovery");
        Check(!drain.Query(TimeSpan.FromMilliseconds(20)) && calls == 1, "new query during cancelled drain reuses it");
        release.SetResult([]);
        Check(drain.Query(TimeSpan.FromSeconds(2)) && drain.Succeeded, "actual completed recovery can pass");
        Check(blocks == 1, "success gate stays closed until session decision");
        drain.Cancel();
        await WaitAsync(() => blocks == 0);
        Check(blocks == 0, "cancelled session reopens gate only after recovery finishes");
        Check(messages.Any(m => m.Contains("未认定恢复完成")), "timeout accurately reported");
        var faultBlocks = 0;
        using (var faulted = new SessionEndDrain(() => Task.FromException<IReadOnlyList<string>>(new IOException("owned-failure")),
            () => { faultBlocks++; return new Lease(() => faultBlocks--); }, _ => { }))
        {
            Check(!faulted.Query(TimeSpan.FromSeconds(1)) && !faulted.Succeeded, "exceptions remain unconfirmed");
            faulted.Cancel();
        }
        Check(faultBlocks == 0, "failed cancelled session releases admission lease");
        using var pending = new SessionEndDrain(() => Task.FromResult<IReadOnlyList<string>>(["cpuframe"]),
            () => new Lease(() => { }), _ => { });
        Check(!pending.Query(TimeSpan.FromSeconds(1)) && !pending.Succeeded, "pending evidence is never mistaken for clean shutdown");
        pending.Dispose();
        Check(!pending.Query(TimeSpan.Zero), "disposed coordinator cannot restart recovery");
    }

    private static void NativeMessages()
    {
        // Synthetic messages target only our own hidden test window, never the desktop/session.
        var window = CreateWindowExW(0, "STATIC", "DeltaNFD owned session test", 0, 0, 0, 0, 0, 0, 0, 0, 0);
        Check(window != 0, "owned native test window created");
        Check(GetProcessShutdownParameters(out var level, out var flags), "test process lifetime order readable");
        var ended = new List<bool>(); var calls = 0; var blocks = 0;
        try
        {
            using var drain = new SessionEndDrain(() => { calls++; return Task.FromResult<IReadOnlyList<string>>([]); },
                () => { blocks++; return new Lease(() => blocks--); }, _ => { });
            using var hook = new SessionEndHook(window, drain, ended.Add);
            Check(GetProcessShutdownParameters(out var frontend, out _) && frontend == ProcessShutdownOrder.FrontendLevel,
                "frontend receives earlier session notification order");
            Check(SendMessageW(window, 0x11, 0, unchecked((nint)0x80000000)) == 1, "query permits user logoff after bounded wait");
            Check(calls == 1 && blocks == 1 && ended.Count == 0, "query drains but does not exit before confirmation");
            SendMessageW(window, 0x16, 0, 0);
            Check(blocks == 0 && ended.Count == 0, "cancelled native session neither exits nor leaves the gate locked");
            SendMessageW(window, 0x11, 0, 0);
            SendMessageW(window, 0x16, 1, 0);
            Check(ended.SequenceEqual([true]) && calls == 2, "confirmed native session exits only with verified outcome");
            ProcessShutdownOrder.Configure(frontend: false);
            Check(GetProcessShutdownParameters(out var backend, out _) && backend == ProcessShutdownOrder.BackendLevel
                && backend < frontend, "backend lifetime order follows the frontend");
        }
        finally { DestroyWindow(window); SetProcessShutdownParameters(level, flags); }

        var failedWindow = CreateWindowExW(0, "STATIC", "DeltaNFD owned failed session test", 0, 0, 0, 0, 0, 0, 0, 0, 0);
        Check(failedWindow != 0, "second owned window created");
        try
        {
            using var failure = new SessionEndDrain(() => Task.FromResult<IReadOnlyList<string>>(["cpuframe"]),
                () => new Lease(() => { }), _ => { });
            using var hook = new SessionEndHook(failedWindow, failure, ended.Add);
            Check(SendMessageW(failedWindow, 0x11, 0, 0) == 1, "failure does not veto user's session decision");
            SendMessageW(failedWindow, 0x16, 1, 0);
            Check(ended.SequenceEqual([true, false]), "native callback retains failure for original exit fallback");
        }
        finally { DestroyWindow(failedWindow); SetProcessShutdownParameters(level, flags); }
    }

    private static async Task OwnedBackendAsync(string root, Guid id)
    {
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT", root);
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ID", id.ToString("D"));
        var backend = Path.Combine(root, "backends", "cpuframe");
        Directory.CreateDirectory(backend);
        Environment.SetEnvironmentVariable(BackendPaths.BackendRootEnvironmentVariable, Path.GetDirectoryName(backend));
        Check(Path.GetFileName(Environment.ProcessPath) == "BackendSmokeTest.exe", "IPC uses owned fixture executable only");
        foreach (var source in Directory.GetFiles(AppContext.BaseDirectory))
            File.Copy(source, Path.Combine(backend, Path.GetFileName(source) == "BackendSmokeTest.exe"
                ? "DeltaNFD.Backend.CpuFrame.exe" : Path.GetFileName(source)));
        try
        {
            var client = await BackendClient.StartAsync(BackendModules.CpuFrame, default);
            var started = await client.InvokeAsync(BackendCommands.CpuFrameMonitorStart, new { }, null, default);
            Check(started.Status == BackendResultStatuses.Ok, "owned monitor started through actual protocol");
            using (var drain = new SessionEndDrain(() => BackendLifecycle.PrepareHandoverAsync("模拟注销", TimeSpan.FromSeconds(3)),
                BackendOperationGate.BlockNewOperations, _ => { }))
            {
                Check(drain.Query(TimeSpan.FromSeconds(5)), "real client restores and drains behind session admission gate");
                Check(client.ExitConfirmed, "actual worker exit confirmed");
                Check(BackendUnconfirmedStore.PendingModules().Count == 0, "clean session drain creates no review record");
                try { BackendOperationGate.Check(); throw new Exception("admitted new operation during session-end"); }
                catch (InvalidOperationException) { Check(true, "new operations remain blocked until cancellation"); }
                drain.Cancel();
            }
            BackendOperationGate.Check();
            Check(true, "cancelled synthetic logoff resumes admission");

            Environment.SetEnvironmentVariable("DELTANFD_TEST_RESTORE_FAIL_ONCE", "1");
            var failed = await BackendClient.StartAsync(BackendModules.CpuFrame, default);
            Environment.SetEnvironmentVariable("DELTANFD_TEST_RESTORE_FAIL_ONCE", null);
            await failed.InvokeAsync(BackendCommands.CpuFrameMonitorStart, new { }, null, default);
            using (var drain = new SessionEndDrain(() => BackendLifecycle.PrepareHandoverAsync("模拟失败注销", TimeSpan.FromSeconds(3)),
                BackendOperationGate.BlockNewOperations, _ => { }))
            {
                Check(!drain.Query(TimeSpan.FromSeconds(5)), "failed real-pipe restore stays unconfirmed");
                Check(BackendUnconfirmedStore.PendingModules().Contains(BackendModules.CpuFrame), "failed restore retains original review protection");
                var path = BackendUnconfirmedStore.RecordPathForTests(BackendModules.CpuFrame);
                var bytes = File.ReadAllBytes(path);
                drain.Cancel();
                Check(File.ReadAllBytes(path).SequenceEqual(bytes), "cancellation does not clear or rewrite failed evidence");
            }
            Check(failed.ExitConfirmed, "failed cleanup still confirms worker exit separately");
            await BackendLifecycle.AcknowledgeCurrentStateAsync(BackendUnconfirmedStore.ReviewAll().Single());
        }
        finally
        {
            Environment.SetEnvironmentVariable(BackendPaths.BackendRootEnvironmentVariable, null);
            Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT", null);
            Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ID", null);
            Environment.SetEnvironmentVariable("DELTANFD_TEST_RESTORE_FAIL_ONCE", null);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
        if (!condition()) throw new TimeoutException("owned coordinator did not release");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint ex, string cls, string title,
        uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint data);
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint window, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessShutdownParameters(out uint level, out uint flags);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetProcessShutdownParameters(uint level, uint flags);
}
