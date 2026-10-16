using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DeltaNFD.Services;

/// <summary>Beta0.9 命令执行方式，范围限定为 x86/x64 的 14.x release CRT。</summary>
internal static class Vc14BetaUninstall
{
    internal static bool InScope(string name, string version, string architecture)
    {
        if (architecture is not ("x64" or "x86")
            || !name.Contains("Visual C++", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Debug", StringComparison.OrdinalIgnoreCase)
            || name.Contains("SDK", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(name, @"\b(2005|2008|2010|2012|2013)\b")
            || name.Contains("ARM64", StringComparison.OrdinalIgnoreCase)
            || Vc14Catalog.IsAppxPackage(name)) return false;
        return Version.TryParse(version, out var parsed) ? parsed.Major == 14
            : name.Contains("Visual C++ v14", StringComparison.OrdinalIgnoreCase);
    }

    internal static string PreferredCommand(string quiet, string uninstall) =>
        !string.IsNullOrWhiteSpace(quiet) ? quiet.Trim() : uninstall.Trim();

    internal static bool RelevantForResidue(string name, string version)
    {
        if (!name.Contains("Visual C++", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Debug", StringComparison.OrdinalIgnoreCase) || name.Contains("SDK", StringComparison.OrdinalIgnoreCase)) return false;
        if (Version.TryParse(version, out var parsed)) return parsed.Major == 14;
        return name.Contains("Visual C++ v14", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(name, @"^Microsoft Visual C\+\+ 2026 Redistributable \((x64|x86)\)$", RegexOptions.IgnoreCase);
    }

    internal static bool IsMsiCommand(string command) => Path.GetFileName(Vc14Catalog.SplitUninstallCommand(command).Executable)
        .Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase);

    internal static bool IsUnsupportedScript(string command)
    {
        var executable = Vc14Catalog.SplitUninstallCommand(command).Executable;
        var name = Path.GetFileName(executable);
        return name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wscript.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cscript.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("mshta.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase)
            || !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || HasShellControl(command);
    }

    private static bool HasShellControl(string command)
    {
        var quoted = false;
        foreach (var c in command)
        {
            if (c == '"') quoted = !quoted;
            if (c is '\r' or '\n' or '%' or '!') return true;
            if (!quoted && c is '&' or '|' or '<' or '>' or '^' or '(' or ')') return true;
        }
        return quoted;
    }

    internal static string ResolveCommand(string command)
    {
        if (IsUnsupportedScript(command)) throw new IOException("登记命令包含脚本、未知 INF 或复合命令，未作为 14.x EXE 卸载执行。");
        var executable = Vc14Catalog.ResolveUninstallExecutable(command) ?? throw new IOException("登记卸载程序已不可用。");
        return "\"" + executable + "\" " + Vc14Catalog.SplitUninstallCommand(command).Arguments;
    }

    internal static Vc14Entry? MsiTarget(string command, Func<string, Vc14Entry?> readProduct)
    {
        if (!IsMsiCommand(command) || HasShellControl(command)) throw new IOException("MSI 卸载命令无法核验。");
        var args = Vc14Catalog.SplitUninstallCommand(command).Arguments;
        var match = Regex.Match(args, @"(?:^|\s)/(?:x|i)\s*(\{[0-9A-F-]{36}\})(?=\s|$)", RegexOptions.IgnoreCase);
        if (!match.Success || !Guid.TryParse(match.Groups[1].Value, out var code))
            throw new IOException("MSI 命令未提供明确产品码，未猜测登记键名。");
        var product = readProduct(code.ToString("B"));
        if (product is not null && !InScope(product.Name, product.Version, product.Architecture))
            throw new IOException("MSI 命令指向非 14.x 运行库，未执行。");
        return product;
    }

    internal static ProcessStartInfo Shell(string command, string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(command) || HasShellControl(command))
            throw new IOException("卸载命令为空或包含换行，未执行。");
        return new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            Arguments = "/d /s /c \"" + command + "\"",
            WorkingDirectory = workingDirectory ?? Environment.SystemDirectory,
            UseShellExecute = false, CreateNoWindow = true,
        };
    }

    internal static string CachedBundleCommand(string path, string log) =>
        "\"" + path + "\" /uninstall /quiet /norestart /log \"" + log + "\"";

    internal static string LegacyInfPath(string arch) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", "vcred14_" + arch + ".inf");

    internal static string LegacyInfCommand(string arch)
    {
        if (arch is not ("x64" or "x86")) throw new IOException("不支持的 INF 架构。");
        return "\"" + Path.Combine(Environment.SystemDirectory, "rundll32.exe")
            + "\" advpack.dll,LaunchINFSection vcred14_" + arch + ".inf,Uninstall";
    }

    // 固定模板中的 GuidMin/GuidAdd 是动态字段；即使形状哈希通过，也必须核验其实际 MSI 身份。
    internal static void ValidateInf(string text, string arch, Func<string, Vc14Entry?> readProduct)
    {
        if (!Vc14ResidueCleanup.MatchesLegacyInfText(text, arch))
            throw new IOException("旧 v14 INF 内容与固定模板不符，未执行其卸载节。");
        var appVersion = Field(text, "AppVer");
        if (!Version.TryParse(appVersion, out var version) || version.Major != 14)
            throw new IOException("旧 INF 版本不是 14.x，未执行。");
        var codes = new HashSet<Guid>();
        foreach (var (field, role) in new[] { ("GuidMin", "Minimum"), ("GuidAdd", "Additional") })
        {
            if (!Guid.TryParse(Field(text, field), out var code) || code == Guid.Empty || !codes.Add(code))
                throw new IOException("旧 INF 产品码无法核验，未执行。");
            var product = readProduct(code.ToString("B"));
            if (product is not null && (!InScope(product.Name, product.Version, product.Architecture)
                || !product.Architecture.Equals(arch, StringComparison.OrdinalIgnoreCase) || product.Role != role))
                throw new IOException("旧 INF 指向非目标 14.x 组件，未执行：" + code);
        }
    }

    private static string Field(string text, string name)
    {
        var matches = Regex.Matches(text, @"^\s*" + name + "\\s*=\\s*\"([^\"]+)\"\\s*(?:;[^\\r\\n]*)?$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (matches.Count != 1) throw new IOException("INF 字段缺失或重复：" + name);
        return matches[0].Groups[1].Value;
    }

    internal static Vc14StepResult MsiResult(Vc14StepResult step) =>
        step.ExitCode == 1605 && !step.Unknown
            ? step with { ExitCode = 0, Detail = "MSI 产品已不登记（1605），无需重复卸载；" + step.Detail }
            : step;
}
