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
    private DateTime _memoryBackoffUntil;
    private string _memoryFeedback = "";
    private readonly List<(ProcmanProcessIdentity Identity, long BeforeBytes, DateTime Time)> _memoryObservations = [];
    private readonly Dictionary<string, DateTime> _memoryReboundUntil = new(StringComparer.OrdinalIgnoreCase);
    private ProcmanMemoryTrimReport? _automaticMemoryReport;
    internal Func<uint?>? MemoryPressureForTests { get; set; }
    internal Func<ProcmanMemoryTelemetry?>? MemoryTelemetryForTests { get; set; }
    internal TimeSpan MemoryPressureWindowForTests { get; set; } = TimeSpan.FromSeconds(30);
    internal TimeSpan MemoryObservationDelayForTests { get; set; } = TimeSpan.FromSeconds(30);
    internal Func<EmergencyPolicyDecision>? MemoryPolicyForTests { get; set; }
    internal bool BackgroundLoopDisabledForTests { get; set; }
    private EmergencyPolicyDecision MemoryPolicy() => MemoryPolicyForTests?.Invoke() ?? EmergencyPolicies.Current;

    private bool IsBackgroundExcluded(string path) => _backgroundLoadError.Length > 0 || _backgroundConfig.ExcludedExecutablePaths.Contains(path, StringComparer.OrdinalIgnoreCase);
    public ProcmanBackgroundStatus BackgroundStatus()
    {
        lock (_gate)
        {
            var policy = MemoryPolicy();
            var note = _backgroundLoadError;
            if (note.Length == 0 && OfflineModeGuard.BlocksNormalAutomation) note = "旧版设置恢复期间暂停自动回收。";
            if (note.Length == 0 && policy.ManualMemoryCleanOnly) note = "紧急策略仅允许手动回收。";
            if (note.Length == 0) note = policy.BlockReason(BackendCommands.CpuFrameMemoryClean) ?? "";
            return new(_backgroundConfig with { ExcludedExecutablePaths = [.. _backgroundConfig.ExcludedExecutablePaths] },
                _automaticMemoryActive && !_frozen && note.Length == 0,
                _lastAutomaticCheck is { } time ? new DateTimeOffset(time, TimeSpan.Zero) : null, _automaticMemoryReport, note, _memoryFeedback);
        }
    }

    public OperationResult SaveBackgroundConfig(ProcmanBackgroundConfig config)
    {
        config = ProcmanBackgroundStore.Validate(config);
        if (config.AutomaticMemoryEnabled && OfflineModeGuard.BlocksNormalAutomation)
            return OperationResult.Fail("旧版设置恢复完成前不能启用自动内存回收。");
        lock (_gate)
        {
            _backgroundStore.Save(config);
            _backgroundConfig = config;
            _backgroundLoadError = "";
            _automaticMemoryActive = config.AutomaticMemoryEnabled;
            _memoryHighSince = null;
            _frozen = false;
            // The new exclusion also releases an existing suppression owned by us.
            RestoreResponsivenessAdjustments(r => IsBackgroundExcluded(r.ExecutablePath));
            if (_changeLog.Records.Any(r => r.Owner == ProcmanControlCoordinator.OwnerResponsiveness && IsBackgroundExcluded(r.ExecutablePath)))
                SetError("排除名单已保存，部分已有后台抑制尚未还原，记录已保留，请核对与恢复。");
        }
        if (_automaticMemoryActive || ResponsivenessEnabled || _rules.EnabledRules().Count > 0) _ = StartRulesEngine();
        else
        {
            _ = StopRulesEngineAsync().GetAwaiter().GetResult();
            lock (_gate) _frozen = false; // A normal configuration change must still allow manual previews.
        }
        var pending = _changeLog.Records.Any(r => r.Owner == ProcmanControlCoordinator.OwnerResponsiveness && IsBackgroundExcluded(r.ExecutablePath));
        return OperationResult.Ok("后台管理设置已保存。" + (config.AutomaticMemoryEnabled ? "自动回收按内存压力触发。" : "自动回收已关闭。")
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
            _automaticMemoryActive = _backgroundConfig.AutomaticMemoryEnabled && !OfflineModeGuard.BlocksNormalAutomation;
        }
        return _automaticMemoryActive || _rules.EnabledRules().Count > 0 ? StartRulesEngine() : OperationResult.Ok("没有启用的自动任务。");
    }

    private uint? MemoryPressure()
    {
        if (MemoryPressureForTests is { } read) return read();
        return ReadMemoryTelemetry()?.LoadPercent;
    }

    private ProcmanMemoryTelemetry? ReadMemoryTelemetry() => MemoryTelemetryForTests is { } read ? read()
        : MemoryPressureForTests is not null ? null : ProcmanMemoryTelemetryReader.Read();

    private void ObserveMemoryRebound(DateTime now)
    {
        foreach (var observation in _memoryObservations.Where(o => now - o.Time >= MemoryObservationDelayForTests).ToArray())
        {
            _memoryObservations.Remove(observation);
            if (now - observation.Time > TimeSpan.FromMinutes(2)) continue; // A resume after sleep is not evidence of rapid rebound.
            if (MemorySize(observation.Identity) is not { } size || size < observation.BeforeBytes * .9) continue;
            // Extend this identity's cooldown to at least 30 minutes from its original attempt.
            _memoryReboundUntil[MemoryKey(observation.Identity)] = observation.Time.AddMinutes(30);
            _memoryFeedback = "部分应用工作集快速回涨，已延长其冷却至至少 30 分钟，避免反复换页。";
        }
    }

    internal void AutomaticMemoryOnceForTests(CancellationToken token = default) => AutomaticMemoryTick(token);
    private void AutomaticMemoryTick(CancellationToken token)
    {
        lock (_gate)
        {
            if (!_automaticMemoryActive || _frozen || token.IsCancellationRequested || OfflineModeGuard.BlocksNormalAutomation)
            { _memoryHighSince = null; return; }
            var now = ClockForTests?.Invoke() ?? DateTime.UtcNow;
            if (_lastAutomaticCheck is { } check && now - check < TimeSpan.FromSeconds(30)) return;
            _lastAutomaticCheck = now;
            ObserveMemoryRebound(now);
            var policy = MemoryPolicy();
            if (policy.ManualMemoryCleanOnly || policy.BlockReason(BackendCommands.CpuFrameMemoryClean) is not null)
            { _memoryHighSince = null; return; }
            if (MemoryPressure() is not { } pressure || pressure > 100 || pressure < policy.MemoryThreshold(_backgroundConfig.MemoryPressurePercent))
            { _memoryHighSince = null; return; }
            if (now < _memoryBackoffUntil) return;
            if (_lastAutomaticRound is { } round && now - round < TimeSpan.FromSeconds(Math.Max(600, policy.MemoryCooldownSeconds))) return;
            _memoryHighSince ??= now;
            if (now - _memoryHighSince < MemoryPressureWindowForTests) { _memoryFeedback = "内存压力达到阈值，正在确认是否持续高压。"; return; }
            var sample = SampleForTests?.Invoke() ?? _sampler.Sample();
            var candidates = MemoryCandidates(sample);
            if (candidates.Length == 0) return;
            _lastAutomaticRound = now; // Failed/denied targets do not cause a rapid retry loop either.
            _automaticMemoryReport = TrimMemoryBatch(candidates.Select(c => c.Identity).ToList(), token, automatic: true);
            _memoryHighSince = null;
            if (!_automaticMemoryReport.Items.Any(i => i.Status == "completed" && i.BeforeBytes is { } before && i.AfterBytes is { } after && after < before * .95))
            {
                _memoryBackoffUntil = now.AddMinutes(20);
                _memoryFeedback = "本轮未确认明显工作集缩减，自动回收冷却延长至 20 分钟；工作集缩减不代表释放等量物理内存。";
            }
            else _memoryFeedback = "本轮已完成，等待观察工作集是否快速回涨。";
        }
    }
}
