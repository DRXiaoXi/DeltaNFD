using System.Diagnostics;
using System.Text.Json;

namespace DeltaNFD.Services;

internal sealed class RuntimeInstallationReview(string root)
{
    private sealed record Activity(string Id, string Stage, int Pid, long StartTicks, string Executable,
        int OwnerPid, long OwnerStartTicks, DateTimeOffset CreatedUtc);
    private string RecordPath => Path.Combine(root, "runtime-repair-active.json");

    internal string? CheckPending(bool installerBusy) => ReadPending(installerBusy, archiveEnded: true, out _);

    internal string? InspectPending(bool installerBusy, out bool endedUnknown)
        => ReadPending(installerBusy, archiveEnded: false, out endedUnknown);

    private string? ReadPending(bool installerBusy, bool archiveEnded, out bool endedUnknown)
    {
        endedUnknown = false;
        if (Directory.Exists(RecordPath)) return "安装记录路径被目录占用，状态无法确认。";
        if (!File.Exists(RecordPath)) return null;
        try
        {
            var record = JsonSerializer.Deserialize<Activity>(File.ReadAllText(RecordPath))
                ?? throw new InvalidDataException("安装记录为空");
            if (!Guid.TryParse(record.Id, out _) || record.OwnerPid <= 0 || record.OwnerStartTicks <= 0
                || !Path.IsPathFullyQualified(record.Executable) || record.Stage is not ("prepared" or "running")) throw new InvalidDataException("安装身份记录无效");
            if (record.Stage == "prepared")
                return "上次安装器启动结果未确认；启动前记录已保留，记录保持待核对。";
            if (record.Pid <= 0 || record.StartTicks <= 0) return "安装器身份缺失，记录保持待核对。";
            using var process = TryFind(record.Pid);
            if (process is not null && !process.HasExited && process.StartTime.ToUniversalTime().Ticks == record.StartTicks)
                return $"上次安装器仍运行（PID {record.Pid}），暂不能归档。";
            if (installerBusy) return "旧安装器进程已退出，但 Windows Installer 事务仍忙，暂不能归档。";
            endedUnknown = true;
            if (!archiveEnded) return null;
            // Confirmation of exit is not confirmation of a successful operation. Archive as unknown.
            var archive = Path.Combine(root, "runtime-repair-history");
            Directory.CreateDirectory(archive);
            File.Move(RecordPath, Path.Combine(archive, record.Id + "-ended-unknown.json"));
            Log.Warn("安装器已确认退出，未确认结果的证据已归档；不代表操作成功或已恢复。");
            return null;
        }
        catch (Exception ex) { return "上次安装记录无法确认，记录保持待核对：" + ex.Message; }
    }

    private static Process? TryFind(int pid)
    { try { return Process.GetProcessById(pid); } catch (ArgumentException) { return null; } }

    internal static bool InstallerBusy()
    {
        foreach (var name in new[] { @"Global\_MSIExecute", "_MSIExecute" })
        {
            if (!Mutex.TryOpenExisting(name, out var mutex)) continue;
            using (mutex)
            {
                var acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) return true;
                }
                finally { if (acquired) mutex.ReleaseMutex(); }
            }
        }
        return false;
    }
}
