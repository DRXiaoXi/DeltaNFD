using DeltaNFD.Backends;
using DeltaNFD.Services;
using Microsoft.UI.Xaml.Controls;

// Compile the real page handlers with inert controls and backend boundaries. No WinUI window or system writes.
internal static class Fixture
{
    public static LowMemoryStatus Status = new(16UL * 1024 * 1024 * 1024, PrefetchDisabled: false, Error: "SysMain unavailable");
    public static bool HasBackup, BackupUnreadable;
    public static string? Blocker;
    public static readonly List<LowMemoryApplyArgs> Requests = [];
    public static readonly List<ContentDialog> Dialogs = [];
    public static Func<Task<ContentDialogResult>> Confirm = () => Task.FromResult(ContentDialogResult.Primary);
    public static Func<LowMemoryApplyArgs, Task<BackendResult>> Invoke = Success;
    public static Task<BackendResult> Success(LowMemoryApplyArgs args)
    {
        Status = Status with { PrefetchDisabled = !args.RestorePrefetch };
        HasBackup = !args.RestorePrefetch;
        return Task.FromResult(BackendResult.Ok(BackendCommands.SystemLowMemoryApply, "done", OperationResult.Ok("fixture done")));
    }
    public static void Reset()
    {
        Status = new(16UL * 1024 * 1024 * 1024, PrefetchDisabled: false, Error: "SysMain unavailable");
        HasBackup = BackupUnreadable = false; Blocker = null; Requests.Clear(); Dialogs.Clear();
        Confirm = () => Task.FromResult(ContentDialogResult.Primary); Invoke = Success;
    }
}
namespace Microsoft.UI.Xaml
{
    public class RoutedEventArgs : EventArgs;
    public delegate void RoutedEventHandler(object sender, RoutedEventArgs e);
    public enum Visibility { Visible, Collapsed }
    public sealed class DispatcherQueue { public bool TryEnqueue(Action action) { action(); return true; } }
}
namespace Microsoft.UI.Xaml.Controls
{
    using Microsoft.UI.Xaml;
    public class Page
    {
        public event RoutedEventHandler? Loaded, Unloaded;
        public object XamlRoot { get; } = new();
        public DispatcherQueue DispatcherQueue { get; } = new();
        public void Load() => Loaded?.Invoke(this, new());
        public void Unload() => Unloaded?.Invoke(this, new());
    }
    public class Button { public bool IsEnabled; public object? Content; public Visibility Visibility; }
    public class TextBlock { public string Text = ""; }
    public sealed class InfoBar { public string Title = "", Message = ""; public InfoBarSeverity Severity; }
    public enum InfoBarSeverity { Informational, Success, Warning }
    public sealed class ProgressRing { public bool IsActive; }
    public sealed class ProgressBar { public double Maximum = 4, Value; public Visibility Visibility; }
    public sealed class ToggleSwitch : Button
    {
        private bool _isOn;
        public object? OffContent;
        public event RoutedEventHandler? Toggled;
        public bool IsOn { get => _isOn; set { if (value == _isOn) return; _isOn = value; Toggled?.Invoke(this, new()); } }
    }
    public sealed class ContentDialog
    {
        public object? XamlRoot, Content;
        public string Title = "", PrimaryButtonText = "", CloseButtonText = "";
        public ContentDialogButton DefaultButton;
    }
    public enum ContentDialogButton { Close, Primary }
    public enum ContentDialogResult { None, Primary }
}
namespace DeltaNFD.Inspection
{
    public sealed class SystemTweakInspection
    {
        public IReadOnlyList<SystemTweak> GetBackedUpTweaks() => Fixture.BackupUnreadable ? throw new IOException("backup unreadable")
            : Fixture.HasBackup ? [SystemTweak.Prefetch] : [];
    }
}
namespace DeltaNFD.Services
{
    public static class LowMemoryHealthCache { public static Task<LowMemoryStatus> ReadAsync(bool force) => Task.FromResult(Fixture.Status); }
    public static class OptimizationScan { public static void Invalidate() { } }
    public static class DialogExtensions
    {
        public static Task<ContentDialogResult> ShowWithBackendRecoveryAsync(this ContentDialog dialog, object? pageOwner)
        { Fixture.Dialogs.Add(dialog); return Fixture.Confirm(); }
    }
}
namespace DeltaNFD.Services.Backends
{
    public sealed class PageBackendLease;
    public sealed class SessionManager
    {
        public PageBackendLease Current { get; } = new();
        public event Action? Changed;
        public void Notify() => Changed?.Invoke();
    }
    public static class PageBackendSessions
    {
        public static SessionManager Manager { get; } = new();
        public static string? SystemWriteBlocker => Fixture.Blocker;
    }
    public sealed class BackendCaller(string module, PageBackendLease? owner)
    {
        public Task<BackendResult> InvokeAsync(string command, object args, IProgress<string>? progress, CancellationToken token)
        {
            if (module != BackendModules.System || owner is null) throw new Exception("Missing system page owner");
            if (command != BackendCommands.SystemLowMemoryApply || args is not LowMemoryApplyArgs selected || selected.Action != LowMemoryAction.Prefetch)
                throw new Exception("Unexpected command or scope");
            Fixture.Requests.Add(selected); return Fixture.Invoke(selected);
        }
    }
}

internal sealed class UiContext : SynchronizationContext, IDisposable
{
    private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = new();
    public void Dispose() => _queue.Dispose();
    public override void Post(SendOrPostCallback action, object? state) => _queue.Add(() => action(state));
    public static void Run(Func<Task> action)
    {
        var previous = Current;
        using var context = new UiContext();
        SetSynchronizationContext(context);
        try
        {
            var task = action();
            while (!task.IsCompleted) { if (context._queue.TryTake(out var next, 5000)) next(); else throw new TimeoutException("UI fixture stalled"); }
            task.GetAwaiter().GetResult();
        }
        finally { SetSynchronizationContext(previous); }
    }
}
namespace DeltaNFD
{
    public static class App { public static Window? MainWindow => null; }
    public sealed class Window { public void NavigateByTag(string tag) { } }
}
namespace DeltaNFD.Views
{
    public sealed partial class LowMemoryRecoveryPage
    {
        internal readonly Button BackButton = new(), RefreshButton = new(), ApplyButton = new(), PrefetchRestoreButton = new();
        internal readonly ToggleSwitch PrefetchToggle = new();
        internal readonly ProgressRing BusyRing = new();
        internal readonly InfoBar StatusBar = new();
        internal readonly TextBlock ServiceText = new(), CompressionText = new(), CombiningText = new(), PrefetchText = new(),
            PrefetchHint = new(), ProgressText = new(), ResultText = new();
        internal readonly ProgressBar Progress = new();
        private void InitializeComponent() => PrefetchToggle.Toggled += Prefetch_Toggled;
        internal void RestoreOriginal() => PrefetchRestore_Click(this, new());
    }
}
