namespace DeltaNFD.Execution.OptimalClock;

/// <summary>可注入的持有进程接口（测试用假实现）。</summary>
internal interface IOptimalClockHolderApi
{
    bool Holding { get; }
    uint Resolution100ns { get; }
    uint Granted100ns { get; }
    uint Current100ns { get; }
    string Error { get; }
    bool Ensure(uint resolution100ns);
    void Stop();
}

internal sealed class NativeOptimalClockHolder(string executablePath, Action<string, object?>? diagnostics = null) : IOptimalClockHolderApi
{
    private readonly OptimalClockHolder _holder = new(executablePath, diagnostics: diagnostics);
    public bool Holding => _holder.Holding;
    public uint Resolution100ns => _holder.Resolution100ns;
    public uint Granted100ns => _holder.Granted100ns;
    public uint Current100ns => _holder.Current100ns;
    public string Error => _holder.Error;
    public bool Ensure(uint resolution100ns) => _holder.Ensure(resolution100ns);
    public void Stop() => _holder.Stop();
}

/// <summary>「钟摆计划」状态快照（后端权威结论，不是用户偏好）。</summary>
internal sealed record OptimalClockState
{
    public bool Supported { get; init; }
    public bool Enabled { get; init; }
    public bool Holding { get; init; }
    public uint Resolution100ns { get; init; }
    public uint Granted100ns { get; init; }
    public uint Current100ns { get; init; }
    /// <summary>实测确认「不请求的进程也获得该时钟」（全局计时器语义生效）。</summary>
    public bool VerifiedGlobal { get; init; }
    public DateTimeOffset? GlobalVerifiedUtc { get; init; }
    public double? RequestlessMilliseconds { get; init; }
    public OptimalClockMode Mode { get; init; }
    public bool HasError { get; init; }
    public string Message { get; init; } = "尚未核对钟摆计划状态";
    public uint? BestTypical100ns { get; init; }
    public uint? BestStable100ns { get; init; }
    /// <summary>扫描给出的「实际锁定值」（稳定侧，避开临界边缘的双峰）。</summary>
    public uint? BestLock100ns { get; init; }
    public double? BestTypicalMedianMs { get; init; }
    public double? BestStableMedianMs { get; init; }
    public double? ScanRequestlessBaselineMs { get; init; }

    /// <summary>扫描时的 CPU 负载线程数（0 = 空闲测量）。</summary>
    public int ScanLoadThreads { get; init; }

    /// <summary>产生当前锁定值的检测模式。</summary>
    public OptimalClockScanKind ScanKind { get; init; }

    /// <summary>稳定延迟模式的安全裕量（ms；0 = 最低延迟模式）。</summary>
    public double SafetyMarginMs { get; init; }

    /// <summary>稳定延迟模式的三条件验证说明。</summary>
    public string StableConditions { get; init; } = "";
    public DateTimeOffset? ScanUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public double ResolutionMilliseconds => Resolution100ns / 10000.0;
}

/// <summary>
/// 「钟摆计划」控制器：用全新子进程常驻持有最佳分辨率请求，并用「不请求的进程」实测确认
/// 该请求确实对全系统生效（全局计时器语义）。未确认全局生效时立即释放并如实说明，绝不假装成功。
/// </summary>
internal sealed class OptimalClockController(IOptimalClockHolderApi holder, IOptimalClockMeasurementApi api,
    Func<uint>? currentResolution = null, Action<uint, uint>? observe = null,
    Action<string, object?>? diagnostics = null, Func<DateTimeOffset>? clock = null)
{
    /// <summary>「系统当前分辨率不粗于请求值」的容差（100ns；500 = 0.05ms）。</summary>
    internal const uint ResolutionTolerance100ns = 500;
    private readonly Func<uint> _currentResolution = currentResolution ?? OptimalClockNative.QueryCurrent100ns;
    private readonly object _gate = new();
    private OptimalClockState _state = new();
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private DateTimeOffset _lastVerification, _retryAfter, _lastHeartbeat;
    private uint _lastObserved;
    internal static readonly TimeSpan VerificationInterval = TimeSpan.FromMinutes(1);

    internal OptimalClockState State { get { lock (_gate) return _state; } }

    // 只清本次运行状态，扫描记录继续保留。
    private OptimalClockState IdleState() => _state with
    {
        Enabled = false, Holding = false, Resolution100ns = 0, Granted100ns = 0,
        Current100ns = 0, VerifiedGlobal = false, GlobalVerifiedUtc = null, RequestlessMilliseconds = null,
        Mode = OptimalClockMode.Unknown, HasError = false, UpdatedUtc = DateTimeOffset.UtcNow,
    };

    /// <summary>启用并锁定指定分辨率；只有实测确认全局生效才返回成功。</summary>
    internal bool Apply(uint resolution100ns, bool supported)
    {
        lock (_gate)
        {
            try
            {
                PendulumDiagnosticLog.Emit(diagnostics, "lock-start", new { resolution100ns, supported });
                if ((api.HasPendingCleanup || (_state.HasError && _state.Holding)) && !Stop()) return false;
                if (!supported || resolution100ns < OptimalClockScanner.CriticalFloor100ns || resolution100ns > 156_250)
                {
                    if (!Stop()) return false;
                    _state = IdleState() with
                    {
                        Supported = supported,
                        HasError = supported,
                        Message = !supported ? "此系统尚不能使用钟摆计划（Windows 11 常规路径 / Windows 10 实验路径）"
                            : "分辨率超出允许范围（0.5100~15.625ms），未锁定",
                    };
                    return false;
                }
                uint current100ns = 0;
                bool verified = false;
                for (var attempt = 0; attempt < 2 && !verified; attempt++)
                {
                    if (!holder.Ensure(resolution100ns))
                        throw new InvalidOperationException("无法启动时钟持有进程：" + holder.Error);
                    Thread.Sleep(250);
                    current100ns = _currentResolution();
                    PendulumDiagnosticLog.Emit(diagnostics, "lock-readback", new { resolution100ns, current100ns, holder.Granted100ns, attempt });
                    observe?.Invoke(resolution100ns, current100ns);
                    verified = current100ns > 0 && current100ns <= resolution100ns + ResolutionTolerance100ns;
                    if (!verified && attempt == 0 && !Stop()) return false;
                }
                if (!verified)
                {
                    if (!Stop()) return false;
                    _state = IdleState() with
                    {
                        Supported = true, HasError = true, Resolution100ns = resolution100ns,
                        Current100ns = current100ns, Mode = OptimalClockMode.Self,
                        Message = current100ns == 0 ? "无法读取系统当前分辨率，未锁定"
                            : $"未锁定：请求 {resolution100ns / 10000.0:0.0000} ms，但系统当前仍是 {current100ns / 10000.0:0.0000} ms（全局计时器语义未生效或请求被系统忽略）",
                    };
                    return false;
                }
                var measured = MeasureEffect(resolution100ns);
                if (!EffectIsConfirmed(resolution100ns, measured))
                {
                    var detail = measured.HasValue ? measured.Value.ToString("0.####") + " ms" : "不可用";
                    var released = Stop();
                    _retryAfter = _clock() + VerificationInterval;
                    if (!released) return false;
                    _state = IdleState() with
                    {
                        Supported = true, HasError = true, Resolution100ns = resolution100ns,
                        Current100ns = current100ns, RequestlessMilliseconds = measured,
                        Message = "系统分辨率读数已变化，但无请求进程实测为 " + detail
                            + "，未确认全局生效；已释放请求，一分钟后再核对。请查看钟摆计划日志。",
                    };
                    return false;
                }
                _lastVerification = _clock();
                _retryAfter = default;
                _state = IdleState() with
                {
                    Supported = true, Enabled = true, Holding = true, Resolution100ns = resolution100ns,
                    Granted100ns = holder.Granted100ns, Current100ns = current100ns, VerifiedGlobal = true,
                    GlobalVerifiedUtc = _lastVerification,
                    RequestlessMilliseconds = measured, Mode = OptimalClockMode.Global,
                    Message = $"已全局锁定钟摆计划 {resolution100ns / 10000.0:0.0000} ms（系统当前 {current100ns / 10000.0:0.0000} ms，无请求进程实测 {(measured.HasValue ? measured.Value.ToString("0.##") + " ms" : "不可用")}）",
                };
                PendulumDiagnosticLog.Emit(diagnostics, "lock-confirmed", _state);
                return true;
            }
            catch (Exception ex)
            {
                PendulumDiagnosticLog.Emit(diagnostics, "lock-error", new { error = ex.ToString() });
                if (!Stop()) return false;
                _state = IdleState() with
                {
                    Supported = supported, HasError = true, Mode = OptimalClockMode.Self,
                    Message = "钟摆计划验证失败，已释放请求：" + ex.Message,
                };
                return false;
            }
        }
    }

    /// <summary>只有确认持有进程结束才返回释放成功；失败保留责任供重试。</summary>
    internal bool Stop()
    {
        lock (_gate)
        {
            var hadResponsibility = holder.Holding || _state.Holding || api.HasPendingCleanup;
            var failures = new List<string>();
            // 两类责任都必须尝试；一方失败不能跳过另一方的清理。
            try
            {
                holder.Stop();
                if (holder.Holding) throw new InvalidOperationException("持有进程仍在运行");
            }
            catch (Exception ex) { failures.Add(ex.Message); }
            try
            {
                api.CleanupPending();
                if (api.HasPendingCleanup) throw new InvalidOperationException("测量子进程释放仍未确认");
            }
            catch (Exception ex) { failures.Add(ex.Message); }
            if (failures.Count == 0)
            {
                _retryAfter = default;
                _state = IdleState() with { Message = "钟摆计划已释放" };
                if (hadResponsibility) PendulumDiagnosticLog.Emit(diagnostics, "release-confirmed", new { });
                return true;
            }
            _state = _state with
            {
                Holding = true, HasError = true, VerifiedGlobal = false,
                Resolution100ns = holder.Resolution100ns,
                Message = "钟摆计划释放未确认，保留释放责任：" + string.Join("；", failures),
                UpdatedUtc = DateTimeOffset.UtcNow,
            };
            PendulumDiagnosticLog.Emit(diagnostics, "release-unconfirmed", new { failures });
            return false;
        }
    }

    /// <summary>监督：启用中但持有进程消失时重新拉起（不重复改值，避免被系统静默忽略）。</summary>
    internal bool Ensure(bool enabled, uint resolution100ns, bool supported)
    {
        lock (_gate)
        {
            if (!enabled || !supported) return false;
            if (api.HasPendingCleanup)
            {
                if (!Stop()) return false;
                return Apply(resolution100ns, supported);
            }
            if (_state.HasError && _state.Holding) return false;
            if (_clock() < _retryAfter) return false;
            if (holder.Holding && holder.Resolution100ns == resolution100ns)
            {
                // 持有进程还活着不等于请求仍有效：系统读数可能被别人/系统策略拉粗，必须复核并在漂移时重试。
                var current = _currentResolution();
                observe?.Invoke(resolution100ns, current);
                if (current != _lastObserved || _clock() - _lastHeartbeat >= VerificationInterval)
                {
                    PendulumDiagnosticLog.Emit(diagnostics, "supervision", new { resolution100ns, current, holder.Holding });
                    _lastObserved = current; _lastHeartbeat = _clock();
                }
                if (current > 0 && current <= resolution100ns + ResolutionTolerance100ns)
                {
                    if (_clock() - _lastVerification >= VerificationInterval)
                    {
                        // 系统读数正确也不能代替其他进程的实际等待；复用既有监督节拍做低频复验。
                        double? measured;
                        try { measured = MeasureEffect(resolution100ns); }
                        catch (Exception ex)
                        {
                            PendulumDiagnosticLog.Emit(diagnostics, "effect-error", new { error = ex.ToString() });
                            measured = null;
                        }
                        if (!EffectIsConfirmed(resolution100ns, measured))
                        {
                            var released = Stop();
                            _retryAfter = _clock() + VerificationInterval;
                            if (released) _state = IdleState() with
                            {
                                Supported = true, HasError = true, Current100ns = current,
                                RequestlessMilliseconds = measured,
                                Message = "无请求进程的实际等待复验未通过，已释放钟摆请求；一分钟后再核对。请查看日志。",
                            };
                            return false;
                        }
                        _lastVerification = _clock();
                        _state = _state with { RequestlessMilliseconds = measured, GlobalVerifiedUtc = _lastVerification };
                    }
                    _state = _state with
                    {
                        Holding = true,
                        Enabled = true,
                        Current100ns = current,
                        VerifiedGlobal = _state.GlobalVerifiedUtc is not null,
                        UpdatedUtc = DateTimeOffset.UtcNow,
                    };
                    return true;
                }
                if (!Stop()) return false;
                return Apply(resolution100ns, supported);
            }
            return Apply(resolution100ns, supported);
        }
    }

    private double? MeasureEffect(uint resolution100ns)
    {
        var values = new List<double>();
        for (var batch = 0; batch < 3; batch++)
        {
            var value = api.MeasureRequestlessMs(61);
            var actual = _currentResolution();
            observe?.Invoke(resolution100ns, actual);
            PendulumDiagnosticLog.Emit(diagnostics, "effect-sample", new { resolution100ns, batch, medianMs = value, actual });
            if (value is not { } measured || !double.IsFinite(measured) || measured <= 0
                || actual == 0 || actual > (ulong)resolution100ns + ResolutionTolerance100ns) return null;
            values.Add(measured);
        }
        values.Sort();
        return values[1];
    }

    internal static bool EffectIsConfirmed(uint resolution100ns, double? measured)
        => measured is { } value && double.IsFinite(value) && value > 0
            && value <= Math.Max(2.5, resolution100ns / 10000.0 * 2.5);

    /// <summary>记录一次扫描结论（供状态展示；不改变当前持有值）。</summary>
    internal void RecordScan(OptimalClockScanResult result)
    {
        lock (_gate)
        {
            _state = _state with
            {
                BestTypical100ns = result.BestTypical?.Resolution100ns,
                BestStable100ns = result.BestStable?.Resolution100ns,
                BestLock100ns = result.BestLock?.Resolution100ns,
                BestTypicalMedianMs = result.BestTypical?.Median,
                BestStableMedianMs = result.BestStable?.Median,
                ScanRequestlessBaselineMs = result.RequestlessBaselineMs,
                ScanLoadThreads = result.LoadThreads,
                ScanKind = result.ScanKind,
                SafetyMarginMs = result.SafetyMarginMs,
                StableConditions = result.StableConditions,
                ScanUtc = result.CompletedUtc,
                UpdatedUtc = DateTimeOffset.UtcNow,
            };
        }
    }

    internal void SetWaiting(string message, bool supported)
    {
        lock (_gate)
        {
            if (_state.Holding) return;
            _state = _state with { Supported = supported, Message = message, UpdatedUtc = DateTimeOffset.UtcNow };
        }
    }
}
