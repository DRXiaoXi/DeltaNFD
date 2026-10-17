using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using DeltaNFD.ViewModels;

internal static class DashboardShaderHealthChecks
{
    public static async Task RunAsync(string? liveLayout)
    {
        int count = 0;
        void Check(bool pass, string message)
        {
            if (!pass) throw new Exception(message);
            count++;
            Console.WriteLine("PASS: " + message);
        }
        NvShaderCacheReport Report(string global, string game, uint value = uint.MaxValue) => new()
        {
            Scopes = [new("全局设置", global, value, "fixture"), new("三角洲程序设置", game, value, "fixture")],
        };
        var shader = new ShaderFake();
        var nv = new NvFake { Report = Report("custom", "custom") };
        foreach (var states in new[] { ("custom", "default"), ("default", "custom"), ("custom", "custom") })
        foreach (var value in new uint[] { 0, 10240, uint.MaxValue })
        {
            nv.Report = Report(states.Item1, states.Item2, value);
            var result = await DashboardShaderHealth.CheckAsync(shader, nv);
            Check(result.State == DashboardHealthState.Abnormal && result.OpenCacheRecovery
                && result.Text.Contains("非默认") && result.Text.Contains("恢复驱动默认"),
                $"normal PSO and driver cannot hide {states} cache override {value}");
            Check(DashboardHealthPresentation.Summary(false, result.State, DashboardHealthState.Normal,
                DashboardHealthState.Normal) == "1 项异常", "homepage summary counts cache override");
        }
        nv.Report = Report("default", "inherited", 16384);
        var good = await DashboardShaderHealth.CheckAsync(shader, nv);
        Check(good.State == DashboardHealthState.Normal && !good.OpenCacheRecovery, "driver-defined default can be any capacity");
        Check(good.Text == "正常（596.49）", "existing normal driver text retained");
        foreach (var missing in new NvShaderCacheReport?[] { null, new(), Report("default", "unknown") })
        {
            nv.Report = missing;
            var result = await DashboardShaderHealth.CheckAsync(shader, nv);
            Check(result.State == DashboardHealthState.Warning && result.OpenCacheRecovery
                && !result.Text.Contains("正常"), "missing or partial cache data never produces green normal");
        }
        nv.Report = Report("custom", "unknown");
        var partial = await DashboardShaderHealth.CheckAsync(shader, nv);
        Check(partial.State == DashboardHealthState.Abnormal && partial.Text.Contains("未能读取"), "known override stays red despite unreadable scope");
        nv.Report = Report("default", "inherited");
        shader.Level = ShaderDiagLevel.Abnormal;
        var pso = await DashboardShaderHealth.CheckAsync(shader, nv);
        Check(pso.State == DashboardHealthState.Abnormal && !pso.OpenCacheRecovery && pso.Text.Contains("PSO异常"), "existing PSO abnormality and maintenance route retained");
        nv.Error = true;
        var both = await DashboardShaderHealth.CheckAsync(shader, nv);
        Check(both.State == DashboardHealthState.Abnormal && both.Text.Contains("无法确认"), "NVAPI failure cannot hide PSO abnormality");
        shader.Level = ShaderDiagLevel.Normal;
        Check((await DashboardShaderHealth.CheckAsync(shader, nv)).State == DashboardHealthState.Warning, "NVAPI failure alone shows warning");
        nv.Error = false;
        nv.Report = Report("custom", "custom");
        shader.DiagnosisError = true;
        var diagFailed = await DashboardShaderHealth.CheckAsync(shader, nv);
        Check(diagFailed.State == DashboardHealthState.Abnormal && diagFailed.OpenCacheRecovery
            && diagFailed.Text.Contains("非默认") && diagFailed.Text.Contains("检测未完成"), "failed PSO read cannot discard confirmed cache override");
        shader.DiagnosisError = false;
        shader.StatusError = true;
        Check((await DashboardShaderHealth.CheckAsync(shader, nv)).State == DashboardHealthState.Abnormal, "GPU read still detects override after shader status failure");
        shader.StatusError = false;
        foreach (var level in new[] { ShaderDiagLevel.Info, ShaderDiagLevel.NotApplicable })
        {
            shader.Level = level;
            Check((await DashboardShaderHealth.CheckAsync(shader, nv)).State == DashboardHealthState.Abnormal, "unwarmed or absent PSO cannot hide driver setting override");
        }
        shader.Level = ShaderDiagLevel.Normal;
        shader.Driver = ShaderDriverState.TooOld;
        nv.Report = Report("default", "inherited");
        Check((await DashboardShaderHealth.CheckAsync(shader, nv)).State == DashboardHealthState.Abnormal, "old driver remains abnormal with default cache");
        shader.Driver = ShaderDriverState.Ok;
        nv.Report = Report("custom", "custom");
        Check((await DashboardShaderHealth.CheckAsync(shader, nv)).OpenCacheRecovery, "override detected before refresh");
        nv.Report = Report("default", "inherited");
        Check((await DashboardShaderHealth.CheckAsync(shader, nv)).State == DashboardHealthState.Normal, "refresh re-reads restored default and clears stale red status");
        shader.Nvidia = false;
        var calls = nv.Reads;
        Check((await DashboardShaderHealth.CheckAsync(shader, nv)).State == DashboardHealthState.Normal && calls == nv.Reads, "confirmed non-NVIDIA skips NVAPI cache check");
        Console.WriteLine($"Dashboard shader health: {count} assertions passed; all write methods reject invocation.");

        if (liveLayout is null) return;
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-dashboard-readonly", id.ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        Environment.SetEnvironmentVariable(BackendPaths.BackendRootEnvironmentVariable, Path.Combine(Path.GetFullPath(liveLayout), "Backends"));
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT", root);
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ID", id.ToString("D"));
        try
        {
            var service = new BackendNvProfileService();
            var actual = await DashboardShaderHealth.CheckAsync(new BackendShaderService(), service);
            var cache = (await service.GetEnvironmentAsync()).ShaderCache;
            Console.WriteLine($"Actual homepage state: {actual.State}; cache custom={cache?.HasNonDefault}; complete={cache?.Complete}\n{actual.Text}");
            Check(cache?.HasNonDefault != true || actual.State == DashboardHealthState.Abnormal && actual.OpenCacheRecovery,
                "actual NVAPI override must appear red on homepage");
            var pending = await BackendLifecycle.PrepareHandoverAsync("dashboard-readonly-check", TimeSpan.FromSeconds(10));
            Check(pending.Count == 0, "actual read-only backends drained");
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class ShaderFake : IShaderService
    {
        public bool Nvidia = true, StatusError, DiagnosisError;
        public ShaderDriverState Driver = ShaderDriverState.Ok;
        public ShaderDiagLevel Level = ShaderDiagLevel.Normal;
        public Task<ShaderStatus> GetStatusAsync() => StatusError ? throw new IOException("fixture status failure")
            : Task.FromResult(new ShaderStatus { IsNvidia = Nvidia, GpuDetectionState = Nvidia ? ShaderGpuDetectionState.Nvidia : ShaderGpuDetectionState.NotPresent,
                DriverState = Driver, DriverVersion = "596.49", DriverAdvice = "fixture", IsGameRunning = false });
        public Task<ShaderDiagnosis> DiagnoseAsync() => DiagnosisError ? throw new IOException("fixture PSO failure")
            : Task.FromResult(new ShaderDiagnosis { Level = Level, Summary = Level == ShaderDiagLevel.Abnormal ? "PSO异常" : "fixture", Details = [], Advice = "fixture" });
        public Task<OperationResult> ClearPsoCacheAsync() => throw new Exception("Unexpected write");
    }
    private sealed class NvFake : INvProfileService
    {
        public NvShaderCacheReport? Report;
        public bool Error;
        public int Reads;
        public Task<NvGpuEnvironmentInfo> GetEnvironmentAsync()
        {
            Reads++;
            if (Error) throw new IOException("fixture NVAPI failure");
            return Task.FromResult(new NvGpuEnvironmentInfo { IsNvidia = true, GpuName = "fixture", NvApiAvailable = true,
                DrsFilesExist = true, DrsFilesLocked = false, ShaderCache = Report });
        }
        public IReadOnlyList<NvSettingDef> GetSettingCatalog() => throw new Exception("Unexpected catalog read");
        public Task<Dictionary<uint, (bool HasOverride, uint Value)>?> GetGameSettingValuesAsync() => throw new Exception("Unexpected settings read");
        public Task<OperationResult> ApplyGameSettingsAsync(IReadOnlyList<(uint Id, uint? Value)> changes) => throw new Exception("Unexpected write");
        public Task<OperationResult> SetDrsFilesReadOnlyAsync(bool readOnly) => throw new Exception("Unexpected write");
        public Task<OperationResult> SetShaderCacheSizeAsync(string scope, uint? value) => throw new Exception("Unexpected write");
    }
}
