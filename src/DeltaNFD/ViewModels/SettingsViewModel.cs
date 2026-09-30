using CommunityToolkit.Mvvm.ComponentModel;
using DeltaNFD.Services;
using Microsoft.UI.Xaml;

namespace DeltaNFD.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IAppControlService _appControl = ServiceLocator.AppControl;

    [ObservableProperty]
    private int selectedThemeIndex;

    /// <summary>背景图片模式（false = Mica 云母背景 + 界面锁定深色主题）。</summary>
    [ObservableProperty]
    private bool backgroundImageEnabled = true;

    /// <summary>明暗主题下拉是否可选：背景图片模式开启时锁定深色，禁止选择。</summary>
    public bool ThemeSelectionEnabled => !BackgroundImageEnabled;

    [ObservableProperty]
    private bool autoStart;

    [ObservableProperty]
    private bool minimizeToTray;

    [ObservableProperty]
    private string generalStatusText = "";

    /// <summary>显示版本（唯一来源：csproj 的 InformationalVersion，已去掉 SourceLink 提交哈希后缀）。</summary>
    public string VersionText => AppVersion.Text;

    private bool _suppressAutoStartChanged;
    private bool _suppressTrayChanged;

    public SettingsViewModel()
    {
        var settings = AppSettingsStore.Read();
        BackgroundImageEnabled = settings.BackgroundImageEnabled;

        // 初始化时不重复应用主题：先挂起变更再赋初值（从设置文件恢复；
        // 背景图片模式开启时显示为深色但不动用户保存的明暗偏好）
        _suppressThemeChanged = true;
        SelectedThemeIndex = BackgroundImageEnabled ? 1 : Math.Clamp(settings.AppThemeIndex, 0, 2);
        _sharedThemeIndex = SelectedThemeIndex;
        _suppressThemeChanged = false;

        // 异步读取真实状态（自启任务 / 设置文件）
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        // 先取值，仅在同步赋值窗口内抑制事件：suppress 跨 await 会让初始化期间
        // 用户拨动自启开关的操作被静默丢弃
        var autoStartValue = await _appControl.IsAutoStartEnabledAsync();
        _suppressAutoStartChanged = true;
        AutoStart = autoStartValue;
        _suppressAutoStartChanged = false;

        _suppressTrayChanged = true;
        MinimizeToTray = _appControl.CloseToTrayEnabled;
        _suppressTrayChanged = false;
    }

    private static int _sharedThemeIndex;

    private bool _suppressThemeChanged;

    partial void OnBackgroundImageEnabledChanged(bool value)
    {
        BackgroundManager.SetBackgroundImageEnabled(value);
        OnPropertyChanged(nameof(ThemeSelectionEnabled));

        // 背景图片模式开启 → 界面锁定深色（不覆盖用户保存的明暗偏好）；
        // 关闭 → 恢复用户保存的明暗主题
        _suppressThemeChanged = true;
        SelectedThemeIndex = value ? 1 : Math.Clamp(AppSettingsStore.Read().AppThemeIndex, 0, 2);
        _suppressThemeChanged = false;
        _sharedThemeIndex = SelectedThemeIndex;
        App.SetAppTheme(value ? ElementTheme.Dark : SelectedThemeIndex switch
        {
            0 => ElementTheme.Light,
            1 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        });
    }

    partial void OnSelectedThemeIndexChanged(int value)
    {
        if (_suppressThemeChanged)
        {
            return;
        }

        _sharedThemeIndex = value;

        // 背景图片模式开启时主题被锁定为深色：不持久化锁定值，保留用户偏好
        if (!BackgroundImageEnabled)
        {
            AppSettingsStore.Update(s => s.AppThemeIndex = value);
        }

        App.SetAppTheme(value switch
        {
            0 => ElementTheme.Light,
            1 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        });
    }

    partial void OnAutoStartChanged(bool value)
    {
        if (_suppressAutoStartChanged)
        {
            return;
        }

        // 真实创建/删除登录自启动计划任务（管理员权限，登录自启不弹 UAC）
        _ = ApplyAutoStartAsync(value);
    }

    private async Task ApplyAutoStartAsync(bool enable)
    {
        var result = await _appControl.SetAutoStartAsync(enable);
        if (!result.Success)
        {
            GeneralStatusText = result.Message;
            // 失败回拨开关
            _suppressAutoStartChanged = true;
            AutoStart = !enable;
            _suppressAutoStartChanged = false;
            return;
        }

        GeneralStatusText = enable ? "开机自动启动：已开启" : "开机自动启动：已关闭";
    }

    partial void OnMinimizeToTrayChanged(bool value)
    {
        if (_suppressTrayChanged)
        {
            return;
        }

        // 持久化；MainWindow 订阅服务的 PropertyChanged 即时应用
        _appControl.SetCloseToTray(value);
        GeneralStatusText = value ? "已开启关闭后最小化到托盘" : "已关闭最小化到托盘";
    }
}
