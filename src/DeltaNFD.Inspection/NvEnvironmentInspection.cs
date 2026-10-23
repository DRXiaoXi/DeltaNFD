using DeltaNFD.Services;

namespace DeltaNFD.Inspection;
public static class NvEnvironmentInspection
{
    public static Task<NvGpuEnvironmentInfo> ReadAsync() => InspectionScheduler.Run(async () =>
    {
        var shader = await new ShaderInspection().GetStatusAsync();
        return new NvGpuEnvironmentInfo
        {
            IsNvidia = shader.IsNvidia,
            GpuName = shader.DriverAdapterName,
            NvApiAvailable = NvShaderInspection.IsAvailable,
            DrsFilesExist = DrsFiles().Any(File.Exists),
            DrsFilesLocked = DrsFiles().Any(p => File.Exists(p) && (File.GetAttributes(p) & FileAttributes.ReadOnly) != 0),
            ShaderCache = shader.IsNvidia ? NvShaderInspection.ReadShaderCacheSizes() : null,
        };
    });

    private static IEnumerable<string> DrsFiles() => new[]
    {
        "nvdrsdb0.bin",
        "nvdrsdb1.bin"
    }.Select(name => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NVIDIA Corporation", "Drs", name));
}
