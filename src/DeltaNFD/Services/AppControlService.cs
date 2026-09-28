using System.ComponentModel;
using System.Diagnostics;

namespace DeltaNFD.Services;

/// <summary>应用级控制：开机自动启动、关闭后最小化到托盘。</summary>
public interface IAppControlService : INotifyPropertyChanged
{
    /// <summary>关闭窗口后是否驻留托盘（即时生效，持久化）。</summary>
    bool CloseToTrayEnabled { get; }

    /// <summary>开机自动启动当前是否已启用（计划任务存在 = 已启用）。</summary>
    Task<bool> IsAutoStartEnabledAsync();

    /// <summary>开启/关闭开机自动启动（计划任务方式，登录自启且不弹 UAC）。</summary>
    Task<OperationResult> SetAutoStartAsync(bool enable);

    /// <summary>设置关闭后最小化到托盘（持久化，UI 层订阅 PropertyChanged 即时应用）。</summary>
    void SetCloseToTray(bool enable);
}

/// <summary>应用级控制的真实实现。</summary>
public sealed class AppControlService : IAppControlService
{
    private const string AutoStartTaskName = "DeltaNFD_AutoStart";
    private const string LegacyAutoStartTaskName = "DeltaOptimizer_AutoStart";

    private bool _closeToTrayEnabled;

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppControlService()
    {
        _closeToTrayEnabled = AppSettingsStore.Read().CloseToTrayEnabled;
    }

    public bool CloseToTrayEnabled
    {
        get => _closeToTrayEnabled;
        private set
        {
            if (_closeToTrayEnabled == value)
            {
                return;
            }

            _closeToTrayEnabled = value;
            AppSettingsStore.Update(s => s.CloseToTrayEnabled = value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CloseToTrayEnabled)));
        }
    }

    public void SetCloseToTray(bool enable) => CloseToTrayEnabled = enable;

    public async Task<bool> IsAutoStartEnabledAsync()
    {
        if (await TaskExistsAsync(AutoStartTaskName))
            return true;

        if (!await TaskExistsAsync(LegacyAutoStartTaskName))
            return false;

        var migration = await SetAutoStartAsync(enable: true);
        if (!migration.Success)
            Log.Warn($"旧版自启动任务迁移失败：{migration.Message}");
        return true;
    }

    public async Task<OperationResult> SetAutoStartAsync(bool enable)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return OperationResult.Fail("无法确定本程序路径。");
        }

        if (enable)
        {
            var (code, stdout, stderr) = await RunCaptureAsync(
                "schtasks.exe",
                $"/Create /TN {AutoStartTaskName} /TR \"\\\"{exePath}\\\"\" /SC ONLOGON /RL HIGHEST /F",
                TimeSpan.FromSeconds(30));

            if (code != 0)
            {
                var reason = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                return OperationResult.Fail($"创建开机自启动任务失败：{FirstLine(reason)}");
            }

            if (!await DeleteTaskIfPresentAsync(LegacyAutoStartTaskName))
            {
                await DeleteTaskIfPresentAsync(AutoStartTaskName);
                return OperationResult.Fail("已创建新自启动任务，但无法移除旧版任务；为避免重复启动，已回滚新任务。");
            }
            return OperationResult.Ok("已开启开机自动启动（登录后自动运行，不弹 UAC）。");
        }

        var legacyRemoved = await DeleteTaskIfPresentAsync(LegacyAutoStartTaskName);
        var (delCode, delStdout, delStderr) = await RunCaptureAsync(
            "schtasks.exe",
            $"/Delete /TN {AutoStartTaskName} /F",
            TimeSpan.FromSeconds(30));

        if (delCode == 0)
        {
            return legacyRemoved
                ? OperationResult.Ok("已关闭开机自动启动。")
                : OperationResult.Fail("新任务已关闭，但旧版自启动任务仍存在，请以管理员权限重试。");
        }

        // 删除失败 ≠ 任务不存在：复核任务是否仍在，仍在则如实报失败（否则开关状态与系统脱节）
        if (await TaskExistsAsync(AutoStartTaskName) || await TaskExistsAsync(LegacyAutoStartTaskName))
        {
            var reason = string.IsNullOrWhiteSpace(delStderr) ? delStdout : delStderr;
            return OperationResult.Fail($"关闭开机自启动失败：{FirstLine(reason)}");
        }

        return legacyRemoved
            ? OperationResult.Ok("开机自动启动本就未开启。")
            : OperationResult.Fail("旧版自启动任务仍存在，请以管理员权限重试。");
    }

    private static async Task<bool> TaskExistsAsync(string taskName)
    {
        var (code, _, _) = await RunCaptureAsync(
            "schtasks.exe", $"/Query /TN {taskName}", TimeSpan.FromSeconds(15));
        return code == 0;
    }

    private static async Task<bool> DeleteTaskIfPresentAsync(string taskName)
    {
        if (!await TaskExistsAsync(taskName))
            return true;

        var (code, stdout, stderr) = await RunCaptureAsync(
            "schtasks.exe", $"/Delete /TN {taskName} /F", TimeSpan.FromSeconds(30));
        if (code != 0 && await TaskExistsAsync(taskName))
        {
            Log.Warn($"旧版自启动任务删除失败：{taskName}；{FirstLine(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr)}");
            return false;
        }

        return true;
    }

    // ---------------- 进程调用 ----------------

    private static async Task<(int Code, string StdOut, string StdErr)> RunCaptureAsync(
        string fileName, string arguments, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            return (-1, "", $"无法启动 {fileName}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 进程可能已自行退出
            }

            return (-1, "", "执行超时");
        }

            return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string FirstLine(string text)
    {
        var trimmed = text.Trim();
        var lineBreak = trimmed.IndexOfAny(['\r', '\n']);
        return lineBreak > 0 ? trimmed[..lineBreak] : trimmed;
    }
}
