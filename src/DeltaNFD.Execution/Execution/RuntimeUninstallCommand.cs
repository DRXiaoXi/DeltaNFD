using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DeltaNFD.Services;

internal static class RuntimeUninstallCommand
{
    internal sealed record Plan(string Kind, string Executable, string[] Arguments, string Source)
    {
        public string[] ProductCodes { get; init; } = [];
        public ProcessStartInfo CreateStartInfo()
        {
            var info = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in Arguments) info.ArgumentList.Add(argument);
            return info;
        }
    }

    internal static string Classify(string key, string uninstall, string quiet)
    {
        var args = Split(uninstall);
        var name = args.Length == 0 ? "" : Path.GetFileName(args[0]);
        if (name.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("msiexec", StringComparison.OrdinalIgnoreCase)) return "Msi";
        if ((name.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("rundll32", StringComparison.OrdinalIgnoreCase))
            && args.Length > 1 && args[1].Equals("advpack.dll,LaunchINFSection", StringComparison.OrdinalIgnoreCase)) return "Inf";
        if (!string.IsNullOrWhiteSpace(quiet)) return "Bundle";
        // A GUID is not proof that an EXE bootstrapper is an MSI product.
        return args.Length == 0 && Guid.TryParse(key, out _) ? "Msi" : "Unknown";
    }

    internal static bool TryCreate(VcRedistEntry entry, string? logPath, out Plan? plan, out string error)
    {
        plan = null; error = "";
        var kind = Classify(entry.KeyName, entry.UninstallString, entry.QuietUninstallString);
        if (kind == "Msi")
        {
            var args = Split(entry.UninstallString);
            string? product = null;
            foreach (var arg in args.Skip(1))
            {
                var match = Regex.Match(arg, @"^(?:/[ix])?(\{[0-9a-f-]{36}\})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (match.Success && Guid.TryParse(match.Groups[1].Value, out var id))
                {
                    if (product is not null && product != id.ToString("B")) { error = "MSI 产品码不唯一。"; return false; }
                    product = id.ToString("B");
                }
            }
            if (product is null && Guid.TryParse(entry.KeyName, out var keyId)) product = keyId.ToString("B");
            if (product is null) { error = "无法确认 MSI 产品码，未执行卸载。"; return false; }
            if (Guid.TryParse(entry.KeyName, out var keyProduct) && !product.Equals(keyProduct.ToString("B"), StringComparison.OrdinalIgnoreCase))
            { error = "MSI 条目键名与命令产品码不一致，未执行。"; return false; }
            var arguments = new List<string> { "/x", product, "/qn", "/norestart" };
            if (logPath is not null) { arguments.Add("/L*V"); arguments.Add(logPath); }
            arguments.Add("REBOOT=ReallySuppress");
            plan = new("Msi", Path.Combine(Environment.SystemDirectory, "msiexec.exe"), arguments.ToArray(), "validated-msi-product")
                { ProductCodes = [product] };
            return true;
        }
        error = "包装条目必须先由统一卸载器解析并验证关联 MSI；不执行 INF 或 EXE 卸载入口。";
        return false;
    }

    internal static string GetInfPath(VcRedistEntry entry)
    {
        if (Classify(entry.KeyName, entry.UninstallString, entry.QuietUninstallString) == "Inf")
        {
            var args = Split(entry.UninstallString);
            if (args.Length != 3) throw new InvalidDataException("不支持的 INF 格式。");
            var fields = args[2].Split(',');
            if (fields.Length < 2 || fields.Length > 4 || fields.Skip(2).Any(f => !string.IsNullOrEmpty(f))
                || !(fields[1].Equals("Uninstall", StringComparison.OrdinalIgnoreCase) || fields[1].Equals("Unattend", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("无法确认 INF 卸载节。");
            var infName = Path.GetFileName(fields[0]);
            if (!Regex.IsMatch(infName, @"^vcred(?:08|09|10|11|12|14)_(?:x86|x64|arm64)\.inf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new InvalidDataException("不是支持的运行库 INF。");
            var infRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "inf");
            var infPath = Path.Combine(infRoot, infName);
            if (fields[0] != infName && !Path.GetFullPath(fields[0]).Equals(infPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("INF 路径不在系统 INF 目录。");
            return infPath;
        }
        throw new InvalidDataException("不是 INF 包装条目。");
    }

    internal static bool HasQuietInfSection(string path)
    {
        try { return ReadQuietInfProducts(path).Length > 0; }
        catch { return false; }
    }

    internal static string[] ReadQuietInfProducts(string path)
    {
        if (!File.Exists(path)) return [];
        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var current = "";
        foreach (var line in File.ReadLines(path))
        {
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith(';')) continue;
            if (text.StartsWith('[') && text.EndsWith(']'))
            {
                current = text[1..^1];
                sections.TryAdd(current, []);
                continue;
            }
            if (sections.TryGetValue(current, out var lines)) lines.Add(text);
        }
        var suffix = RuntimeInformation.OSArchitecture switch
        { Architecture.X64 => "ntamd64", Architecture.Arm64 => "ntarm64", _ => "ntx86" };
        if (!sections.TryGetValue("Unattend." + suffix, out var unattended)
            && !sections.TryGetValue("Unattend", out unattended)) return [];
        var preCommands = false;
        foreach (var directive in unattended)
        {
            var parts = directive.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2) return [];
            if (parts[0].Equals("RunPreSetupCommands", StringComparison.OrdinalIgnoreCase))
            {
                if (!parts[1].Equals("Msi.Unattend:1", StringComparison.OrdinalIgnoreCase)) return [];
                preCommands = true;
            }
            else if (!parts[0].Equals("DelReg", StringComparison.OrdinalIgnoreCase)
                && !parts[0].Equals("DelFiles", StringComparison.OrdinalIgnoreCase)) return [];
        }
        if (!preCommands || !sections.TryGetValue("Msi.Unattend", out var commands)
            || !sections.TryGetValue("Strings", out var strings) || commands.Count == 0) return [];
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in strings)
        {
            var parts = text.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && !values.TryAdd(parts[0], parts[1].Trim('"'))) return [];
        }
        var products = new List<string>();
        foreach (var command in commands)
        {
            var match = Regex.Match(command, @"^msiexec(?:\.exe)?\s+/X%(GuidCode|GuidMin|GuidAdd)%\s+/quiet\s+/norestart$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success || !values.TryGetValue(match.Groups[1].Value, out var value) || !Guid.TryParse(value, out var product)) return [];
            products.Add(product.ToString("B"));
        }
        return products.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static string[] Split(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return [];
        var pointer = CommandLineToArgvW(Environment.ExpandEnvironmentVariables(command), out var count);
        if (pointer == IntPtr.Zero) return [];
        try { return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size)) ?? "").ToArray(); }
        finally { LocalFree(pointer); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
}
