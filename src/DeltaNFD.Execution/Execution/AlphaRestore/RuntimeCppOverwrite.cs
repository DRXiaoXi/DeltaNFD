namespace DeltaNFD.Services.AlphaRestore;
internal static class RuntimeCppPlan
{
    internal static IReadOnlyList<RuntimeRepairStep> CppPlan() => OfficialCppCatalog.Packages.Select(p =>
        new RuntimeRepairStep(p.Id, p.Year + " " + p.Arch + " 微软原包", p.Year + " (" + p.Arch + ")", "official-exe", p.Exe)).ToArray();
}
internal sealed class RuntimeCppOverwriteEnvironment(RuntimeRepairDiagnostics diagnostics)
{
    private static string CppRoot => Path.Combine(BackendEnvironment.AssetsRoot, "RuntimeCppOfficial");
    internal static void VerifyCppPayload(string root, CancellationToken token, bool verifyHashes) => OfficialCppCatalog.VerifyAll(root, token);
    public Task<RuntimeStepResult> RepairCppAsync(RuntimeRepairStep step, TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
    {
        var package = OfficialCppCatalog.Packages.Single(p => p.Id == step.Id && p.Exe == step.RelativeFile);
        return new OfficialCppInstaller(new OfficialCppEnvironment(CppRoot), diagnostics)
            .RunAsync(package, false, timeout, progress, token);
    }
    internal static bool CanRetainRelatedPackage(RuntimeMsiPackage source, RuntimeMsiPackage installed) =>
        Guid.TryParse(source.UpgradeCode, out var family) && Guid.TryParse(installed.UpgradeCode, out var other) && family == other
        && source.Architecture == installed.Architecture && installed.Publisher.Equals(source.Publisher, StringComparison.OrdinalIgnoreCase)
        && installed.Name.Contains("Visual C++", StringComparison.OrdinalIgnoreCase) && !installed.Name.Contains("Debug", StringComparison.OrdinalIgnoreCase)
        && Version.TryParse(installed.Version, out var version) && Normalize(version) > Normalize(Version.Parse(source.Version)) && installed.Files.Count > 0;
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}
