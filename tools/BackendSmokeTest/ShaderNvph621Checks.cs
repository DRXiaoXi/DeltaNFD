using DeltaNFD.Services;
using DeltaNFD.Inspection;

internal static class ShaderNvph621Checks
{
    public static async Task RunAsync()
    {
        const long mb = 1024L * 1024;
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var root = Path.GetFullPath(Path.Combine(tempRoot, "DeltaNFD_Nvph621_" + Guid.NewGuid().ToString("N")));
        if (!root.StartsWith(Path.TrimEndingDirectorySeparator(tempRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new Exception("Fixture path must stay inside temporary directory");
        var gameVer = Path.Combine(root, "GameVer_Test");
        var cache = Path.Combine(gameVer, "SM5", "DXCache");
        int count = 0;
        void Check(bool pass, string message)
        {
            if (!pass) throw new Exception(message);
            count++;
        }
        void Fixture(params long[] sizes)
        {
            Directory.CreateDirectory(cache);
            foreach (var file in Directory.GetFiles(cache)) File.Delete(file);
            for (var i = 0; i < sizes.Length; i++)
            {
                using var file = File.Create(Path.Combine(cache, $"fixture{i}.nvph"));
                file.SetLength(sizes[i]);
            }
        }
        ShaderDiagnosis Diagnose(string version, ShaderDiagLevel expected,
            ShaderGpuDetectionState gpu = ShaderGpuDetectionState.Nvidia)
        {
            var inspection = new ShaderInspection(null, () => new(gpu, version));
            var execution = new ShaderService(null, () => new DeltaNFD.Services.ShaderDriverReading(gpu, version));
            var result = inspection.DiagnoseAt(root);
            var backend = execution.DiagnoseAt(root);
            Check(result.Level == expected, $"{version}/{gpu}: expected {expected}, got {result.Level}: {result.Summary}");
            Check(result.Summary == backend.Summary && result.Level == backend.Level && result.Details.SequenceEqual(backend.Details),
                $"{version}: homepage and backend diagnosis must agree");
            Check(!result.Summary.Contains("问题驱动") && !result.Advice.Contains("更换") && result.Level != ShaderDiagLevel.AbnormalDriverIssue,
                $"{version}: cache findings must not blame driver");
            return result;
        }
        try
        {
            foreach (var version in new[] { "621.00", "621.01", "621.99", "622.00", "700.00" })
            {
                var legacyLevel = version.StartsWith("621.") ? ShaderDiagLevel.Warning : ShaderDiagLevel.Normal;
                Fixture(256 * mb, mb);
                Diagnose(version, ShaderDiagLevel.Normal);
                foreach (var other in new long[] { 1, 1023, mb, 255 * mb })
                {
                    Fixture(512 * mb, other);
                    Diagnose(version, ShaderDiagLevel.Normal);
                }
                Fixture(512 * mb, mb, 1);
                Diagnose(version, ShaderDiagLevel.Normal);
                foreach (var sizes in new long[][] { [512 * mb, 1024 * mb], [512 * mb, 512 * mb], [256 * mb + 1, 256 * mb + 1] })
                {
                    Fixture(sizes);
                    var legacy = Diagnose(version, legacyLevel);
                    Check(legacy.Summary.Contains("旧版布局"), version + ": legacy layout is identified without requiring an exact 256MB file");
                }
                Fixture(256 * mb, mb, 65536);
                Diagnose(version, ShaderDiagLevel.Normal);
                Fixture(512 * mb, 512 * mb, 1);
                Diagnose(version, legacyLevel);
                Fixture(512 * mb, 512 * mb, 0);
                Diagnose(version, ShaderDiagLevel.Abnormal);
                Fixture(512 * mb, 512 * mb, 512 * mb);
                Diagnose(version, legacyLevel);
                foreach (var (sizes, oldLayout) in new (long[] Sizes, bool OldLayout)[]
                {
                    ([256 * mb, mb, mb], false), ([256 * mb, mb, 256 * mb], true),
                    ([256 * mb + 1, 1024 * mb, 1, 65536], true),
                    ([1024 * mb, 1024 * mb, 1024 * mb, mb], true),
                    ([256 * mb, 1, 1023, 65536, mb, 255 * mb], false),
                })
                {
                    Fixture(sizes);
                    var withIntermediates = Diagnose(version, oldLayout ? legacyLevel : ShaderDiagLevel.Normal);
                    Check(withIntermediates.Summary.Contains($"共 {sizes.Length} 个非空 NVPH"),
                        version + ": accepted extra files are still reported in the count");
                }
                Fixture(new long[] { 512 * mb }.Concat(Enumerable.Repeat(1L, 64)).ToArray());
                Diagnose(version, ShaderDiagLevel.Normal);
                foreach (var sizes in new long[][]
                {
                    [mb, mb, mb, mb], [256 * mb - 1, mb, 1], [512 * mb + 1, mb, 65536],
                    [256 * mb, mb, 0], [256 * mb + 1, 1024 * mb, 0, 1], [0, 0, 0],
                })
                {
                    Fixture(sizes);
                    Diagnose(version, ShaderDiagLevel.Abnormal);
                }
            }
            foreach (var sizes in new long[][]
            {
                [256 * mb], [mb], [mb, mb], [256 * mb, 0],
                [256 * mb, mb, 0],
                [256 * mb - 1, mb], [256 * mb + 1, mb],
                [512 * mb - 1, mb], [512 * mb + 1, mb], [512 * mb, 0], [256000000, 1000000], [0], [65536],
            })
            {
                Fixture(sizes);
                Diagnose("621.01", ShaderDiagLevel.Abnormal);
            }
            foreach (var otherSize in new long[] { 1, 1023, 65536, mb - 1, mb + 1, 256 * mb, 512 * mb })
            {
                Fixture(256 * mb, otherSize);
                Diagnose("621.01", otherSize >= 256 * mb ? ShaderDiagLevel.Warning : ShaderDiagLevel.Normal);
            }
            Fixture(256 * mb, mb);
            File.WriteAllText(Path.Combine(cache, "ignored.parc"), "fixture");
            File.WriteAllText(Path.Combine(cache, "ignored.nvph.bak"), "fixture");
            var nested = Path.Combine(cache, "nested");
            Directory.CreateDirectory(nested);
            File.WriteAllBytes(Path.Combine(nested, "ignored.nvph"), []);
            Diagnose("621.01", ShaderDiagLevel.Normal);

            foreach (var version in new[] { "576.88", "591.32", "592.71", "596.49", "610.03", "616.24", "617.14", "617.99", "620.99" })
            {
                Fixture(256 * mb, 512 * mb);
                Diagnose(version, ShaderDiagLevel.Normal);
                Fixture(256 * mb, mb);
                Diagnose(version, ShaderDiagLevel.Abnormal);
                Fixture(256 * mb, 512 * mb, 1);
                Diagnose(version, ShaderDiagLevel.Normal);
                Fixture(256 * mb, 512 * mb, 512 * mb);
                Diagnose(version, ShaderDiagLevel.Abnormal);
                Fixture(256 * mb, 512 * mb, 0);
                Diagnose(version, ShaderDiagLevel.Abnormal);
                var advice = ShaderInspection.BuildDriverAdvice(ShaderGpuDetectionState.Nvidia, version);
                Check(advice.State == ShaderDriverState.Ok && !advice.Advice.Contains("问题驱动"), $"{version}: no series blacklist");
            }
            Fixture(256 * mb, mb);
            Diagnose("572.83", ShaderDiagLevel.NotApplicable);
            Diagnose("", ShaderDiagLevel.NotApplicable);
            Diagnose("invalid", ShaderDiagLevel.NotApplicable);
            Diagnose("621.01", ShaderDiagLevel.Info, ShaderGpuDetectionState.NotPresent);
            Diagnose("621.01", ShaderDiagLevel.NotApplicable, ShaderGpuDetectionState.Unknown);
            Directory.CreateDirectory(Path.Combine(root, "GameVer_Old"));
            Diagnose("621.01", ShaderDiagLevel.Abnormal);
            Diagnose("621.01", ShaderDiagLevel.Abnormal, ShaderGpuDetectionState.NotPresent);
            Directory.Delete(Path.Combine(root, "GameVer_Old"));

            Fixture();
            Diagnose("621.01", ShaderDiagLevel.Info);
            Fixture(256 * mb, mb);
            Check(Diagnose("621.01", ShaderDiagLevel.Normal).Details.Any(d => d.Contains("SM5\\DXCache")), "621 uses SM5 fallback");
            cache = Path.Combine(gameVer, "SM6", "DXCache");
            Directory.CreateDirectory(cache);
            Diagnose("621.01", ShaderDiagLevel.Info);
            Fixture(256 * mb, 1);
            Check(Diagnose("621.01", ShaderDiagLevel.Normal).Details.Any(d => d.Contains("SM6\\DXCache")), "621 prefers SM6 when both paths exist");
            Fixture(512 * mb, 512 * mb);
            Check(Diagnose("621.01", ShaderDiagLevel.Warning).Summary.Contains("旧版布局"), "legacy 621 layout also works on the preferred SM6 path");

            var status = await new ShaderInspection(null, () => new(ShaderGpuDetectionState.Nvidia, "617.14")).GetStatusAsync();
            Check(status.DriverState == ShaderDriverState.Ok && !status.DriverAdvice.Contains("问题驱动"), "public status no longer marks 617.14 problematic");
            Check(ShaderInspection.BuildDriverAdvice(ShaderGpuDetectionState.Nvidia, "572.83").State == ShaderDriverState.TooOld, "minimum driver check retained");
            Console.WriteLine($"Shader NVPH 621 checks: {count} assertions passed; fixture files only, no real cache cleanup or GPU writes.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
