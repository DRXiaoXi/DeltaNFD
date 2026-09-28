using System.Management;

namespace DeltaNFD.Services;

/// <summary>一块固定磁盘的类型报告（介质类型 + 角色）。</summary>
public sealed class DriveTypeReport
{
    public char Letter { get; init; }

    /// <summary>true = 机械硬盘（HDD 介质）。</summary>
    public bool IsMechanical { get; init; }

    /// <summary>true = 固态（SSD / NVMe）。</summary>
    public bool IsSolidState { get; init; }

    /// <summary>是否为三角洲游戏安装所在盘。</summary>
    public bool IsGameDrive { get; init; }

    /// <summary>是否为 Windows 系统盘。</summary>
    public bool IsSystemDrive { get; init; }

    /// <summary>给 UI 的介质标注（"固态" / "机械硬盘" / ""=未知）。</summary>
    public string MediaLabel => IsSolidState ? "固态" : IsMechanical ? "机械硬盘" : "";
}

/// <summary>
/// 磁盘介质检测（固态 / 机械）：查询 root\Microsoft\Windows\Storage 的
/// MSFT_PhysicalDisk（MediaType：3=HDD 4=SSD；BusType 17=NVMe 一律按固态计）
/// 与 MSFT_Partition（DiskNumber → DriveLetter），在内存中按磁盘号拼接。
/// 查询失败（存储 WMI 缺失 / 权限受限）时介质按未知处理，不触发任何建议。
/// </summary>
public static class DriveInspector
{
    /// <summary>枚举全部固定磁盘并给出类型报告（调用方用于盘符下拉与机械盘/游戏盘建议）。</summary>
    public static IReadOnlyList<DriveTypeReport> GetFixedDriveReports()
    {
        // 磁盘号 → 介质结论（true=固态 false=机械 null=未知）
        var mediaByDisk = new Dictionary<int, bool?>();
        // 盘符 → 磁盘号
        var diskByLetter = new Dictionary<char, int>();

        try
        {
            foreach (var disk in new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                         "SELECT DeviceId, MediaType, BusType FROM MSFT_PhysicalDisk").Get())
            {
                using (disk)
                {
                    if (!int.TryParse(disk["DeviceId"]?.ToString(), out var number))
                    {
                        continue;
                    }

                    var mediaType = ToUInt(disk["MediaType"]);
                    var busType = ToUInt(disk["BusType"]);
                    mediaByDisk[number] = busType == 17 ? true       // NVMe
                        : mediaType == 3 ? false                     // HDD
                        : mediaType == 4 ? true                      // SSD
                        : null;                                      // 未上报 → 未知
                }
            }

            foreach (var part in new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                         "SELECT DiskNumber, DriveLetter FROM MSFT_Partition").Get())
            {
                using (part)
                {
                    var letter = ToUInt(part["DriveLetter"]);
                    if (letter is >= (uint)'A' and <= (uint)'Z')
                    {
                        diskByLetter[(char)letter] = ToInt(part["DiskNumber"]);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Info("DriveInspector：存储 WMI 查询失败，磁盘介质按未知处理 —— " + ex.Message);
        }

        var gameLetters = CollectGameDriveLetters();
        var systemLetter = SystemLetter();

        var reports = new List<DriveTypeReport>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                continue;
            }

            var letter = char.ToUpperInvariant(drive.Name[0]);
            var media = diskByLetter.TryGetValue(letter, out var diskNo) && mediaByDisk.TryGetValue(diskNo, out var m)
                ? m
                : null;
            reports.Add(new DriveTypeReport
            {
                Letter = letter,
                IsSolidState = media == true,
                IsMechanical = media == false,
                IsGameDrive = gameLetters.Contains(letter),
                IsSystemDrive = letter == systemLetter,
            });
        }

        return reports.OrderBy(r => r.IsSystemDrive ? 0 : r.IsGameDrive ? 1 : 2)
            .ThenBy(r => r.Letter)
            .ToList();
    }

    private static uint ToUInt(object? value) => value is null ? 0 : Convert.ToUInt32(value);

    private static int ToInt(object? value) => value is null ? -1 : Convert.ToInt32(value);

    /// <summary>三角洲游戏安装根目录所在盘符集合（WeGame 注册表 + 常规目录扫描）。</summary>
    private static HashSet<char> CollectGameDriveLetters()
    {
        var letters = new HashSet<char>();
        try
        {
            foreach (var root in DeltaForceLocator.FindRoots())
            {
                var driveRoot = Path.GetPathRoot(root);
                if (!string.IsNullOrEmpty(driveRoot))
                {
                    letters.Add(char.ToUpperInvariant(driveRoot[0]));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Info("DriveInspector：游戏安装盘检测失败 —— " + ex.Message);
        }

        return letters;
    }

    private static char SystemLetter()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory);
        return string.IsNullOrEmpty(root) ? 'C' : char.ToUpperInvariant(root[0]);
    }
}
