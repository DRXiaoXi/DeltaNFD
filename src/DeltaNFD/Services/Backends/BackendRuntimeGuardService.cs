using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;

/// <summary>
/// 前端侧运行库防护服务：状态读取、IFEO/UE4 ACL 开关、VC++ 运行库扫描/重装/卸载全部经运行库后端 EXE 完成。
/// </summary>
internal sealed class BackendRuntimeGuardService : IRuntimeGuardService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private BackendClient? _client;

    private async Task<BackendClient> EnsureClientAsync(CancellationToken cancellationToken)
    {
        if (_client is { IsUsable: true }) return _client;
        if (_client is not null)
        {
            if (_client.RequiresAttention) throw new BackendContractException("上次运行库操作结果未确认，请先核对安装器是否退出及恢复证据，禁止自动启动第二份后端。");
            await _client.DisposeAsync();
            _client = null;
        }
        _client = await BackendClient.StartAsync(BackendModules.Runtime, cancellationToken);
        return _client;
    }

    private async Task<BackendResult> InvokeAsync(string command, object args, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var client = await EnsureClientAsync(cancellationToken);
            return await client.InvokeAsync(command, args, progress, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuntimeGuardStatus> GetStatusAsync()
    {
        var result = await InvokeAsync(BackendCommands.RuntimeStatus, new { }, null, CancellationToken.None);
        return RequireData<RuntimeGuardStatus>(result, "读取运行库防护状态");
    }

    public async Task<OperationResult> EnableAsync()
    {
        var result = await InvokeAsync(BackendCommands.RuntimeGuardEnable, new { }, null, CancellationToken.None);
        return Map(result, "开启运行库拦截");
    }

    public async Task<OperationResult> DisableAsync()
    {
        var result = await InvokeAsync(BackendCommands.RuntimeGuardDisable, new { }, null, CancellationToken.None);
        return Map(result, "关闭运行库拦截");
    }

    public async Task<OperationResult> SelectModeAsync(RuntimeGuardMode mode)
    {
        var result = await InvokeAsync(BackendCommands.RuntimeGuardSelectMode, new RuntimeSelectModeArgs { Mode = mode }, null, CancellationToken.None);
        return Map(result, "保存防护范围");
    }

    public async Task<OperationResult> CleanupLegacyIfeoAsync(IReadOnlyList<RuntimeGuardLegacyEntry> confirmedEntries)
    {
        var result = await InvokeAsync(BackendCommands.RuntimeGuardCleanupLegacy,
            new RuntimeCleanupLegacyArgs { Entries = confirmedEntries.ToList() }, null, CancellationToken.None);
        return Map(result, "清理旧拦截");
    }

    public async Task<OperationResult> RepairLegacyUe4AclAsync(string expectedPath)
    {
        var result = await InvokeAsync(BackendCommands.RuntimeUe4RepairAcl,
            new RuntimeUe4RepairArgs { ExpectedPath = expectedPath }, null, CancellationToken.None);
        return Map(result, "修复 UE4 前置包 ACL");
    }

    public async Task<VcRedistScanReport> ScanVcRedistsAsync()
    {
        var result = await InvokeAsync(BackendCommands.RuntimeRedistScan, new { }, null, CancellationToken.None);
        return RequireData<VcRedistScanReport>(result, "扫描 VC++ 运行库");
    }

    public async Task<OperationResult> RepairVcRedistAsync(IProgress<string>? progress = null,
        RuntimeRepairMode mode = RuntimeRepairMode.FullReinstall, CancellationToken cancellationToken = default, bool strongRepairMode = false)
    {
        var result = await InvokeAsync(BackendCommands.RuntimeRepair, new RuntimeRepairArgs { Mode = mode, StrongRepairMode = strongRepairMode }, progress, cancellationToken);
        return Map(result, "重装运行库");
    }

    public async Task<List<VcRedistEntry>> GetV14EntriesAsync()
    {
        var result = await InvokeAsync(BackendCommands.RuntimeRedistListV14, new { }, null, CancellationToken.None);
        return RequireData<List<VcRedistEntry>>(result, "枚举 v14 运行库");
    }

    public async Task<OperationResult> RepairCompatibilityAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default, bool strongRepairMode = false)
    {
        var result = await InvokeAsync(BackendCommands.RuntimeCompatibilityRepair, new RuntimeCompatibilityRepairArgs { StrongRepairMode = strongRepairMode }, progress, cancellationToken);
        return Map(result, "兼容性修复");
    }

    public async Task<OperationResult> UninstallV14Async(IProgress<string>? progress = null)
    {
        var result = await InvokeAsync(BackendCommands.RuntimeRedistUninstall, new { }, progress, CancellationToken.None);
        return Map(result, "卸载 v14 运行库");
    }

    public async Task<RuntimeUninstallPreview> PreviewUninstallAllAsync()
    {
        var result = await InvokeAsync(BackendCommands.RuntimeRedistPreviewAll, new { }, null, CancellationToken.None);
        return RequireData<RuntimeUninstallPreview>(result, "预览全部 C++ 卸载条目");
    }
    public async Task<OperationResult> UninstallAllAsync(string fingerprint, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var result = await InvokeAsync(BackendCommands.RuntimeRedistUninstallAll, new RuntimeUninstallAllArgs { Fingerprint = fingerprint }, progress, cancellationToken);
        return Map(result, "卸载全部 C++ 运行库");
    }

    private static T RequireData<T>(BackendResult result, string what) where T : notnull
    {
        if (result.Status != BackendResultStatuses.Ok || result.Data is null)
            throw new InvalidOperationException($"{what}失败：{result.Message}");
        return BackendJson.Deserialize<T>(result.Data.Value);
    }

    internal static OperationResult Map(BackendResult result, string what)
    {
        if (result.Data is { } data)
        {
            var operation = BackendJson.Deserialize<OperationResult>(data);
            return new OperationResult
            {
                Success = operation.Success && result.Status is BackendResultStatuses.Ok or BackendResultStatuses.Partial,
                Message = operation.Message, IsSkipped = operation.IsSkipped && result.Status == BackendResultStatuses.Ok,
                OutcomeUnknown = operation.OutcomeUnknown || result.PendingRestore,
                RequiresReboot = operation.RequiresReboot, RequiresLogoff = operation.RequiresLogoff,
                RequiresDisplayConfirm = operation.RequiresDisplayConfirm,
                IsPartial = operation.IsPartial,
            };
        }
        if (result.PendingRestore || result.Code is "backend.timeout" or "backend.cancel-unconfirmed"
            or "backend.transport-unconfirmed" or "backend.disconnected")
            return new OperationResult { Success = false, OutcomeUnknown = true, Message = result.Message };
        return result.Status is BackendResultStatuses.Ok or BackendResultStatuses.Partial
            ? OperationResult.Ok(result.Message)
            : OperationResult.Fail($"{what}失败：{result.Message}");
    }
}
