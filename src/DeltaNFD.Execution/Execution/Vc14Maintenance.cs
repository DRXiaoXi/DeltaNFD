namespace DeltaNFD.Services;

/// <summary>14.x 修复入口（原“全量重装”命名，范围仅 C++ 14.x）；旧 AIO/DirectX 命令保持下线。</summary>
public static class Vc14Maintenance
{
    public static Task<Vc14Scan> ScanAsync() => Task.Run(() =>
    {
        var scan = Vc14Catalog.Scan();
        Vc14Diagnostics.Save(scan);
        return scan;
    });

    public static Task<Vc14ReinstallPreview> PreviewAsync(Vc14RepairMode mode = Vc14RepairMode.Full, bool allowTemporaryInstaller = false,
        Vc14Baseline baseline = Vc14Baseline.V144) =>
        !baseline.IsSupported()
            ? Task.FromResult(new Vc14ReinstallPreview(new Vc14Scan([], [], [baseline.UnsupportedMessage()]), [], "", false, baseline.UnsupportedMessage()) { Baseline = baseline })
            : Task.Run(() => Vc14Reinstall.Preview(new Vc14ReinstallEnvironment(allowTemporaryInstaller, baseline), mode));

    public static Task<OperationResult> RepairAsync(string fingerprint, IProgress<string>? progress, CancellationToken token, Vc14RepairMode mode = Vc14RepairMode.Full,
        bool allowTemporaryInstaller = false, Vc14Baseline baseline = Vc14Baseline.V144)
        => !baseline.IsSupported()
            ? Task.FromResult(OperationResult.Fail(baseline.UnsupportedMessage()))
            : Task.Run(() => RuntimeGuardProtection.Exclusive(() =>
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromMinutes(40));
            return Vc14Reinstall.RunAsync(new Vc14ReinstallEnvironment(allowTemporaryInstaller, baseline), fingerprint, progress, budget.Token, mode).GetAwaiter().GetResult();
        }), token);

    public static Task<OperationResult> RestoreTemporaryInstallerAsync() => Task.Run(() => RuntimeGuardProtection.Exclusive(() =>
        new SafeInstallerEngine(new SafeInstallerWindowsPlatform(), new SafeInstallerFileStore(AppDataPaths.Root)).Restore()));
}
