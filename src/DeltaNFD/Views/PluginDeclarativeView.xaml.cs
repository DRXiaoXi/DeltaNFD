using System.Text;
using DeltaNFD.Services;
using DeltaNFD.Services.Plugins;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

/// <summary>
/// 共享声明式页面（规范第 4.1、4 节）：按插件包内 ui.json 渲染标准 WinUI 控件。
/// 由插件管理页与侧栏独立入口共用，读取同一份宿主状态与配置，不复制授权/运行记录、不新增后端实例。
/// 只做“导航 + 渲染 + 执行明确操作”：设置条目、切页、加载都不启动或重连后端；
/// 离开视图只取消本次读取并丢弃旧回调，不停止持续插件、不撤销授权、不恢复系统设置。
/// </summary>
public sealed partial class PluginDeclarativeView : UserControl
{
    private PluginUi? _ui;
    private PluginIndexEntry? _entry;
    private IReadOnlyDictionary<string, object?> _configuredValues = new Dictionary<string, object?>();
    private bool _configurationWritable;
    private readonly Dictionary<string, Func<object?>> _valueReaders = new(StringComparer.Ordinal);
    /// <summary>代次：条目切换/清空/离页后，旧异步回调据此失效，不写到新页面的控件上。</summary>
    private int _generation;

    public PluginDeclarativeView()
    {
        InitializeComponent();
        Unloaded += (_, _) => Clear();
    }

    /// <summary>当前渲染的插件 ID；未渲染时为 null。路由身份始终是插件 ID，不用显示名。</summary>
    public string? PluginId => _entry?.Id;

    /// <summary>最近一次操作结果文本（供宿主页面在需要时读取；不参与状态判定）。</summary>
    public string Status => StatusText.Text;

    /// <summary>
    /// 渲染指定插件的声明式界面。entry 为 null 或状态/完整性不允许时清空表单并显示宿主诊断，
    /// 不渲染未验证 UI。禁用、未授权、不兼容、待恢复/未知状态均不得执行操作。
    /// </summary>
    public void SetEntry(PluginIndexEntry? entry)
    {
        SaveCurrentConfiguration();
        _generation++;
        ResetForm();
        _entry = entry;
        if (entry is null)
        {
            StatusText.Text = "未选择插件。";
            return;
        }

        var manifest = TryReadManifest(entry, out var manifestError);
        var compatible = manifest?.IsCompatible(AppVersion.Current) ?? false;
        if (manifest is null)
        {
            ShowNotice("无法渲染插件界面：" + manifestError, isError: true);
            StatusText.Text = "仅显示宿主诊断；未渲染未验证的插件界面。";
            return;
        }
        if (!compatible)
        {
            ShowNotice($"插件“{entry.Name}”与当前宿主版本不兼容，仅展示详情，不能执行操作。", isError: true);
            StateFor(entry, allowExecute: false);
            return;
        }
        if (entry.State == PluginPackageState.PendingRestore)
        {
            ShowNotice("该插件存在待恢复/未知运行记录，已禁用执行；请在插件管理页完成恢复。", isError: true);
            StateFor(entry, allowExecute: false);
            return;
        }
        if (entry.State != PluginPackageState.Authorized)
        {
            ShowNotice("插件已导入但未授权/已停用，仅展示界面；请在插件管理页完成授权后再执行。", isError: false);
            StateFor(entry, allowExecute: false);
            return;
        }

        try
        {
            using var lease = PluginPackageIntegrity.VerifyAndLock(entry);
            var verifiedManifest = PluginManifestParser.Parse(lease.ReadJson("manifest.json"));
            if (verifiedManifest.Id != entry.Id || verifiedManifest.Version != entry.Version || !verifiedManifest.IsCompatible(AppVersion.Current))
                throw new InvalidDataException("插件清单身份不符。");
            _ui = PluginUiParser.Parse(lease.ReadJson("ui.json"), verifiedManifest);
        }
        catch (Exception ex)
        {
            ShowNotice("插件界面未通过完整性校验，仅显示宿主诊断：" + ex.GetType().Name, isError: true);
            StateFor(entry, allowExecute: false);
            return;
        }

        _configuredValues = _ui.DefaultValues();
        _configurationWritable = true;
        var configNotice = "";
        if (!ServiceLocator.Plugins.TryReadConfig(entry.Id, out var config, out var configError))
        { configNotice = configError; _configurationWritable = false; }
        else
        {
            try { _configuredValues = PluginUiParser.ReadConfiguration(_ui, Encoding.UTF8.GetBytes(config)); }
            catch (PluginContractException ex)
            { _configurationWritable = false; configNotice = "保存的配置无效，已展示默认值；未覆盖原文件：" + ex.Message; }
        }
        foreach (var control in _ui.Controls)
            FormPanel.Children.Add(BuildControl(control));
        FormPanel.Visibility = FormPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = configNotice;
        SaveConfigButton.Visibility = Visibility.Visible;
    }

    /// <summary>清空表单并释放文件锁与控件引用（离页/切换条目时调用）。</summary>
    public void Clear()
    {
        SaveCurrentConfiguration();
        _generation++;
        ResetForm();
        _entry = null;
        StatusText.Text = "";
    }

    private void ResetForm()
    {
        FormPanel.Children.Clear();
        _valueReaders.Clear();
        _ui = null;
        _configurationWritable = false;
        SaveConfigButton.Visibility = Visibility.Collapsed;
        FormPanel.Visibility = Visibility.Collapsed;
        NoticeText.Visibility = Visibility.Collapsed;
        NoticeText.Text = "";
    }

    private void StateFor(PluginIndexEntry entry, bool allowExecute)
    {
        // 表单已清空（allowExecute=false 时）；此处仅保留明确说明，防止误以为可执行。
        _ = entry;
        _ = allowExecute;
        FormPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowNotice(string text, bool isError)
    {
        NoticeText.Text = (isError ? "⚠ " : "ℹ ") + text;
        NoticeText.Visibility = Visibility.Visible;
    }

    private static PluginManifest? TryReadManifest(PluginIndexEntry entry, out string error)
    {
        error = "";
        try
        {
            using var snapshot = PluginPackageIntegrity.VerifyAndLock(entry);
            return PluginManifestParser.Parse(snapshot.ReadJson("manifest.json"));
        }
        catch (PluginContractException ex) { error = ex.Message; return null; }
        catch (Exception ex) { error = "读取 manifest 失败：" + ex.GetType().Name; return null; }
    }

    private UIElement BuildControl(PluginControl control)
    {
        if (_configuredValues.TryGetValue(control.Id, out var saved)) control = control.Type switch
        {
            PluginControlType.Input => control with { DefaultInput = saved as string },
            PluginControlType.Number => control with { DefaultNumber = Convert.ToDouble(saved) },
            PluginControlType.Toggle => control with { DefaultToggle = saved as bool? },
            PluginControlType.Select => control with { DefaultSelect = saved as string },
            _ => control,
        };
        switch (control.Type)
        {
            case PluginControlType.Text:
                return new TextBlock
                {
                    Text = control.Text ?? "",
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                };
            case PluginControlType.Input:
            {
                var box = new TextBox { Header = control.Label, Text = control.DefaultInput ?? "", MaxLength = PluginContract.MaxFormValueChars };
                _valueReaders[control.Id] = () => box.Text;
                return box;
            }
            case PluginControlType.Number:
            {
                var box = new NumberBox
                {
                    Header = control.Label,
                    Value = control.DefaultNumber ?? 0,
                    Minimum = control.Min ?? double.MinValue,
                    Maximum = control.Max ?? double.MaxValue,
                    SmallChange = control.Step ?? 1,
                };
                _valueReaders[control.Id] = () => box.Value;
                return box;
            }
            case PluginControlType.Toggle:
            {
                var toggle = new ToggleSwitch { Header = control.Label, IsOn = control.DefaultToggle ?? false, OnContent = "开", OffContent = "关" };
                _valueReaders[control.Id] = () => toggle.IsOn;
                return toggle;
            }
            case PluginControlType.Select:
            {
                var combo = new ComboBox { Header = control.Label, PlaceholderText = "请选择" };
                if (control.Options is not null)
                    foreach (var option in control.Options)
                        combo.Items.Add(option.Item2);
                if (control.DefaultSelect is { } defaultValue && control.Options is not null)
                    for (var i = 0; i < control.Options.Count; i++)
                        if (control.Options[i].Item1 == defaultValue) { combo.SelectedIndex = i; break; }
                _valueReaders[control.Id] = () => control.Options is not null && combo.SelectedIndex >= 0
                    ? control.Options[combo.SelectedIndex].Item1 : null;
                return combo;
            }
            case PluginControlType.Button:
            {
                var button = new Button { Content = control.Label ?? control.OperationId };
                var operationId = control.OperationId!;
                button.Click += async (_, _) => await RunOperationAsync(operationId);
                return button;
            }
            case PluginControlType.Progress:
                return new ProgressBar { Minimum = 0, Maximum = 100 };
            case PluginControlType.Result:
                return new TextBlock { Text = "", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            default:
                return new TextBlock();
        }
    }

    /// <summary>收集表单值并经宿主校验后发起一次操作（按操作启动、完成即停止）。</summary>
    private async Task RunOperationAsync(string operationId)
    {
        var entry = _entry;
        var ui = _ui;
        if (entry is null || ui is null) return;
        var generation = _generation;

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (id, reader) in _valueReaders)
            values[id] = reader();
        if (!PluginUiParser.TryValidateValues(ui, values, out var validationError))
        {
            StatusText.Text = "输入无效：" + validationError;
            return;
        }

        StatusText.Text = $"正在执行“{operationId}”…（后端按操作启动，完成后停止）";
        PluginInvokeResult result;
        try
        {
            result = await ServiceLocator.PluginRuntime.InvokeOnceAsync(entry, operationId, values);
        }
        catch (Exception ex)
        {
            if (generation == _generation) StatusText.Text = "执行失败：" + ex.Message;
            return;
        }
        if (generation != _generation) return; // 页面已切换/离页：丢弃旧回调。

        var items = result.Items.Count == 0 ? "" : Environment.NewLine +
            string.Join(Environment.NewLine, result.Items.Select(i => $"· {i.Id}：{i.Status} —— {i.Message}"));
        StatusText.Text =
            $"状态：{result.Status}（{result.Code}）" + Environment.NewLine + $"{result.Message}{items}" +
            (result.PendingRestore ? Environment.NewLine + "注意：存在待恢复备份。" : "");
        var logError = PluginDiagnostics.GetWriteError(Path.Combine(ServiceLocator.Plugins.DataRoot, "diagnostics"));
        if (logError.Length > 0) StatusText.Text += Environment.NewLine + logError;

    }

    private void SaveConfig_Click(object sender, RoutedEventArgs e) => SaveCurrentConfiguration(showSuccess: true);

    private void SaveCurrentConfiguration(bool showSuccess = false)
    {
        if (_entry is null || _ui is null || _valueReaders.Count == 0 || (!_configurationWritable && !showSuccess)) return;
        if (!ServiceLocator.Plugins.Index.TryGet(_entry.Id, out var current, out _) || current is null ||
            current.Version != _entry.Version || current.PackageSha256 != _entry.PackageSha256) return;
        var values = _valueReaders.ToDictionary(p => p.Key, p => p.Value(), StringComparer.Ordinal);
        if (!PluginUiParser.TryValidateValues(_ui, values, out var error) ||
            !ServiceLocator.Plugins.TryWriteConfig(_entry.Id, System.Text.Json.JsonSerializer.Serialize(values), out error))
        {
            StatusText.Text = "配置未保存：" + error;
            Log.Warn("插件配置保存失败：" + _entry.Id);
        }
        else
        {
            _configurationWritable = true;
            if (showSuccess) StatusText.Text = "配置已保存，未执行插件操作。";
        }
    }
}
