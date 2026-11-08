using DeltaNFD.Services;
using DeltaNFD.ViewModels;

internal static class ShaderCacheViewChecks
{
    internal static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        foreach (var def in NvSettingsCatalog.Definitions)
        {
            var row = new NvSettingRowVm { Def = def };
            var dlss = def.Id is 0x10E41DF3 or 0x10E41DF5 or 0x10E41DF1 or 0x10308298;
            Check(row.ShowDescription == !dlss, def.Title + ": only DLSS descriptions move out of the row");
            Check(row.TitleHelpText == (dlss ? def.Description : ""), def.Title + ": title help retains the complete description");
        }
        var shader = new ShaderFake(); var nv = new NvFake();
        ServiceLocator.Shader = shader; ServiceLocator.Cleanup = new CleanupFake(); ServiceLocator.NvProfile = nv;
        var vm = new ShaderViewModel();
        nv.Report = Report("default", "inherited"); await vm.LoadAsync();
        Check(vm.ShaderCacheGlobalText.StartsWith("驱动默认") && vm.ShaderCacheGameText.StartsWith("继承全局设置"), "scope display distinguishes default and inherited values");
        Check(!vm.ShaderCacheCheckFailed && !vm.ShaderCacheNonDefault, "complete default data has no warning");
        nv.Report = Report("custom", "unknown"); await vm.LoadAsync();
        Check(vm.ShaderCacheNonDefault && vm.ShaderCacheGlobalText.StartsWith("非默认设置") && vm.ShaderCacheGameText.StartsWith("状态未确认"), "known override and unreadable game scope remain distinct");
        nv.Report = null; await vm.LoadAsync();
        Check(vm.ShaderCacheCheckFailed && vm.ShaderCacheGlobalText.Contains("未确认") && vm.ShaderCacheGameText.Contains("未确认"), "old backend missing report cannot display default");
        nv.Report = new() { Scopes = [new("全局设置", "default", null, "one"), new("全局设置", "default", null, "duplicate")] };
        await vm.LoadAsync();
        Check(vm.ShaderCacheCheckFailed && vm.ShaderCacheGlobalText.Contains("未确认") && vm.ShaderCacheGameText.Contains("未确认")
            && vm.ShaderCacheAdvice.Contains("无法确认"), "duplicate scopes cannot impersonate a complete two-scope read");
        nv.Error = true; await vm.LoadAsync();
        Check(!vm.IsBusy && vm.ShaderCacheCheckFailed && vm.ShaderCacheGlobalText.Contains("失败"), "NVAPI read failure releases busy state and invalidates displayed scopes");
        nv.Error = false; nv.Report = Report("default", "default"); await vm.LoadAsync();
        shader.Error = true; await vm.LoadAsync();
        Check(vm.ShaderCacheCheckFailed && !vm.CanClear && vm.ShaderCacheGlobalText.Contains("失败") && !vm.IsBusy, "failed base status read cannot retain a prior default or enable cleanup");
        shader.Error = false; shader.Nvidia = false; var reads = nv.Reads; await vm.LoadAsync();
        Check(nv.Reads == reads && vm.ShaderCacheGlobalText.Contains("不适用") && vm.ShaderCacheGameText.Contains("不适用"), "non-NVIDIA has explicit inapplicable scope display without NVAPI access");
        shader.Unknown = true; await vm.LoadAsync();
        Check(vm.ShaderCacheCheckFailed && vm.ShaderCacheGlobalText.Contains("未确认") && !vm.ShaderCacheGlobalText.Contains("不适用"), "unknown GPU never impersonates a confirmed non-NVIDIA device");
        Check(nv.Writes == 0, "opening and refreshing cache status never writes driver settings");
        Console.WriteLine($"Shader cache and DLSS UI: {checks} passed; actual view-models, fake reads only.");
    }

    private static NvShaderCacheReport Report(string global, string game) => new()
    { Scopes = [new("全局设置", global, null, "global fixture"), new("三角洲程序设置", game, null, "game fixture")] };
    private sealed class ShaderFake : IShaderService
    {
        internal bool Nvidia = true, Error, Unknown;
        public Task<ShaderStatus> GetStatusAsync() => Error ? throw new IOException("fixture base read failed") : Task.FromResult(new ShaderStatus
        { IsNvidia = Nvidia, GpuDetectionState = Unknown ? ShaderGpuDetectionState.Unknown : Nvidia ? ShaderGpuDetectionState.Nvidia : ShaderGpuDetectionState.NotPresent,
            DriverState = ShaderDriverState.Ok, DriverAdvice = "fixture", IsGameRunning = false, PsoCachePath = "fixture" });
        public Task<ShaderDiagnosis> DiagnoseAsync() => Task.FromResult(new ShaderDiagnosis
        { Level = ShaderDiagLevel.Normal, Summary = "fixture", Details = [], Advice = "fixture" });
        public Task<OperationResult> ClearPsoCacheAsync() => throw new Exception("Unexpected real cleanup");
    }
    private sealed class CleanupFake : ICleanupService
    {
        public Task<List<CleanupTarget>> ScanAsync() => throw new Exception("Unexpected scan");
        public Task<(double FreedMb, List<string> Skipped)> CleanAsync(IProgress<string>? progress = null) => throw new Exception("Unexpected cleanup");
        public Task<ShaderCacheCleanupResult> CleanShaderCachesAsync(IProgress<string>? progress = null, CancellationToken token = default) => throw new Exception("Unexpected cleanup");
    }
    private sealed class NvFake : INvProfileService
    {
        internal NvShaderCacheReport? Report;
        internal bool Error;
        internal int Reads, Writes;
        public Task<NvGpuEnvironmentInfo> GetEnvironmentAsync()
        {
            Reads++; if (Error) throw new IOException("fixture NVAPI read failed");
            return Task.FromResult(new NvGpuEnvironmentInfo { IsNvidia = true, GpuName = "fixture", NvApiAvailable = true, DrsFilesExist = true, DrsFilesLocked = false, ShaderCache = Report });
        }
        public IReadOnlyList<NvSettingDef> GetSettingCatalog() => NvSettingsCatalog.Definitions;
        public Task<Dictionary<uint, (bool HasOverride, uint Value)>?> GetGameSettingValuesAsync() => throw new Exception("Unexpected setting read");
        private Task<OperationResult> Deny() { Writes++; throw new Exception("Unexpected driver write"); }
        public Task<OperationResult> ApplyGameSettingsAsync(IReadOnlyList<(uint Id, uint? Value)> changes, bool rebarWarningAccepted = false) => Deny();
        public Task<OperationResult> SetDrsFilesReadOnlyAsync(bool readOnly) => Deny();
        public Task<OperationResult> SetShaderCacheSizeAsync(string scope, uint? value) => Deny();
    }
}
