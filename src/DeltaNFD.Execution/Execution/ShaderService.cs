using System.Diagnostics;
using System.Management;
using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>
/// 一次 NVIDIA 驱动探测结果；除显示版本外保留 Windows 原始值、来源与适配器名，
/// 供界面同时显示两种格式并写入只读反馈诊断（避免“设备管理器/工具版本不一致”的误判）。
/// </summary>
internal sealed record ShaderDriverReading(
    ShaderGpuDetectionState State, string Version, string Raw = "", string Source = "", string Adapter = "");

/// <summary>着色器维护的真实实现。</summary>
public sealed class ShaderService : IShaderService
{
    private readonly GameTargetService _target;
    private readonly Func<ShaderDriverReading> _driverReader;
    private readonly Func<IEnumerable<string>> _psoPaths;
    private readonly Func<bool> _gameRunning;
    private readonly Func<string> _localAppData;
    public ShaderService(GameTargetService? target = null) : this(target, ReadNvidiaDriver) { }
    internal ShaderService(GameTargetService? target, Func<ShaderDriverReading> driverReader,
        Func<IEnumerable<string>>? psoPaths = null, Func<bool>? gameRunning = null, Func<string>? localAppData = null)
    {
        _target = target ?? GameTargetService.Default; _driverReader = driverReader;
        _psoPaths = psoPaths ?? (() => DeltaForceLocator.FindPsoCachePaths());
        _gameRunning = gameRunning ?? DeltaForceLocator.IsGameRunning;
        _localAppData = localAppData ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }
    private const string DisplayClassPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public Task<ShaderStatus> GetStatusAsync() => CreateInspection().GetStatusAsync();

    public Task<OperationResult> ClearPsoCacheAsync() => ClearPsoCacheAsync(CancellationToken.None);

    public Task<OperationResult> ClearPsoCacheAsync(CancellationToken token) => Task.Run(() =>
    {
        using var operation = _target.BeginOperation();
        if (_target.IsCustom) return OperationResult.Fail(GameTargetService.DeltaOnlyMessage);
        using var shaderGate = ShaderCacheOperationGate.Acquire();
        var driver = _driverReader();
        var includeSystem = ShaderCacheCleanupPolicy.IncludesSystemD3DCache(driver.State, driver.Version);
        var local = _localAppData();
        if (includeSystem && !Path.IsPathFullyQualified(local))
            return OperationResult.Fail("当前用户系统缓存目录无法确认，未执行清理。");
        return ClearPsoCache(_psoPaths(), _gameRunning, token,
            includeSystem ? Path.Combine(local, "D3DSCache") : null);
    });

    internal static OperationResult ClearPsoCache(IEnumerable<string> paths, Func<bool> gameRunning, CancellationToken token = default,
        string? systemD3DCacheRoot = null)
    {
        if (gameRunning())
        {
            return OperationResult.Fail("三角洲正在运行，PSOCache 文件被游戏锁定。请先完全退出游戏再清理。");
        }

        var psoPaths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (psoPaths.Count == 0 && systemD3DCacheRoot is null)
        {
            return OperationResult.Fail("未找到 PSOCache 目录（可能游戏尚未生成过着色器缓存，无需清理）。");
        }

        // 使用固定根安全清理：链接/重解析项跳过，游戏启动时立即停止，逐项记录失败。
        if (systemD3DCacheRoot is not null && !Path.IsPathFullyQualified(systemD3DCacheRoot))
            return OperationResult.Fail("系统 D3DSCache 路径不是可确认的绝对路径，未执行清理。");
        var report = ShaderCacheCleanup.CleanMixedRoots(psoPaths,
            systemD3DCacheRoot is null ? [] : new[] { systemD3DCacheRoot }, gameRunning, token);
        var scope = systemD3DCacheRoot is null ? "" : " 本次 NVIDIA 621.xx 联动范围包含当前用户系统 D3DSCache：" + systemD3DCacheRoot + "。";
        var summary = $"已检查 {psoPaths.Count} 处 PSOCache。{scope}{report.Message}";
        if (report.Skipped.Count > 0)
        {
            summary += " 部分目录/文件未完成：" + string.Join("；", report.Skipped.Take(8));
        }
        if (report.Partial)
            summary += "\n请按上述路径核对原因；占用时退出对应程序，权限不足时以管理员身份运行，然后再次清理。链接目录不会被强制清理。";
        summary += "\n下次进入游戏会重新编译着色器，首局可能出现卡顿 / 掉帧，属正常现象。";

        var success = !report.Partial;
        Log.Info($"PSOCache 清理：位置={string.Join("；", psoPaths)}；系统D3DSCache={systemD3DCacheRoot ?? "未附带"}；{report.Message}");
        foreach (var detail in report.Skipped.Take(12)) Log.Warn($"PSOCache 未完成：{detail}");
        return new OperationResult
        {
            Success = success || report.DeletedFiles > 0,
            IsPartial = !success && report.DeletedFiles > 0,
            Message = summary,
        };
    }

    // ---------------- N 卡驱动读取与判定 ----------------

    /// <summary>
    /// 读取 NVIDIA 驱动版本。优先枚举“当前存在”的显示适配器（WMI），避免把已拔掉/换卡的残留注册表条目
    /// 当成当前驱动；WMI 不可用时才回退注册表，并保留来源与原始值供反馈诊断。
    /// </summary>
    internal static ShaderDriverReading ReadNvidiaDriver()
    {
        var foundNvidiaWmi = false;
        var present = new List<(string Name, string Pnp, string Raw, string Version, bool Healthy)>();
        var wmiOk = false;
        var anyAdapter = false;
        var hadAdapterError = false;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID, DriverVersion, Status, ConfigManagerErrorCode FROM Win32_VideoController");
            using var adapters = searcher.Get();
            wmiOk = true;
            foreach (ManagementObject adapter in adapters)
            {
                using (adapter)
                {
                    anyAdapter = true;
                    try
                    {
                        var name = adapter["Name"] as string ?? "";
                        var pnp = adapter["PNPDeviceID"] as string ?? "";
                        if (!IsNvidiaAdapter(null, name, pnp)) continue;
                        foundNvidiaWmi = true;
                        var raw = adapter["DriverVersion"] as string ?? "";
                        if (!TryFormatDriverVersion(raw, out var version)) continue;
                        var healthy = string.Equals(adapter["Status"] as string, "OK", StringComparison.OrdinalIgnoreCase)
                            || Convert.ToInt32(adapter["ConfigManagerErrorCode"] ?? 0) == 0;
                        present.Add((name, pnp, raw, version, healthy));
                    }
                    catch (Exception ex)
                    {
                        hadAdapterError = true;
                        Log.Error("显卡检测：读取 WMI 显卡条目失败", ex);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("显卡检测：WMI 显卡枚举失败", ex);
        }

        if (present.Count > 0)
        {
            var chosen = present.Where(p => p.Healthy)
                .OrderByDescending(p => ToVersionNumber(p.Version) ?? 0)
                .FirstOrDefault();
            if (chosen == default)
                chosen = present.OrderByDescending(p => ToVersionNumber(p.Version) ?? 0).First();
            return LogDriverReading(new ShaderDriverReading(
                ShaderGpuDetectionState.Nvidia, chosen.Version, chosen.Raw, "WMI", chosen.Name));
        }

        // WMI 可用且明确列出了显示适配器、但没有 NVIDIA：不采用注册表里的残留/旧卡条目。
        if (wmiOk && anyAdapter && !hadAdapterError)
        {
            return foundNvidiaWmi
                ? LogDriverReading(new ShaderDriverReading(ShaderGpuDetectionState.Nvidia, "", "", "WMI", ""))
                : new ShaderDriverReading(ShaderGpuDetectionState.NotPresent, "");
        }

        var registry = ReadNvidiaFromRegistry(out var foundNvidiaRegistry);
        if (registry is not null)
            return LogDriverReading(new ShaderDriverReading(
                ShaderGpuDetectionState.Nvidia, registry.Value.Version, registry.Value.Raw, "注册表", registry.Value.Adapter));
        return foundNvidiaWmi || foundNvidiaRegistry
            ? new ShaderDriverReading(ShaderGpuDetectionState.Nvidia, "")
            : new ShaderDriverReading(ShaderGpuDetectionState.Unknown, "");
    }

    private static (string Version, string Raw, string Adapter)? ReadNvidiaFromRegistry(out bool foundNvidia)
    {
        foundNvidia = false;
        (string Version, string Raw, string Adapter)? best = null;
        var bestNumber = -1;
        try
        {
            using var displayClass = Registry.LocalMachine.OpenSubKey(DisplayClassPath);
            if (displayClass is null) return null;
            foreach (var subName in displayClass.GetSubKeyNames())
            {
                if (subName.Length != 4 || !subName.All(char.IsDigit)) continue;
                try
                {
                    using var key = displayClass.OpenSubKey(subName);
                    if (key is null || !IsNvidiaAdapter(
                        key.GetValue("ProviderName") as string,
                        key.GetValue("DriverDesc") as string,
                        key.GetValue("MatchingDeviceId") as string)) continue;
                    foundNvidia = true;
                    var raw = key.GetValue("DriverVersion") as string ?? "";
                    if (!TryFormatDriverVersion(raw, out var version)) continue;
                    var number = ToVersionNumber(version) ?? 0;
                    if (number <= bestNumber) continue;
                    bestNumber = number;
                    best = (version, raw, key.GetValue("DriverDesc") as string ?? subName);
                }
                catch (Exception ex)
                {
                    Log.Error($"显卡检测：读取显示适配器 {subName} 失败", ex);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("显卡检测：显示类注册表读取失败", ex);
        }
        return best;
    }

    private static readonly object DriverLogGate = new();
    private static string _lastDriverLogKey = "";

    /// <summary>同一组读数只记录一次，避免主页刷新时重复刷日志。</summary>
    private static ShaderDriverReading LogDriverReading(ShaderDriverReading reading)
    {
        var key = $"{reading.State}|{reading.Version}|{reading.Raw}|{reading.Source}|{reading.Adapter}";
        lock (DriverLogGate)
        {
            if (key == _lastDriverLogKey) return reading;
            _lastDriverLogKey = key;
        }
        Log.Info($"显卡检测：适配器={reading.Adapter}；原始版本={reading.Raw}；显示版本={reading.Version}；来源={reading.Source}；状态={reading.State}");
        return reading;
    }

    internal static bool IsNvidiaAdapter(string? provider, string? description, string? deviceId)
    {
        if (deviceId?.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase) == true) return true;
        if (deviceId?.Contains("VEN_", StringComparison.OrdinalIgnoreCase) == true) return false;
        return provider?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) == true ||
            description?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) == true;
    }

    internal static bool TryFormatDriverVersion(string? raw, out string version)
    {
        version = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length < 5 || !int.TryParse(digits[^5..^2], out var major) ||
            !int.TryParse(digits[^2..], out var minor)) return false;
        version = $"{major}.{minor:00}";
        return true;
    }

    private static (ShaderDriverState State, string Advice) BuildDriverAdvice(ShaderGpuDetectionState gpuState, string version) =>
        DeltaNFD.Inspection.ShaderInspection.BuildDriverAdvice(gpuState, version);

    // 状态、公开诊断与指定目录诊断共用只读实现及同一驱动快照来源。
    public Task<ShaderDiagnosis> DiagnoseAsync() => CreateInspection().DiagnoseAsync();

    internal ShaderDiagnosis DiagnoseAt(string psoPath) => CreateInspection().DiagnoseAt(psoPath);

    internal static string? FindDxCachePath(string gameVerDirectory) =>
        DeltaNFD.Inspection.ShaderInspection.FindDxCachePath(gameVerDirectory);

    private DeltaNFD.Inspection.ShaderInspection CreateInspection() => new(_target, () =>
    {
        var reading = _driverReader();
        return new(reading.State, reading.Version, reading.Raw, reading.Source, reading.Adapter);
    });

    /// <summary>"596.36" → 59636（与 MinDriverVersion 同构）；解析失败为 null。</summary>
    private static int? ToVersionNumber(string version)
    {
        var parts = version.Split('.');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
        {
            return null;
        }

        return major * 100 + minor;
    }

}
