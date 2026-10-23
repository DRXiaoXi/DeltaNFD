using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;

/// <summary>只读体检复用 Inspection；缓存清理仅由所属栏目的系统维护后端执行。</summary>
internal sealed class BackendShaderService : IShaderService
{
    private readonly BackendCaller _caller = new(BackendModules.System);

    public Task<ShaderStatus> GetStatusAsync() => new DeltaNFD.Inspection.ShaderInspection().GetStatusAsync();

    public async Task<OperationResult> ClearPsoCacheAsync()
    {
        var result = await _caller.InvokeAsync(BackendCommands.SystemShaderClear, new { }, null, CancellationToken.None);
        return BackendResults.Map(result, "清理着色器缓存");
    }

    public Task<ShaderDiagnosis> DiagnoseAsync() => new DeltaNFD.Inspection.ShaderInspection().DiagnoseAsync();
}
