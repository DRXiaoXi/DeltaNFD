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
    private DateTime? _memoryHighSince;
    private string _memoryFeedback = "";
    private ProcmanMemoryTrimReport? _automaticMemoryReport;
    internal Func<uint?>? MemoryPressureForTests { get; set; }
    internal Func<ProcmanMemoryTelemetry?>? MemoryTelemetryForTests { get; set; }
    internal TimeSpan MemoryPressureWindowForTests { get; set; } = TimeSpan.FromSeconds(30);
    internal Func<EmergencyPolicyDecision>? MemoryPolicyForTests { get; set; }
    internal bool BackgroundLoopDisabledForTests { get; set; }
    internal Func<int>? LogicalProcessorCountForTests { get; set; }
    internal static int AdaptiveCandidateThreshold(int logicalProcessors)
        => Math.Clamp((int)Math.Round(50d / Math.Max(1, logicalProcessors), MidpointRounding.AwayFromZero), 1, 5);
    private int ResolveCandidateThreshold(int configured)
        => configured > 0 ? configured : AdaptiveCandidateThreshold(LogicalProcessorCountForTests?.Invoke()
            ?? ProcmanTopology.SystemGroupMasks().Sum(g => System.Numerics.BitOperations.PopCount(g.Mask)));
    internal Func<bool>? FrameModeActiveForTests { get; set; }
    internal Func<bool>? FrameSuppressionSelectedForTests { get; set; }
    internal Func<bool>? FrameMemorySelectedForTests { get; set; }
    private bool FrameBackgroundActive => (FrameModeActiveForTests?.Invoke() ?? AppSettingsStore.Read().FrameModeActive)
        && !OfflineModeGuard.BlocksNormalAutomation;
    private bool FrameSuppressionActive => FrameBackgroundActive
        && (FrameSuppressionSelectedForTests?.Invoke() ?? AppSettingsStore.Read().FrameBackgroundSuppressionEnabled);
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
            if (note.Length == 0 && OfflineModeGuard.BlocksNormalAutomation) note = "旧版设置恢复期间暂停自动回收。";
            if (note.Length == 0 && !FrameMemorySelected) note = "未选择帧格中的“自动内存回收”功能。";
            if (note.Length == 0 && !FrameBackgroundActive) note = "等待开启一键帧格模式。";
            if (note.Length == 0 && policy.ManualMemoryCleanOnly) note = "紧急策略仅允许手动回收。";
            if (note.Length == 0) note = policy.BlockReason(BackendCommands.CpuFrameMemoryClean) ?? "";
            return new(_backgroundConfig with { ExcludedExecutablePaths = [.. _backgroundConfig.ExcludedExecutablePaths] },
                _automaticMemoryActive && !_frozen && note.Length == 0,
                _lastAutomaticCheck is { } time ? new DateTimeOffset(time, TimeSpan.Zero) : null, _automaticMemoryReport, note, _memoryFeedback,
                ResolveCandidateThreshold(_backgroundConfig.Suppression.CandidateCpuFloorPercent),
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
        bool suppressionChanged;
        lock (_gate)
        {
            suppressionChanged = _backgroundConfig.Suppression != config.Suppression;
            _backgroundStore.Save(config);
            if (_backgroundConfig.MemoryBatchLimit != config.MemoryBatchLimit || _backgroundConfig.MemoryMode != config.MemoryMode) _memoryPreview = null;
            _backgroundConfig = config;
            _backgroundLoadError = "";
            _automaticMemoryActive = config.AutomaticMemoryEnabled && FrameAutomaticMemoryActive;
            _memoryHighSince = null;
            _frozen = false;
            // The new exclusion also releases an existing suppression owned by us.
            RestoreResponsivenessAdjustments(r => IsBackgroundExcluded(r.ExecutablePath));
            if (_changeLog.Records.Any(r => r.Owner == ProcmanControlCoordinator.OwnerResponsiveness && IsBackgroundExcluded(r.ExecutablePath)))
                SetError("排除名单已保存，部分已有后台抑制尚未还原，记录已保留，请核对与恢复。");
        }
        if (suppressionChanged || (_responsiveness is { Enabled: true }) != FrameSuppressionActive)
        {
            var adjusted = ConfigureResponsiveness(SuppressionArgs(config.Suppression, FrameSuppressionActive));
            if (!adjusted.Success) return OperationResult.Fail("参数已保存，但后台抑制同步未完成：" + adjusted.Message);
        }
        if (_automaticMemoryActive || ResponsivenessEnabled || _rules.EnabledRules().Count > 0) _ = StartRulesEngine();
        else
        {
            _ = StopRulesEngineAsync().GetAwaiter().GetResult();
            lock (_gate) _frozen = false; // A normal configuration change must still allow manual previews.
        }
        var pending = _changeLog.Records.Any(r => r.Owner == ProcmanControlCoordinator.OwnerResponsiveness && IsBackgroundExcluded(r.ExecutablePath));
        return OperationResult.Ok("后台管理设置已保存，后台功能由帧格总开关与各自的功能开关共同控制。"
            + (pending ? "部分已有后台抑制尚未还原，记录保留，请在核对与恢复中处理。" : ""));
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
        var suppressionActive = FrameSuppressionActive;
        if (!memoryActive) lock (_gate) _automaticMemoryActive = false;
        ProcmanBackgroundConfig config;
        try { config = _backgroundStore.Load(); }
        catch (InvalidDataException ex)
        {
            lock (_gate) { _backgroundLoadError = ex.Message; _automaticMemoryActive = false; }
            var stopped = ConfigureResponsiveness(new() { Enabled = false });
            return OperationResult.Fail(ex.Message + " " + stopped.Message);
        }
        var suppression = ConfigureResponsiveness(SuppressionArgs(config.Suppression, suppressionActive));
        if (!suppression.Success) return OperationResult.Fail("后台抑制随帧格同步未完成：" + suppression.Message);
        var memory = SaveBackgroundConfig(config with { AutomaticMemoryEnabled = memoryActive });
        if (!memory.Success) return memory;
        var status = BackgroundStatus();
        if (memoryActive && !status.AutomaticMemoryActive)
            return OperationResult.Fail("帧格后台功能同步后，自动内存回收当前暂停：" + status.Note);
        return OperationResult.Ok(active
            ? $"帧格后台功能：后台抑制{(suppressionActive ? "已启用" : "未选择")}，自动内存回收{(memoryActive ? "已启用" : "未选择")}。"
            : "后台抑制与自动内存回收已随帧格停用，功能选项和详细参数保留。");
    }

    private static ProcmanResponsivenessConfigArgs SuppressionArgs(ProcmanSuppressionOptions options, bool enabled) => new()
    {
        Enabled = enabled, CpuBusyThreshold = options.CpuBusyThreshold, CpuBusySeconds = options.CpuBusySeconds,
        CpuIdleThreshold = options.CpuIdleThreshold, CpuIdleSeconds = options.CpuIdleSeconds,
        CooldownSeconds = options.CooldownSeconds, CandidateCpuFloorPercent = options.CandidateCpuFloorPercent,
        MaxAdjustPerRound = options.MaxAdjustPerRound,
    };

    private uint? MemoryPressure()
    {
        if (MemoryPressureForTests is { } read) return read();
        return ReadMemoryTelemetry()?.LoadPercent;
    }

    private ProcmanMemoryTelemetry? ReadMemoryTelemetry() => MemoryTelemetryForTests is { } read ? read()
        : MemoryPressureForTests is not null ? null : ProcmanMemoryTelemetryReader.Read();

    internal void AutomaticMemoryOnceForTests(CancellationToken token = default) => AutomaticMemoryTick(token);
    private void AutomaticMemoryTick(CancellationToken token)
    {
        lock (_gate)
        {
            if (!_automaticMemoryActive || !FrameAutomaticMemoryActive || _frozen || token.IsCancellationRequested || OfflineModeGuard.BlocksNormalAutomation)
            { _memoryHighSince = null; return; }
            var now = ClockForTests?.Invoke() ?? DateTime.UtcNow;
            if (_lastAutomaticCheck is { } check && now - check < TimeSpan.FromSeconds(30)) return;
            _lastAutomaticCheck = now;
            var policy = MemoryPolicy();
            if (policy.ManualMemoryCleanOnly || policy.BlockReason(BackendCommands.CpuFrameMemoryClean) is not null)
            { _memoryHighSince = null; return; }
            if (MemoryPressure() is not { } pressure || pressure > 100 || pressure < policy.MemoryThreshold(_backgroundConfig.MemoryPressurePercent))
            { _memoryHighSince = null; return; }
            if (_lastAutomaticRound is { } round && now - round < TimeSpan.FromSeconds(policy.MemoryCooldown(_backgroundConfig.MemoryCooldownSeconds))) return;
            _memoryHighSince ??= now;
            if (now - _memoryHighSince < MemoryPressureWindowForTests) { _memoryFeedback = "内存压力达到阈值，正在确认是否持续高压。"; return; }
            var mode = _backgroundConfig.MemoryMode;
            var candidates = mode == ProcmanMemoryModes.Aggressive ? [] : MemoryCandidates(ReadMemorySample());
            if (mode != ProcmanMemoryModes.Aggressive && candidates.Length == 0)
            { _memoryFeedback = "没有当前可回收的进程；仍按用户设置的间隔检查，不追加无效或回涨退避。"; return; }
            _lastAutomaticRound = now;
            _automaticMemoryReport = mode == ProcmanMemoryModes.Aggressive
                ? TrimGlobalMemory(token, automatic: true)
                : TrimMemoryBatch(candidates.Select(c => c.Identity).ToList(), token, automatic: true);
            _memoryHighSince = null;
            _memoryFeedback = "本轮已处理；下一轮沿用用户设置的最短间隔，不因无明显效果或快速回涨额外延长。";
        }
    }
}
