using System.Diagnostics;
using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>
/// 三角洲游戏目录定位（多个服务共用）：
/// 根目录来源 = 用户指定目录 + WeGame 注册表 InstallLocation + 常见 WeGameApps\rail_apps 路径扫描。
/// </summary>
internal static class DeltaForceLocator
{
    /// <summary>三角洲游戏进程名（定义在此，供无 WinUI 依赖的模块共用）。</summary>
    public const string GameProcessName = "DeltaForceClient-Win64-Shipping";

    /// <summary>手动指定的游戏根目录（设置页可改）；空 = 未指定，走自动识别。</summary>
    public static string ManualRootOverride => AppSettingsStore.Read().GameRootOverride ?? "";

    /// <summary>
    /// 校验游戏目录并返回规范化路径。接受安装根目录（其下含 DeltaForce 游戏目录）或游戏目录本身。
    /// </summary>
    public static bool TryGetValidGameRoot(string? path, out string normalizedPath)
    {
        normalizedPath = "";
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim().Trim('"')));
            if (!Directory.Exists(fullPath))
            {
                return false;
            }

            // WeGame 常见结构：安装根\DeltaForce\Binaries\Win64；
            // 也兼容用户直接选择 DeltaForce 子目录的情况。
            if (LooksLikeGamePayload(fullPath) || LooksLikeGamePayload(Path.Combine(fullPath, "DeltaForce")))
            {
                normalizedPath = fullPath;
                return true;
            }
        }
        catch
        {
            // 无效/不可访问路径视为未识别。
        }

        return false;
    }

    private static bool LooksLikeGamePayload(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        return File.Exists(Path.Combine(path, "Binaries", "Win64", GameProcessName + ".exe")) ||
               Directory.Exists(Path.Combine(path, "Content", "Paks"));
    }

    /// <summary>枚举三角洲可能的安装根目录（如 D:\WeGameApps\rail_apps\DeltaForce(2001918)）。
    /// 优先使用设置页手动指定的目录，其次 WeGame 注册表与常见路径扫描。</summary>
    public static IEnumerable<string> FindRoots()
    {
        var roots = new List<string>();

        void AddRoot(string? candidate)
        {
            if (TryGetValidGameRoot(candidate, out var normalized) &&
                !roots.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(normalized);
            }
        }

        // 1) 手动指定目录（设置页：游戏目录识别）优先
        AddRoot(ManualRootOverride);

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Tencent\WeGame\Games");
            if (key is not null)
            {
                foreach (var subName in key.GetSubKeyNames())
                {
                    using var sub = key.OpenSubKey(subName);
                    if (sub?.GetValue("InstallLocation") is not string location)
                    {
                        continue;
                    }

                    if (location.Contains("DeltaForce", StringComparison.OrdinalIgnoreCase) ||
                        location.Contains("三角洲", StringComparison.Ordinal))
                    {
                        AddRoot(location);
                    }
                }
            }
        }
        catch
        {
            // 注册表不可读则退回路径扫描
        }

        // 常见 WeGame 库目录扫描（rail_apps\DeltaForce*）
        var drives = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed);
        foreach (var drive in drives)
        {
            foreach (var libRoot in new[]
                     {
                         Path.Combine(drive.Name, "WeGameApps"),
                         Path.Combine(drive.Name, "Program Files (x86)", "WeGameApps"),
                         Path.Combine(drive.Name, "Games", "WeGameApps"),
                     })
            {
                var railApps = Path.Combine(libRoot, "rail_apps");
                if (!Directory.Exists(railApps))
                {
                    continue;
                }

                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(railApps, "DeltaForce*", SearchOption.TopDirectoryOnly))
                    {
                        AddRoot(dir);
                    }
                }
                catch
                {
                    // 目录不可读跳过
                }
            }
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>枚举游戏根目录和游戏子目录两种结构下的 ACE 文件夹位置。</summary>
    public static IEnumerable<string> FindGameAceDirectories() => FindRoots()
        .SelectMany(root => new[]
        {
            Path.Combine(root, "DeltaForce", "Binaries", "Win64", "AntiCheatExpert"),
            Path.Combine(root, "Binaries", "Win64", "AntiCheatExpert"),
        })
        .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>定位 UE4 前置包（游戏自带运行库安装器）；未找到返回空字符串。</summary>
    public static Task<string> FindUe4PrereqAsync() => Task.Run(() =>
    {
        foreach (var gameRoot in FindRoots())
        {
            // 兼容两种手动指定层级：安装根目录（含 Engine）或游戏子目录
            var candidates = new[]
            {
                Path.Combine(gameRoot, @"Engine\Extras\Redist\en-us"),
                Path.Combine(gameRoot, @"DeltaForce\Engine\Extras\Redist\en-us"),
            };

            foreach (var candidate in candidates.Where(Directory.Exists))
            {
                foreach (var file in Directory.EnumerateFiles(candidate, "UE4PrereqSetup_*.exe"))
                {
                    return file;
                }
            }
        }

        return "";
    });

    /// <summary>定位着色器缓存目录（{游戏根}\DeltaForce\Saved\PSOCache）；未找到返回空字符串。</summary>
    public static string FindPsoCachePath()
    {
        foreach (var gameRoot in FindRoots())
        {
            // 兼容两种手动指定层级：安装根目录（含 DeltaForce 子目录）或 DeltaForce 子目录本身
            var candidates = new[]
            {
                Path.Combine(gameRoot, "DeltaForce", "Saved", "PSOCache"),
                Path.Combine(gameRoot, "Saved", "PSOCache"),
            };

            foreach (var candidate in candidates.Where(Directory.Exists))
            {
                return candidate;
            }
        }

        return "";
    }

    /// <summary>三角洲游戏进程当前是否在运行。</summary>
    public static bool IsGameRunning()
    {
        var processes = Process.GetProcessesByName(GameProcessName);
        foreach (var process in processes)
        {
            process.Dispose();
        }

        return processes.Length > 0;
    }
}
