using System.Diagnostics;
namespace DeltaNFD.Services;
internal sealed class NonCppRuntimeEnvironment : INonCppRuntimeEnvironment
{
    public string Assets => Path.Combine(BackendEnvironment.AssetsRoot, "DirectXRepair");
    public string State => Path.Combine(AppDataPaths.Root, "non-cpp-runtime");
    public string Target(NonCppSource f)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windows) || !Path.IsPathFullyQualified(windows)) throw new IOException("系统目录不可确认。");
        return Path.Combine(windows, f.Architecture == "x64" ? "System32" : "SysWOW64", f.Name);
    }
    public async Task<string?> BlockerAsync(CancellationToken token)
    {
        if (new Vc14ReinstallEnvironment(baseline: Vc14Baseline.V144).Blocker() is { } blocked) return blocked;
        var ready = await new AlphaRestore.RuntimeGuardService().CheckRepairEnvironmentAsync().WaitAsync(token);
        return ready.CanProceed ? null : ready.Summary + "\n" + ready.Details;
    }
    public bool Trusted(string path) => NonCppRuntimeTrust.Verify(path);
    public async Task RegisterAsync(NonCppSource f, string path, CancellationToken token)
    {
        NonCppRuntimeMaintenance.NoLinks(path);
        using var pinned = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (NonCppRuntimeMaintenance.Hash(path) != f.Sha256.ToUpperInvariant() || !Trusted(path)) throw new IOException("注册前文件校验失败。");
        var executable = Path.Combine(Path.GetDirectoryName(Target(f))!, "regsvr32.exe");
        NonCppRuntimeMaintenance.NoLinks(executable);
        var command = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        command.ArgumentList.Add("/s"); command.ArgumentList.Add(path);
        using var process = Process.Start(command) ?? throw new IOException("无法启动系统 COM 注册程序。");
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { process.Kill(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); throw new IOException("COM 注册超时，结果待核对。"); }
        if (process.ExitCode != 0) throw new IOException("COM 注册失败，退出码 " + process.ExitCode);
    }
}
