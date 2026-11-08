using System.Management;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using DeltaNFD.Services;

namespace DeltaNFD.Inspection;

/// <summary>Fixed-key, query-only inspection. A registry value alone is not proof of a completed restart.</summary>
public static class PendulumGlobalInspection
{
    public static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);
    public static bool ExperimentalWindows10 => Supported && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
    public const string KeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";
    public const string ValueName = "GlobalTimerResolutionRequests";
    public static string RecordPath => Path.Combine(AppDataPaths.Root, "pendulum-global-settings.json");
    private static readonly Lazy<string> Boot = new(ReadBootId);
    public static string CurrentBootId => Boot.Value;
    public static Task<PendulumGlobalStatus> ReadAsync() => InspectionScheduler.Run(Read);

    public static PendulumRegistryValue ReadValue()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(KeyPath);
        var value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return new(false, 0);
        if (key!.GetValueKind(ValueName) != RegistryValueKind.DWord || value is not int number || number is not (0 or 1))
            throw new IOException("全局精度配置类型或数值异常，未将其视为已启用。");
        return new(true, number);
    }

    public static PendulumGlobalRecord? ReadRecord()
    {
        if (!File.Exists(RecordPath)) return null;
        if ((File.GetAttributes(RecordPath) & FileAttributes.ReparsePoint) != 0 || new FileInfo(RecordPath).Length > 64 * 1024)
            throw new IOException("全局精度设置记录的类型或大小异常。");
        var record = JsonSerializer.Deserialize<PendulumGlobalRecord>(File.ReadAllText(RecordPath))
            ?? throw new IOException("全局精度设置记录为空。");
        if (!Guid.TryParseExact(record.Id, "N", out _) || record.Original is null || record.Original.Value is not (0 or 1)
            || string.IsNullOrWhiteSpace(record.WrittenBootId)) throw new IOException("全局精度设置记录损坏。");
        return record;
    }

    public static PendulumGlobalStatus Read()
    {
        // Win10 的实验路径不以未确认有效的 Win11 注册表设置作为前提，也不触碰该键。
        if (ExperimentalWindows10)
        {
            try { return EvaluateWindows10(ReadSystemVersion()); }
            catch (Exception ex) { return new() { Supported = true, ExperimentalWindows10 = true,
                Message = "无法核实 Win10 系统修订版本，暂不准入实验扫描与锁定：" + ex.Message }; }
        }
        var supported = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
        try { return Evaluate(supported, ReadValue(), CurrentBootId, ReadRecord()); }
        catch (Exception ex) { return new() { Supported = supported, Message = "全局生效精度状态未确认：" + ex.Message }; }
    }

    /// <summary>OSVersion 不一定含累计更新修订号；使用只读 UBR 绑定实验结果。</summary>
    public static Version ReadSystemVersion()
    {
        var version = Environment.OSVersion.Version;
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        if (key?.GetValue("UBR", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not int revision || revision < 0
            || key.GetValueKind("UBR") != RegistryValueKind.DWord)
            throw new IOException("系统 UBR 类型或数值不可读。");
        return new(version.Major, version.Minor, version.Build, revision);
    }

    public static PendulumGlobalStatus EvaluateWindows10(Version version)
    {
        var supported = version.Major == 10 && version.Minor == 0 && version.Build is >= 17763 and < 22000;
        var platform = "win10-experimental-v1/" + version;
        return new()
        {
            Supported = supported, Readable = supported, ExperimentalWindows10 = supported,
            PlatformId = platform, Fingerprint = platform,
            Message = supported
                ? "Windows 10 实验兼容：先检测无请求进程能否随精度变化，再扫描本机最佳值；通过后才允许随帧格锁定。此路径不写全局精度注册表，不要求为此重启。"
                : "此系统版本不在 Windows 10 实验兼容范围（1809 及以后）。",
        };
    }

    public static PendulumGlobalStatus Evaluate(bool supported, PendulumRegistryValue value, string bootId, PendulumGlobalRecord? record)
    {
        var configured = value.Exists && value.Value == 1;
        var pending = record is { Completed: false };
        var registration = configured && record is null;
        var confirmed = configured && record is { Completed: true, TargetEnabled: true }
            && bootId.Length > 0 && record.WrittenBootId.Length > 0 && bootId != record.WrittenBootId;
        var mismatch = record is { Completed: true } && configured != record.TargetEnabled;
        var message = !supported ? "此系统不支持钟摆计划当前路径。"
            : bootId.Length == 0 ? "无法确认本次系统启动身份，钟摆计划暂不可用。"
            : pending ? "上次全局精度设置尚未完成，请刷新并重新确认设置；记录已保留。"
            : mismatch ? "全局精度配置在上次操作后发生变化，请重新确认并重启电脑。"
            : registration ? "检测到注册表已开启，但缺少重启确认记录；请登记当前设置并重启电脑。"
            : !configured ? "先开启全局生效精度，再手动重启电脑，才能使用钟摆计划。"
            : !confirmed ? "全局精度配置已保存，必须重启电脑后才能使用钟摆计划；重开工具不能代替重启。"
            : "全局生效精度已开启，且已确认完成系统重启；钟摆计划可用。";
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { supported, value, bootId, record })));
        return new() { Supported = supported, Readable = bootId.Length > 0, Configured = configured,
            BootConfirmed = confirmed && !mismatch, NeedsRegistration = registration || mismatch,
            PendingRecovery = pending, BootId = bootId, Fingerprint = fingerprint, Message = message };
    }

    private static string ReadBootId()
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\cimv2", new ConnectionOptions { Timeout = TimeSpan.FromSeconds(5) });
            var options = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(5), ReturnImmediately = false };
            using var search = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT LastBootUpTime FROM Win32_OperatingSystem"), options);
            using var results = search.Get();
            foreach (ManagementObject value in results) using (value) return NormalizeBootId(value["LastBootUpTime"] as string ?? "");
        }
        catch { }
        return "";
    }

    public static string NormalizeBootId(string value)
    {
        if (value.Length > 0 && value.All(char.IsAsciiDigit) && long.TryParse(value, out var ticks) && ticks > 0) return value;
        try { return ManagementDateTimeConverter.ToDateTime(value).ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        catch { return ""; }
    }
}
