using System.Text.Json;
using Microsoft.Win32;
namespace DeltaNFD.Execution.Procman;
internal sealed record ProcmanGpuPreference(bool Exists, string Kind, string Raw)
{
    private const string KeyPath = @"SOFTWARE\Microsoft\DirectX\UserGpuPreferences";
    internal string Encode() => "gpu2:" + JsonSerializer.Serialize(this);
    internal static ProcmanGpuPreference Decode(string raw) => raw.StartsWith("gpu2:", StringComparison.Ordinal)
        ? JsonSerializer.Deserialize<ProcmanGpuPreference>(raw[5..]) ?? throw new InvalidDataException("GPU 原值为空。")
        : new(raw != "(未设置)", "String", raw == "(未设置)" ? "" : raw);
    internal static ProcmanGpuPreference Read(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        if (key is null || !key.GetValueNames().Contains(path, StringComparer.OrdinalIgnoreCase)) return new(false, "String", "");
        var kind = key.GetValueKind(path);
        if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString)) throw new InvalidDataException("GPU 首选项不是字符串，未修改。");
        return new(true, kind.ToString(), (string)key.GetValue(path, "", RegistryValueOptions.DoNotExpandEnvironmentNames)!);
    }
    internal void Write(string path)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (!Exists) key.DeleteValue(path, false); else key.SetValue(path, Raw, Enum.Parse<RegistryValueKind>(Kind));
        if (Read(path) != this) throw new IOException("GPU 首选项写入后核验不一致。");
    }
    internal static string Replace(string raw, int value) => string.Concat(raw.Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Where(s => !s.TrimStart().StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase)).Select(s => s + ";")) + $"GpuPreference={value};";
}
