using DeltaNFD.Backends;

namespace DeltaNFD.Backend.Gpu;

internal static class Program
{
    private static Task<int> Main(string[] args) => BackendHost.RunAsync(GpuCommands.Options, args);
}
