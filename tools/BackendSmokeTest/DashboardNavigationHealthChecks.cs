using DeltaNFD.Services.Backends;
using DeltaNFD.ViewModels;

internal static class DashboardNavigationHealthChecks
{
    private static int _count;
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); _count++; }
    private static TaskCompletionSource<string> Pending() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
    internal static async Task RunAsync()
    {
        _count = 0;
        var manager = PageBackendSessions.Manager;
        var original = manager.DeltaRunning;
        var active = new List<DashboardViewModel>();
        DashboardViewModel New(DashboardHealthTestDispatcher? dispatcher = null)
        { var vm = new DashboardViewModel(dispatcher); active.Add(vm); return vm; }
        var sessions = BackendSessions.Snapshot().Count;
        try
        {
            DeltaNFD.App.MainWindow = new() { StartupGameDirectoryCheckCompleted = true };
            manager.ObserveDelta(false);
            var vm = New();
            await vm.RefreshHealthAsync(); // WinUI OnNavigatedTo precedes Loaded.
            Check(vm.Reads.Count == 0 && vm.MemoryRefreshes == 0 && vm.PreferenceRefreshes == 0, "unloaded refresh cannot consume observation or perform reads");
            vm.AttachHealth();
            Check(vm.Reads.SequenceEqual(new[] { "shader", "ace", "runtime" }), "Loaded starts all components after pre-Loaded refresh");
            Check(vm.HealthSummary == "组件检测正常" && !vm.IsHealthScanning, "returning homepage no longer stuck gray");
            Check(vm.LastHealthCheckText.StartsWith("最近检测"), "successful read has completion time");
            vm.AttachHealth();
            Check(vm.Reads.Count == 3, "duplicate Loaded does not duplicate subscriptions or scans");
            vm.DetachHealth();
            var lastTime = vm.LastHealthCheckText;
            Check(vm.HealthSummary == "组件检测正常" && vm.LastHealthCheckText == lastTime, "completed values survive Unloaded");
            var reentry = Pending();
            vm.Probe = area => area == "shader" ? reentry.Task : Task.FromResult(area + " new");
            vm.AttachHealth();
            Check(vm.IsHealthScanning && vm.ShaderHealthState == DashboardHealthState.Normal && vm.AceHealthState == DashboardHealthState.Normal,
                "reentry refresh keeps last completed component colors");
            reentry.SetResult("shader new"); await UntilAsync(() => !vm.IsHealthScanning);
            Check(vm.ShaderStatusText == "shader new" && vm.AceStatusText == "ace new", "reentry finishes with new results");

            var old = Pending(); var current = Pending();
            vm.Probe = area => area == "shader" ? old.Task : Task.FromResult("stale must not continue");
            var abandoned = vm.RefreshHealthAsync();
            Check(vm.IsHealthScanning, "fixture suspends submitted read");
            vm.DetachHealth();
            Check(!vm.IsHealthScanning && vm.ShaderStatusText == "shader new", "leave clears busy and restores previous checkpoint");
            vm.Probe = area => area == "shader" ? current.Task : Task.FromResult(area + " newest");
            vm.AttachHealth(); var readCount = vm.Reads.Count;
            old.SetResult("STALE"); await abandoned;
            Check(vm.IsHealthScanning && vm.ShaderStatusText == "shader new", "old completion cannot clear new generation busy or overwrite cached result");
            Check(vm.Reads.Count == readCount, "abandoned scan cannot queue next component");
            current.SetResult("CURRENT"); await UntilAsync(() => !vm.IsHealthScanning);
            Check(vm.ShaderStatusText == "CURRENT" && vm.RuntimeStatusText == "runtime newest", "new generation completes normally");

            var gameRead = Pending();
            vm.Probe = area => gameRead.Task;
            var beforeGame = vm.RefreshHealthAsync();
            manager.ObserveDelta(true);
            Check(!vm.IsHealthScanning && !vm.CanRefreshHealth && vm.ShaderStatusText == "CURRENT", "game start cancels display generation and retains last results");
            gameRead.SetResult("GAME STALE"); await beforeGame;
            Check(vm.ShaderStatusText == "CURRENT", "late game-time completion rejected");
            var whileGame = vm.Reads.Count;
            await vm.RefreshHealthAsync(); Check(vm.Reads.Count == whileGame, "manual refresh cannot read components during game");
            vm.Probe = area => Task.FromResult(area + " resumed");
            manager.ObserveDelta(false);
            Check(vm.Reads.Count == whileGame + 3 && vm.ShaderStatusText == "shader resumed", "game exit resumes suspended scan once");
            var settled = vm.Reads.Count;
            manager.Navigate("dashboard"); manager.Navigate("dashboard");
            Check(vm.Reads.Count == settled, "unchanged game-state notifications do not periodically rescan");
            vm.DetachHealth();
            manager.ObserveDelta(null); var unknown = New(); unknown.AttachHealth();
            Check(unknown.Reads.Count == 0 && !unknown.CanRefreshHealth && !unknown.HealthSummary.Contains("正在检测"), "unknown state pauses rather than false scanning");
            Check(unknown.ShaderHealthState == DashboardHealthState.Information, "never-read unknown component is explicit information");
            manager.ObserveDelta(false);
            Check(unknown.Reads.Count == 3 && unknown.HealthSummary == "组件检测正常", "unknown-to-idle observation starts first scan");
            unknown.DetachHealth();

            DeltaNFD.App.MainWindow.StartupGameDirectoryCheckCompleted = false;
            var startup = New(); startup.AttachHealth(); Check(startup.Reads.Count == 0, "startup directory readiness still gates automatic scan");
            DeltaNFD.App.MainWindow.StartupGameDirectoryCheckCompleted = true;
            await startup.RefreshHealthAsync(); Check(startup.Reads.Count == 3, "startup completion starts scan while already loaded"); startup.DetachHealth();

            var failed = New(); failed.Probe = _ => throw new IOException("fixture probe failure"); failed.AttachHealth();
            Check(failed.Reads.Count == 3 && failed.ShaderHealthState == DashboardHealthState.Failed && failed.RuntimeHealthState == DashboardHealthState.Failed,
                "read errors produce explicit failures without leaving other components gray");
            Check(!failed.IsHealthScanning && failed.CanRefreshHealth && failed.LastHealthCheckText.StartsWith("最近检测"), "errors release refresh and record detection time");
            failed.Probe = area => Task.FromResult(area + " retry"); await failed.RefreshHealthAsync();
            Check(failed.HealthSummary == "组件检测正常", "manual retry recovers after failures"); failed.DetachHealth();

            var queue = new DashboardHealthTestDispatcher(); var dispatched = New(queue); dispatched.AttachHealth();
            queue.HasThreadAccess = false; manager.ObserveDelta(true);
            Check(queue.Pending.Count == 1, "manager notification dispatches to UI queue");
            dispatched.DetachHealth(); manager.ObserveDelta(false); dispatched.AttachHealth();
            var beforeLateCallback = dispatched.Reads.Count; queue.Drain();
            Check(dispatched.Reads.Count == beforeLateCallback && dispatched.CanRefreshHealth, "queued old-page callback cannot change reattached generation");
            dispatched.DetachHealth();

            var rapid = New(); var pending = new List<TaskCompletionSource<string>>();
            rapid.Probe = area => { var completion = Pending(); pending.Add(completion); return completion.Task; };
            for (var i = 0; i < 30; i++) { rapid.AttachHealth(); rapid.DetachHealth(); }
            Check(rapid.Reads.Count == 30 && !rapid.IsHealthScanning, "30 rapid roundtrips never strand busy state");
            foreach (var completion in pending) completion.SetResult("late after leave");
            await Task.Delay(30);
            Check(rapid.Reads.Count == 30 && rapid.ShaderHealthState != DashboardHealthState.Checking, "rapid late callbacks cannot continue probes or leave false Checking");
            rapid.Probe = area => Task.FromResult(area + " final"); rapid.AttachHealth();
            Check(rapid.Reads.Count == 33 && rapid.HealthSummary == "组件检测正常", "rapid navigation can still complete a fresh scan"); rapid.DetachHealth();
            var countAfterLeave = rapid.Reads.Count; manager.ObserveDelta(true); manager.ObserveDelta(false);
            Check(rapid.Reads.Count == countAfterLeave, "unloaded dashboard is unsubscribed and cannot scan on game exit");
            Check(BackendSessions.Snapshot().Count == sessions, "lifecycle checks start zero backend processes");

            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "src/DeltaNFD/Views/DashboardPage.xaml.cs"))) root = root.Parent;
            var page = File.ReadAllText(Path.Combine(root!.FullName, "src/DeltaNFD/Views/DashboardPage.xaml.cs"));
            Check(page.Contains("NavigationCacheMode = NavigationCacheMode.Required"), "homepage retains its view model across frame navigation");
            Check(!page.Contains("override void OnNavigatedTo") && page.Contains("ViewModel.AttachHealth();"), "automatic refresh enters only after Loaded attachment");
            var vmSource = File.ReadAllText(Path.Combine(root.FullName, "src/DeltaNFD/ViewModels/DashboardViewModel.cs"));
            Check(vmSource.Contains("OptimizationScan.RunIfNotScannedAsync()") && !vmSource.Contains("OptimizationScan.RunAsync()"), "navigation reuses existing optimization preload task and cache");
            Console.WriteLine($"主页切页检测生命周期检查通过：{_count} 项；生产只读编排，模拟查询/界面，不启动后端、不写系统。");
        }
        finally
        {
            foreach (var vm in active) vm.DetachHealth();
            manager.ObserveDelta(original);
            DeltaNFD.App.MainWindow = null;
        }
    }
}

namespace DeltaNFD
{
    // Only the WinUI startup readiness boundary is replaced in this console fixture.
    internal static class App { internal static DashboardTestWindow? MainWindow; }
    internal sealed class DashboardTestWindow { internal bool StartupGameDirectoryCheckCompleted { get; set; } }
}
