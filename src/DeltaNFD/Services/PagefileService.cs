using System.Diagnostics;
using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>当前虚拟内存（页面文件）状态。</summary>
public sealed class PagefileInfo
{
    /// <summary>系统是否处于「自动管理所有驱动器的分页文件大小」。</summary>
    public bool AutomaticManaged { get; init; }

    /// <summary>是否配置了页面文件。</summary>
    public bool HasPagefile { get; init; }

    /// <summary>原始 PagingFiles 配置（每驱动一项，形如 "C:\pagefile.sys 16384 32768"）。</summary>
    public string[] RawEntries { get; init; } = Array.Empty<string>();

    /// <summary>给 UI 的状态摘要。</summary>
    public string Summary { get; init; } = "";
}

/// <summary>虚拟内存（页面文件）设置服务。</summary>
public interface IPagefileService
{
    bool HasOriginalBackup { get; }
    /// <summary>读取当前虚拟内存状态（纯只读）。</summary>
    Task<PagefileInfo> GetInfoAsync();

    /// <summary>
    /// 设置固定大小页面文件（MB），写入指定盘符。约束：最小/最大均不得低于 16 GB，最大不小于最小。
    /// 写入后需重启电脑生效；原配置自动备份，可用 <see cref="RestoreAutomaticAsync"/> 恢复系统托管。
    /// </summary>
    Task<OperationResult> SetCustomAsync(int minMb, int maxMb, char driveLetter);

    /// <summary>恢复为系统托管（自动管理所有驱动器的分页文件大小）。</summary>
    Task<OperationResult> RestoreAutomaticAsync();

    /// <summary>按首次修改前的备份恢复原页面文件配置。</summary>
    Task<OperationResult> RestoreOriginalAsync();
}

/// <summary>虚拟内存（页面文件）设置的真实实现。</summary>
public sealed class PagefileService : IPagefileService
{
    /// <summary>页面文件配置（REG_MULTI_SZ）：系统托管时 Windows 写 "?:\pagefile.sys"。</summary>
    private const string MemoryKeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private const string PagingFilesValue = "PagingFiles";
    private const string AutoManagedData = @"?:\pagefile.sys";

    /// <summary>页面文件最小/最大下限：16 GB（用户要求，低于此值不允许设置）。</summary>
    public const int MinMbFloor = 16 * 1024;

    public bool HasOriginalBackup =>
        TweakBackupStore.Default.Get("HKLM", MemoryKeyPath, PagingFilesValue) is not null;

    public Task<PagefileInfo> GetInfoAsync() => Task.Run(() =>
    {
        using var key = Registry.LocalMachine.OpenSubKey(MemoryKeyPath);
        var raw = key?.GetValue(PagingFilesValue) as string[];
        var entries = raw ?? Array.Empty<string>();
        var automatic = IsAutomatic(entries);

        var summary = automatic
            ? "当前：系统托管（自动管理所有驱动器的大小）"
            : entries.Length == 0
                ? "当前：未配置任何页面文件"
                : "当前：手动管理 — " + string.Join("；", entries.Select(FormatEntry));

        return new PagefileInfo
        {
            AutomaticManaged = automatic,
            HasPagefile = entries.Length > 0,
            RawEntries = entries,
            Summary = summary,
        };
    });

    public Task<OperationResult> SetCustomAsync(int minMb, int maxMb, char driveLetter) => Task.Run(() =>
    {
        if (!ElevationHelper.IsElevated)
        {
            return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        }

        if (minMb < MinMbFloor)
        {
            return OperationResult.Fail($"最小值不能低于 16 GB（{MinMbFloor} MB）。");
        }

        if (maxMb < MinMbFloor)
        {
            return OperationResult.Fail($"最大值不能低于 16 GB（{MinMbFloor} MB）。");
        }

        if (maxMb < minMb)
        {
            return OperationResult.Fail("最大值不能小于最小值。");
        }

        var drive = char.ToUpperInvariant(driveLetter);
        if (!IsValidFixedDrive(drive))
        {
            return OperationResult.Fail($"盘符 {drive}: 不是有效的固定磁盘。");
        }

        try
        {
            var available = new DriveInfo($"{drive}:").AvailableFreeSpace;
            if (!HasEnoughSpace(available, maxMb))
                return OperationResult.Fail($"{drive}: 可用空间不足，最大页面文件需要至少 {maxMb / 1024} GB 可用空间。");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"无法读取 {drive}: 的可用空间：{ex.Message}");
        }

        Log.Info($"虚拟内存：设置固定页面文件 {drive}:\\pagefile.sys {minMb} MB / {maxMb} MB");
        try
        {
            BackupCurrent();
            using var key = OpenWritable();
            key.SetValue(PagingFilesValue, new[] { $"{drive}:\\pagefile.sys {minMb} {maxMb}" }, RegistryValueKind.MultiString);
            return OperationResult.Ok(
                $"虚拟内存已设置：{drive}:\\pagefile.sys 最小 {minMb / 1024} GB / 最大 {maxMb / 1024} GB。重启电脑后生效。",
                requiresReboot: true);
        }
        catch (Exception ex)
        {
            Log.Error("虚拟内存：设置失败", ex);
            return OperationResult.Fail($"设置虚拟内存失败：{ex.Message}");
        }
    });

    public Task<OperationResult> RestoreAutomaticAsync() => Task.Run(() =>
    {
        if (!ElevationHelper.IsElevated)
        {
            return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        }

        Log.Info("虚拟内存：恢复系统托管");
        try
        {
            using var key = OpenWritable();
            key.SetValue(PagingFilesValue, new[] { AutoManagedData }, RegistryValueKind.MultiString);
            return OperationResult.Ok("已恢复为系统托管（自动管理所有驱动器的分页文件大小）。重启电脑后生效。", requiresReboot: true);
        }
        catch (Exception ex)
        {
            Log.Error("虚拟内存：恢复系统托管失败", ex);
            return OperationResult.Fail($"恢复系统托管失败：{ex.Message}");
        }
    });

    public Task<OperationResult> RestoreOriginalAsync() => Task.Run(() =>
    {
        if (!ElevationHelper.IsElevated)
        {
            return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        }

        var backup = TweakBackupStore.Default.Get("HKLM", MemoryKeyPath, PagingFilesValue);
        if (backup is null)
        {
            return OperationResult.Fail("没有虚拟内存原配置备份。");
        }

        try
        {
            using var key = OpenWritable();
            if (backup.ValueKind == RegistryValueKind.None)
            {
                key.DeleteValue(PagingFilesValue, throwOnMissingValue: false);
            }
            else if (backup.ValueKind == RegistryValueKind.MultiString)
            {
                var entries = DecodeOriginalEntries(backup);
                key.SetValue(PagingFilesValue, entries, RegistryValueKind.MultiString);
            }
            else
            {
                return OperationResult.Fail("虚拟内存备份类型不正确，未修改当前配置。");
            }

            TweakBackupStore.Default.Remove("HKLM", MemoryKeyPath, PagingFilesValue);
            return OperationResult.Ok("已恢复修改前的虚拟内存配置，重启电脑后生效。", requiresReboot: true);
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("恢复虚拟内存原配置失败：" + ex.Message);
        }
    });

    // ---------------- 内部实现 ----------------

    /// <summary>盘符必须是 A–Z 且实际存在的固定磁盘（防止写入非法 PagingFiles 条目）。</summary>
    private static bool IsValidFixedDrive(char letter)
    {
        if (letter is < 'A' or > 'Z')
        {
            return false;
        }

        try
        {
            return new DriveInfo($"{letter}:").DriveType == DriveType.Fixed;
        }
        catch
        {
            return false;
        }
    }

    internal static bool HasEnoughSpace(long availableBytes, int maxMb) =>
        availableBytes >= (long)maxMb * 1024 * 1024;

    private static RegistryKey OpenWritable() =>
        Registry.LocalMachine.OpenSubKey(MemoryKeyPath, writable: true)
        ?? Registry.LocalMachine.CreateSubKey(MemoryKeyPath, writable: true);

    private static bool IsAutomatic(string[] entries) =>
        entries.Length == 1 && entries[0].StartsWith("?:", StringComparison.OrdinalIgnoreCase);

    private static string FormatEntry(string entry)
    {
        // "C:\pagefile.sys 16384 32768" → "C:\pagefile.sys（16384–32768 MB）"
        var parts = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 && int.TryParse(parts[1], out var min) && int.TryParse(parts[2], out var max))
        {
            return $"{parts[0]}（{min / 1024}–{max / 1024} GB）";
        }

        return entry;
    }

    internal static string[] DecodeOriginalEntries(RegistryValueBackup backup)
    {
        if (backup.ValueKind != RegistryValueKind.MultiString)
            throw new FormatException("页面文件备份类型不是 REG_MULTI_SZ");
        if (backup.StringData is null && backup.Data == "System.String[]")
            throw new FormatException("旧版页面文件备份已损坏，无法安全恢复");
        return backup.StringData ?? backup.Data.Split(';');
    }

    /// <summary>把当前 PagingFiles 原值备份进 TweakBackupStore（首次改动前记录，MultiString 以分号合并存储）。</summary>
    private void BackupCurrent()
    {
        using var key = Registry.LocalMachine.OpenSubKey(MemoryKeyPath);
        var raw = key?.GetValue(PagingFilesValue) as string[];

        TweakBackupStore.Default.Save(new RegistryValueBackup
        {
            Hive = "HKLM",
            Id = TweakBackupStore.MakeId("HKLM", MemoryKeyPath, PagingFilesValue),
            KeyPath = MemoryKeyPath,
            ValueName = PagingFilesValue,
            ValueKind = raw is null ? RegistryValueKind.None : RegistryValueKind.MultiString,
            Data = raw is null ? "" : string.Join(";", raw),
            StringData = raw,
            CreatedAt = DateTimeOffset.Now,
        });
    }
}
