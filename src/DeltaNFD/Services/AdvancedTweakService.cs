using System.Diagnostics;
using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>高级（高危）优化的真实实现，分组目录驱动，全部可还原。</summary>
public sealed class AdvancedTweakService : IAdvancedTweakService
{
    // ---------------- 目录定义 ----------------

    private sealed record RegSpec(string Hive, string KeyPath, string ValueName, object Value, RegistryValueKind Kind);

    private sealed record SvcSpec(string Name);

    private sealed record TaskSpec(string Path);

    private sealed record GroupDef(
        string Id, string Name, string Desc, string RiskNote, AdvancedRisk Risk, AdvancedCategory Category,
        RegSpec[] Regs, SvcSpec[] Services, TaskSpec[] Tasks);

    private const string Policies = @"SOFTWARE\Policies\Microsoft";
    private const string Nt = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string CurVer = @"SOFTWARE\Microsoft\Windows\CurrentVersion";

    private const string TaskkillDebugger = @"%windir%\System32\taskkill.exe";

    private static RegSpec D(string hive, string keyPath, string valueName, int value) =>
        new(hive, keyPath, valueName, value, RegistryValueKind.DWord);

    private static RegSpec S(string hive, string keyPath, string valueName, string value) =>
        new(hive, keyPath, valueName, value, RegistryValueKind.String);

    private static readonly GroupDef[] Groups =
    [
        new("telemetry", "遥测与数据采集",
            "禁用诊断跟踪服务（DiagTrack/dmwappush/diagsvc）、遥测策略 AllowTelemetry=0、兼容性数据采集（IFEO 劫持 CompatTelRunner/DeviceCensus/AggregatorHost/mobsync 为 taskkill）、CEIP 计划任务与应用兼容引擎",
            "影响 Microsoft 诊断数据的收集能力；个别系统诊断功能（如可靠性历史记录）将没有数据",
            AdvancedRisk.High, AdvancedCategory.Telemetry,
            [
                D("HKLM", $@"{Policies}\Windows\DataCollection", "AllowTelemetry", 0),
                D("HKLM", $@"{Policies}\SQMClient\Windows", "CEIPEnable", 0),
                S("HKLM", $@"{Nt}\Image File Execution Options\CompatTelRunner.exe", "Debugger", TaskkillDebugger),
                S("HKLM", $@"{Nt}\Image File Execution Options\DeviceCensus.exe", "Debugger", TaskkillDebugger),
                S("HKLM", $@"{Nt}\Image File Execution Options\AggregatorHost.exe", "Debugger", TaskkillDebugger),
                S("HKLM", $@"{Nt}\Image File Execution Options\mobsync.exe", "Debugger", TaskkillDebugger),
                D("HKLM", $@"{Policies}\Windows\AppCompat", "AITEnable", 0),
                D("HKLM", $@"{Policies}\Windows\AppCompat", "DisableEngine", 1),
                D("HKLM", $@"{Policies}\Windows\AppCompat", "DisablePCA", 1),
            ],
            [new SvcSpec("DiagTrack"), new SvcSpec("dmwappushservice"), new SvcSpec("diagsvc"), new SvcSpec("diagnosticshub.standardcollector.service")],
            [
                new TaskSpec(@"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator"),
                new TaskSpec(@"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip"),
                new TaskSpec(@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser"),
                new TaskSpec(@"\Microsoft\Windows\Application Experience\ProgramDataUpdater"),
                new TaskSpec(@"\Microsoft\Windows\Application Experience\PcaPatchDbTask"),
            ]),

        new("wer", "错误报告（WER）",
            "禁用 Windows 错误报告服务与队列上报任务，写入 Disabled=1",
            "应用崩溃后不再上传错误报告；错误报告中心将无数据",
            AdvancedRisk.Medium, AdvancedCategory.Telemetry,
            [D("HKLM", @"SOFTWARE\Microsoft\Windows\Windows Error Reporting", "Disabled", 1)],
            [new SvcSpec("WerSvc"), new SvcSpec("WercplSupport")],
            [new TaskSpec(@"\Microsoft\Windows\Windows Error Reporting\QueueReporting")]),

        new("cortana", "Cortana 与输入个性化",
            "关闭语音激活、云端输入个性化（TIPC）、手写/墨迹隐式收集、Cortana 策略",
            "开始菜单/输入法的云联想与语音助手能力下降",
            AdvancedRisk.Medium, AdvancedCategory.Telemetry,
            [
                D("HKCU", @"Software\Microsoft\Input\TIPC", "Enabled", 0),
                D("HKCU", @"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted", 0),
                D("HKLM", $@"{Policies}\Windows\AppPrivacy", "LetAppsActivateWithVoice", 2),
                D("HKLM", $@"{Policies}\InputPersonalization", "RestrictImplicitInkCollection", 1),
                D("HKLM", $@"{Policies}\InputPersonalization", "RestrictImplicitTextCollection", 1),
                D("HKLM", $@"{Policies}\Windows\Windows Search", "AllowCortana", 0),
                D("HKCU", @"Software\Microsoft\Personalization\Settings", "AcceptedPrivacyPolicy", 0),
            ],
            [],
            []),

        new("location", "位置与搜索",
            "禁用定位服务与策略、Bing 网页搜索、搜索使用位置",
            "开始菜单/任务栏搜索只搜本机文件；地图与天气等定位功能失效",
            AdvancedRisk.Medium, AdvancedCategory.Telemetry,
            [
                D("HKLM", $@"{Policies}\Windows\LocationAndSensors", "DisableLocation", 1),
                D("HKLM", $@"{Policies}\Windows\AppPrivacy", "LetAppsAccessLocation", 2),
                D("HKCU", $@"{CurVer}\Search", "BingSearchEnabled", 0),
                D("HKCU", $@"{CurVer}\Search", "DisableWebSearch", 1),
                D("HKCU", $@"{CurVer}\Search", "AllowSearchToUseLocation", 0),
            ],
            [new SvcSpec("lfsvc")],
            []),

        new("ads", "广告与推荐内容",
            "关闭锁屏/开始菜单/设置页的推广内容与「建议」、消费者功能推送（ContentDeliveryManager 全组 + CloudContent 策略）",
            "只影响推广展示，不影响系统功能",
            AdvancedRisk.Low, AdvancedCategory.Telemetry,
            [
                D("HKCU", $@"{CurVer}\ContentDeliveryManager", "ContentDeliveryAllowed", 0),
                D("HKCU", $@"{CurVer}\ContentDeliveryManager", "OemPreInstalledAppsEnabled", 0),
                D("HKCU", $@"{CurVer}\ContentDeliveryManager", "PreInstalledAppsEnabled", 0),
                D("HKCU", $@"{CurVer}\ContentDeliveryManager", "SilentInstalledAppsEnabled", 0),
                D("HKCU", $@"{CurVer}\ContentDeliveryManager", "SoftLandingEnabled", 0),
                D("HKCU", $@"{CurVer}\ContentDeliveryManager", "SubscribedContent-310093Enabled", 0),
                D("HKCU", $@"{CurVer}\ContentDeliveryManager", "SubscribedContent-338387Enabled", 0),
                D("HKCU", $@"{CurVer}\ContentDeliveryManager", "SubscribedContent-338388Enabled", 0),
                D("HKCU", $@"{CurVer}\ContentDeliveryManager", "SubscribedContent-338389Enabled", 0),
                D("HKCU", $@"{CurVer}\ContentDeliveryManager", "SubscribedContent-338393Enabled", 0),
                D("HKCU", $@"{CurVer}\ContentDeliveryManager", "SubscribedContent-353694Enabled", 0),
                D("HKCU", $@"{CurVer}\ContentDeliveryManager", "SubscribedContent-353696Enabled", 0),
                D("HKCU", $@"{CurVer}\ContentDeliveryManager", "SubscribedContent-88000326Enabled", 0),
                D("HKCU", $@"{CurVer}\ContentDeliveryManager", "SystemPaneSuggestionsEnabled", 0),
                D("HKCU", $@"{CurVer}\ContentDeliveryManager", "RotatingLockScreenOverlayEnabled", 0),
                D("HKLM", $@"{Policies}\Windows\CloudContent", "DisableWindowsConsumerFeatures", 1),
                D("HKLM", $@"{Policies}\Windows\CloudContent", "DisableSoftLanding", 1),
            ],
            [],
            []),

        new("cloudsync", "云同步与传递优化",
            "禁用设置云同步、地图自动更新、商店自动下载、传递优化（DODownloadMode=0）",
            "多设备设置不再同步；Windows 更新不再从局域网/互联网共享下载",
            AdvancedRisk.Medium, AdvancedCategory.Telemetry,
            [
                D("HKLM", $@"{Policies}\Windows\SettingSync", "DisableSettingSync", 2),
                D("HKLM", $@"{Policies}\Windows\SettingSync", "DisableSettingSyncUserOverride", 1),
                D("HKLM", @"SYSTEM\Maps", "AutoUpdateEnabled", 0),
                D("HKLM", $@"{Policies}\WindowsStore", "AutoDownload", 2),
                D("HKLM", $@"{Policies}\Windows\DeliveryOptimization", "DODownloadMode", 0),
            ],
            [],
            []),

        new("uwpbg", "UWP 后台应用",
            "全局禁用 UWP 应用后台运行（GlobalUserDisabled、BackgroundAppGlobalToggle、embeddedmode 服务）",
            "Microsoft Store 应用（如邮件、闹钟）不能在后台推送/刷新",
            AdvancedRisk.Medium, AdvancedCategory.Telemetry,
            [
                D("HKCU", $@"{CurVer}\BackgroundAccessApplications", "GlobalUserDisabled", 1),
                D("HKCU", $@"{CurVer}\Search", "BackgroundAppGlobalToggle", 0),
            ],
            [new SvcSpec("embeddedmode")],
            []),

        new("updates", "禁用 Windows 更新",
            "停止并禁用更新服务（wuauserv/UsoSvc/WaaSMedicSvc/uhssvc）、策略 NoAutoUpdate=1、排除驱动更新、禁用更新计划任务",
            "系统将不再接收安全更新；需恢复时关闭本开关或手动开启 wuauserv 服务",
            AdvancedRisk.Critical, AdvancedCategory.Updates,
            [
                D("HKLM", $@"{Policies}\Windows\WindowsUpdate\AU", "NoAutoUpdate", 1),
                D("HKLM", $@"{Policies}\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", 1),
            ],
            [new SvcSpec("wuauserv"), new SvcSpec("UsoSvc"), new SvcSpec("WaaSMedicSvc"), new SvcSpec("uhssvc")],
            [
                new TaskSpec(@"\Microsoft\Windows\WindowsUpdate\Scheduled Start"),
                new TaskSpec(@"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan"),
            ]),

        new("defender_rt", "Defender 实时防护",
            "Set-MpPreference 关闭实时监控（含行为监控、PUA 策略归零）",
            "系统将失去实时病毒/恶意软件拦截，建议仅在纯游戏环境短期开启；若篡改保护已开启，需在 Windows 安全中心手动关闭后才能生效",
            AdvancedRisk.Critical, AdvancedCategory.Security,
            [D("HKLM", $@"{Policies}\Windows Defender", "PUAProtection", 0)],
            [],
            []),

        new("defender_svc", "Defender 服务全套",
            "禁用 Microsoft Defender 相关服务（Sense/MsSecCore/MsSecFlt/wscsvc/SgrmAgent/webthreatdefsvc）与 Defender 计划任务",
            "安全中心将显示无防病毒 provider；与「实时防护」同时开启后杀毒完全停用，联网风险自负",
            AdvancedRisk.Critical, AdvancedCategory.Security,
            [],
            [new SvcSpec("Sense"), new SvcSpec("MsSecCore"), new SvcSpec("MsSecFlt"), new SvcSpec("wscsvc"), new SvcSpec("SgrmAgent"), new SvcSpec("webthreatdefsvc"), new SvcSpec("WinDefend")],
            [
                new TaskSpec(@"\Microsoft\Windows\Windows Defender\Windows Defender Cache Maintenance"),
                new TaskSpec(@"\Microsoft\Windows\Windows Defender\Windows Defender Cleanup"),
                new TaskSpec(@"\Microsoft\Windows\Windows Defender\Windows Defender Scheduled Scan"),
                new TaskSpec(@"\Microsoft\Windows\Windows Defender\Windows Defender Verification"),
            ]),

        new("smartscreen", "SmartScreen 关闭",
            "Explorer SmartScreenEnabled=Off、策略 EnableSmartScreen=0、Edge SmartScreen 策略关闭（等效 扩展优化库，注册表方式实现，不做文件改名删除）",
            "运行未知下载文件时不再弹出「已保护你的电脑」拦截；钓鱼网站防护（Edge）关闭",
            AdvancedRisk.Critical, AdvancedCategory.Security,
            [
                S("HKLM", $@"{CurVer}\Explorer", "SmartScreenEnabled", "Off"),
                D("HKLM", $@"{Policies}\Windows\System", "EnableSmartScreen", 0),
                D("HKLM", $@"{Policies}\Edge", "SmartScreenEnabled", 0),
            ],
            [],
            []),

        new("uac", "UAC 降级",
            "ConsentPromptBehaviorAdmin=0 + PromptOnSecureDesktop=0：管理员提权不再弹确认（等效 扩展优化库）",
            "任何程序请求管理员权限时直接通过，恶意程序更容易静默提权；强烈不建议日常使用",
            AdvancedRisk.Critical, AdvancedCategory.Security,
            [
                D("HKLM", $@"{CurVer}\Policies\System", "ConsentPromptBehaviorAdmin", 0),
                D("HKLM", $@"{CurVer}\Policies\System", "PromptOnSecureDesktop", 0),
            ],
            [],
            []),

        new("firewall", "防火墙关闭",
            "三个配置文件（域/专用/公用）EnableFirewall=0",
            "系统完全暴露于网络环境（扩展优化库 标注：Xbox 联机部分功能会受影响）",
            AdvancedRisk.Critical, AdvancedCategory.Security,
            [
                D("HKLM", @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\DomainProfile", "EnableFirewall", 0),
                D("HKLM", @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\StandardProfile", "EnableFirewall", 0),
                D("HKLM", @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\PublicProfile", "EnableFirewall", 0),
            ],
            [],
            []),

        new("miscsec", "其他安全功能",
            "AMSI 反恶意软件扫描接口（AmsiEnable=0）、WPBT 固件执行（DisableWpbtExecution=1）、Smart App Control（VerifiedAndReputablePolicyState=0）、安全中心通知",
            "AMSI 关闭后部分杀软/脚本扫描能力失效；SAC 关闭后不可再次开启（需重装系统恢复），请谨慎",
            AdvancedRisk.Critical, AdvancedCategory.Security,
            [
                D("HKLM", @"SOFTWARE\Microsoft\Wbem", "AmsiEnable", 0),
                D("HKLM", @"SYSTEM\CurrentControlSet\Control\Session Manager", "DisableWpbtExecution", 1),
                D("HKLM", @"SYSTEM\CurrentControlSet\Control\CI\Policy", "VerifiedAndReputablePolicyState", 0),
                D("HKLM", @"SOFTWARE\Microsoft\Security Center", "UpdatesDisableNotify", 1),
                D("HKLM", @"SOFTWARE\Microsoft\Security Center", "FirewallDisableNotify", 1),
                D("HKLM", @"SOFTWARE\Microsoft\Security Center", "AntiVirusDisableNotify", 1),
                D("HKLM", @"SOFTWARE\Microsoft\Security Center", "AntiSpywareDisableNotify", 1),
            ],
            [],
            []),

        new("tasks", "计划任务批量精简",
            "禁用非必要的系统计划任务：诊断类（Chkdsk/DiskDiagnostic/MemoryDiagnostic）、体验分析（Autochk Proxy）、地图、语言组件、WinSAT、远程协助、设备信息、设置同步上传",
            "内存诊断、磁盘诊断等维护能力关闭；可随时还原",
            AdvancedRisk.Medium, AdvancedCategory.Updates,
            [],
            [],
            [
                new TaskSpec(@"\Microsoft\Windows\Autochk\Proxy"),
                new TaskSpec(@"\Microsoft\Windows\Chkdsk\ProactiveScan"),
                new TaskSpec(@"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector"),
                new TaskSpec(@"\Microsoft\Windows\MemoryDiagnostic\ProcessMemoryDiagnosticEvents"),
                new TaskSpec(@"\Microsoft\Windows\MemoryDiagnostic\RunFullMemoryDiagnostic"),
                new TaskSpec(@"\Microsoft\Windows\Maps\MapsToastTask"),
                new TaskSpec(@"\Microsoft\Windows\Maps\MapsUpdateTask"),
                new TaskSpec(@"\Microsoft\Windows\LanguageComponentsInstaller\Installation"),
                new TaskSpec(@"\Microsoft\Windows\LanguageComponentsInstaller\ReconcileLanguageResources"),
                new TaskSpec(@"\Microsoft\Windows\WinSAT\WinSAT"),
                new TaskSpec(@"\Microsoft\Windows\RemoteAssistance\RemoteAssistanceTask"),
                new TaskSpec(@"\Microsoft\Windows\Device Information\Device"),
                new TaskSpec(@"\Microsoft\Windows\SettingSync\BackgroundUploadTask"),
            ]),
    ];

    private readonly TweakBackupStore _backups;

    public AdvancedTweakService() : this(TweakBackupStore.Default)
    {
    }

    public AdvancedTweakService(TweakBackupStore backups)
    {
        _backups = backups;
    }

    // ---------------- 接口实现 ----------------

    public async Task<List<AdvancedTweakGroup>> GetGroupsAsync()
    {
        var taskStates = await QueryTaskStatesAsync();

        return Groups.Select(group =>
        {
            var detail = "";
            var isApplied = IsGroupApplied(group, taskStates, out detail);
            return new AdvancedTweakGroup
            {
                Id = group.Id,
                DisplayName = group.Name,
                Description = group.Desc,
                Category = group.Category,
                Risk = group.Risk,
                RiskNote = group.RiskNote,
                IsApplied = isApplied,
                Detail = detail,
            };
        }).ToList();
    }

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
