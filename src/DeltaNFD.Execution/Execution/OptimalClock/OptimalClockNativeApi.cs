using System.Diagnostics;
using System.Globalization;

namespace DeltaNFD.Execution.OptimalClock;

/// <summary>
/// 原生测量实现：每个候选都在**全新子进程**里完成，规避 Win11 对长驻进程反复改值的静默忽略。
/// 全局语义下 = 「一个进程持有 + 另一个不请求的进程测量」；按进程语义下 = 「同一新进程自请求自测」。
/// </summary>
internal sealed class NativeOptimalClockMeasurementApi(string executablePath,
    Func<ProcessStartInfo, IOptimalClockChildProcess>? startChild = null,
    Action<string, object?>? diagnostics = null) : IOptimalClockMeasurementApi
{
    private readonly HashSet<IOptimalClockChildProcess> _pendingCleanup = [];
    internal int PendingCleanupCount => _pendingCleanup.Count;
    public bool HasPendingCleanup => _pendingCleanup.Count > 0;
    public void CleanupPending() => RetryPendingCleanup();
    internal void RetryPendingCleanup()
    {
        foreach (var child in _pendingCleanup.ToArray()) Kill(child);
    }

    public OptimalClockSample? MeasureGlobal(uint resolution100ns, int samples, int spinMs)
    {
        var holder = StartChild(OptimalClockProbeHost.HoldArgument, resolution100ns.ToString(CultureInfo.InvariantCulture));
        try
        {
            var held = ReadLine(holder, "HELD:");
            if (held is null) return null;
            var parts = held.Split(':');
            if (parts.Length < 6 || parts[1] != "true") return null;
            var granted = ParseUInt(parts[3]);
            var current = ParseUInt(parts[4]);
            var measured = MeasureInFreshProcess(samples, spinMs);
            if (measured is null) return null;
            return measured with { Requested100ns = resolution100ns, Granted100ns = granted,
                Current100ns = measured.ObservedBefore100ns ?? current };
        }
        finally
        {
            // 必须显式结束持有进程：只靠 Dispose 依赖管道关闭，可能留下持有进程长期改变系统时钟。
            Kill(holder);
        }
    }

    public OptimalClockSample? MeasureSelf(uint resolution100ns, int samples, int spinMs)
    {
        var child = StartChild(OptimalClockProbeHost.SelfArgument,
            resolution100ns.ToString(CultureInfo.InvariantCulture), samples.ToString(CultureInfo.InvariantCulture));
        string? line;
        try { line = ReadLine(child, "SELF:"); }
        finally { Kill(child); }
        if (line is null) return null;
        var parts = line.Split(':');
        if (parts.Length < 12 || parts[1] != "true") return null;
        return new OptimalClockSample
        {
            Requested100ns = resolution100ns,
            Granted100ns = ParseUInt(parts[3]),
            Current100ns = ParseUInt(parts[4]),
            Median = ParseDouble(parts[5]),
            Mean = ParseDouble(parts[6]),
            P95 = ParseDouble(parts[7]),
            Min = ParseDouble(parts[8]),
            Max = ParseDouble(parts[9]),
            Over130Pct = ParseDouble(parts[10]),
            Samples = (int)ParseUInt(parts[11]),
        };
    }

    public IOptimalClockLoad? StartLoad(int threads) => StartLoad(threads, Stopwatch.StartNew());

    private IOptimalClockLoad? StartLoad(int threads, Stopwatch totalLifetime)
    {
        // 续期也有总上限；超时失败，单个子进程仍保持120秒硬上限。
        if (threads <= 0 || totalLifetime.Elapsed.TotalMinutes >= 10) return null;
        var lifetime = Stopwatch.StartNew();
        var child = StartChild(OptimalClockProbeHost.LoadArgument,
            threads.ToString(CultureInfo.InvariantCulture), "120");
        var transferred = false;
        try
        {
            var line = ReadLine(child, "LOAD:");
            if (line is null) return null;
            var actual = (int)ParseUInt(line.Split(':').ElementAtOrDefault(1) ?? "0");
            var load = new NativeLoad(child, actual > 0 ? actual : threads, () => StartLoad(threads, totalLifetime), () => lifetime.Elapsed.TotalSeconds, Kill);
            transferred = true;
            return load;
        }
        finally
        {
            if (!transferred) Kill(child);
        }
    }

    internal sealed class NativeLoad(IOptimalClockChildProcess child, int threads, Func<IOptimalClockLoad?> restart, Func<double>? elapsedSeconds = null, Action<IOptimalClockChildProcess>? cleanup = null) : IOptimalClockLoad
    {
        private readonly Stopwatch _age = Stopwatch.StartNew();
        private double AgeSeconds => elapsedSeconds?.Invoke() ?? _age.Elapsed.TotalSeconds;
        private IOptimalClockLoad? _replacement;
        private bool _stopped;
        private bool _childStopped;
        public int Threads { get; } = threads;
        public bool IsActive => !_stopped && (_replacement?.IsActive ?? (!_childStopped && !child.HasExited && AgeSeconds < 115));

        public bool EnsureActive()
        {
            if (_replacement is not null) return _replacement.EnsureActive();
            if (!IsActive) return false;
            if (AgeSeconds < 60) return true;
            // 先结束旧负载再创建有独立120秒硬上限的新负载，不延长任何子进程的硬寿命。
            StopChild();
            _replacement = restart();
            return _replacement is not null && _replacement.EnsureActive();
        }

        private void StopChild()
        {
            if (_childStopped) return;
            if (cleanup is not null) cleanup(child);
            else
            {
                ConfirmExited(child);
                child.Dispose();
            }
            _childStopped = true;
        }

        public void Dispose()
        {
            if (_stopped) return;
            if (_replacement is not null) _replacement.Dispose();
            else StopChild();
            _stopped = true;
        }
    }

    public double? MeasureRequestlessMs(int samples)
    {
        var measured = MeasureInFreshProcess(samples, 0);
        return OptimalClockScanner.ValidSample(measured, samples) ? measured!.Median : null;
    }

    private OptimalClockSample? MeasureInFreshProcess(int samples, int spinMs)
    {
        var child = StartChild(OptimalClockProbeHost.MeasureArgument, samples.ToString(CultureInfo.InvariantCulture),
            spinMs.ToString(CultureInfo.InvariantCulture));
        string? line;
        try { line = ReadLine(child, "MEAS:"); }
        finally { Kill(child); }
        if (line is null) return null;
        var parts = line.Split(':');
        if (parts.Length < 8) return null;
        return new OptimalClockSample
        {
            Requested100ns = 0,
            Granted100ns = 0,
            Current100ns = 0,
            Median = ParseDouble(parts[1]),
            Mean = ParseDouble(parts[2]),
            P95 = ParseDouble(parts[3]),
            Min = ParseDouble(parts[4]),
            Max = ParseDouble(parts[5]),
            Over130Pct = ParseDouble(parts[6]),
            Samples = (int)ParseUInt(parts[7]),
            ObservedBefore100ns = parts.Length >= 10 ? ParseUInt(parts[8]) : null,
            ObservedAfter100ns = parts.Length >= 10 ? ParseUInt(parts[9]) : null,
        };
    }

    private IOptimalClockChildProcess StartChild(string argument, params string[] values)
    {
        RetryPendingCleanup();
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(argument);
        foreach (var value in values) startInfo.ArgumentList.Add(value);
        // 不在测量子进程已开始采样时同步刷盘，避免日志 IO 混入 Sleep 样本。
        return (startChild ?? OptimalClockChildProcess.Start)(startInfo);
    }

    private void Kill(IOptimalClockChildProcess child)
    {
        // 失败时保留强引用；下一次探针前必须先重试清理，不允许污染后续候选。
        _pendingCleanup.Add(child);
        try { ConfirmExited(child); }
        catch (Exception ex)
        {
            PendulumDiagnosticLog.Emit(diagnostics, "probe-child-release-unconfirmed", new { child.Pid, error = ex.ToString() });
            throw new InvalidOperationException("测量子进程释放未确认，已中止扫描并保留清理责任：" + ex.Message, ex);
        }
        _pendingCleanup.Remove(child);
        PendulumDiagnosticLog.Emit(diagnostics, "probe-child-exited", new { child.Pid });
        child.Dispose();
    }

    private static void ConfirmExited(IOptimalClockChildProcess child)
    {
        try { child.CloseInput(); } catch { }
        if (!child.HasExited && !child.WaitForExit(3000)) child.Kill();
        if (!child.HasExited && !child.WaitForExit(3000))
            throw new InvalidOperationException("子进程仍在运行");
        if (!child.HasExited) throw new InvalidOperationException("无法确认子进程退出");
    }

    private string? ReadLine(IOptimalClockChildProcess child, string prefix)
    {
        try
        {
            var readStartedUtc = DateTimeOffset.UtcNow;
            var line = child.ReadReadyLine();
            PendulumDiagnosticLog.Emit(diagnostics, "probe-child-output", new { child.Pid, prefix, readStartedUtc, line });
            return line is not null && line.StartsWith(prefix, StringComparison.Ordinal) ? line : null;
        }
        // 调用方finally统一清理：超时不单独吞掉Kill失败或丢弃清理责任。
        catch (TimeoutException) { PendulumDiagnosticLog.Emit(diagnostics, "probe-child-timeout", new { child.Pid, prefix }); return null; }
        catch (InvalidOperationException ex) when (child.HasExited)
        { PendulumDiagnosticLog.Emit(diagnostics, "probe-child-error", new { child.Pid, prefix, error = ex.Message }); return null; }
    }

    private static double ParseDouble(string text) => double.Parse(text, CultureInfo.InvariantCulture);
    private static uint ParseUInt(string text) => uint.Parse(text, CultureInfo.InvariantCulture);
}

/// <summary>
/// 持有进程：用「全新子进程」常驻持有一个计时器分辨率请求。
/// 子进程在 stdin 关闭（父进程退出/崩溃）时自行退出并释放，避免残留请求。
/// </summary>
internal interface IOptimalClockChildProcess : IDisposable
{
    int? Pid => null;
    bool HasExited { get; }
    void CloseInput();
    bool WaitForExit(int milliseconds);
    void Kill();
    string? ReadReadyLine();
}

internal sealed class OptimalClockChildProcess(Process child) : IOptimalClockChildProcess
{
    public int? Pid => child.Id;
    public bool HasExited => child.HasExited;
    public void CloseInput() => child.StandardInput.Close();
    public bool WaitForExit(int milliseconds) => child.WaitForExit(milliseconds);
    public void Kill() => child.Kill(entireProcessTree: true);
    public string? ReadReadyLine() => child.StandardOutput.ReadLineAsync()
        .WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
    public void Dispose() => child.Dispose();
    internal static IOptimalClockChildProcess Start(ProcessStartInfo info)
    {
        var process = new Process { StartInfo = info };
        try { process.Start(); return new OptimalClockChildProcess(process); }
        catch { process.Dispose(); throw; }
    }
}

internal sealed class OptimalClockHolder(string executablePath,
    Func<ProcessStartInfo, IOptimalClockChildProcess>? startChild = null, Action<string, object?>? diagnostics = null) : IDisposable
{
    private IOptimalClockChildProcess? _process;
    private uint _resolution100ns;

    internal bool Holding
    {
        get { try { return _process is not null && !_process.HasExited; } catch { return _process is not null; } }
    }
    internal uint Resolution100ns => _resolution100ns;
    internal uint Granted100ns { get; private set; }
    internal uint Current100ns { get; private set; }
    internal string Error { get; private set; } = "";

    internal bool Ensure(uint resolution100ns)
    {
        if (Holding && _resolution100ns == resolution100ns) return true;
        Stop();
        _resolution100ns = resolution100ns;
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(OptimalClockProbeHost.HoldArgument);
        startInfo.ArgumentList.Add(resolution100ns.ToString(CultureInfo.InvariantCulture));
        try
        {
            var child = (startChild ?? OptimalClockChildProcess.Start)(startInfo);
            _process = child;
            var line = child.ReadReadyLine();
            PendulumDiagnosticLog.Emit(diagnostics, "holder-ready", new { child.Pid, resolution100ns, line,
                preservePolicy = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ? "required" : "Win10-not-applied" });
            if (line is null || !line.StartsWith("HELD:", StringComparison.Ordinal)) { Stop(); Error = "持有进程未在限定时间内报告就绪"; return false; }
            var parts = line.Split(':');
            if (parts.Length < 6 || parts[1] != "true")
            {
                Error = parts.Length >= 6 ? "持有进程请求失败：" + parts[5] : "持有进程请求失败";
                Stop();
                return false;
            }
            Granted100ns = uint.Parse(parts[3], CultureInfo.InvariantCulture);
            Current100ns = uint.Parse(parts[4], CultureInfo.InvariantCulture);
            Error = "";
            return true;
        }
        catch (Exception ex)
        {
            PendulumDiagnosticLog.Emit(diagnostics, "holder-error", new { resolution100ns, error = ex.ToString() });
            Error = "启动持有进程失败：" + ex.Message;
            Stop();
            return false;
        }
    }

    internal void Stop()
    {
        var process = _process;
        if (process is null) return;
        try { process.CloseInput(); } catch { }
        try
        {
            if (!process.HasExited && !process.WaitForExit(3000)) process.Kill();
            if (!process.HasExited && !process.WaitForExit(3000))
                throw new InvalidOperationException("持有进程仍未退出");
            if (!process.HasExited) throw new InvalidOperationException("无法确认持有进程退出");
        }
        catch (Exception ex)
        {
            PendulumDiagnosticLog.Emit(diagnostics, "holder-release-unconfirmed", new { process.Pid, error = ex.ToString() });
            Error = "时钟释放未确认：" + ex.Message;
            throw new InvalidOperationException(Error, ex);
        }
        _process = null;
        PendulumDiagnosticLog.Emit(diagnostics, "holder-exited", new { process.Pid });
        process.Dispose();
    }

    public void Dispose() => Stop();
}
