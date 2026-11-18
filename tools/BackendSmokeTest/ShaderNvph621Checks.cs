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
        Directory.CreateDirectory(root);
        var testId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(root,"isolated-test.marker"),testId.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root,testId);
        var psoRoot = Path.Combine(root, "PSOCache");
        var gameVer = Path.Combine(psoRoot, "GameVer_Test");
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
                var path = Path.Combine(cache, $"fixture{i}.nvph");
                if (sizes[i] >= 256 * mb) NvphFixtureFiles.Write(path, sizes[i]);
                else NvphFixtureFiles.Raw(path, sizes[i]);
            }
        }
        void FixtureB(params long[] sizes)
        {
            Fixture(sizes);
            var lastLarge = Array.FindLastIndex(sizes, s => s >= 256 * mb);
            if (lastLarge < 0) throw new Exception("B fixture needs a large container");
            NvphFixtureFiles.Write(Path.Combine(cache, $"fixture{lastLarge}.nvph"), sizes[lastLarge], 800, 5400);
        }
        ShaderDiagnosis Diagnose(string version, ShaderDiagLevel expected,
            ShaderGpuDetectionState gpu = ShaderGpuDetectionState.Nvidia)
        {
            var inspection = new ShaderInspection(null, () => new(gpu, version));
            var execution = new ShaderService(null, () => new DeltaNFD.Services.ShaderDriverReading(gpu, version));
            var result = inspection.DiagnoseAt(psoRoot);
            var backend = execution.DiagnoseAt(psoRoot);
            Check(result.Level == expected, $"{version}/{gpu}: expected {expected}, got {result.Level}: {result.Summary}");
            Check(result.Summary == backend.Summary && result.Level == backend.Level && result.CacheLayout == backend.CacheLayout
                && result.Details.SequenceEqual(backend.Details),
                $"{version}: homepage and backend diagnosis must agree");
            Check(!result.Summary.Contains("问题驱动") && !result.Advice.Contains("更换") && result.Level != ShaderDiagLevel.AbnormalDriverIssue,
                $"{version}: cache findings must not blame driver");
            return result;
        }
        try
        {
            var values = new long[] { 0, 1, 1023, mb, 100 * mb, 256 * mb - 1, 256 * mb, 256 * mb + 1, 512 * mb, 1024 * mb };
            foreach (var version in new[] { "621.00", "621.99", "622.00", "700.00" })
            foreach (var first in values)
            foreach (var second in values)
            {
                Fixture(first, second);
                var expected = (first >= 256 * mb && second > 0) || (second >= 256 * mb && first > 0);
                var diag = Diagnose(version, expected ? ShaderDiagLevel.Normal : ShaderDiagLevel.Abnormal);
                Check(diag.CacheLayout == ShaderCacheLayout.Unspecified && !diag.IsLayoutUncertain && diag.SystemCache is null,
                    "automatic diagnosis never infers ASD or associates system cache");
                Check(!diag.Summary.Contains("ASD已激活") && !diag.Summary.Contains("未激活ASD"), "capacity result is distinct from manual ASD");
            }
            foreach(var primary in new long[]{256*mb,256*mb+1,512*mb,1024*mb})
            foreach(var extra in new long[]{0,1,mb,256*mb,512*mb})
            {
                Fixture(primary,1,extra);Diagnose("621.14",ShaderDiagLevel.Normal);
            }
            foreach(var primary in new long[]{256*mb,256*mb+1,512*mb,1024*mb})
            {
                Fixture(primary);Diagnose("621.14",ShaderDiagLevel.Abnormal);
                NvphFixtureFiles.Raw(Path.Combine(cache,"unrelated.bin"),1);
                Diagnose("621.14",ShaderDiagLevel.Abnormal);
                Fixture(primary,0);Diagnose("621.14",ShaderDiagLevel.Abnormal);
            }
            Fixture(256*mb,1); var capacity=Diagnose("621.14",ShaderDiagLevel.Normal);
            FixtureB(256*mb,256*mb);Check(Diagnose("621.14",ShaderDiagLevel.Normal).CacheLayout==ShaderCacheLayout.Unspecified,"B-layer contents do not change the capacity rule");
            Fixture(new long[]{256*mb}.Concat(Enumerable.Repeat(1L,64)).ToArray());Diagnose("621.14",ShaderDiagLevel.Normal);
            foreach (var version in new[] { "576.88", "591.32", "592.71", "596.49", "610.03", "616.24", "617.14", "617.99", "620.99" })
            {
                Fixture(256 * mb, 256 * mb);
                Check(Diagnose(version, ShaderDiagLevel.Abnormal).CacheLayout == ShaderCacheLayout.Unspecified,
                    version + ": legacy drivers no longer accept an exact 256MB pair");
                Fixture(256 * mb, 256 * mb, 1);
                Diagnose(version, ShaderDiagLevel.Abnormal);
                Fixture(512 * mb, 512 * mb);
                Diagnose(version, ShaderDiagLevel.Normal);
                Fixture(256 * mb + 1, 256 * mb + 1);
                Diagnose(version, ShaderDiagLevel.Normal);
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
                Check(version.StartsWith("591.", StringComparison.Ordinal)
                    ? advice.State == ShaderDriverState.ProblemSeries && advice.Advice.Contains("问题驱动")
                    : advice.State == ShaderDriverState.Ok && !advice.Advice.Contains("问题驱动"), $"{version}: only 591 series is marked as a problem driver");
            }
            Fixture(256 * mb, mb);
            Diagnose("572.83", ShaderDiagLevel.NotApplicable);
            Diagnose("", ShaderDiagLevel.NotApplicable);
            Diagnose("invalid", ShaderDiagLevel.NotApplicable);
            Diagnose("621.01", ShaderDiagLevel.Info, ShaderGpuDetectionState.NotPresent);
            Diagnose("621.01", ShaderDiagLevel.NotApplicable, ShaderGpuDetectionState.Unknown);
            Directory.CreateDirectory(Path.Combine(psoRoot, "GameVer_Old"));
            Diagnose("621.01", ShaderDiagLevel.Abnormal);
            Diagnose("621.01", ShaderDiagLevel.Abnormal, ShaderGpuDetectionState.NotPresent);
            Directory.Delete(Path.Combine(psoRoot, "GameVer_Old"));

            Fixture(); Diagnose("621.01", ShaderDiagLevel.Info);
            Fixture(256 * mb, mb);
            Check(Diagnose("621.01", ShaderDiagLevel.Normal).Details.Any(d => d.Contains("SM5\\DXCache")), "621 uses SM5 fallback");
            cache = Path.Combine(gameVer, "SM6", "DXCache"); Directory.CreateDirectory(cache);
            Diagnose("621.01", ShaderDiagLevel.Info); Fixture(256 * mb, 1);
            Check(Diagnose("621.01", ShaderDiagLevel.Normal).Details.Any(d => d.Contains("SM6\\DXCache")), "621 prefers SM6");
            FixtureB(512 * mb, 512 * mb); Diagnose("621.01", ShaderDiagLevel.Normal);
            Fixture(256 * mb, 256 * mb);Check(Diagnose("621.01", ShaderDiagLevel.Normal).CacheLayout == ShaderCacheLayout.Unspecified,"pair is green without ASD claim");
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
