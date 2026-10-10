using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;

/// <summary>前端侧着色器维护服务：驱动体检、PSOCache 诊断与清理全部经系统维护后端 EXE 完成。</summary>
internal sealed class BackendShaderService : IShaderService
{
    private readonly BackendCaller _caller = new(BackendModules.System);

    public async Task<ShaderStatus> GetStatusAsync()
    {
        var result = await _caller.InvokeAsync(BackendCommands.SystemShaderStatus, new { }, null, CancellationToken.None);
        return BackendResults.RequireData<ShaderStatus>(result, "读取着色器维护状态");
    }

    public async Task<OperationResult> ClearPsoCacheAsync()
    {
        var result = await _caller.InvokeAsync(BackendCommands.SystemShaderClear, new { }, null, CancellationToken.None);
        return BackendResults.Map(result, "清理着色器缓存");
    }

    public async Task<ShaderDiagnosis> DiagnoseAsync()
    {
        var result = await _caller.InvokeAsync(BackendCommands.SystemShaderDiagnose, new { }, null, CancellationToken.None);
        return BackendResults.RequireData<ShaderDiagnosis>(result, "着色器诊断");
    }
}
