using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using DeltaNFD.Services;
using DeltaNFD.Services.TweakDb;

namespace DeltaNFD.Inspection;
/// <summary>Read-only inspection shared by the UI and backend. No system mutations.</summary>
public sealed class SystemTweakInspection
{
    private static bool? ParsePreLaunch(string? text) => text?.Trim() switch
    {
        "0" => false,
        "1" => true,
        _ => null
    };
    private const string DeviceGuardPath = @"SYSTEM\CurrentControlSet\Control\DeviceGuard";
    private const string HvciPath = DeviceGuardPath + @"\Scenarios\HypervisorEnforcedCodeIntegrity";
    private const string CredentialGuardPath = DeviceGuardPath + @"\Scenarios\CredentialGuard";
    private const string MemoryManagementPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private const string MousePath = @"Control Panel\Mouse";
    private const string GraphicsDriversPath = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string WSearchServicePath = @"SYSTEM\CurrentControlSet\Services\WSearch";
    private const string ApplicationPreLaunchPath = @"Software\Microsoft\Windows\CurrentVersion\ApplicationPreLaunch";
    private static readonly string[] HyperVFeatures = ["Microsoft-Hyper-V-All", "Microsoft-Hyper-V", "Microsoft-Hyper-V-Hypervisor", "Microsoft-Hyper-V-Services", "Microsoft-Hyper-V-Tools-All", "Microsoft-Hyper-V-Management-Clients", "Microsoft-Hyper-V-Management-PowerShell", "HypervisorPlatform", "VirtualMachinePlatform", ];
    public async Task<bool?> IsHyperVFullyDisabledAsync()
    {
        var launchType = await QueryHypervisorLaunchTypeAsync();
        if (launchType == "unknown")
            return null;
        if (!HyperVToState(launchType, ReadDword(DeviceGuardPath, "EnableVirtualizationBasedSecurity"), ReadDword(HvciPath, "Enabled"), ReadDword(CredentialGuardPath, "Enabled")).IsOptimized)
            return false;
        var features = string.Join(",", HyperVFeatures.Select(f => "'" + f + "'"));
        var script = "$items = @(Get-WindowsOptionalFeature -Online -ErrorAction Stop | Where-Object { $_.FeatureName -in @(" + features + ") } | Select-Object FeatureName,@{Name='State';Expression={$_.State.ToString()}}); ConvertTo-Json -InputObject $items -Compress";
        var(code, output, _) = await RunPowerShellAsync(script, TimeSpan.FromSeconds(30));
        return code == 0 ? AssessHyperVFeatureStates(output) : null;
    }

    internal static bool? AssessHyperVFeatureStates(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(output);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                return null;
            var hasEnabled = false;
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                var state = row.GetProperty("State").GetString();
                if (state is "Enabled" or "EnablePending" or "DisablePending")
                    hasEnabled = true;
                else if (state is not ("Disabled" or "DisabledWithPayloadRemoved"))
                    return null;
            }

            return !hasEnabled;
        }
        catch
        {
            return null;
        }
    }

    public Task<List<TweakStatus>> GetStatusesAsync() => InspectionScheduler.Run(async () =>
    {
        try
        {
            return await ReadStatusesCoreAsync();
        }
        catch (Exception ex)
        {
            return Enum.GetValues<SystemTweak>().Select(t => MakeStatus(t, (false, "读取失败：" + ex.Message), known: false)).ToList();
        }
    });
    private async Task<List<TweakStatus>> ReadStatusesCoreAsync()
    {
        var vbs = ReadDword(DeviceGuardPath, "EnableVirtualizationBasedSecurity");
        var hvci = ReadDword(HvciPath, "Enabled");
        var credentialGuard = ReadDword(CredentialGuardPath, "Enabled");
        // HKCU 兼容值不代表 MMAgent ApplicationPreLaunch；后者通过同一次 Get-MMAgent 查询取得。
        var mmLaunch = ReadDword("HKLM", MemoryManagementPath, "EnableApplicationLaunchPrefetching");
        var prelaunch = ReadDword("HKCU", ApplicationPreLaunchPath, "Enabled");
        var operationApi = ReadDword("HKLM", MemoryManagementPath, "EnableOperationAPI");
        var(compressionOn, pageCombiningOn, hypervisor, applicationPreLaunchOn) = await QueryMmAgentAndHypervisorAsync();
        // 游戏微调包状态
        var mouseSpeed = ReadString("HKCU", MousePath, "MouseSpeed");
        var hags = ReadDword(GraphicsDriversPath, "HwSchMode");
        var wSearch = ReadDword(WSearchServicePath, "Start");
        return[MakeStatus(SystemTweak.HyperVAndVbs, HyperVToState(hypervisor, vbs, hvci, credentialGuard), known: hypervisor != "unknown"), MakeStatus(SystemTweak.MemoryCompression, compressionOn is null ? (false, "状态无法确认") : compressionOn.Value ? (false, "已开启") : (true, "已关闭"), known: compressionOn.HasValue), MakeStatus(SystemTweak.PageCombining, pageCombiningOn is null ? (false, "状态无法确认") : pageCombiningOn.Value ? (false, "已开启") : (true, "已关闭"), known: pageCombiningOn.HasValue), MakeStatus(SystemTweak.Prefetch, PrefetchTrioToState(mmLaunch, prelaunch, operationApi, applicationPreLaunchOn), known: applicationPreLaunchOn.HasValue), // 游戏微调包
        MakeStatus(SystemTweak.MouseAccelerationOff, mouseSpeed == "0" ? (true, "已关闭鼠标加速") : (false, $"未优化（MouseSpeed={mouseSpeed ?? "默认"}）")), MakeStatus(SystemTweak.HardwareGpuScheduling, ValueToState(hags, 2, "未启用（1/未配置）", "已开启（2，重启后生效）")), MakeStatus(SystemTweak.WSearchOff, wSearch == 4 ? (true, "已禁用 Windows Search") : (false, $"未优化（WSearch={ServiceStartText(wSearch)}）")), ];
    }

    private static string ServiceStartText(int? start) => start switch
    {
        2 => "自动",
        3 => "手动",
        4 => "已禁用",
        null => "未配置",
        _ => start.ToString() ?? "",
    };
    private static (bool IsOptimized, string Detail) PrefetchTrioToState(int? mmLaunch, int? prelaunch, int? operationApi, bool? applicationPreLaunchOn)
    {
        bool launchOn = mmLaunch != 0; // Memory Management\EnableApplicationLaunchPrefetching：未配置 = 默认开启
        bool prelaunchOn = prelaunch != 0; // 未配置 = 默认开启
        bool operationOn = operationApi != 0; // 未配置 = 默认开启
        if (!launchOn && !prelaunchOn && !operationOn && applicationPreLaunchOn == false)
        {
            return (true, "预读取关闭配置已设置，ApplicationPreLaunch 当前已关闭；SysMain 未被此项停止或禁用");
        }

        var parts = new List<string>();
        if (applicationPreLaunchOn != false)
            parts.Add(applicationPreLaunchOn is null ? "ApplicationPreLaunch 状态无法确认" : "ApplicationPreLaunch 开启");
        if (launchOn)
        {
            parts.Add("应用启动预读取开启");
        }

        if (prelaunchOn)
        {
            parts.Add("UWP 预启动开启");
        }

        if (operationOn)
        {
            parts.Add("OperationAPI 开启");
        }

        return (false, $"未优化（{string.Join("，", parts)}）");
    }

    private static (bool IsOptimized, string Detail) ValueToState(int? current, int optimized, string defaultText, string optimizedText) => current == optimized ? (true, optimizedText) : (false, $"未优化（{defaultText}）");
    public async Task<string> GetVbsRuntimeSummaryAsync()
    {
        // VirtualizationBasedSecurityStatus: 0=未启用 1=已启用未运行 2=已启用并运行
        // SecurityServicesRunning 包含 2 表示 HVCI 正在运行
        var command = "$dg = Get-CimInstance -Namespace root\\Microsoft\\Windows\\DeviceGuard -ClassName Win32_DeviceGuard -ErrorAction SilentlyContinue; " + "if ($dg) { [int]$dg.VirtualizationBasedSecurityStatus; @($dg.SecurityServicesRunning) -join ',' } else { 'ERR' }";
        var(exitCode, stdout, _) = await RunPowerShellAsync(command, TimeSpan.FromSeconds(20));
        if (exitCode != 0 || stdout is null)
        {
            return "运行时状态查询失败";
        }

        var lines = SplitLines(stdout);
        if (lines.FirstOrDefault() == "ERR")
        {
            return "运行时状态查询失败（系统未提供 Win32_DeviceGuard）";
        }

        var vbsStatus = int.TryParse(lines.ElementAtOrDefault(0), out var status) ? status : -1;
        var hvciRunning = (lines.ElementAtOrDefault(1) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(item => item == "2");
        var vbsText = vbsStatus switch
        {
            2 => "已启用并运行",
            1 => "已启用（未运行）",
            0 => "未启用",
            _ => "未知",
        };
        return $"VBS：{vbsText}；内核隔离（HVCI）：{(hvciRunning ? "运行中" : "未运行")}。" + "（反映本次开机的实际状态，改注册表后需重启刷新）";
    }

    private static async Task<string> QueryHypervisorLaunchTypeAsync()
    {
        var(code, stdout, _) = await RunProcessCaptureAsync("bcdedit.exe", "/enum {current}", TimeSpan.FromSeconds(20));
        if (code != 0 || stdout is null)
        {
            return "unknown";
        }

        foreach (var line in SplitLines(stdout))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("hypervisorlaunchtype", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["hypervisorlaunchtype".Length..].Trim();
                return value.Equals("Off", StringComparison.OrdinalIgnoreCase) ? "off" : "auto";
            }
        }

        // bcdedit 输出中没有该项 = 未设置过 = 默认 auto
        return "auto";
    }

    private static (bool IsOptimized, string Detail) HyperVToState(string hypervisor, int? vbs, int? hvci, int? credentialGuard)
    {
        bool vbsOff = vbs == 0;
        bool hvciOff = hvci is null || hvci == 0;
        bool credentialOff = credentialGuard is null || credentialGuard == 0;
        if (hypervisor == "off" && vbsOff && hvciOff && credentialOff)
        {
            return (true, "已彻底关闭（hypervisor off；VBS / 内核隔离 / Credential Guard 均已关）");
        }

        if (hypervisor == "off" && vbsOff && credentialOff)
        {
            return (true, "已关闭（hypervisor off；VBS / Credential Guard 已关）");
        }

        var parts = new List<string>
        {
            hypervisor switch
            {
                "off" => "hypervisor 已关",
                "unknown" => "hypervisor 状态未知（需管理员）",
                _ => "hypervisor 自启动",
            },
            vbs switch
            {
                null => "VBS 未配置",
                0 => "VBS 已关",
                _ => "VBS 已开启",
            },
        };
        if (!hvciOff)
        {
            parts.Add("内核隔离开启");
        }

        if (!credentialOff)
        {
            parts.Add("Credential Guard 开启");
        }

        return (false, $"未优化（{string.Join("，", parts)}）");
    }

    private static int? ReadDword(string keyPath, string valueName) => ReadDword("HKLM", keyPath, valueName);
    private static int? ReadDword(string hive, string keyPath, string valueName)
    {
        using var key = OpenRead(hive, keyPath);
        return key?.GetValue(valueName)is int value ? value : null;
    }

    private static string? ReadString(string hive, string keyPath, string valueName)
    {
        using var key = OpenRead(hive, keyPath);
        return key?.GetValue(valueName) as string;
    }

    private static RegistryKey? OpenRead(string hive, string keyPath) => hive switch
    {
        "HKCU" => Registry.CurrentUser.OpenSubKey(keyPath),
        _ => Registry.LocalMachine.OpenSubKey(keyPath),
    };
    private static TweakStatus MakeStatus(SystemTweak tweak, (bool IsOptimized, string Detail) state, bool known = true)
    {
        var(displayName, description) = Describe(tweak);
        return new TweakStatus
        {
            Tweak = tweak,
            DisplayName = displayName,
            Description = description,
            IsOptimized = state.IsOptimized,
            StateKnown = known,
            Detail = state.Detail,
        };
    }

    private static (string DisplayName, string Description) Describe(SystemTweak tweak) => tweak switch
    {
        SystemTweak.HyperVAndVbs => ("关闭 Hyper-V / VBS（含内核隔离）", "等效 HyperV-off：关闭 hypervisor 启动、禁用 Hyper-V 与虚拟机平台功能、关闭内核隔离与 Credential Guard。" + "重启生效。注意：重启后 WSL2、虚拟机、Windows 沙盒将不可用，系统安全性降低"),
        SystemTweak.MemoryCompression => ("关闭内存压缩", "关闭 MemCompression 进程，降低 CPU 占用；可用内存量会略微减少"),
        SystemTweak.PageCombining => ("关闭分页合并", "关闭内存页合并，减少后台 CPU 活动"),
        SystemTweak.Prefetch => ("禁用预读取（Prefetch / PreLaunch / OperationAPI）", "关闭应用预读取、MMAgent ApplicationPreLaunch 与 OperationAPI，并保留用户级预启动兼容设置。" + "不停止、不禁用 SysMain；Windows Search 为独立开关。需重启后重新检测"),
        // ---- 游戏微调包 ----
        SystemTweak.MouseAccelerationOff => ("关闭鼠标加速", "关闭指针加速曲线（MouseSpeed/Threshold=0），瞄准移动 1:1 线性；FPS 玩家推荐，立即生效"),
        SystemTweak.HardwareGpuScheduling => ("HAGS 硬件加速 GPU 调度", "开启 HwSchMode=2，由显卡直接管理显存调度，可小幅降低延迟；需 RTX/GTX16 系或较新显卡支持，重启生效"),
        SystemTweak.WSearchOff => ("禁用 Windows Search 服务", "关闭搜索索引，显著减少磁盘与 CPU 后台占用；开始菜单/文件搜索会变慢。" + "注意：不动 SysMain——SysMain 承载内存压缩（MemCompression），禁用它会把内存压缩一起关掉"),
        _ => ("未知", ""),
    };
    private static async Task<(bool? CompressionOn, bool? PageCombiningOn, string Hypervisor, bool? ApplicationPreLaunchOn)> QueryMmAgentAndHypervisorAsync()
    {
        var mmTask = QueryMmAgentAsync();
        var hvTask = QueryHypervisorLaunchTypeAsync();
        await Task.WhenAll(mmTask, hvTask);
        var(compressionOn, pageCombiningOn, applicationPreLaunchOn) = mmTask.Result;
        return (compressionOn, pageCombiningOn, hvTask.Result, applicationPreLaunchOn);
    }

    private static async Task<(bool? CompressionOn, bool? PageCombiningOn, bool? ApplicationPreLaunchOn)> QueryMmAgentAsync()
    {
        var command = "$m = Get-MMAgent -ErrorAction Stop; [int]$m.MemoryCompression; [int]$m.PageCombining; if ($m.ApplicationPreLaunch -is [bool]) { [int]$m.ApplicationPreLaunch } else { 'UNKNOWN' }";
        var(exitCode, stdout, _) = await RunPowerShellAsync(command, TimeSpan.FromSeconds(20));
        if (exitCode != 0 || stdout is null)
        {
            // 查询失败时按「未优化」处理，避免误导用户
            return (null, null, null);
        }

        var lines = SplitLines(stdout);
        return (LineOn(lines.ElementAtOrDefault(0)), LineOn(lines.ElementAtOrDefault(1)), ParsePreLaunch(lines.ElementAtOrDefault(2)));
        static bool? LineOn(string? line) => line?.Trim() switch
        {
            "0" => false,
            "1" => true,
            _ => null
        };
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessCaptureAsync(string fileName, string arguments, TimeSpan timeout)
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

    private static async Task<(int ExitCode, string? StdOut, string? StdErr)> RunPowerShellAsync(string command, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " + $"\"[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; {command}\"",
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

            return (-1, null, "PowerShell 执行超时");
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string[] SplitLines(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private const string PrefetchPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters";
    private const string ServicesRoot = @"SYSTEM\CurrentControlSet\Services";
    private static readonly (string KeyPath, string ValueName, string Label)[] DeviceGuardValues = [(DeviceGuardPath, "EnableVirtualizationBasedSecurity", "VBS（基于虚拟化的安全）"), (HvciPath, "Enabled", "内核隔离（内存完整性 HVCI）"), (CredentialGuardPath, "Enabled", "Credential Guard"), ];
    private readonly TweakBackupStore _backups = TweakBackupStore.Default;
    public IReadOnlyList<SystemTweak> GetBackedUpTweaks()
    {
        var entries = _backups.GetAllStrict();
        bool Has(string hive, string key, string value) => entries.Any(e => e.Id == TweakBackupStore.MakeId(hive, key, value));
        var result = new List<SystemTweak>();
        if (Has("State", "HyperV", "LaunchType") || Has("State", "HyperV", "FeatureBackupProtocol") || HyperVFeatures.Any(f => Has("State", "HyperVFeatures", f)) || Has("HKLM", ServicesRoot + @"\HvHost", "Start") || Has("HKLM", ServicesRoot + @"\vmms", "Start") || DeviceGuardValues.Any(v => Has("HKLM", v.KeyPath, v.ValueName)))
            result.Add(SystemTweak.HyperVAndVbs);
        if (Has("State", "MMAgent", "MemoryCompression"))
            result.Add(SystemTweak.MemoryCompression);
        if (Has("State", "MMAgent", "PageCombining"))
            result.Add(SystemTweak.PageCombining);
        if (Has("HKLM", MemoryManagementPath, "EnableApplicationLaunchPrefetching") || Has("HKLM", MemoryManagementPath, "EnableOperationAPI") || Has("HKLM", PrefetchPath, "EnablePrefetcher") || Has("HKCU", ApplicationPreLaunchPath, "Enabled") || Has("State", "MMAgent", "ApplicationPreLaunch"))
            result.Add(SystemTweak.Prefetch);
        if (Has("HKCU", MousePath, "MouseSpeed"))
            result.Add(SystemTweak.MouseAccelerationOff);
        if (Has("HKLM", GraphicsDriversPath, "HwSchMode"))
            result.Add(SystemTweak.HardwareGpuScheduling);
        if (Has("HKLM", WSearchServicePath, "Start"))
            result.Add(SystemTweak.WSearchOff);
        return result;
    }
}
