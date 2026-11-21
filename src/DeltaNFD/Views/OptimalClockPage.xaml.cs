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
    public string PageDescription => "游戏运行正常？可以保持默认，不必做测试。如果想知道换一种计时设置，电脑能否更准时地唤醒程序，可以在这里比较。测的不是游戏帧数，也不会自动套用结果。";
    public string SelectionTitle => "2　比较哪种设置让程序醒得更准时";
    public string SelectionHelp => "测量 Sleep(1) 与 144 Hz 周期的晚醒和漏周期。快速检测只提名；完整验证重新交替测量基准与候选，只生成报告。";
    public string ProbeLabel => Experimental ? "仅检测 DPC（不保存）" : "仅检测兼容性（无 CPU 负载）";
    public string SelectLabel => "开始完整比较";
    public string ScanPrerequisiteText => Experimental ? "先退出游戏、关闭帧格，并在上方核验自己的 DPC 文件。测试期间会临时运行它，有蓝屏风险。" : "确认上方的系统设置已就绪，然后退出游戏、关闭帧格。";
    public bool CanScan => Supported && !Busy && _global.Ready;
    public bool CanCancelScan => _applying && !_clockValueBusy && _scanCancellation is not null;
    public bool CanRefresh => Supported && !Busy;
    public bool DpcWindowHidden => AppSettingsStore.Read().OptimalClockDpcWindowHidden;
    private void DpcWindowHidden_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _refreshing || _applying || sender is not ToggleSwitch toggle || toggle.IsOn == DpcWindowHidden) return;
        try { AppSettingsStore.UpdateRequired(s => s.OptimalClockDpcWindowHidden = toggle.IsOn); _operationText = toggle.IsOn ? "已启用隐藏，核验成功的自有DPC窗口会持续隐藏。" : "已关闭隐藏，自有DPC窗口将在下一次核对时显示。"; }
        catch (Exception ex) { _operationText = "窗口设置未保存：" + ex.Message; }
        RaiseAll();
    }
    public bool CanPrepareDriver => Experimental && !_applying && PageBackendSessions.SystemWriteBlocker is null;
    public bool Busy => _applying;
    public bool GlobalIsOn => _global.Configured;
    public bool CanRegister => Editable && (_global.NeedsRegistration || _global.PendingRecovery);
    public string GlobalStatusText => _global.Message;
    public string OperationText => string.IsNullOrEmpty(_operationText) ? "还没有本次操作的结果。比较结束后，结论会显示在这里。" : _operationText;
    public string DpcPathText => _global.Win10Dpc?.ExePath is { Length: > 0 } path ? "已核验原件：" + path : "尚未选择原版 DPC EXE；工具不会自动下载或复制第三方原件。";
    public string FrameLinkText => Experimental
        ? "保存后可在帧格页开启或关闭钟摆联动：基础方式只运行 DPC；算法或自定义方式同时运行 DPC 和额外请求。DPC 退出后不反复拉起。"
        : "这里显示帧格的实际状态。准备完成且帧格和钟摆联动均开启时才使用保存值；关闭联动或帧格即释放。比较报告不会替换保存值。";
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
            if (state.Holding && state.DpcProcessVerified && state.VerifiedGlobal)
                return $"DPC辅助持有：DPC基底1ms，额外目标{Ms(state.Resolution100ns):0.0000}ms，当前{Ms(state.Current100ns):0.0000}ms；独立等待{state.RequestlessMilliseconds:0.####}ms。";
            if (state.Holding && state.DpcProcessVerified)
                return state.Current100ns is > 0 and <= 10000
                    ? $"自备 DPC 正在常驻：原版目标1.0000ms，当前{Ms(state.Current100ns):0.0000}ms；进程和服务已核验，未验证Sleep或游戏效果。"
                    : "DPC 当前分辨率读数无效。";
            if (state.Holding && state.KernelResolutionVerified)
                return state.Resolution100ns is >= 5000 and <= 10000 && state.Granted100ns is > 0 and <= 10000
                    && state.Current100ns > 0 && state.Current100ns <= (ulong)state.Resolution100ns + 50
                    ? $"Win10 驱动分辨率已确认：请求 {Ms(state.Resolution100ns):0.0000}ms，授予 {Ms(state.Granted100ns):0.0000}ms，当前 {Ms(state.Current100ns):0.0000}ms。"
                    : "驱动分辨率读数无效，无法确认请求。";
            if (state.Holding && (state.Resolution100ns < (state.CustomResolutionActive ? 5000u : 5100u) || state.Resolution100ns > 156250 || state.Current100ns is < 1 or > 156250))
                return "计时器读数无效，无法确认锁定。";
            if (state.Holding && (ulong)state.Current100ns + 10 < state.Resolution100ns)
                return $"当前使用的不是帧格设定的值：目标 {Ms(state.Resolution100ns):0.0000} ms，系统当前 {Ms(state.Current100ns):0.0000} ms；可在历史提醒中查看记录。";
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
            if (Experimental && !IsDpcAlgorithmSaved)
                return "仅核对本工具启动的DPC、原版驱动服务和系统读数；没有直接读取其授予值，不保证反作弊兼容。不会关闭外部DPC或研究插件的实例。";
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
        : Experimental ? (AppSettingsStore.Read().OptimalClockScanUtc is { } kernelUtc
                && (AppSettingsStore.Read().OptimalClockScanPlatform == _global.PlatformId || IsDpcAlgorithmSaved)
            ? IsDpcAlgorithmSaved
                ? $"最近算法保存：{kernelUtc.ToLocalTime():yyyy-MM-dd HH:mm}（{ModeLabel(AppSettingsStore.Read().OptimalClockScanMode)}），目标{Ms(AppSettingsStore.Read().OptimalClockResolution100ns):0.0000}ms，DPC基底1ms。"
                : $"最近基础DPC保存：{kernelUtc.ToLocalTime():yyyy-MM-dd HH:mm}；目标1.0000ms。"
            : "尚未保存当前来源。仅检测不会保存；可保存基础1ms；新对照测量只生成报告。")
        : AppSettingsStore.Read().OptimalClockScanUtc is { } utc
            ? $"最近扫描：{utc.ToLocalTime():yyyy-MM-dd HH:mm}（{ModeLabel(AppSettingsStore.Read().OptimalClockScanMode)}，为上次实际扫描模式）；保存值 {Ms(AppSettingsStore.Read().OptimalClockResolution100ns):0.0000} ms"
            : "尚无已保存的扫描值。运行比较只生成报告，不会在这里新增保存值。";
    public string DiagnosticText
    {
        get
        {
            var state = State;
            var text = (Experimental ? "Win10日志记录基础DPC检测、算法候选/Sleep/CPU负载、联合持有与释放；基础1ms检测不测Sleep。\n"
                : "Win11 日志记录兼容检测、算法与负载、候选统计、锁定复验、精度变化及释放结果。\n")
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
    private bool IsDpcAlgorithmSaved => AppSettingsStore.Read().OptimalClockScanPlatform == _global.PlatformId + DpcPendulumSource.AlgorithmProfileSuffix;
    private void RaiseAll()
    {
        RaiseCustom();
        RaiseClockEnvironment();
        foreach (var name in new[] { nameof(Editable), nameof(CanScan), nameof(CanRefresh), nameof(Busy), nameof(GlobalIsOn),
            nameof(CanRegister), nameof(GlobalStatusText), nameof(StatusText), nameof(DetailText), nameof(FrameLinkText),
            nameof(ScanText), nameof(HistoryText), nameof(OperationText) }) Raise(name);
        Raise(nameof(GlobalSettingsVisibility)); Raise(nameof(ExperimentalVisibility));
        Raise(nameof(ScanPrerequisiteText)); Raise(nameof(DiagnosticText));
        Raise(nameof(CanPrepareDriver));
        Raise(nameof(CanCancelScan));
        Raise(nameof(DpcWindowHidden));
        Raise(nameof(DpcPathText));
    }
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        if (!Supported) { App.MainWindow?.NavigateByTag("dashboard"); return; }
        _loaded = true;
        Backend.StatusUpdated += OnStatusUpdated;
        SetWindowPresented(App.MainWindow?.IsUiPresented != false);
    }
    internal void SetWindowPresented(bool presented)
    {
        // This timer only reads display status; scans and holds have independent lifetimes.
        if (!_loaded) return;
        if (!presented) { _statusTimer.Stop(); return; }
        if (_statusTimer.IsEnabled) return;
        _statusTimer.Start();
        RaiseAll();
        if (!_applying) _ = RefreshAsync();
    }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false; _statusTimer.Stop(); Backend.StatusUpdated -= OnStatusUpdated; _scanCancellation?.Cancel();
    }
    private async void OnStatusTick(object? sender, object e)
    {
        if (!_loaded || !_statusTimer.IsEnabled || App.MainWindow?.IsUiPresented == false) return;
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
            if (await new ContentDialog { XamlRoot = XamlRoot, Title = enabled ? "允许计时设置对其他程序生效？" : "关闭全局计时设置？",
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
    private async void DriverGuide_Click(object sender, RoutedEventArgs e)
    {
        await new ContentDialog { XamlRoot = XamlRoot, Title = "准备 Win10 钟摆驱动",
            Content = "玩家自行准备原版DPC Latency Checker 1.4.0 EXE（306928字节）。工具仅记录并核验本地原件，不提供下载、嵌入或提取驱动。\n\n核验文件只记录路径。“仅检测”不保存设置；只有想让帧格使用基础 1 ms 时，才选“检测并保存”。帧格关闭或退出工具时会释放 DPC。原版自行加载其驱动并固定申请1ms。DPC已知有CVE-2025-51619蓝屏缺陷，有签名不代表反作弊兼容。系统阻止时不会关闭安全保护。\n\n先前研究插件或外部DPC应自行释放，本工具不会接管。释放未确认会保留专用记录；请关闭帧格后点“释放并核对DPC”。\n\n旧自研驱动、测试证书与TESTSIGNING不会被自动改动。恢复旧环境的方法见程序目录Docs/Pendulum/Win10-DPC.md。",
            CloseButtonText = "知道了" }.ShowAsync();
    }
    private async void PrepareDriver_Click(object sender, RoutedEventArgs e)
    {
        if (!CanPrepareDriver) return;
        try
        {
            if (App.MainWindow is null) return;
            var path = DeltaNFD.Native.FolderPickerDialog.PickExecutable(WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow), AppSettingsStore.Read().OptimalClockDpcExePath, "选择原版 DPC EXE");
            if (path is null) return;
            if (await new ContentDialog { XamlRoot = XamlRoot, Title = "记录并使用自备 DPC？",
                Content = "将核验所选原版1.4.0文件（306928字节），只保存路径，不复制或运行原件。\n\n之后检测/帧格会启动该程序，它会自行加载旧内核驱动并申请1ms。存在CVE-2025-51619蓝屏缺陷，反作弊兼容性未验收；文件有签名不等于安全保证。工具不修改证书、TESTSIGNING或启动保护。\n\n请仅在已接受这些限制的Win10环境使用，并先退出游戏。",
                PrimaryButtonText = "同意并核验", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close }.ShowAsync() != ContentDialogResult.Primary) return;
            await ConfigureDpcAsync(path);
        }
        catch (Exception ex) { _operationText = "DPC 文件选择未完成：" + ex.Message; RaiseAll(); }
    }
    private async Task ConfigureDpcAsync(string path)
    {
        _applying = true; _operationText = "正在核验原件，请稍候…"; RaiseAll();
        try { var result = await Backend.InvokeAsync(BackendCommands.ProcmanClockDpcConfigure, new DpcPendulumConfigureArgs(path, true));
            _operationText = result.Message; }
        catch (Exception ex) { _operationText = "DPC 配置未完成：" + ex.Message; }
        finally { _applying = false; await RefreshAsync(); RaiseAll(); }
    }
    private async void ClearDpc_Click(object sender, RoutedEventArgs e) { if (CanPrepareDriver) await ConfigureDpcAsync(""); }
    private async void ReleaseDpc_Click(object sender, RoutedEventArgs e)
    {
        if (!CanRefresh) return; _applying = true; RaiseAll();
        try { _operationText = (await Backend.ReleaseClockAsync()).Message; }
        catch (Exception ex) { _operationText = "DPC 释放核对未完成：" + ex.Message; }
        finally { _applying = false; await RefreshAsync(); }
    }
    private async void DriverFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = System.IO.Path.Combine(AppContext.BaseDirectory, "Docs", "Pendulum");
        try
        {
            if (!System.IO.Directory.Exists(folder)) throw new System.IO.DirectoryNotFoundException("随包驱动文件夹缺失，请重新获取完整的 Alpha0.935 安装包。");
            if (!await Windows.System.Launcher.LaunchFolderPathAsync(folder)) throw new System.IO.IOException("未能打开驱动文件夹。");
        }
        catch (Exception ex) { _operationText = ex.Message; Raise(nameof(OperationText)); }
    }
    private async void BasicDpcSave_Click(object sender, RoutedEventArgs e) => await ScanAsync(false, basicDpc: true);
    private void CancelScan_Click(object sender, RoutedEventArgs e) { _scanCancellation?.Cancel(); _operationText = "正在取消并释放临时请求，请稍候…"; RaiseAll(); }
    private async Task ScanAsync(bool quick, bool compatibilityOnly = false, bool basicDpc = false)
    {
        if (!CanScan) return;
        _applying = true; _scanCancellation = new(); RaiseAll();
        try
        {
            if ((Experimental || !compatibilityOnly && !basicDpc) && await new ContentDialog { XamlRoot = XamlRoot, Title = Experimental ? "临时运行自备 DPC？有蓝屏风险" : "开始比较计时设置？",
                Content = compatibilityOnly || basicDpc
                    ? "将临时运行原版DPC并核对1ms，结束后正常关闭；不测量Sleep或施加CPU负载。" + (compatibilityOnly ? "本次只检测，不保存。" : "通过后保存基础1ms方式。")
                    : (Experimental ? "将临时运行自备 DPC，反作弊兼容性未经验证。" : "") + "请关闭帧格并退出游戏。测量会施加 CPU 负载，比较独立进程的唤醒表现；结束后释放，仅生成报告。",
                PrimaryButtonText = "开始检测", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close }.ShowAsync() != ContentDialogResult.Primary) return;
            var mode = AppSettingsStore.Read().OptimalClockPreferredScanMode;
            _operationText = !compatibilityOnly && !basicDpc ? (quick ? "正在比较不同设置，筛选值得复测的值…" : "正在比较现有方案和其他设置，并轮流复测。此步骤不会保存新值…") : "正在检测，请稍候…"; RaiseAll();
            var result = compatibilityOnly
                ? await Backend.ProbeClockCompatibilityAsync(_scanCancellation.Token)
                : basicDpc ? await Backend.ScanClockAsync(false, mode, _scanCancellation.Token)
                : await Backend.CompareWakeAsync(quick, _scanCancellation.Token);
            _operationText = result.Message;
        }
        catch (Exception ex) { _operationText = "钟摆计划扫描未完成：" + ex.Message; Log.Warn(_operationText); }
        finally { _scanCancellation?.Dispose(); _scanCancellation = null; _applying = false; await RefreshAsync(); RaiseAll(); }
    }
    private void Logs_Click(object sender, RoutedEventArgs e) => Log.OpenFolderInExplorer();
    private void Frame_Click(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateByTag("frame");
}
