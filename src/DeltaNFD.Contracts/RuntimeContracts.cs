namespace DeltaNFD.Services;

public enum RuntimeGuardMode { BasicV14, Full }
public sealed record RuntimeGuardLegacyEntry(string Name, string Fingerprint);

/// <summary>Visual C++ 安装登记信息，仅用于只读展示。</summary>
public sealed class VcRedistInfo
{
    public required string DisplayName { get; init; }

    public required string Version { get; init; }

    /// <summary>x64 / x86。</summary>
    public required string Architecture { get; init; }
}

/// <summary>运行库保护的整体状态（只读快照，供 UI 展示）。</summary>
public sealed class RuntimeGuardStatus
{
    /// <summary>读取到的 Visual C++ 安装登记，不代表 DLL 完整性。</summary>
    public required List<VcRedistInfo> InstalledRedists { get; init; }

    /// <summary>拦截是否生效（全部 IFEO 键已写入）。</summary>
    public required bool IfeoApplied { get; init; }

    /// <summary>IFEO 已拦截的安装器名数量 / 名单总数。</summary>
    public required int IfeoCount { get; init; }

    public required int IfeoTotal { get; init; }
    public int IfeoManagedCount { get; init; }
    public int IfeoExternalCount { get; init; }
    public RuntimeGuardMode Mode { get; init; }
    public int ScopeManagedCount { get; init; }
    public int ScopeBlockedCount { get; init; }
    public IReadOnlyList<RuntimeGuardLegacyEntry> LegacyIfeo { get; init; } = [];
    public bool IfeoRestorePending { get; init; }
    public bool Ue4RestorePending { get; init; }
    public bool CanRepairLegacyUe4Acl { get; init; }
    /// <summary>读取过程中的非致命问题（状态文件损坏、权限无法读取等），为空表示无。</summary>
    public string GuardIssue { get; init; } = "";

    /// <summary>是否找到了三角洲的 UE4 前置包（运行库载体）。</summary>
    public required bool Ue4PrereqFound { get; init; }

    /// <summary>UE4 前置包路径（未找到为空）。</summary>
    public string Ue4PrereqPath { get; init; } = "";

    /// <summary>UE4 前置包上是否残留旧版本写入的 Everyone 拒绝执行（新版本只用 IFEO，关闭/强制清理时剥离）。</summary>
    public required bool Ue4PrereqDenied { get; init; }
}

/// <summary>安装器防护、历史事务核对与 14.x 全量重装（卸载全部 14.x 后安装内置 14.42 基线）。</summary>
public interface IRuntimeGuardService
{
    Task<RuntimeFileUseScan> ScanFileUseAsync(CancellationToken token, RuntimeFileUseScope scope = RuntimeFileUseScope.Vc14) => throw new NotSupportedException();
    Task<RuntimeFileUseCloseResult> CloseFileUseAsync(RuntimeFileUseCloseArgs args, CancellationToken token) => throw new NotSupportedException();
    Task<RuntimeHighFpsPreview> PreviewHighFpsAsync(bool strongMode) => throw new NotSupportedException();
    Task<OperationResult> RepairHighFpsAsync(string fingerprint, bool strongMode, IProgress<string>? progress, CancellationToken token) => throw new NotSupportedException();
    Task<Vc14Scan> Scan14Async() => throw new NotSupportedException();
    Task<Vc14ReinstallPreview> Preview14RepairAsync(Vc14RepairMode mode = Vc14RepairMode.Full, bool allowTemporaryInstaller = false) => throw new NotSupportedException();
    Task<OperationResult> Repair14Async(string fingerprint, IProgress<string>? progress, CancellationToken token, Vc14RepairMode mode = Vc14RepairMode.Full, bool allowTemporaryInstaller = false) => throw new NotSupportedException();
    Task<OperationResult> RestoreTemporaryInstallerAsync() => throw new NotSupportedException();
    Task<RuntimeGuardStatus> GetStatusAsync();
    Task<OperationResult> EnableAsync();
    Task<OperationResult> DisableAsync();
    Task<OperationResult> SelectModeAsync(RuntimeGuardMode mode);
    Task<OperationResult> CleanupLegacyIfeoAsync(IReadOnlyList<RuntimeGuardLegacyEntry> confirmedEntries);
    Task<OperationResult> RepairLegacyUe4AclAsync(string expectedPath);
}
