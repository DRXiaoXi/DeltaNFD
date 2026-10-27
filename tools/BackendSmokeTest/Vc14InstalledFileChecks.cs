using System.Security.Cryptography;
using DeltaNFD.Services;

/// <summary>Actual postcheck + actual reinstall orchestration; installation effects stay inside a new fake Windows root.</summary>
internal static class Vc14InstalledFileChecks
{
    internal static async Task RunAsync(string assetsRoot)
    {
        var id = Guid.NewGuid(); var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-all-dlls-" + id.ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        var count = 0;
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); count++; }
        foreach (var baseline in new[] { Vc14Baseline.V142, Vc14Baseline.V144 })
        {
            var info = Vc14BaselineInfo.For(baseline);
            var folder = Path.Combine(root, baseline.ToString()); var windows = Path.Combine(folder, "Windows");
            var media = Path.Combine(Path.GetFullPath(assetsRoot), info.FileRepairDirectory);
            var payload = Vc14ForceReplace.ExtractPayload(media, Path.Combine(folder, "payload"), default, baseline);
            string Target(string key)
            {
                var parts = key.Split('|');
                var path = Path.GetFullPath(Path.Combine(windows, parts[0] == "x64" ? "System32" : "SysWOW64", parts[1]));
                if (!path.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe fixture path");
                return path;
            }
            void RestoreHeld(string path)
            {
                if (File.Exists(path)) File.Move(path, path + ".unexpected-" + Guid.NewGuid().ToString("N"));
                File.Move(path + ".held", path);
            }
            foreach (var pair in payload)
            {
                var path = Target(pair.Key); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.Copy(pair.Value, path);
            }
            void Verify(string arch) => Vc14InstalledFileVerifier.Verify(baseline, arch, Path.Combine(windows, arch == "x64" ? "System32" : "SysWOW64"));
            var before = payload.Keys.ToDictionary(k => k, k => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Target(k)))));
            Verify("x64"); Verify("x86");
            Check(payload.Count == 51 && payload.Count(p => p.Key.StartsWith("x64|")) == 26 && payload.Count(p => p.Key.StartsWith("x86|")) == 25, "Both architectures have the full official file set.");
            Check(before.All(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Target(p.Key)))) == p.Value), "Read-only verification leaves all official file bytes unchanged.");
            foreach (var key in payload.Keys) File.Move(Target(key), Target(key) + ".held");
            try
            {
                var allMissing = new EnvironmentFixture(baseline, Verify)
                {
                    ForceFiles = arch =>
                    {
                        foreach (var file in payload.Where(p => p.Key.StartsWith(arch + "|", StringComparison.Ordinal))) File.Copy(file.Value, Target(file.Key));
                    }
                };
                var initial = Vc14Reinstall.Preview(allMissing);
                var result = await Vc14Reinstall.RunAsync(allMissing, initial.Fingerprint, null, default);
                Check(result.Success && allMissing.Forced == 2 && allMissing.Installs == 4 && allMissing.Pending is null,
                    "All 51 files missing still use at most one ordinary repair and one MSI file repair per architecture.");
            }
            finally { foreach (var key in payload.Keys) RestoreHeld(Target(key)); }
            foreach (var key in payload.Keys)
            {
                var arch = key.Split('|')[0]; var name = key.Split('|')[1]; var path = Target(key);
                File.Move(path, path + ".held");
                try
                {
                    try { Verify(arch); throw new Exception("Missing file was not detected: " + key); }
                    catch (Vc14DllMissing ex) { Check(ex.Architecture == arch && ex.File == name, "Every missing baseline member is identified precisely."); }
                    Verify(arch == "x64" ? "x86" : "x64");
                    var environment = new EnvironmentFixture(baseline, Verify);
                    environment.ForceFiles = requested => { if (requested != arch) throw new Exception("Unneeded architecture repair"); File.Copy(payload[key], path); };
                    var preview = Vc14Reinstall.Preview(environment);
                    var result = await Vc14Reinstall.RunAsync(environment, preview.Fingerprint, null, default);
                    Check(result.Success && environment.Installs == 3 && environment.Forced == 1 && environment.Pending is null,
                        "Reinstall detects every missing file, repairs only its architecture, and clears intent after full verification: " + key);
                    File.Move(path, path + ".repaired");
                }
                finally { RestoreHeld(path); }
            }
            var otherBaseline = baseline == Vc14Baseline.V142 ? Vc14Baseline.V144 : Vc14Baseline.V142;
            var other = Vc14BaselineInfo.For(otherBaseline);
            var otherPayload = Vc14ForceReplace.ExtractPayload(Path.Combine(Path.GetFullPath(assetsRoot), other.FileRepairDirectory),
                Path.Combine(folder, "other-baseline"), default, otherBaseline);
            foreach (var key in payload.Keys)
            {
                var path = Target(key); File.Move(path, path + ".held");
                try
                {
                    File.Copy(otherPayload[key], path);
                    try { Verify(key.Split('|')[0]); throw new Exception("Wrong baseline DLL was accepted: " + key); }
                    catch (Vc14DllVersionMismatch ex) { Check(ex.Architecture == key.Split('|')[0], "Every member is version checked against the selected baseline."); }
                    File.Move(path, path + ".other-version-fixture");
                }
                finally { RestoreHeld(path); }
            }

            var selected = "x64|msvcp140_atomic_wait.dll"; var selectedPath = Target(selected);
            File.Move(selectedPath, selectedPath + ".held");
            try
            {
                var unresolved = new EnvironmentFixture(baseline, Verify);
                var preview = Vc14Reinstall.Preview(unresolved);
                var failed = await Vc14Reinstall.RunAsync(unresolved, preview.Fingerprint, null, default);
                Check(!failed.Success && unresolved.Forced == 1 && unresolved.Pending is not null, "Exit zero with a still-missing DLL retains recovery evidence.");
                var ordinary = new EnvironmentFixture(baseline, Verify)
                { OrdinaryFiles = arch => { if (arch == "x64" && !File.Exists(selectedPath)) File.Copy(payload[selected], selectedPath); } };
                preview = Vc14Reinstall.Preview(ordinary);
                var fixedNormally = await Vc14Reinstall.RunAsync(ordinary, preview.Fingerprint, null, default);
                Check(fixedNormally.Success && ordinary.Forced == 0 && ordinary.Pending is null, "Normal installer repair can satisfy the full verifier without MSI fallback.");
                File.Move(selectedPath, selectedPath + ".normal-repaired");
                foreach (var outcome in new[] { new Vc14StepResult(3010), new Vc14StepResult(1603), new Vc14StepResult(0, true) })
                {
                    var pending = new EnvironmentFixture(baseline, Verify) { ForceResult = outcome };
                    preview = Vc14Reinstall.Preview(pending);
                    var unfinished = await Vc14Reinstall.RunAsync(pending, preview.Fingerprint, null, default);
                    Check(!unfinished.Success && pending.Forced == 1 && pending.Pending is not null, "Failed/reboot/unknown file repair never clears pending install.");
                }
            }
            finally { RestoreHeld(selectedPath); }

            // Every member must expose a readable version; an empty regular file is not a missing-file case.
            foreach (var key in payload.Keys)
            {
                var path = Target(key); File.Move(path, path + ".held");
                try
                {
                    File.WriteAllBytes(path, []);
                    try { Verify(key.Split('|')[0]); throw new Exception("Empty DLL was accepted: " + key); }
                    catch (Vc14DllMissing) { throw new Exception("Unreadable version was incorrectly classified as missing."); }
                    catch (IOException) { Check(true, "Each DLL must expose a readable version."); }
                    File.Move(path, path + ".empty-fixture");
                }
                finally { RestoreHeld(path); }
            }
            var locked = Target("x64|concrt140.dll");
            using (var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var unavailable = new EnvironmentFixture(baseline, Verify);
                var preview = Vc14Reinstall.Preview(unavailable);
                var result = await Vc14Reinstall.RunAsync(unavailable, preview.Fingerprint, null, default);
                Check(!result.Success && unavailable.Forced == 0 && unavailable.Pending is not null, "Unreadable locked file never grants missing-file MSI fallback.");
            }
            Verify("x64"); Verify("x86");
        }
        Console.WriteLine($"Installed DLL completeness: {count} checks passed; both baselines/all 51 files, actual verifier and reinstall flow, fake MSI/files only; no system DLL writes.");
    }

    private sealed class EnvironmentFixture : IVc14ReinstallEnvironment
    {
        public Vc14Baseline Baseline { get; }
        private readonly Action<string> _verify;
        private readonly List<Vc14Entry> _products = [];
        public Vc14Continuation? Pending;
        public int Installs, Forced;
        public Action<string>? ForceFiles, OrdinaryFiles;
        public Vc14StepResult ForceResult = new(0);
        public string DiagnosticsDirectory { get; } = "isolated-memory-diagnostics";
        public EnvironmentFixture(Vc14Baseline baseline, Action<string> verify)
        {
            Baseline = baseline; _verify = verify;
            foreach (var arch in new[] { "x64", "x86" }) Add(arch, "14.51.36247.0");
        }
        private void Add(string arch, string version)
        {
            foreach (var role in new[] { "Minimum", "Additional" })
                _products.Add(new(Guid.NewGuid().ToString("B"), $"Microsoft Visual C++ 2022 {arch} {role} Runtime", version, arch, role, Guid.NewGuid().ToString("B")));
        }
        public Vc14Scan Scan() => new(_products.ToArray(), [], []);
        public IReadOnlyList<Vc14RemovalPlanItem> Plan(Vc14Scan scan) => scan.Products.Select(p => new Vc14RemovalPlanItem(p.Code, p.Architecture, p.Name, p.Version, "msi", p.Code, "mock", "mock", p.Role, p.UpgradeCode)).ToArray();
        public string? Blocker() => null;
        public void VerifyBaselines() { }
        public void VerifyInstalled() { _verify("x64"); _verify("x86"); }
        public void VerifyInstalled(string arch) => _verify(arch);
        public void Trace(string stage, object value) { }
        public Vc14Continuation? PendingInstall() => Pending;
        public void SavePendingInstall(Vc14Continuation record) => Pending = record;
        public void CompletePendingInstall() => Pending = null;
        public OperationResult Cleanup(Vc14Scan scan, IProgress<string>? progress, CancellationToken token) => OperationResult.Ok("mock cleanup");
        public Task<Vc14StepResult> RemoveAsync(Vc14RemovalPlanItem item, IProgress<string>? progress, CancellationToken token)
        { _products.RemoveAll(p => p.Code == item.Key); return Task.FromResult(new Vc14StepResult(0)); }
        public Task<Vc14StepResult> InstallAsync(string arch, IProgress<string>? progress, CancellationToken token)
        { Installs++; _products.RemoveAll(p => p.Architecture == arch); Add(arch, Baseline.FullVersion()); OrdinaryFiles?.Invoke(arch); return Task.FromResult(new Vc14StepResult(0)); }
        public Task<Vc14StepResult> ForceFileRepairAsync(string arch, IProgress<string>? progress, CancellationToken token)
        { Forced++; ForceFiles?.Invoke(arch); return Task.FromResult(ForceResult); }
    }
}
