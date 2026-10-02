using System.Diagnostics;
using System.Management;
using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>着色器维护的真实实现。</summary>
public sealed class ShaderService : IShaderService
{
    private readonly GameTargetService _target;
    private readonly Func<(ShaderGpuDetectionState State, string Version)> _driverReader;
    public ShaderService(GameTargetService? target = null) : this(target, ReadNvidiaDriver) { }
    internal ShaderService(GameTargetService? target, Func<(ShaderGpuDetectionState State, string Version)> driverReader)
    { _target = target ?? GameTargetService.Default; _driverReader = driverReader; }
    private const string DisplayClassPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>问题驱动系列（591 / 610 / 616）：存在着色器文件缺失问题。</summary>
    private static readonly int[] ProblemDriverMajors = [591, 610, 616];

    /// <summary>最低可用驱动（低于 572.83 为过老驱动）。</summary>
    private const int MinDriverVersion = 57283;

    /// <summary>617.14：已确认的问题驱动基线；之后的新版本按缓存缺失特征判断。</summary>
    private const int Driver61714Version = 61714;

    /// <summary>临时通用判据：PSOCache 中 GameVer 目录达到此数量即判为残留异常，不依赖显卡厂商。</summary>
    private const int GameVerAbnormalThreshold = 2;

    public Task<ShaderStatus> GetStatusAsync() => Task.Run(() =>
    {
        using var operation = _target.BeginOperation();
        if (_target.IsCustom) return new ShaderStatus
        {
            IsNvidia = false, DriverState = ShaderDriverState.NotApplicable,
            DriverAdvice = GameTargetService.DeltaOnlyMessage, IsGameRunning = false,
        };
        var (gpuState, driverVersion) = _driverReader();
        var isNvidia = gpuState == ShaderGpuDetectionState.Nvidia;

        double psoSizeMb = 0;
        var psoPath = DeltaForceLocator.FindPsoCachePath();
        if (psoPath.Length > 0)
        {
            psoSizeMb = GetDirectorySizeMb(psoPath);
        }

        var (state, advice) = BuildDriverAdvice(gpuState, driverVersion);
        if (HasProblemDriverCacheSignature(gpuState, driverVersion, psoPath))
        {
            state = ShaderDriverState.ProblemSeries;
            var versionNumber = ToVersionNumber(driverVersion);
            var reason = versionNumber == Driver61714Version
                ? "617.14 与 591 / 610 / 616 系列按同一问题驱动规则处理"
                : "驱动高于 617.14，且单版本缓存中 ≥256MB NVPH 文件少于两个";
            advice = $"⚠ 问题驱动（{driverVersion}）：{reason}。"
                + "建议更换为其他版本驱动，并一键清除旧着色器文件后重新编译。";
        }

        return new ShaderStatus
        {
            IsNvidia = isNvidia,
            GpuDetectionState = gpuState,
            DriverVersion = driverVersion,
            DriverState = state,
            DriverAdvice = advice,
            PsoCachePath = psoPath,
            PsoCacheSizeMb = Math.Round(psoSizeMb, 1),
            IsGameRunning = DeltaForceLocator.IsGameRunning(),
        };
    });

    public Task<OperationResult> ClearPsoCacheAsync() => Task.Run(() =>
    {
        using var operation = _target.BeginOperation();
        if (_target.IsCustom) return OperationResult.Fail(GameTargetService.DeltaOnlyMessage);
        if (DeltaForceLocator.IsGameRunning())
        {
            return OperationResult.Fail("三角洲正在运行，PSOCache 文件被游戏锁定。请先完全退出游戏再清理。");
        }

        var psoPath = DeltaForceLocator.FindPsoCachePath();
        if (psoPath.Length == 0 || !Directory.Exists(psoPath))
        {
            return OperationResult.Fail("未找到 PSOCache 目录（可能游戏尚未生成过着色器缓存，无需清理）。");
        }

        try
        {
            var before = GetDirectorySizeMb(psoPath);
            var failed = 0;

            foreach (var file in Directory.EnumerateFiles(psoPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    failed++;
                }
            }

            foreach (var dir in Directory.EnumerateDirectories(psoPath).OrderByDescending(d => d.Length))
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch
                {
                    // 非空或被占用则保留
                }
            }

            var freedMb = Math.Max(0, before - GetDirectorySizeMb(psoPath));
            var failNote = failed > 0 ? $"（{failed} 个文件被占用已跳过）" : "";
            return OperationResult.Ok(
                $"已清除旧着色器缓存，释放 {freedMb:0.0} MB{failNote}。下次进入游戏会重新编译着色器，" +
                "首局可能出现卡顿 / 掉帧，属正常现象。");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"清理着色器缓存失败：{ex.Message}");
        }
    });

    // ---------------- N 卡驱动读取与判定 ----------------

    /// <summary>从显示适配器注册表和当前显卡枚举读取 NVIDIA 驱动版本。</summary>
    private static (ShaderGpuDetectionState State, string Version) ReadNvidiaDriver()
    {
        var foundNvidia = false;
        try
        {
            using var displayClass = Registry.LocalMachine.OpenSubKey(DisplayClassPath);
            if (displayClass is not null)
            {
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
                        if (TryFormatDriverVersion(key.GetValue("DriverVersion") as string, out var version))
                            return (ShaderGpuDetectionState.Nvidia, version);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"显卡检测：读取显示适配器 {subName} 失败", ex);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("显卡检测：显示类注册表读取失败", ex);
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID, DriverVersion FROM Win32_VideoController");
            using var adapters = searcher.Get();
            var anyAdapter = false;
            var hadAdapterError = false;
            foreach (ManagementObject adapter in adapters)
            {
                using (adapter)
                {
                    anyAdapter = true;
                    try
                    {
                        if (!IsNvidiaAdapter(null, adapter["Name"] as string, adapter["PNPDeviceID"] as string))
                            continue;
                        foundNvidia = true;
                        if (TryFormatDriverVersion(adapter["DriverVersion"] as string, out var version))
                            return (ShaderGpuDetectionState.Nvidia, version);
                    }
                    catch (Exception ex)
                    {
                        hadAdapterError = true;
                        Log.Error("显卡检测：读取 WMI 显卡条目失败", ex);
                    }
                }
            }
            if (anyAdapter && !hadAdapterError && !foundNvidia)
                return (ShaderGpuDetectionState.NotPresent, "");
        }
        catch (Exception ex)
        {
            Log.Error("显卡检测：WMI 显卡枚举失败", ex);
        }

        return foundNvidia
            ? (ShaderGpuDetectionState.Nvidia, "")
            : (ShaderGpuDetectionState.Unknown, "");
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

    private static (ShaderDriverState State, string Advice) BuildDriverAdvice(ShaderGpuDetectionState gpuState, string version)
    {
        if (gpuState == ShaderGpuDetectionState.Unknown)
        {
            return (ShaderDriverState.NotApplicable, "显卡信息读取失败，暂无法判定 N 卡驱动状态。");
        }
        if (gpuState == ShaderGpuDetectionState.NotPresent)
        {
            return (ShaderDriverState.NotApplicable, "未检测到 N 卡，无需驱动适配检查（AMD / Intel 显卡可忽略本项）。");
        }

        // "572.83" → 57283（与 WMI 末 5 位同构），便于比较
        var parts = version.Split('.');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
        {
            return (ShaderDriverState.NotApplicable, "驱动版本读取失败，请通过 GeForce App / nvidia.cn 确认驱动版本。");
        }

        var versionNumber = major * 100 + minor;

        if (versionNumber < MinDriverVersion)
        {
            return (ShaderDriverState.TooOld,
                $"⚠ 驱动过老（{version}）：着色器渲染使用旧方法，不适合现在的三角洲。" +
                "建议升级到 572.83 或更新的驱动，并在升级后一键清除旧着色器文件重新编译。");
        }

        if (ProblemDriverMajors.Contains(major))
        {
            return (ShaderDriverState.ProblemSeries,
                $"⚠ 问题驱动（{version}）：{major} 系驱动存在着色器文件缺失问题，可能导致游戏中着色器异常。" +
                "建议更换为其他版本驱动，并一键清除旧着色器文件后重新编译。");
        }

        return (ShaderDriverState.Ok, $"✓ 驱动状态正常（{version}），无已知着色器兼容问题。");
    }

    // ---------------- PSOCache 健康检测 ----------------

    /// <summary>NVPH 正常文件的大小下限：256MB（256 * 1024 * 1024 字节）。</summary>
    private const long NvphMinBytes = 256L * 1024 * 1024;

    /// <summary>正常状态下 ≥256MB 的 NVPH 文件至少应有的数量。</summary>
    private const int NvphMinLargeCount = 2;

    public Task<ShaderDiagnosis> DiagnoseAsync() => Task.Run(() =>
    {
        using var operation = _target.BeginOperation();
        return _target.IsCustom
            ? new ShaderDiagnosis { Level = ShaderDiagLevel.NotApplicable, Summary = GameTargetService.DeltaOnlyMessage, Details = [], Advice = "" }
            : DiagnoseAt(DeltaForceLocator.FindPsoCachePath());
    });

    /// <summary>
    /// 对指定 PSOCache 目录做着色器健康诊断。
    /// 抽成独立方法是为了能在没有安装游戏时用临时目录做回归测试（NVPH 判定）。
    /// </summary>
    internal ShaderDiagnosis DiagnoseAt(string psoPath)
    {
        if (psoPath.Length == 0 || !Directory.Exists(psoPath))
        {
            return new ShaderDiagnosis
            {
                Level = ShaderDiagLevel.NotApplicable,
                Summary = "未找到 PSOCache 目录（游戏尚未安装或未生成过着色器缓存）。",
                Details = new List<string>(),
                Advice = "进入一次游戏让着色器系统初始化后再来检测。",
            };
        }

        // 一级子目录 = GameVer 文件夹（命名：着色器版本号_玩家显卡名_玩家显卡驱动版本）
        var gameVerDirs = Directory.GetDirectories(psoPath);
        var details = new List<string>
        {
            $"GameVer 文件夹：{gameVerDirs.Length} 个",
            $"临时通用判定：GameVer 达到 {GameVerAbnormalThreshold} 个及以上即判异常；AMD / Intel / NVIDIA 均适用。",
        };
        foreach (var dir in gameVerDirs)
        {
            details.Add("· " + Path.GetFileName(dir));
        }

        // GameVer = 0：尚未生成
        if (gameVerDirs.Length == 0)
        {
            return new ShaderDiagnosis
            {
                Level = ShaderDiagLevel.Info,
                Summary = "PSOCache 下还没有 GameVer 文件夹（游戏尚未生成着色器缓存）。",
                Details = details,
                Advice = "进入游戏完成一次着色器预热后再来检测。",
            };
        }

        // 临时通用判定：GameVer 达到阈值即判多版本残留，不依赖 NVIDIA 驱动 / NVPH 结构；AMD、Intel 同样适用。
        if (gameVerDirs.Length >= GameVerAbnormalThreshold)
        {
            Log.Info($"着色器临时通用判定：GameVer={gameVerDirs.Length}，阈值={GameVerAbnormalThreshold}；判定异常（AMD/Intel/NVIDIA 通用）");
            return new ShaderDiagnosis
            {
                Level = ShaderDiagLevel.Abnormal,
                Summary = $"临时通用判定为着色器异常：检测到 {gameVerDirs.Length} 个 GameVer 版本文件夹，达到异常阈值（≥{GameVerAbnormalThreshold}；AMD / Intel / NVIDIA 通用）。",
                Details = details,
                Advice = "建议：一键清除旧着色器文件，然后进入游戏重新预热着色器。",
            };
        }

        // GameVer = 1：进入 NVPH 深度检测（仅 N 卡 + 驱动 ≥572.83）
        var (gpuState, driverVersion) = _driverReader();
        if (gpuState != ShaderGpuDetectionState.Nvidia)
        {
            if (gpuState == ShaderGpuDetectionState.NotPresent)
            {
                Log.Info($"着色器临时通用判定：GameVer={gameVerDirs.Length}，阈值={GameVerAbnormalThreshold}；非 NVIDIA 显卡未达到异常阈值，NVPH 深度判定不适用");
                return new ShaderDiagnosis
                {
                    Level = ShaderDiagLevel.Info,
                    Summary = $"临时通用判定：当前检测到 {gameVerDirs.Length} 个 GameVer 版本文件夹，未达到异常阈值（≥{GameVerAbnormalThreshold}）；AMD / Intel 等非 NVIDIA 显卡暂按此规则判定。",
                    Details = details,
                    Advice = "当前未达到异常阈值。NVPH 文件结构深度检测仅面向 NVIDIA 显卡。",
                };
            }

            return new ShaderDiagnosis
            {
                Level = ShaderDiagLevel.NotApplicable,
                Summary = "显卡信息读取失败，暂无法判定 NVPH 结构。",
                Details = details,
                Advice = "请检查显卡驱动和设备管理器后重新检测。",
            };
        }

        // ---------- NVPH 结构判定依赖驱动版本 ----------
        var dxCache = FindDxCachePath(gameVerDirs[0]);
        if (dxCache is null)
        {
            return new ShaderDiagnosis
            {
                Level = ShaderDiagLevel.Info,
                Summary = "未找到 SM6\\DXCache 或 SM5\\DXCache 目录（尚未生成可检测的缓存）。",
                Details = details,
                Advice = "进入游戏完成一次着色器预热后再来检测。",
            };
        }
        details.Add("检测路径：" + Path.GetFileName(Path.GetDirectoryName(dxCache)) + "\\DXCache");

        if (string.IsNullOrEmpty(driverVersion))
        {
            return new ShaderDiagnosis
            {
                Level = ShaderDiagLevel.NotApplicable,
                Summary = "已检测到 N 卡，但驱动版本读取失败，暂无法判定 NVPH 结构。",
                Details = details,
                Advice = "请通过 GeForce App 确认驱动版本后重新检测。",
            };
        }

        var versionNumber = ToVersionNumber(driverVersion);
        if (versionNumber is null || versionNumber < MinDriverVersion)
        {
            return new ShaderDiagnosis
            {
                Level = ShaderDiagLevel.NotApplicable,
                Summary = $"驱动 {driverVersion} 低于 572.83，旧版驱动的 NVPH 结构不同，拒绝判定。",
                Details = details,
                Advice = "建议先升级到 572.83 或更新驱动，然后一键清除旧着色器文件并进游戏重新预热。",
            };
        }

        details.Add($"驱动：{driverVersion}");

        var nvphFiles = new DirectoryInfo(dxCache)
            .EnumerateFiles("*.nvph", SearchOption.TopDirectoryOnly)
            .Select(f => { try { return (Name: f.Name, Size: f.Length); } catch { return (Name: f.Name, Size: -1L); } })
            .Where(f => f.Size >= 0)
            .OrderByDescending(f => f.Size)
            .ToList();

        if (nvphFiles.Count == 0)
        {
            return new ShaderDiagnosis
            {
                Level = ShaderDiagLevel.Info,
                Summary = "DXCache 中未找到 NVPH 文件（尚未预热或刚被清理）。",
                Details = details,
                Advice = "进入游戏完成一次着色器预热后再来检测。",
            };
        }

        foreach (var f in nvphFiles)
        {
            details.Add($"· {f.Name}（{f.Size / 1024.0 / 1024.0:0.0} MB）");
        }

        var zeroFiles = nvphFiles.Where(f => f.Size == 0).ToList();
        var largeFiles = nvphFiles.Where(f => f.Size >= NvphMinBytes).ToList();
        var missingLargeFileProblemDriver = versionNumber.Value > Driver61714Version
            && largeFiles.Count < NvphMinLargeCount;
        if (missingLargeFileProblemDriver)
        {
            details.Add($"硬性驱动判定：驱动高于 617.14，且单版本缓存中 ≥256MB NVPH 文件不足 {NvphMinLargeCount} 个（当前 {largeFiles.Count} 个）。");
        }

        // 判定异常的三类特征：0KB 损坏文件 / ≥256MB 大文件不足两个 / ≥256MB 大文件达到 3 个及以上（缓存膨胀异常）
        string reason;
        if (zeroFiles.Count > 0)
        {
            reason = $"存在 {zeroFiles.Count} 个 0 KB 的损坏 NVPH 文件（{string.Join("、", zeroFiles.Take(3).Select(f => f.Name))}）";
        }
        else if (largeFiles.Count >= 3)
        {
            reason = $"检测到 {largeFiles.Count} 个 ≥256MB 的 NVPH 缓存文件（正常应为 2 个：256MB+256MB 或 256MB+更大），缓存出现膨胀异常";
        }
        else if (largeFiles.Count < NvphMinLargeCount)
        {
            reason = $"预期至少 {NvphMinLargeCount} 个 ≥256MB 的 NVPH 缓存文件（常见 256MB+256MB 或 256MB+更大），实际只找到 {largeFiles.Count} 个";
        }
        else
        {
            return new ShaderDiagnosis
            {
                Level = ShaderDiagLevel.Normal,
                Summary = $"着色器缓存结构正常（{largeFiles.Count} 个 ≥256MB 的 NVPH 文件，共 {nvphFiles.Count} 个 NVPH）。",
                Details = details,
                Advice = "无需处理。若游戏中仍出现着色器异常，可尝试一键清除旧着色器文件后重新预热。",
            };
        }

        // 异常确认后再分支驱动系列：问题驱动 = 先换驱动；正常驱动 = 直接清理预热
        var major = versionNumber.Value / 100;
        var is61714ProblemDriver = versionNumber.Value == Driver61714Version;
        if (ProblemDriverMajors.Contains(major) || is61714ProblemDriver || missingLargeFileProblemDriver)
        {
            var driverIssue = ProblemDriverMajors.Contains(major)
                ? $"属于问题系列（{major} 系）"
                : is61714ProblemDriver
                    ? "617.14 属于与 591 / 610 / 616 系列相同的问题驱动"
                    : $"高于 617.14 且达到缺失判定（≥256MB NVPH 文件仅 {largeFiles.Count} 个，预期至少 {NvphMinLargeCount} 个）";
            return new ShaderDiagnosis
            {
                Level = ShaderDiagLevel.AbnormalDriverIssue,
                Summary = $"判定为着色器异常：{reason}。你的驱动 {driverVersion}{driverIssue}——这是驱动版本导致的问题。",
                Details = details,
                Advice = "建议：① 更换为其他版本的显卡驱动；② 一键清除旧着色器文件；③ 进入游戏重新预热着色器。",
            };
        }

        return new ShaderDiagnosis
        {
            Level = ShaderDiagLevel.Abnormal,
            Summary = $"判定为着色器异常：{reason}。驱动 {driverVersion} 非问题系列，缓存文件本身出了问题。",
            Details = details,
            Advice = "建议：一键清除旧着色器文件，然后进入游戏重新预热着色器。",
        };
    }

    /// <summary>状态卡与健康检测共用驱动归因：617.14 缓存异常时视为问题驱动；更高版本仅在大文件不足时归因驱动。</summary>
    internal static bool HasProblemDriverCacheSignature(
        ShaderGpuDetectionState gpuState,
        string driverVersion,
        string psoCachePath)
    {
        var versionNumber = ToVersionNumber(driverVersion);
        if (gpuState != ShaderGpuDetectionState.Nvidia
            || versionNumber is null
            || versionNumber < Driver61714Version
            || string.IsNullOrWhiteSpace(psoCachePath))
        {
            return false;
        }

        try
        {
            if (!Directory.Exists(psoCachePath))
            {
                return false;
            }

            var gameVerDirs = Directory.GetDirectories(psoCachePath);
            if (gameVerDirs.Length != 1)
            {
                return false;
            }

            var dxCache = FindDxCachePath(gameVerDirs[0]);
            if (dxCache is null)
            {
                return false;
            }

            var nvphFiles = new DirectoryInfo(dxCache)
                .EnumerateFiles("*.nvph", SearchOption.TopDirectoryOnly)
                .Select(file =>
                {
                    try { return file.Length; }
                    catch { return -1L; }
                })
                .Where(size => size >= 0)
                .ToArray();

            if (nvphFiles.Length == 0)
            {
                return false;
            }

            var zeroFileExists = nvphFiles.Any(size => size == 0);
            var largeFileCount = nvphFiles.Count(size => size >= NvphMinBytes);
            if (versionNumber == Driver61714Version)
            {
                return zeroFileExists || largeFileCount < NvphMinLargeCount || largeFileCount >= 3;
            }

            return largeFileCount < NvphMinLargeCount;
        }
        catch (Exception ex)
        {
            Log.Warn($"着色器驱动硬规则：读取 NVPH 缓存失败：{ex.Message}");
            return false;
        }
    }

    internal static string? FindDxCachePath(string gameVerDirectory)
    {
        var sm6 = Path.Combine(gameVerDirectory, "SM6", "DXCache");
        if (Directory.Exists(sm6)) return sm6;
        var sm5 = Path.Combine(gameVerDirectory, "SM5", "DXCache");
        return Directory.Exists(sm5) ? sm5 : null;
    }

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

    // ---------------- 工具 ----------------

    private static double GetDirectorySizeMb(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return 0;
            }

            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(file => { try { return file.Length; } catch { return 0; } }) / 1024.0 / 1024.0;
        }
        catch
        {
            return 0;
        }
    }
}
