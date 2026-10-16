using System.Diagnostics;

namespace DeltaNFD.Services;

/// <summary>
/// 显卡设备重载执行（T4）：禁用再启用指定 PnP 设备实例，使伪装/还原立即生效。
/// 纯执行实现，位于执行库；黑屏兜底（延迟自动恢复进程）与玩家亮屏确认仍由前端编排决定。
/// 算法与频次与迁移前的前端实现逐字一致。
/// </summary>
public static class GpuDeviceReloadService
{
    public static async Task<OperationResult> ReloadAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return OperationResult.Fail("无法解析设备实例 ID。");
        }

        var command =
            "try { Disable-PnpDevice -InstanceId '" + instanceId + "' -Confirm:$false -ErrorAction Stop; " +
            "Enable-PnpDevice -InstanceId '" + instanceId + "' -Confirm:$false -ErrorAction Stop; exit 0 } " +
            "catch { $_.Exception.Message; exit 1 }";

        var (exitCode, stdout, stderr) = await RunPowerShellAsync(command, TimeSpan.FromMinutes(2), cancellationToken);
        if (exitCode != 0)
        {
            var reason = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            return OperationResult.Fail(
                $"重载显卡失败：{(string.IsNullOrWhiteSpace(reason) ? "PowerShell 执行出错" : reason.Trim())}。");
        }

        return OperationResult.Ok("显卡已禁用并重新启用。");
    }

    private static async Task<(int ExitCode, string? StdOut, string? StdErr)> RunPowerShellAsync(
        string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments =
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " +
                $"\"[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; {command}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            return (-1, null, "无法启动 powershell.exe");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-1, null, "PowerShell 执行超时");
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
