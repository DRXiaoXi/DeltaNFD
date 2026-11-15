using System.Diagnostics;
using System.Security.Cryptography;
using System.Xml.Linq;
using DeltaNFD.Inspection;
using DeltaNFD.Services;

internal static class Runtime1451Checks
{
    internal static async Task RunAsync(string assets)
    {
        var count = 0;
        void Check(bool value, string reason) { if (!value) throw new Exception(reason); count++; }
        void Reject(Action action, string reason) { try { action(); } catch (IOException) { count++; return; } throw new Exception(reason); }
        foreach (var version in new[] { "14.0.24212.0", "14.42.34433", "v14.43.99999.0", "14.40" })
            Check(RuntimeVersionInspection.BelowMinimum(version), "old version requires update: " + version);
        foreach (var version in new[] { "14.44", "14.44.0.0", "14.44.35211.0", "14.50.35719", "14.51.36247.0", "14.52.0", "", "unknown", "V14", "13.0" })
            Check(!RuntimeVersionInspection.BelowMinimum(version), "no old compatibility classification: " + version);
        var current = RuntimeVersionInspection.Read(_ => "14.51.36247.0", (_, _) => "14.51.36247.0");
        Check(!current.RequiresUpdate && current.Entries.Count == 6, "current versions stay off homepage");
        var old = RuntimeVersionInspection.Read(a => a == "x86" ? "14.42.34433" : "14.51.36247", (_, _) => null);
        Check(old.RequiresUpdate && old.OlderVersions.Contains("x86") && !old.OlderVersions.Contains("x64"), "one old architecture alerts alone");
        Check(RuntimeVersionInspection.Read(_ => "14.51.36247", (a, f) => a == "x64" && f == "msvcp140.dll" ? "14.42.34433" : null).RequiresUpdate,
            "old actual DLL is not hidden by newer registration");
        var missing = RuntimeVersionInspection.Read(_ => null, (_, _) => null);
        Check(!missing.RequiresUpdate && missing.Entries.Count == 0 && missing.Detail.Contains("未读取"), "missing is unknown, not normal and not a fabricated below-minimum alert");
        var errors = RuntimeVersionInspection.Read(_ => throw new IOException("fixture unreadable"), (_, _) => "bad-version");
        Check(!errors.RequiresUpdate && errors.Issues.Count == 6, "unreadable versions stay explicit in runtime page without inventing homepage warning");
        Check(RuntimeVersionInspection.Read(_ => "14.42.34433", (_, _) => throw new IOException("fixture denied")).RequiresUpdate,
            "unrelated read failure does not suppress confirmed old version");

        Check((int)Vc14Baseline.V142 == 0 && (int)Vc14Baseline.V144 == 1 && (int)Vc14Baseline.V151 == 2, "stable baseline wire values");
        Check(Vc14Baseline.V151.IsSupported() && Vc14Baseline.V151.Label().Contains("Intel") && Vc14Baseline.V144.Label().Contains("AMD"), "both requested labels available");
        var info = Vc14BaselineInfo.For(Vc14Baseline.V151);
        Check(info.Version == "14.51.36247.0" && info.BaselineDirectory == "Vc14Baseline14.51" && info.FileRepairDirectory == "Vc14FileRepair14.51", "151 metadata follows selected baseline");
        Check(Vc14BaselineInfo.FromVersion(info.Version)?.Id == Vc14Baseline.V151, "151 version recognized");
        foreach (var arch in new[] { "x64", "x86" })
        {
            var exe = Path.Combine(assets, info.BaselineDirectory, "vc_redist." + arch + ".exe");
            using var stream = File.OpenRead(exe);
            Check(Convert.ToHexString(SHA256.HashData(stream)) == info.ExeHashes[arch], "actual official EXE pinned hash: " + arch);
            Check(FileVersionInfo.GetVersionInfo(exe).FileVersion == info.Version && Vc14PackageTrust.Verify(exe, info.Version, arch), "actual Microsoft signature and version: " + arch);
            Check(!Vc14PackageTrust.Verify(exe, info.Version, arch == "x64" ? "x86" : "x64"), "wrong architecture rejected: " + arch);
            Check(Vc14BaselineInfo.FromBundle(arch, info.BundleIds[arch])?.Id == Vc14Baseline.V151, "actual Burn bundle identity: " + arch);
            Check(Vc14BundleIdentity.Read(exe, info.BundleIds[arch], arch).Verified, "actual signed Burn PE identity: " + arch);
            var packages = Vc14FileRepairSource.Verify(Path.Combine(assets, info.FileRepairDirectory), arch, Vc14Baseline.V151);
            Check(packages.Count == 2, "real MSI metadata and CAB hashes: " + arch);
            foreach (var package in packages)
            {
                var product = new Vc14Entry(package.Code, package.Name, package.Version, arch, package.Role, package.UpgradeCode);
                var provider = new Vc14DependencyProvider("LocalMachine:Registry64", "Microsoft.VS.VC_Runtime" + package.Role + "VSU_" + (arch == "x64" ? "amd64" : "x86") + ",v14", package.Code, "fixture", [info.BundleIds[arch]]);
                var approval = Vc14DependencyPolicy.Evaluate(product, [provider], _ => [], (baseline, requestedArch) => baseline.Id == Vc14Baseline.V151 && requestedArch == arch);
                Check(approval.Argument == "IGNOREDEPENDENCIES=" + info.BundleIds[arch] && approval.Evidence.Contains(info.ExeHashes[arch]),
                    "151 own Burn reference recognized with pinned evidence: " + arch + " " + package.Role);
                Reject(() => Vc14DependencyPolicy.Evaluate(product, [provider], _ => [], (_, _) => false),
                    "151 dependency approval requires verified original: " + arch + " " + package.Role);
            }
            Reject(() => Vc14FileRepairSource.Verify(Path.Combine(assets, info.FileRepairDirectory), arch, Vc14Baseline.V144), "151 media cannot be used as 144: " + arch);
        }
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-runtime1451-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var extracted = Vc14ForceReplace.ExtractPayload(Path.Combine(assets, info.FileRepairDirectory), Path.Combine(root, "extract"), default, Vc14Baseline.V151);
        Check(extracted.Count == 51 && Vc14ForceReplace.PayloadSet(Vc14Baseline.V151).Files.Count == 51, "real 151 media extracts exactly 51 pinned DLLs");
        foreach (var dll in Vc14ForceReplace.PayloadSet(Vc14Baseline.V151).Files)
        {
            var directory = Path.Combine(root, "system-fixture", dll.Architecture); Directory.CreateDirectory(directory);
            var path = extracted[dll.Architecture + "|" + dll.File];
            File.Copy(path, Path.Combine(directory, dll.File));
            var version = FileVersionInfo.GetVersionInfo(path);
            Check(version.FileMajorPart == 14 && version.FileMinorPart == 51 && version.FileBuildPart == 36247, "actual DLL version: " + dll.Architecture + " " + dll.File);
        }
        foreach (var arch in new[] { "x64", "x86" })
        {
            Vc14InstalledFileVerifier.Verify(Vc14Baseline.V151, arch, Path.Combine(root, "system-fixture", arch)); count++;
            Reject(() => Vc14InstalledFileVerifier.Verify(Vc14Baseline.V144, arch, Path.Combine(root, "system-fixture", arch)), "151 DLLs cannot pass 144 postcheck: " + arch);
        }
        Vc14StagedChecks.Fake Fresh(string name) => new(Path.Combine(root, name), Vc14Baseline.V151);
        Task<OperationResult> Run(Vc14StagedChecks.Fake env, Vc14RepairMode mode) => Vc14Reinstall.RunAsync(env,
            Vc14Reinstall.Preview(env, mode).Fingerprint, null, default, mode);
        var full = Fresh("full");
        var preview = Vc14Reinstall.Preview(full);
        Check(preview.CanRepair && preview.Baseline == Vc14Baseline.V151, "151 full preview admitted");
        var result = await Run(full, Vc14RepairMode.Full);
        Check(result.Success && full.PendingInstall() is null && full.Products.Count == 4 && full.Products.All(p => p.Version == info.Version), "real pipeline with fake installers completes all four 151 components: " + result.Message);
        Check(full.SavedBeforeRemoval && full.SavedBeforeInstall, "durable continuation precedes uninstall and install");
        var partial = Fresh("partial"); partial.InstallFail = "x86";
        Check(!(await Run(partial, Vc14RepairMode.Full)).Success && partial.PendingInstall()?.Baseline == Vc14Baseline.V151 && partial.PendingInstall()?.UninstallVerified == true,
            "151 x86 failure retains verified-uninstall continuation and chosen baseline");
        partial.InstallFail = "";
        Check((await Run(partial, Vc14RepairMode.InstallOnly)).Success && partial.PendingInstall() is null, "151 interrupted installation resumes through actual pipeline");
        var wrong = Fresh("wrong"); wrong.SavePendingInstall(new(Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow) { Baseline = Vc14Baseline.V144, UninstallVerified = true });
        Check(!Vc14Reinstall.Preview(wrong, Vc14RepairMode.InstallOnly).CanRepair && wrong.Calls.Count == 0, "144 continuation cannot silently install 151");
        var denied = Fresh("denied"); denied.SaveError = true;
        Check(!(await Run(denied, Vc14RepairMode.Full)).Success && denied.Calls.Count == 0, "failed durable write blocks all 151 mutation");
        var stale = Fresh("stale");
        var staleFingerprint = Vc14Reinstall.Preview(new Vc14StagedChecks.Fake(stale.Root, Vc14Baseline.V144)).Fingerprint;
        Check(!(await Vc14Reinstall.RunAsync(stale, staleFingerprint, null, default)).Success && stale.Calls.Count == 0, "144 approval cannot authorize 151 install");
        var safe = Fresh("safe"); safe.IsSafeMode = true;
        Check(!(await Run(safe, Vc14RepairMode.Full)).Success && !safe.Calls.Any(c => c.StartsWith("install:")), "151 is not installed in safe mode");
        var badDll = Fresh("bad-dll"); badDll.WrongDll = badDll.ForceKeepsMismatch = true;
        Check(!(await Run(badDll, Vc14RepairMode.Full)).Success && badDll.PendingInstall()?.Baseline == Vc14Baseline.V151, "failed actual-DLL postcheck never reports 151 success");

        var source = new DirectoryInfo(Environment.CurrentDirectory);
        while (source is not null && !File.Exists(Path.Combine(source.FullName, "DeltaNFD.sln"))) source = source.Parent;
        if (source is null) throw new Exception("source unavailable");
        var dashboard = File.ReadAllText(Path.Combine(source.FullName, "src/DeltaNFD/ViewModels/DashboardViewModel.cs"));
        var loader = dashboard[dashboard.IndexOf("private async Task LoadRuntimeHealthAsync")..].Split("[RelayCommand]")[0];
        Check(loader.Contains("RuntimeVersionInspection.ReadAsync") && !loader.Contains("ServiceLocator.Guard") && !loader.Contains("Scan14Async"), "homepage does not run old MSI/guard compatibility scan");
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var page = XDocument.Load(Path.Combine(source.FullName, "src/DeltaNFD/Views/RuntimePage.xaml"));
        Check(!page.Descendants(ns + "ToggleSwitch").Any(e => ((string?)e.Attribute("Header")) == "运行库检测") && !page.ToString().Contains("待长期评估"), "old automatic scan and assessment UI removed");
        Check(page.Descendants(ns + "ComboBox").Any(e => ((string?)e.Attribute("SelectedIndex"))?.Contains("BaselineIndex") == true), "baseline selection exposed");
        Console.WriteLine($"Runtime 14.51 and minimum-version checks: {count} passed. Real signed media/MSI/CAB/DLL reads and fake install pipeline; no host install, runtime or system writes. Fixture: {root}");
    }
}
