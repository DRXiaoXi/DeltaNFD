using DeltaNFD.Backends;

namespace DeltaNFD.Backend.CpuFrame;

internal static class Program
{
    private static Task<int> Main(string[] args) => BackendHost.RunAsync(CpuFrameCommands.Options, args);
}
