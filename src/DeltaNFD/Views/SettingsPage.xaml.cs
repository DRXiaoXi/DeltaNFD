using DeltaNFD.Services;
using DeltaNFD.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace DeltaNFD.Views;

/// <summary>主题色预设（设置页色板）。</summary>
public sealed class AccentOption
{
    public required string Name { get; init; }
    public required string Hex { get; init; }
    public required Windows.UI.Color Color { get; init; }
    public bool IsSelected { get; set; }
}

public sealed partial class SettingsPage : Page
{
    public string[] ThemeOptions { get; } = ["浅色", "深色", "跟随系统"];

    public SettingsViewModel ViewModel { get; } = new();

    /// <summary>主题色预设（含默认天空蓝与历史用色）。</summary>
    private static readonly (string Name, string Hex)[] AccentPresets =
    [
        ("天空蓝", "#5EB0FF"),
        ("军事橙", "#F97316"),
        ("薄荷绿", "#0FF796"),
        ("紫罗兰", "#A78BFA"),
        ("玫粉", "#F472B6"),
        ("琥珀", "#FBBF24"),
        ("青碧", "#22D3EE"),
        ("石墨", "#94A3B8"),
    ];

    /// <summary>程序化回写滑条时置 true，避免触发遮罩重复写设置。</summary>
    private bool _suppressDimSlider;

    public SettingsPage()
    {
        InitializeComponent();

        // 关于卡 logo：ms-appx 资源加载失败时回退到绝对路径文件
        AboutLogoImage.ImageFailed += (_, _) =>
        {
            try
            {
                var logoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "DeltaNFD.png");
                AboutLogoImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(logoPath));
            }
            catch
            {
                // logo 加载失败不影响页面功能
            }
        };

        // 初始滑条位置 = 当前遮罩浓度（程序化赋值期间抑制事件）
        _suppressDimSlider = true;
        DimSlider.Value = BackgroundManager.DimOpacity * 100;
        _suppressDimSlider = false;

        var current = BackgroundManager.ResolveCurrent();
        BackgroundStatusText.Text = DescribeBackground(current);
        // 背景图片模式开关切换后状态文字跟随刷新（否则显示与现实相反）
        Loaded += (_, _) =>
        {
            BackgroundManager.Changed -= OnBackgroundChanged;
            BackgroundManager.Changed += OnBackgroundChanged;
            OnBackgroundChanged();
        };
        Unloaded += (_, _) => BackgroundManager.Changed -= OnBackgroundChanged;

        BuildAccentSwatches();

        // 三角洲目录识别：回显手动指定值 + 当前识别结果
        var overridePath = AppSettingsStore.Read().GameRootOverride;
        GamePathBox.Text = overridePath ?? "";
        UpdateGamePathStatus();
    }

    private void OnBackgroundChanged() =>
        BackgroundStatusText.Text = DescribeBackground(BackgroundManager.ResolveCurrent());

    private static string DescribeBackground(string? current) => current is null
        ? "当前未使用背景图（Mica 云母背景）"
        : "当前背景：" + current;

    // ---------------- 主题色 ----------------

    private void BuildAccentSwatches()
    {
        var current = AppSettingsStore.Read().AccentColor ?? "";
        AccentSwatchList.Items.Clear();
        foreach (var (name, hex) in AccentPresets)
        {
            var color = App.TryParseColor(hex, out var c) ? c : App.DefaultAccent;
            AccentSwatchList.Items.Add(new AccentOption
            {
                Name = name,
                Hex = hex,
                Color = color,
                IsSelected = hex.Equals(current, StringComparison.OrdinalIgnoreCase),
            });
        }
    }

    private void AccentSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex } && App.TryParseColor(hex, out var color))
        {
            ApplyAccent(color);
        }
    }

    private async void CustomAccentButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ColorPicker
        {
            IsAlphaEnabled = false,
            IsColorSliderVisible = true,
            IsColorPreviewVisible = true,
            MinWidth = 260,
            Color = App.TryParseColor(AppSettingsStore.Read().AccentColor, out var current)
                ? current
                : App.DefaultAccent,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "自定义主题色",
            Content = picker,
            PrimaryButtonText = "应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ApplyAccent(picker.Color);
        }
    }

    private void ApplyAccent(Windows.UI.Color color)
    {
        App.SetAccentColor(color);

        // 色板勾选状态刷新 + 延迟重建当前页面（让 ToggleSwitch 等默认控件取到新强调色）
        BuildAccentSwatches();
        DispatcherQueue.TryEnqueue(() => App.MainWindow?.RefreshCurrentPage());
    }

    // ---------------- 三角洲目录识别 ----------------

    private void PickGameFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is null)
        {
            return;
        }

        try
        {
            // 管理员进程里 WinUI FolderPicker 会抛 COMException（系统限制），
            // 改用 COM IFileOpenDialog（提权/非提权均可用）
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            var folder = Native.FolderPickerDialog.Pick(handle, GamePathBox.Text?.Trim());
            if (string.IsNullOrEmpty(folder))
            {
                return;
            }

            GamePathBox.Text = folder;
        }
        catch (Exception ex)
        {
            GamePathStatusText.Text = "系统选择器不可用，请直接在上方输入框粘贴游戏目录后保存。";
            Services.Log.Error("设置：游戏目录选择器失败", ex);
        }
    }

    private void SaveGamePathButton_Click(object sender, RoutedEventArgs e)
    {
        var path = GamePathBox.Text?.Trim() ?? "";
        if (path.Length > 0)
        {
            if (!DeltaForceLocator.TryGetValidGameRoot(path, out var normalizedPath))
            {
                GamePathStatusText.Text = "所选目录中未找到三角洲游戏文件，请选择游戏安装目录（包含三角洲游戏程序或 Content/Paks）。";
                return;
            }

            path = normalizedPath;
            GamePathBox.Text = normalizedPath;
        }

        AppSettingsStore.Update(s => s.GameRootOverride = path);
        UpdateGamePathStatus();
    }

    private void ClearGamePathButton_Click(object sender, RoutedEventArgs e)
    {
        GamePathBox.Text = "";
        AppSettingsStore.Update(s => s.GameRootOverride = "");
        UpdateGamePathStatus();
    }

    private void UpdateGamePathStatus()
    {
        var overridePath = AppSettingsStore.Read().GameRootOverride ?? "";
        var roots = DeltaForceLocator.FindRoots().ToList();

        string manualPart = string.IsNullOrWhiteSpace(overridePath)
            ? "手动指定：未设置（自动识别）"
            : $"手动指定：{overridePath}";

        string detectPart = roots.Count == 0
            ? "当前识别：未找到游戏目录"
            : "当前识别：" + string.Join("；", roots);

        GamePathStatusText.Text = manualPart + "\n" + detectPart;
    }

    // ---------------- 背景 ----------------

    private async void PickBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is null)
        {
            return;
        }

        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".webp");

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            BackgroundManager.SetCustomImage(file.Path);
            BackgroundStatusText.Text = "背景已更新：" + file.Name;
        }
        catch (Exception ex)
        {
            BackgroundStatusText.Text = "设置背景失败：" + ex.Message;
        }
    }

    private void ResetBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        BackgroundManager.ResetToDefault();
        var current = BackgroundManager.ResolveCurrent();
        BackgroundStatusText.Text = current is null
            ? "已恢复默认（Mica 云母背景）"
            : "已恢复内置背景：" + current;
    }

    private void DimSlider_ValueChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressDimSlider)
        {
            return;
        }

        BackgroundManager.SetDimOpacity(DimSlider.Value / 100);
    }

    private void OpenLogFolderButton_Click(object sender, RoutedEventArgs e)
    {
        Services.Log.OpenFolderInExplorer();
    }
}
