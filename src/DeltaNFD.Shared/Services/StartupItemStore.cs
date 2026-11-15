using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;

namespace DeltaNFD.Services;

/// <summary>停用前的原始启动登记。只允许固定Run范围和启动目录快捷方式。</summary>
public sealed record StartupItemOriginal(StartupItemScope Scope, string Name, int Kind, string Data,
    long CreatedUtcTicks = 0, long WrittenUtcTicks = 0, int Attributes = 0, string? ZoneIdentifier = null, string SecurityDescriptor = "")
{
    [JsonIgnore] public string Id => Hash($"{(int)Scope}|{Name.ToUpperInvariant()}");
    [JsonIgnore] public string Fingerprint => Hash(JsonSerializer.Serialize(this));
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public bool SameContent(StartupItemOriginal other) => Scope == other.Scope
        && Name.Equals(other.Name, StringComparison.OrdinalIgnoreCase) && Kind == other.Kind && Data == other.Data
        && ZoneIdentifier == other.ZoneIdentifier && SecurityDescriptor == other.SecurityDescriptor;
}
public sealed record StartupItemBackup(int Schema, string UserSid, StartupItemOriginal Original)
{
    [JsonIgnore] public string Fingerprint => StartupItemOriginal.Hash(JsonSerializer.Serialize(this));
}

public sealed class StartupItemStore(string directory, string userSid)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly string _directory = Path.GetFullPath(directory);
    public static bool ValidId(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("自启动项目路径包含重解析点，已拒绝操作。"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    public T Exclusive<T>(Func<T> work)
    {
        using var mutex = new Mutex(false, @"Global\DeltaNFD.StartupItems." + StartupItemOriginal.Hash(_directory.ToUpperInvariant()));
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("自启动项目正在由另一操作处理，请稍后重试。");
            return work();
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }
    private string PathFor(string id)
    {
        if (!ValidId(id)) throw new InvalidDataException("自启动项目标识无效。");
        var path = Path.Combine(_directory, id + ".json"); RejectLinks(path); return path;
    }
    public IReadOnlyList<StartupItemBackup> ReadAll() => Exclusive(ReadAllCore);
    private IReadOnlyList<StartupItemBackup> ReadAllCore()
    {
        RejectLinks(_directory);
        if (!Directory.Exists(_directory)) return [];
        var files = Directory.GetFiles(_directory, "*.json", SearchOption.TopDirectoryOnly);
        if (files.Length > 1024) throw new InvalidDataException("自启动项目备份数量超过上限。");
        if (files.Sum(p => new FileInfo(p).Length) > 16L * 1024 * 1024) throw new InvalidDataException("自启动备份总量超过16MiB，请先核对已有记录。");
        return files.Select(path => ReadCore(Path.GetFileNameWithoutExtension(path)) ?? throw new IOException("读取期间自启动备份发生变化。")).ToArray();
    }
    public StartupItemBackup? Read(string id) => Exclusive(() => ReadCore(id));
    private StartupItemBackup? ReadCore(string id)
    {
        var path = PathFor(id);
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 2 * 1024 * 1024) throw new InvalidDataException("自启动备份过大。");
        var record = JsonSerializer.Deserialize<StartupItemBackup>(stream) ?? throw new InvalidDataException("自启动备份为空。");
        var original = record.Original ?? throw new InvalidDataException("自启动原始记录缺失。");
        Validate(original);
        if (record.Schema != 1 || record.UserSid != userSid || original.Id != id.ToUpperInvariant())
            throw new InvalidDataException("自启动备份版本、用户或标识无法核对。");
        return record;
    }
    public static void Validate(StartupItemOriginal item)
    {
        if (!Enum.IsDefined(item.Scope) || item.Name is null || item.Name.Length > 512 || item.Name.Any(char.IsControl)
            || item.Data is null || item.Data.Length > 1400000) throw new InvalidDataException("自启动原始记录无效。");
        if (item.Scope is StartupItemScope.UserFolder or StartupItemScope.CommonFolder)
        {
            if (item.Name != Path.GetFileName(item.Name) || item.Name.IndexOfAny(['\\', '/', ':']) >= 0
                || !new[] { ".lnk", ".url" }.Contains(Path.GetExtension(item.Name), StringComparer.OrdinalIgnoreCase)
                || item.Kind != 0 || item.CreatedUtcTicks <= 0 || item.WrittenUtcTicks <= 0
                || item.CreatedUtcTicks > DateTime.MaxValue.Ticks || item.WrittenUtcTicks > DateTime.MaxValue.Ticks
                || (item.Attributes & (int)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidDataException("仅支持固定启动目录中的快捷方式。");
            const FileAttributes restorable = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive
                | FileAttributes.Normal | FileAttributes.Temporary | FileAttributes.NotContentIndexed;
            if (((FileAttributes)item.Attributes & ~restorable) != 0) throw new InvalidDataException("快捷方式包含无法完整恢复的文件属性，保留只读。");
            if (Convert.FromBase64String(item.Data).Length > 1024 * 1024) throw new InvalidDataException("启动快捷方式过大。");
            if (item.ZoneIdentifier is not null && Convert.FromBase64String(item.ZoneIdentifier).Length > 65536)
                throw new InvalidDataException("快捷方式来源标记过大。");
            if (string.IsNullOrEmpty(item.SecurityDescriptor) || item.SecurityDescriptor.Length > 32768)
                throw new InvalidDataException("启动快捷方式的访问权限备份无法核对。");
            _ = new System.Security.AccessControl.RawSecurityDescriptor(item.SecurityDescriptor);
        }
        else if (item.Kind is not (1 or 2) || item.Data.Length > 32768 || item.Data.Contains('\0')
            || item.CreatedUtcTicks != 0 || item.WrittenUtcTicks != 0 || item.Attributes != 0
            || item.ZoneIdentifier is not null || item.SecurityDescriptor != "")
            throw new InvalidDataException("仅支持字符串类型的Run启动登记。");
    }
    public StartupItemBackup Save(StartupItemOriginal original) => Exclusive(() => SaveCore(original));
    private StartupItemBackup SaveCore(StartupItemOriginal original)
    {
        Validate(original);
        var path = PathFor(original.Id);
        if (ReadCore(original.Id) is not null) throw new IOException("已有原始备份，请先处理该项恢复记录。");
        Directory.CreateDirectory(_directory); RejectLinks(_directory);
        var record = new StartupItemBackup(1, userSid, original);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("自启动备份序列化后过大，未保存或修改。");
        var existing = Directory.GetFiles(_directory, "*.json", SearchOption.TopDirectoryOnly);
        if (existing.Length >= 1024 || existing.Sum(p => new FileInfo(p).Length) + bytes.Length > 16L * 1024 * 1024)
            throw new InvalidDataException("自启动备份数量或总量达到上限，未修改启动登记。");
        var temp = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temp, path, overwrite: false);
            if (ReadCore(original.Id)?.Fingerprint != record.Fingerprint) throw new IOException("自启动备份回读不一致。");
            return record;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Archive(string id, string expectedFingerprint) => Exclusive(() => { ArchiveCore(id, expectedFingerprint); return true; });
    private void ArchiveCore(string id, string expectedFingerprint)
    {
        var record = ReadCore(id) ?? throw new IOException("原始备份已变化。");
        if (record.Fingerprint != expectedFingerprint) throw new IOException("原始备份已变化，未结束恢复记录。");
        var reviewed = Path.Combine(_directory, "reviewed"); RejectLinks(reviewed); Directory.CreateDirectory(reviewed); RejectLinks(reviewed);
        File.Move(PathFor(id), Path.Combine(reviewed, id + "-" + Guid.NewGuid().ToString("N") + ".json"), overwrite: false);
        if (ReadCore(id) is not null) throw new IOException("备份记录收尾未完成。");
    }
}
