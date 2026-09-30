using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>一条被修改前的注册表原始值备份。</summary>
public sealed class RegistryValueBackup
{
    /// <summary>唯一标识：{Hive}\{键路径}\{值名}（小写）。保存时自动按 Hive/KeyPath/ValueName 生成。</summary>
    public string Id { get; set; } = "";

    /// <summary>注册表 Hive：HKLM（默认，兼容旧备份）或 HKCU。</summary>
    public string Hive { get; set; } = "HKLM";

    /// <summary>Hive 内的键路径。</summary>
    public string KeyPath { get; set; } = "";

    public string ValueName { get; set; } = "";

    /// <summary>原始值的类型；None 表示修改前该值不存在（恢复时应删除该值）。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RegistryValueKind ValueKind { get; set; } = RegistryValueKind.String;

    /// <summary>原始值数据：REG_SZ 存原文，REG_DWORD 存十进制文本。</summary>
    public string Data { get; set; } = "";

    /// <summary>REG_MULTI_SZ 的无损备份；旧备份继续使用 Data。</summary>
    public string[]? StringData { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// 统一备份仓库：所有改过的注册表值的原始值都记到这里，供「恢复默认」使用。
/// 存储位置：%APPDATA%\Delta NFD\backups.json。
/// 每个值只保留「第一次修改前」的原始值（重复修改不会覆盖最早的备份），恢复后删除对应条目。
/// </summary>
public sealed class TweakBackupStore
{
    // 注意：DefaultPath 必须声明在 Default 之前 —— 静态初始化按声明顺序执行，
    // 否则 Default 实例构造时拿到的是 null 路径。
    private static readonly string DefaultPath = Path.Combine(AppDataPaths.Root, "backups.json");

    public static TweakBackupStore Default { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _filePath;
    private readonly object _gate = new();

    public TweakBackupStore() : this(DefaultPath)
    {
    }

    public TweakBackupStore(string filePath)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath) ? DefaultPath : filePath;
    }

    public static string MakeId(string keyPath, string valueName)
        => MakeId("HKLM", keyPath, valueName);

    public static string MakeId(string hive, string keyPath, string valueName)
        => $@"{hive}\{keyPath.TrimStart('\\')}\{valueName}".ToLowerInvariant();

    public RegistryValueBackup? Get(string keyPath, string valueName) => Get("HKLM", keyPath, valueName);

    public RegistryValueBackup? Get(string hive, string keyPath, string valueName)
    {
        var id = MakeId(hive, keyPath, valueName);
        lock (_gate)
        {
            return LoadAll().FirstOrDefault(b => b.Id == id);
        }
    }

    /// <summary>用于恢复操作：备份文件无法读取时必须报错，不能把它当成“没有备份”。</summary>
    public RegistryValueBackup? GetStrict(string hive, string keyPath, string valueName)
    {
        var id = MakeId(hive, keyPath, valueName);
        lock (_gate)
        {
            return LoadAll(strict: true).FirstOrDefault(b => b.Id == id);
        }
    }

    public List<RegistryValueBackup> GetAll()
    {
        lock (_gate)
        {
            return LoadAll();
        }
    }

    /// <summary>保存备份。同一 Id 已存在时保留最早的原值（那才是真正的原始值），不覆盖。</summary>
    public void Save(RegistryValueBackup entry) => SaveCore(entry, strict: false);

    public void SaveStrict(RegistryValueBackup entry) => SaveCore(entry, strict: true);

    private void SaveCore(RegistryValueBackup entry, bool strict)
    {
        // Id 始终由 Hive/KeyPath/ValueName 生成，保证一致
        entry.Id = MakeId(entry.Hive, entry.KeyPath, entry.ValueName);

        lock (_gate)
        {
            var all = LoadAll(strict);
            if (all.Any(b => b.Id == entry.Id))
            {
                return;
            }

            all.Add(entry);
            WriteAll(all);
        }
    }

    public bool Remove(string keyPath, string valueName) => Remove("HKLM", keyPath, valueName);

    public bool Remove(string hive, string keyPath, string valueName) => RemoveCore(hive, keyPath, valueName, strict: false);

    public bool RemoveStrict(string hive, string keyPath, string valueName) => RemoveCore(hive, keyPath, valueName, strict: true);

    private bool RemoveCore(string hive, string keyPath, string valueName, bool strict)
    {
        lock (_gate)
        {
            var all = LoadAll(strict);
            var removed = all.RemoveAll(b => b.Id == MakeId(hive, keyPath, valueName)) > 0;
            if (removed)
            {
                WriteAll(all);
            }

            return removed;
        }
    }

    private List<RegistryValueBackup> LoadAll(bool strict = false)
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                if (strict)
                {
                    try { _ = File.GetAttributes(_filePath); }
                    catch (FileNotFoundException) { return []; }
                    catch (DirectoryNotFoundException) { return []; }
                    throw new IOException($"备份路径不是可读取的文件：{_filePath}");
                }
                return [];
            }

            using var stream = File.OpenRead(_filePath);
            var entries = JsonSerializer.Deserialize<List<RegistryValueBackup>>(stream, JsonOptions);
            return entries ?? (strict
                ? throw new InvalidDataException($"备份文件内容为空：{_filePath}")
                : []);
        }
        catch (JsonException) when (!strict)
        {
            // 备份文件损坏时不阻塞正常流程；代价是丢失恢复能力，由调用方「无备份」提示兜底
            return [];
        }
        catch (IOException) when (!strict)
        {
            return [];
        }
    }

    private void WriteAll(List<RegistryValueBackup> entries)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 先写临时文件再替换，避免写一半崩溃导致备份损坏
        var tempPath = _filePath + ".tmp";
        using (var stream = File.Create(tempPath))
        {
            JsonSerializer.Serialize(stream, entries, JsonOptions);
        }

        File.Move(tempPath, _filePath, overwrite: true);
    }
}
