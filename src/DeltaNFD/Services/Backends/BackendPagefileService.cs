using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;

/// <summary>页面文件查询使用前端只读缓存；设置与恢复经所属栏目的系统维护后端完成。</summary>
internal sealed class BackendPagefileService : IPagefileService
{
    private readonly BackendCaller _caller = new(BackendModules.System);
    private bool? _hasOriginalBackup;

    public bool HasOriginalBackup => _hasOriginalBackup ?? false;

    public async Task<PagefileInfo> GetInfoAsync() { var info = await SystemInspectionCache.Pagefile(); _hasOriginalBackup = new DeltaNFD.Inspection.PagefileInspection().HasOriginalBackup; return info; }

    public async Task<OperationResult> SetCustomAsync(int minMb, int maxMb, char driveLetter)
    {
        var result = await _caller.InvokeAsync(BackendCommands.SystemPagefileSet,
            new PagefileSetArgs { MinMb = minMb, MaxMb = maxMb, DriveLetter = driveLetter.ToString() }, null, CancellationToken.None);
        return BackendResults.Map(result, "设置虚拟内存");
    }

    public async Task<OperationResult> RestoreAutomaticAsync()
    {
        var result = await _caller.InvokeAsync(BackendCommands.SystemPagefileRestoreAuto, new { }, null, CancellationToken.None);
        return BackendResults.Map(result, "恢复系统托管的虚拟内存");
    }

    public async Task<OperationResult> RestoreOriginalAsync()
    {
        var result = await _caller.InvokeAsync(BackendCommands.SystemPagefileRestoreOriginal, new { }, null, CancellationToken.None);
        return BackendResults.Map(result, "恢复原虚拟内存配置");
    }
}
