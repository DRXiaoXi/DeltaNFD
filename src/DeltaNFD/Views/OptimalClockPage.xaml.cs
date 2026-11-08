using DeltaNFD.Backends;
using DeltaNFD.Inspection;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;

namespace DeltaNFD.Views;

/// <summary>钟摆计划只负责设置和展示，持有和监督随帧格在 procman 后端执行。</summary>
public sealed partial class OptimalClockPage : Page, INotifyPropertyChanged
{
    private bool _applying, _loaded, _refreshing;
    private string _refreshError = "", _operationText = "";
    private PendulumGlobalStatus _global = new();
    private CancellationTokenSource? _scanCancellation;
    private readonly BackendCaller _system = new(BackendModules.System);
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private static BackendProcmanService Backend => BackendProcmanService.Instance;
    private static OptimalClockSnapshot? State => Backend.ClockState;
    private static bool Supported => PendulumGlobalInspection.Supported;
    private static bool Experimental => PendulumGlobalInspection.ExperimentalWindows10;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public OptimalClockPage()
    {
        InitializeComponent(); Loaded += OnLoaded; Unloaded += OnUnloaded; _statusTimer.Tick += OnStatusTick;
    }
    public bool Editable => Supported && !Experimental && !_applying && _global.Readable && PageBackendSessions.SystemWriteBlocker is null;
    public Visibility GlobalSettingsVisibility => Experimental ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ExperimentalVisibility => Experimental ? Visibility.Visible : Visibility.Collapsed;
    public string ScanPrerequisiteText => Experimental
        ? "先用无 CPU 负载的兼容检测确认本机能力；扫描会再次检测，通过后才保存全局锁定值。"
        : "完成全局精度设置与重启后，再扫描本机最佳值。";
    public bool CanScan => Supported && !_applying && _global.Ready;
    /// <summary>检测模式下拉：0 = 算法1（更稳定），1 = 算法2（更激进）。</summary>
    public int ScanModeIndex
    {
        get => AppSettingsStore.Read().OptimalClockPreferredScanMode == OptimalClockScanMode.StableLatency ? 1 : 0;
        set
        {
            var mode = value == 1 ? OptimalClockScanMode.StableLatency : OptimalClockScanMode.LowestLatency;
            if (AppSettingsStore.Read().OptimalClockPreferredScanMode == mode) return;
            AppSettingsStore.Update(s => s.OptimalClockPreferredScanMode = mode);
            Raise(nameof(ScanModeIndex));
            Raise(nameof(ScanText));
            Raise(nameof(DetailText));
        }
    }
    public bool CanRefresh => Supported && !_applying;
    public bool Busy => _applying;
    public bool GlobalIsOn => _global.Configured;
    public bool CanRegister => Editable && (_global.NeedsRegistration || _global.PendingRecovery);
    public string GlobalStatusText => _global.Message;
    public string OperationText => _operationText;
    public string FrameLinkText => AppSettingsStore.Read().FrameModeActive
        ? "帧格已开启：满足全局精度前置条件并完成本机扫描后，自动锁定并每两秒核对。"
        : "锁定随帧格总开关开启、关闭；此页用于设置全局前提、扫描最佳值和查看提醒。";
    public string StatusText
    {
        get
        {
            if (!Supported) return "此系统版本不支持钟摆计划。";
            if (!_global.Ready) return _global.Message;
            if (_refreshError.Length > 0) return "状态读取失败，无法确认锁定：" + _refreshError;
            if (!Backend.HasCurrentStatus || State is not { } state || !BackendProcmanService.IsFresh(state.UpdatedUtc))
                return "当前锁定状态尚未确认，请刷新；历史快照不能代表当前状态。";
            if (state.HasError) return "锁定未确认：" + state.Message;
            if (state.Holding && (state.Resolution100ns is < 5100 or > 156250 || state.Current100ns is < 1 or > 156250))
                return "计时器读数无效，无法确认锁定。";
            if (state.Holding && (ulong)state.Current100ns + 10 < state.Resolution100ns)
                return $"精度被更细请求覆盖：目标 {Ms(state.Resolution100ns):0.0000} ms，系统当前 {Ms(state.Current100ns):0.0000} ms；提醒已记录于下方。";
            if (state.Holding && state.Current100ns > (ulong)state.Resolution100ns + 500)
                return $"锁定未生效：目标 {Ms(state.Resolution100ns):0.0000} ms，系统当前 {Ms(state.Current100ns):0.0000} ms。";
            if (state.Holding && state.VerifiedGlobal)
                return $"帧格正在锁定 {Ms(state.Resolution100ns):0.0000} ms（系统当前 {Ms(state.Current100ns):0.0000} ms）";
            return state.Message;
        }
    }
    public string DetailText
    {
        get
        {
            var state = State;
            if (state is null || !Backend.HasCurrentStatus || !BackendProcmanService.IsFresh(state.UpdatedUtc))
                return "等待后端当前状态；关闭页面后，帧格仍按已有后台循环持续核对。";
            var parts = new List<string>();
            if (state.BestLock100ns is { } best) parts.Add($"稳定侧锁定值 {Ms(best):0.0000} ms");
            if (state.BestTypical100ns is { } typical) parts.Add($"典型值 {Ms(typical):0.0000} ms，等待 {state.BestTypicalMedianMs:0.###} ms");
            if (state.BestStable100ns is { } stable) parts.Add($"更稳值 {Ms(stable):0.0000} ms，等待 {state.BestStableMedianMs:0.###} ms");
            parts.Add(ModeLabel(state.ScanMode));
            if (state.ScanSafetyMarginMs > 0) parts.Add($"安全裕量 {state.ScanSafetyMarginMs:0.0000} ms");
            if (state.ScanStableConditions.Length > 0) parts.Add("三条件 " + state.ScanStableConditions);
            if (state.ScanLoadThreads > 0) parts.Add($"测量负载 {state.ScanLoadThreads} 线程");
            if (state.ScanUtc is { } utc) parts.Add($"扫描于 {utc.ToLocalTime():yyyy-MM-dd HH:mm}");
            if (state.GlobalVerifiedUtc is { } verified) parts.Add($"无请求进程复验于 {verified.ToLocalTime():HH:mm:ss}，等待 {state.RequestlessMilliseconds:0.####} ms");
            if (state.DiagnosticHistoryError.Length > 0) parts.Add(state.DiagnosticHistoryError);
            return string.Join("；", parts);
        }
    }
    public string ScanText => _applying ? "操作进行中，请稍候…"
        : AppSettingsStore.Read().OptimalClockScanUtc is { } utc
            ? $"最近扫描：{utc.ToLocalTime():yyyy-MM-dd HH:mm}（{ModeLabel(AppSettingsStore.Read().OptimalClockScanMode)}，为上次实际扫描模式）；保存值 {Ms(AppSettingsStore.Read().OptimalClockResolution100ns):0.0000} ms"
            : "尚未扫描。" + ScanPrerequisiteText;
    public string DiagnosticText
    {
        get
        {
            var state = State;
            var text = "Win10 实验路径与 Win11 路径均保存本地钟摆日志：兼容检测、算法与负载、候选统计、锁定复验、精度变化及释放结果。\n"
                + (state?.DiagnosticLogPath is { Length: > 0 } path ? path : Log.LogDirectory)
                + "\n日志有大小和数量上限；可打开文件夹复制 pendulum_ 开头的文件反馈。";
            if (state?.DiagnosticLogError is { Length: > 0 } error) text += "\n" + error;
            return text;
        }
    }
    public string HistoryText
    {
        get
        {
            var notices = State?.Notices;
            if (notices is null || notices.Count == 0) return "暂无已记录的精度覆盖或锁定失效提醒。";
            return "以下为已保存的历史提醒，当前是否生效以上方状态为准。\n\n" + string.Join("\n\n", notices.Take(8).Select(n =>
                $"{n.FirstSeenUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 目标 {Ms(n.Requested100ns):0.0000} ms / 观测 {Ms(n.Observed100ns):0.0000} ms\n"
                + n.Message + (n.RecoveredUtc is { } recovered ? $"\n已于 {recovered.ToLocalTime():yyyy-MM-dd HH:mm:ss} 检测恢复。" : "\n末次记录尚未确认恢复。")));
        }
    }
    private static double Ms(uint value) => value / 10000.0;
    private void RaiseAll()
    {
        foreach (var name in new[] { nameof(Editable), nameof(CanScan), nameof(CanRefresh), nameof(Busy), nameof(GlobalIsOn),
            nameof(CanRegister), nameof(GlobalStatusText), nameof(StatusText), nameof(DetailText), nameof(FrameLinkText),
            nameof(ScanText), nameof(HistoryText), nameof(OperationText) }) Raise(name);
        Raise(nameof(GlobalSettingsVisibility)); Raise(nameof(ExperimentalVisibility));
        Raise(nameof(ScanPrerequisiteText)); Raise(nameof(DiagnosticText));
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        if (!Supported) { App.MainWindow?.NavigateByTag("dashboard"); return; }
        _loaded = true; Backend.StatusUpdated += OnStatusUpdated; _statusTimer.Start(); await RefreshAsync();
    }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false; _statusTimer.Stop(); Backend.StatusUpdated -= OnStatusUpdated; _scanCancellation?.Cancel();
    }
    private async void OnStatusTick(object? sender, object e)
    {
        if (!_loaded) return;
        RaiseAll(); if (!_applying) await RefreshAsync();
    }
    private void OnStatusUpdated(ProcmanStatusSnapshot snapshot) => DispatcherQueue?.TryEnqueue(RaiseAll);
    private async Task RefreshAsync()
    {
        if (_refreshing || !Supported) return;
        _refreshing = true;
        try
        {
            _global = await PendulumGlobalInspection.ReadAsync();
            await Backend.InvokeAsync(BackendCommands.ProcmanStatus, new { }); _refreshError = "";
        }
        catch (Exception ex) { _refreshError = ex.Message; }
        finally { RaiseAll(); _refreshing = false; }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void Global_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _refreshing || _applying || sender is not ToggleSwitch toggle || toggle.IsOn == GlobalIsOn) return;
        await ConfigureGlobalAsync(toggle.IsOn);
    }
    private async void RegisterGlobal_Click(object sender, RoutedEventArgs e) => await ConfigureGlobalAsync(true);
    private async Task ConfigureGlobalAsync(bool enabled)
    {
        if (!Editable) { RaiseAll(); return; }
        _applying = true; RaiseAll();
        try
        {
            var selected = await PendulumGlobalInspection.ReadAsync();
            if (!selected.Readable) throw new InvalidOperationException(selected.Message);
            if (await new ContentDialog { XamlRoot = XamlRoot, Title = enabled ? "开启全局生效精度？" : "关闭全局生效精度？",
                Content = "将保存固定的系统全局计时器设置，并备份修改前的值。必须手动重启电脑后，系统设置才会生效。工具不会自动重启。"
                    + (enabled ? "\n重启前钟摆计划不能扫描或锁定。" : "\n关闭后钟摆计划停止扫描与锁定，帧格其他功能可继续使用。"),
                PrimaryButtonText = "保存设置", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close }.ShowAsync() != ContentDialogResult.Primary) return;
            if (PageBackendSessions.SystemWriteBlocker is { } blocked) throw new InvalidOperationException(blocked);
            var response = await _system.InvokeAsync(BackendCommands.SystemPendulumGlobalConfigure,
                new PendulumGlobalArgs(enabled, selected.Fingerprint), null, CancellationToken.None);
            var result = response.Data is { } data ? BackendJson.Deserialize<OperationResult>(data) : OperationResult.Fail(response.Message);
            _operationText = result.Message;
            await Backend.SyncFrameBackgroundAsync();
        }
        catch (Exception ex) { _operationText = "全局精度设置未完成：" + ex.Message; Log.Warn(_operationText); }
        finally { _applying = false; await RefreshAsync(); RaiseAll(); }
    }
    private static string ModeLabel(OptimalClockScanMode mode)
        => mode == OptimalClockScanMode.StableLatency ? "算法2（更激进）" : "算法1（更稳定）";
    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync(false);
    private async void QuickScan_Click(object sender, RoutedEventArgs e) => await ScanAsync(true);
    private async void Compatibility_Click(object sender, RoutedEventArgs e) => await ScanAsync(true, true);
    private async Task ScanAsync(bool quick, bool compatibilityOnly = false)
    {
        if (!CanScan) return;
        _applying = true; _scanCancellation = new(); RaiseAll();
        try
        {
            var mode = AppSettingsStore.Read().OptimalClockPreferredScanMode;
            var result = compatibilityOnly
                ? await Backend.ProbeClockCompatibilityAsync(_scanCancellation.Token)
                : await Backend.ScanClockAsync(quick, mode, _scanCancellation.Token);
            _operationText = result.Message;
        }
        catch (Exception ex) { _operationText = "钟摆计划扫描未完成：" + ex.Message; Log.Warn(_operationText); }
        finally { _scanCancellation?.Dispose(); _scanCancellation = null; _applying = false; await RefreshAsync(); RaiseAll(); }
    }
    private void Logs_Click(object sender, RoutedEventArgs e) => Log.OpenFolderInExplorer();
    private void Frame_Click(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateByTag("frame");
}
