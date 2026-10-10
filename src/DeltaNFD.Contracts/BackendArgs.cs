using DeltaNFD.Services.TweakDb;

namespace DeltaNFD.Backends;

public sealed class RuntimeRepairArgs
{
    public Services.RuntimeRepairMode Mode { get; set; } = Services.RuntimeRepairMode.FullReinstall;
    public bool StrongRepairMode { get; set; }
}

public sealed class RuntimeCompatibilityRepairArgs
{
    public bool StrongRepairMode { get; set; }
}

/// <summary>官方后端命令参数 DTO（前端与后端共用，避免匿名对象的字段漂移）。</summary>
public sealed class GpuSpoofApplyArgs
{
    public string RegistryPath { get; set; } = "";

    public string FakeName { get; set; } = "";
}

public sealed class GpuSpoofPathArgs
{
    public string RegistryPath { get; set; } = "";
}

/// <summary>显卡设备重载参数：PnP 设备实例 ID（由注册表路径解析后传入，执行在显卡后端）。</summary>
public sealed class GpuSpoofReloadArgs
{
    public string InstanceId { get; set; } = "";
}

public sealed class NvDriverApplyArgs
{
    public List<Services.NvSettingChangeDto> Changes { get; set; } = [];
}

public sealed class NvDriverRestoreArgs
{
    public List<uint> Ids { get; set; } = [];
}

public sealed class NvDriverReadOnlyArgs
{
    public bool ReadOnly { get; set; }
}

public sealed class RuntimeSelectModeArgs
{
    public Services.RuntimeGuardMode Mode { get; set; }
}

public sealed class RuntimeCleanupLegacyArgs
{
    public List<Services.RuntimeGuardLegacyEntry> Entries { get; set; } = [];
}

public sealed class RuntimeUe4RepairArgs
{
    public string ExpectedPath { get; set; } = "";
}

public sealed class PowerSetSchemeArgs
{
    public string SchemeGuid { get; set; } = "";
}

public sealed class PowerHibernateSetArgs
{
    public bool Enable { get; set; }
}

public sealed class PowerHeteroSetArgs
{
    public string SettingGuid { get; set; } = "";
    public int AcValue { get; set; }
    public int DcValue { get; set; }
}

public sealed class PagefileSetArgs
{
    public int MinMb { get; set; }
    public int MaxMb { get; set; }
    public string DriveLetter { get; set; } = "C";
}

/// <summary>清理操作结果（释放 MB + 跳过项）。</summary>
public sealed class CleanupResultDto
{
    public double FreedMb { get; set; }
    public List<string> Skipped { get; set; } = [];
}

// ---------------- 扩展优化库 / Defender / 内存 / 一键优化（T3） ----------------

/// <summary>批量条目状态读取：只传「栏目 Id + 条目名」，后端按自己的数据库解析。</summary>
public sealed class BxStatusArgs
{
    public List<BxItemRef> Items { get; set; } = [];
}

/// <summary>单条目（含栏目 Id）参数。</summary>
public sealed class BxItemArgs
{
    public string SectionId { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>单条目应用参数。</summary>
public sealed class BxApplyArgs
{
    public string SectionId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool TurnOn { get; set; }
}

/// <summary>多档位条目切换参数。</summary>
public sealed class BxGearApplyArgs
{
    public string SectionId { get; set; } = "";
    public string Name { get; set; } = "";
    public int GearIndex { get; set; }
}

/// <summary>服务组参数。</summary>
public sealed class BxServiceGroupArgs
{
    public string GroupId { get; set; } = "";
}

/// <summary>服务组应用参数。</summary>
public sealed class BxServiceGroupApplyArgs
{
    public string GroupId { get; set; } = "";
    public bool TurnOn { get; set; }
}

/// <summary>扩展库缓存失效参数（what = appx / task / all）。</summary>
public sealed class BxCacheInvalidateArgs
{
    public string What { get; set; } = "";
}

/// <summary>Defender 预检参数（名称 + 操作文本，仅用于安全类关键词匹配以决定是否引导）。</summary>
public sealed class DefenderPreflightArgs
{
    public List<Services.DefenderBatchItem> Items { get; set; } = [];
}

/// <summary>一键优化（后端可执行部分）参数。</summary>
public sealed class OptimizeRunArgs
{
    public bool UltimatePower { get; set; }
    public bool DisableMouseAcceleration { get; set; }
    public bool PurgeStandbyList { get; set; }
}
