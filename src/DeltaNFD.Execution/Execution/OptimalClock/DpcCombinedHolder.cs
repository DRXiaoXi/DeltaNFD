using DeltaNFD.Services;

namespace DeltaNFD.Execution.OptimalClock;

/// <summary>DPC always holds its original 1ms; a separate owned process requests the scanned value.</summary>
internal sealed class DpcCombinedHolder(IOptimalClockHolderApi dpc, IOptimalClockHolderApi candidate) : IOptimalClockHolderApi
{
    public bool AlgorithmMode { get; set; }
    internal bool CustomResolution { get; set; }
    internal uint? BurstResolution100ns { get; set; }
    public bool DpcProcess => !AlgorithmMode;
    public bool DpcAssisted => AlgorithmMode;
    public bool DpcHolding => dpc.Holding;
    public bool HasCleanupResponsibility => dpc.Holding || candidate.Holding;
    public bool Holding => dpc.Holding && (!AlgorithmMode || candidate.Holding);
    public uint Resolution100ns => AlgorithmMode ? candidate.Resolution100ns : 10000;
    public uint Granted100ns => AlgorithmMode ? candidate.Granted100ns : 0;
    public uint Current100ns => AlgorithmMode ? candidate.Current100ns : dpc.Current100ns;
    public string Error { get; private set; } = "";
    public bool Ensure(uint requested)
    {
        try
        {
            var minimum = CustomResolution || requested == BurstResolution100ns ? 5000u : OptimalClockScanner.CriticalFloor100ns;
            if (AlgorithmMode && (requested < minimum || requested > 10000))
                throw new IOException(CustomResolution ? "DPC自定义持有只接受0.5000～1.0000ms的已验证值。" : "DPC辅助持有只接受0.5100～1.0000ms的已扫描值。");
            if (!AlgorithmMode && requested != 10000) throw new IOException("基础DPC只接受1.0000ms。");
            if (!dpc.Ensure(10000)) throw new IOException(dpc.Error);
            if (AlgorithmMode && !candidate.Ensure(requested)) throw new IOException(candidate.Error);
            if (!Holding) throw new IOException("DPC或候选持有进程未确认存活。");
            Error = ""; return true;
        }
        catch (Exception ex)
        {
            var reason = ex.Message;
            try { Stop(); } catch (Exception cleanup) { reason += "；释放待核对：" + cleanup.Message; }
            Error = reason; return false;
        }
    }
    public void Stop()
    {
        var failures = new List<string>();
        // Both owners must be attempted even when either one's cleanup fails.
        try { candidate.Stop(); if (candidate.Holding) throw new IOException("候选请求仍在运行"); } catch (Exception ex) { failures.Add(ex.Message); }
        try { dpc.Stop(); if (dpc.Holding) throw new IOException("DPC仍在运行"); } catch (Exception ex) { failures.Add(ex.Message); }
        if (failures.Count > 0) { Error = string.Join("；", failures); throw new IOException(Error); }
        Error = "";
    }
}

/// <summary>Use the original scanner with requestless measurements; reject clamped or contaminated candidate values.</summary>
internal sealed class DpcAssistedMeasurement(IOptimalClockMeasurementApi inner, Func<bool> dpcHolding,
    CancellationToken token, Action<string, object?>? diagnostics = null) : IOptimalClockMeasurementApi
{
    private void CheckBase()
    {
        token.ThrowIfCancellationRequested();
        if (!dpcHolding()) throw new IOException("DPC基底已退出或身份变化，算法扫描已停止。");
    }
    internal static bool MatchesCandidate(OptimalClockSample? sample, uint requested)
        => sample is not null && sample.Requested100ns == requested && sample.Granted100ns > 0
            && Math.Abs((long)sample.Granted100ns - requested) <= 50
            && Math.Abs((long)sample.Current100ns - requested) <= 50
            && sample.ObservedBefore100ns is { } before && Math.Abs((long)before - requested) <= 50
            && sample.ObservedAfter100ns is { } after && Math.Abs((long)after - requested) <= 50;
    public OptimalClockSample? MeasureGlobal(uint resolution100ns, int samples, int spinMs)
    {
        CheckBase();
        if (resolution100ns is < 4900 or > 10000) return null;
        var sample = inner.MeasureGlobal(resolution100ns, samples, spinMs);
        CheckBase();
        if (MatchesCandidate(sample, resolution100ns)) return sample;
        PendulumDiagnosticLog.Emit(diagnostics, "dpc-candidate-rejected", new { resolution100ns, sample,
            reason = "候选请求/授予/独立进程前后读数不匹配；不能把DPC基底1ms当作候选值" });
        return null;
    }
    public OptimalClockSample? MeasureSelf(uint resolution100ns, int samples, int spinMs) => null;
    public double? MeasureRequestlessMs(int samples) { CheckBase(); var value = inner.MeasureRequestlessMs(samples); CheckBase(); return value; }
    public IOptimalClockLoad? StartLoad(int threads)
    {
        CheckBase();
        var load = inner.StartLoad(threads);
        return load is null ? null : new Load(load, CheckBase);
    }
    public IOptimalClockLoad? StartBurstLoad()
    {
        CheckBase();
        var load = inner.StartBurstLoad();
        return load is null ? null : new Load(load, CheckBase);
    }
    public ClockBurstMeasurement? MeasureBurst(uint resolution100ns, CancellationToken cancellation,
        ClockBurstProbeMode mode = ClockBurstProbeMode.FirstPassing, ClockSamplingMask? samplingCpu = null)
    {
        CheckBase();
        var result = inner.MeasureBurst(resolution100ns, cancellation, mode, samplingCpu);
        CheckBase();
        return result;
    }
    public bool HasPendingCleanup => inner.HasPendingCleanup;
    public void CleanupPending() => inner.CleanupPending();
    private sealed class Load(IOptimalClockLoad inner, Action check) : IOptimalClockLoad
    {
        public int Threads => inner.Threads;
        public bool EnsureActive() { check(); return inner.EnsureActive(); }
        public bool IsActive { get { check(); return inner.IsActive; } }
        public void Dispose() => inner.Dispose();
    }
}
