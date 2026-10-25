using System.Security.Cryptography;

namespace DeltaNFD.Services;

/// <summary>
/// 工具内置的官方 2015–2022 14.x 基线元数据。载荷来源为微软官方 vc_redist 原包及其
/// MSI/CAB；各文件固定哈希并在预览/执行前复核。默认 14.42，用户可按需选择 14.44。
/// </summary>
internal sealed class Vc14BaselineInfo
{
    internal Vc14Baseline Id { get; }
    internal string Version { get; }
    internal string ShortVersion => Id.ShortVersion();
    internal string Label => Id.Display();
    internal string BaselineDirectory { get; }
    internal string FileRepairDirectory { get; }
    /// <summary>vc_redist.{arch}.exe 的固定 SHA256。</summary>
    internal IReadOnlyDictionary<string, string> ExeHashes { get; }
    /// <summary>微软 Burn 包装注册码（按架构），用于识别已登记的基线包装。</summary>
    internal IReadOnlyDictionary<string, string> BundleIds { get; }

    private Vc14BaselineInfo(Vc14Baseline id, string version, string baselineDirectory, string fileRepairDirectory,
        IReadOnlyDictionary<string, string> exeHashes, IReadOnlyDictionary<string, string> bundleIds)
    {
        Id = id; Version = version; BaselineDirectory = baselineDirectory; FileRepairDirectory = fileRepairDirectory;
        ExeHashes = exeHashes; BundleIds = bundleIds;
    }

    internal static Vc14BaselineInfo V142 { get; } = new(
        Vc14Baseline.V142, "14.42.34433.0", "Vc14Baseline14.42", "Vc14FileRepair14.42",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x64"] = "1821577409C35B2B9505AC833E246376CC68A8262972100444010B57226F0940",
            ["x86"] = "DD1A8BE03398367745A87A5E35BEBDAB00FDAD080CF42AF0C3F20802D08C25D4",
        },
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x64"] = "{804e7d66-ccc2-4c12-84ba-476da31d103d}",
            ["x86"] = "{e7802eac-3305-4da0-9378-e55d1ed05518}",
        });

    internal static Vc14BaselineInfo V144 { get; } = new(
        Vc14Baseline.V144, "14.44.35211.0", "Vc14Baseline14.44", "Vc14FileRepair14.44",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x64"] = "CC0FF0EB1DC3F5188AE6300FAEF32BF5BEEBA4BDD6E8E445A9184072096B713B",
            ["x86"] = "0C09F2611660441084CE0DF425C51C11E147E6447963C3690F97E0B25C55ED64",
        },
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x64"] = "{d8bbe9f9-7c5b-42c6-b715-9ee898a2e515}",
            ["x86"] = "{0b5169e3-39da-4313-808e-1f9c0407f3bf}",
        });

    internal static IReadOnlyList<Vc14BaselineInfo> All { get; } = [V142, V144];
    internal static Vc14BaselineInfo Default => V142;

    internal static Vc14BaselineInfo For(Vc14Baseline baseline) => baseline switch
    {
        Vc14Baseline.V142 => V142,
        Vc14Baseline.V144 => V144,
        _ => throw new ArgumentOutOfRangeException(nameof(baseline), "未知的 14.x 基线。"),
    };

    internal static Vc14BaselineInfo? FromVersion(string? version) =>
        version is null ? null : All.FirstOrDefault(info => Vc14Catalog.SameVersion(version, info.Version));

    internal static Vc14BaselineInfo? FromBundle(string arch, string bundleId)
    {
        if (!Guid.TryParse(bundleId, out var id)) return null;
        return All.FirstOrDefault(info => info.BundleIds.TryGetValue(arch, out var expected)
            && Guid.TryParse(expected, out var candidate) && candidate == id);
    }

    internal bool MatchesVersion(string? version) => version is not null && Vc14Catalog.SameVersion(version, Version);

    /// <summary>官方 vc_redist 原包路径（按当前部署目录解析）。</summary>
    internal string Exe(string arch) =>
        Path.Combine(BackendEnvironment.AssetsRoot, BaselineDirectory, "vc_redist." + arch + ".exe");

    internal string FileRepairRoot => Path.Combine(BackendEnvironment.AssetsRoot, FileRepairDirectory);

    internal string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal bool VerifyExe(string arch)
    {
        var path = Exe(arch);
        return ExeHashes.TryGetValue(arch, out var expected)
            && HashFile(path).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
