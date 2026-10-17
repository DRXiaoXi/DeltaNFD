using System.Reflection;

namespace DeltaNFD.Services;

internal static class ReleaseFeatures
{
    internal static bool RuntimeUiEnabled { get; } = !string.Equals(
        typeof(ReleaseFeatures).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(a => a.Key == "RuntimeUiEnabled")?.Value, "false", StringComparison.OrdinalIgnoreCase);
}
