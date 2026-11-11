using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;

/// <summary>Explicit preservation of unreadable runtime evidence after a verified restart; never a repair result.</summary>
internal static class RuntimeCorruptRecordReview
{
    internal sealed record State(string Fingerprint, bool CanPrepare, bool CanPreserve, string Detail);
    private sealed record Checkpoint(string Fingerprint, string BootId);
    internal static Func<string?>? BootForTests { get; set; }
    internal static Func<bool>? BusyForTests { get; set; }
    private static string Record => Path.Combine(AppDataPaths.Root, "official-backend-unconfirmed", "runtime.json");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string CheckpointPath(string hash) => Path.Combine(AppDataPaths.Root, "official-backend-corrupt-review", "runtime-" + hash + ".restart.json");
    private static string? Boot()
    {
        var value = (BootForTests ?? WindowsBootIdentity.Read)();
        return Guid.TryParseExact(value, "D", out var guid) && guid != Guid.Empty ? guid.ToString("D") : null;
    }
    private static byte[] Read(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("记录含重解析点。");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 1024 * 1024) throw new IOException("损坏记录超过读取上限。");
        var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes); return bytes;
    }
    private static byte[] CorruptBytes()
    {
        var bytes = Read(Record);
        try { using var parsed = JsonDocument.Parse(bytes); }
        catch (JsonException) { return bytes; }
        throw new IOException("记录仍可解析，不适用损坏记录处理；请使用原核对流程。");
    }
    private static bool Busy()
    {
        if (BusyForTests is { } test) return test();
        if (BackendSessions.Snapshot().Any(c => c.ModuleId == BackendModules.Runtime && !c.ExitConfirmed)) return true;
        var processes = Process.GetProcessesByName("DeltaNFD.Backend.Runtime");
        try { if (processes.Any(p => !p.HasExited)) return true; }
        finally { foreach (var process in processes) process.Dispose(); }
        foreach (var name in new[] { @"Global\_MSIExecute", "_MSIExecute" })
        {
            if (!Mutex.TryOpenExisting(name, out var mutex)) continue;
            using (mutex)
            {
                var held = false;
                try { try { held = mutex.WaitOne(0); } catch (AbandonedMutexException) { held = true; } if (!held) return true; }
                finally { if (held) mutex.ReleaseMutex(); }
            }
        }
        return false;
    }

    internal static State? Inspect()
    {
        try
        {
            var bytes = CorruptBytes(); var hash = Hash(bytes); var boot = Boot();
            if (boot is null) return new(hash, false, false, "无法读取本次 Windows 启动身份，保留阻塞。");
            var path = CheckpointPath(hash);
            if (Directory.Exists(path)) throw new IOException("核对点路径被目录占用。");
            if (!File.Exists(path)) return new(hash, true, false,
                "旧记录损坏，无法取得原执行者身份。先建立核对点，保存工作后手动使用 Windows“重启”，再回到此处核对。建立核对点不会解除阻塞。");
            var checkpoint = JsonSerializer.Deserialize<Checkpoint>(Read(path));
            if (checkpoint is null || checkpoint.Fingerprint != hash || !Guid.TryParseExact(checkpoint.BootId, "D", out var id) || id == Guid.Empty)
                throw new IOException("核对点损坏，保留原件和阻塞。");
            if (checkpoint.BootId == boot) return new(hash, false, false, "核对点已建立，尚未确认 Windows 启动实例变化。请在方便时手动重启后再核对。");
            if (Busy()) return new(hash, false, false, "已确认启动实例变化，但运行库后端或 Windows Installer 尚未空闲，暂不归档。");
            return new(hash, false, true, "已确认核对点之后 Windows 启动实例变化，旧进程无法继续存活，且当前运行库后端与 Installer 空闲。可显式保留当前状态并归档损坏原件；不表示运行库安装或 DLL 已恢复。独立安装事务、防护和 DLL 备份保持，后续仍须到运行库页重新检测。");
        }
        catch (Exception) { return null; }
    }

    internal static string Prepare(State confirmed)
    {
        var current = Inspect();
        if (current is null || !current.CanPrepare || current.Fingerprint != confirmed.Fingerprint)
            throw new IOException("损坏记录或核对条件已变化，请重新核对。");
        var boot = Boot() ?? throw new IOException("启动身份未知。");
        var bytes = CorruptBytes();
        if (Hash(bytes) != confirmed.Fingerprint) throw new IOException("记录已变化。");
        var directory = Path.GetDirectoryName(CheckpointPath(confirmed.Fingerprint))!;
        var original = Path.Combine(directory, "runtime-" + confirmed.Fingerprint + ".original.bin");
        if (File.Exists(original))
        { if (!Read(original).SequenceEqual(bytes)) throw new IOException("已有原件副本指纹不符，未覆盖。"); }
        else BackendEvidenceFiles.WriteBytes(original, bytes);
        BackendEvidenceFiles.WriteJson(CheckpointPath(confirmed.Fingerprint), new Checkpoint(confirmed.Fingerprint, boot));
        return "核对点已建立，损坏原件和阻塞保留。请保存工作，在方便时手动使用 Windows 的“重启”，然后回来重新核对。不会自动重启或重新安装运行库。";
    }

    internal static string Preserve(State confirmed)
    {
        var current = Inspect();
        if (current is null || !current.CanPreserve || current.Fingerprint != confirmed.Fingerprint)
            throw new IOException("尚未满足重启核对条件，原件和阻塞保留。");
        var bytes = CorruptBytes();
        if (Hash(bytes) != confirmed.Fingerprint || Busy()) throw new IOException("记录或进程状态已变化，未归档。");
        var directory = Path.Combine(AppDataPaths.Root, "official-backend-reviewed");
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("归档目录含重解析点。");
        var destination = Path.Combine(directory, "runtime-corrupt-kept-current-" + Guid.NewGuid().ToString("N") + ".json");
        BackendEvidenceFiles.WriteJson(destination + ".outcome.json", new { Outcome = "corrupt-kept-current-after-restart",
            Fingerprint = confirmed.Fingerprint, BootId = Boot(), Repaired = false, Utc = DateTimeOffset.UtcNow });
        using (var held = new FileStream(Record, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            var locked = new byte[(int)held.Length]; held.ReadExactly(locked);
            if (!locked.SequenceEqual(bytes) || Busy()) throw new IOException("归档前条件已变化。");
            File.Move(Record, destination);
        }
        if (!Read(destination).SequenceEqual(bytes)) throw new IOException("归档内容未能复核，保留证据。");
        return destination;
    }
}
