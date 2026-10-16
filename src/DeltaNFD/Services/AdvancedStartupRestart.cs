using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeltaNFD.Services;

internal enum StartupRequestState { Rejected, Accepted, Unconfirmed }
internal sealed record StartupRequest(StartupRequestState State, string Message);
internal sealed record StartupPreparation(IDisposable Lease, bool Ready, string Message);
internal sealed record StartupRestartResult(StartupRequest Request, IDisposable? RetainedLease)
{
    public bool ShouldExit => Request.State != StartupRequestState.Rejected;
}

/// <summary>One-shot advanced startup only. Never persists safeboot or resumes a repair.</summary>
internal static class AdvancedStartupRestart
{
    public static bool IsSafeMode => GetSystemMetrics(67) != 0; // SM_CLEANBOOT

    internal static ProcessStartInfo CreateStartInfo(string systemDirectory)
    {
        var info = new ProcessStartInfo(Path.Combine(systemDirectory, "shutdown.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        // A nonzero timeout implies /f on Windows. Do not force other apps to close.
        foreach (var argument in new[] { "/r", "/o", "/t", "0" }) info.ArgumentList.Add(argument);
        return info;
    }

    internal static async Task<StartupRequest> RequestWindowsAsync()
    {
        Process? process;
        try { process = Process.Start(CreateStartInfo(Environment.SystemDirectory)); }
        catch (Exception ex) { return new(StartupRequestState.Rejected, "无法启动 Windows 高级启动请求：" + ex.Message); }
        if (process is null) return new(StartupRequestState.Rejected, "Windows 高级启动请求未启动。");
        using (process)
        {
            try
            {
                // After launch, cancellation cannot prove Windows has cancelled its reboot.
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                return process.ExitCode == 0
                    ? new(StartupRequestState.Accepted, "Windows 已接受高级启动请求。工具将退出；不会自动继续运行库修复。")
                    : new(StartupRequestState.Rejected, "Windows 拒绝高级启动请求，退出码 " + process.ExitCode + "。可打开系统恢复设置手动操作。");
            }
            catch (Exception ex)
            {
                return new(StartupRequestState.Unconfirmed, "高级启动请求已发出，但结果尚未确认：" + ex.Message
                    + "。工具将退出，不自动重试；若电脑未重启，请手动打开 Windows 恢复设置。");
            }
        }
    }

    internal static async Task<StartupRestartResult> RunAsync(bool confirmed,
        Func<Task<StartupPreparation>> prepare, Func<Task<StartupRequest>> request, CancellationToken token)
    {
        if (!confirmed || token.IsCancellationRequested)
            return new(new(StartupRequestState.Rejected, "已取消，未请求重启，也未执行修复。"), null);
        StartupPreparation? prepared = null;
        var retain = false;
        try
        {
            prepared = await prepare();
            if (!prepared.Ready)
                return new(new(StartupRequestState.Rejected, "交接未完成，未请求重启：\n" + prepared.Message), null);
            if (token.IsCancellationRequested)
                return new(new(StartupRequestState.Rejected, "已取消，未请求重启。已停止的后台功能需按需重新开启。"), null);
            var result = await request();
            retain = result.State != StartupRequestState.Rejected;
            return new(result, retain ? prepared.Lease : null);
        }
        finally { if (!retain) prepared?.Lease.Dispose(); }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
