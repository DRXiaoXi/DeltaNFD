namespace DeltaNFD.Services;

internal static class GpuShaderGuidance
{
    // 仅为本次回执生成引导，不写设置或任何提醒文件。
    internal static string AfterVerifiedChange(string before, string after) =>
        string.Equals(before, after, StringComparison.Ordinal) ? "" :
        " 请在型号生效后前往「着色器维护」，清除旧缓存并完成完整预热。";
}
