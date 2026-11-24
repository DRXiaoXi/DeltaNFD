using DeltaNFD.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace DeltaNFD;

public sealed partial class MainWindow
{
    private MainWindowPlacement? _observedWindowPlacement;
    private MainWindowPlacement? _savedWindowPlacement;
    private DispatcherQueueTimer? _windowPlacementTimer;
    private Func<MainWindowPlacement, bool>? _saveWindowPlacement;
    private bool _maximizeOnFirstShow;
    private bool _hasVisibleWindowPlacement;

    private void InitializeWindowPlacement(MainWindowPlacement? saved, Func<MainWindowPlacement, bool> save)
    {
        _saveWindowPlacement = save;
        _savedWindowPlacement = saved;

        try
        {
            var hasSavedBounds = HasValidBounds(saved);
            var requested = hasSavedBounds ? new RectInt32(saved!.X, saved.Y, saved.Width, saved.Height)
                : new RectInt32(0, 0, 1280, 820);
            var savedDisplay = hasSavedBounds ? DisplayArea.GetFromRect(requested, DisplayAreaFallback.None) : null;
            var display = savedDisplay ?? (hasSavedBounds ? DisplayArea.Primary
                : DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary));
            if (display is null) AppWindow.Resize(new SizeInt32(1280, 820));
            else AppWindow.MoveAndResize(FitWindowBounds(requested, GetScreenWorkArea(display), center: savedDisplay is null));
            _maximizeOnFirstShow = hasSavedBounds && saved!.Maximized;
        }
        catch (Exception ex)
        {
            Log.Warn("恢复主窗口位置失败：" + ex.Message);
            AppWindow.Resize(new SizeInt32(1280, 820));
        }

        _observedWindowPlacement = ReadRestoredBounds();
        _windowPlacementTimer = DispatcherQueue.CreateTimer();
        _windowPlacementTimer.Interval = TimeSpan.FromMilliseconds(550);
        _windowPlacementTimer.IsRepeating = false;
        _windowPlacementTimer.Tick += (_, _) => FlushWindowPlacement();
        AppWindow.Changed += AppWindow_PlacementChanged;
        Closed += (_, _) =>
        {
            FlushWindowPlacement();
            _windowPlacementTimer = null;
            _saveWindowPlacement = null;
            AppWindow.Changed -= AppWindow_PlacementChanged;
        };
    }

    internal static RectInt32 FitWindowBounds(RectInt32 requested, RectInt32 work, bool center)
    {
        var width = Math.Min(requested.Width, work.Width);
        var height = Math.Min(requested.Height, work.Height);
        var maxX = work.X + (work.Width - width);
        var maxY = work.Y + (work.Height - height);
        return new RectInt32(
            center ? work.X + (work.Width - width) / 2 : Math.Clamp(requested.X, work.X, maxX),
            center ? work.Y + (work.Height - height) / 2 : Math.Clamp(requested.Y, work.Y, maxY),
            width, height);
    }

    private static RectInt32 GetScreenWorkArea(DisplayArea display)
    {
        var work = display.WorkArea;
        var outer = display.OuterBounds;
        // WorkArea's origin is relative to the display. AppWindow uses screen coordinates.
        work.X += outer.X;
        work.Y += outer.Y;
        return work;
    }

    internal static RectInt32 MoveWindowBoundsToDisplay(RectInt32 bounds, RectInt32? previousWork, RectInt32 currentWork)
    {
        if (previousWork is not { } previous) return FitWindowBounds(bounds, currentWork, center: true);
        bounds.X = currentWork.X + (bounds.X - previous.X);
        bounds.Y = currentWork.Y + (bounds.Y - previous.Y);
        return FitWindowBounds(bounds, currentWork, center: false);
    }

    private static bool HasValidBounds(MainWindowPlacement? placement) => placement is { Width: > 0, Height: > 0 }
        && (long)placement.X + placement.Width <= int.MaxValue && (long)placement.Y + placement.Height <= int.MaxValue;

    private void RestoreMaximizationWhenShown()
    {
        if (_maximizeOnFirstShow)
        {
            try
            {
                if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
            }
            catch (Exception ex) { Log.Warn("恢复主窗口最大化状态失败：" + ex.Message); }
            finally { _maximizeOnFirstShow = false; }
        }
        CaptureWindowPlacement();
    }

    private void AppWindow_PlacementChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange || args.DidVisibilityChange || args.DidPresenterChange)
            CaptureWindowPlacement();
    }

    private MainWindowPlacement ReadRestoredBounds()
    {
        var position = AppWindow.Position;
        var size = AppWindow.Size;
        return new MainWindowPlacement(position.X, position.Y, size.Width, size.Height, Maximized: false);
    }

    private MainWindowPlacement CaptureMaximizedPlacement(MainWindowPlacement normal)
    {
        var maximized = normal with { Maximized = true };
        try
        {
            var current = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.None);
            if (current is null) return maximized;
            var bounds = new RectInt32(normal.X, normal.Y, normal.Width, normal.Height);
            var previous = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.None);
            if (previous?.DisplayId.Value == current.DisplayId.Value) return maximized;
            // A maximized window can move between displays without returning to the restored state.
            var moved = MoveWindowBoundsToDisplay(bounds, previous is null ? null : GetScreenWorkArea(previous), GetScreenWorkArea(current));
            return new MainWindowPlacement(moved.X, moved.Y, moved.Width, moved.Height, Maximized: true);
        }
        catch (Exception ex)
        {
            Log.Warn("未能读取最大化窗口所在的显示器：" + ex.Message);
            return maximized;
        }
    }

    private void CaptureWindowPlacement()
    {
        if (_windowPlacementTimer is not { } timer || _maximizeOnFirstShow
            || !IsUiPresented
            || AppWindow.Presenter is not OverlappedPresenter presenter) return;

        var placement = presenter.State switch
        {
            OverlappedPresenterState.Restored => ReadRestoredBounds(),
            OverlappedPresenterState.Maximized when _observedWindowPlacement is { } normal => CaptureMaximizedPlacement(normal),
            _ => null,
        };
        if (!HasValidBounds(placement) || _hasVisibleWindowPlacement && placement == _observedWindowPlacement) return;

        _hasVisibleWindowPlacement = true;
        _observedWindowPlacement = placement;
        timer.Stop();
        if (placement != _savedWindowPlacement) timer.Start();
    }

    internal void FlushWindowPlacement()
    {
        CaptureWindowPlacement();
        _windowPlacementTimer?.Stop();
        if (!_hasVisibleWindowPlacement || _observedWindowPlacement is not { } placement
            || placement == _savedWindowPlacement || _saveWindowPlacement is not { } save) return;
        try
        {
            if (save(placement)) _savedWindowPlacement = placement;
        }
        catch (Exception ex) { Log.Warn("保存主窗口位置失败：" + ex.Message); }
    }
}
