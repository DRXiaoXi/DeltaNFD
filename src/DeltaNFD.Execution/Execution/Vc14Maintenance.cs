namespace DeltaNFD.Services;

/// <summary>Only the newly authorized v14 CRT workflow. Legacy AIO/DirectX commands remain retired.</summary>
public static class Vc14Maintenance
{
    public static Task<Vc14Scan> ScanAsync() => Task.Run(() => new Vc14Environment().Scan());
    public static Task<OperationResult> CleanLegacyAsync(string fingerprint, IProgress<string>? progress, CancellationToken token)
        => Task.Run(() => RuntimeGuardProtection.Exclusive(() => Vc14LegacyCleanup.Run(new Vc14LegacyEnvironment(), fingerprint, progress, token)), token);
    public static Task<Vc14Preview> PreviewAsync() => Task.Run(() => Vc14Repair.Preview(new Vc14Environment()));
    public static Task<OperationResult> RepairAsync(string fingerprint, IProgress<string>? progress, CancellationToken token)
        => Task.Run(() => RuntimeGuardProtection.Exclusive(() =>
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromMinutes(40));
            return Vc14Repair.RunAsync(new Vc14Environment(), fingerprint, progress, budget.Token).GetAwaiter().GetResult();
        }), token);
}
