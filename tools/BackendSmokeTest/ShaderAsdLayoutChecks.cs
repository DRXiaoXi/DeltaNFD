using System.Text.Json;
using System.Xml.Linq;
using DeltaNFD.Services;
using DeltaNFD.Inspection;
using DeltaNFD.ViewModels;

internal static class ShaderAsdLayoutChecks
{
    internal static void Run()
    {
        const long mb = 1024L * 1024;
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-asd-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), "owned ASD fixture");
        var cache = Path.Combine(root, "GameVer_Test", "SM6", "DXCache");
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
        void Fixture(params long[] sizes)
        {
            Directory.CreateDirectory(cache);
            foreach (var p in Directory.GetFiles(cache)) File.Delete(p);
            for (var i = 0; i < sizes.Length; i++) { using var f = File.Create(Path.Combine(cache, i + ".nvph")); f.SetLength(sizes[i]); }
        }
        ShaderDiagnosis Diagnose(string version = "621.14") => new ShaderInspection(null,
            () => new(ShaderGpuDetectionState.Nvidia, version)).DiagnoseAt(root);
        ShaderStatus Status() => new() { IsNvidia = true, GpuDetectionState = ShaderGpuDetectionState.Nvidia,
            DriverVersion = "621.14", DriverState = ShaderDriverState.Ok, DriverAdvice = "fixture", IsGameRunning = false };
        NvShaderCacheReport Good() => new() { Scopes = [new("全局设置", "default", null, "fixture"), new("三角洲程序设置", "inherited", null, "fixture")] };
        foreach (var version in new[] { "621.00", "621.14", "621.99" })
        {
            foreach (var sizes in new long[][] { [256 * mb, 512 * mb], [512 * mb, 1024 * mb],
                [256 * mb + 1, 256 * mb + 1], [256 * mb, 256 * mb + 1], [512 * mb, 512 * mb, 1], [1024 * mb, 1024 * mb, mb] })
            {
                Fixture(sizes); var diag = Diagnose(version);
                Check(diag.Level == ShaderDiagLevel.Warning && diag.CacheLayout == ShaderCacheLayout.Legacy && diag.Summary.StartsWith("未激活ASD"),
                    "621 legacy layout is a warning, not damaged: " + version);
                var health = DashboardShaderHealth.Evaluate(Status(), diag, Good(), true);
                Check(health.State == DashboardHealthState.Warning && !health.ShowAction && !health.OpenCacheRecovery && health.Text.Contains("未激活ASD"),
                    "legacy warning stays yellow without go-handle action: " + version);
            }
            foreach (var sizes in new long[][] { [256 * mb, 1], [512 * mb, mb], [256 * mb, 1, 65536, mb], [512 * mb, 255 * mb, 1],
                [256 * mb, 256 * mb], [256 * mb, mb, 256 * mb], [256 * mb, 256 * mb, 256 * mb],
                [256 * mb, 512 * mb, 256 * mb], [1024 * mb, 256 * mb, 1, 256 * mb] })
            {
                Fixture(sizes); var diag = Diagnose(version);
                Check(diag.Level == ShaderDiagLevel.Normal && diag.CacheLayout == ShaderCacheLayout.Asd && diag.Summary.StartsWith("ASD已激活"),
                    "621 modern layout supplies active ASD hint: " + version);
                var health = DashboardShaderHealth.Evaluate(Status(), diag, Good(), true);
                Check(health.State == DashboardHealthState.Normal && health.Text.Contains("ASD已激活"), "homepage does not replace active ASD text with generic normal");
            }
        }
        foreach (var version in new[] { "576.88", "596.49", "620.99" })
        foreach (var sizes in new long[][] { [256 * mb, 256 * mb], [256 * mb, mb, 256 * mb] })
        {
            Fixture(sizes); var diag = Diagnose(version);
            Check(diag.Level == ShaderDiagLevel.Abnormal && diag.CacheLayout == ShaderCacheLayout.Unspecified && !diag.Summary.Contains("ASD"),
                "older drivers do not accept a pair of exact 256MB files: " + version);
            var health = DashboardShaderHealth.Evaluate(Status(), diag, Good(), true);
            Check(health.State == DashboardHealthState.Abnormal && health.ShowAction,
                "invalid old-driver pair remains red and actionable: " + version);
        }
        foreach (var version in new[] { "576.88", "620.99", "622.00", "700.00" })
        {
            Fixture(256 * mb, 512 * mb); var diag = Diagnose(version);
            Check(diag.Level == ShaderDiagLevel.Normal && diag.CacheLayout == ShaderCacheLayout.Unspecified && !diag.Summary.Contains("ASD"),
                "outside621 series retains prior diagnosis: " + version);
        }
        Fixture(256 * mb, mb, 0);
        var broken = Diagnose(); var brokenHealth = DashboardShaderHealth.Evaluate(Status(), broken, Good(), true);
        Check(broken.Level == ShaderDiagLevel.Abnormal && broken.CacheLayout == ShaderCacheLayout.Unspecified
            && brokenHealth.State == DashboardHealthState.Abnormal && brokenHealth.ShowAction, "zero-byte NVPH remains red and actionable");
        Fixture(256 * mb, 256 * mb, 0); var brokenPair = Diagnose();
        Check(brokenPair.Level == ShaderDiagLevel.Abnormal && brokenPair.CacheLayout == ShaderCacheLayout.Unspecified,
            "an exact 256MB pair cannot hide an empty intermediate file");
        Directory.CreateDirectory(Path.Combine(root, "GameVer_Old"));
        Check(Diagnose().Level == ShaderDiagLevel.Abnormal && Diagnose().CacheLayout == ShaderCacheLayout.Unspecified,
            "multi-version residue outranks ASD hint");
        Directory.Delete(Path.Combine(root, "GameVer_Old"));
        Fixture(); var empty = Diagnose();
        Check(empty.Level == ShaderDiagLevel.Info && empty.CacheLayout == ShaderCacheLayout.Unspecified && !empty.Summary.Contains("ASD已激活"),
            "no cache never claims active ASD");
        Fixture(512 * mb, 512 * mb); var legacy = Diagnose();
        var custom = new NvShaderCacheReport { Scopes = [new("全局设置", "custom", 10240, "fixture"), new("三角洲程序设置", "default", null, "fixture")] };
        var otherIssue = DashboardShaderHealth.Evaluate(Status(), legacy, custom, true);
        Check(otherIssue.State == DashboardHealthState.Abnormal && otherIssue.ShowAction && otherIssue.OpenCacheRecovery,
            "real nondefault driver cache remains red and actionable over legacy hint");
        var incomplete = DashboardShaderHealth.Evaluate(Status(), legacy, new NvShaderCacheReport(), true);
        Check(incomplete.State == DashboardHealthState.Warning && incomplete.ShowAction && incomplete.OpenCacheRecovery,
            "cache size read failure still has recovery action; only benign ASD warning hides it");
        var failed = DashboardShaderHealth.Evaluate(Status(), legacy, Good(), true, readFailures: "fixture read failure");
        Check(failed.State == DashboardHealthState.Failed && failed.ShowAction, "independent read failure cannot inherit hidden ASD action");
        var wire = JsonSerializer.Deserialize<ShaderDiagnosis>(JsonSerializer.Serialize(legacy))!;
        Check(wire.Level == ShaderDiagLevel.Warning && wire.CacheLayout == ShaderCacheLayout.Legacy, "IPC retains layout and new warning");
        Check((int)ShaderDiagLevel.NotApplicable == 0 && (int)ShaderDiagLevel.Info == 1 && (int)ShaderDiagLevel.Normal == 2
            && (int)ShaderDiagLevel.Abnormal == 3 && (int)ShaderDiagLevel.AbnormalDriverIssue == 4 && (int)ShaderDiagLevel.Warning == 5,
            "existing shader diagnosis protocol values remain stable");
        var old = JsonSerializer.Deserialize<ShaderDiagnosis>("{\"Level\":2,\"Summary\":\"old\",\"Details\":[],\"Advice\":\"old\"}")!;
        Check(old.CacheLayout == ShaderCacheLayout.Unspecified, "legacy diagnosis payload cannot invent ASD layout");
        Fixture(256 * mb, 256 * mb); var activePair = Diagnose();
        var activeWire = JsonSerializer.Deserialize<ShaderDiagnosis>(JsonSerializer.Serialize(activePair))!;
        Check(activeWire.Level == ShaderDiagLevel.Normal && activeWire.CacheLayout == ShaderCacheLayout.Asd
            && activeWire.Summary.StartsWith("ASD已激活"), "exact 256MB pair remains active ASD over IPC");
        Check(DashboardShaderHealth.Evaluate(Status(), activePair, custom, true).State == DashboardHealthState.Abnormal,
            "active ASD pair does not hide independent driver-cache policy errors");
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "src/DeltaNFD/Views/DashboardPage.xaml"))) repo = repo.Parent;
        Check(repo is not null, "UI sources found");
        var page = XDocument.Load(Path.Combine(repo!.FullName, "src/DeltaNFD/Views/DashboardPage.xaml"));
        var action = page.Descendants().Single(e => (string?)e.Attribute("Click") == "ShaderAction_Click");
        Check((string?)action.Attribute("Visibility") == "{x:Bind ViewModel.ShaderActionVisible, Mode=OneWay}", "shader action uses its own visibility instead of generic warning action");
        var shaderPage = XDocument.Load(Path.Combine(repo.FullName, "src/DeltaNFD/Views/ShaderPage.xaml"));
        Check(shaderPage.Descendants().Any(e => (string?)e.Attribute("Foreground") == "{x:Bind DiagnosisBrush(ViewModel.DiagLevel), Mode=OneWay}"),
            "shader maintenance page binds diagnosis severity colors");
        var vm = File.ReadAllText(Path.Combine(repo.FullName, "src/DeltaNFD/ViewModels/DashboardViewModel.ReadOnly.cs"));
        Check(vm.Contains("ShaderActionAllowed = saved.ShaderActionAllowed") && vm.Contains("CacheAttention, bool ShaderActionAllowed"),
            "paused/cancelled health refresh restores action suppression with status");
        Console.WriteLine($"ASD layout: {count} checks passed; owned cache metadata and pure/static UI only; no real driver/game/cache writes. Evidence: {root}");
    }
}
