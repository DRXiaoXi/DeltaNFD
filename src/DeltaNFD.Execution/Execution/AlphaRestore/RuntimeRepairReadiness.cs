using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeltaNFD.Services.AlphaRestore;

internal sealed record RuntimeRepairReadinessInput(bool Windows, bool X64, bool Elevated,
    RuntimePreflightSnapshot Preflight, bool? GameRunning, string? ProtectionBlocker, string? PendingOperation,
    long? SystemFreeBytes, long? TempFreeBytes, string TempWriteError, string DiagnosticWriteError, string QueryError = "",
    bool ProtectionKnown = true, bool PendingKnown = true, bool EndedUnknown = false);

/// <summary>统一的卸载前基础环境门禁。只查询状态并测试专用临时文件，不修改服务、权限或注册表。</summary>
internal static class RuntimeRepairReadiness
{
    internal const long MinimumFreeBytes = 2L * 1024 * 1024 * 1024;
    internal static RuntimeRepairEnvironmentReport Evaluate(RuntimeRepairReadinessInput input)
    {
        var items = new List<RuntimeRepairEnvironmentItem>();
        void Add(string name, string status, string detail, string suggestion = "") => items.Add(new(name, status, detail, suggestion));
        Add("系统与架构", input.Windows && input.X64 ? "passed" : "blocked",
            input.Windows && input.X64 ? "当前为支持的 Windows x64 环境。" : "当前修复资源与流程要求 Windows x64。", "");
        Add("管理员权限", input.Elevated ? "passed" : "blocked", input.Elevated ? "已取得管理员权限。" : "未取得管理员权限。", input.Elevated ? "" : "以管理员身份重新运行工具。");
        var snapshot = input.Preflight;
        if (snapshot.Error.Length > 0 || snapshot.Services.Count != 2)
            Add("安装服务状态", "unknown", snapshot.Error.Length > 0 ? snapshot.Error : "Windows Installer/TrustedInstaller 查询不完整。", "检查系统服务和日志，确认状态后重新检测。");
        else foreach (var service in snapshot.Services)
        {
            var unknown = service.Error.Length > 0 || service.StartType is not (2 or 3 or 4) || service.State is < 1 or > 7;
            var blocked = service.StartType == 4 || service.State is 2 or 3 or 5 or 6 or 7;
            Add(service.Name, unknown ? "unknown" : blocked ? "blocked" : "passed",
                unknown ? "无法可靠读取服务配置：" + service.Error : service.StartType == 4 ? "服务已禁用。"
                    : blocked ? "服务正在切换状态或已暂停。" : "服务可用；按需启动的服务处于停止状态也是正常情况。",
                unknown ? "确认服务配置可读取后再试。" : blocked ? "先在 Windows 服务管理中核对并恢复正常配置，或等待状态切换结束；不会自动修改。" : "");
        }
        Add("安装事务", snapshot.Error.Length > 0 || !input.PendingKnown ? "unknown" : snapshot.InstallerBusy || input.PendingOperation is not null ? "blocked" : input.EndedUnknown ? "warning" : "passed",
            !input.PendingKnown ? "安装事务身份无法确认。" : input.PendingOperation ?? (snapshot.InstallerBusy ? "Windows Installer 正在处理其他事务。"
                : input.EndedUnknown ? "上次安装进程已退出，但旧结果尚未确认；原始证据保留，正式修复仍需重新扫描。" : "未发现正在运行或启动结果未确认的安装事务。"),
            snapshot.InstallerBusy || input.PendingOperation is not null ? "等待相关安装器结束；若有未确认记录，请先核对。不要强杀 msiexec 或删除事务记录。" : "");
        Add("重启状态", snapshot.Error.Length > 0 ? "unknown" : snapshot.ServicingRebootPending ? "blocked" : snapshot.RebootPending ? "warning" : "passed",
            snapshot.ServicingRebootPending ? "Windows 组件维护或更新要求重启。" : snapshot.RebootPending ? "存在文件重命名等一般待重启记录。" : "未发现待重启记录。",
            snapshot.ServicingRebootPending ? "保存工作并重启电脑后再检测，避免先卸载再遇到系统维护冲突。" : snapshot.RebootPending ? "建议先重启再修复；该记录不直接证明安装必定失败。" : "");
        Add("组件缓存访问", snapshot.WinSxSWarning.Length == 0 && snapshot.Error.Length == 0 ? "passed" : "unknown",
            snapshot.WinSxSWarning.Length == 0 ? "WinSxS 基础访问检查通过；不代表系统组件完整性验证。" : snapshot.WinSxSWarning,
            snapshot.WinSxSWarning.Length > 0 ? "先核对 Windows 组件维护状态；不要批量接管 WinSxS 权限。" : "");
        Add("运行库防护", !input.ProtectionKnown ? "unknown" : input.ProtectionBlocker is null ? "passed" : "blocked",
            !input.ProtectionKnown ? "防护状态无法确认。" : input.ProtectionBlocker ?? "未发现阻止安装/卸载的运行库防护配置。",
            input.ProtectionBlocker is not null ? "先通过工具关闭防护并按记录恢复，再重新检查。" : "");
        Add("三角洲运行状态", input.GameRunning is null ? "unknown" : input.GameRunning.Value ? "blocked" : "passed",
            input.GameRunning is null ? "游戏进程状态无法确认。" : input.GameRunning.Value ? "三角洲主进程仍在运行。" : "三角洲主进程未运行。",
            input.GameRunning == true ? "先退出游戏；也建议关闭其他依赖 C++ 运行库的软件。" : "");
        void Disk(string name, long? free) => Add(name, free is null ? "unknown" : free < MinimumFreeBytes ? "blocked" : "passed",
            free is null ? "可用空间无法确认。" : $"可用 {free.Value / 1073741824d:0.00} GiB；修复前保守预留至少 2 GiB。",
            free is null ? "确认目录所在磁盘可访问。" : free < MinimumFreeBytes ? "先清理磁盘空间。该阈值为工具的保守预留，不是安装成功保证。" : "");
        Disk("系统盘空间", input.SystemFreeBytes); Disk("临时目录所在盘空间", input.TempFreeBytes);
        Add("临时目录读写", input.TempWriteError.Length == 0 ? "passed" : "blocked", input.TempWriteError.Length == 0 ? "专用临时文件写入、回读和清理通过。" : input.TempWriteError, input.TempWriteError.Length > 0 ? "检查 TEMP 目录权限、磁盘空间与安全软件拦截日志。" : "");
        Add("诊断目录读写", input.DiagnosticWriteError.Length == 0 ? "passed" : "blocked", input.DiagnosticWriteError.Length == 0 ? "可以保存安装日志与恢复诊断。" : input.DiagnosticWriteError, input.DiagnosticWriteError.Length > 0 ? "确认工具数据目录可写，避免操作后没有诊断依据。" : "");
        if (input.QueryError.Length > 0) Add("其他预检", "unknown", input.QueryError, "处理查询失败后重新检测。");
        return new(items);
    }

    internal static async Task<RuntimeRepairEnvironmentReport> ReadAsync(RuntimeGuardService service, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var snapshot = RuntimeRepairPreflight.Read();
        bool? game = null;
        var queryError = "";
        try
        {
            var processes = Process.GetProcessesByName(DeltaForceLocator.GameProcessName);
            try { game = processes.Length > 0; } finally { foreach (var process in processes) process.Dispose(); }
        }
        catch (Exception ex) { queryError = "游戏状态查询失败：" + ex.Message; }
        string? protection = null, pending = null;
        bool protectionKnown = true, pendingKnown = true, endedUnknown = false;
        try { protection = await service.RepairProtectionBlockerAsync().WaitAsync(token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { protectionKnown = false; queryError += "\n防护状态无法确认：" + ex.Message; }
        try { pending = new RuntimeRepairProcessRunner(AppDataPaths.Root).InspectPending(snapshot.InstallerBusy, out endedUnknown); }
        catch (Exception ex) { pendingKnown = false; queryError += "\n安装事务无法确认：" + ex.Message; }
        token.ThrowIfCancellationRequested();
        var temp = Path.GetTempPath();
        return Evaluate(new(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763), RuntimeInformation.OSArchitecture == Architecture.X64,
            ElevationHelper.IsElevated, snapshot, game, protection, pending, FreeSpace(Environment.SystemDirectory), FreeSpace(temp),
            ProbeWrite(temp), ProbeWrite(Path.Combine(AppDataPaths.Root, "logs")), queryError.Trim(), protectionKnown, pendingKnown, endedUnknown));
    }

    private static long? FreeSpace(string directory)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!);
            return drive.DriveType == DriveType.Fixed && drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch { return null; }
    }

    internal static string ProbeWrite(string directory)
    {
        string? path = null;
        var created = false;
        try
        {
            if (!Path.IsPathFullyQualified(directory) || directory.StartsWith(@"\\", StringComparison.Ordinal)) return "临时/诊断目录不是本地绝对路径。";
            for (var parent = new DirectoryInfo(directory); parent is not null; parent = parent.Parent)
                if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) return "目录含重解析点，无法可靠确认写入目标。";
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, "runtime-preflight-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                created = true;
                file.WriteByte(0x5a); file.Flush(true); file.Position = 0;
                if (file.ReadByte() != 0x5a) return "专用临时文件回读不一致。";
            }
            File.Delete(path); path = null;
            return "";
        }
        catch (Exception ex) { return ex.Message; }
        finally { if (created && path is not null) try { File.Delete(path); } catch { } }
    }
}
