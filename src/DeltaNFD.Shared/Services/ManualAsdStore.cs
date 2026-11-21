using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeltaNFD.Backends;

namespace DeltaNFD.Services;

/// <summary>Per-user durable session; it grants no file paths or cleanup permission to callers.</summary>
public sealed class ManualAsdStore(string path)
{
    public static void RejectLinks(string path)
    {
        for (var p = Path.GetFullPath(path); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
        {
            try
            {
                if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("ASD 路径包含重解析点。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public static ManualAsdStore Default => new(Path.Combine(AppDataPaths.Root, "Shader", "manual-asd.json"));
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string ContextId(ManualAsdContext context) => Hash(context.Owner + "|" + context.DriverKey + "|"
        + context.Selection.InventoryId + "|" + Path.GetFullPath(context.SystemRoot).ToUpperInvariant());
    public string FilePath { get; } = Path.GetFullPath(path);
    public ManualAsdSession? Read()
    {
        RejectLinks(FilePath);
        try
        {
            using var f = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (f.Length > 65536) throw new IOException("ASD 判定记录超过上限，请重新核对。");
            var bytes = new byte[(int)f.Length]; f.ReadExactly(bytes);
            using var doc = BackendJson.ParseStrict(bytes, "手动ASD记录");
            var value = JsonSerializer.Deserialize<ManualAsdSession>(doc.RootElement) ?? throw new IOException("ASD 判定记录为空。");
            if (value.Schema != 1 || !Guid.TryParseExact(value.Id, "N", out _) || value.Selection is null
                || value.Selection.Roots is null || value.Selection.Roots.Length is < 1 or > 64
                || !Enum.IsDefined(value.Stage) || value.Owner is not { Length: 64 } || value.DriverKey is not { Length: 64 }
                || value.StartedUtc == default || !Path.IsPathFullyQualified(value.SystemRoot)
                || (value.Stage is ManualAsdStage.Warmup or ManualAsdStage.Activated or ManualAsdStage.NotDetected && value.CleanedUtc is null))
                throw new IOException("ASD 判定记录格式无效，请重新开始判定。");
            if (value.Stage == ManualAsdStage.Activated && (value.MatchedBytes <= ManualAsdPolicy.ThresholdBytes
                || string.IsNullOrWhiteSpace(value.MatchedFile) || value.CheckedUtc is null))
                throw new IOException("ASD 激活记录缺少本次手动检测证据，请重新判定。");
            return value;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    public void Save(ManualAsdSession session)
    {
        RejectLinks(FilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        RejectLinks(FilePath);
        var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(f, session); f.Flush(true); }
            File.Move(temp, FilePath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        if (JsonSerializer.Serialize(Read()) != JsonSerializer.Serialize(session)) throw new IOException("ASD 判定记录写后核对失败。");
    }
    public static void InvalidateForCleanup() => Default.Invalidate();
    public void Invalidate()
    {
        var current = Read();
        if (current?.Stage is ManualAsdStage.Warmup or ManualAsdStage.Activated or ManualAsdStage.NotDetected)
            Save(current with { Stage = ManualAsdStage.Cancelled, Message = "缓存已再次清理，请重新开始手动 ASD 判定。" });
    }
}
