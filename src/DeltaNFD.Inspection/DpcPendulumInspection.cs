using System.Security.Cryptography;
using System.Text.Json;
using DeltaNFD.Services;

namespace DeltaNFD.Inspection;

public static class DpcPendulumInspection
{
    public static string DataDirectory => Path.Combine(AppDataPaths.Root, "PendulumDpc");
    public static string JournalPath => Path.Combine(DataDirectory, "owned-process.json");
    public static void NoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("DPC 文件与记录路径不能经过重解析点。");
    }
    public static string CanonicalExe(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Length < 4 || !char.IsAsciiLetter(path[0])
            || path[1] != ':' || path[2] != '\\' || path.Contains('"') || path.Contains('\0') || path.IndexOf(':', 2) >= 0
            || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("请选择本地磁盘上的原版 DPC EXE，不接受网络路径、参数或其他文件类型。");
        var full = Path.GetFullPath(path);
        NoLinks(full);
        return full;
    }
    public static FileStream OpenVerified(string path)
    {
        path = CanonicalExe(path);
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (file.Length != DpcPendulumSource.ExeBytes || !Convert.ToHexString(SHA256.HashData(file)).Equals(DpcPendulumSource.ExeSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("文件不是已核验的原版 DPC Latency Checker 1.4.0（306928 字节）。未运行或复制文件。");
            return file;
        }
        catch { file.Dispose(); throw; }
    }
    public static DpcPendulumStatus Read()
    {
        try
        {
            var settings = AppSettingsStore.ReadRequired();
            if (settings.OptimalClockDpcAcceptedSha != DpcPendulumSource.ExeSha256 || settings.OptimalClockDpcExePath.Length == 0)
                return new(false, "", "先选择并核验玩家自备的原版 DPC 1.4.0 EXE，再检测并保存 1.0000ms 持有方式。");
            var path = CanonicalExe(settings.OptimalClockDpcExePath);
            using var file = OpenVerified(path);
            return new(true, path, "自备 DPC 原件已核验；原版默认申请 1.0000ms。无需本工具的测试证书或 TESTSIGNING；系统仍可能拒绝旧驱动。");
        }
        catch (Exception ex) { return new(false, "", "DPC 准备未确认：" + ex.Message); }
    }
    public static PendulumGlobalStatus Evaluate(Version version, DpcPendulumStatus status)
    {
        var supported = version.Major == 10 && version.Minor == 0 && version.Build is >= 17763 and < 22000;
        var sourceId = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(status.ExePath.ToUpperInvariant())));
        var platform = DpcPendulumSource.Profile + version + "/" + DpcPendulumSource.ExeSha256 + "/" + sourceId;
        return new() { Supported = supported, Readable = supported, ExperimentalWindows10 = supported,
            Win10Dpc = status, PlatformId = platform, Fingerprint = platform, Message = status.Message };
    }
    public static DpcPendulumJournal? ReadJournal()
    {
        NoLinks(JournalPath);
        FileStream file;
        try { file = new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return null; } catch (DirectoryNotFoundException) { return null; }
        using var lifetime = file;
        if (file.Length is <= 0 or > 32768) throw new IOException("DPC 常驻记录大小异常，原件保留。");
        var value = JsonSerializer.Deserialize<DpcPendulumJournal>(file) ?? throw new IOException("DPC 常驻记录为空。");
        ValidateJournal(value);
        return value;
    }
    public static void ValidateJournal(DpcPendulumJournal value)
    {
        if (!Guid.TryParseExact(value.Id, "N", out _) || !Guid.TryParse(value.BootId, out _)
            || value.Phase is not ("intent" or "running") || value.ControllerPid <= 0 || value.ControllerStartTicks <= 0
            || value.Pid < 0 || (value.Pid > 0 && value.StartTicks <= 0)
            || (value.Phase == "running" && value.Pid <= 0) || value.ServicePath.Length > 4096)
            throw new IOException("DPC 常驻记录身份不完整，未猜测或删除记录。");
        _ = CanonicalExe(value.ExePath);
    }
}
