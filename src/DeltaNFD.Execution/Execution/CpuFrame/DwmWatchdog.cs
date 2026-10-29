using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DeltaNFD.Execution.Procman;
using DeltaNFD.Services;
using Microsoft.Win32.SafeHandles;

namespace DeltaNFD.Execution.CpuFrame;

internal sealed record DwmIdentity(int Pid, long Created, int Session, string Image);
internal sealed record DwmReading(DwmIdentity Identity, TimeSpan CpuTime, double? WorkingSetMiB = null);
internal interface IDwmWatchEnvironment
{
    TimeSpan Now { get; }
    DwmReading Read();
    OperationResult Restart(DwmIdentity expected, Func<bool> allowed);
    Task Delay(TimeSpan duration, CancellationToken token);
}

/// <summary>
/// Reuses the frame tick: CPU deltas only, no sleeping tick or memory/code-region scanning.
/// The supplied tool's three-second, half-a-core measurement is retained; two high samples
/// are required. Replacement discovery and post-restart verification are separate states.
/// </summary>
internal sealed class DwmWatchdog(IDwmWatchEnvironment environment)
{
    internal static readonly TimeSpan SampleWindow = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);
    private readonly IDwmWatchEnvironment _env = environment;
    private readonly object _lifecycle = new();
    private bool _manualActive;
    private string _review = "";
    private DwmMonitorSnapshot _status = new();
    private (DwmReading Reading, TimeSpan At)? _sample;
    private DwmIdentity? _pending;
    private DwmIdentity? _replacement;
    private TimeSpan _restartAt, _replacementAt;
    private TimeSpan? _lastAttempt;
    private string _game = "";
    private int _highSamples, _failures;
    internal DwmMonitorSnapshot Status => Volatile.Read(ref _status);

    internal void Pause(string message)
    {
        // StopMonitor runs before the automation gate drains. An already submitted manual
        // command retains responsibility for its verification until it completes/cancels.
        lock (_lifecycle) { if (!_manualActive) PauseCore(message); }
    }

    private void PauseCore(string message)
    {
        var incomplete = _pending is not null;
        if (incomplete) _review = "上次重启后的复查未完成";
        _sample = null; _pending = null; _replacement = null; _highSamples = 0;
        Publish(Status with { Monitoring = false, CheckingAfterRestart = false, CpuCorePercent = null,
            Message = message + (_review.Length > 0 ? "；" + _review : ""), HasError = _review.Length > 0 });
    }

    internal void Tick(bool enabled, string game, string pauseReason, Func<bool> allowed)
    {
        if (!enabled) { Pause(pauseReason); return; }
        if (_game != game)
        {
            _game = game; _sample = null; _highSamples = 0; _failures = 0;
            Publish(Status with { RestartedThisGame = false });
        }
        Publish(Status with { Monitoring = true });
        Advance(allowed, automatic: true);
    }

    // Caller serializes this with the frame tick. No restart task survives command/host cancellation.
    internal async Task<OperationResult> RestartNowAsync(Func<bool> allowed, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_pending is not null) return OperationResult.Fail("DWM 上次重启仍在复查，未重复执行。");
        if (_lastAttempt is { } last && _env.Now - last < Cooldown)
            return OperationResult.Fail("DWM 重启冷却中（两次至少间隔 60 秒），未重复执行。");
        lock (_lifecycle)
        {
            if (_manualActive) return OperationResult.Fail("DWM 手动操作尚未结束。");
            _manualActive = true;
        }
        try
        {
            if (!allowed()) return OperationResult.Fail("DWM 操作已被停止或被恢复/紧急策略门禁阻止。");
            var reading = _env.Read();
            _failures = 0;
            var started = BeginRestart(reading, allowed);
            if (!started.Success) return started;
            while (_pending is not null)
            {
                await _env.Delay(TimeSpan.FromMilliseconds(300), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!allowed()) { PauseCore("DWM 操作已停止"); return OperationResult.Fail(Status.Message); }
                Advance(allowed, automatic: false);
            }
            return Status.HasError ? OperationResult.Fail(Status.Message) : OperationResult.Ok(Status.Message);
        }
        catch (OperationCanceledException)
        { PauseCore("DWM 复查已取消"); return OperationResult.Fail(Status.Message); }
        catch (Exception ex)
        { Fail("DWM 检测/重启失败：" + ex.Message); return OperationResult.Fail(Status.Message); }
        finally { lock (_lifecycle) _manualActive = false; }
    }

    private void Advance(Func<bool> allowed, bool automatic)
    {
        var now = _env.Now;
        DwmReading reading;
        try { reading = _env.Read(); }
        catch (Exception ex)
        {
            _sample = null; _highSamples = 0;
            if (_pending is not null && now - _restartAt >= TimeSpan.FromSeconds(30))
                Fail("DWM 重启后 30 秒内未能确认新进程：" + ex.Message);
            else Publish(Status with { CpuCorePercent = null, HasError = true, Message = "DWM 状态待核对：" + ex.Message });
            return;
        }
        Publish(Status with { Pid = reading.Identity.Pid, WorkingSetMiB = reading.WorkingSetMiB });
        if (_pending is not null)
        {
            if (now - _restartAt >= TimeSpan.FromSeconds(45))
            { Fail("DWM 重启后复查超时，未确认恢复；请手动核对，必要时注销或重启电脑。"); return; }
            if (reading.Identity == _pending)
            {
                if (now - _restartAt >= TimeSpan.FromSeconds(30)) Fail("30 秒内未确认 DWM 更换为新进程，请手动核对。");
                else Publish(Status with { Message = "已请求重启 DWM，等待 Windows 拉起新进程…" });
                return;
            }
            if (reading.Identity != _replacement)
            { _replacement = reading.Identity; _replacementAt = now; _sample = null; }
            if (now - _replacementAt < TimeSpan.FromSeconds(8))
            { Publish(Status with { Message = "已发现新的 DWM，等待 8 秒稳定后复查…", HasError = false }); return; }
            if (!TryMeasure(reading, now, out var cpu)) return;
            _pending = null; _replacement = null;
            if (cpu >= 50) Fail($"DWM 已更换为新进程，但复查 CPU 仍高（单核口径 {cpu:F1}%）；未确认恢复。");
            else
            {
                _failures = 0; _review = "";
                Publish(Status with { CheckingAfterRestart = false, HasError = false,
                    Message = $"DWM 已重启并复查，CPU 已低于触发阈值（单核口径 {cpu:F1}%）" });
            }
            return;
        }
        if (!automatic) return;
        if (_failures >= 2)
        {
            Publish(Status with { HasError = true, Message = "DWM 连续两次处理未恢复，已暂停自动重启；请手动核对。" });
            return;
        }
        if (!TryMeasure(reading, now, out var percent)) return;
        _highSamples = percent >= 50 ? _highSamples + 1 : 0;
        var cooling = _lastAttempt is { } attempt && now - attempt < Cooldown;
        Publish(Status with { HasError = false, Message = cooling ? "DWM 重启后冷却中，持续监测…"
            : _highSamples == 0 ? "DWM 持续监测中，CPU 未达到触发阈值"
            : "DWM CPU 偏高，正在确认持续负载…" });
        if (_highSamples >= 2 && !cooling && allowed()) BeginRestart(reading, allowed);
    }

    private bool TryMeasure(DwmReading reading, TimeSpan now, out double percent)
    {
        percent = 0;
        if (_sample is not { } sample || sample.Reading.Identity != reading.Identity
            || now < sample.At || now - sample.At > TimeSpan.FromSeconds(15) || reading.CpuTime < sample.Reading.CpuTime)
        {
            _sample = (reading, now); _highSamples = 0;
            Publish(Status with { CpuCorePercent = null, Message = "正在采样 DWM CPU（至少 3 秒）…" });
            return false;
        }
        var elapsed = now - sample.At;
        if (elapsed < SampleWindow) return false;
        percent = (reading.CpuTime - sample.Reading.CpuTime).TotalSeconds / elapsed.TotalSeconds * 100;
        _sample = (reading, now);
        Publish(Status with { CpuCorePercent = percent });
        return true;
    }

    private OperationResult BeginRestart(DwmReading reading, Func<bool> allowed)
    {
        if (!allowed()) return OperationResult.Fail("DWM 重启前门禁复核未通过。");
        _lastAttempt = _env.Now; _sample = null; _highSamples = 0;
        OperationResult result;
        try { result = _env.Restart(reading.Identity, allowed); }
        catch (Exception ex) { result = OperationResult.Fail(ex.Message); }
        if (!result.Success) { Fail("DWM 重启未完成：" + result.Message); return result; }
        _pending = reading.Identity; _replacement = null; _restartAt = _env.Now; _review = "";
        Publish(Status with { CheckingAfterRestart = true, RestartCount = Status.RestartCount + 1,
            RestartedThisGame = true, CpuCorePercent = null, HasError = false,
            Message = "已请求重启 DWM，等待新进程及 CPU 复查…" });
        Log.Info("DWM 守护：已请求重启当前桌面会话的 DWM；等待复查。");
        return OperationResult.Ok(Status.Message);
    }

    private void Fail(string message)
    {
        _pending = null; _replacement = null; _sample = null; _highSamples = 0; _failures++;
        _review = message;
        Publish(Status with { CheckingAfterRestart = false, HasError = true, Message = message });
        Log.Warn(message);
    }
    private void Publish(DwmMonitorSnapshot value) => Volatile.Write(ref _status, value with { UpdatedUtc = DateTimeOffset.UtcNow });
}

/// <summary>Only queries CPU/metadata; termination is restricted to one verified System32 DWM in our session.</summary>
internal sealed class NativeDwmWatchEnvironment : IDwmWatchEnvironment
{
    private readonly int _session = CurrentSession();
    private readonly string _image = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dwm.exe");
    public TimeSpan Now => TimeSpan.FromMilliseconds(Environment.TickCount64);
    public Task Delay(TimeSpan duration, CancellationToken token) => Task.Delay(duration, token);
    private static int CurrentSession() { using var self = Process.GetCurrentProcess(); return self.SessionId; }

    public DwmReading Read()
    {
        var processes = Process.GetProcessesByName("dwm");
        try
        {
            var matching = processes.Where(p => p.SessionId == _session).ToArray();
            if (matching.Length != 1) throw new IOException("当前桌面会话未找到唯一的 DWM 进程。");
            var p = matching[0];
            using var handle = OpenProcess(0x1000, false, p.Id);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var reading = ReadHandle(handle, p.Id);
            if (!IsExpected(reading.Identity, reading.Identity, _session, _image)) throw new IOException("DWM 路径/会话无法核验。");
            double? memory = null;
            try { memory = p.WorkingSet64 / 1048576d; } catch { /* Optional reference, never a restart trigger. */ }
            return reading with { WorkingSetMiB = memory };
        }
        finally { foreach (var p in processes) p.Dispose(); }
    }

    internal static bool IsExpected(DwmIdentity expected, DwmIdentity actual, int session, string image) =>
        expected.Pid > 0 && expected.Created > 0 && expected.Pid == actual.Pid && expected.Created == actual.Created
        && expected.Session == session && actual.Session == session && Path.IsPathFullyQualified(image)
        && expected.Image.Equals(image, StringComparison.OrdinalIgnoreCase) && actual.Image.Equals(image, StringComparison.OrdinalIgnoreCase);

    public OperationResult Restart(DwmIdentity expected, Func<bool> allowed)
    {
        using var handle = OpenProcess(0x1001, false, expected.Pid); // QUERY_LIMITED_INFORMATION | TERMINATE
        if (handle.IsInvalid) return OperationResult.Fail("打开 DWM 失败：" + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        var actual = ReadHandle(handle, expected.Pid).Identity;
        if (!IsExpected(expected, actual, _session, _image)) return OperationResult.Fail("DWM 身份已变化，未结束进程。");
        if (!allowed()) return OperationResult.Fail("DWM 重启前门禁已关闭，未结束进程。");
        return TerminateProcess(handle, 0) ? OperationResult.Ok("已请求结束当前 DWM，等待 Windows 自动恢复。")
            : OperationResult.Fail("结束 DWM 失败：" + new Win32Exception(Marshal.GetLastWin32Error()).Message);
    }

    private static DwmReading ReadHandle(SafeProcessHandle handle, int pid)
    {
        if (!ProcessIdToSessionId((uint)pid, out var session) || !GetExitCodeProcess(handle, out var code) || code != 259
            || !GetProcessTimes(handle, out var created, out _, out var kernel, out var user))
            throw new IOException("DWM 已退出或无法读取进程计时信息。");
        var image = ProcmanIdentity.ReadPath(handle);
        if (image.Length == 0) throw new IOException("DWM 映像路径不可读。");
        return new(new(pid, created, (int)session, image), TimeSpan.FromTicks(checked(kernel + user)));
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle handle, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeProcessHandle handle, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeProcessHandle handle, uint exitCode);
}
