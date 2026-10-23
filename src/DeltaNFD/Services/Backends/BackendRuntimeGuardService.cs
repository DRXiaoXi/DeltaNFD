using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;
/// <summary>
/// 前端运行库代理：防护、14.x 兼容性检测和定向修复，旧 AIO 命令不恢复。
/// </summary>
internal sealed class BackendRuntimeGuardService : IRuntimeGuardService
{
    private readonly PageBackendLease? _owner = PageBackendSessions.ConstructionOwner;
    private readonly SemaphoreSlim _gate = PageBackendSessions.ModuleGate(BackendModules.Runtime);
    private readonly BackendCaller _caller = new(BackendModules.Runtime);
    private BackendClient? _client { get => _caller.SharedClient; set => _caller.SharedClient = value; }

    private async Task<BackendClient> EnsureClientAsync(CancellationToken cancellationToken)
    {
        if (_client is { IsUsable: true })
            return _client;
        if (_client is not null)
        {
            if (_client.RequiresAttention)
                throw new BackendContractException("上次运行库操作结果未确认，请先核对安装器是否退出及恢复证据，禁止自动启动第二份后端。");
            await _client.QuiesceAsync(BackendSessions.StopTimeoutFor(BackendModules.Runtime));
            _client = null;
        }

        _client = await BackendClient.StartAsync(BackendModules.Runtime, cancellationToken, _owner);
        return _client;
    }

    private async Task<BackendResult> InvokeAsync(string command, object args, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await PageBackendSessions.WaitGateAsync(_gate, _owner, cancellationToken, BackendModules.Runtime);
        try
        {
            var client = await EnsureClientAsync(cancellationToken);
            return await client.InvokeAsync(command, args, progress, cancellationToken);
        }
        finally
        {
            try
            {
                await PageBackendSessions.FinishUnownedAsync(_client, _owner);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public Task<RuntimeGuardStatus> GetStatusAsync() => new DeltaNFD.Inspection.RuntimeGuardInspection().GetStatusAsync();
    public Task<Vc14Scan> Scan14Async() => DeltaNFD.Inspection.InspectionScheduler.Run(() => Vc14CatalogInspection.Scan());
    public async Task<RuntimeFileUseScan> ScanFileUseAsync(CancellationToken token, RuntimeFileUseScope scope = RuntimeFileUseScope.Vc14) => RequireData<RuntimeFileUseScan>(await InvokeAsync(BackendCommands.RuntimeFileUseScan, new RuntimeFileUseScanArgs { Scope = scope }, null, token), "运行库文件占用检测");
    public async Task<RuntimeFileUseCloseResult> CloseFileUseAsync(RuntimeFileUseCloseArgs args, CancellationToken token) => RequireData<RuntimeFileUseCloseResult>(await InvokeAsync(BackendCommands.RuntimeFileUseClose, args, null, token), "处理运行库文件占用");
    public async Task<Vc14ReinstallPreview> Preview14RepairAsync(Vc14RepairMode mode = Vc14RepairMode.Full, bool allowTemporaryInstaller = false) => RequireData<Vc14ReinstallPreview>(await InvokeAsync(BackendCommands.Vc14Preview, new Vc14RepairArgs { Mode = mode, AllowTemporaryInstaller = allowTemporaryInstaller }, null, CancellationToken.None), "14.x 阶段预览");
    public async Task<OperationResult> Repair14Async(string fingerprint, IProgress<string>? progress, CancellationToken token, Vc14RepairMode mode = Vc14RepairMode.Full, bool allowTemporaryInstaller = false) => Map(await InvokeAsync(BackendCommands.Vc14Repair, new Vc14RepairArgs { Fingerprint = fingerprint, Mode = mode, AllowTemporaryInstaller = allowTemporaryInstaller }, progress, token), "14.x 阶段修复");
    public async Task<Vc14ForceReplacePreview> PreviewForceReplace14Async(CancellationToken token) => RequireData<Vc14ForceReplacePreview>(await InvokeAsync(BackendCommands.Vc14ForceReplacePreview, new { }, null, token), "强制替换 14.x 预览");
    public async Task<OperationResult> ForceReplace14Async(string fingerprint, IProgress<string>? progress, CancellationToken token) => Map(await InvokeAsync(BackendCommands.Vc14ForceReplace, new Vc14ForceReplaceArgs { Fingerprint = fingerprint }, progress, token), "强制替换 14.x");
    public async Task<OperationResult> RestoreTemporaryInstallerAsync()
    {
        if (_owner is { IsAvailable: false })
            return OperationResult.Fail("栏目已暂停，未启动新的恢复操作。");
        // Reconcile control is usable after an uncertain repair; native recovery still checks MSI/children.
        var caller = new BackendCaller(BackendModules.Runtime);
        try
        {
            var result = await caller.InvokeReconcileAsync(BackendCommands.RuntimeRecover, new RecoverArgs { Scope = "safe-installer" }, null, CancellationToken.None);
            var outcome = RequireData<RecoverOutcome>(result, "恢复临时 Installer 设置");
            return new OperationResult
            {
                Success = outcome.Success && outcome.Verified,
                Message = outcome.Message,
                OutcomeUnknown = !outcome.Verified
            };
        }
        finally
        {
            if (!await caller.ResetAsync())
                throw new InvalidOperationException("恢复后端退出未确认，证据保留。");
        }
    }

    public async Task<RuntimeHighFpsPreview> PreviewHighFpsAsync(bool strongMode) => RequireData<RuntimeHighFpsPreview>(await InvokeAsync(BackendCommands.RuntimeHighFpsPreview, new RuntimeHighFpsArgs { StrongMode = strongMode }, null, CancellationToken.None), "全量修复预览");
    public async Task<OperationResult> RepairHighFpsAsync(string fingerprint, bool strongMode, IProgress<string>? progress, CancellationToken token) => Map(await InvokeAsync(BackendCommands.RuntimeHighFpsRepair, new RuntimeHighFpsArgs { StrongMode = strongMode, Fingerprint = fingerprint }, progress, token), "全量修复");
    public async Task<RuntimeSafeUninstallPreview> PreviewSafeUninstallAsync(CancellationToken token) => RequireData<RuntimeSafeUninstallPreview>(await InvokeAsync(BackendCommands.RuntimeSafeUninstallPreview, new { }, null, token), "安全模式 C++ 全量卸载预览");
    public async Task<OperationResult> SafeUninstallAllCppAsync(string fingerprint, bool allowTemporaryInstaller, IProgress<string>? progress, CancellationToken token) => Map(await InvokeAsync(BackendCommands.RuntimeSafeUninstall, new RuntimeSafeUninstallArgs { Fingerprint = fingerprint, AllowTemporaryInstaller = allowTemporaryInstaller }, progress, token), "安全模式 C++ 全量卸载");
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
        var result = await InvokeAsync(BackendCommands.RuntimeGuardCleanupLegacy, new RuntimeCleanupLegacyArgs { Entries = confirmedEntries.ToList() }, null, CancellationToken.None);
        return Map(result, "清理旧拦截");
    }

    public async Task<OperationResult> RepairLegacyUe4AclAsync(string expectedPath)
    {
        var result = await InvokeAsync(BackendCommands.RuntimeUe4RepairAcl, new RuntimeUe4RepairArgs { ExpectedPath = expectedPath }, null, CancellationToken.None);
        return Map(result, "修复 UE4 前置包 ACL");
    }

    private static T RequireData<T>(BackendResult result, string what)
        where T : notnull
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
                Message = operation.Message,
                IsSkipped = operation.IsSkipped && result.Status == BackendResultStatuses.Ok,
                OutcomeUnknown = operation.OutcomeUnknown || result.PendingRestore,
                RequiresReboot = operation.RequiresReboot,
                RequiresLogoff = operation.RequiresLogoff,
                RequiresDisplayConfirm = operation.RequiresDisplayConfirm,
                IsPartial = operation.IsPartial,
                DiagnosticsDirectory = operation.DiagnosticsDirectory,
            };
        }

        if (result.PendingRestore || result.Code is "backend.timeout" or "backend.cancel-unconfirmed" or "backend.transport-unconfirmed" or "backend.disconnected")
            return new OperationResult
            {
                Success = false,
                OutcomeUnknown = true,
                Message = result.Message
            };
        return result.Status is BackendResultStatuses.Ok or BackendResultStatuses.Partial ? OperationResult.Ok(result.Message) : OperationResult.Fail($"{what}失败：{result.Message}");
    }
}
