using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>
/// 三角洲游戏本体兼容性调整：禁用/恢复 DeltaForceClient-Win64-Shipping.exe 的全屏优化（FSO）。
/// 写入 HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers，
/// 该位置不需要管理员权限；但修改前仍会把原值备份到 <see cref="TweakBackupStore"/>。
/// 游戏路径解析：手动指定目录 → WeGame 注册表 → 常见路径扫描。
/// </summary>
public static class GameCompatibilityService
{
    private const string LayersKeyPath = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    private const string DisableFsoValue = "~ DISABLEDXMAXIMIZEDWINDOWEDMODE";

    /// <summary>解析当前应使用的所有游戏 EXE 完整路径（每份安装一个）；找不到返回空列表。</summary>
    public static IReadOnlyList<string> ResolveGameExePaths()
    {
        var results = new List<string>();
        foreach (var root in DeltaForceLocator.FindRoots())
        {
            var candidates = new[]
            {
                Path.Combine(root, "Binaries", "Win64", DeltaForceLocator.GameProcessName + ".exe"),
                Path.Combine(root, "DeltaForce", "Binaries", "Win64", DeltaForceLocator.GameProcessName + ".exe"),
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    results.Add(candidate);
                    break; // 每份安装只取一个主程序
                }
            }
        }
        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>解析当前应使用的游戏 EXE 完整路径；找不到返回 null。</summary>
    public static string? ResolveGameExePath() => ResolveGameExePaths().FirstOrDefault();

    /// <summary>查询指定 EXE 当前是否已禁用全屏优化。</summary>
    public static bool IsFullscreenOptimizationsDisabled(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LayersKeyPath, writable: false);
            var value = key?.GetValue(exePath) as string;
            return !string.IsNullOrEmpty(value) &&
                   value.Contains("DISABLEDXMAXIMIZEDWINDOWEDMODE", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 禁用指定 EXE 的全屏优化（写入 Layers 注册表值）。
    /// 原值会备份到 TweakBackupStore，重复调用幂等。
    /// </summary>
    public static OperationResult DisableFullscreenOptimizations(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return OperationResult.Fail("游戏 EXE 路径为空。");
        }

        if (!File.Exists(exePath))
        {
            return OperationResult.Fail($"游戏主程序不存在：{exePath}");
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(LayersKeyPath, writable: true);
            if (key is null)
            {
                return OperationResult.Fail("无法打开注册表 Layers 键。");
            }

            var existing = key.GetValue(exePath) as string;

            // 已包含目标标记：幂等成功
            if (!string.IsNullOrEmpty(existing) &&
                existing.Contains("DISABLEDXMAXIMIZEDWINDOWEDMODE", StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult.Ok("已处于禁用全屏优化状态。");
            }

            // 备份原值（仅第一次）
            var backups = TweakBackupStore.Default;
            var id = TweakBackupStore.MakeId("HKCU", LayersKeyPath, exePath);
            if (backups.Get("HKCU", LayersKeyPath, exePath) is null)
            {
                backups.Save(new RegistryValueBackup
                {
                    Id = id,
                    Hive = "HKCU",
                    KeyPath = LayersKeyPath,
                    ValueName = exePath,
                    ValueKind = existing is null ? RegistryValueKind.None : RegistryValueKind.String,
                    Data = existing ?? "",
                    CreatedAt = DateTimeOffset.Now,
                });
            }

            // 已有其他兼容性标记时追加，否则写入标准值
            var newValue = string.IsNullOrEmpty(existing)
                ? DisableFsoValue
                : existing + " DISABLEDXMAXIMIZEDWINDOWEDMODE";

            key.SetValue(exePath, newValue, RegistryValueKind.String);
            Log.Info($"游戏兼容性：已禁用全屏优化 {exePath}（原值：{existing ?? "(空)"}）");
            return OperationResult.Ok("已禁用全屏优化，下次启动游戏生效。");
        }
        catch (Exception ex)
        {
            Log.Error("游戏兼容性：禁用全屏优化失败", ex);
            return OperationResult.Fail("禁用全屏优化失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 恢复全屏优化设置（从备份还原；无备份时删除本工具写入的值）。
    /// </summary>
    public static OperationResult RestoreFullscreenOptimizations(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return OperationResult.Fail("游戏 EXE 路径为空。");
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LayersKeyPath, writable: true);
            if (key is null)
            {
                return OperationResult.Fail("无法打开注册表 Layers 键。");
            }

            var backups = TweakBackupStore.Default;
            var backup = backups.Get("HKCU", LayersKeyPath, exePath);

            if (backup is not null)
            {
                // 有备份：还原到修改前的值
                if (backup.ValueKind == RegistryValueKind.None)
                {
                    key.DeleteValue(exePath, throwOnMissingValue: false);
                }
                else
                {
                    key.SetValue(exePath, backup.Data, RegistryValueKind.String);
                }

                backups.Remove("HKCU", LayersKeyPath, exePath);
                Log.Info($"游戏兼容性：已恢复全屏优化 {exePath}（还原到备份值）");
                return OperationResult.Ok("已恢复全屏优化设置。");
            }

            // 无备份：仅当当前值包含本工具写入的标记时才清理
            var existing = key.GetValue(exePath) as string;
            if (string.IsNullOrEmpty(existing) ||
                !existing.Contains("DISABLEDXMAXIMIZEDWINDOWEDMODE", StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult.Skip("当前未处于禁用全屏优化状态，无需恢复。");
            }

            // 尝试从组合值中移除本工具的标记；如果只剩空串则删除整个值
            var cleaned = existing
                .Replace("DISABLEDXMAXIMIZEDWINDOWEDMODE", "", StringComparison.OrdinalIgnoreCase)
                .Trim();

            if (cleaned.Length == 0 || cleaned == "~")
            {
                key.DeleteValue(exePath, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(exePath, cleaned, RegistryValueKind.String);
            }

            Log.Info($"游戏兼容性：已移除全屏优化禁用标记 {exePath}（无备份，按当前值清理）");
            return OperationResult.Ok("已恢复全屏优化设置。");
        }
        catch (Exception ex)
        {
            Log.Error("游戏兼容性：恢复全屏优化失败", ex);
            return OperationResult.Fail("恢复全屏优化失败：" + ex.Message);
        }
    }
}
