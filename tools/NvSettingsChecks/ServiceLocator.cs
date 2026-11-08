namespace DeltaNFD.Services;

internal static class ServiceLocator
{
    public static INvProfileService NvProfile { get; set; } = null!;
    public static IShaderService Shader { get; set; } = null!;
    public static ICleanupService Cleanup { get; set; } = null!;
    public static GameTargetService GameTarget => GameTargetService.Default;
}
