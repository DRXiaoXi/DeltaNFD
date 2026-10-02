using DeltaNFD.Services;

internal static class ShaderCachePathChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD_ShaderPaths_" + Guid.NewGuid().ToString("N"));
        var version = Path.Combine(root, "GameVer_Test");
        var sm5 = Path.Combine(version, "SM5", "DXCache");
        var sm6 = Path.Combine(version, "SM6", "DXCache");
        var service = new ShaderService(null, () => (ShaderGpuDetectionState.Nvidia, "596.36"));
        try
        {
            Directory.CreateDirectory(version);
            Require(ShaderService.FindDxCachePath(version) is null, "both paths absent");
            Require(service.DiagnoseAt(root).Summary.Contains("SM5"), "missing message names both models");
            Directory.CreateDirectory(sm5);
            File.WriteAllBytes(Path.Combine(sm5, "fixture.nvph"), []);
            Require(ShaderService.FindDxCachePath(version) == sm5, "SM5 fallback");
            var diagnosis = service.DiagnoseAt(root);
            Require(diagnosis.Level == ShaderDiagLevel.Abnormal && diagnosis.Summary.Contains("0 KB"), "SM5 file actually diagnosed");
            Require(diagnosis.Details.Any(d => d.Contains("SM5\\DXCache")), "chosen path visible");
            Require(ShaderService.HasProblemDriverCacheSignature(ShaderGpuDetectionState.Nvidia, "617.14", root), "homepage uses SM5 fallback");
            var unknown = new ShaderService(null, () => (ShaderGpuDetectionState.Nvidia, ""));
            Require(unknown.DiagnoseAt(root).Level == ShaderDiagLevel.NotApplicable, "unknown driver still refuses structural conclusion");
            Directory.CreateDirectory(sm6);
            Require(ShaderService.FindDxCachePath(version) == sm6, "SM6 priority when both exist");
            Require(service.DiagnoseAt(root).Level == ShaderDiagLevel.Info, "empty SM6 does not silently select SM5");
            Require(!ShaderService.HasProblemDriverCacheSignature(ShaderGpuDetectionState.Nvidia, "617.14", root), "homepage and diagnosis select same path");
            File.WriteAllBytes(Path.Combine(sm6, "fixture.nvph"), []);
            Require(service.DiagnoseAt(root).Details.Any(d => d.Contains("SM6\\DXCache")), "SM6 behavior retained");
            Console.WriteLine("Shader path checks passed: SM5 fallback, SM6 priority, actual diagnosis, homepage consistency, unknown driver.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
