using System.Text.Json;

namespace DeltaNFD.Services;

/// <summary>完整序列化、落盘后再发布，避免中断留下已命名的半份责任/诊断记录。</summary>
internal static class RuntimeOperationJournal
{
    internal static void Write(string path, object value, bool overwrite = false)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
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
