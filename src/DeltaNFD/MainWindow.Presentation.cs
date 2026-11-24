using DeltaNFD.Native;
using DeltaNFD.Services;
using DeltaNFD.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DeltaNFD;

public sealed partial class MainWindow
{
    private nint _presentationWindow;
    private WindowPresentationHook? _presentationHook;
    private bool _presentationRefreshQueued;
    private bool _uiWasPresented;

    // Losing focus is not minimizing: keep a visible window usable beside other windows.
    internal bool IsUiPresented => WindowPresentationHook.IsPresented(_presentationWindow);

    private void InitializeWindowPresentation()
    {
        _presentationWindow = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _uiWasPresented = IsUiPresented;
        _presentationHook = WindowPresentationHook.Attach(_presentationWindow, QueuePresentationRefresh);
        if (_presentationHook is null) Log.Warn("未能监听窗口最小化通知，将使用常规窗口变更通知。");
        ApplyBackgroundImage();
        BackgroundManager.Changed += ApplyBackgroundImage;
        AppWindow.Changed += AppWindow_PresentationChanged;
        Closed += (_, _) =>
        {
            CancelTrayWorkingSetTrim();
            _presentationWindow = 0;
            _presentationHook?.Dispose();
            _presentationHook = null;
            BackgroundManager.Changed -= ApplyBackgroundImage;
            AppWindow.Changed -= AppWindow_PresentationChanged;
        };
    }

    private void AppWindow_PresentationChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidVisibilityChange || args.DidSizeChange || args.DidPresenterChange) QueuePresentationRefresh();
    }

    private void QueuePresentationRefresh()
    {
        // Run after Win32 completes the transition; AppWindow.Size is then the restored size.
        if (_presentationWindow == 0 || _presentationRefreshQueued) return;
        _presentationRefreshQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _presentationRefreshQueued = false;
            if (_presentationWindow != 0) RefreshWindowPresentation();
        });
    }

    private void RefreshWindowPresentation()
    {
        var presented = IsUiPresented;
        if (!presented) ReleaseBackgroundImage();
        else if (BgImage.Visibility == Visibility.Visible && _backgroundPath is { } path)
        {
            // Reuse the current path instead of reading settings on every resize.
            try { LoadBackgroundBitmap(path, BackgroundDecodeWidth()); }
            catch { ReleaseBackgroundImage(); }
        }

        if (_uiWasPresented == presented) return;
        _uiWasPresented = presented;
        // Only page-display polling. Do not unload the current page or touch backend ownership.
        switch (ContentFrame.Content)
        {
            case ProcessPage page: page.SetWindowPresented(presented); break;
            case OptimalClockPage page: page.SetWindowPresented(presented); break;
        }
    }

    /// <summary>
    /// 应用背景：按 BackgroundManager 解析（自定义 > 内置 Assets\background.*）显示图片，
    /// 并按设置里的遮罩浓度叠暗色遮罩保证可读；无背景时维持 Mica。
    /// </summary>
    private void ApplyBackgroundImage()
    {
        try
        {
            var path = BackgroundManager.ResolveCurrent();
            if (path is null)
            {
                ReleaseBackgroundImage();
                _backgroundPath = null;
                BgImage.Visibility = Visibility.Collapsed;
                BgDim.Visibility = Visibility.Collapsed;
                return;
            }

            // 设置变更（含同名替换自定义图）一律重新解码；窗口隐藏或最小化时推迟到恢复显示。
            ReleaseBackgroundImage();
            if (path != _backgroundPath) _backgroundNaturalWidth = 0;
            _backgroundPath = path;
            if (IsUiPresented) LoadBackgroundBitmap(path, BackgroundDecodeWidth());
            BgImage.Visibility = Visibility.Visible;

            var alpha = (byte)Math.Round(255 * BackgroundManager.DimOpacity);
            BgDim.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, 0, 0, 0));
            BgDim.Visibility = alpha == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        catch
        {
            // 背景加载失败不影响主功能（保持 Mica）
            ReleaseBackgroundImage();
            BgImage.Visibility = Visibility.Collapsed;
            BgDim.Visibility = Visibility.Collapsed;
        }
    }

    private string? _backgroundPath;
    private int _backgroundDecodeWidth;
    private int _backgroundNaturalWidth;

    /// <summary>按窗口物理像素宽度解码（320 档位，上限 2560）；UniformToFill 时按 16:10 换算高度需求。</summary>
    private int BackgroundDecodeWidth()
    {
        var size = AppWindow.Size;
        var needed = Math.Max(size.Width, (int)Math.Ceiling(size.Height * 1.6));
        var width = Math.Clamp((needed + 319) / 320 * 320, 960, 2560);
        return _backgroundNaturalWidth > 0 ? Math.Min(width, _backgroundNaturalWidth) : width;
    }

    private void LoadBackgroundBitmap(string path, int decodeWidth)
    {
        if (BgImage.Source is not null && decodeWidth <= _backgroundDecodeWidth) return;
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelWidth = decodeWidth };
        bitmap.ImageOpened += (_, _) =>
        {
            // 记录原始宽度，之后不再放大解码。
            if (ReferenceEquals(BgImage.Source, bitmap) && bitmap.PixelWidth > 0) _backgroundNaturalWidth = bitmap.PixelWidth;
        };
        bitmap.UriSource = new Uri(path);
        BgImage.Source = bitmap;
        _backgroundDecodeWidth = decodeWidth;
    }

    private void ReleaseBackgroundImage()
    {
        if (BgImage.Source is not null) BgImage.Source = null;
        _backgroundDecodeWidth = 0;
    }
}
