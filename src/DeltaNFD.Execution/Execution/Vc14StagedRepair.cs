using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>Borrow the standard-wrapper-first strategy, never upstream's process-started success rule.</summary>
public static partial class Vc14Reinstall
{
    internal static IReadOnlyList<Vc14RemovalPlanItem> PreferVerifiedBundles(IReadOnlyList<Vc14RemovalPlanItem> plan)
    {
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Vc14RemovalPlanItem>();
        foreach (var bundle in plan.Where(p => p.Kind == "bundle").OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!IsStandardBundle(bundle) || bundle.CacheSha256.Length != 64) continue;
            var children = plan.Where(p => p.Kind == "msi" && !covered.Contains(p.Key)
                && p.Architecture == bundle.Architecture && Vc14Catalog.SameVersion(p.Version, bundle.Version)).ToArray();
            if (children.Length is < 1 or > 2 || children.Select(p => p.Role).Distinct().Count() != children.Length
                || children.Any(p => p.Role is not ("Minimum" or "Additional") || p.DependencyFingerprint.Length == 0)) continue;
            result.Add(bundle with { Method = "微软原包装 /uninstall /quiet /norestart（优先）",
                BundleChildren = children.Select(p => new Vc14BundleChild(
                    new(p.Key, p.Name, p.Version, p.Architecture, p.Role, p.UpgradeCode), p.DependencyFingerprint, p.Command)).ToArray() });
            foreach (var child in children) covered.Add(child.Key);
        }
        result.AddRange(plan.Where(p => !(p.Kind == "msi" && covered.Contains(p.Key))
            && !result.Any(b => b.Key == p.Key && b.Hive == p.Hive && b.View == p.View)));
        return result.OrderBy(p => p.BundleChildren?.Count > 0 ? 0 : p.Kind == "msi" ? 1 : 2)
            .ThenBy(p => p.Architecture, StringComparer.Ordinal).ThenBy(p => p.Key, StringComparer.Ordinal).ToArray();
    }

    private static bool IsStandardBundle(Vc14RemovalPlanItem item) => item.Architecture is "x64" or "x86"
        && Regex.IsMatch(item.Name, @"^Microsoft Visual C\+\+ (?:2015-2022|2015-2019|2015|2017|2019|2022|2026|v14) Redistributable \("
            + item.Architecture + @"\)(?: - 14\..*)?$", RegexOptions.IgnoreCase);

    internal static ProcessStartInfo BuildBundleUninstall(string cache, string log)
    {
        var info = new ProcessStartInfo(cache) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "/uninstall", "/quiet", "/norestart", "/log", log }) info.ArgumentList.Add(arg);
        return info;
    }

    internal static string StageFingerprint(IReadOnlyList<Vc14RemovalPlanItem> plan, Vc14RepairMode mode, Vc14Continuation? pending, Vc14Scan? scan = null,
        SafeInstallerPreparation? temporary = null, Vc14Baseline baseline = Vc14Baseline.V144)
        => Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                Plan = Fingerprint(plan, baseline), Mode = mode, Pending = pending, Scan = ScanProjection(scan),
                Temporary = temporary, Baseline = baseline,
            })));

    private static object? ScanProjection(Vc14Scan? scan) => scan is null ? null : new
    {
        Products = scan.Products.OrderBy(p => p.Code, StringComparer.OrdinalIgnoreCase).ToArray(),
        Notices = scan.Notices.OrderBy(n => n.Name, StringComparer.Ordinal)
            .ThenBy(n => n.Architecture, StringComparer.Ordinal)
            .ThenBy(n => n.Version, StringComparer.Ordinal).ToArray(),
        scan.Issues,
    };
}

/// <summary>Read-only readiness. Does not start services, edit SafeBoot or change boot configuration.</summary>
internal static class Vc14InstallerReadiness
{
    internal static bool IsSafeMode => GetSystemMetrics(67) != 0;
    internal static string? Evaluate(bool safeMode, bool serviceExists, bool disabled, bool running)
        => !serviceExists ? "Windows Installer 服务不存在或无法读取，未启动运行库操作。"
        : disabled ? "Windows Installer 服务被禁用，未启动运行库操作。请在正常模式核对服务配置。"
        : safeMode && !running ? "安全模式下 Windows Installer 服务未运行，无法卸载或安装（可能返回 1601 / 1652 / 0x8007043C）。请回正常模式操作；本工具不会修改 SafeBoot/MSIServer。"
        : null;

    internal static string? Check()
    {
        var scm = OpenSCManager(null, null, 1);
        if (scm == IntPtr.Zero) return "无法核对 Windows Installer 服务，未启动运行库操作。";
        try
        {
            var service = OpenService(scm, "msiserver", 4);
            if (service == IntPtr.Zero) return Evaluate(IsSafeMode, false, false, false);
            try
            {
                if (!QueryServiceStatus(service, out var state)) return "Windows Installer 服务状态无法确认，未启动运行库操作。";
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\msiserver");
                return Evaluate(IsSafeMode, true, key?.GetValue("Start") is int start && start == 4, state.CurrentState == 4);
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(scm); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus
    { public uint Type, CurrentState, Accepted, Win32ExitCode, ServiceExitCode, Checkpoint, WaitHint; }
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr OpenService(IntPtr scm, string name, uint access);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
}

/// <summary>Data only: no executable, command, task or automatic restart is stored.</summary>
internal sealed class Vc14ContinuationStore(string root)
{
    private string PathName => Path.Combine(root, "vc14-awaiting-install.json");
    internal Vc14Continuation? Read()
    {
        if (!File.Exists(PathName)) return null;
        if (new FileInfo(PathName).Length > 4096) throw new IOException("14.x 待安装记录异常，保留记录，请人工核对。");
        var record = JsonSerializer.Deserialize<Vc14Continuation>(File.ReadAllText(PathName));
        if (record is null || !Guid.TryParseExact(record.Id, "D", out _) || record.CreatedUtc == default
            || !Enum.IsDefined(record.Baseline))
            throw new IOException("14.x 待安装记录损坏，保留记录，请人工核对。");
        return record;
    }
    internal void Save(Vc14Continuation record)
    {
        Directory.CreateDirectory(root);
        var temp = PathName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, record); file.Flush(true); }
            File.Move(temp, PathName, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal void Complete()
    {
        if (!File.Exists(PathName)) return;
        var record = Read()!;
        var history = Path.Combine(root, "runtime-repair-history"); Directory.CreateDirectory(history);
        File.Move(PathName, Path.Combine(history, record.Id + "-baseline-verified.json"), true);
    }
}
