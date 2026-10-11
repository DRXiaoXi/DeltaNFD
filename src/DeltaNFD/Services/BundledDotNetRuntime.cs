namespace DeltaNFD.Services;

/// <summary>构建时从微软正式版元数据固定的离线安装器身份；不信任安装目录中可变的清单。</summary>
internal static class BundledDotNetRuntime
{
    internal const string Version = "8.0.31";
    internal const string FileName = "dotnet-runtime-8.0.31-win-x64.exe";
    internal const string Url = "https://builds.dotnet.microsoft.com/dotnet/Runtime/8.0.31/dotnet-runtime-8.0.31-win-x64.exe";
    internal const string Sha512 = "2DC2F346A4BCB53AB15AB6AB84348C02EB31B0A3D4E8E06AAC73B4C89F1B001AF01943ED4C5735FA5585F1DD731A942D2034577DFE937F4454367FF2D6EDF929";
}
