using System.Diagnostics;
using DeltaNFD.Services;

internal static class Shader621CleanupChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new IOException(message); count++; }
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-shader621-" + id.ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        var target = new GameTargetService(Path.Combine(root, "settings.json"));
        string FileAt(string folder, string name = "cache.bin", bool readOnly = false)
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name); File.WriteAllText(path, "owned shader fixture");
            if (readOnly) File.SetAttributes(path, FileAttributes.ReadOnly);
            return path;
        }
        ShaderDriverReading Driver(string version, ShaderGpuDetectionState state = ShaderGpuDetectionState.Nvidia)
            => new(state, version, Source: "fixture");

        foreach (var version in new[] { "621.00", "621.01", "621.14", "621.99" })
            Check(ShaderCacheCleanupPolicy.IncludesSystemD3DCache(ShaderGpuDetectionState.Nvidia, version), "621 family includes system cache: " + version);
        foreach (var version in new string?[] { null, "", "unknown", "620.99", "622.00", "700.00", "621.100", "621.14.0", "32.0.16.2114", "621.-1" })
            Check(!ShaderCacheCleanupPolicy.IncludesSystemD3DCache(ShaderGpuDetectionState.Nvidia, version), "other or unverifiable versions do not widen cleanup: " + version);
        foreach (var state in new[] { ShaderGpuDetectionState.Unknown, ShaderGpuDetectionState.NotPresent })
            Check(!ShaderCacheCleanupPolicy.IncludesSystemD3DCache(state, "621.14"), "NVIDIA presence must be confirmed: " + state);

        foreach (var version in new[] { "621.00", "621.14", "621.99", "620.99", "622.00", "" })
        {
            var area = Path.Combine(root, "version-" + version.Replace('.', '-'));
            var pso = Path.Combine(area, "PSOCache"); var local = Path.Combine(area, "local");
            var gameFile = FileAt(Path.Combine(pso, "GameVer", "SM6", "DXCache"), "game.nvph", true);
            var system = Path.Combine(local, "D3DSCache");
            var systemFile = FileAt(Path.Combine(system, "app"), "system.dxcache");
            var unrelated = FileAt(Path.Combine(local, "NVIDIA", "DXCache"), "keep.nvph");
            var reads = 0;
            var service = new ShaderService(target, () => { reads++; return Driver(version); }, () => [pso], () => false, () => local);
            var result = await service.ClearPsoCacheAsync();
            var linked = version.StartsWith("621.");
            Check(result.Success && !result.IsPartial && !File.Exists(gameFile), "actual async service clears game cache: " + version);
            Check(File.Exists(systemFile) != linked, "actual async service applies the driver rule at execution: " + version);
            Check(File.Exists(unrelated), "game cleanup does not add NVIDIA DXCache: " + version);
            Check(reads == 1 && Directory.Exists(pso) && Directory.Exists(system), "single execution read and cache roots are retained");
            Check(!linked || result.Message.Contains("D3DSCache"), "result discloses coupled system scope");
        }

        var late = Path.Combine(root, "changed-driver"); var latePso = Path.Combine(late, "PSOCache"); var lateLocal = Path.Combine(late, "local");
        FileAt(latePso); var lateSystem = FileAt(Path.Combine(lateLocal, "D3DSCache"));
        var currentVersion = "621.14";
        var lateService = new ShaderService(target, () => Driver(currentVersion), () => [latePso], () => false, () => lateLocal);
        currentVersion = "622.00";
        Check((await lateService.ClearPsoCacheAsync()).Success && File.Exists(lateSystem), "driver is read when clearing rather than when the service is constructed");

        var mixed = Path.Combine(root, "readonly"); var mixedPso = Path.Combine(mixed, "PSOCache"); var mixedSystem = Path.Combine(mixed, "D3DSCache");
        var readOnlyGame = FileAt(Path.Combine(mixedPso, "old"), "old.nvph", true);
        var readOnlySystem = FileAt(Path.Combine(mixedSystem, "app"), "readonly.val", true);
        var partial = ShaderService.ClearPsoCache([mixedPso], () => false, systemD3DCacheRoot: mixedSystem);
        Check(partial.Success && partial.IsPartial && !File.Exists(readOnlyGame) && File.Exists(readOnlySystem), "partial system cleanup never hides incomplete work");
        Check(File.GetAttributes(readOnlySystem).HasFlag(FileAttributes.ReadOnly), "system read-only attributes are not cleared by PSOCache policy");
        Check(!Directory.Exists(Path.Combine(mixedPso, "old")) && Directory.Exists(Path.Combine(mixedSystem, "app")), "empty subdirectory cleanup stays limited to PSOCache");

        var lockedPso = Path.Combine(root, "locked", "PSOCache"); var lockedSystem = Path.Combine(root, "locked", "D3DSCache");
        FileAt(lockedPso); var lockedFile = FileAt(lockedSystem);
        using (var file = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failed = ShaderService.ClearPsoCache([lockedPso], () => false, systemD3DCacheRoot: lockedSystem);
            Check(failed.IsPartial && File.Exists(lockedFile) && failed.Message.Contains(lockedFile), "occupied system files are retained and reported");
        }
        Check(ShaderService.ClearPsoCache([lockedPso], () => false, systemD3DCacheRoot: lockedSystem).Success && !File.Exists(lockedFile), "cleanup can finish after the occupied file is released");

        var runningPso = Path.Combine(root, "running", "PSOCache"); var runningSystem = Path.Combine(root, "running", "D3DSCache");
        var runningGame = FileAt(runningPso); var runningOs = FileAt(runningSystem);
        Check(!ShaderService.ClearPsoCache([runningPso], () => true, systemD3DCacheRoot: runningSystem).Success
            && File.Exists(runningGame) && File.Exists(runningOs), "game-running gate protects both cache roots");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            Check(!ShaderService.ClearPsoCache([runningPso], () => false, cancellation.Token, runningSystem).Success
                && File.Exists(runningGame) && File.Exists(runningOs), "cancellation before cleanup touches neither root");
        }
        Check(!ShaderService.ClearPsoCache([runningPso], () => false, systemD3DCacheRoot: "D3DSCache").Success && File.Exists(runningGame), "relative system path is rejected before game deletion");
        var invalidRoot = FileAt(Path.Combine(root, "invalid-system"), "not-a-directory");
        Check(!ShaderService.ClearPsoCache([runningPso], () => false, systemD3DCacheRoot: invalidRoot).Success && File.Exists(runningGame), "invalid second root prevents every deletion");

        var midPso = Path.Combine(root, "mid-game", "PSOCache"); var midSystem = Path.Combine(root, "mid-game", "D3DSCache");
        var midFile = FileAt(midPso); var midOs = FileAt(midSystem);
        var midResult = ShaderService.ClearPsoCache([midPso], () => !File.Exists(midFile), systemD3DCacheRoot: midSystem);
        Check(midResult.IsPartial && !File.Exists(midFile) && File.Exists(midOs) && midResult.Message.Contains("已启动"), "game start between roots stops system cleanup and reports partial result");
        var cancelPso = Path.Combine(root, "mid-cancel", "PSOCache"); var cancelSystem = Path.Combine(root, "mid-cancel", "D3DSCache");
        var cancelFile = FileAt(cancelPso); var cancelOs = FileAt(cancelSystem);
        using (var cancellation = new CancellationTokenSource())
        {
            var result = ShaderService.ClearPsoCache([cancelPso], () => { if (!File.Exists(cancelFile)) cancellation.Cancel(); return false; },
                cancellation.Token, cancelSystem);
            Check(result.IsPartial && !File.Exists(cancelFile) && File.Exists(cancelOs), "cancellation between roots preserves remaining system cache");
        }
        var noLocalPso = Path.Combine(root, "no-local", "PSOCache"); var noLocalFile = FileAt(noLocalPso);
        var noLocal = new ShaderService(target, () => Driver("621.14"), () => [noLocalPso], () => false, () => "");
        Check(!(await noLocal.ClearPsoCacheAsync()).Success && File.Exists(noLocalFile), "unconfirmed LocalAppData never falls back to a relative system cache");
        var customSettings = Path.Combine(root, "custom-settings.json");
        File.WriteAllText(customSettings, System.Text.Json.JsonSerializer.Serialize(new AppSettings { CustomGameModeEnabled = true, CustomGameExecutablePath = Path.Combine(root, "custom.exe") }));
        var custom = new ShaderService(new GameTargetService(customSettings), () => throw new IOException("must reject before driver read"),
            () => [runningPso], () => false, () => root);
        Check(!(await custom.ClearPsoCacheAsync()).Success && File.Exists(runningGame), "custom-game gate still rejects coupled cleanup before probing or deleting");

        var systemOnly = Path.Combine(root, "system-only", "D3DSCache"); var systemOnlyFile = FileAt(systemOnly);
        Check(ShaderService.ClearPsoCache([], () => false, systemD3DCacheRoot: systemOnly).Success && !File.Exists(systemOnlyFile), "621 cleanup can handle a PSOCache that disappeared before execution");
        Check(!ShaderService.ClearPsoCache([], () => false).Success, "ordinary missing PSOCache behavior remains unchanged");

        var recoveryLocal = Path.Combine(root, "recovery-local");
        var linkedRoots = ShaderRecoveryCleanup.SystemRoots(true, false, Driver("621.14"), recoveryLocal);
        Check(linkedRoots.SequenceEqual(new[] { Path.Combine(recoveryLocal, "D3DSCache") }), "game-only recovery adds only D3DSCache for 621");
        Check(ShaderRecoveryCleanup.SystemRoots(true, false, Driver("620.99"), recoveryLocal).Count == 0, "older game-only recovery remains game-only");
        Check(ShaderRecoveryCleanup.SystemRoots(true, false, Driver(""), recoveryLocal).Count == 0, "unknown driver never silently adds recovery scope");
        var explicitRoots = ShaderRecoveryCleanup.SystemRoots(true, true, null, recoveryLocal);
        Check(explicitRoots.Count == 2 && explicitRoots.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2, "explicit NVIDIA recovery retains DXCache and a single D3DSCache root");
        var recoveryPso = Path.Combine(root, "recovery-PSOCache"); var recoveryGame = FileAt(recoveryPso, readOnly: true);
        var recoveryOs = FileAt(linkedRoots[0]);
        var recovered = ShaderRecoveryCleanup.CleanRoots([recoveryPso], () => false, gameCache: true, systemRoots: linkedRoots);
        Check(recovered.Success && !File.Exists(recoveryGame) && !File.Exists(recoveryOs), "recovery entry uses the same mixed-root safety behavior");
        Check(recovered.Message.Contains("D3DSCache"), "recovery result lists the added system scope");
        var recoveryPartialGame = FileAt(recoveryPso, "again.nvph", true);
        var recoveryReadOnly = FileAt(linkedRoots[0], "readonly.idx", true);
        var recoveryPartial = ShaderRecoveryCleanup.CleanRoots([recoveryPso], () => false, gameCache: true, systemRoots: linkedRoots);
        Check(!recoveryPartial.Success && recoveryPartial.IsPartial && !File.Exists(recoveryPartialGame) && File.Exists(recoveryReadOnly),
            "recovery entry does not claim full success when added system scope remains incomplete");

        var safe = Path.Combine(root, "preflight", "PSOCache"); var safeFile = FileAt(safe);
        var outside = Path.Combine(root, "outside"); var sentinel = FileAt(outside, readOnly: true);
        var junction = Path.Combine(root, "linked-system");
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "/d", "/c", "mklink", "/J", junction, outside }) info.ArgumentList.Add(arg);
        using (var process = Process.Start(info)!)
        { Check(process.WaitForExit(10000) && process.ExitCode == 0, "owned system-junction fixture created"); }
        try
        {
            var refused = ShaderService.ClearPsoCache([safe], () => false, systemD3DCacheRoot: junction);
            Check(!refused.Success && File.Exists(safeFile) && File.Exists(sentinel), "linked system root prevents game deletion and preserves outside files");
            Check(File.GetAttributes(sentinel).HasFlag(FileAttributes.ReadOnly), "linked target attributes remain untouched");
        }
        finally { Directory.Delete(junction); } // Remove this owned junction entry only, never recurse.

        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "DeltaNFD.sln"))) repository = repository.Parent;
        var page = File.ReadAllText(Path.Combine(repository!.FullName, "src/DeltaNFD/Views/ShaderPage.xaml.cs"));
        var recoveryPage = File.ReadAllText(Path.Combine(repository.FullName, "src/DeltaNFD/Views/ShaderRecoveryPage.xaml.cs"));
        Check(page.Contains("ShaderCacheCleanupPolicy.SystemCacheNotice") && recoveryPage.Contains("ShaderCacheCleanupPolicy.SystemCacheNotice"), "both confirmations disclose the expanded scope");
        var clearDialog = page[page.IndexOf("Title = \"清除旧着色器文件？\"", StringComparison.Ordinal)..];
        Check(clearDialog[..clearDialog.IndexOf("if (await confirm", StringComparison.Ordinal)].Contains("DefaultButton = ContentDialogButton.Close"), "expanded destructive confirmation defaults to cancel");
        Console.WriteLine($"Shader 621 system cleanup: {count} checks passed; actual service with injected driver/paths; only owned fixture caches deleted.");
    }
}
