using RegSpec = DeltaNFD.Inspection.AdvancedTweakInspection.RegSpec;
using SvcSpec = DeltaNFD.Inspection.AdvancedTweakInspection.SvcSpec;
using TaskSpec = DeltaNFD.Inspection.AdvancedTweakInspection.TaskSpec;
using GroupDef = DeltaNFD.Inspection.AdvancedTweakInspection.GroupDef;
using System.Diagnostics;
using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>高级（高危）优化的真实实现，分组目录驱动，全部可还原。</summary>
public sealed class AdvancedTweakService : IAdvancedTweakService
{
    // ---------------- 目录定义 ----------------

    

    

    

    

    private const string Policies = @"SOFTWARE\Policies\Microsoft";
    private const string Nt = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string CurVer = @"SOFTWARE\Microsoft\Windows\CurrentVersion";

    private const string TaskkillDebugger = @"%windir%\System32\taskkill.exe";

    private static RegSpec D(string hive, string keyPath, string valueName, int value) =>
        new(hive, keyPath, valueName, value, RegistryValueKind.DWord);

    private static RegSpec S(string hive, string keyPath, string valueName, string value) =>
        new(hive, keyPath, valueName, value, RegistryValueKind.String);

    private static IReadOnlyList<GroupDef> Groups => DeltaNFD.Inspection.AdvancedTweakInspection.Definitions;

    private readonly TweakBackupStore _backups;

    public AdvancedTweakService() : this(TweakBackupStore.Default)
    {
    }

    public AdvancedTweakService(TweakBackupStore backups)
    {
        _backups = backups;
    }

    // ---------------- 接口实现 ----------------

    public Task<List<AdvancedTweakGroup>> GetGroupsAsync() => new DeltaNFD.Inspection.AdvancedTweakInspection().GetGroupsAsync();

    public async Task<OperationResult> ApplyAsync(string groupId)
    {
        if (!ElevationHelper.IsElevated)
        {
            return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        }

        var group = FindGroup(groupId);
        if (group is null)
        {
            return OperationResult.Fail("未知的优化分组。");
        }

        Log.Info($"安全隐私：应用分组「{group.Name}」");

        var problems = new List<string>();

        // 1. 注册表（备份原值 → 写入）
        foreach (var reg in group.Regs)
        {
            try
            {
                BackupAndWriteReg(reg);
            }
            catch (Exception ex)
            {
                problems.Add($"{reg.KeyPath}\\{reg.ValueName}: {ex.Message}");
            }
        }

        // 2. 服务（备份 Start → 写 4 → 尽力停止）
        foreach (var svc in group.Services)
        {
            try
            {
                var keyPath = $@"SYSTEM\CurrentControlSet\Services\{svc.Name}";
                using (var probe = Registry.LocalMachine.OpenSubKey(keyPath))
                {
                    if (probe is null)
                    {
                        continue; // 服务不存在（如 uhssvc）：跳过
                    }
                }

                BackupServiceStart(svc.Name, keyPath);
                using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
                key?.SetValue("Start", 4, RegistryValueKind.DWord);
                await RunCaptureAsync("sc.exe", $"stop {svc.Name}", TimeSpan.FromSeconds(15));
            }
            catch (Exception ex)
            {
                problems.Add($"服务 {svc.Name}: {ex.Message}");
            }
        }

        // 3. 计划任务（schtasks 禁用，best-effort）
        var taskFailed = 0;
        foreach (var task in group.Tasks)
        {
            var (code, _, _) = await RunCaptureAsync(
                "schtasks.exe", $"/Change /TN \"{task.Path}\" /DISABLE", TimeSpan.FromSeconds(20));
            if (code != 0)
            {
                taskFailed++;
            }
        }

        // 4. Defender 实时防护特殊步骤（Set-MpPreference，可能被篡改保护拦截）
        if (group.Id == "defender_rt")
        {
            var (mpCode, mpOut, _) = await RunCaptureAsync(
                "powershell.exe",
                "-NoProfile -NonInteractive -Command \"try { Set-MpPreference -DisableRealtimeMonitoring $true -ErrorAction Stop; Write-Output OK } catch { Write-Output $_.Exception.Message; exit 1 }\"",
                TimeSpan.FromSeconds(40));
            if (mpCode != 0)
            {
                problems.Add($"实时监控未能关闭（{FirstLine(mpOut)}）——请在 Windows 安全中心手动关闭「篡改防护」后重试");
            }
        }

        if (problems.Count > 0)
        {
            return OperationResult.Fail($"应用「{group.Name}」部分失败：{string.Join("；", problems.Take(3))}");
        }

        var taskNote = taskFailed > 0 ? $"（{taskFailed} 个计划任务因权限跳过，通常为 TrustedInstaller 保护项）" : "";
        return OperationResult.Ok($"已应用「{group.Name}」{taskNote}。");
    }

    public async Task<OperationResult> RevertAsync(string groupId)
    {
        if (!ElevationHelper.IsElevated)
        {
            return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        }

        var group = FindGroup(groupId);
        if (group is null)
        {
            return OperationResult.Fail("未知的优化分组。");
        }

        Log.Info($"安全隐私：还原分组「{group.Name}」");

        var restored = 0;

        // 1. 注册表按备份还原
        foreach (var reg in group.Regs)
        {
            var backup = _backups.Get(reg.Hive, reg.KeyPath, reg.ValueName);
            if (backup is null)
            {
                continue;
            }

            try
            {
                using var key = OpenWritableOrCreate(reg.Hive, reg.KeyPath);
                if (backup.ValueKind == RegistryValueKind.None)
                {
                    key.DeleteValue(reg.ValueName, throwOnMissingValue: false);
                }
                else
                {
                    key.SetValue(reg.ValueName, backup.Data, backup.ValueKind);
                }

                _backups.Remove(reg.Hive, reg.KeyPath, reg.ValueName);
                restored++;
            }
            catch
            {
                // 单项失败继续
            }
        }

        // 2. 服务 Start 还原
        foreach (var svc in group.Services)
        {
            var keyPath = $@"SYSTEM\CurrentControlSet\Services\{svc.Name}";
            var backup = _backups.Get("HKLM", keyPath, "Start");
            if (backup is null)
            {
                continue;
            }

            try
            {
                if (backup.ValueKind == RegistryValueKind.None)
                {
                    using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
                    key?.DeleteValue("Start", throwOnMissingValue: false);
                }
                else if (int.TryParse(backup.Data, out var start))
                {
                    using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
                    key?.SetValue("Start", start, RegistryValueKind.DWord);
                }

                _backups.Remove("HKLM", keyPath, "Start");
                restored++;
            }
            catch
            {
                // 单项失败继续
            }
        }

        // 3. 重新启用计划任务（best-effort）
        foreach (var task in group.Tasks)
        {
            await RunCaptureAsync("schtasks.exe", $"/Change /TN \"{task.Path}\" /ENABLE", TimeSpan.FromSeconds(20));
        }

        // 4. Defender 实时防护特殊步骤（重新开启实时监控）
        if (group.Id == "defender_rt")
        {
            await RunCaptureAsync(
                "powershell.exe",
                "-NoProfile -NonInteractive -Command \"try { Set-MpPreference -DisableRealtimeMonitoring $false -ErrorAction Stop; Write-Output OK } catch { Write-Output $_.Exception.Message; exit 1 }\"",
                TimeSpan.FromSeconds(40));
        }

        return restored > 0
            ? OperationResult.Ok($"已还原「{group.Name}」（{restored} 项注册表/服务，计划任务已重新启用）。")
            : OperationResult.Fail($"「{group.Name}」没有本工具的备份记录（可能未用本工具应用过）。");
    }

    // ---------------- 状态判定 ----------------

    private bool IsGroupApplied(GroupDef group, Dictionary<string, bool>? taskStates, out string detail)
    {
        var notes = new List<string>();

        // 计划任务状态整体查询失败且本组依赖任务判定时，不能凭空判定「已应用」
        if (taskStates is null && group.Tasks.Length > 0)
        {
            detail = "计划任务状态查询失败，无法判定";
            return false;
        }

        foreach (var reg in group.Regs)
        {
            var current = ReadRegValue(reg.Hive, reg.KeyPath, reg.ValueName);
            if (!ValueEquals(current, reg.Value, reg.Kind))
            {
                detail = "";
                return false;
            }
        }

        foreach (var svc in group.Services)
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{svc.Name}");
            if (key is null)
            {
                notes.Add($"{svc.Name} 不存在");
                continue;
            }

            if (key.GetValue("Start") is not int start || start != 4)
            {
                detail = "";
                return false;
            }
        }

        var failedTasks = 0;
        foreach (var task in group.Tasks)
        {
            if (taskStates!.TryGetValue(task.Path, out var disabled))
            {
                if (!disabled)
                {
                    detail = "";
                    return false;
                }
            }
            else
            {
                failedTasks++;
            }
        }

        detail = failedTasks > 0 ? $"{failedTasks} 个计划任务状态未知" : string.Join("；", notes);
        return true;
    }

    /// <summary>查询全部相关计划任务状态（True=已禁用 / False=启用中；任务不存在不写入）。
    /// PowerShell 查询整体失败返回 null（区别于「查询成功但无任务」的空字典）。</summary>
    private static async Task<Dictionary<string, bool>?> QueryTaskStatesAsync()
    {
        var result = new Dictionary<string, bool>();
        var allTasks = Groups.SelectMany(g => g.Tasks).Select(t => t.Path).Distinct().ToList();
        if (allTasks.Count == 0)
        {
            return result;
        }

        var pathArray = string.Join(",", allTasks.Select(p => $"'{p}'"));
        var command =
            "$paths = @(" + pathArray + "); " +
            "foreach ($p in $paths) { " +
            "$dir = if ($p.LastIndexOf('\\') -gt 0) { $p.Substring(0, $p.LastIndexOf('\\') + 1) } else { '\\' }; " +
            "$name = $p.Split('/')[-1].Split('\\')[-1]; " +
            "$t = Get-ScheduledTask -TaskPath $dir -TaskName $name -ErrorAction SilentlyContinue; " +
            "if ($t) { Write-Output ([string]($t.State -eq 'Disabled')) } else { Write-Output 'NA' } }";

        var (code, stdout, _) = await RunCaptureAsync(
            "powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " +
            $"\"{command.Replace("\"", "`\"")}\"",
            TimeSpan.FromSeconds(40));
        if (code != 0 || stdout is null)
        {
            return null; // 查询失败：任务状态按未知处理（不误判为已应用）
        }

        var lines = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < allTasks.Count && i < lines.Length; i++)
        {
            if (lines[i] == "True")
            {
                result[allTasks[i]] = true;
            }
            else if (lines[i] == "False")
            {
                result[allTasks[i]] = false;
            }
        }

        return result;
    }

    // ---------------- 注册表 / 服务工具 ----------------

    private static GroupDef? FindGroup(string id) => Groups.FirstOrDefault(g => g.Id == id);

    private void BackupAndWriteReg(RegSpec reg)
    {
        var current = ReadRegValue(reg.Hive, reg.KeyPath, reg.ValueName);
        var (kind, data) = current switch
        {
            null => (RegistryValueKind.None, ""),
            int dword => (RegistryValueKind.DWord, dword.ToString()),
            string str => (RegistryValueKind.String, str),
            _ => (RegistryValueKind.None, ""),
        };

        _backups.Save(new RegistryValueBackup
        {
            Hive = reg.Hive,
            Id = TweakBackupStore.MakeId(reg.Hive, reg.KeyPath, reg.ValueName),
            KeyPath = reg.KeyPath,
            ValueName = reg.ValueName,
            ValueKind = kind,
            Data = data,
            CreatedAt = DateTimeOffset.Now,
        });

        using var key = OpenWritableOrCreate(reg.Hive, reg.KeyPath);
        key.SetValue(reg.ValueName, reg.Value, reg.Kind);
    }

    private void BackupServiceStart(string serviceName, string keyPath)
    {
        using var key = Registry.LocalMachine.OpenSubKey(keyPath);
        var current = key?.GetValue("Start");
        _backups.Save(new RegistryValueBackup
        {
            Hive = "HKLM",
            Id = TweakBackupStore.MakeId("HKLM", keyPath, "Start"),
            KeyPath = keyPath,
            ValueName = "Start",
            ValueKind = current is int ? RegistryValueKind.DWord : RegistryValueKind.None,
            Data = current is int dword ? dword.ToString() : "",
            CreatedAt = DateTimeOffset.Now,
        });
    }

    private static object? ReadRegValue(string hive, string keyPath, string valueName)
    {
        using var key = hive == "HKCU"
            ? Registry.CurrentUser.OpenSubKey(keyPath)
            : Registry.LocalMachine.OpenSubKey(keyPath);
        return key?.GetValue(valueName);
    }

    private static string FirstLine(string text)
    {
        var trimmed = text.Trim();
        var lineBreak = trimmed.IndexOfAny(['\r', '\n']);
        return lineBreak > 0 ? trimmed[..lineBreak] : trimmed;
    }

    private static bool ValueEquals(object? current, object target, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => current is int i && i == (int)target,
        RegistryValueKind.String => current is string s &&
            string.Equals(s, (string)target, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static RegistryKey OpenWritableOrCreate(string hive, string keyPath) => hive == "HKCU"
        ? Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
        : Registry.LocalMachine.CreateSubKey(keyPath, writable: true);

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
}
