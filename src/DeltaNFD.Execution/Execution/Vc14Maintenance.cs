namespace DeltaNFD.Services;

/// <summary>全新 14.x 全量重装入口（2026-10-15 用户授权）；旧 AIO/DirectX 命令保持下线。</summary>
public static class Vc14Maintenance
{
    public static Task<Vc14Scan> ScanAsync() => Task.Run(() =>
    {
        var scan = Vc14Catalog.Scan();
        Vc14Diagnostics.Save(scan);
        return scan;
    });

    public static Task<Vc14ReinstallPreview> PreviewAsync() =>
        Task.Run(() => Vc14Reinstall.Preview(new Vc14ReinstallEnvironment()));

    public static Task<OperationResult> RepairAsync(string fingerprint, IProgress<string>? progress, CancellationToken token)
        => Task.Run(() => RuntimeGuardProtection.Exclusive(() =>
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromMinutes(40));
            return Vc14Reinstall.RunAsync(new Vc14ReinstallEnvironment(), fingerprint, progress, budget.Token).GetAwaiter().GetResult();
        }), token);
}
