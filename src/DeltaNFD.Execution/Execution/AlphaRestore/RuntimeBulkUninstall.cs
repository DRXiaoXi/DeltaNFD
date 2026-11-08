using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeltaNFD.Services.AlphaRestore;

internal static class RuntimeBulkUninstall
{
    internal static bool IsCandidate(VcRedistEntry entry) => Regex.IsMatch(entry.DisplayName,
        @"^Microsoft Visual C\+\+ (?:2005|2008|2010|2012|2013|2015|2017|2019|2022|2026|v14)\b", RegexOptions.IgnoreCase)
        && entry.Architecture is "x86" or "x64"
        && Regex.IsMatch(entry.DisplayName, @"\b(?:Redistributable|Minimum Runtime|Additional Runtime)\b", RegexOptions.IgnoreCase)
        && !Regex.IsMatch(entry.DisplayName, @"\b(?:Debug|SDK|VSTO|Tools)\b", RegexOptions.IgnoreCase);

    internal static RuntimeUninstallPreview Preview(IEnumerable<VcRedistEntry> entries, bool allowNonstandard = false)
    {
        var targets = entries.Where(IsCandidate).OrderBy(e => e.UninstallKind == "Bundle" ? 0 : e.UninstallKind == "Inf" ? 1 : 2)
            .ThenBy(e => e.KeyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Architecture, StringComparer.OrdinalIgnoreCase).ToList();
        if (targets.Count > 200 || targets.Select(e => e.KeyName.ToUpperInvariant()).Distinct().Count() != targets.Count)
            throw new InvalidDataException("运行库登记数量异常或重复，未创建卸载清单。");
        var content = allowNonstandard ? JsonSerializer.SerializeToUtf8Bytes(new { Entries = targets, AllowNonstandard = true })
            : JsonSerializer.SerializeToUtf8Bytes(targets);
        return new(targets, Convert.ToHexString(SHA256.HashData(content)), allowNonstandard);
    }

}
