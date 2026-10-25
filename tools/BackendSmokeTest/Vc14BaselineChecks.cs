using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DeltaNFD.Services;

/// <summary>14.42/14.44 双基线：元数据、两套媒体、文件修复源、强制替换与用户选择传递。</summary>
internal static class Vc14BaselineChecks
{
    internal static async Task RunAsync(string assetsRoot)
    {
        int count = 0;
        void Check(bool value, string text) { if (!value) throw new IOException(text); count++; }
        void Reject(Action action, string text)
        {
            try { action(); } catch (IOException) { count++; return; }
            throw new IOException("Expected refusal: " + text);
        }

        var root = Path.GetFullPath(assetsRoot);
        var hostDll = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msvcp140.dll");
        var hostHashBefore = File.Exists(hostDll) ? Hash(hostDll) : "";
        var media42 = Path.Combine(root, "Vc14FileRepair14.42");
        var media44 = Path.Combine(root, "Vc14FileRepair14.44");
        var baseline42 = Path.Combine(root, "Vc14Baseline14.42");
        var baseline44 = Path.Combine(root, "Vc14Baseline14.44");
        Check(Directory.Exists(media42) && Directory.Exists(media44), "both deployed file-repair media directories exist");
        Check(File.Exists(Path.Combine(baseline42, "vc_redist.x64.exe"))
            && File.Exists(Path.Combine(baseline44, "vc_redist.x64.exe")), "both baseline installers are deployed");

        // 1) 元数据与展示。
        Check(Vc14BaselineOption.Default == Vc14Baseline.V142 && Vc14Baseline.V142.Display() == "14.42"
            && Vc14Baseline.V142.Label() == "14.42（默认）" && Vc14Baseline.V142.FullVersion() == "14.42.34433.0"
            && Vc14Baseline.V144.Display() == "14.44" && Vc14Baseline.V144.Label() == "14.44（兼容性更好）"
            && Vc14Baseline.V144.FullVersion() == "14.44.35211.0", "baseline display/version metadata");
        Check(Vc14Baseline.V142.IsSupported() && Vc14Baseline.V144.IsSupported() && !((Vc14Baseline)9).IsSupported(),
            "only 14.42/14.44 are supported");
        Check(Vc14BaselineInfo.All.Count == 2 && Vc14BaselineInfo.Default.Id == Vc14Baseline.V142
            && Vc14BaselineInfo.V144.Version == "14.44.35211.0"
            && Vc14BaselineInfo.V144.ShortVersion == "14.44.35211"
            && Vc14BaselineInfo.V144.BaselineDirectory == "Vc14Baseline14.44"
            && Vc14BaselineInfo.V144.FileRepairDirectory == "Vc14FileRepair14.44", "baseline info metadata");
        Check(Vc14BaselineInfo.FromVersion("14.42.34433.0")?.Id == Vc14Baseline.V142
            && Vc14BaselineInfo.FromVersion("14.44.35211.0")?.Id == Vc14Baseline.V144
            && Vc14BaselineInfo.FromVersion("14.42.34438.0") is null
            && Vc14BaselineInfo.FromVersion("14.51.36247.0") is null, "only pinned baseline versions map to a baseline");
        Check(Vc14BaselineInfo.FromBundle("x64", "{804e7d66-ccc2-4c12-84ba-476da31d103d}")?.Id == Vc14Baseline.V142
            && Vc14BaselineInfo.FromBundle("x86", "{0b5169e3-39da-4313-808e-1f9c0407f3bf}")?.Id == Vc14Baseline.V144
            && Vc14BaselineInfo.FromBundle("x64", "{0b5169e3-39da-4313-808e-1f9c0407f3bf}") is null,
            "bundle IDs are architecture-specific and map to the selected baseline");
        try { _ = Vc14BaselineInfo.For((Vc14Baseline)9); throw new IOException("unknown baseline must not resolve"); }
        catch (ArgumentOutOfRangeException) { count++; }
        Check(Vc14BaselineInfo.V144.MatchesVersion("14.44.35211.0") && !Vc14BaselineInfo.V144.MatchesVersion("14.42.34433.0"),
            "baseline version matching is exact to the selected line");
        Check(new FileInfo(Path.Combine(baseline44, "vc_redist.x64.exe")).Length == 25_635_768
            && new FileInfo(Path.Combine(baseline44, "vc_redist.x86.exe")).Length == 13_953_392,
            "14.44 official installers have pinned sizes");
        Check(Hash(Path.Combine(baseline44, "vc_redist.x64.exe")).Equals(Vc14BaselineInfo.V144.ExeHashes["x64"], StringComparison.OrdinalIgnoreCase)
            && Hash(Path.Combine(baseline44, "vc_redist.x86.exe")).Equals(Vc14BaselineInfo.V144.ExeHashes["x86"], StringComparison.OrdinalIgnoreCase),
            "14.44 official installers match pinned SHA256");

        // 2) 两套 MSI/CAB 都能独立核验，不能串用。
        Check(Vc14FileRepairSource.DirectoryName == "Vc14FileRepair14.42"
            && Vc14FileRepairSource.DirectoryNameFor(Vc14Baseline.V144) == "Vc14FileRepair14.44",
            "file-repair directory follows the selected baseline");
        Check(Vc14FileRepairSource.Packages.Count == 4 && Vc14FileRepairSource.PackagesFor(Vc14Baseline.V144).Count == 4
            && Vc14FileRepairSource.PackagesFor(Vc14Baseline.V144).All(p => Vc14Catalog.SameVersion(p.Version, "14.44.35211.0")),
            "both embedded MSI/CAB catalogs contain four 14.x packages");
        foreach (var arch in new[] { "x64", "x86" })
        {
            Check(Vc14FileRepairSource.Verify(media42, arch, Vc14Baseline.V142).Count == 2
                && Vc14FileRepairSource.Verify(media44, arch, Vc14Baseline.V144).Count == 2,
                "each baseline verifies its own official MSI/CAB bytes: " + arch);
        }
        Reject(() => Vc14FileRepairSource.Verify(media44, "x64", Vc14Baseline.V142), "14.44 media is not accepted as 14.42");
        Reject(() => Vc14FileRepairSource.Verify(media42, "x86", Vc14Baseline.V144), "14.42 media is not accepted as 14.44");

        // 2b) 依赖核验把两套固定包装码都视为基线，而不是未知第三方引用。
        var product144 = new Vc14Entry("{A0A0A0A0-0000-0000-0000-000000000144}",
            "Microsoft Visual C++ 2022 X64 Minimum Runtime - 14.51.36247", "14.51.36247", "x64", "Minimum",
            "{36F68A90-239C-34DF-B58C-64B30153CE35}");
        var provider144 = new Vc14DependencyProvider("LocalMachine:Registry64", "Microsoft.VS.VC_RuntimeMinimumVSU_amd64,v14",
            product144.Code, "fixture", [Vc14BaselineInfo.V144.BundleIds["x64"]]);
        var approval144 = Vc14DependencyPolicy.Evaluate(product144, [provider144], _ => [], _ => true);
        Check(approval144.Argument == "IGNOREDEPENDENCIES=" + Vc14BaselineInfo.V144.BundleIds["x64"]
            && approval144.Clients.SequenceEqual([Vc14BaselineInfo.V144.BundleIds["x64"]])
            && approval144.Evidence.Contains(Vc14BaselineInfo.V144.ExeHashes["x64"]),
            "14.44 pinned baseline reference is approved with its own source hash");
        Reject(() => Vc14DependencyPolicy.Evaluate(product144,
            [provider144 with { Clients = [Vc14BaselineInfo.V144.BundleIds["x86"]] }], _ => [], _ => true),
            "14.44 x86 baseline cannot satisfy an x64 product reference");
        Reject(() => Vc14DependencyPolicy.Evaluate(product144, [provider144], _ => [], _ => false),
            "14.44 baseline reference cannot bypass a failed pinned-source verification");

        // 3) 强制替换载荷：两套固定清单、解包版本和目标运行。
        var extracted42Root = Path.Combine(Path.GetTempPath(), "DeltaNFD-vc144-payload-42-" + Guid.NewGuid().ToString("N"));
        var extracted44Root = Path.Combine(Path.GetTempPath(), "DeltaNFD-vc144-payload-44-" + Guid.NewGuid().ToString("N"));
        var fixtures = new List<string>();
        var isolated = Path.Combine(Path.GetTempPath(), "DeltaNFD-vc144-isolated-" + Guid.NewGuid().ToString("N"));
        var testId = Guid.NewGuid();
        Directory.CreateDirectory(isolated);
        File.WriteAllText(Path.Combine(isolated, "isolated-test.marker"), testId.ToString("D"));
        try
        {
            AppDataPaths.ConfigureIsolatedDataRoot(isolated, testId);
            var set42 = Vc14ForceReplace.PayloadSet(Vc14Baseline.V142);
            var set44 = Vc14ForceReplace.PayloadSet(Vc14Baseline.V144);
            Check(set42.Files.Count == 51 && set42.Files.Count(p => p.Architecture == "x64") == 26
                && set42.Files.Count(p => p.Architecture == "x86") == 25, "14.42 force payload is 26 x64 + 25 x86");
            Check(set44.Files.Count == 51 && set44.Files.Count(p => p.Architecture == "x64") == 26
                && set44.Files.Count(p => p.Architecture == "x86") == 25, "14.44 force payload is 26 x64 + 25 x86");
            Check(set42.Fingerprint.Length == 64 && set44.Fingerprint.Length == 64
                && set42.Fingerprint != set44.Fingerprint && Vc14ForceReplace.PayloadFingerprint == set42.Fingerprint,
                "force payload fingerprints identify the baselines and keep the default 14.42");
            Check(set44.ExpectedVersion == "14.44.35211.0" && Vc14ForceReplace.ExpectedVersion == "14.42.34433.0",
                "force target version follows the selected baseline");

            var extracted42 = Vc14ForceReplace.ExtractPayload(media42, extracted42Root, CancellationToken.None, Vc14Baseline.V142);
            var extracted44 = Vc14ForceReplace.ExtractPayload(media44, extracted44Root, CancellationToken.None, Vc14Baseline.V144);
            Check(extracted42.Count == 51 && extracted44.Count == 51, "both baselines extract all 51 official DLLs");
            Check(extracted42.Values.All(p => ReadVersion(p) == "14.42.34433.0"), "14.42 payload files report 14.42.34433.0");
            Check(extracted44.Values.All(p => ReadVersion(p) == "14.44.35211.0"), "14.44 payload files report 14.44.35211.0");
            Check(extracted44.All(pair => Hash(pair.Value).Equals(set44.Files.Single(p => pair.Key == p.Architecture + "|" + p.File).Sha256,
                StringComparison.OrdinalIgnoreCase)), "14.44 payload hashes match the pinned catalog");
            Reject(() => Vc14ForceReplace.ExtractPayload(media42, extracted44Root + "-wrong", CancellationToken.None, Vc14Baseline.V144),
                "14.44 extraction cannot use 14.42 media");
            try
            {
                Vc14ForceReplace.ExtractPayload(media44, extracted44Root + "-wrong", CancellationToken.None, Vc14Baseline.V142);
                throw new IOException("must reject wrong media");
            }
            catch (IOException) { count++; }

            // 14.42 文件上用 14.44 基线：升级，不标降级。
            var fakeFrom42 = CreateFakeRoot(extracted42);
            fixtures.Add(fakeFrom42);
            var preview44 = Vc14ForceReplace.PreviewCore(media44, fakeFrom42, false, null, CancellationToken.None, Vc14Baseline.V144);
            Check(preview44.CanRun && preview44.Baseline == Vc14Baseline.V144 && preview44.PayloadVersion == "14.44.35211.0"
                && preview44.ReplaceCount == 51 && preview44.SkipCount == 0 && preview44.DowngradeCount == 0,
                "14.44 preview upgrades 14.42 files without a downgrade flag");
            var backupRoot = Path.Combine(AppDataPaths.Root, "Vc14ForceReplace");
            var dirsBefore = Directory.Exists(backupRoot) ? Directory.GetDirectories(backupRoot).Length : 0;
            var executed = await Vc14ForceReplace.RunCoreAsync(preview44.Fingerprint, null, CancellationToken.None,
                media44, fakeFrom42, false, null, null, Vc14Baseline.V144);
            Check(executed.Success && !executed.RequiresReboot, "14.44 force replacement succeeds on the fake root");
            Check(Directory.GetDirectories(backupRoot).Length == dirsBefore + 1, "14.44 execution writes one isolated backup");
            Check(extracted44.Keys.All(key =>
            {
                var parts = key.Split('|');
                return ReadVersion(Vc14ForceReplace.TargetPath(fakeFrom42, parts[0], parts[1])) == "14.44.35211.0";
            }), "all fake targets become 14.44.35211.0");
            using (var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(executed.DiagnosticsDirectory, "inventory.json"))))
            {
                Check(inventory.RootElement.GetProperty("baseline").GetInt32() == (int)Vc14Baseline.V144
                    && inventory.RootElement.GetProperty("expectedVersion").GetString() == "14.44.35211.0",
                    "inventory records the selected baseline and target version");
            }

            // 14.44 文件上用默认 14.42：预览应标记全部降级，证明选择真正改变目标。
            var fakeFrom44 = CreateFakeRoot(extracted44);
            fixtures.Add(fakeFrom44);
            var preview42 = Vc14ForceReplace.PreviewCore(media42, fakeFrom44, false, null, CancellationToken.None, Vc14Baseline.V142);
            Check(preview42.CanRun && preview42.Baseline == Vc14Baseline.V142 && preview42.PayloadVersion == "14.42.34433.0"
                && preview42.ReplaceCount == 51 && preview42.DowngradeCount == 51,
                "selecting 14.42 against 14.44 files is reported as a full downgrade");
            var noop44 = Vc14ForceReplace.PreviewCore(media44, fakeFrom44, false, null, CancellationToken.None, Vc14Baseline.V144);
            Check(noop44.CanRun && noop44.ReplaceCount == 0 && noop44.SkipCount == 51,
                "14.44 fake root is a no-op under the 14.44 baseline");

            // 4) 续装记录：旧 JSON 默认 14.42，显式 14.44 可往返；不同基线不得继续安装。
            var legacy = JsonSerializer.Deserialize<Vc14Continuation>(
                "{\"Id\":\"11111111-1111-1111-1111-111111111111\",\"CreatedUtc\":\"2026-10-25T00:00:00+08:00\"}");
            Check(legacy is not null && legacy.Baseline == Vc14Baseline.V142, "old pending record without Baseline defaults to 14.42");
            var roundTrip = JsonSerializer.Deserialize<Vc14Continuation>(JsonSerializer.Serialize(
                new Vc14Continuation(Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow) { Baseline = Vc14Baseline.V144 }));
            Check(roundTrip is not null && roundTrip.Baseline == Vc14Baseline.V144, "selected pending baseline survives serialization");
            var missing = new Vc14Scan([], [], []);
            var pendingMismatch = Vc14Reinstall.Preview(new FakeEnvironment(Vc14Baseline.V144, legacy), Vc14RepairMode.InstallOnly);
            Check(!pendingMismatch.CanRepair && pendingMismatch.Blocker.Contains("14.42（默认）")
                && pendingMismatch.Blocker.Contains("14.44（兼容性更好）") && pendingMismatch.Baseline == Vc14Baseline.V144,
                "install-only refuses a pending record from the other baseline");
            var pendingMatch = Vc14Reinstall.Preview(new FakeEnvironment(Vc14Baseline.V144,
                new Vc14Continuation(Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow) { Baseline = Vc14Baseline.V144 }),
                Vc14RepairMode.InstallOnly);
            Check(pendingMatch.CanRepair && pendingMatch.Baseline == Vc14Baseline.V144,
                "install-only can continue the same selected baseline");
            Check(missing.IsMissing, "empty fixture scan is treated as missing only for the fake environment");

            var scan144 = new Vc14Scan(new[]
            {
                new Vc14Entry(Guid.NewGuid().ToString("B"), "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.44.35211",
                    "14.44.35211.0", "x64", "Minimum", "{36F68A90-239C-34DF-B58C-64B30153CE35}"),
                new Vc14Entry(Guid.NewGuid().ToString("B"), "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.44.35211",
                    "14.44.35211.0", "x64", "Additional", "{9B0BAA88-E15F-3A1F-ACC0-B206E9DDF71C}"),
            }, [], []);
            var pendingVerified = new Vc14Continuation(Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow)
                { UninstallVerified = true, Baseline = Vc14Baseline.V144 };
            Check(!RuntimeHighFpsMaintenance.NeedsV14Uninstall(scan144, [], pendingVerified, Vc14Baseline.V144),
                "full repair can continue an already uninstalled 14.44 baseline");
            Check(RuntimeHighFpsMaintenance.NeedsV14Uninstall(scan144, [], pendingVerified, Vc14Baseline.V142),
                "full repair still uninstalls an installed 14.44 when 14.42 was selected");
            Check(RuntimeHighFpsMaintenance.Fingerprint("legacy", "v14", false, Vc14Baseline.V142)
                != RuntimeHighFpsMaintenance.Fingerprint("legacy", "v14", false, Vc14Baseline.V144),
                "full-repair fingerprint includes the selected baseline");
            Check(RuntimeHighFpsMaintenance.NeedsV14Uninstall(new Vc14Scan([], [], []), [], null, Vc14Baseline.V144) == false,
                "empty scan has no V14 uninstall work");
            Check(Vc14Reinstall.Fingerprint([], Vc14Baseline.V142) != Vc14Reinstall.Fingerprint([], Vc14Baseline.V144),
                "V14 removal-plan fingerprint includes the selected baseline");

            Check(!File.Exists(hostDll) || Hash(hostDll) == hostHashBefore,
                "host System32 msvcp140.dll bytes are unchanged by this read-only check");
            Console.WriteLine("VC14 14.42/14.44 baseline checks passed: " + count + " checks.");
        }
        finally
        {
            TryDelete(extracted42Root);
            TryDelete(extracted44Root);
            TryDelete(extracted44Root + "-wrong");
            foreach (var fixture in fixtures) TryDelete(fixture);
            TryDelete(isolated);
        }
    }

    private sealed class FakeEnvironment(Vc14Baseline baseline, Vc14Continuation? pending) : IVc14ReinstallEnvironment
    {
        public Vc14Baseline Baseline { get; } = baseline;
        public Vc14Scan Scan() => new([], [], []);
        public string? Blocker() => null;
        public void VerifyBaselines() { }
        public void VerifyInstalled() { }
        public string DiagnosticsDirectory { get; } = Path.Combine(Path.GetTempPath(), "DeltaNFD-vc144-fake-log-" + Guid.NewGuid().ToString("N"));
        public void Trace(string stage, object value) { }
        public Vc14Continuation? PendingInstall() => pending;
        public Task<Vc14StepResult> RemoveAsync(Vc14RemovalPlanItem item, IProgress<string>? progress, CancellationToken token)
            => throw new IOException("fixture must not uninstall");
        public Task<Vc14StepResult> InstallAsync(string arch, IProgress<string>? progress, CancellationToken token)
            => throw new IOException("fixture must not install");
    }

    private static string CreateFakeRoot(IReadOnlyDictionary<string, string> payload)
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-vc144-root-" + Guid.NewGuid().ToString("N"));
        foreach (var pair in payload)
        {
            var parts = pair.Key.Split('|');
            var target = Vc14ForceReplace.TargetPath(root, parts[0], parts[1]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(pair.Value, target);
        }
        return root;
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string? ReadVersion(string path)
    {
        try { return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion : null; }
        catch (Exception) { return null; }
    }

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception) { }
    }
}
