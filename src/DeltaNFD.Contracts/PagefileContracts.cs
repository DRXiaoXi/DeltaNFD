namespace DeltaNFD.Services;

/// <summary>当前虚拟内存（页面文件）状态。</summary>
public sealed class PagefileInfo
{
    /// <summary>系统是否处于「自动管理所有驱动器的分页文件大小」。</summary>
    public bool AutomaticManaged { get; init; }

    /// <summary>是否配置了页面文件。</summary>
    public bool HasPagefile { get; init; }

    /// <summary>原始 PagingFiles 配置（每驱动一项，形如 "C:\pagefile.sys 16384 32768"）。</summary>
    public string[] RawEntries { get; init; } = Array.Empty<string>();

    /// <summary>给 UI 的状态摘要。</summary>
    public string Summary { get; init; } = "";
}

/// <summary>
/// 虚拟内存（页面文件）设置服务（自 C4 起，执行实现仅在系统维护后端；前端经 IPC 调用）。
/// </summary>
public interface IPagefileService
{
    bool HasOriginalBackup { get; }

    /// <summary>读取当前虚拟内存状态（纯只读）。</summary>
    Task<PagefileInfo> GetInfoAsync();

    /// <summary>
    /// 设置固定大小页面文件（MB），写入指定盘符。约束：最小/最大均不得低于 16 GB，最大不小于最小。
    /// 写入后需重启电脑生效；原配置自动备份，可用 <see cref="RestoreAutomaticAsync"/> 恢复系统托管。
    /// </summary>
    Task<OperationResult> SetCustomAsync(int minMb, int maxMb, char driveLetter);

    /// <summary>恢复为系统托管（自动管理所有驱动器的分页文件大小）。</summary>
    Task<OperationResult> RestoreAutomaticAsync();

    /// <summary>按首次修改前的备份恢复原页面文件配置。</summary>
    Task<OperationResult> RestoreOriginalAsync();
}

/// <summary>虚拟内存状态查询的线格式结果（含是否有原始备份）。</summary>
public sealed class PagefileStatusDto
{
    public PagefileInfo Info { get; set; } = new();

    public bool HasOriginalBackup { get; set; }
}
