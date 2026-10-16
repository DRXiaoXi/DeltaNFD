namespace DeltaNFD.Services;

/// <summary>一个可清理的垃圾文件目录（扫描结果）。</summary>
public sealed class CleanupTarget
{
    /// <summary>显示名（如「用户临时文件」「DirectX 着色器缓存」）。</summary>
    public required string Name { get; init; }

    /// <summary>实际扫描的目录路径（可多个，分号分隔）。</summary>
    public required string Path { get; init; }

    /// <summary>扫描到的可清理体积（MB）。</summary>
    public double SizeMb { get; set; }

    /// <summary>说明（清理影响）。</summary>
    public string Note { get; init; } = "";
}

/// <summary>
/// 系统垃圾清理服务：固定的安全清理清单（扩展优化库 CleanUp.json 精选），
/// 先扫描体积，再按目录清空内容（保留目录本身与正在使用的文件）。
/// </summary>
public interface ICleanupService
{
    /// <summary>扫描清理清单各项目前的体积（只读）。</summary>
    Task<List<CleanupTarget>> ScanAsync();

    /// <summary>执行清理，返回释放的 MB 与被跳过的路径说明。</summary>
    Task<(double FreedMb, List<string> Skipped)> CleanAsync(IProgress<string>? progress = null);

    /// <summary>
    /// 仅清理系统级着色器缓存（LocalAppData 下 D3DSCache / NVIDIA DXCache / AMD DxCache），
    /// 不含游戏目录内的 PSOCache。被占用文件自动跳过。
    /// </summary>
    Task<(double FreedMb, List<string> Skipped)> CleanShaderCachesAsync(IProgress<string>? progress = null);
}
