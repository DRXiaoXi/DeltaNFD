using System.Diagnostics;
using System.Text.Json;

namespace DeltaNFD.Execution.Procman;

public sealed record FrameTimerPolicyResult(bool Accepted, string Error);

internal interface IFrameTimerPolicyApi
{
    (bool Ok, uint Control, uint State, string Error) Read();
    (bool Ok, string Error) Write(uint control, uint state);
}

/// <summary>Win10 trial: test only the disposable worker's own policy, never a game PID.</summary>
public static class FrameTimerPolicyProbeHost
{
    internal const string Argument = "--frame-timer-policy-probe-child";
    internal static bool Candidate(Version version) => version.Major == 10 && version.Minor == 0 && version.Build >= 17763;

    internal static FrameTimerPolicyResult Verify(IFrameTimerPolicyApi api)
    {
        try
        {
            var before = api.Read();
            if (!before.Ok) return new(false, before.Error);
            var desired = new FrameTimerPowerState(before.Control, before.State).Apply();
            var write = api.Write(desired.Control, desired.State);
            if (!write.Ok) return new(false, write.Error);
            var after = api.Read();
            if (!after.Ok) return new(false, after.Error);
            if (!new FrameTimerPowerState(after.Control, after.State).Forced
                || (after.Control & ~4u) != (before.Control & ~4u)
                || ((after.State ^ before.State) & before.Control & ~4u) != 0)
                return new(false, "进程策略回读不匹配，未确认支持忽略定时器请求。");
            return new(true, "");
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }

    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args is not [Argument]) return false;
        // This state lives only until this worker exits; it requests no timer.
        var result = Verify(new SelfApi());
        Console.Out.WriteLine(JsonSerializer.Serialize(result));
        return true;
    }
    private sealed class SelfApi : IFrameTimerPolicyApi
    {
        public (bool Ok, uint Control, uint State, string Error) Read() => ProcmanCapabilities.ReadPowerThrottling(Environment.ProcessId);
        public (bool Ok, string Error) Write(uint control, uint state) => ProcmanCapabilities.WritePowerThrottling(Environment.ProcessId, control, state);
    }

    internal static FrameTimerPolicyResult RunIsolated()
    {
        var exe = Environment.ProcessPath;
        if (exe is null || !File.Exists(exe)) return new(false, "无法定位自检子进程入口。");
        using var child = new Process { StartInfo = new ProcessStartInfo(exe)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        child.StartInfo.ArgumentList.Add(Argument);
        try
        {
            if (!child.Start()) return new(false, "策略自检子进程未启动。");
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            Task.WhenAll(output, error, child.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (child.ExitCode != 0 || output.Result.Length > 4096) return new(false, "策略自检子进程退出异常或输出无效。");
            return JsonSerializer.Deserialize<FrameTimerPolicyResult>(output.Result) ?? new(false, "策略自检没有返回结果。");
        }
        catch (Exception ex) { return new(false, "策略自检未完成：" + ex.Message); }
        finally
        {
            // Only the exact child handle created above is owned here.
            try { if (!child.HasExited) { child.Kill(); child.WaitForExit(2000); } } catch { }
        }
    }
}
