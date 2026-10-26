using System.Diagnostics;
using System.Security.Cryptography;
using DeltaNFD.Services;

/// <summary>Relocate a cold test process to the real app/Assets/Backends layout; no host DLL writes.</summary>
internal static class Vc14InstalledMediaChecks
{
    internal static async Task RunAsync(string sourceRoot)
    {
        var fixture = Path.Combine(Path.GetTempPath(), "DeltaNFD-media-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        try
        {
            var assetsRoot = Path.GetDirectoryName(Path.GetFullPath(sourceRoot).TrimEnd('\\', '/'))!;
            var sourceRoots = new Dictionary<Vc14Baseline, string>
            {
                [Vc14Baseline.V144] = Path.Combine(assetsRoot, Vc14FileRepairSource.DirectoryNameFor(Vc14Baseline.V144)),
            };
            DeploymentLayoutChecks.CheckVc14Media(assetsRoot);
            var extracted = Vc14ForceReplace.ExtractPayload(Path.GetFullPath(sourceRoot), Path.Combine(fixture, "unpacked"), CancellationToken.None);
            var windows = Path.Combine(fixture, "fake-windows");
            foreach (var pair in extracted)
            {
                var parts = pair.Key.Split('|');
                var target = Vc14ForceReplace.TargetPath(windows, parts[0], parts[1]);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(pair.Value, target);
            }
            var before = Directory.GetFiles(windows, "*", SearchOption.AllDirectories)
                .ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
            foreach (var scenario in new[] { "valid", "missing", "tampered" })
            {
                var app = Path.Combine(fixture, "安装目录 with spaces " + scenario);
                var assets = Path.Combine(app, "Assets");
                foreach (var baseline in new[] { Vc14Baseline.V144 })
                {
                    var sourceMedia = sourceRoots[baseline];
                    var media = Path.Combine(assets, Vc14FileRepairSource.DirectoryNameFor(baseline));
                    foreach (var file in Vc14FileRepairSource.PackagesFor(baseline).SelectMany(p => p.Files))
                    {
                        var path = Path.Combine(media, file.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        File.Copy(Path.Combine(sourceMedia, file.Path), path);
                    }
                }
                if (scenario == "valid")
                {
                    var old = Vc14ForceReplace.PreviewCore(assets, windows, false, null, CancellationToken.None);
                    Check(!old.CanRun && old.Blocker.Contains("载荷无法核验"), "Old Assets-root call reproduces screenshot failure");
                    Console.WriteLine("Reproduced old call: " + old.Blocker);
                }
                else
                {
                    // Valid files in the old/wrong location must never rescue missing/tampered fixed media.
                    // Relative paths are shared by both baselines, so one decoy set is enough.
                    foreach (var file in Vc14FileRepairSource.Packages.SelectMany(p => p.Files))
                    {
                        var wrong = Path.Combine(assets, file.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(wrong)!);
                        File.Copy(Path.Combine(sourceRoots[Vc14Baseline.V144], file.Path), wrong);
                    }
                    foreach (var baseline in new[] { Vc14Baseline.V144 })
                    {
                        var media = Path.Combine(assets, Vc14FileRepairSource.DirectoryNameFor(baseline));
                        var source = Vc14FileRepairSource.PackagesFor(baseline).SelectMany(p => p.Files)
                            .First(f => f.Path.EndsWith(scenario == "missing" ? ".msi" : ".cab", StringComparison.OrdinalIgnoreCase));
                        var changed = Path.Combine(media, source.Path);
                        if (scenario == "missing") File.Delete(changed);
                        else
                        {
                            using var file = new FileStream(changed, FileMode.Open, FileAccess.ReadWrite);
                            var first = file.ReadByte(); file.Position = 0; file.WriteByte((byte)(first ^ 0x40));
                        }
                    }
                }
                var backend = Path.Combine(app, "Backends", "runtime");
                Directory.CreateDirectory(backend);
                foreach (var file in Directory.GetFiles(AppContext.BaseDirectory))
                    File.Copy(file, Path.Combine(backend, Path.GetFileName(file)));
                var unrelatedCwd = Path.Combine(fixture, "unrelated-cwd");
                Directory.CreateDirectory(unrelatedCwd);
                var info = new ProcessStartInfo(Path.Combine(backend, "BackendSmokeTest.exe"))
                { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = unrelatedCwd,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
                foreach (var argument in new[] { "--vc14-installed-media-child", app, windows, scenario }) info.ArgumentList.Add(argument);
                using var process = Process.Start(info) ?? throw new Exception("Cannot start relocated fixture");
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch { try { process.Kill(true); } catch { } throw; }
                var output = await stdout;
                Check(process.ExitCode == 0, scenario + ": " + await stderr + output);
                Check(output.Contains("Installed-media child PASS", StringComparison.Ordinal), "Child finished all assertions: " + scenario);
                Console.WriteLine(output.Trim());
            }
            Check(before.All(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p.Key))) == p.Value), "Fake target DLLs unchanged by no-op/refused requests");
            Console.WriteLine($"Installed media layout checks passed: {count} parent + 18 cold-child + 2 deployment-media assertions; no real installation or DLL replacement.");
        }
        finally
        {
            var full = Path.GetFullPath(fixture);
            if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("DeltaNFD-media-layout-"))
                Directory.Delete(full, recursive: true);
        }
    }

    internal static async Task ChildAsync(string app, string windows, string scenario)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        var id = Guid.NewGuid();
        var data = Path.Combine(app, "isolated-data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(data, id);
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        Check(Path.GetFullPath(BackendEnvironment.AppRoot).TrimEnd('\\') == Path.GetFullPath(app).TrimEnd('\\'), "Cold process resolves app root above Backends/runtime independently of cwd");
        var expected42 = Path.Combine(app, "Assets", "Vc14FileRepair14.44");
        var expected44 = Path.Combine(app, "Assets", "Vc14FileRepair14.44");
        Check(Vc14FileRepairSource.InstalledRoot == expected42
            && Vc14FileRepairSource.InstalledRootFor(Vc14Baseline.V144) == expected44,
            "Production media roots include current 14.44 child directory");
        var preview = Vc14ForceReplace.PreviewCore(Vc14FileRepairSource.InstalledRoot, windows, false, null, CancellationToken.None);
        var preview44 = Vc14ForceReplace.PreviewCore(Vc14FileRepairSource.InstalledRootFor(Vc14Baseline.V144), windows,
            false, null, CancellationToken.None, Vc14Baseline.V144);
        Check(preview.CanRun == (scenario == "valid") && preview44.CanRun == (scenario == "valid"),
            "Installed media must pass only in the valid layout for the current baseline");
        Check(scenario == "valid"
                ? preview.SkipCount == 51 && preview44.SkipCount == 51 && preview44.DowngradeCount == 0
                : preview.Blocker.Contains("载荷无法核验") && preview44.Blocker.Contains("载荷无法核验"),
            "Complete payload or explicit missing/tampered rejection for the current baseline");
        var run = await Vc14ForceReplace.RunCoreAsync(preview.Fingerprint, null, CancellationToken.None,
            Vc14FileRepairSource.InstalledRoot, windows, false, null);
        Check(run.Success == (scenario == "valid"), "Execution uses the same installed source, healthy fake root no-op or refused");
        Check(!Directory.Exists(Path.Combine(data, "Vc14ForceReplace")), "No backup/replacement transaction started for no-op or invalid media");
        Console.WriteLine($"Installed-media child PASS: {scenario}, {checks} assertions, source={Vc14FileRepairSource.InstalledRoot}");
    }
}
