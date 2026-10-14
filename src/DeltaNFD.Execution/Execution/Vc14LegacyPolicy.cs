using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DeltaNFD.Services;

internal sealed record Vc14InfIdentity(string Name, string Version, string Minimum, string Additional);
internal static class Vc14LegacyPolicy
{
    // Shape of the historically deployed INF, excluding only its four data fields. No INF command is executed.
    private static readonly IReadOnlyDictionary<string, string> Shapes = new Dictionary<string, string>
    {
        ["x64"] = "A56649D9F4192128BB1172DF21E822CB686098948DF52908A144314A365E4356",
        ["x86"] = "51913917664A469C8D8AB970601A2CF63587A2D94E5355CC191B98898B5AA525",
    };
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static Vc14InfIdentity Validate(string text, string arch)
    {
        if (!Shapes.ContainsKey(arch) || text.Length > 65536) throw new InvalidDataException("旧 INF 架构或大小不符。");
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim())
            .Where(s => s.Length > 0 && !s.StartsWith(';')).Select(s => Regex.Replace(s, @"\s*=\s*", "=")).ToArray();
        var normalized = string.Join("\n", lines.Select(s => Regex.Replace(s, @"^((?:AppName|AppVer|GuidMin|GuidAdd)=).*$", "$1<value>", RegexOptions.IgnoreCase)));
        if (Hash(Encoding.UTF8.GetBytes(normalized)) != Shapes[arch]) throw new InvalidDataException("旧 INF 模板无法核验；保留原文件，未运行其命令。");
        string Field(string name)
        {
            var matches = lines.Where(l => l.StartsWith(name + "=", StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1 || !Regex.IsMatch(matches[0], "^" + name + "=\"[^\"]+\"$")) throw new InvalidDataException("旧 INF 字段无效：" + name);
            return matches[0][(name.Length + 2)..^1];
        }
        var identity = new Vc14InfIdentity(Field("AppName"), Field("AppVer"), Field("GuidMin"), Field("GuidAdd"));
        if (!Regex.IsMatch(identity.Name, @"^Microsoft Visual C\+\+ (?:2015-2019|2015-2022|2022|2026|v14) Redistributable \(" + arch + @"\)$", RegexOptions.IgnoreCase)
            || !Version.TryParse(identity.Version, out var version) || version.Major != 14
            || !Guid.TryParse(identity.Minimum, out var minimum) || minimum == Guid.Empty
            || !Guid.TryParse(identity.Additional, out var additional) || additional == Guid.Empty || minimum == additional)
            throw new InvalidDataException("旧 INF 名称、版本或产品码无效。");
        return identity;
    }
    internal static void ValidateRegistration(string name, string version, string publisher, string command, string arch, Vc14InfIdentity inf)
    {
        if (name != inf.Name || !Vc14Catalog.SameVersion(version, inf.Version) || publisher != "Microsoft Corporation"
            || !command.Equals("rundll32.exe advpack.dll,LaunchINFSection vcred14_" + arch + ".inf,Uninstall", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("旧包装登记与 INF 身份不一致，保留原值。");
    }
}
