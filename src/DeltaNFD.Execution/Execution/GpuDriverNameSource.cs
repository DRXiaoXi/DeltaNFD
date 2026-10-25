using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>Read the installed driver's INF, never DeviceDesc/WMI/DXGI as a model source.</summary>
internal static class GpuDriverNameSource
{
    internal sealed record Evidence(string RegistryPath, string CurrentDescription, string DriverKey,
        string InfName, string InfSection, string MatchingId, string DriverDescription,
        string DriverVersion, string InfSha256, string Description, GpuSpoofRestoreState.Snapshot Pending)
    {
        internal string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this))));
        internal GpuDriverNamePreview Preview => new(RegistryPath, CurrentDescription, DriverDescription, InfName, MatchingId, Fingerprint);
    }

    internal static bool ValidPath(string path) => Regex.IsMatch(path ?? "",
        @"^SYSTEM\\CurrentControlSet\\Enum\\PCI\\VEN_[0-9A-F]{4}&DEV_[0-9A-F]{4}(?:&[0-9A-Z_]+)*\\[0-9A-Z&_.-]+\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static Evidence Read(string path, TweakBackupStore backups)
    {
        if (!ValidPath(path)) throw new IOException("仅支持核验所选 PCI 显示适配器，设备路径无效。");
        if (backups.GetStrict("HKLM", path, "DeviceDesc") is not null)
            throw new IOException("原始型号备份已存在，请使用正常恢复，不覆盖该备份。");
        using var device = Registry.LocalMachine.OpenSubKey(path) ?? throw new IOException("所选显卡实例已不存在。");
        if (!string.Equals(device.GetValue("ClassGUID") as string, GpuSpoofService.DisplayClassGuid, StringComparison.OrdinalIgnoreCase))
            throw new IOException("目标不是显示适配器。");
        if (device.GetValueKind("DeviceDesc") != RegistryValueKind.String) throw new IOException("显卡描述类型无法核验。");
        var current = device.GetValue("DeviceDesc", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? throw new IOException("当前显卡描述不存在。");
        if (current.Length > 4096 || current.Contains('\0')) throw new IOException("当前显卡描述过长或无效，未继续核验。");
        var driver = device.GetValue("Driver") as string ?? "";
        if (!Regex.IsMatch(driver, @"^\{4d36e968-e325-11ce-bfc1-08002be10318\}\\[0-9]{4}\z", RegexOptions.IgnoreCase))
            throw new IOException("设备没有可核验的已安装显示驱动绑定。");
        using var software = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driver)
            ?? throw new IOException("已安装驱动登记不存在。");
        string Value(string key) => software.GetValue(key) as string ?? "";
        var inf = Value("InfPath"); var section = Value("InfSection"); var matching = Value("MatchingDeviceId");
        var name = Value("DriverDesc"); var version = Value("DriverVersion");
        var ids = device.GetValue("HardwareID") as string[] ?? [];
        if (!Regex.IsMatch(inf, @"^oem[0-9]{1,5}\.inf\z", RegexOptions.IgnoreCase)
            || !Regex.IsMatch(section, @"^[A-Za-z0-9_.-]{1,200}\z") || !ValidName(name)
            || !MatchesHardware(path, matching, ids))
            throw new IOException("驱动 INF、型号或硬件 ID 无法相互核验；请安装对应厂商驱动后重试。");
        var infPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", inf);
        RejectLinks(infPath);
        using var stream = new FileStream(infPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > 16 * 1024 * 1024) throw new IOException("驱动 INF 大小不在核验范围内。");
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        var description = ResolveInf(infPath, section, matching, name);
        var settings = AppSettingsStore.ReadRequired();
        if (settings.FrameModeActive) throw new IOException("请先退出帧格模式，避免自动伪装再次覆盖恢复名称。");
        return new(path, current, driver, inf, section, matching, name, version, hash, description,
            GpuSpoofRestoreState.Capture(settings));
    }

    internal static bool ValidName(string name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 512
        && !name.Any(c => char.IsControl(c) || c is ';' or '%' or '@')
        && !name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase);

    internal static bool MatchesHardware(string path, string matching, IReadOnlyList<string> ids) => ValidPath(path)
        && Regex.IsMatch(matching ?? "", @"^PCI\\VEN_[0-9A-F]{4}&DEV_[0-9A-F]{4}(?:&[0-9A-Z_]+)*\z", RegexOptions.IgnoreCase)
        && ids.Any(id => id.Equals(matching, StringComparison.OrdinalIgnoreCase))
        // HardwareID contains the authoritative complete match. The instance's PCI key also
        // checks VEN/DEV; suffix order may differ for a valid generic REV-only driver match.
        && path.Split('\\')[4].StartsWith(matching![4..21], StringComparison.OrdinalIgnoreCase);

    internal static void RejectLinks(string path)
    {
        for (var item = new FileInfo(Path.GetFullPath(path)) as FileSystemInfo; item is not null;
             item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("核验路径包含重解析点，未继续。");
    }

    internal static string ResolveInf(string path, string section, string matching, string expectedName)
    {
        var handle = SetupOpenInfFileW(path, null, 2, out _);
        if (handle == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法只读打开已安装 INF。");
        try
        {
            var timer = System.Diagnostics.Stopwatch.StartNew(); var budget = 200000;
            void Budget() { if (--budget < 0 || timer.Elapsed > TimeSpan.FromSeconds(10)) throw new IOException("INF 核验超出预算。"); }
            if (!Lines("Version").Any(line => Field(line, 0).Equals("ClassGuid", StringComparison.OrdinalIgnoreCase)
                && Field(line, 1).Equals(GpuSpoofService.DisplayClassGuid, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("INF 不属于显示设备类。");
            var sections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in Lines("Manufacturer"))
            {
                var context = line;
                var count = SetupGetFieldCount(ref context);
                var model = Field(context, 1);
                if (count == 1) sections.Add(model);
                for (uint i = 2; i <= count && i < 32; i++) sections.Add(model + "." + Field(context, i));
            }
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var model in sections)
                foreach (var line in Lines(model))
                {
                    var context = line; Budget();
                    if (!Field(context, 1).Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
                    for (uint i = 2, count = SetupGetFieldCount(ref context); i <= count && i < 32; i++)
                        if (Field(context, i).Equals(matching, StringComparison.OrdinalIgnoreCase)) names.Add(Field(context, 0));
                }
            if (names.Count != 1 || !names.Contains(expectedName)) throw new IOException("INF 型号与当前驱动/硬件绑定不一致或存在歧义，未猜测名称。");

            var tokens = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            for (uint index = 0; index < 4096; index++)
            {
                Budget(); var buffer = new StringBuilder(4096);
                if (!SetupEnumInfSectionsW(handle, index, buffer, (uint)buffer.Capacity, out _))
                {
                    if (Marshal.GetLastWin32Error() == 259) break;
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                var strings = buffer.ToString();
                if (!strings.Equals("Strings", StringComparison.OrdinalIgnoreCase)
                    && !Regex.IsMatch(strings, @"^Strings\.[0-9a-fA-F]{4}\z")) continue;
                foreach (var line in Lines(strings))
                    if (Field(line, 1).Equals(expectedName, StringComparison.Ordinal))
                    {
                        var token = Field(line, 0);
                        if (Regex.IsMatch(token, @"^[A-Za-z0-9_.-]{1,200}\z")) tokens.Add(token);
                    }
            }
            if (tokens.Count == 0) throw new IOException("无法取得 INF 中可验证的型号字符串标识。");
            var value = $"@{Path.GetFileName(path)},%{tokens.Min}%;{expectedName}";
            if (!GpuSpoofRestoreState.IsNativeDescription(value)) throw new IOException("重建描述不符合驱动描述格式。");
            return value;

            IEnumerable<InfContext> Lines(string name)
            {
                if (!SetupFindFirstLineW(handle, name, null, out var context)) yield break;
                do { Budget(); yield return context; }
                while (SetupFindNextLine(ref context, out context));
            }
        }
        finally { SetupCloseInfFile(handle); }
    }

    private static string Field(InfContext context, uint index)
    {
        var buffer = new StringBuilder(4096);
        if (!SetupGetStringFieldW(ref context, index, buffer, (uint)buffer.Capacity, out var required) || required > 4096)
            throw new IOException("INF 字段无法完整读取。");
        return buffer.ToString();
    }
    [StructLayout(LayoutKind.Sequential)] private struct InfContext { public IntPtr Inf, CurrentInf; public uint Section, Line; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupOpenInfFileW(string file, string? @class, uint style, out uint errorLine);
    [DllImport("setupapi.dll")] private static extern void SetupCloseInfFile(IntPtr inf);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupFindFirstLineW(IntPtr inf, string section, string? key, out InfContext context);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupFindNextLine(ref InfContext input, out InfContext output);
    [DllImport("setupapi.dll")] private static extern uint SetupGetFieldCount(ref InfContext context);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupGetStringFieldW(ref InfContext context, uint index, StringBuilder value, uint size, out uint required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupEnumInfSectionsW(IntPtr inf, uint index, StringBuilder value, uint size, out uint required);
}
