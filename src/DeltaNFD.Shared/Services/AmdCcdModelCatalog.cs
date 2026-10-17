using System.Text.RegularExpressions;

namespace DeltaNFD.Services;

/// <summary>型号的出厂设计信息；不提供或推算本机亲和性掩码。</summary>
internal sealed record AmdCcdModel(string Name, int PhysicalCores, bool L3IsCcd, int MaxL3Mb = 32)
{
    internal int CcdCount => 2;
}

internal static class AmdCcdModelCatalog
{
    // 来源与维护说明：notes/常见AMD双CCD型号与7900X拓扑识别修复-20261017.md。
    // 精确列举 SKU，不能按 Ryzen 9、HX 或核心数认定双 CCD；HS/U、AI HX 另有设计。
    private static readonly AmdCcdModel[] Models =
    [
        new("Ryzen 9 3900", 12, false),
        new("Ryzen 9 3900X", 12, false),
        new("Ryzen 9 3900XT", 12, false),
        new("Ryzen 9 3950X", 16, false),
        new("Ryzen 9 5900", 12, true),
        new("Ryzen 9 5900X", 12, true),
        new("Ryzen 9 5900XT", 16, true),
        new("Ryzen 9 5950X", 16, true),
        new("Ryzen 9 7900", 12, true),
        new("Ryzen 9 7900X", 12, true),
        new("Ryzen 9 7900X3D", 12, true, 96),
        new("Ryzen 9 7950X", 16, true),
        new("Ryzen 9 7950X3D", 16, true, 96),
        new("Ryzen 9 9900X", 12, true),
        new("Ryzen 9 9900X3D", 12, true, 96),
        new("Ryzen 9 9950X", 16, true),
        new("Ryzen 9 9950X3D", 16, true, 96),
        new("Ryzen 9 9950X3D2", 16, true, 96),
        new("Ryzen 7 7840HX", 12, true),
        new("Ryzen 9 7845HX", 12, true),
        new("Ryzen 9 7940HX", 16, true),
        new("Ryzen 9 7945HX", 16, true),
        new("Ryzen 9 7945HX3D", 16, true, 96),
        new("Ryzen 9 8940HX", 16, true),
        new("Ryzen 9 8945HX", 16, true),
        new("Ryzen 9 9850HX", 12, true),
        new("Ryzen 9 9955HX", 16, true),
        new("Ryzen 9 9955HX3D", 16, true, 96),
        new("Ryzen AI Max 390", 12, true),
        new("Ryzen AI Max+ 395", 16, true),
    ];

    private static readonly Regex Brand = new(
        @"^(?:AMD\s+)?(?<model>Ryzen\s+[579]\s+\d{4}(?:X3D2|HX3D|X3D|HX|XT|X)?|Ryzen\s+AI\s+Max\+?\s+\d{3})(?=\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static AmdCcdModel? Find(string? cpuName)
    {
        if (string.IsNullOrWhiteSpace(cpuName) || cpuName.Length > 256) return null;
        var normalized = Regex.Replace(cpuName.Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(R)", "", StringComparison.OrdinalIgnoreCase).Replace("™", "").Replace("®", ""), @"\s+", " ").Trim();
        var match = Brand.Match(normalized);
        return match.Success
            ? Models.FirstOrDefault(m => string.Equals(m.Name, match.Groups["model"].Value, StringComparison.OrdinalIgnoreCase))
            : null;
    }
}
