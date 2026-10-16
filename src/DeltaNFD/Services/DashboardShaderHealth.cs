using DeltaNFD.Services;

namespace DeltaNFD.ViewModels;

internal sealed record DashboardShaderHealthResult(DashboardHealthState State, string Text, bool OpenCacheRecovery);

/// <summary>合并主页的缓存文件、驱动版本及 NVAPI 缓存大小结论；已确认异常不被局部读取失败覆盖。</summary>
internal static class DashboardShaderHealth
{
    internal static async Task<DashboardShaderHealthResult> CheckAsync(IShaderService shader, INvProfileService nv)
    {
        ShaderStatus? status = null;
        ShaderDiagnosis? diagnosis = null;
        NvShaderCacheReport? cache = null;
        var failures = new List<string>();
        string cacheError = "";
        try { status = await shader.GetStatusAsync(); }
        catch (Exception ex) { failures.Add("驱动状态：" + ex.Message); }
        var checkCache = status is null || status.IsNvidia || status.GpuDetectionState == ShaderGpuDetectionState.Unknown;
        if (checkCache)
        {
            try
            {
                var environment = await nv.GetEnvironmentAsync();
                checkCache = environment.IsNvidia || status?.IsNvidia == true;
                cache = environment.ShaderCache;
            }
            catch (Exception ex) { cacheError = ex.Message; }
        }
        try { diagnosis = await shader.DiagnoseAsync(); }
        catch (Exception ex) { failures.Add("缓存文件：" + ex.Message); }
        return Evaluate(status, diagnosis, cache, checkCache, cacheError, string.Join("；", failures));
    }

    internal static DashboardShaderHealthResult Evaluate(ShaderStatus? status, ShaderDiagnosis? diagnosis,
        NvShaderCacheReport? cache, bool checkCache, string cacheError = "", string readFailures = "")
    {
        var issues = new List<string>();
        var notes = new List<string>();
        var nonDefault = checkCache && cache?.HasNonDefault == true;
        var incomplete = checkCache && cache?.Complete != true;
        if (nonDefault)
        {
            var scopes = string.Join("、", cache!.Scopes.Where(s => s.State == "custom").Select(s => s.Scope).Distinct());
            issues.Add($"着色器缓存大小非默认（{scopes}），请恢复驱动默认，避免三角洲抽帧");
        }
        if (diagnosis?.Level is ShaderDiagLevel.Abnormal or ShaderDiagLevel.AbnormalDriverIssue)
            issues.Add(diagnosis.Summary);
        if (status?.DriverState is ShaderDriverState.TooOld or ShaderDriverState.ProblemSeries)
            issues.Add(status.DriverState == ShaderDriverState.TooOld
                ? $"驱动过老（{status.DriverVersion}）" : $"问题驱动（{status.DriverVersion}）");
        if (incomplete)
            notes.Add(nonDefault ? "部分缓存大小设置未能读取，请同时核对"
                : "着色器缓存大小检测不完整，无法确认是否为驱动默认" + (cacheError.Length > 0 ? "：" + cacheError : ""));
        if (readFailures.Length > 0) notes.Add("着色器检测未完成：" + readFailures);
        else if (status is null || diagnosis is null) notes.Add("未返回完整着色器检测结果");

        var failed = readFailures.Length > 0 || status is null || diagnosis is null;
        if (issues.Count > 0 || incomplete || failed)
            return new(issues.Count > 0 ? DashboardHealthState.Abnormal
                : failed ? DashboardHealthState.Failed : DashboardHealthState.Warning,
                string.Join("；", issues.Concat(notes)), nonDefault || incomplete);

        var state = diagnosis!.Level switch
        {
            ShaderDiagLevel.Normal => DashboardHealthState.Normal,
            ShaderDiagLevel.Info => DashboardHealthState.Information,
            _ => DashboardHealthState.NotApplicable,
        };
        return new(state, diagnosis.Level == ShaderDiagLevel.Normal && status!.IsNvidia && status.DriverVersion.Length > 0
            ? $"正常（{status.DriverVersion}）" : diagnosis.Summary, false);
    }
}
