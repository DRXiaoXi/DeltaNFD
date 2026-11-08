using DeltaNFD.Services;

using DeltaNFD.Services.AlphaRestore;
using RuntimeGuardService = DeltaNFD.Services.AlphaRestore.RuntimeGuardService;

namespace DeltaNFD.Tests.AlphaRestore;

internal static class RuntimeForcePolicyChecks
{
    internal static int Run(string root)
    {
        int count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        void Refuse(Action action, string message)
        {
            try { action(); } catch (InvalidDataException) { Check(true, message); return; }
            throw new Exception(message);
        }
        var entry = new VcRedistEntry { DisplayName = "Microsoft Visual C++ 2013 Redistributable (x64) - 12.0.30501",
            DisplayVersion = "12.0.30501.0", Architecture = "x64", KeyName = "{050d4fc8-5d48-4b8f-8972-47c82c46020f}", UninstallKind = "Bundle" };
        var product = new RuntimeMsiProduct("{A749D8E6-B613-3BE3-8F5F-045C84EBA29B}", 5,
            "Microsoft Visual C++ 2013 x64 Minimum Runtime - 12.0.21005", "Microsoft Corporation", "12.0.21005", "x64");
        var unknown = product with { Code = "{11111111-1111-1111-1111-111111111111}" };
        var low = product with { Version = "12.0.21004" };
        Refuse(() => RuntimeUnifiedUninstallBackend.ShouldRetainNewerMsi(entry, unknown), "default refuses unknown baseline product");
        Refuse(() => RuntimeUnifiedUninstallBackend.ShouldRetainNewerMsi(entry, low), "default refuses unexpectedly low MSI");
        foreach (var p in new[] { unknown, low, unknown with { Version = "12.0.21004" } })
            Check(!RuntimeUnifiedUninstallBackend.ShouldRetainNewerMsi(entry, p, true), "explicit mode admits verified family unknown/lower metadata");
        Check(RuntimeUnifiedUninstallBackend.ShouldRetainNewerMsi(entry, product with { Version = "12.0.30000" }, true), "explicit mode still retains newer child MSI");
        foreach (var p in new[]
        {
            unknown with { Architecture = "x86" }, unknown with { Publisher = "Other" },
            unknown with { State = 2 }, unknown with { Code = "not-guid" }, unknown with { Code = Guid.Empty.ToString("B") },
            unknown with { Version = "invalid" }, unknown with { Version = "14.0.21005" },
            product with { Code = "{929FBD26-9020-399B-9A7A-751D61F0B942}" },
            unknown with { Name = "Microsoft Visual C++ 2013 SDK Minimum Runtime" },
            unknown with { Name = "Microsoft Visual C++ 2013 Tools Redistributable" },
            unknown with { Name = "Microsoft Visual C++ 2013 VSTO Redistributable" },
            unknown with { Name = "Microsoft Visual C++ 2013 Debug Redistributable" },
        }) Refuse(() => RuntimeUnifiedUninstallBackend.ShouldRetainNewerMsi(entry, p, true), "explicit mode still refuses ambiguous identity or excluded component");

        void Cached(RuntimeMsiProduct p, string? code = null, string? name = null, string? publisher = null,
            string? version = null, string? architecture = null) => RuntimeMsiMetadata.ValidateCachedIdentity(p,
                code ?? p.Code, name ?? p.Name, publisher ?? p.Publisher, version ?? p.Version, architecture ?? p.Architecture);
        Cached(unknown); Check(true, "matching cached identity permits code outside catalog");
        Cached(low); Check(true, "matching cached identity permits older verified version");
        Cached(product, version: "12.0.21005.0"); Check(true, "cached version normalization");
        Refuse(() => Cached(product, code: unknown.Code), "cache product code mismatch remains fatal");
        Refuse(() => Cached(product, name: "Other application"), "cache name mismatch remains fatal");
        Refuse(() => Cached(product, publisher: "Other"), "cache manufacturer mismatch remains fatal");
        Refuse(() => Cached(product, version: "12.0.21004"), "installed/cache version mismatch remains fatal");
        Refuse(() => Cached(product, architecture: "x86"), "cache architecture mismatch remains fatal");
        Refuse(() => Cached(product with { State = 2 }), "unknown installed state remains fatal");
        Refuse(() => Cached(product, version: "invalid"), "invalid cached version remains fatal");
        foreach (var mode in new[] { false, true })
        {
            var plan = new RuntimeUnifiedUninstallPlan(entry, [], []) { AllowNonstandard = !mode };
            try { new RuntimeUnifiedUninstallBackend(new RuntimeRepairDiagnostics(root), mode).ValidateRegistration(plan); }
            catch (IOException) { Check(true, "saved plan cannot cross mode boundary"); continue; }
            throw new Exception("mismatched plan accepted");
        }
        foreach (var arch in new[] { "x86", "x64" })
        foreach (var role in new[] { "Minimum", "Additional" })
        {
            var file = Path.Combine(Directory.GetCurrentDirectory(), "src/DeltaNFD/Assets/RuntimeCppOfficial/2013",
                arch, "packages", $"vcRuntime{role}_{(arch == "x64" ? "amd64" : "x86")}", $"vc_runtime{role}_{arch}.msi");
            var package = RuntimeMsiMetadata.ReadPackage(file);
            var installed = new RuntimeMsiProduct(package.Product, 5, package.Name, package.Publisher, package.Version, package.Architecture);
            Cached(installed); Check(true, "real Microsoft MSI cache identity fields read-only: " + arch + role);
        }
        Console.WriteLine($"Runtime force policy checks: {count} passed; no native uninstall or registry writes.");
        return count;
    }
}
