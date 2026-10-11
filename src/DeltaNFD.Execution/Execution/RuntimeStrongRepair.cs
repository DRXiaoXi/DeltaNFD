using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace DeltaNFD.Services;

internal sealed record RuntimeSystemProbe(string Name, bool Passed, string Detail);
internal sealed record RuntimeSystemRestartState(bool ComponentPending, bool UpdatePending, IReadOnlyList<string> PendingRenames)
{
    internal bool HasNewObligation(RuntimeSystemRestartState before) => ComponentPending && !before.ComponentPending
        || UpdatePending && !before.UpdatePending || PendingRenames.Except(before.PendingRenames, StringComparer.OrdinalIgnoreCase).Any();
}
internal enum RuntimeSystemRepairStep { ComponentStore, SystemFiles }
internal interface IRuntimeStrongRepairEnvironment
{
    Task<OperationResult> CheckReadyAsync(CancellationToken token);
    Task<RuntimeSystemRestartState> ReadRestartStateAsync(CancellationToken token);
    Task<IReadOnlyList<RuntimeSystemProbe>> ProbeAsync(CancellationToken token);
    Task<RuntimeStepResult> ExecuteAsync(RuntimeSystemRepairStep step, TimeSpan timeout,
        IProgress<string>? progress, CancellationToken token);
}

/// <summary>Shared opt-in phase. Windows owns API Sets/UCRT; never deploy arbitrary forwarder DLLs.</summary>
internal sealed class RuntimeStrongRepair(IRuntimeStrongRepairEnvironment environment, RuntimeRepairDiagnostics diagnostics, TimeSpan? repairBudget = null)
{
    internal static readonly TimeSpan SystemBudget = TimeSpan.FromMinutes(60);
    internal static readonly TimeSpan TotalBudget = SystemBudget + RuntimeRepairCoordinator.TotalBudget + TimeSpan.FromMinutes(1);

    internal async Task<OperationResult> RunAsync(
        Func<IProgress<string>?, CancellationToken, Task<OperationResult>> verify,
        Func<IProgress<string>?, CancellationToken, Task<OperationResult>> repair,
        IProgress<string>? progress, CancellationToken token)
    {
        using var total = CancellationTokenSource.CreateLinkedTokenSource(token);
        total.CancelAfter(repairBudget.HasValue ? SystemBudget + repairBudget.Value + TimeSpan.FromMinutes(1) : TotalBudget);
        var reboot = false;
        var originalStarted = false;
        OperationResult Stop(string message, bool unknown = false) => new()
        {
            Success = false, OutcomeUnknown = unknown, RequiresReboot = reboot,
            Message = (originalStarted ? "强力修复未完成，已有运行库操作不会撤销。\n" : "强力修复未完成，未继续运行库卸载／安装。\n")
                + message + "\n诊断目录：" + diagnostics.DirectoryPath,
        };
        try
        {
            total.Token.ThrowIfCancellationRequested();
            // Validate every selected payload before any system repair or runtime uninstall.
            var verified = await verify(progress, total.Token);
            if (!verified.Success) return verified;
            var ready = await environment.CheckReadyAsync(total.Token);
            if (!ready.Success) return ready;
            var restartBefore = await environment.ReadRestartStateAsync(total.Token);
            diagnostics.Save("strong-restart-before.json", restartBefore);
            if (restartBefore.ComponentPending)
            {
                reboot = true;
                return Stop("Windows 组件维护已有待重启操作，请重启后再进行强力修复。");
            }
            using var system = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
            system.CancelAfter(SystemBudget);
            var watch = Stopwatch.StartNew();
            IReadOnlyList<RuntimeSystemProbe> before;
            var beforeUnavailable = false;
            try { before = await environment.ProbeAsync(system.Token); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A damaged UCRT can prevent a new probe from starting; that is a reason to repair, not to skip repair.
                before = []; beforeUnavailable = true;
                diagnostics.Save("strong-before-warning.json", new { Error = ex.Message });
                progress?.Report("修复前独立抽查未完成，将继续系统修复，并在修复后重新启动探测进程。");
            }
            diagnostics.Save("strong-before.json", before);
            foreach (var step in Enum.GetValues<RuntimeSystemRepairStep>())
            {
                system.Token.ThrowIfCancellationRequested();
                var left = SystemBudget - watch.Elapsed;
                if (left <= TimeSpan.Zero) return Stop("系统修复达到时间上限。");
                var cap = step == RuntimeSystemRepairStep.ComponentStore ? TimeSpan.FromMinutes(35) : TimeSpan.FromMinutes(25);
                progress?.Report(step == RuntimeSystemRepairStep.ComponentStore
                    ? "强力修复 1/3：DISM 修复 Windows 组件存储，可能需要联网…"
                    : "强力修复 2/3：SFC 检查并修复受保护的系统文件…");
                var result = await environment.ExecuteAsync(step, left < cap ? left : cap, progress, system.Token);
                diagnostics.Save("strong-" + step + ".json", result);
                reboot |= result.RequiresReboot;
                if (!result.Success) return Stop(result.Details + "；退出码=" + result.ExitCode, result.OutcomeUnknown);
                var restartAfter = await environment.ReadRestartStateAsync(system.Token);
                diagnostics.Save("strong-restart-" + step + ".json", restartAfter);
                reboot |= restartAfter.HasNewObligation(restartBefore);
                // A pending OS restart must complete before a destructive C++ reinstall.
                if (reboot) return Stop("系统组件要求重启。请重启电脑后重新执行修复；本次尚未执行原运行库修复。");
            }
            var after = await environment.ProbeAsync(system.Token);
            diagnostics.Save("strong-after.json", after);
            if ((!beforeUnavailable && before.Count == 0) || after.Count == 0 || after.Any(p => !p.Passed)
                || after.Select(p => p.Name).Distinct().Count() != after.Count
                || (!beforeUnavailable && !before.Select(p => p.Name).Order().SequenceEqual(after.Select(p => p.Name).Order())))
                return Stop("API Sets／UCRT 抽查未通过或项目不完整：\n" + string.Join("\n", after.Where(p => !p.Passed).Select(p => p.Name + "：" + p.Detail)));
            total.Token.ThrowIfCancellationRequested();
            progress?.Report("强力修复 3/3：系统组件阶段通过，开始所选运行库修复…");
            originalStarted = true;
            var repaired = await repair(progress, total.Token);
            var combined = new OperationResult
            {
                Success = repaired.Success, IsPartial = repaired.IsPartial, OutcomeUnknown = repaired.OutcomeUnknown,
                RequiresReboot = reboot || repaired.RequiresReboot, RequiresLogoff = repaired.RequiresLogoff,
                RequiresDisplayConfirm = repaired.RequiresDisplayConfirm, IsSkipped = repaired.IsSkipped,
                Message = "强力模式：Windows 组件修复及 API Sets／UCRT 抽查通过。\n" + repaired.Message,
            };
            diagnostics.Save("strong-summary.json", combined);
            return combined;
        }
        catch (OperationCanceledException) { return Stop("已停止后续步骤或达到总时间上限；已执行的系统修复不会撤销。"); }
        catch (Exception ex) { return Stop("修复流程异常：" + ex.Message); }
    }
}

internal sealed class RuntimeStrongRepairEnvironment(RuntimeRepairDiagnostics diagnostics) : IRuntimeStrongRepairEnvironment
{
    private readonly RuntimeRepairProcessRunner _runner = new(AppDataPaths.Root);
    internal static readonly (string Contract, string Export)[] Contracts =
    [
        ("api-ms-win-crt-runtime-l1-1-0.dll", "_initterm"),
        ("api-ms-win-crt-heap-l1-1-0.dll", "malloc"),
        ("api-ms-win-crt-stdio-l1-1-0.dll", "fopen"),
        ("api-ms-win-crt-string-l1-1-0.dll", "strlen"),
        ("api-ms-win-crt-convert-l1-1-0.dll", "atoi"),
        ("api-ms-win-crt-math-l1-1-0.dll", "sqrt"),
        ("api-ms-win-crt-time-l1-1-0.dll", "_time64"),
        ("api-ms-win-crt-locale-l1-1-0.dll", "setlocale"),
    ];
    public Task<OperationResult> CheckReadyAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10) || !Environment.Is64BitProcess)
            return Task.FromResult(OperationResult.Fail("强力系统组件修复需要 Windows 10/11 x64。"));
        var pending = _runner.CheckPending(RuntimeRepairPreflight.InstallerBusy());
        var state = RuntimeRepairPreflight.Read();
        return Task.FromResult(pending is not null || state.Blocker is not null
            ? OperationResult.Fail(pending ?? state.Blocker!) : OperationResult.Ok("系统修复预检通过。"));
    }
    public Task<RuntimeSystemRestartState> ReadRestartStateAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var registry = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
        using var cbs = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        using var cbsProgress = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootInProgress");
        using var update = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        using var session = registry.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
        var pending = session?.GetValue("PendingFileRenameOperations") as string[] ?? [];
        // Some readers omit the trailing empty destination of a pending deletion.
        var pairs = Enumerable.Range(0, (pending.Length + 1) / 2)
            .Select(i => pending[i * 2] + "\n" + (i * 2 + 1 < pending.Length ? pending[i * 2 + 1] : "")).ToArray();
        return Task.FromResult(new RuntimeSystemRestartState(cbs is not null || cbsProgress is not null, update is not null, pairs));
    }
    public Task<IReadOnlyList<RuntimeSystemProbe>> ProbeAsync(CancellationToken token)
        => RuntimeSystemProbeProcess.ProbeAsync(token);

    internal static IReadOnlyList<RuntimeSystemProbe> ProbeInProcess(CancellationToken token)
    {
        var results = new List<RuntimeSystemProbe>();
        foreach (var arch in new[] { "x64", "x86" })
        {
            token.ThrowIfCancellationRequested();
            var directory = arch == "x64" ? Environment.SystemDirectory
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64");
            results.Add(ProbeUcrt(Path.Combine(directory, "ucrtbase.dll"), arch));
        }
        foreach (var (contract, export) in Contracts)
        {
            token.ThrowIfCancellationRequested();
            // API set aliases are resolved by the OS loader; absence of a physical DLL is not corruption.
            var module = LoadLibraryExW(contract, IntPtr.Zero, 0x800 /* LOAD_LIBRARY_SEARCH_SYSTEM32 */);
            try
            {
                var passed = module != IntPtr.Zero && GetProcAddress(module, export) != IntPtr.Zero;
                results.Add(new("x64/" + contract, passed, passed ? "系统加载器解析及导出抽查通过。" : "契约加载或导出解析失败。"));
            }
            finally { if (module != IntPtr.Zero) FreeLibrary(module); }
        }
        return results;
    }
    internal static RuntimeSystemProbe ProbeUcrt(string path, string arch)
    {
        try
        {
            if (arch is not ("x64" or "x86")) throw new InvalidDataException("架构无效。");
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(file);
            if (reader.ReadUInt16() != 0x5a4d) throw new InvalidDataException("文件格式异常。");
            file.Position = 0x3c; var offset = reader.ReadInt32();
            if (offset < 64 || offset > file.Length - 6) throw new InvalidDataException("PE 头异常。");
            file.Position = offset;
            if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != (arch == "x64" ? 0x8664 : 0x14c))
                throw new InvalidDataException("UCRT 架构不符。");
            return new(arch + "/ucrtbase.dll", true, "文件存在及 PE 架构抽查通过；完整性由 SFC 检查。");
        }
        catch (Exception ex) { return new(arch + "/ucrtbase.dll", false, ex.Message); }
    }
    internal static ProcessStartInfo Command(RuntimeSystemRepairStep step, string directory)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,
            step == RuntimeSystemRepairStep.ComponentStore ? "Dism.exe" : "sfc.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (step == RuntimeSystemRepairStep.ComponentStore)
            foreach (var arg in new[] { "/Online", "/Cleanup-Image", "/RestoreHealth", "/NoRestart", "/English", "/LogPath:" + Path.Combine(directory, "strong-dism.log") }) info.ArgumentList.Add(arg);
        else if (step == RuntimeSystemRepairStep.SystemFiles)
        {
            info.ArgumentList.Add("/scannow");
            info.StandardOutputEncoding = Encoding.Unicode;
            info.StandardErrorEncoding = Encoding.Unicode;
        }
        else throw new ArgumentOutOfRangeException(nameof(step));
        return info;
    }
    internal static bool SfcSummaryPassed(string output)
    {
        var text = Regex.Replace(output.Replace("\0", ""), @"\s+", "").ToLowerInvariant();
        if (new[] { "unabletofix", "couldnotperform", "couldnotstart", "无法修复", "无法执行", "无法启动", "無法修復", "無法執行", "無法啟動" }.Any(text.Contains)) return false;
        // Fail closed for missing/unrecognized localized output; exit code zero alone is insufficient.
        return text.Contains("windowsresourceprotectiondidnotfindanyintegrityviolations")
            || text.Contains("windowsresourceprotectionfoundcorruptfilesandsuccessfullyrepairedthem")
            || text.Contains("windows资源保护未找到任何完整性冲突")
            || text.Contains("windows资源保护找不到任何完整性冲突")
            || text.Contains("windows資源保護找不到任何完整性違規")
            || text.Contains("windows資源保護未發現任何完整性違規")
            || text.Contains("windows資源保護找到損毀的檔案並成功修復")
            || text.Contains("windows资源保护找到了损坏文件并成功修复了它们")
            || text.Contains("windows资源保护找到了损坏文件并成功修复了这些文件");
    }
    public async Task<RuntimeStepResult> ExecuteAsync(RuntimeSystemRepairStep step, TimeSpan timeout,
        IProgress<string>? progress, CancellationToken token)
    {
        using var capture = new CancellationTokenSource();
        Task stdout = Task.CompletedTask, stderr = Task.CompletedTask;
        var name = "strong-" + step;
        var result = await _runner.RunAsync(Command(step, diagnostics.DirectoryPath), name, timeout, progress, token,
            process =>
            {
                stdout = diagnostics.CaptureOutputAsync(process.StandardOutput, name + "-stdout.log", capture.Token);
                stderr = diagnostics.CaptureOutputAsync(process.StandardError, name + "-stderr.log", capture.Token);
            });
        capture.CancelAfter(result.OutcomeUnknown ? TimeSpan.Zero : TimeSpan.FromSeconds(5));
        try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(6)); }
        catch (Exception ex) { diagnostics.Save(name + "-output-warning.json", new { Error = ex.Message }); }
        if (!result.Success || result.RequiresReboot || step != RuntimeSystemRepairStep.SystemFiles) return result;
        try
        {
            var output = File.ReadAllText(Path.Combine(diagnostics.DirectoryPath, name + "-stdout.log"));
            return SfcSummaryPassed(output) ? result : result with { PostcheckPassed = false,
                Details = "SFC 未确认系统文件完整性通过；请查看诊断目录中的输出及 Windows Logs/CBS/CBS.log。未继续卸载运行库。" };
        }
        catch (Exception ex) { return result with { PostcheckPassed = false, Details = "SFC 输出未确认：" + ex.Message }; }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string file, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr module);
}
