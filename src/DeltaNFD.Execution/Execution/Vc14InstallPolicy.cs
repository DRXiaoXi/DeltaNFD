using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>An installed Burn registration can make /install a successful no-op after its MSI children were removed.</summary>
internal static class Vc14InstallPolicy
{
    private static readonly IReadOnlyDictionary<string, string> Bundles = new Dictionary<string, string>
    {
        ["x64"] = "{804e7d66-ccc2-4c12-84ba-476da31d103d}",
        ["x86"] = "{e7802eac-3305-4da0-9378-e55d1ed05518}",
    };
    internal static string SelectAction(bool registered) => registered ? "/repair" : "/install";
    internal static string ReadAction(string arch)
    {
        if (!Bundles.TryGetValue(arch, out var id)) throw new ArgumentException("不支持的原包架构。");
        var registered = false;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + id);
            if (key is null) continue;
            var version = key.GetValue("BundleVersion") as string ?? "";
            var publisher = key.GetValue("Publisher") as string ?? "";
            var name = key.GetValue("DisplayName") as string ?? "";
            if (!Vc14Catalog.SameVersion(version, "14.42.34433.0") || publisher != "Microsoft Corporation"
                || name != "Microsoft Visual C++ 2015-2022 Redistributable (" + arch + ") - 14.42.34433")
                throw new IOException("14.42 包装登记身份不符，已停止：" + arch);
            var cache = key.GetValue("BundleCachePath") as string;
            if (File.Exists(cache) && !Vc14PackageTrust.Verify(cache, version, arch))
                throw new IOException("14.42 包装缓存签名或身份不符，已停止：" + arch);
            registered = true;
        }
        return SelectAction(registered);
    }
    internal static bool ArchitectureComplete(Vc14Scan scan, string arch) => Vc14Catalog.Families.Where(f => f.Arch == arch)
        .All(f => scan.Products.Count(p => p.Architecture == arch && p.Role == f.Role
            && Vc14Catalog.SameVersion(p.Version, "14.42.34433.0")) == 1)
        && scan.Products.Count(p => p.Architecture == arch) == 2 && scan.Issues.Count == 0;
}
