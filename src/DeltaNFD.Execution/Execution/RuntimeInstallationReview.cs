using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace DeltaNFD.Services;

internal sealed class RuntimeInstallationReview(string root, Func<string?>? bootIdentity = null,
    Func<int, long, bool>? alive = null)
{
    private sealed record Activity(string Id, string Stage, int Pid, long StartTicks, string Executable,
        int OwnerPid, long OwnerStartTicks, DateTimeOffset CreatedUtc, string? BootId = null);
    private sealed record RestartCheckpoint(string ActivityId, string ActivityHash, string BootId);
    private string RecordPath => Path.Combine(root, "runtime-repair-active.json");
    private string CheckpointPath => Path.Combine(root, "runtime-repair-restart.json");

    private static byte[] ReadEvidence(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("安装记录含重解析点。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > 1024 * 1024) throw new InvalidDataException("安装记录大小无效。");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); return bytes;
    }

    private Activity ReadActivity(out byte[] bytes)
    {
        bytes = ReadEvidence(RecordPath);
        var record = JsonSerializer.Deserialize<Activity>(bytes) ?? throw new InvalidDataException("安装记录为空");
        if (!Guid.TryParse(record.Id, out var id) || id == Guid.Empty || record.OwnerPid <= 0 || record.OwnerStartTicks <= 0
            || !Path.IsPathFullyQualified(record.Executable) || record.Stage is not ("prepared" or "running")
            || record.BootId is { Length: > 0 } && !ValidBoot(record.BootId)) throw new InvalidDataException("安装身份记录无效");
        return record;
    }

    private static bool ValidBoot(string? value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty;
    private string? Boot() { var value = (bootIdentity ?? WindowsBootIdentity.Read)(); return ValidBoot(value) ? value : null; }
    private bool Alive(int pid, long ticks)
    {
        if (alive is not null) return alive(pid, ticks);
        using var process = TryFind(pid);
        return process is not null && !process.HasExited && process.StartTime.ToUniversalTime().Ticks == ticks;
    }
    private static bool Untracked(Activity record) => record.Stage == "prepared" || record.Pid <= 0 || record.StartTicks <= 0;
    private bool CrossedRestart(Activity record, byte[] bytes, string? currentBoot)
    {
        if (currentBoot is null) return false;
        if (ValidBoot(record.BootId)) return !string.Equals(record.BootId, currentBoot, StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(CheckpointPath)) throw new IOException("重启核对点路径被目录占用。");
        if (!File.Exists(CheckpointPath)) return false;
        var checkpoint = JsonSerializer.Deserialize<RestartCheckpoint>(ReadEvidence(CheckpointPath))
            ?? throw new InvalidDataException("重启核对点为空。");
        if (!ValidBoot(checkpoint.BootId)) throw new InvalidDataException("重启核对点启动身份无效。");
        return checkpoint.ActivityId == record.Id && checkpoint.ActivityHash == Convert.ToHexString(SHA256.HashData(bytes))
            && !string.Equals(checkpoint.BootId, currentBoot, StringComparison.OrdinalIgnoreCase);
    }

    // Explicit user action only. Inspection never creates a checkpoint or rewrites the old activity.
    internal bool CanPrepareRestartCheckpoint(bool installerBusy)
    {
        try
        {
            var record = ReadActivity(out var bytes); var boot = Boot();
            if (installerBusy || !Untracked(record) || ValidBoot(record.BootId) || boot is null || Alive(record.OwnerPid, record.OwnerStartTicks)
                || record.Pid > 0 && record.StartTicks > 0 && Alive(record.Pid, record.StartTicks)
                || CrossedRestart(record, bytes, boot)) return false;
            if (!File.Exists(CheckpointPath)) return !Directory.Exists(CheckpointPath);
            var checkpoint = JsonSerializer.Deserialize<RestartCheckpoint>(ReadEvidence(CheckpointPath));
            return checkpoint is not null && (checkpoint.ActivityId != record.Id
                || checkpoint.ActivityHash != Convert.ToHexString(SHA256.HashData(bytes))
                || !string.Equals(checkpoint.BootId, boot, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    internal string PrepareRestartCheckpoint(bool installerBusy, string expectedFingerprint)
    {
        if (!CanPrepareRestartCheckpoint(installerBusy)) throw new IOException("尚不满足建立重启核对点的条件；请重新核对。");
        var record = ReadActivity(out var bytes); var boot = Boot() ?? throw new IOException("无法读取启动身份。");
        if (Convert.ToHexString(SHA256.HashData(bytes)) != expectedFingerprint) throw new IOException("安装记录已变化，请重新核对。");
        if (Alive(record.OwnerPid, record.OwnerStartTicks)) throw new IOException("原执行者仍在运行，未建立核对点。");
        if (File.Exists(CheckpointPath))
        {
            var history = Path.Combine(root, "runtime-repair-history"); Directory.CreateDirectory(history);
            File.Copy(CheckpointPath, Path.Combine(history, "restart-checkpoint-" + Guid.NewGuid().ToString("N") + ".json"));
        }
        if (!ReadEvidence(RecordPath).SequenceEqual(bytes)) throw new IOException("安装记录已变化，请重新核对。");
        RuntimeOperationJournal.Write(CheckpointPath, new RestartCheckpoint(record.Id,
            Convert.ToHexString(SHA256.HashData(bytes)), boot), overwrite: true);
        return "已建立重启核对点，原始事务和模块阻塞仍保留。请保存工作，在方便时使用 Windows 的“重启”，再回到此处核对。"
            + "\n不会自动重启，也不会重新执行上次修复；重启后仍须确认 MSI 空闲并扫描当前运行库。";
    }

    internal string Fingerprint() => Directory.Exists(RecordPath) ? throw new IOException("安装记录路径被目录占用。")
        : File.Exists(RecordPath) ? Convert.ToHexString(SHA256.HashData(ReadEvidence(RecordPath))) : "";

    internal string? CheckPending(bool installerBusy, string? expectedFingerprint = null)
        => ReadPending(installerBusy, archiveEnded: true, out _, expectedFingerprint);

    internal string? InspectPending(bool installerBusy, out bool endedUnknown)
        => ReadPending(installerBusy, archiveEnded: false, out endedUnknown);

    private string? ReadPending(bool installerBusy, bool archiveEnded, out bool endedUnknown, string? expectedFingerprint = null)
    {
        endedUnknown = false;
        if (Directory.Exists(RecordPath)) return "安装记录路径被目录占用，状态无法确认。";
        if (!File.Exists(RecordPath)) return expectedFingerprint is { Length: > 0 } ? "安装记录已变化，请重新核对。" : null;
        try
        {
            if (expectedFingerprint is not null && Fingerprint() != expectedFingerprint)
                return "安装记录已变化，请重新核对。";
            var record = ReadActivity(out var bytes);
            if (record.Pid > 0 && record.StartTicks > 0 && Alive(record.Pid, record.StartTicks))
                return $"上次安装器仍运行（PID {record.Pid}），暂不能归档。";
            if (Untracked(record))
            {
                if (Alive(record.OwnerPid, record.OwnerStartTicks)) return "原安装执行者仍在运行，暂不能核对结束。";
                if (!CrossedRestart(record, bytes, Boot()))
                    return ValidBoot(record.BootId)
                        ? "安装器身份未确认，尚未取得启动身份变化的证据。请在方便时使用 Windows 的“重启”，然后重新核对；启动身份读取失败时继续保留阻塞。"
                        : "安装器启动结果或身份未确认。请先建立重启核对点，在方便时使用 Windows 的“重启”，然后重新核对；已有核对点时直接重启即可。启动身份读取失败时继续保留阻塞。";
            }
            if (installerBusy) return "旧安装器进程已退出，但 Windows Installer 事务仍忙，暂不能归档。";
            endedUnknown = true;
            if (!archiveEnded) return null;
            // Confirmation of exit is not confirmation of a successful operation. Archive as unknown.
            var archive = Path.Combine(root, "runtime-repair-history");
            Directory.CreateDirectory(archive);
            if (!ReadEvidence(RecordPath).SequenceEqual(bytes)) throw new IOException("安装记录已变化，请重新核对。");
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
