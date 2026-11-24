using System.Runtime.InteropServices;
using DeltaNFD;
using DeltaNFD.Services;
using DeltaNFD.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;

internal static class Program
{
    private const uint SysCommand = 0x0112;
    private const int Minimize = 0xF020, Maximize = 0xF030, Restore = 0xF120;
    private static int _passed;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2 || args.Any(p => !File.Exists(p))) return 2;
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(ignored =>
        {
            var queue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
            _ = new TestApp(queue, args);
        });
        return Environment.ExitCode;
    }

    private sealed class TestApp : Application
    {
        internal TestApp(DispatcherQueue queue, string[] args)
        {
            UnhandledException += (_, e) =>
            {
                Console.Error.WriteLine(e.Exception); e.Handled = true; Environment.ExitCode = 1; Exit();
            };
            queue.TryEnqueue(async () =>
            {
                try { await Run(args); Console.WriteLine($"TOTAL {_passed} WinUI presentation checks passed (isolated window)."); }
                catch (Exception e) { Console.Error.WriteLine(e); Environment.ExitCode = 1; }
                finally { Exit(); }
            });
        }
    }

    private static async Task Run(string[] args)
    {
        BackgroundManager.Picture = Path.GetFullPath(args[0]);
        var window = new MainWindow();
        var process = new ProcessPage();
        window.Page = process;
        try
        {
            Check(!window.IsUiPresented && window.Bitmap is null, "no bitmap on initially hidden window");
            Check(window.NativeHookInstalled, "native hook installed");
            window.AppWindow.Show(activateWindow: false);
            await Until(() => window.IsUiPresented && window.Bitmap is { PixelWidth: > 0 } && process.DisplayPolling, "show loads bitmap and starts display polling");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var original = window.Bitmap;
            SendMessage(hwnd, 0x0006, 0, 0); // WA_INACTIVE, not a minimize notification.
            await Task.Delay(150);
            Check(window.IsUiPresented && ReferenceEquals(original, window.Bitmap) && process.DisplayPolling, "deactivation does not suspend a shown window");
            original = null;

            // The remaining transitions must work even with no AppWindow changed events.
            window.DisconnectAppWindowSignals();
            var backgroundCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SendMessage(hwnd, SysCommand, Minimize, 0);
            await Until(() => IsIconic(hwnd) && !window.IsUiPresented && window.Bitmap is null && !process.DisplayPolling, "taskbar minimize releases image and pauses polling via native hook alone");
            Check(IsWindowVisible(hwnd) && ReferenceEquals(window.Page, process), "minimized window retains WS_VISIBLE and page identity");
            _ = Task.Run(() => backgroundCompleted.SetResult());
            await backgroundCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(!window.IsUiPresented, "independent work completed while still minimized");
            BackgroundManager.SetPicture(null);
            Check(window.Bitmap is null, "disabling background while minimized does not decode it");
            BackgroundManager.SetPicture(Path.GetFullPath(args[1]));
            Check(window.Bitmap is null, "changing background while minimized does not decode it");
            SendMessage(hwnd, SysCommand, Restore, 0);
            await Until(() => window.IsUiPresented && window.Bitmap is { PixelWidth: > 0 } && process.DisplayPolling, "restore decodes latest image and resumes poll");
            Check(window.Bitmap?.UriSource.LocalPath == Path.GetFullPath(args[1]), "restored image uses newest path");
            SendMessage(hwnd, SysCommand, Maximize, 0);
            await Until(() => IsZoomed(hwnd) && window.IsUiPresented && window.Bitmap is { PixelWidth: > 0 } && process.DisplayPolling, "maximized window stays active");

            window.AppWindow.Hide();
            await Until(() => !window.IsUiPresented && window.Bitmap is null && !process.DisplayPolling, "tray-style hiding still releases bitmap and pauses polling");
            var clock = new OptimalClockPage();
            window.Page = clock;
            await Task.Delay(100);
            Check(!clock.DisplayPolling && !process.DisplayPolling, "switching pages while hidden does not resume either page's display poll");
            window.AppWindow.Show(activateWindow: false);
            await Until(() => window.IsUiPresented && window.Bitmap is { PixelWidth: > 0 } && clock.DisplayPolling, "show resumes only current page");
            Check(!process.DisplayPolling && ReferenceEquals(window.Page, clock), "previous page stays paused");
            for (var i = 0; i < 4; i++)
            {
                SendMessage(hwnd, SysCommand, Minimize, 0);
                await Until(() => !window.IsUiPresented && window.Bitmap is null && !clock.DisplayPolling, $"repeat minimize {i + 1}");
                SendMessage(hwnd, SysCommand, Restore, 0);
                await Until(() => window.IsUiPresented && window.Bitmap is { PixelWidth: > 0 } && clock.DisplayPolling, $"repeat restore {i + 1}");
            }
            SendMessage(hwnd, SysCommand, Minimize, 0);
            await Until(() => !clock.DisplayPolling && window.Bitmap is null, "pause before closing fixture");
            await CheckWindowPlacement();
        }
        finally { window.Close(); }
        BackgroundManager.SetPicture(null);
        Check(!window.NativeHookInstalled && !window.IsUiPresented && BackgroundManager.ListenerCount == 0, "close detaches native hook and background listener");
    }

    private static async Task CheckWindowPlacement()
    {
        var primary = DisplayArea.Primary;
        var work = primary.WorkArea;
        work.X += primary.OuterBounds.X;
        work.Y += primary.OuterBounds.Y;
        var width = Math.Min(640, work.Width - 80);
        var height = Math.Min(420, work.Height - 80);
        var first = new MainWindowPlacement(work.X + 20, work.Y + 20, width, height, false);
        var writes = new List<MainWindowPlacement>();
        var window = new MainWindow(first, placement => { writes.Add(placement); return true; });
        var bounds = new RectInt32(first.X + 28, first.Y + 25, first.Width + 12, first.Height + 9);
        var moved = new MainWindowPlacement(bounds.X, bounds.Y, bounds.Width, bounds.Height, false);
        try
        {
            Check(CurrentBounds(window).Equals(new RectInt32(first.X, first.Y, first.Width, first.Height)) && !window.IsUiPresented,
                "normal bounds restored before showing");
            window.ShowPlacement();
            window.AppWindow.MoveAndResize(new RectInt32(bounds.X - 4, bounds.Y, bounds.Width, bounds.Height));
            window.AppWindow.MoveAndResize(bounds);
            await Until(() => writes.Count > 0, "moving and resizing schedules a write");
            Check(writes.SequenceEqual([moved]), "rapid changes save the last size and position once");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            SendMessage(hwnd, SysCommand, Maximize, 0);
            var maximized = moved with { Maximized = true };
            await Until(() => writes.LastOrDefault() == maximized, "maximize preserves the normal rectangle");
            SendMessage(hwnd, SysCommand, Minimize, 0);
            Check(IsIconic(hwnd), "test window is minimized");
            window.FlushWindowPlacement();
            Check(writes.SequenceEqual([moved, maximized]), "minimized state does not overwrite last visible placement");

            var serialized = System.Text.Json.JsonSerializer.Serialize(writes[^1]);
            var saved = System.Text.Json.JsonSerializer.Deserialize<MainWindowPlacement>(serialized);
            var reopenedWrites = new List<MainWindowPlacement>();
            var reopened = new MainWindow(saved, placement => { reopenedWrites.Add(placement); return true; });
            try
            {
                reopened.FlushWindowPlacement();
                Check(!reopened.IsUiPresented && CurrentBounds(reopened).Equals(bounds),
                    "a new hidden window restores normal bounds without showing maximized");
                reopened.ShowPlacement();
                var reopenedHwnd = WinRT.Interop.WindowNative.GetWindowHandle(reopened);
                await Until(() => IsZoomed(reopenedHwnd), "opening again restores maximized mode");
                reopened.FlushWindowPlacement();
                Check(reopenedWrites.Count == 0, "hidden launch and showing an unchanged placement do not write settings");
                SendMessage(reopenedHwnd, SysCommand, Restore, 0);
                Check(CurrentBounds(reopened).Equals(bounds), "unmaximize returns to the remembered position and size");
                reopened.FlushWindowPlacement();
                Check(reopenedWrites.SequenceEqual([moved]), "restored normal mode is saved for the next launch");
            }
            finally { reopened.Close(); }
        }
        finally { window.Close(); }

        var missingDisplay = new MainWindow(first with { X = 100000, Y = 100000 }, _ => true);
        try
        {
            var expected = MainWindow.FitWindowBounds(new RectInt32(100000, 100000, width, height), work, center: true);
            Check(CurrentBounds(missingDisplay).Equals(expected), "a disconnected monitor returns the window to the primary work area");
        }
        finally { missingDisplay.Close(); }
        var leftMonitor = new RectInt32(-1280, 40, 1280, 760);
        Check(MainWindow.FitWindowBounds(new RectInt32(-1150, 75, 700, 500), leftMonitor, center: false)
            .Equals(new RectInt32(-1150, 75, 700, 500)), "negative monitor coordinates remain exact");
        Check(MainWindow.FitWindowBounds(new RectInt32(-1400, -50, 1600, 900), leftMonitor, center: false)
            .Equals(leftMonitor), "a smaller work area clamps the whole window within reach");
        var rightMonitor = new RectInt32(1920, -200, 1600, 900);
        Check(MainWindow.MoveWindowBoundsToDisplay(new RectInt32(-1150, 75, 700, 500), leftMonitor, rightMonitor)
            .Equals(new RectInt32(2050, -165, 700, 500)), "moving a maximized window also moves its normal rectangle to the new monitor");
    }

    private static RectInt32 CurrentBounds(MainWindow window)
    {
        var position = window.AppWindow.Position;
        var size = window.AppWindow.Size;
        return new RectInt32(position.X, position.Y, size.Width, size.Height);
    }

    private static async Task Until(Func<bool> condition, string label)
    {
        var limit = Environment.TickCount64 + 4500;
        while (!condition() && Environment.TickCount64 < limit) await Task.Delay(30);
        Check(condition(), label);
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        _passed++;
        Console.WriteLine("PASS: " + label);
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint hwnd, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint hwnd);
}

namespace DeltaNFD
{
    public sealed partial class MainWindow : Window
    {
        private readonly Image BgImage = new();
        private readonly Border BgDim = new();
        private readonly Frame ContentFrame = new();

        internal MainWindow(MainWindowPlacement? placement = null, Func<MainWindowPlacement, bool>? save = null)
        {
            Title = "DeltaNFD 隔离测试窗口";
            Content = new Grid { Children = { BgImage, BgDim, ContentFrame } };
            if (save is null) AppWindow.Resize(new Windows.Graphics.SizeInt32(768, 480));
            else InitializeWindowPlacement(placement, save);
            InitializeWindowPresentation();
        }

        internal void ShowPlacement()
        {
            AppWindow.Show(activateWindow: false);
            RestoreMaximizationWhenShown();
        }

        internal object Page { get => ContentFrame.Content; set => ContentFrame.Content = value; }
        internal BitmapImage? Bitmap => BgImage.Source as BitmapImage;
        internal bool NativeHookInstalled => _presentationHook is not null;
        internal void DisconnectAppWindowSignals() => AppWindow.Changed -= AppWindow_PresentationChanged;
    }
}

namespace DeltaNFD.Services
{
    internal static class BackgroundManager
    {
        internal static string? Picture { get; set; }
        internal static event Action? Changed;
        internal static string? ResolveCurrent() => Picture;
        internal static double DimOpacity => 0.55;
        internal static int ListenerCount => Changed?.GetInvocationList().Length ?? 0;
        internal static void SetPicture(string? path) { Picture = path; Changed?.Invoke(); }
    }

    internal static class Log
    {
        internal static void Warn(string text) => Console.Error.WriteLine(text);
    }
}

namespace DeltaNFD.Views
{
    public sealed class ProcessPage : Page
    {
        internal bool DisplayPolling { get; private set; }
        internal void SetWindowPresented(bool presented) => DisplayPolling = presented;
    }

    public sealed class OptimalClockPage : Page
    {
        internal bool DisplayPolling { get; private set; }
        internal void SetWindowPresented(bool presented) => DisplayPolling = presented;
    }
}
