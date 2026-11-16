using DeltaNFD.Backends;
using DeltaNFD.Inspection;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class OptimalClockPage
{
    private ClockEnvironmentStatus _clockEnvironment = new();
    private string _clockEnvironmentResult = "";
    private bool _environmentRefreshing;
    public bool CanReadClockEnvironment => !Busy && !_environmentRefreshing && !AppSettingsStore.Read().FrameModeActive
        && PageBackendSessions.Manager.DeltaRunning == false && PageBackendSessions.SystemWriteBlocker is null;
    private bool CanChangeClockEnvironment => CanReadClockEnvironment && Backend.HasCurrentStatus && State?.Holding != true && State?.HasError != true;
    public bool CanDefaultClockSource => CanChangeClockEnvironment && _clockEnvironment.CanDefaultSource;
    public bool CanDisableHpetDevice => CanChangeClockEnvironment && _clockEnvironment.CanDisableHpet;
    public bool CanRestoreClockSource => CanChangeClockEnvironment && _clockEnvironment.CanRestoreSource;
    public bool CanRestoreHpetDevice => CanChangeClockEnvironment && _clockEnvironment.CanRestoreHpet;
    public string ClockEnvironmentMessage => _clockEnvironment.Message;
    public string ClockEnvironmentResult => _clockEnvironmentResult;
    public string ClockEnvironmentDetail
    {
        get
        {
            var system = _clockEnvironment.System; if (system is null) return "关闭帧格并退出游戏后，点击检测计时源与设备。";
            var bcd = system.Bcd;
            static string Value(string value) => value.Length == 0 ? "未配置" : value;
            var source = bcd is null ? "启动计时源：未能读取"
                : $"useplatformclock：{Value(bcd.Clock)}；useplatformtick：{Value(bcd.Tick)}"
                    + (bcd.InheritedOverride ? "\n发现继承启动项含平台覆盖，本入口不修改继承项，请人工核对。" : "");
            var devices = !system.DevicesReadable ? "HPET 设备：未能读取" : system.Devices.Count == 0 ? "未发现可识别的 HPET 设备"
                : string.Join("\n", system.Devices.Select(d => $"{d.InstanceId}：{(d.Disabled ? "已禁用" : d.Problem == 0 ? "已启用" : "设备异常（" + d.Problem + "）")}"));
            return $"CPU 厂商：{system.CpuVendor}\n{source}\n{devices}\n平台计时源原始备份：{(_clockEnvironment.SourceBackup ? "有" : "无待恢复项")}；HPET 原始备份：{(_clockEnvironment.HpetBackup ? "有" : "无待恢复项")}";
        }
    }
    private void RaiseClockEnvironment()
    {
        foreach (var name in new[] { nameof(CanReadClockEnvironment), nameof(CanDefaultClockSource), nameof(CanDisableHpetDevice),
            nameof(CanRestoreClockSource), nameof(CanRestoreHpetDevice), nameof(ClockEnvironmentMessage), nameof(ClockEnvironmentResult), nameof(ClockEnvironmentDetail) }) Raise(name);
    }
    private async Task ReadClockEnvironmentAsync()
    {
        var response = await _system.InvokeAsync(BackendCommands.SystemClockEnvironmentRead, new { }, null, CancellationToken.None);
        _clockEnvironment = BackendResults.RequireData<ClockEnvironmentStatus>(response, "检测计时源与 HPET");
    }
    private async void ReadClockEnvironment_Click(object sender, RoutedEventArgs e)
    {
        if (!CanReadClockEnvironment) return;
        _environmentRefreshing = true; RaiseAll();
        try { await ReadClockEnvironmentAsync(); }
        catch (Exception ex) { _clockEnvironment = new() { Message = "读取未完成：" + ex.Message }; }
        finally { _environmentRefreshing = false; RaiseAll(); }
    }
    private async void DefaultClockSource_Click(object sender, RoutedEventArgs e) => await ChangeClockEnvironmentAsync(ClockEnvironmentAction.RestoreDefaultSource);
    private async void DisableHpetDevice_Click(object sender, RoutedEventArgs e) => await ChangeClockEnvironmentAsync(ClockEnvironmentAction.DisableHpet);
    private async void RestoreClockSource_Click(object sender, RoutedEventArgs e) => await ChangeClockEnvironmentAsync(ClockEnvironmentAction.RestoreSource);
    private async void RestoreHpetDevice_Click(object sender, RoutedEventArgs e) => await ChangeClockEnvironmentAsync(ClockEnvironmentAction.RestoreHpet);
    private async Task ChangeClockEnvironmentAsync(ClockEnvironmentAction action)
    {
        var allowed = action switch { ClockEnvironmentAction.RestoreDefaultSource => CanDefaultClockSource,
            ClockEnvironmentAction.DisableHpet => CanDisableHpetDevice, ClockEnvironmentAction.RestoreSource => CanRestoreClockSource,
            ClockEnvironmentAction.RestoreHpet => CanRestoreHpetDevice, _ => false };
        if (!allowed) return;
        _applying = true; RaiseAll();
        try
        {
            await ReadClockEnvironmentAsync(); var selected = _clockEnvironment;
            var source = ClockEnvironmentPolicy.IsSource(action); var restoring = ClockEnvironmentPolicy.IsRestore(action);
            var title = restoring ? source ? "按原始备份恢复平台计时源？" : "按原始备份恢复 HPET？"
                : source ? "恢复 Windows 默认计时源选择？" : "禁用 HPET 设备？";
            var content = restoring ? source ? "只还原本功能记录的计时源原值。须先恢复尚未完成的 HPET 备份。"
                    : "按备份恢复 HPET 设备原状态；本版设备备份不涉及 BCD。若备份来自旧版联动操作，仍先恢复设备，再还原其中记录的计时源原值。身份不符或设备恢复失败时停止并保留备份。"
                : source ? "先保存原值，再移除当前启动项的 useplatformclock 与 useplatformtick 覆盖，让 Windows 自动选择。此操作不保证强制使用 TSC。"
                : "先备份所列 HPET 设备原状态，再持久禁用已启用的设备并逐项回读，相当于在设备管理器中禁用这些设备。不修改 useplatformclock、useplatformtick 或其他 BCD 项。原本已禁用的设备在恢复时保持禁用；任一步失败即停止并保留备份。此操作不保证强制使用 TSC 或改善游戏延迟。";
            content += "\n\n动态节拍保持原样。操作后请手动重启，旧钟摆扫描与自定义验证须重新确认。\n\n" + ClockEnvironmentDetail;
            if (await new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = new ScrollViewer
                { MaxHeight = 420, Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } },
                PrimaryButtonText = restoring ? "按备份恢复" : "备份并应用", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close }.ShowAsync() != ContentDialogResult.Primary) return;
            if (PageBackendSessions.SystemWriteBlocker is { } blocker) throw new InvalidOperationException(blocker);
            var response = await _system.InvokeAsync(BackendCommands.SystemClockEnvironmentChange,
                new ClockEnvironmentArgs(action, selected.Fingerprint, true), null, CancellationToken.None);
            _clockEnvironmentResult = response.Message;
            if (response.RequiresReview || response.PendingRestore) _clockEnvironmentResult += "\n请先在后端待核对记录中完成核对与恢复。";
            await ReadClockEnvironmentAsync();
        }
        catch (Exception ex) { _clockEnvironmentResult = "计时源操作未完成：" + ex.Message; }
        finally { _applying = false; await RefreshAsync(); RaiseAll(); }
    }
}
