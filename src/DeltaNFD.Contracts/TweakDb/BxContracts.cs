namespace DeltaNFD.Services.TweakDb;

/// <summary>扩展优化库里单个条目的状态。</summary>
public enum BxState { Off, On, Mixed, Unknown, NotApplicable }

/// <summary>一条被修改前的原始状态备份（注册表 / 服务 / 计划任务 / 引导项 / 事件日志 / PnP 设备）。</summary>
public sealed class BxBackupEntry
{
    /// <summary>Reg / Task / Wevt / Service / Device / Bcd / Power。</summary>
    public string Kind { get; set; } = "Reg";

    /// <summary>唯一标识：注册表=hive|key|valueName；任务/服务=完整路径或名称。</summary>
    public string Id { get; set; } = "";

    /// <summary>注册表=键路径；任务=完整任务路径；服务=服务名；wevt=通道名；PnP 设备=实例 ID。</summary>
    public string KeyPath { get; set; } = "";

    public string ValueName { get; set; } = "";

    /// <summary>注册表：RegistryValueKind 数值（0=原本不存在）；任务/wevt/设备：1=原本启用 0=原本禁用；服务=原 Start 值。</summary>
    public int ValueKind { get; set; }

    public string? Data { get; set; }

    /// <summary>新备份中的 REG_MULTI_SZ 原始数组；旧备份仍从 Data 读取。</summary>
    public string[]? StringData { get; set; }
}

/// <summary>扩展优化库的全局备份文件（optimize_backup.json）。</summary>
public sealed class BxBackupFile
{
    public List<BxBackupEntry> Entries { get; set; } = new();
}

/// <summary>一键恢复结果汇总。</summary>
public sealed record BxRestoreSummary(int Restored, int Failed, IReadOnlyList<string> Errors);

/// <summary>备份仓库的异步读取结果，带明确的来源与可读性（读取失败绝不返回零/无备份）。</summary>
public sealed class BxBackupStatus
{
    /// <summary>备份文件是否成功读取。false = 读失败/损坏，此时 <see cref="Count"/> 与 <see cref="Entries"/> 不可信。</summary>
    public bool Readable { get; set; }

    /// <summary>备份条目数（Readable=false 时无意义）。</summary>
    public int Count { get; set; }

    /// <summary>读取失败原因（Readable=false 时非空）。</summary>
    public string Error { get; set; } = "";

    /// <summary>备份条目（前端展示/核对用；读取失败时为空）。</summary>
    public List<BxBackupEntry> Entries { get; set; } = new();
}

/// <summary>「栏目 Id + 条目名」引用：后端只按自己的内置数据库解析，拒绝伪造 Id 与任意路径。</summary>
public sealed class BxItemRef
{
    public string SectionId { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>单个条目的状态回读。</summary>
public sealed class BxItemStateDto
{
    public string SectionId { get; set; } = "";
    public string Name { get; set; } = "";
    public BxState State { get; set; }
}

/// <summary>批量应用中的单项结果（用于部分失败的逐项明细）。</summary>
public sealed class BxOperationItem
{
    public string Id { get; set; } = "";
    public string Status { get; set; } = "ok";
    public string Message { get; set; } = "";
}

/// <summary>批量应用结果：整体成功/跳过/失败 + 逐项明细（部分失败时不谎报整体成功）。</summary>
public sealed class BxOperationOutcome
{
    public bool Success { get; set; }
    public bool Skipped { get; set; }
    public string Message { get; set; } = "";
    public List<BxOperationItem> Items { get; set; } = new();

    /// <summary>原始操作结果（含 IsSkipped / RequiresReboot 等语义，供前端映射）。</summary>
    public OperationResult? Result { get; set; }

    /// <summary>是否有成员未完成（成功但存在失败/跳过项时也成立，按部分结果上报）。</summary>
    public bool HasPartial => Items.Any(i => i.Status != "ok");
}

/// <summary>
/// 扩展优化库服务合同（前端侧实现走 IPC，后端侧实现调用执行库）。
/// 相较迁移前：同步的 <c>ResolveServiceGroup</c> 改为异步；新增带来源的异步备份读取。
/// </summary>
public interface IBxService
{
    /// <summary>展开服务组内的服务清单（过滤本机不存在的服务）。</summary>
    Task<List<string>> ResolveServiceGroupAsync(BxServiceGroup group);

    /// <summary>读取一个 JSON 条目的当前状态。</summary>
    Task<BxState> GetItemStateAsync(BxItem item);

    /// <summary>批量读取多个 JSON 条目的状态（一次性查询全部计划任务，避免逐条启动 PowerShell 拖慢加载）。</summary>
    Task<Dictionary<BxItem, BxState>> GetItemStatesAsync(IReadOnlyList<BxItem> items);

    /// <summary>读取服务组的当前状态（On=组内全部已禁用）。</summary>
    Task<BxState> GetServiceGroupStateAsync(BxServiceGroup group);

    /// <summary>作废 APPX 安装状态缓存（下次查询重新从系统拉取）。</summary>
    Task InvalidateAppxCacheAsync();

    /// <summary>作废计划任务状态缓存。</summary>
    Task InvalidateTaskCacheAsync();

    /// <summary>应用 JSON 条目（turnOn=true = 应用优化/关闭系统功能）。</summary>
    Task<OperationResult> ApplyItemAsync(BxItem item, bool turnOn);

    /// <summary>应用服务组（turnOn=true = 禁用组内服务）。</summary>
    Task<OperationResult> ApplyServiceGroupAsync(BxServiceGroup group, bool turnOn);

    /// <summary>读取多档位条目的档位信息（单条目多值时提供；非档位条目返回 null）。</summary>
    Task<BxGearInfo?> GetGearInfoAsync(BxItem item);

    /// <summary>应用多档位条目的指定档位（写入该档位的值，写前自动备份）。</summary>
    Task<OperationResult> ApplyGearAsync(BxItem item, int gearIndex);

    /// <summary>一键恢复扩展库备份状态，返回成功与失败明细。</summary>
    Task<BxRestoreSummary> RestoreAllAsync();

    /// <summary>异步读取备份仓库状态（带来源；顺序与数量供「一键恢复」提示与核对）。</summary>
    Task<BxBackupStatus> GetBackupStatusAsync();
}
