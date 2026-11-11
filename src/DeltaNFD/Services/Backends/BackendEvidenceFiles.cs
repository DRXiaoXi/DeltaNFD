using System.Text.Json;

namespace DeltaNFD.Services.Backends;

/// <summary>Publish evidence only after its complete contents have been flushed to disk.</summary>
internal static class BackendEvidenceFiles
{
    internal static void WriteJson(string path, object value, bool overwrite = false) =>
        WriteBytes(path, JsonSerializer.SerializeToUtf8Bytes(value), overwrite);

    internal static void WriteBytes(string path, byte[] bytes, bool overwrite = false)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0
            || File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("记录路径含重解析点，未写入。");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
