using System.Text.Json;
using System.Text.Json.Serialization;
using DeltaNFD.Backends;

namespace DeltaNFD.Services;

public sealed record DriverPriorityBackup(int Schema, string Machine, string InstanceId, string Identity,
    DriverInterruptSettings Original, DriverInterruptSettings Before, DriverInterruptSettings Applied, DateTimeOffset WrittenUtc)
{
    [JsonIgnore] public string Id => DriverPriorityPolicy.Hash(InstanceId.ToUpperInvariant());
    [JsonIgnore] public string Digest => DriverPriorityPolicy.Hash(JsonSerializer.Serialize(this));
    [JsonIgnore] public bool Pending => Original != Applied || Original != Before;
}

/// <summary>Bounded software-data journal. Original, pre-write and intended values survive an interrupted batch.</summary>
public sealed class DriverPriorityStore(string directory, string machine)
{
    private readonly string _directory = Path.GetFullPath(directory);
    public Action? BeforeSaveForTests { get; set; }
    public string Machine => machine;
    public T Exclusive<T>(Func<T> work)
    {
        // Hardware is global, so different software data roots must serialize too.
        using var mutex = new Mutex(false, @"Global\DeltaNFD.DriverPriority.System.v1");
        var entered = false;
        try { try { entered = mutex.WaitOne(TimeSpan.FromSeconds(10)); } catch (AbandonedMutexException) { entered = true; }
            if (!entered) throw new IOException("另一项驱动中断设置正在执行。"); return work(); }
        finally { if (entered) mutex.ReleaseMutex(); }
    }
    public static void RejectLinks(string path)
    {
        for (var p = Path.GetFullPath(path); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
        {
            try { if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("驱动设置备份路径包含重解析点。"); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
    private string PathFor(string id)
    {
        if (!DriverPriorityPolicy.ValidHash(id)) throw new InvalidDataException("备份标识无效。");
        var path = Path.Combine(_directory, id.ToUpperInvariant() + ".json"); RejectLinks(path); return path;
    }
    public IReadOnlyList<DriverPriorityBackup> ReadAll()
    {
        RejectLinks(_directory); if (!Directory.Exists(_directory)) return [];
        var files = Directory.GetFiles(_directory, "*.json");
        if (files.Length > 1024 || files.Sum(p => new FileInfo(p).Length) > 16L * 1024 * 1024) throw new IOException("驱动设置备份数量或总量超过上限。");
        return files.Select(p =>
        {
            RejectLinks(p); using var stream = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read);
            var b = Decode(stream);
            if (b.Schema != 1 || b.Machine != machine || !DriverPriorityPolicy.ValidId(b.InstanceId)
                || !DriverPriorityPolicy.ValidHash(b.Identity) || Path.GetFileNameWithoutExtension(p) != b.Id
                || b.Original is null || b.Before is null || b.Applied is null || b.WrittenUtc == default)
                throw new IOException("驱动设置备份的设备、计算机或格式无法核对。");
            DriverPriorityPolicy.Validate(b.Original); DriverPriorityPolicy.Validate(b.Before); DriverPriorityPolicy.Validate(b.Applied); return b;
        }).ToArray();
    }
    private static DriverPriorityBackup Decode(FileStream stream)
    {
        if (stream.Length > 32768) throw new IOException("驱动设置备份过大。");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        using var doc = BackendJson.ParseStrict(bytes, "驱动设置备份");
        return JsonSerializer.Deserialize<DriverPriorityBackup>(doc.RootElement) ?? throw new IOException("驱动设置备份为空。");
    }
    public IDisposable Hold(DriverPriorityBackup record)
    {
        var stream = new FileStream(PathFor(record.Id), FileMode.Open, FileAccess.Read, FileShare.Read);
        try { if (Decode(stream).Digest != record.Digest) throw new IOException("原值记录已变化，未进入写入。"); return stream; }
        catch { stream.Dispose(); throw; }
    }
    public void Save(DriverPriorityBackup record)
    {
        BeforeSaveForTests?.Invoke();
        var path = PathFor(record.Id); Directory.CreateDirectory(_directory); RejectLinks(_directory);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, record); stream.Flush(true); }
            File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        if (ReadAll().Single(b => b.Id == record.Id).Digest != record.Digest) throw new IOException("驱动设置备份写后核对不符。");
    }
    public void Archive(DriverPriorityBackup record)
    {
        var current = ReadAll().Single(b => b.Id == record.Id);
        if (current.Digest != record.Digest) throw new IOException("驱动设置备份已变化，未归档。");
        var archive = Path.Combine(_directory, "archive"); RejectLinks(archive); Directory.CreateDirectory(archive); RejectLinks(archive);
        File.Move(PathFor(record.Id), Path.Combine(archive, record.Id + "-" + Guid.NewGuid().ToString("N") + ".json"));
    }
}
