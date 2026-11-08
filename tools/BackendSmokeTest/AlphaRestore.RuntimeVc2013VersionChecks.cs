using DeltaNFD.Services;

using DeltaNFD.Services.AlphaRestore;
using RuntimeGuardService = DeltaNFD.Services.AlphaRestore.RuntimeGuardService;

namespace DeltaNFD.Tests.AlphaRestore;

internal static class RuntimeVc2013VersionChecks
{
    internal static async Task<int> RunAsync(string root)
    {
        int count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        void Refuse(VcRedistEntry entry, RuntimeMsiProduct product, string message)
        {
            try { RuntimeUnifiedUninstallBackend.ShouldRetainNewerMsi(entry, product); }
            catch (InvalidDataException) { Check(true, message); return; }
            throw new Exception(message);
        }
        // Independent fixture values from the signed Microsoft EXE manifests (download id 40784).
        foreach (var (arch, bundle, minimum, additional) in new[]
        {
            ("x64", "{050d4fc8-5d48-4b8f-8972-47c82c46020f}", "{A749D8E6-B613-3BE3-8F5F-045C84EBA29B}", "{929FBD26-9020-399B-9A7A-751D61F0B942}"),
            ("x86", "{f65db027-aff3-4070-886a-0d87064aabb1}", "{13A4EE12-23EA-3371-91EE-EFB36DDFFF3E}", "{F8CFEB22-A2E7-3971-9EDA-4B11EDEFC185}"),
        })
        {
            VcRedistEntry Entry(string? key = null, string version = "12.0.30501.0", string? architecture = null,
                string kind = "Bundle", string? name = null) => new()
            {
                DisplayName = name ?? $"Microsoft Visual C++ 2013 Redistributable ({arch}) - 12.0.30501",
                DisplayVersion = version, Architecture = architecture ?? arch, KeyName = key ?? bundle, UninstallKind = kind,
            };
            RuntimeMsiProduct Product(string code, string role) => new(code, 5,
                $"Microsoft Visual C++ 2013 {arch} {role} Runtime - 12.0.21005", "Microsoft Corporation", "12.0.21005", arch);
            var entry = Entry();
            var min = Product(minimum, "Minimum");
            var add = Product(additional, "Additional");
            foreach (var product in new[] { min, add })
            {
                Check(!RuntimeUnifiedUninstallBackend.ShouldRetainNewerMsi(entry, product), arch + " official MSI below bundle is accepted");
                Check(!RuntimeUnifiedUninstallBackend.ShouldRetainNewerMsi(Entry(version: "12.0.30501"), product with { Version = "12.0.21005.0" }), "three/four component versions normalize");
                foreach (var version in new[] { "12.0.21005.1", "12.0.30000", "12.0.30501", "12.0.40664" })
                    Check(RuntimeUnifiedUninstallBackend.ShouldRetainNewerMsi(entry, product with { Version = version }), "newer-than-child baseline retained: " + version);
                Refuse(entry, product with { Version = "12.0.21004" }, "older-than-official child refused");
                Refuse(entry, product with { Version = "14.0.21005" }, "different runtime family refused");
                Refuse(entry, product with { Architecture = arch == "x64" ? "x86" : "x64" }, "cross architecture refused");
                Refuse(entry, product with { Publisher = "Other" }, "untrusted publisher refused");
                Refuse(entry, product with { State = 2 }, "unknown MSI state refused");
                Refuse(entry, product with { Version = "invalid" }, "invalid MSI version refused");
            }
            Refuse(entry, min with { Code = "{11111111-1111-1111-1111-111111111111}" }, "unrecognized baseline product code refused");
            Refuse(entry, min with { Code = additional }, "minimum/additional role mismatch refused");
            Refuse(entry, min with { Name = $"Microsoft Visual C++ 2013 {arch} Redistributable" }, "baseline role cannot be guessed");
            Refuse(Entry(key: "{11111111-1111-1111-1111-111111111111}"), min, "other bundle does not inherit known child baseline");
            Refuse(Entry(version: "12.0.30502"), min, "other bundle version does not inherit known child baseline");
            Refuse(Entry(architecture: arch == "x64" ? "x86" : "x64"), min, "known bundle cannot change architecture");
            Refuse(Entry(kind: "Msi"), min, "known bundle cannot become MSI entry");
            Refuse(Entry(name: $"Microsoft Visual C++ 2013 Redistributable ({arch}) - 12.0.99999"), min, "bundle display identity mismatch refused");

            var fake = new VersionBackend(entry, min, add);
            var uninstaller = new RuntimeUnifiedUninstaller(fake, new RuntimeRepairDiagnostics(root), Path.Combine(root, "vc2013-" + arch));
            var prepared = uninstaller.PrepareBatch([entry], CancellationToken.None);
            Check(prepared.Count == 1 && fake.Calls == 0, "official bundle prepares before any execution");
            var result = await uninstaller.RunAsync(entry, "fixture-vc2013", TimeSpan.FromSeconds(5), null, CancellationToken.None);
            Check(result.Success && fake.Calls == 2 && fake.Cleanups == 1, "both official child MSIs complete in simulated uninstall flow");
            var newer = new VersionBackend(entry, min with { Version = "12.0.30000" }, add with { Version = "12.0.30000" });
            var retained = new RuntimeUnifiedUninstaller(newer, new RuntimeRepairDiagnostics(root), Path.Combine(root, "vc2013-newer-" + arch));
            var kept = await retained.RunAsync(entry, "fixture-retained", TimeSpan.FromSeconds(5), null, CancellationToken.None);
            Check(kept.Success && newer.Calls == 0 && newer.Cleanups == 1, "newer-than-child MSI below wrapper version is never uninstalled");
            var wrong = new VersionBackend(entry, min with { Code = "{11111111-1111-1111-1111-111111111111}" }, add);
            var blocked = new RuntimeUnifiedUninstaller(wrong, new RuntimeRepairDiagnostics(root), Path.Combine(root, "vc2013-blocked-" + arch));
            try { blocked.PrepareBatch([entry], CancellationToken.None); throw new Exception("unknown product prepared"); }
            catch (InvalidDataException) { Check(wrong.Calls == 0 && wrong.Cleanups == 0, "unknown child blocks before any operation"); }
        }
        Console.WriteLine($"VC2013 version checks: {count} passed; signed-manifest identities, no real uninstall or registry writes.");
        return count;
    }

    private sealed class VersionBackend(VcRedistEntry entry, params RuntimeMsiProduct[] items) : IRuntimeUnifiedUninstallBackend
    {
        private readonly Dictionary<string, RuntimeMsiProduct> _items = items.ToDictionary(p => p.Code);
        internal int Calls, Cleanups;
        public RuntimeMsiProduct ReadProduct(string code) => _items[code];
        public RuntimeUnifiedUninstallPlan Resolve(VcRedistEntry target)
        {
            var products = new List<string>();
            var retained = new List<string>();
            foreach (var product in _items.Values)
                (RuntimeUnifiedUninstallBackend.ShouldRetainNewerMsi(entry, product) ? retained : products).Add(product.Code);
            return new(entry, products.ToArray(), []) { RetainedProducts = retained.ToArray() };
        }
        public void ValidateRegistration(RuntimeUnifiedUninstallPlan plan) { }
        public Task<RuntimeStepResult> UninstallAsync(RuntimeUnifiedUninstallPlan plan, string product, string log,
            TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
        {
            Calls++;
            _items[product] = _items[product] with { State = -1 };
            return Task.FromResult(new RuntimeStepResult(0));
        }
        public void CleanupRegistration(RuntimeUnifiedUninstallPlan plan) { Cleanups++; }
    }
}
