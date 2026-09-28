using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DeltaNFD.Services;

public partial class SystemTweakService
{
    // SystemParametersInfo（鼠标加速立即生效）
    private const uint SpiSetMouse = 0x0004;
    private const uint SpifUpdateIniFile = 0x01;
    private const uint SpifSendChange = 0x02;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, int[] pvParam, uint fWinIni);
}

/// <summary>
/// 系统精简优化的真实实现：
/// - Hyper-V / VBS → 等效 HyperV-off 类工具的完整流程：
///   1) bcdedit 关闭 hypervisor 启动
///   2) DISM 依次禁用 Hyper-V / 虚拟机平台相关功能（best-effort，不存在即跳过）
///   3) HvHost / vmms 服务停止并改为手动启动
///   4) DeviceGuard 注册表关闭 VBS / 内核隔离(HVCI) / Credential Guard
/// - 内存压缩、分页合并 → Windows 官方 MMAgent 模块（powershell.exe 调 Disable/Enable-MMAgent）
/// - 预读取 → 注册表直写
/// 所有修改前都会把原始值写入 <see cref="TweakBackupStore"/>，可用 RestoreAsync 恢复。
/// </summary>
public sealed partial class SystemTweakService : ISystemTweakService
{
    private const string DeviceGuardPath = @"SYSTEM\CurrentControlSet\Control\DeviceGuard";
    private const string HvciPath = DeviceGuardPath + @"\Scenarios\HypervisorEnforcedCodeIntegrity";
    private const string CredentialGuardPath = DeviceGuardPath + @"\Scenarios\CredentialGuard";
    private const string PrefetchPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters";

    /// <summary>MMAgent 的注册表落点：Disable/Enable-MMAgent 写的是本键下的
    /// EnableApplicationLaunchPrefetching / EnableOperationAPI（不是 PrefetchParameters\EnablePrefetcher）。</summary>
    private const string MemoryManagementPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";

    // ---- 游戏微调包键路径（社区同款） ----
    private const string MultimediaProfilePath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string GameBarPolicyPath = @"SOFTWARE\Policies\Microsoft\GameBar";
    private const string GameDvrPolicyPath = @"SOFTWARE\Policies\Microsoft\Windows\GameDVR";
    private const string MousePath = @"Control Panel\Mouse";
    private const string GraphicsDriversPath = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string ControlPath = @"SYSTEM\CurrentControlSet\Control";
    private const string PowerThrottlingPath = @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling";
    private const string WSearchServicePath = @"SYSTEM\CurrentControlSet\Services\WSearch";
    private const string ApplicationPreLaunchPath = @"Software\Microsoft\Windows\CurrentVersion\ApplicationPreLaunch";

    // ---- 系统底层调优套件键路径 ----
    private const string KernelSessionPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";
    private const string PriorityControlPath = @"SYSTEM\CurrentControlSet\Control\PriorityControl";
    private const string StornvmeDevicePath = @"SYSTEM\CurrentControlSet\Services\stornvme\Parameters\Device";
    private const string StorageControlPath = @"SYSTEM\CurrentControlSet\Control\Storage";
    private const string NtfsPath = @"SYSTEM\CurrentControlSet\Control\FileSystem";
    private const string ServicesRoot = @"SYSTEM\CurrentControlSet\Services";
    private const string LsaPath = @"SYSTEM\CurrentControlSet\Control\Lsa";
    private const string KernelShadowStacksPath = DeviceGuardPath + @"\Scenarios\KernelShadowStacks";
    private const string NetworkClassGuid = @"{4d36e972-e325-11ce-bfc1-08002be10318}";

    /// <summary>DISM 禁用的功能清单（与 HyperV-off 等效脚本一致；不存在或已禁用时 best-effort 跳过）。</summary>
    private static readonly string[] HyperVFeatures =
    [
        "Microsoft-Hyper-V-All",
        "Microsoft-Hyper-V",
        "Microsoft-Hyper-V-Hypervisor",
        "Microsoft-Hyper-V-Services",
        "Microsoft-Hyper-V-Tools-All",
        "Microsoft-Hyper-V-Management-Clients",
        "Microsoft-Hyper-V-Management-PowerShell",
        "HypervisorPlatform",
        "VirtualMachinePlatform",
    ];

    private static readonly (string KeyPath, string ValueName, string Label)[] DeviceGuardValues =
    [
        (DeviceGuardPath, "EnableVirtualizationBasedSecurity", "VBS（基于虚拟化的安全）"),
        (HvciPath, "Enabled", "内核隔离（内存完整性 HVCI）"),
        (CredentialGuardPath, "Enabled", "Credential Guard"),
    ];

    private readonly TweakBackupStore _backups;

    public SystemTweakService() : this(TweakBackupStore.Default)
    {
    }

    public SystemTweakService(TweakBackupStore backups)
    {
        _backups = backups;
    }

    public IReadOnlyList<SystemTweak> GetBackedUpTweaks()
    {
        var entries = _backups.GetAll();
        bool Has(string hive, string key, string value) => entries.Any(e => e.Id == TweakBackupStore.MakeId(hive, key, value));
        var result = new List<SystemTweak>();
        if (Has("State", "HyperV", "LaunchType") ||
            HyperVFeatures.Any(f => Has("State", "HyperVFeatures", f)) ||
            Has("HKLM", ServicesRoot + @"\HvHost", "Start") ||
            Has("HKLM", ServicesRoot + @"\vmms", "Start") ||
            DeviceGuardValues.Any(v => Has("HKLM", v.KeyPath, v.ValueName)))
            result.Add(SystemTweak.HyperVAndVbs);
        if (Has("State", "MMAgent", "MemoryCompression")) result.Add(SystemTweak.MemoryCompression);
        if (Has("State", "MMAgent", "PageCombining")) result.Add(SystemTweak.PageCombining);
        if (Has("HKLM", MemoryManagementPath, "EnableApplicationLaunchPrefetching") ||
            Has("HKLM", MemoryManagementPath, "EnableOperationAPI") ||
            Has("HKLM", PrefetchPath, "EnablePrefetcher") || Has("HKCU", ApplicationPreLaunchPath, "Enabled"))
            result.Add(SystemTweak.Prefetch);
        if (Has("HKCU", MousePath, "MouseSpeed")) result.Add(SystemTweak.MouseAccelerationOff);
        if (Has("HKLM", GraphicsDriversPath, "HwSchMode")) result.Add(SystemTweak.HardwareGpuScheduling);
        if (Has("HKLM", WSearchServicePath, "Start")) result.Add(SystemTweak.WSearchOff);
        return result;
    }

    // ---------------- 状态查询 ----------------

    public async Task<List<TweakStatus>> GetStatusesAsync()
    {
        var vbs = ReadDword(DeviceGuardPath, "EnableVirtualizationBasedSecurity");
        var hvci = ReadDword(HvciPath, "Enabled");
        var credentialGuard = ReadDword(CredentialGuardPath, "Enabled");
        // 预读取三件套状态（与实际写入路径一致：MMAgent 两项 + HKCU UWP 预启动）
        var mmLaunch = ReadDword("HKLM", MemoryManagementPath, "EnableApplicationLaunchPrefetching");
        var prelaunch = ReadDword("HKCU", ApplicationPreLaunchPath, "Enabled");
        var operationApi = ReadDword("HKLM", MemoryManagementPath, "EnableOperationAPI");
        var (compressionOn, pageCombiningOn, hypervisor) = await QueryMmAgentAndHypervisorAsync();

        // 游戏微调包状态
        var mouseSpeed = ReadString("HKCU", MousePath, "MouseSpeed");
        var hags = ReadDword(GraphicsDriversPath, "HwSchMode");
        var wSearch = ReadDword(WSearchServicePath, "Start");

        return
        [
            MakeStatus(SystemTweak.HyperVAndVbs, HyperVToState(hypervisor, vbs, hvci, credentialGuard)),
            MakeStatus(SystemTweak.MemoryCompression, compressionOn ? (false, "已开启") : (true, "已关闭")),
            MakeStatus(SystemTweak.PageCombining, pageCombiningOn ? (false, "已开启") : (true, "已关闭")),
            MakeStatus(SystemTweak.Prefetch, PrefetchTrioToState(mmLaunch, prelaunch, operationApi)),

            // 游戏微调包
            MakeStatus(SystemTweak.MouseAccelerationOff,
                mouseSpeed == "0"
                    ? (true, "已关闭鼠标加速")
                    : (false, $"未优化（MouseSpeed={mouseSpeed ?? "默认"}）")),
            MakeStatus(SystemTweak.HardwareGpuScheduling, ValueToState(hags, 2, "未启用（1/未配置）", "已开启（2，重启后生效）")),
            MakeStatus(SystemTweak.WSearchOff,
                wSearch == 4
                    ? (true, "已禁用 Windows Search")
                    : (false, $"未优化（WSearch={ServiceStartText(wSearch)}）")),
        ];
    }

    private static string ServiceStartText(int? start) => start switch
    {
        2 => "自动",
        3 => "手动",
        4 => "已禁用",
        null => "未配置",
        _ => start.ToString() ?? "",
    };

    /// <summary>预读取三件套的状态：应用启动预读取（MMAgent ApplicationLaunchPrefetching）/ UWP 预启动（HKCU）/ OperationAPI。
    /// 三项均读实际写入的键：未配置 = 系统默认开启。</summary>
    private static (bool IsOptimized, string Detail) PrefetchTrioToState(int? mmLaunch, int? prelaunch, int? operationApi)
    {
        bool launchOn = mmLaunch != 0;   // Memory Management\EnableApplicationLaunchPrefetching：未配置 = 默认开启
        bool prelaunchOn = prelaunch != 0; // 未配置 = 默认开启
        bool operationOn = operationApi != 0; // 未配置 = 默认开启

        if (!launchOn && !prelaunchOn && !operationOn)
        {
            return (true, "已全部关闭（应用启动预读取 / UWP 预启动 / OperationAPI）");
        }

        var parts = new List<string>();
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

    /// <summary>值型微调的状态：当前值 == 优化值 → 已优化。</summary>
    private static (bool IsOptimized, string Detail) ValueToState(int? current, int optimized, string defaultText, string optimizedText)
        => current == optimized ? (true, optimizedText) : (false, $"未优化（{defaultText}）");

    public async Task<string> GetVbsRuntimeSummaryAsync()
    {
        // VirtualizationBasedSecurityStatus: 0=未启用 1=已启用未运行 2=已启用并运行
        // SecurityServicesRunning 包含 2 表示 HVCI 正在运行
        var command =
            "$dg = Get-CimInstance -Namespace root\\Microsoft\\Windows\\DeviceGuard -ClassName Win32_DeviceGuard -ErrorAction SilentlyContinue; " +
            "if ($dg) { [int]$dg.VirtualizationBasedSecurityStatus; @($dg.SecurityServicesRunning) -join ',' } else { 'ERR' }";
        var (exitCode, stdout, _) = await RunPowerShellAsync(command, TimeSpan.FromSeconds(20));
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
        var hvciRunning = (lines.ElementAtOrDefault(1) ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(item => item == "2");

        var vbsText = vbsStatus switch
        {
            2 => "已启用并运行",
            1 => "已启用（未运行）",
            0 => "未启用",
            _ => "未知",
        };

        return $"VBS：{vbsText}；内核隔离（HVCI）：{(hvciRunning ? "运行中" : "未运行")}。" +
               "（反映本次开机的实际状态，改注册表后需重启刷新）";
    }

    // ---------------- 关闭 ----------------

    public async Task<OperationResult> DisableAsync(SystemTweak tweak)
    {
        if (!ElevationHelper.IsElevated)
        {
            return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        }

        Log.Info($"深度优化：应用 {tweak}");
        try
        {
            var result = tweak switch
            {
                SystemTweak.HyperVAndVbs => await DisableHyperVAsync(),
                SystemTweak.MemoryCompression => await DisableMmAgentWithBackupAsync("MemoryCompression", "-mc", "内存压缩"),
                SystemTweak.PageCombining => await DisableMmAgentWithBackupAsync("PageCombining", "-PageCombining", "分页合并"),
                SystemTweak.Prefetch => await ApplyPrefetchTrioOffAsync(),

                // ---- 游戏微调包 ----
                SystemTweak.MouseAccelerationOff => ApplyMouseAccelerationOff(),
                SystemTweak.HardwareGpuScheduling =>
                    ApplyOptimizedDword(GraphicsDriversPath, "HwSchMode", 2, "HAGS 硬件加速 GPU 调度"),
                SystemTweak.WSearchOff => ApplyWSearchDisabledAsync(),
                _ => OperationResult.Fail("未知的优化项。"),
            };
            Log.Info(result.Success
                ? $"深度优化：应用 {tweak} 成功 —— {result.Message}"
                : $"深度优化：应用 {tweak} 失败 —— {result.Message}");
            return result;
        }
        catch (Exception ex)
        {
            Log.Error($"深度优化：应用 {tweak} 异常", ex);
            return OperationResult.Fail($"关闭失败：{ex.Message}");
        }
    }

    // ---------------- 恢复 ----------------

    public async Task<OperationResult> RestoreAsync(SystemTweak tweak)
    {
        if (!ElevationHelper.IsElevated)
        {
            return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        }

        Log.Info($"深度优化：还原 {tweak}");
        try
        {
            var result = tweak switch
            {
                SystemTweak.HyperVAndVbs => await RestoreHyperVAsync(),
                SystemTweak.MemoryCompression => await RestoreMmAgentAsync("MemoryCompression", "-mc", "内存压缩"),
                SystemTweak.PageCombining => await RestoreMmAgentAsync("PageCombining", "-PageCombining", "分页合并"),
                SystemTweak.Prefetch => await RestorePrefetchTrioAsync(),

                // ---- 游戏微调包（还原 = 写回备份的原始值） ----
                SystemTweak.MouseAccelerationOff => RestoreMouseAcceleration(),
                SystemTweak.HardwareGpuScheduling =>
                    RestoreValue("HKLM", GraphicsDriversPath, "HwSchMode", "HAGS 硬件加速 GPU 调度"),
                SystemTweak.WSearchOff => RestoreWSearchAsync(),
                _ => OperationResult.Fail("未知的优化项。"),
            };
            Log.Info(result.Success
                ? $"深度优化：还原 {tweak} 成功 —— {result.Message}"
                : $"深度优化：还原 {tweak} 失败 —— {result.Message}");
            return result;
        }
        catch (Exception ex)
        {
            Log.Error($"深度优化：还原 {tweak} 异常", ex);
            return OperationResult.Fail($"恢复失败：{ex.Message}");
        }
    }

    // ---------------- Hyper-V / VBS（等效 HyperV-off 流程） ----------------

    private async Task<OperationResult> DisableHyperVAsync()
    {
        var done = new List<string>();
        var skipped = new List<string>();

        var launchType = await QueryHypervisorLaunchTypeAsync();
        if (launchType is "auto" or "off")
        {
            _backups.Save(new RegistryValueBackup
            {
                Hive = "State", KeyPath = "HyperV", ValueName = "LaunchType",
                ValueKind = RegistryValueKind.String, Data = launchType, CreatedAt = DateTimeOffset.Now,
            });
        }

        // 1. 通知引导程序不要启动 hypervisor
        var (bcdCode, _, bcdError) = await RunProcessCaptureAsync(
            "bcdedit.exe", "/set hypervisorlaunchtype off", TimeSpan.FromSeconds(30));
        if (bcdCode == 0)
        {
            done.Add("hypervisor 启动已关闭");
        }
        else
        {
            skipped.Add($"bcdedit 失败（{FirstLine(bcdError)}）");
        }

        // 2. DISM 禁用全部 Hyper-V / 虚拟机平台功能（不存在或已禁用的会报错，逐项跳过）
        foreach (var feature in HyperVFeatures)
        {
            var (stateCode, stateText, _) = await RunPowerShellAsync(
                $"(Get-WindowsOptionalFeature -Online -FeatureName '{feature}' -ErrorAction Stop).State.ToString()",
                TimeSpan.FromSeconds(30));
            if (stateCode == 0 && stateText?.Trim() is "Enabled" or "Disabled")
            {
                _backups.Save(new RegistryValueBackup
                {
                    Hive = "State", KeyPath = "HyperVFeatures", ValueName = feature,
                    ValueKind = RegistryValueKind.String, Data = stateText.Trim(), CreatedAt = DateTimeOffset.Now,
                });
            }
            var (code, _, _) = await RunProcessCaptureAsync(
                "dism.exe", $"/Online /NoRestart /Disable-Feature /FeatureName:{feature}", TimeSpan.FromMinutes(3));
            if (code == 0)
            {
                done.Add($"已禁用功能 {feature}");
            }
        }

        // 3. 停止 Hyper-V 相关服务并改为手动启动（改前备份原 Start，恢复时按备份还原）
        foreach (var service in (string[])["HvHost", "vmms"])
        {
            await RunProcessCaptureAsync("sc.exe", $"stop {service}", TimeSpan.FromSeconds(20));
            var serviceKeyPath = ServicesRoot + "\\" + service;
            var currentStart = ReadDword("HKLM", serviceKeyPath, "Start");
            if (currentStart is not null)
            {
                _backups.Save(new RegistryValueBackup
                {
                    Hive = "HKLM",
                    Id = TweakBackupStore.MakeId("HKLM", serviceKeyPath, "Start"),
                    KeyPath = serviceKeyPath,
                    ValueName = "Start",
                    ValueKind = Microsoft.Win32.RegistryValueKind.DWord,
                    Data = currentStart.Value.ToString(),
                    CreatedAt = DateTimeOffset.Now,
                });
            }

            var (configCode, _, _) = await RunProcessCaptureAsync(
                "sc.exe", $"config {service} start= demand", TimeSpan.FromSeconds(20));
            if (configCode == 0)
            {
                done.Add($"服务 {service} 已改为手动启动");
            }
        }

        // 4. 关闭内核隔离（HVCI）、VBS、Credential Guard
        foreach (var (keyPath, valueName, label) in DeviceGuardValues)
        {
            var result = DisableDwordValue(keyPath, valueName, label);
            if (result.Success)
            {
                done.Add($"{label} 已关闭");
            }
            else
            {
                skipped.Add(result.Message);
            }
        }

        if (done.Count == 0)
        {
            return OperationResult.Fail($"关闭 Hyper-V / VBS 失败，所有步骤均未成功：{string.Join("；", skipped)}");
        }

        var skipNote = skipped.Count > 0
            ? $"（跳过 {skipped.Count} 项：{string.Join("；", skipped.Take(2))}{(skipped.Count > 2 ? " 等" : "")}）"
            : "";
        return OperationResult.Ok(
            $"已执行关闭 Hyper-V / VBS 共 {done.Count} 步操作{skipNote}。重启电脑后生效；" +
            "重启后 WSL2、虚拟机、Windows 沙盒将不可用。",
            requiresReboot: true);
    }

    private async Task<OperationResult> RestoreHyperVAsync()
    {
        var done = new List<string>();
        var skipped = new List<string>();

        // 只恢复修改前确实启用的项目，避免把用户原本关闭的功能擅自打开。
        var launchBackup = _backups.Get("State", "HyperV", "LaunchType");
        if (launchBackup?.Data is "auto" or "off")
        {
            var (code, _, error) = await RunProcessCaptureAsync(
                "bcdedit.exe", $"/set hypervisorlaunchtype {launchBackup.Data}", TimeSpan.FromSeconds(30));
            if (code == 0)
            {
                done.Add("hypervisor 启动状态已还原");
                _backups.Remove("State", "HyperV", "LaunchType");
            }
            else skipped.Add($"bcdedit 失败（{FirstLine(error)}）");
        }
        else
        {
            skipped.Add("缺少 hypervisor 原启动状态备份，未改动引导配置");
        }

        var hasFeatureBackup = false;
        foreach (var feature in HyperVFeatures)
        {
            var backup = _backups.Get("State", "HyperVFeatures", feature);
            if (backup is null) continue;
            hasFeatureBackup = true;
            if (backup.Data == "Enabled")
            {
                var (code, _, _) = await RunProcessCaptureAsync(
                    "dism.exe", $"/Online /NoRestart /Enable-Feature /FeatureName:{feature} /All", TimeSpan.FromMinutes(5));
                if (code != 0)
                {
                    skipped.Add($"功能 {feature} 恢复失败");
                    continue;
                }
                done.Add($"功能 {feature} 已还原");
            }
            _backups.Remove("State", "HyperVFeatures", feature);
        }
        if (!hasFeatureBackup)
        {
            skipped.Add("缺少可选功能原状态备份，未擅自启用 Hyper-V 功能");
        }

        // 3. 按备份还原 Hyper-V 服务启动类型（HvHost / vmms；无备份跳过）
        foreach (var service in (string[])["HvHost", "vmms"])
        {
            var serviceKeyPath = ServicesRoot + "\\" + service;
            var backup = _backups.Get("HKLM", serviceKeyPath, "Start");
            if (backup is null)
            {
                continue;
            }

            var result = RestoreValue("HKLM", serviceKeyPath, "Start", service);
            if (result.Success)
            {
                done.Add($"服务 {service} 启动类型已还原为 {ServiceStartText(int.TryParse(backup.Data, out var s) ? s : null)}");
            }
            else skipped.Add(result.Message);
        }

        // 4. 按备份还原 DeviceGuard 各项（无备份的项跳过）
        foreach (var (keyPath, valueName, label) in DeviceGuardValues)
        {
            var result = RestoreDwordValue(keyPath, valueName, label);
            if (result.Success)
            {
                done.Add($"{label} 已还原");
            }
            else
            {
                skipped.Add($"{label}：{result.Message}");
            }
        }

        if (done.Count == 0)
        {
            return OperationResult.Fail($"恢复 Hyper-V / VBS 失败：{string.Join("；", skipped)}");
        }

        if (skipped.Count > 0)
        {
            return OperationResult.Fail($"Hyper-V / VBS 已恢复 {done.Count} 步，但仍有未恢复项：{string.Join("；", skipped)}。请检查后重启。");
        }

        return OperationResult.Ok($"已恢复 Hyper-V / VBS 共 {done.Count} 步，重启电脑后生效。", requiresReboot: true);
    }

    /// <summary>查询 hypervisor 开机启动类型："off" / "auto" / "unknown"（bcdedit 不可用时）。</summary>
    private static async Task<string> QueryHypervisorLaunchTypeAsync()
    {
        var (code, stdout, _) = await RunProcessCaptureAsync(
            "bcdedit.exe", "/enum {current}", TimeSpan.FromSeconds(20));
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

    private static (bool IsOptimized, string Detail) HyperVToState(
        string hypervisor, int? vbs, int? hvci, int? credentialGuard)
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

    // ---------------- 注册表实现 ----------------

    private OperationResult DisableDwordValue(string keyPath, string valueName, string friendlyName)
    {
        try
        {
            using var key = OpenWritableOrCreate(keyPath);

            var current = key.GetValue(valueName);
            _backups.Save(new RegistryValueBackup
            {
                Id = TweakBackupStore.MakeId(keyPath, valueName),
                KeyPath = keyPath,
                ValueName = valueName,
                ValueKind = current is null ? RegistryValueKind.None : RegistryValueKind.DWord,
                Data = current is int dword ? dword.ToString() : "",
                CreatedAt = DateTimeOffset.Now,
            });

            key.SetValue(valueName, 0, RegistryValueKind.DWord);
            return OperationResult.Ok($"已关闭{friendlyName}，重启后生效。", requiresReboot: true);
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult.Fail($"没有权限写入 HKLM\\{keyPath}，请确认以管理员身份运行。");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"关闭{friendlyName}失败：{ex.Message}");
        }
    }

    private OperationResult RestoreDwordValue(string keyPath, string valueName, string friendlyName)
    {
        try
        {
            var backup = _backups.Get(keyPath, valueName);
            if (backup is null)
            {
                return OperationResult.Fail($"{friendlyName}没有本工具的备份记录（可能未用本工具修改过）。");
            }

            using var key = OpenWritableOrCreate(keyPath);
            if (backup.ValueKind == RegistryValueKind.None)
            {
                // 修改前该值不存在：删除即回到系统默认
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
            else
            {
                if (!int.TryParse(backup.Data, out var original))
                {
                    return OperationResult.Fail($"备份记录损坏（无法解析原始值），{friendlyName}未改动。");
                }

                key.SetValue(valueName, original, RegistryValueKind.DWord);
            }

            _backups.Remove(keyPath, valueName);
            return OperationResult.Ok($"已恢复{friendlyName}的原设置，重启后生效。", requiresReboot: true);
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult.Fail($"没有权限写入 HKLM\\{keyPath}，请确认以管理员身份运行。");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"恢复{friendlyName}失败：{ex.Message}");
        }
    }

    private static RegistryKey OpenWritableOrCreate(string keyPath)
        => OpenWritableOrCreate("HKLM", keyPath);

    private static RegistryKey OpenWritableOrCreate(string hive, string keyPath)
    {
        if (hive == "HKCU")
        {
            return Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        }

        if (RegistryAclHelper.EnsureWritable(keyPath))
        {
            var existing = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
            if (existing is not null)
            {
                return existing;
            }
        }

        // 键不存在（如从未配置过 HVCI）：父键管理员可写，直接创建
        return Registry.LocalMachine.CreateSubKey(keyPath, writable: true);
    }

    private static int? ReadDword(string keyPath, string valueName)
        => ReadDword("HKLM", keyPath, valueName);

    private static int? ReadDword(string hive, string keyPath, string valueName)
    {
        using var key = OpenRead(hive, keyPath);
        return key?.GetValue(valueName) is int value ? value : null;
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

    // ---------------- 游戏微调包实现 ----------------

    /// <summary>应用值型微调：备份原值 → 写入优化值。</summary>
    private OperationResult ApplyOptimizedDword(string keyPath, string valueName, int optimizedValue, string friendlyName)
        => ApplyOptimizedDword("HKLM", keyPath, valueName, optimizedValue, friendlyName);

    private OperationResult ApplyOptimizedDword(string hive, string keyPath, string valueName, int optimizedValue, string friendlyName)
    {
        try
        {
            using var key = OpenWritableOrCreate(hive, keyPath);

            var current = key.GetValue(valueName);
            _backups.Save(new RegistryValueBackup
            {
                Hive = hive,
                Id = TweakBackupStore.MakeId(hive, keyPath, valueName),
                KeyPath = keyPath,
                ValueName = valueName,
                ValueKind = current is null ? RegistryValueKind.None : RegistryValueKind.DWord,
                Data = current is int dword ? dword.ToString() : "",
                CreatedAt = DateTimeOffset.Now,
            });

            key.SetValue(valueName, optimizedValue, RegistryValueKind.DWord);
            return OperationResult.Ok($"已应用{friendlyName}。", requiresReboot: true);
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult.Fail($"没有权限写入 {hive}\\{keyPath}，请确认以管理员身份运行。");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"应用{friendlyName}失败：{ex.Message}");
        }
    }

    /// <summary>通用恢复：按备份写回原值（DWORD / String），原值不存在则删除该值。</summary>
    private OperationResult RestoreValue(string hive, string keyPath, string valueName, string friendlyName)
    {
        try
        {
            var backup = _backups.Get(hive, keyPath, valueName);
            if (backup is null)
            {
                return OperationResult.Fail($"{friendlyName}没有本工具的备份记录（可能未用本工具修改过）。");
            }

            using var key = OpenWritableOrCreate(hive, keyPath);
            if (backup.ValueKind == RegistryValueKind.None)
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
            else if (backup.ValueKind == RegistryValueKind.DWord)
            {
                if (!int.TryParse(backup.Data, out var original))
                {
                    return OperationResult.Fail($"备份记录损坏（无法解析原始值），{friendlyName}未改动。");
                }

                key.SetValue(valueName, original, RegistryValueKind.DWord);
            }
            else
            {
                key.SetValue(valueName, backup.Data, RegistryValueKind.String);
            }

            _backups.Remove(hive, keyPath, valueName);
            return OperationResult.Ok($"已恢复{friendlyName}的原设置。", requiresReboot: true);
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult.Fail($"没有权限写入 {hive}\\{keyPath}，请确认以管理员身份运行。");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"恢复{friendlyName}失败：{ex.Message}");
        }
    }

    // ---- 鼠标加速（SystemParametersInfo 立即生效 + HKCU 备份） ----

    private OperationResult ApplyMouseAccelerationOff()
    {
        try
        {
            // 备份 3 个 HKCU 原始字符串值
            foreach (var valueName in (string[])["MouseSpeed", "MouseThreshold1", "MouseThreshold2"])
            {
                var current = ReadString("HKCU", MousePath, valueName);
                _backups.Save(new RegistryValueBackup
                {
                    Hive = "HKCU",
                    Id = TweakBackupStore.MakeId("HKCU", MousePath, valueName),
                    KeyPath = MousePath,
                    ValueName = valueName,
                    ValueKind = current is null ? RegistryValueKind.None : RegistryValueKind.String,
                    Data = current ?? "",
                    CreatedAt = DateTimeOffset.Now,
                });
            }

            // SPI_SETMOUSE = 0x0004，参数 {Threshold1, Threshold2, Speed} = {0,0,0} 关闭加速并写回注册表
            var mouseParams = new[] { 0, 0, 0 };
            if (!SystemParametersInfo(SpiSetMouse, 0u, mouseParams, SpifUpdateIniFile | SpifSendChange))
            {
                return OperationResult.Fail("设置鼠标参数失败（SystemParametersInfo 调用出错）。");
            }

            return OperationResult.Ok("已关闭鼠标加速（立即生效）。");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"关闭鼠标加速失败：{ex.Message}");
        }
    }

    private OperationResult RestoreMouseAcceleration()
    {
        try
        {
            var speed = _backups.Get("HKCU", MousePath, "MouseSpeed");
            var threshold1 = _backups.Get("HKCU", MousePath, "MouseThreshold1");
            var threshold2 = _backups.Get("HKCU", MousePath, "MouseThreshold2");
            if (speed is null && threshold1 is null && threshold2 is null)
            {
                return OperationResult.Fail("鼠标加速没有本工具的备份记录（可能未用本工具修改过）。");
            }

            int ParseOr(RegistryValueBackup? backup, int fallback) =>
                backup is { ValueKind: not RegistryValueKind.None } && int.TryParse(backup.Data, out var value)
                    ? value
                    : fallback;

            var mouseParams = new[]
            {
                ParseOr(threshold1, 6),
                ParseOr(threshold2, 10),
                ParseOr(speed, 1),
            };

            if (!SystemParametersInfo(SpiSetMouse, 0u, mouseParams, SpifUpdateIniFile | SpifSendChange))
            {
                return OperationResult.Fail("恢复鼠标参数失败（SystemParametersInfo 调用出错）。");
            }

            foreach (var valueName in (string[])["MouseSpeed", "MouseThreshold1", "MouseThreshold2"])
            {
                _backups.Remove("HKCU", MousePath, valueName);
            }

            return OperationResult.Ok("已恢复鼠标加速的原设置（立即生效）。");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"恢复鼠标加速失败：{ex.Message}");
        }
    }

    // ---- 预读取三件套（Prefetch / ApplicationPreLaunch / OperationAPI） ----

    private async Task<OperationResult> ApplyPrefetchTrioOffAsync()
    {
        var problems = new List<string>();

        // 1. 应用启动预读取 + OperationAPI：直写 Memory Management 键的两个底层注册表位。
        //    Disable-MMAgent 的底层就是这两个值，但该 cmdlet 在部分系统/虚拟机（尤其 Hyper-V 客户机）
        //    上返回 "The request is not supported"（任一参数不受支持即整条失败）——直写绕开该限制，效果等同。
        try
        {
            BackupDword(MemoryManagementPath, "EnableApplicationLaunchPrefetching");
            WriteDword(MemoryManagementPath, "EnableApplicationLaunchPrefetching", 0);
            BackupDword(MemoryManagementPath, "EnableOperationAPI");
            WriteDword(MemoryManagementPath, "EnableOperationAPI", 0);
        }
        catch (Exception ex)
        {
            problems.Add($"应用启动预读取/OperationAPI：{ex.Message}");
        }

        // 1.5 内核预读取总开关 EnablePrefetcher（Get-MMAgent 的 ApplicationLaunchPrefetching
        //     反映的就是它）：0 = 内核不再为应用启动/开机生成预读取文件
        try
        {
            BackupDword(PrefetchPath, "EnablePrefetcher");
            WriteDword(PrefetchPath, "EnablePrefetcher", 0);
        }
        catch (Exception ex)
        {
            problems.Add($"EnablePrefetcher：{ex.Message}");
        }

        // 2. UWP 应用预启动（HKCU，备份原值后写 0）
        try
        {
            var current = ReadDword("HKCU", ApplicationPreLaunchPath, "Enabled");
            _backups.Save(new RegistryValueBackup
            {
                Hive = "HKCU",
                Id = TweakBackupStore.MakeId("HKCU", ApplicationPreLaunchPath, "Enabled"),
                KeyPath = ApplicationPreLaunchPath,
                ValueName = "Enabled",
                ValueKind = current is null ? RegistryValueKind.None : RegistryValueKind.DWord,
                Data = current?.ToString() ?? "",
                CreatedAt = DateTimeOffset.Now,
            });

            using var key = OpenWritableOrCreate("HKCU", ApplicationPreLaunchPath);
            key.SetValue("Enabled", 0, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            problems.Add($"UWP 预启动：{ex.Message}");
        }

        if (problems.Count > 0)
        {
            return OperationResult.Fail($"禁用预读取部分失败：{string.Join("；", problems)}");
        }

        return OperationResult.Ok(
            "已禁用应用启动预读取 / UWP 预启动 / OperationAPI（写入注册表关闭值；若系统仍发生预读取，重启电脑后完全生效）。Windows Search 为独立开关，未受影响。");
    }

    /// <summary>备份某注册表键下的一个 DWORD 原值（不存在则记 None）。</summary>
    private void BackupDword(string keyPath, string valueName)
    {
        var hive = keyPath.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase) ? "HKCU" : "HKLM";
        var current = ReadDword(hive, keyPath, valueName);
        _backups.Save(new RegistryValueBackup
        {
            Hive = hive,
            Id = TweakBackupStore.MakeId(hive, keyPath, valueName),
            KeyPath = keyPath,
            ValueName = valueName,
            ValueKind = current is null ? RegistryValueKind.None : RegistryValueKind.DWord,
            Data = current?.ToString() ?? "",
            CreatedAt = DateTimeOffset.Now,
        });
    }

    /// <summary>写入某注册表键下的一个 DWORD。</summary>
    private void WriteDword(string keyPath, string valueName, int value)
    {
        using var key = OpenWritableOrCreate(keyPath);
        key.SetValue(valueName, value, RegistryValueKind.DWord);
    }

    private async Task<OperationResult> RestorePrefetchTrioAsync()
    {
        var problems = new List<string>();

        // 1. 应用启动预读取 + OperationAPI 按备份还原（无备份 = 默认开启，写 1；不依赖 MMAgent cmdlet）
        try
        {
            RestoreDword(MemoryManagementPath, "EnableApplicationLaunchPrefetching");
            RestoreDword(MemoryManagementPath, "EnableOperationAPI");
        }
        catch (Exception ex)
        {
            problems.Add($"应用启动预读取/OperationAPI 还原：{ex.Message}");
        }

        // 1.5 内核预读取总开关按备份还原（无备份 = 默认 3，应用+开机预读取）
        try
        {
            RestoreDword(PrefetchPath, "EnablePrefetcher");
        }
        catch (Exception ex)
        {
            problems.Add($"EnablePrefetcher 还原：{ex.Message}");
        }

        // 2. UWP 预启动按备份还原（原值不存在则删除 = 回到默认开启）
        var backup = _backups.Get("HKCU", ApplicationPreLaunchPath, "Enabled");
        if (backup is not null)
        {
            try
            {
                using var key = OpenWritableOrCreate("HKCU", ApplicationPreLaunchPath);
                if (backup.ValueKind == RegistryValueKind.None)
                {
                    key.DeleteValue("Enabled", throwOnMissingValue: false);
                }
                else if (int.TryParse(backup.Data, out var original))
                {
                    key.SetValue("Enabled", original, RegistryValueKind.DWord);
                }
                else
                {
                    throw new FormatException("UWP 预启动备份值无效");
                }

                _backups.Remove("HKCU", ApplicationPreLaunchPath, "Enabled");
            }
            catch (Exception ex)
            {
                problems.Add($"UWP 预启动还原：{ex.Message}");
            }
        }

        if (problems.Count > 0)
        {
            return OperationResult.Fail($"还原预读取部分失败：{string.Join("；", problems)}");
        }

        return OperationResult.Ok("已恢复预读取三件套（应用启动预读取/OperationAPI 恢复默认开启）。");
    }

    /// <summary>只按真实备份还原；缺失的备份不触碰该注册表值。</summary>
    private void RestoreDword(string keyPath, string valueName)
    {
        const string hive = "HKLM";
        var backup = _backups.Get(hive, keyPath, valueName);
        if (backup is null) return;
        using var key = OpenWritableOrCreate(keyPath);
        if (backup.ValueKind == RegistryValueKind.None)
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
        else
        {
            if (!int.TryParse(backup.Data, out var original))
                throw new FormatException($"{valueName} 备份值无效");
            key.SetValue(valueName, original, RegistryValueKind.DWord);
        }

        _backups.Remove(hive, keyPath, valueName);
    }

    // ---- Windows Search 服务禁用（不动 SysMain：禁用 SysMain 会连带关掉内存压缩） ----

    private OperationResult ApplyWSearchDisabledAsync()
    {
        var wSearch = ApplyOptimizedDword(WSearchServicePath, "Start", 4, "Windows Search 服务");
        if (!wSearch.Success)
        {
            return OperationResult.Fail($"禁用服务失败：{wSearch.Message}");
        }

        // 尽力停止运行中的服务（失败不阻塞：重启后即不再启动）
        _ = RunProcessCaptureAsync("sc.exe", "stop WSearch", TimeSpan.FromSeconds(15));

        return OperationResult.Ok("已禁用 Windows Search 服务，重启后生效。（SysMain 未动，内存压缩不受影响）", requiresReboot: true);
    }

    private OperationResult RestoreWSearchAsync()
    {
        if (_backups.Get("HKLM", WSearchServicePath, "Start") is null)
        {
            return OperationResult.Fail("Windows Search 没有本工具的备份记录。");
        }

        var result = RestoreValue("HKLM", WSearchServicePath, "Start", "Windows Search 服务");
        return result.Success
            ? OperationResult.Ok("已恢复 Windows Search 服务设置，重启后生效。", requiresReboot: true)
            : result;
    }

    private static TweakStatus MakeStatus(SystemTweak tweak, (bool IsOptimized, string Detail) state)
    {
        var (displayName, description) = Describe(tweak);
        return new TweakStatus
        {
            Tweak = tweak,
            DisplayName = displayName,
            Description = description,
            IsOptimized = state.IsOptimized,
            Detail = state.Detail,
        };
    }

    private static (bool IsOptimized, string Detail) DwordToState(int? value, string notConfiguredDetail)
        => value switch
        {
            null => (false, notConfiguredDetail),
            0 => (true, "已关闭"),
            _ => (false, "已开启"),
        };

    private static (string DisplayName, string Description) Describe(SystemTweak tweak) => tweak switch
    {
        SystemTweak.HyperVAndVbs =>
            ("关闭 Hyper-V / VBS（含内核隔离）",
             "等效 HyperV-off：关闭 hypervisor 启动、禁用 Hyper-V 与虚拟机平台功能、关闭内核隔离与 Credential Guard。" +
             "重启生效。注意：重启后 WSL2、虚拟机、Windows 沙盒将不可用，系统安全性降低"),
        SystemTweak.MemoryCompression =>
            ("关闭内存压缩", "关闭 MemCompression 进程，降低 CPU 占用；可用内存量会略微减少"),
        SystemTweak.PageCombining =>
            ("关闭分页合并", "关闭内存页合并，减少后台 CPU 活动"),
        SystemTweak.Prefetch =>
            ("禁用预读取（Prefetch / PreLaunch / OperationAPI）",
             "一次关闭三项：应用启动预读取（EnablePrefetcher）、UWP 应用预启动（ApplicationPreLaunch）、操作记录 API（OperationAPI）。" +
             "减少磁盘与 CPU 后台活动；应用首次启动略慢，UWP 应用不再被提前拉起。Windows Search 已单独列项"),

        // ---- 游戏微调包 ----
        SystemTweak.MouseAccelerationOff =>
            ("关闭鼠标加速", "关闭指针加速曲线（MouseSpeed/Threshold=0），瞄准移动 1:1 线性；FPS 玩家推荐，立即生效"),
        SystemTweak.HardwareGpuScheduling =>
            ("HAGS 硬件加速 GPU 调度", "开启 HwSchMode=2，由显卡直接管理显存调度，可小幅降低延迟；需 RTX/GTX16 系或较新显卡支持，重启生效"),
        SystemTweak.WSearchOff =>
            ("禁用 Windows Search 服务",
             "关闭搜索索引，显著减少磁盘与 CPU 后台占用；开始菜单/文件搜索会变慢。" +
             "注意：不动 SysMain——SysMain 承载内存压缩（MemCompression），禁用它会把内存压缩一起关掉"),
        _ => ("未知", ""),
    };

    // ---------------- MMAgent（内存压缩 / 分页合并） ----------------

    private static async Task<OperationResult> RunMmAgentAsync(string psCommand, string friendlyName, string doneText)
    {
        var (exitCode, stdout, stderr) = await RunPowerShellAsync(psCommand, TimeSpan.FromSeconds(30));
        if (exitCode != 0)
        {
            var reason = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            return OperationResult.Fail(
                $"操作{friendlyName}失败：{(string.IsNullOrWhiteSpace(reason) ? "PowerShell 执行出错" : reason.Trim())}");
        }

        return OperationResult.Ok(doneText, requiresReboot: true);
    }

    private async Task<OperationResult> DisableMmAgentWithBackupAsync(string property, string argument, string label)
    {
        var (code, stdout, error) = await RunPowerShellAsync($"[int](Get-MMAgent).{property}", TimeSpan.FromSeconds(20));
        if (code != 0 || !int.TryParse(stdout?.Trim(), out var original))
        {
            return OperationResult.Fail($"无法读取{label}原状态：{error ?? stdout ?? "未知错误"}");
        }

        _backups.Save(new RegistryValueBackup
        {
            Hive = "State", KeyPath = "MMAgent", ValueName = property,
            ValueKind = RegistryValueKind.String, Data = original.ToString(), CreatedAt = DateTimeOffset.Now,
        });
        return await RunMmAgentAsync($"Disable-MMAgent {argument}", label, $"已关闭{label}，重启后生效。");
    }

    private async Task<OperationResult> RestoreMmAgentAsync(string property, string argument, string label)
    {
        var backup = _backups.Get("State", "MMAgent", property);
        if (backup?.Data is not ("0" or "1"))
        {
            return OperationResult.Fail($"没有{label}的原状态备份。");
        }

        var command = backup.Data == "1" ? "Enable-MMAgent" : "Disable-MMAgent";
        var result = await RunMmAgentAsync($"{command} {argument}", label, $"已恢复{label}修改前的状态，重启后生效。");
        if (result.Success)
        {
            _backups.Remove("State", "MMAgent", property);
        }
        return result;
    }

    /// <summary>并行查询 MMAgent 和 hypervisor 状态（原来串行，现合并为一次并行调用减少等待）。</summary>
    private static async Task<(bool CompressionOn, bool PageCombiningOn, string Hypervisor)> QueryMmAgentAndHypervisorAsync()
    {
        var mmTask = QueryMmAgentAsync();
        var hvTask = QueryHypervisorLaunchTypeAsync();
        await Task.WhenAll(mmTask, hvTask);
        var (compressionOn, pageCombiningOn) = mmTask.Result;
        return (compressionOn, pageCombiningOn, hvTask.Result);
    }

    private static async Task<(bool CompressionOn, bool PageCombiningOn)> QueryMmAgentAsync()
    {
        var command = "$m = Get-MMAgent; [int]$m.MemoryCompression; [int]$m.PageCombining";
        var (exitCode, stdout, _) = await RunPowerShellAsync(command, TimeSpan.FromSeconds(20));
        if (exitCode != 0 || stdout is null)
        {
            // 查询失败时按「未优化」处理，避免误导用户
            return (true, true);
        }

        var lines = SplitLines(stdout);
        return (LineOn(lines.ElementAtOrDefault(0)), LineOn(lines.ElementAtOrDefault(1)));

        static bool LineOn(string? line) => line is not null && line.Trim() != "0";
    }

    // ---------------- 进程调用 ----------------

    /// <summary>执行原生命令行工具（bcdedit / dism / sc），返回退出码与输出。</summary>
    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessCaptureAsync(
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

    private static async Task<(int ExitCode, string? StdOut, string? StdErr)> RunPowerShellAsync(
        string command, TimeSpan timeout)
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

    private static string FirstLine(string text)
    {
        var trimmed = text.Trim();
        var lineBreak = trimmed.IndexOfAny(['\r', '\n']);
        return lineBreak > 0 ? trimmed[..lineBreak] : trimmed;
    }

    private static string[] SplitLines(string text)
        => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
