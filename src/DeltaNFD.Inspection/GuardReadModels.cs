using Microsoft.Win32;
using System.Security.AccessControl;
using System.Security.Principal;
using DeltaNFD.Services;

namespace DeltaNFD.Inspection;
internal sealed record GuardRegistryValue(string? Debugger, RegistryValueKind Kind, string? Owner, bool ForeignOwner)
{
    public string? ForeignMarker { get; init; }
}

internal sealed record GuardFileValue(string Path, string Identity, string Dacl);
internal interface IGuardRegistry
{
    GuardRegistryValue Read(string name);
}

internal interface IGuardFiles
{
    GuardFileValue Read(string path);
}

internal interface IGuardStateStore
{
    GuardProtectionState Load();
}

internal sealed class GuardProtectionState
{
    public int Version { get; set; } = 1;
    public List<GuardIfeoRecord> Ifeo { get; set; } = [];
    public List<GuardAclRecord> Acls { get; set; } = [];
    public List<GuardLegacyAclRepair> LegacyAclRepairs { get; set; } = [];
    public RuntimeGuardMode? Mode { get; set; }
    public List<GuardLegacyIfeoRepair> LegacyIfeoRepairs { get; set; } = [];
}

internal sealed class GuardLegacyIfeoRepair
{
    public string Name { get; set; } = "";
    public GuardRegistryValue Before { get; set; } = new(null, RegistryValueKind.None, null, false);
    public bool Completed { get; set; }
}

internal sealed class GuardLegacyAclRepair
{
    public string Path { get; set; } = "";
    public string Identity { get; set; } = "";
    public string Before { get; set; } = "";
    public string After { get; set; } = "";
    public bool Completed { get; set; }
}

internal sealed class GuardIfeoRecord
{
    public string Name { get; set; } = "";
    public string Marker { get; set; } = "";
    public string? Original { get; set; }
    public RegistryValueKind OriginalKind { get; set; }
}

internal sealed class GuardAclRecord
{
    public string Path { get; set; } = "";
    public string Identity { get; set; } = "";
    public string Original { get; set; } = "";
    public string Applied { get; set; } = "";
}

internal sealed record GuardProtectionStatus(int Blocked, int Managed, int External, bool FileDenied, bool PendingAcl)
{
    public RuntimeGuardMode Mode { get; init; }
    public int ScopeManaged { get; init; }
    public int ScopeBlocked { get; init; }
    /// <summary>独立防护工具写入的 taskkill 拦截：普通关闭不删除，只能强制清理。</summary>
    public IReadOnlyList<RuntimeGuardLegacyEntry> LegacyIfeo { get; init; } = [];
    public bool PendingIfeo { get; init; }
    public string Issue { get; init; } = "";
}
/// <summary>
/// 运行库防护：按目标状态收敛，逐项处理、逐项报告，不因单项或记录不一致整体失败。
/// 11 个安装器名上的 taskkill Debugger 一律视为运行库拦截（本工具各版本与独立防护工具写法相同）；
/// 其他程序的 Debugger 普通开关不动，只有强制清理在留存快照后移除。
/// UE4 前置包已在 IFEO 名单内按文件名拦截，不再写文件权限；旧版本留下的 Everyone 拒绝执行在关闭/清理时剥离。
/// </summary>

