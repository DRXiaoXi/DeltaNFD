using DeltaNFD.Services;

if (args.Contains("--gpu-spoof-watch-checks", StringComparer.OrdinalIgnoreCase))
{
    try { GpuSpoofWatchChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--dashboard-design-checks", StringComparer.OrdinalIgnoreCase))
{
    try { DashboardDesignChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--plugin-navigation-checks", StringComparer.OrdinalIgnoreCase))
{
    try { BackendSmokeTest.PluginNavigationChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--plugin-ui-wiring-checks", StringComparer.OrdinalIgnoreCase))
{
    try { PluginUiWiringChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--plugin-diagnostics-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await PluginDiagnosticsChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--shader-path-checks", StringComparer.OrdinalIgnoreCase))
{
    try { ShaderCachePathChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--plugin-test-delayed-exit", StringComparer.Ordinal))
{
    await Task.Delay(1800);
    return;
}

if (args.Contains("--plugin-integrity-checks", StringComparer.OrdinalIgnoreCase))
{
    try { BackendSmokeTest.PluginIntegrityChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--plugin-stability-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await PluginStabilityChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

// 插件运行时测试后端：宿主按规范只传 --dnfd-pipe/--dnfd-session/--dnfd-host-pid 三个标准参数，
// 因此这里按可执行文件名（PluginChild<Mode>.exe）识别，而不是额外命令行开关。
if (Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "")
    .StartsWith("PluginChild", StringComparison.OrdinalIgnoreCase))
{
    BackendSmokeTest.PluginRuntimeChild.Run();
    return;
}

if (args.Contains("--offline-helper-e2e-child", StringComparer.OrdinalIgnoreCase))
{
    OfflineHelperE2EChecks.RunChild();
    return;
}

if (args.Contains("--plugin-adversarial-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await BackendSmokeTest.PluginAdversarialChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--plugin-offline-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await BackendSmokeTest.PluginOfflineChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--plugin-continuous-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await BackendSmokeTest.PluginContinuousChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--plugin-restore-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await BackendSmokeTest.PluginRestoreChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--plugin-runtime-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await BackendSmokeTest.PluginRuntimeChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--plugin-manager-checks", StringComparer.OrdinalIgnoreCase))
{
    try { BackendSmokeTest.PluginManagerChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--plugin-import-checks", StringComparer.OrdinalIgnoreCase))
{
    try { BackendSmokeTest.PluginImportChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--plugin-contract-checks", StringComparer.OrdinalIgnoreCase))
{
    try { BackendSmokeTest.PluginContractChecks.Run(args); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--plugin-contract-e2e-checks", StringComparer.OrdinalIgnoreCase))
{
    try { BackendSmokeTest.PluginContractChecks.Run(args); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--offline-helper-e2e-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await OfflineHelperE2EChecks.RunAsync(args); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--offline-audit-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await OfflineAuditChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--offline-audit-child", StringComparer.OrdinalIgnoreCase))
{
    await Task.Delay(TimeSpan.FromSeconds(90));
    return;
}

if (args.Contains("--offline-cpu-set-native-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await OfflineCpuSetNativeChecks.RunIsolatedAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}
if (args.Contains("--offline-cpu-set-native-child", StringComparer.OrdinalIgnoreCase))
{
    try { OfflineCpuSetNativeChecks.RunCurrentProcess(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--offline-cpu-set-checks", StringComparer.OrdinalIgnoreCase))
{
    try { OfflineCpuSetCatalogChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--offline-state-checks", StringComparer.OrdinalIgnoreCase))
{
    try { OfflineModeStateChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--offline-cpu-plan-checks", StringComparer.OrdinalIgnoreCase))
{
    try { OfflineCpuSetPlannerChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--offline-task-ownership-checks", StringComparer.OrdinalIgnoreCase))
{
    try { OfflineTaskOwnershipChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--offline-timer-unit-checks", StringComparer.OrdinalIgnoreCase))
{
    try { OfflineTimerResolutionChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--frame-power-safety-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await FramePowerSafetyChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--runtime-installer-fixture", StringComparer.OrdinalIgnoreCase))
{
    var logIndex = Array.FindIndex(args, a => a.Equals("/L*V", StringComparison.OrdinalIgnoreCase));
    var packageIndex = Array.FindIndex(args, a => a.Equals("/i", StringComparison.OrdinalIgnoreCase));
    if (logIndex < 0 || packageIndex < 0) { Environment.ExitCode = 2; return; }
    var path = Path.GetFullPath(args[logIndex + 1]);
    var prefix = Path.Combine(Path.GetTempPath(), "DeltaNFD_RepairChecks_");
    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { Environment.ExitCode = 2; return; }
    var fail = args[packageIndex + 1].Contains("2005", StringComparison.OrdinalIgnoreCase);
    File.WriteAllText(path, fail ? "Error 1935 HRESULT: 0x80070005" : "MainEngineThread is returning 3010", System.Text.Encoding.Unicode);
    Environment.ExitCode = fail ? 1603 : 3010;
    return;
}

if (args.Contains("--runtime-repair-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await RuntimeRepairChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--frame-feature-checks", StringComparer.OrdinalIgnoreCase))
{
    try { FrameFeatureChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--runtime-guard-recovery-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await RuntimeGuardRecoveryChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--game-target-test-child"))
{
    using var child = args.Length > 1 ? System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(args[1], "--game-target-test-child")
        { UseShellExecute = false, CreateNoWindow = true }) : null;
    if (child is not null) Console.WriteLine(child.Id);
    await Task.Delay(TimeSpan.FromSeconds(60));
    if (child is not null && !child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
    return;
}
if (args.Contains("--game-target-checks"))
{
    try { await GameTargetChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

﻿// 只读冒烟测试：只枚举显卡、读取优化项状态、查询 VBS 运行时。
// 绝不调用 SpoofAsync / RestoreAsync / DisableAsync —— 那些会真实改动系统。
if (args.Contains("--affinity-test-child", StringComparer.OrdinalIgnoreCase))
{
    await Task.Delay(TimeSpan.FromSeconds(60));
    return;
}

if (args.Contains("--affinity-reliability-checks", StringComparer.OrdinalIgnoreCase))
{
    try { AffinityReliabilityChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--appdata-migration-checks", StringComparer.OrdinalIgnoreCase))
{
    try { AppDataMigrationChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--update-checks", StringComparer.OrdinalIgnoreCase))
{
    try { UpdateChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--update-live", StringComparer.OrdinalIgnoreCase))
{
    try { await UpdateChecksLive.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--power-scheme-checks", StringComparer.OrdinalIgnoreCase))
{
    try
    {
        var powerSchemes = await new PowerService().GetSchemesAsync();
        var active = powerSchemes.SingleOrDefault(s => s.IsActive);
        if (active is null)
            throw new Exception("未能从 powercfg 列表识别当前电源计划。");
        Console.WriteLine($"电源计划只读检查通过：{powerSchemes.Count} 条，当前「{active.Name}」（{active.Guid}）");
    }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--runtime-guard-safety-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await RuntimeGuardSafetyChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--ace-guard-checks", StringComparer.OrdinalIgnoreCase))
{
    try { await AceGuardChecks.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--hardware-only", StringComparer.OrdinalIgnoreCase))
{
    await HardwareDetectionChecks.RunAsync();
    return;
}

if (args.Contains("--cpu-plan-checks", StringComparer.OrdinalIgnoreCase))
{
    try { CpuPlanChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--settings-cache-checks", StringComparer.OrdinalIgnoreCase))
{
    try { SettingsCacheChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--recovery-checks", StringComparer.OrdinalIgnoreCase))
{
    try
    {
        OptimizationRecoveryChecks.Run();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("系统优化恢复检查失败：" + ex);
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Contains("--phase1-checks", StringComparer.OrdinalIgnoreCase))
{
    try { SystemOptimizePhaseOneChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--phase1-live", StringComparer.OrdinalIgnoreCase))
{
    await SystemOptimizePhaseOneChecks.RunLiveAsync();
    return;
}

if (args.Contains("--phase23-checks", StringComparer.OrdinalIgnoreCase))
{
    try { SystemOptimizePhaseTwoThreeChecks.Run(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--recovery-checks-no-registry", StringComparer.OrdinalIgnoreCase))
{
    try { OptimizationRecoveryChecks.Run(includeRegistry: false); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

DeltaNFD.Services.Log.Startup();
DeltaNFD.Services.Log.Info("冒烟测试启动");
Console.WriteLine("=== Delta NFD 后端只读冒烟测试 ===");
Console.WriteLine($"管理员权限：{(ElevationHelper.IsElevated ? "是" : "否")}");
Console.WriteLine();

var spoof = new GpuSpoofService();
var adapters = await spoof.GetAdaptersAsync();
Console.WriteLine($"检测到 {adapters.Count} 个显示适配器：");
foreach (var adapter in adapters)
{
    Console.WriteLine($"  · {adapter.DisplayName}");
    Console.WriteLine($"      键：HKLM\\{adapter.RegistryPath}");
    Console.WriteLine($"      DeviceDesc：{adapter.DeviceDesc}");
    Console.WriteLine($"      已伪装：{(adapter.IsMasked ? "是" : "否")}（本工具备份：{(adapter.HasBackup ? "有" : "无")}）");
}

Console.WriteLine();
Console.WriteLine($"预置伪装型号：{string.Join("  ", spoof.PresetNames)}");
Console.WriteLine();

var tweaks = new SystemTweakService();
Console.WriteLine("系统优化项状态（IsOptimized=已关闭该项功能）：");
foreach (var status in await tweaks.GetStatusesAsync())
{
    Console.WriteLine($"  [{(status.IsOptimized ? "已优化" : "未优化")}] {status.DisplayName} —— {status.Detail}");
}

Console.WriteLine();
Console.WriteLine($"VBS/HVCI 运行时：{await tweaks.GetVbsRuntimeSummaryAsync()}");
Console.WriteLine();

// 电源计划（只读查询）
var power = new PowerService();
var schemes = await power.GetSchemesAsync();
Console.WriteLine($"电源计划（{schemes.Count} 个）：");
foreach (var scheme in schemes)
{
    Console.WriteLine($"  [{(scheme.IsActive ? "当前" : "    ")}] {scheme.Name} ({scheme.Guid})");
}
Console.WriteLine();

// 系统清理清单体积扫描（只读，不删除）
var cleanup = new CleanupService();
var targets = await cleanup.ScanAsync();
Console.WriteLine($"清理清单扫描（共 {targets.Sum(t => t.SizeMb) / 1024.0:0.00} GB）：");
foreach (var target in targets)
{
    Console.WriteLine($"  · {target.Name}: {(target.SizeMb >= 1024 ? $"{target.SizeMb / 1024.0:0.00} GB" : $"{target.SizeMb:0.0} MB")}");
}
Console.WriteLine();

// 高危分组状态（只读查询；计划任务状态走 PowerShell 查询，可能需要几秒）
var advanced = new AdvancedTweakService();
var groups = await advanced.GetGroupsAsync();
Console.WriteLine($"高级优化分组（{groups.Count} 组）：");
foreach (var group in groups)
{
    Console.WriteLine($"  [{(group.IsApplied ? "已应用" : "未应用")}] {group.DisplayName}（{group.Risk}）{group.Detail}");
}
Console.WriteLine();

// 运行库保护状态（只读：已装 VC++ 运行库版本 / UE4Prereq 定位 / IFEO 现状）
var guard = new RuntimeGuardService();
var guardStatus = await guard.GetStatusAsync();
Console.WriteLine($"运行库保护状态：IFEO {guardStatus.IfeoCount}/{guardStatus.IfeoTotal}，UE4Prereq {(guardStatus.Ue4PrereqFound ? $"已找到({guardStatus.Ue4PrereqPath})，{(guardStatus.Ue4PrereqDenied ? "已拒绝执行" : "未拦截")}" : "未找到")}");
foreach (var redist in guardStatus.InstalledRedists)
{
    Console.WriteLine($"  · {redist.DisplayName} [{redist.Architecture}] v{redist.Version}");
}
Console.WriteLine();

// 着色器维护状态（只读：驱动体检 + PSOCache 体积 + 健康检测，不删除）
var shader = new ShaderService();
var shaderStatus = await shader.GetStatusAsync();
Console.WriteLine($"着色器维护状态：{(shaderStatus.IsNvidia ? $"N 卡 {shaderStatus.DriverVersion}" : "非 N 卡")}");
Console.WriteLine($"  驱动体检：{shaderStatus.DriverAdvice}");
Console.WriteLine($"  PSOCache：{(shaderStatus.PsoCachePath.Length > 0 ? $"{shaderStatus.PsoCachePath}（{shaderStatus.PsoCacheSizeMb:0.0} MB）" : "未找到")}，游戏运行中={shaderStatus.IsGameRunning}");
var shaderDiag = await shader.DiagnoseAsync();
Console.WriteLine($"  健康检测[{shaderDiag.Level}]：{shaderDiag.Summary}");
foreach (var d in shaderDiag.Details)
{
    Console.WriteLine("      " + d);
}
Console.WriteLine($"      建议：{shaderDiag.Advice}");
Console.WriteLine();

// ACE 组件扫描（只读，不删除）
var ace = new AceService();
var aceScan = await ace.ScanAsync();
Console.WriteLine($"ACE 扫描：{(aceScan.CanClean ? "无阻止进程" : $"被阻断（{string.Join("、", aceScan.BlockingProcesses)}）")} + " +
                  $"服务 {aceScan.Services.Count(s => s.Exists)}/6，目录 {aceScan.Paths.Count(p => p.Exists)}，注册表残留 {aceScan.RegistryKeys.Count}");
var aceCore = await ace.CheckCoreFilesAsync();
Console.WriteLine($"  ACE-CORE 检测[{aceCore.Level}]：{aceCore.Summary}（{aceCore.SysCount} 个）");
foreach (var f in aceCore.Files) { Console.WriteLine("      " + f); }
var vcScan = await guard.ScanVcRedistsAsync();
Console.WriteLine($"v14 伪装运行库：{vcScan.Abnormal.Count} 个");
foreach (var e in vcScan.Abnormal) { Console.WriteLine($"      [异常] {e.Architecture} {e.DisplayVersion} {e.DisplayName}"); }
Console.WriteLine($"非最适版本运行库：{vcScan.Suboptimal.Count} 项");
foreach (var s in vcScan.Suboptimal) { Console.WriteLine($"      [非最适] {s.Branch}：{(string.IsNullOrEmpty(s.InstalledVersion) ? "未安装" : s.InstalledVersion)}（推荐 ≥ {s.RecommendedVersion}）"); }
Console.WriteLine($"      建议：{vcScan.Advice}");
var v14Entries = await guard.GetV14EntriesAsync();
Console.WriteLine($"V14 分支条目（单独卸载目标）：{v14Entries.Count} 个");
foreach (var e in v14Entries) { Console.WriteLine($"      [V14] {e.Architecture} {e.DisplayVersion} [{e.UninstallKind}] {e.DisplayName}"); }

Console.WriteLine();
// CPU 拓扑检测（只读）
var cpu = new CpuTopologyService();
var topology = await cpu.GetTopologyAsync();
Console.WriteLine($"CPU 拓扑：{topology.Vendor} · {topology.CpuName}");
Console.WriteLine($"  物理核心 {topology.PhysicalCores} · 逻辑处理器 {topology.LogicalProcessors} · 处理器组 {topology.GroupCount}");
Console.WriteLine($"  大小核：{(topology.IsHybrid ? $"是（P {topology.PCoreCount} + E {topology.ECoreCount}）" : "否")} · 超线程：{(topology.HasHyperThreading ? "已启用" : "未启用")}");
if (topology.IsAmd)
{
    Console.WriteLine($"  CCD 数：{topology.CcdCount}");
    foreach (var ccd in topology.Ccds)
    {
        Console.WriteLine($"    · CCD{ccd.Index}：{ccd.CoreCount} 核，L3 {ccd.L3CacheMb:0} MB（掩码 0x{ccd.Mask:X}）");
    }

    Console.WriteLine($"  结论：{topology.CcdAdvice}");
}
Console.WriteLine();

// NVIDIA 驱动设置服务（只读：环境 + 三角洲配置的覆盖状态）
var nv = new NvProfileService();
var nvEnv = await nv.GetEnvironmentAsync();
Console.WriteLine($"NVIDIA 驱动设置环境：N卡={(nvEnv.IsNvidia ? nvEnv.GpuName : "无")} · NVAPI={(nvEnv.NvApiAvailable ? "可用" : "不可用")} · nvdrsdb锁定={(nvEnv.DrsFilesLocked ? "是" : "否")}");
if (nvEnv.IsNvidia && nvEnv.NvApiAvailable)
{
    var nvValues = await nv.GetGameSettingValuesAsync();
    if (nvValues is null)
    {
        Console.WriteLine("  三角洲驱动配置读取失败");
    }
    else
    {
        foreach (var def in nv.GetSettingCatalog())
        {
            var state = nvValues.TryGetValue(def.Id, out var s) ? s : (HasOverride: false, Value: 0u);
            Console.WriteLine($"  · {def.Title}（0x{def.Id:X8}）：{(state.HasOverride ? $"已覆盖 0x{state.Value:X}" : "默认")}");
        }
    }
}
Console.WriteLine();
Console.WriteLine("（本次只做了读取，未执行任何伪装/关闭/清理/杀进程操作）");

// DefenderGuard 预检状态（只读：Defender 在位状态 + 第三方杀软清单；批次含安全类条目时的引导依据）
Console.WriteLine();
Console.WriteLine("=== 内存清理（待备列表）原语（只读验证，不执行清理） ===");
{
    var mem = DeltaNFD.Native.MemoryPurge.QueryMemory();
    Console.WriteLine($"  物理内存总量：{DeltaNFD.Native.MemoryPurge.FormatBytes(mem.TotalPhys)} · 可用：{DeltaNFD.Native.MemoryPurge.FormatBytes(mem.AvailPhys)}（{mem.AvailRatio:P1}） · 负载 {mem.LoadPercent}%");
    Console.WriteLine("  （PurgeStandbyList 会真实清空待备内存列表，属写操作，冒烟测试不执行）");
}

Console.WriteLine();
Console.WriteLine("=== DefenderGuard 预检状态（只读） ===");
{
    var guardReport = await DefenderGuard.PreflightAsync(new List<(string, string)>
    {
        ("模拟条目", "REG HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows Defender DisableAntiSpyware"),
    });
    if (guardReport is null)
    {
        Console.WriteLine("  预检未触发（配置缺失或无命中）");
    }
    else
    {
        Console.WriteLine($"  Defender：{(guardReport.DefenderPresent ? "在" : "无")} · 实时保护：{(guardReport.InitialRealTimeProtectionOn ? "开" : "关")} · 篡改防护：{(guardReport.TamperProtectionOn ? "开" : "关")}");
        Console.WriteLine($"  第三方杀软：{(guardReport.ThirdPartyAvNames.Count > 0 ? string.Join("、", guardReport.ThirdPartyAvNames) : "无")}");
        Console.WriteLine($"  命中条目：{string.Join("、", guardReport.SecurityItemNames)}（需引导={guardReport.NeedsGuidance}）");
    }
}

// 加速系统响应模式原语只读验证（PDH 停靠计数轮询 3 秒 + 定时器分辨率读取）
Console.WriteLine();
Console.WriteLine("=== 加速系统响应模式原语（只读验证） ===");
Console.WriteLine($"当前系统定时器分辨率：{DeltaNFD.Native.ProcessPerf.QueryTimerResolutionMs():0.###} ms");
var loop = new DeltaNFD.Native.CoreParkingLoop();
int? lastParked = null;
var observed = 0;
loop.Start(p =>
{
    if (p != lastParked)
    {
        lastParked = p;
        observed++;
        Console.WriteLine($"  停靠核心数：{p}");
    }
});
await Task.Delay(3000);
loop.Stop();
Console.WriteLine($"核心常驻轮询环运行 3 秒，停靠计数变化 {observed} 次（0 次说明停靠数稳定，机制仍在轮询）");
Console.WriteLine();

// CPU 集（软亲和性）自检：对本测试进程设置默认集 → 读回 → 还原为全部核心
Console.WriteLine("=== CPU 集（软亲和性）原语自检（本进程，结束前还原） ===");
{
    var self = System.Diagnostics.Process.GetCurrentProcess();
    var testMask = 0xFUL; // CPU0–3
    var setOk = DeltaNFD.Native.CpuSets.SetDefaultByMask(self.Handle, testMask);
    var getOk = DeltaNFD.Native.CpuSets.TryGetDefaultMask(self.Handle, out var gotMask);
    Console.WriteLine($"  设置默认集 0x{testMask:X}：{(setOk ? "成功" : "失败")}；读回：{(getOk ? $"0x{gotMask:X}" : "无")}（{(getOk && gotMask == testMask ? "一致 ✓" : "不一致 ✗")}）");
    var restoreMask = (1UL << Environment.ProcessorCount) - 1;
    var restoreOk = DeltaNFD.Native.CpuSets.SetDefaultByMask(self.Handle, restoreMask);
    Console.WriteLine($"  还原为全部核心 0x{restoreMask:X}：{(restoreOk ? "成功" : "失败")}");
}
Console.WriteLine();
Console.WriteLine("（加速响应验证也全部只读，未修改任何电源/进程设置）");
