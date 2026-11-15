using DeltaNFD.Backends;
using DeltaNFD.Native;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.Procman;

public sealed partial class ProcmanEngine
{
    private ProcmanBackgroundStore _backgroundStore = null!;
    private ProcmanBackgroundConfig _backgroundConfig = new();
    private string _backgroundLoadError = "";
    private bool _automaticMemoryActive;
    private DateTime? _lastAutomaticCheck;
    private DateTime? _lastAutomaticRound;
    private DateTime? _lastAutomaticCandidateScan;
    private DateTime? _memoryHighSince;
    private string _memoryFeedback = "";
    private ProcmanMemoryTrimReport? _automaticMemoryReport;
    internal Func<uint?>? MemoryPressureForTests { get; set; }
    internal Func<ProcmanMemoryTelemetry?>? MemoryTelemetryForTests { get; set; }
    internal TimeSpan MemoryPressureWindowForTests { get; set; } = TimeSpan.FromSeconds(30);
    internal Func<EmergencyPolicyDecision>? MemoryPolicyForTests { get; set; }
    internal bool BackgroundLoopDisabledForTests { get; set; }
    internal Func<bool>? FrameModeActiveForTests { get; set; }
    internal Func<bool>? FrameMemorySelectedForTests { get; set; }
    private bool FrameBackgroundActive => (FrameModeActiveForTests?.Invoke() ?? AppSettingsStore.Read().FrameModeActive)
        && !OfflineModeGuard.BlocksNormalAutomation;
    private bool FrameMemorySelected => FrameMemorySelectedForTests?.Invoke() ?? AppSettingsStore.Read().FrameAutomaticMemoryEnabled;
    private bool FrameAutomaticMemoryActive => FrameBackgroundActive && FrameMemorySelected;
    private EmergencyPolicyDecision MemoryPolicy() => MemoryPolicyForTests?.Invoke() ?? EmergencyPolicies.Current;

    private bool IsBackgroundExcluded(string path) => _backgroundLoadError.Length > 0 || _backgroundConfig.ExcludedExecutablePaths.Contains(path, StringComparer.OrdinalIgnoreCase);
    public ProcmanBackgroundStatus BackgroundStatus()
    {
        lock (_gate)
        {
            var policy = MemoryPolicy();
            var note = _backgroundLoadError;
            if (note.Length == 0 && _changeLog.Records.Any(r => r.Owner == ProcmanControlCoordinator.OwnerResponsiveness)) note = "旧版后台抑制尚未还原，请核对与恢复。";
            if (note.Length == 0 && OfflineModeGuard.BlocksNormalAutomation) note = "旧版设置恢复期间暂停自动回收。";
            if (note.Length == 0 && !FrameMemorySelected) note = "未选择帧格中的“自动内存回收”功能。";
            if (note.Length == 0 && !FrameBackgroundActive) note = "等待开启一键帧格模式。";
            if (note.Length == 0 && policy.ManualMemoryCleanOnly) note = "紧急策略仅允许手动回收。";
            if (note.Length == 0) note = policy.BlockReason(BackendCommands.CpuFrameMemoryClean) ?? "";
            return new(_backgroundConfig with { ExcludedExecutablePaths = [.. _backgroundConfig.ExcludedExecutablePaths] },
                _automaticMemoryActive && !_frozen && note.Length == 0,
                _lastAutomaticCheck is { } time ? new DateTimeOffset(time, TimeSpan.Zero) : null, _automaticMemoryReport, note, _memoryFeedback,
                0, // Retired suppression threshold; kept for older status readers.
                policy.MemoryCooldown(_backgroundConfig.MemoryCooldownSeconds));
        }
    }

    public OperationResult SaveBackgroundConfig(ProcmanBackgroundConfig config)
    {
        config = ProcmanBackgroundStore.Validate(config);
        if (config.AutomaticMemoryEnabled && OfflineModeGuard.BlocksNormalAutomation)
            return OperationResult.Fail("旧版设置恢复完成前不能启用自动内存回收。");
        // Compatibility field follows both frame switches, never an independent client-supplied enable flag.
        config = config with { AutomaticMemoryEnabled = FrameAutomaticMemoryActive };
        OperationResult legacyRecovery;
        lock (_gate)
        {
            if (_memoryStopping > 0 || _memoryDisposed) return OperationResult.Fail("回收正在停止或引擎已关闭，参数尚未保存。");
            _backgroundStore.Save(config);
            if (_backgroundConfig.MemoryBatchLimit != config.MemoryBatchLimit || _backgroundConfig.MemoryMode != config.MemoryMode) _memoryPreview = null;
            _backgroundConfig = config;
            Interlocked.Increment(ref _memoryRevision); // In-flight work cannot resume under a later configuration.
            _lastAutomaticCandidateScan = null;
            _backgroundLoadError = "";
            _automaticMemoryActive = config.AutomaticMemoryEnabled && FrameAutomaticMemoryActive;
            _memoryHighSince = null;
            _frozen = false;
            legacyRecovery = RestoreLegacySuppressionAdjustments();
            if (!legacyRecovery.Success) _automaticMemoryActive = false;
        }
        var stopped = OperationResult.Ok("");
        if (_automaticMemoryActive || _rules.EnabledRules().Count > 0 || FramePriorityRequested || FrameNvidiaGpuRequested || FrameTimerIgnoreRequested || TimerIgnoreListRequested || OptimalClockRequested) _ = StartRulesEngine();
        else
        {
            stopped = StopRulesEngineAsync().GetAwaiter().GetResult();
            lock (_gate) _frozen = false; // A normal configuration change must still allow manual previews.
        }
        if (!stopped.Success) return OperationResult.Fail("设置已保存，帧格优先级恢复未完成：" + stopped.Message);
        return legacyRecovery.Success
            ? OperationResult.Ok("内存回收设置已保存，自动回收由帧格总开关与自动内存回收开关共同控制。")
            : OperationResult.Fail("内存回收设置已保存。" + legacyRecovery.Message);
    }

    public OperationResult ActivateBackground()
    {
        lock (_gate)
        {
            try { _backgroundConfig = _backgroundStore.Load(); _backgroundLoadError = ""; }
            catch (InvalidDataException ex)
            {
                _backgroundLoadError = ex.Message;
                _automaticMemoryActive = false;
                return OperationResult.Fail(ex.Message);
            }
            _automaticMemoryActive = _backgroundConfig.AutomaticMemoryEnabled && FrameAutomaticMemoryActive;
        }
        return SyncFrameBackground();
    }

    public OperationResult SyncFrameBackground()
    {
        var active = FrameBackgroundActive;
        var memoryActive = FrameAutomaticMemoryActive;
        if (!memoryActive) lock (_gate) _automaticMemoryActive = false;
        ProcmanBackgroundConfig config;
        try { config = _backgroundStore.Load(); }
        catch (InvalidDataException ex)
        {
            lock (_gate) { _backgroundLoadError = ex.Message; _automaticMemoryActive = false; }
            if (FramePriorityRequested || FrameNvidiaGpuRequested || FrameTimerIgnoreRequested || TimerIgnoreListRequested || OptimalClockRequested) _ = StartRulesEngine();
            FramePriorityTick(force: true);
            FramePendulumTick(force: true);
            TimerIgnoreListTick(force: true);
            return OperationResult.Fail(ex.Message + "；" + FramePriorityState.Message + " " + _frameNvidiaGpuShare.Snapshot.Message + " " + FrameTimerIgnoreState.Message + " " + TimerIgnoreListState.Message + " " + OptimalClockState.Message);
        }
        var memory = SaveBackgroundConfig(config with { AutomaticMemoryEnabled = memoryActive });
        var priorityOk = FramePriorityTick(force: true);
        var clockAndIgnoreOk = FramePendulumTick(force: true);
        var timerListOk = TimerIgnoreListTick(force: true);
        if (!memory.Success || !priorityOk || !clockAndIgnoreOk || !timerListOk)
            return OperationResult.Fail(memory.Message + "；" + FramePriorityState.Message + " " + _frameNvidiaGpuShare.Snapshot.Message + " " + FrameTimerIgnoreState.Message + " " + TimerIgnoreListState.Message + " " + OptimalClockState.Message);
        var status = BackgroundStatus();
        if (memoryActive && !status.AutomaticMemoryActive)
            return OperationResult.Fail("帧格后台功能同步后，自动内存回收当前暂停：" + status.Note);
        return OperationResult.Ok((active
            ? $"帧格自动内存回收{(memoryActive ? "已启用" : "未选择")}。"
            : "自动内存回收已随帧格停用，功能选项和详细参数保留。")
            + (AppSettingsStore.Read().FrameGamePriorityEnabled || AppSettingsStore.Read().FrameGameGpuPriorityEnabled || FramePriorityState.RestorePending ? "；" + FramePriorityState.Message : "")
            + (AppSettingsStore.Read().FrameNvidiaGpuShareEnabled || _frameNvidiaGpuShare.Snapshot.RestorePending ? "；" + _frameNvidiaGpuShare.Snapshot.Message : "")
            + (FrameTimerIgnoreRequested || FrameTimerIgnoreState.RestorePending ? "；" + FrameTimerIgnoreState.Message : "")
            + (TimerIgnoreListRequested || TimerIgnoreListState.RestorePending || TimerIgnoreListState.HasError ? "；" + TimerIgnoreListState.Message : "")
            + (OptimalClockRequested || OptimalClockState.Holding ? "；" + OptimalClockState.Message : ""));
    }

    private uint? MemoryPressure()
    {
        if (MemoryPressureForTests is { } read) return read();
        return MemoryTelemetryForTests is { } telemetry ? telemetry()?.LoadPercent : ProcmanMemoryTelemetryReader.ReadPressure();
    }

    private ProcmanMemoryTelemetry? ReadMemoryTelemetry() => MemoryTelemetryForTests is { } read ? read()
        : MemoryPressureForTests is not null ? null : ProcmanMemoryTelemetryReader.Read();

    private void AutomaticMemoryTick(MemoryWork work, DateTime now)
    {
        if (!MemoryOperationAllowed(work))
        { lock (_gate) { if (work.Revision == _memoryRevision) _memoryHighSince = null; } return; }
        lock (_gate)
        {
            if (!MemoryWorkCurrent(work)) return;
            var cooldown = TimeSpan.FromSeconds(MemoryPolicy().MemoryCooldown(work.Config.MemoryCooldownSeconds));
            if (_lastAutomaticRound is { } round && now - round < cooldown) return;
            _memoryHighSince ??= now;
            if (now - _memoryHighSince < MemoryPressureWindowForTests)
            { _memoryFeedback = "内存压力达到阈值，正在确认是否持续高压。"; return; }
            if (_lastAutomaticCandidateScan is { } scan && now - scan < cooldown) return;
            _lastAutomaticCandidateScan = now;
        }
        using var targetLease = GameTargetService.Default.BeginOperation();
        var mode = work.Config.MemoryMode;
        var target = mode == ProcmanMemoryModes.Aggressive ? null : CaptureMemoryTarget();
        var sample = mode == ProcmanMemoryModes.Aggressive ? [] : ReadMemorySample(() => MemoryWorkCurrent(work));
        var candidates = mode == ProcmanMemoryModes.Aggressive ? []
            : MemoryCandidates(sample, work.Config, target!, () => MemoryWorkCurrent(work) && MemoryTargetCurrent(target!));
        lock (_gate)
        {
            if (!MemoryWorkCurrent(work)) return;
            if (mode != ProcmanMemoryModes.Aggressive && candidates.Length == 0)
            {
                _memoryFeedback = "没有当前可回收的进程；压力每 30 秒检查，候选扫描沿用设置间隔，不追加无效或回涨退避。";
                return;
            }
            _lastAutomaticRound = now;
        }
        var report = mode == ProcmanMemoryModes.Aggressive ? TrimGlobalMemory(work)
            : TrimMemoryBatch(candidates.Select(c => c.Identity).ToList(), sample, target!, work);
        lock (_gate)
        {
            if (work.Revision != _memoryRevision) return;
            _automaticMemoryReport = report;
            _memoryHighSince = null;
            _memoryFeedback = "本轮已处理；下一轮沿用用户设置的最短间隔，不因无明显效果或快速回涨额外延长。";
        }
    }
}
