using System.Globalization;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class OptimalClockPage
{
    private bool _clockValueBusy;
    private string _valueOperationText = "";
    public string ClockValueOperationText => _valueOperationText;
    public bool CanCancelClockValue => _clockValueBusy && _scanCancellation is not null;
    private string _customResolutionText = (AppSettingsStore.Read().OptimalClockCustomResolution100ns / 10000m).ToString("0.0000", CultureInfo.InvariantCulture);
    public string CustomResolutionText
    {
        get => _customResolutionText;
        set { if (_customResolutionText == value) return; _customResolutionText = value; Raise(nameof(CustomResolutionText)); RaiseCustom(); }
    }
    public bool ClockValuesEditable => Supported && !Busy && _global.Ready && Backend.HasCurrentStatus
        && State?.ClockValueProtocolVersion == 1 && !AppSettingsStore.Read().FrameModeActive
        && PageBackendSessions.Manager.DeltaRunning == false && PageBackendSessions.SystemWriteBlocker is null;
    public bool CanCustomValue => ClockValuesEditable && ClockValueInput.TryParse(CustomResolutionText, Experimental, out _);
    public bool CanUseAutomaticValue => ClockValuesEditable && PendulumFramePolicy.AutomaticPrepared(_global, AppSettingsStore.Read());
    public bool CanTestAutomaticValue => ClockValuesEditable && PendulumFramePolicy.AutomaticOptimumPrepared(_global, AppSettingsStore.Read());
    public string CustomValueRangeText => Experimental ? "范围 0.5000～1.0000ms；DPC 基底固定为1ms。" : "范围 0.5000～15.6250ms；实际可用值以授予和回读为准。";
    public string CustomValueError => ClockValueInput.TryParse(CustomResolutionText, Experimental, out _) ? "" : "请输入范围内的毫秒数，使用小数点，最多4位小数。";
    public string ClockValueGateText => AppSettingsStore.Read().FrameModeActive ? "先关闭帧格并退出目标游戏，再测试或更换使用值。"
        : !Backend.HasCurrentStatus ? "等待后端确认状态。" : State?.ClockValueProtocolVersion != 1 ? "当前后端不支持此功能，请使用完整新版程序。"
        : "测试仅临时请求所选值，不施加 CPU 负载；结束后释放，不代表游戏帧率收益。";
    public string SelectedClockValueText
    {
        get
        {
            var settings = AppSettingsStore.Read();
            var ready = PendulumFramePolicy.Prepared(_global, settings);
            return $"帧格使用：{(settings.OptimalClockUseCustomResolution ? "自定义" : "自动/基础保存值")} {PendulumFramePolicy.Resolution(settings) / 10000.0:0.0000}ms"
                + (ready ? "；准备已匹配。" : "；尚未完成当前来源的准备与验证。")
                + $"\n自动保存值保留为 {settings.OptimalClockResolution100ns / 10000.0:0.0000}ms；未自动扫描时该数字不代表最优结果。";
        }
    }
    public string SleepTestResultText => ClockValuePresentation.Sleep(State?.SleepTest);
    private void RaiseCustom()
    {
        foreach (var name in new[] { nameof(ClockValuesEditable), nameof(CanCustomValue), nameof(CanUseAutomaticValue),
            nameof(CanTestAutomaticValue), nameof(CustomValueRangeText), nameof(CustomValueError), nameof(ClockValueGateText),
            nameof(SelectedClockValueText), nameof(SleepTestResultText), nameof(ClockValueOperationText), nameof(CanCancelClockValue) }) Raise(name);
    }
    private void CancelClockValue_Click(object sender, RoutedEventArgs e) { _scanCancellation?.Cancel(); _valueOperationText = "正在取消并确认释放临时请求…"; RaiseAll(); }
    private async void SaveCustomValue_Click(object sender, RoutedEventArgs e) => await RunClockValueAsync(ClockValueAction.SaveCustom);
    private async void UseAutomaticValue_Click(object sender, RoutedEventArgs e) => await RunClockValueAsync(ClockValueAction.UseAutomatic);
    private async void TestCustomSleep_Click(object sender, RoutedEventArgs e) => await RunClockValueAsync(ClockValueAction.TestCustomSleep);
    private async void TestAutomaticSleep_Click(object sender, RoutedEventArgs e) => await RunClockValueAsync(ClockValueAction.TestAutomaticSleep);
    private async Task RunClockValueAsync(ClockValueAction action)
    {
        if (!ClockValuesEditable) return;
        var custom = action is ClockValueAction.SaveCustom or ClockValueAction.TestCustomSleep;
        if (custom && !CanCustomValue || action == ClockValueAction.UseAutomatic && !CanUseAutomaticValue
            || action == ClockValueAction.TestAutomaticSleep && !CanTestAutomaticValue) return;
        uint? ticks = null;
        if (custom) { if (!ClockValueInput.TryParse(CustomResolutionText, Experimental, out var value)) return; ticks = value; }
        _applying = true; _clockValueBusy = true; _scanCancellation = new(); RaiseAll();
        try
        {
            if (Experimental && action != ClockValueAction.UseAutomatic && await new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "开始 DPC 辅助单值验证或 Sleep 测试？",
                Content = "将临时运行已核验的自备 DPC，配合工具子进程申请目标值；不施加 CPU 负载。结束或取消后确认释放。请先退出游戏；自定义值只有持有验证和释放都通过后才保存，原自动结果保留。",
                PrimaryButtonText = "开始", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
            }.ShowAsync() != ContentDialogResult.Primary) return;
            _valueOperationText = "正在请求并核对分辨率，等待测量与释放完成…"; RaiseAll();
            var result = await Backend.ClockValueAsync(action, ticks, _scanCancellation.Token);
            _valueOperationText = result.Message;
        }
        catch (Exception ex) { _valueOperationText = "分辨率操作未完成：" + ex.Message; Log.Warn(_valueOperationText); }
        finally { _scanCancellation?.Dispose(); _scanCancellation = null; _applying = false; _clockValueBusy = false; await RefreshAsync(); RaiseAll(); }
    }
}
