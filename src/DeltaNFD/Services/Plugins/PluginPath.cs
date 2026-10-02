using System.Text;

namespace DeltaNFD.Services.Plugins;

/// <summary>
/// 包内相对路径校验（规范第 2 节）。统一分隔符后逐段校验；拒绝绝对路径、盘符、UNC、
/// 冒号/备用数据流、`.`/`..` 段、设备名、尾部空格/点、重名（Windows 大小写折叠）。
/// 符号链接/reparse point 的实盘检查由导入器在暂存阶段执行，这里只做词法校验。
/// </summary>
public static class PluginPath
{
    private static readonly string[] DeviceNames =
    ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
     "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    public const char Separator = '/';

    /// <summary>把反斜杠统一为正斜杠；出现 NUL 或换行直接判非法。</summary>
    public static string NormalizeSeparators(string path)
    {
        if (path.Contains('\0') || path.Contains('\n') || path.Contains('\r'))
            throw new PluginContractException($"路径包含非法字符：{path}");
        return path.Trim().Replace('\\', '/');
    }

    /// <summary>词法校验包内相对路径；通过时返回统一分隔符后的路径。</summary>
    public static string ValidateRelativePath(string raw, string what)
    {
        var path = NormalizeSeparators(raw);
        if (path.Length == 0) throw new PluginContractException($"{what}：路径为空。");
        if (path.StartsWith('/') || Path.IsPathRooted(raw) || path.Contains(':'))
            throw new PluginContractException($"{what}：不允许绝对路径、盘符或备用数据流：{raw}");
        if (path.StartsWith("//") || raw.StartsWith(@"\\"))
            throw new PluginContractException($"{what}：不允许 UNC 路径：{raw}");

        var segments = path.Split(Separator);
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
                throw new PluginContractException($"{what}：路径含空段：{raw}");
            if (segment is "." or "..")
                throw new PluginContractException($"{what}：路径不允许 . 或 .. 段：{raw}");
            if (segment.EndsWith(' ') || segment.EndsWith('.'))
                throw new PluginContractException($"{what}：段不允许以空格或点结尾：{segment}");
            foreach (var c in segment)
                if (c is '<' or '>' or '"' or '|' or '?' or '*' or '\0')
                    throw new PluginContractException($"{what}：段含 Windows 非法字符：{segment}");
            var name = segment.Split('.')[0];
            if (DeviceNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                DeviceNames.Contains(segment, StringComparer.OrdinalIgnoreCase))
                throw new PluginContractException($"{what}：段不允许使用设备名：{segment}");
        }
        var foldedPath = string.Join(Separator.ToString(), segments).ToLowerInvariant();
        if (!folded.Add(foldedPath))
            throw new PluginContractException($"{what}：重复路径：{raw}");
        return path;
    }

    public static bool IsSafeRelativePath(string path)
    {
        try { ValidateRelativePath(path, "路径"); return true; }
        catch (PluginContractException) { return false; }
    }

    /// <summary>解析后的路径必须位于暂存根内（防御 TOCTOU 之外的词法逃逸，如大小写变体）。</summary>
    public static bool IsWithinRoot(string root, string candidate)
    {
        var rootFull = Path.GetFullPath(root);
        var candidateFull = Path.GetFullPath(candidate);
        var comparison = StringComparison.OrdinalIgnoreCase;
        return candidateFull.StartsWith(rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull : rootFull + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>设备名与折叠重名检查也用于目录段（导入器对每个 ZIP 条目调用）。</summary>
    public static string ValidateZipEntryName(string entryName) => ValidateRelativePath(entryName, "ZIP 条目");
}
