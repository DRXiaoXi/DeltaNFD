using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace DeltaNFD.Services.TweakDb;

// ---------------- 全局备份（一键恢复） ----------------

public sealed class BxBackupEntry
{
    /// <summary>Reg / Task / Wevt / Service / Device。</summary>
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

public sealed class BxBackupFile
{
    public List<BxBackupEntry> Entries { get; set; } = new();
}

/// <summary>
/// 扩展优化库 子栏目的全局备份仓库（%APPDATA%\Delta NFD\optimize_backup.json）。
/// 首次改动某个值前记录原始状态；「一键恢复」把所有记录过的值还原为最初状态。
/// 同一值只备份一次（先到先得），保证多次开关后恢复的仍是用户最原始的状态。
/// </summary>
public static class BxBackupStore
{
    private static readonly object Gate = new();
    public static readonly string DefaultPath = Path.Combine(AppDataPaths.Root, "optimize_backup.json");

    public static List<BxBackupEntry> ReadAll()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(DefaultPath))
                {
                    return JsonSerializer.Deserialize<BxBackupFile>(File.ReadAllText(DefaultPath))?.Entries ?? new();
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"优化备份读取失败：{DefaultPath}；{ex.Message}");
            }

            return new();
        }
    }

    /// <summary>按 Id 首次备份（已存在则忽略），返回是否新写入。</summary>
    public static bool SaveOnce(BxBackupEntry entry)
    {
        lock (Gate)
        {
            var file = LoadUnsafe();
            if (file.Entries.Any(e => e.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            file.Entries.Add(entry);
            Persist(file);
            return true;
        }
    }

    public static void SaveMany(IEnumerable<BxBackupEntry> entries)
    {
        lock (Gate)
        {
            var file = LoadUnsafe();
            var added = false;
            foreach (var entry in entries)
            {
                if (file.Entries.Any(e => e.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                file.Entries.Add(entry);
                added = true;
            }

            if (added)
            {
                Persist(file);
            }
        }
    }

    public static void RemoveIds(IEnumerable<string> ids)
    {
        lock (Gate)
        {
            var file = LoadUnsafe();
            var done = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (file.Entries.RemoveAll(e => done.Contains(e.Id)) > 0)
            {
                Persist(file);
            }
        }
    }

    private static BxBackupFile LoadUnsafe()
    {
        try
        {
            if (File.Exists(DefaultPath))
            {
                return JsonSerializer.Deserialize<BxBackupFile>(File.ReadAllText(DefaultPath)) ?? new();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"优化备份加载失败：{DefaultPath}；{ex.Message}");
        }

        return new();
    }

    private static void Persist(BxBackupFile file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DefaultPath)!);
            var temp = DefaultPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file));
            File.Move(temp, DefaultPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error($"优化备份写入失败：{DefaultPath}；条目数={file.Entries.Count}", ex);
        }
    }
}

// ---------------- 条目状态 ----------------

public enum BxState { Off, On, Mixed, Unknown, NotApplicable }

internal enum BxTaskState { Enabled, Disabled, Missing, Unknown }

public sealed record BxRestoreSummary(int Restored, int Failed, IReadOnlyList<string> Errors);

// ---------------- 服务接口与实现 ----------------

public interface IBxService
{
    /// <summary>展开服务组内的服务清单（过滤本机不存在的服务）。</summary>
    List<string> ResolveServiceGroup(BxServiceGroup group);

    /// <summary>读取一个 JSON 条目的当前状态。</summary>
    Task<BxState> GetItemStateAsync(BxItem item);

    /// <summary>作废 APPX 安装状态缓存（下次查询重新从系统拉取，保证每次进页读到最新状态）。</summary>
    void InvalidateAppxCache();

    void InvalidateTaskCache();

    /// <summary>批量读取多个 JSON 条目的状态（一次性查询全部计划任务，避免逐条启动 PowerShell 拖慢加载）。</summary>
    Task<Dictionary<BxItem, BxState>> GetItemStatesAsync(IReadOnlyList<BxItem> items);

    /// <summary>读取服务组的当前状态（On=组内全部已禁用）。</summary>
    Task<BxState> GetServiceGroupStateAsync(BxServiceGroup group);

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
}

public sealed class BxService : IBxService
{
    private const string NotElevated = "请以管理员身份运行本程序后再使用系统优化功能。";

    private sealed record PnpDeviceState(string InstanceId, bool? Enabled);

    // ---------------- 服务组展开 ----------------

    public List<string> ResolveServiceGroup(BxServiceGroup group)
    {
        var names = group.UseBlockedDb
            ? BxCatalog.AllServices
                .Where(s => s.IsBlocked || s.IsBlocked11)
                .Select(s => s.ServiceName)
                .ToList()
            : group.Services.ToList();

        return ExpandServicePatterns(names).Where(ServiceExists).ToList();
    }

    private static bool ServiceExists(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
        return key is not null;
    }

    private static List<string> ExpandServicePatterns(IEnumerable<string> names)
    {
        var result = new List<string>();
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (root is null)
        {
            return result;
        }

        foreach (var name in names)
        {
            if (name.EndsWith('*'))
            {
                var prefix = name.TrimEnd('*');
                result.AddRange(root.GetSubKeyNames().Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
            }
            else
            {
                result.Add(name);
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------------- 状态读取 ----------------

    public async Task<BxState> GetItemStateAsync(BxItem item) =>
        (await GetItemStatesAsync([item]))[item];

    public Task<Dictionary<BxItem, BxState>> GetItemStatesAsync(IReadOnlyList<BxItem> items) => Task.Run(() =>
    {
        // 批量查询：一次性拉取系统全部计划任务状态，再在内存里逐条评估。
        // 任务状态快照带 2 分钟 TTL 缓存（全库扫描/多栏目连续加载只起一次 PowerShell；
        // ApplyTasks 写操作与显式失效会立即清空）。
        var taskStates = _taskStateCache;
        if (items.Any(i => i.Tweaks.Any(t => t.TweakType == "TASK")) &&
            (!_taskStateCacheValid || DateTime.Now - _taskStateCacheTime > TimeSpan.FromMinutes(2)))
        {
            taskStates = QueryAllTaskStates();
            _taskStateCache = taskStates;
            _taskStateCacheTime = DateTime.Now;
            _taskStateCacheValid = true;
        }

        var result = new Dictionary<BxItem, BxState>();
        foreach (var item in items)
        {
            BxTaskState Lookup(string path)
            {
                var normalized = "\\" + path.TrimStart('\\');
                if (taskStates is null) return BxTaskState.Unknown;
                return taskStates.TryGetValue(normalized, out var enabled)
                    ? enabled ? BxTaskState.Enabled : BxTaskState.Disabled
                    : BxTaskState.Missing;
            }

            var diagnostics = new List<string>();
            var state = EvaluateItem(item, Lookup, diagnostics);
            result[item] = state;
            if (state is BxState.Mixed or BxState.Unknown or BxState.NotApplicable)
            {
                var evidence = diagnostics.Count > 0 ? string.Join("；", diagnostics) : "没有得到足以细分原因的状态证据";
                Log.Info($"扩展库状态诊断：{item.Name} → {state}；依据：{evidence}");
            }
        }

        return result;
    });

    private static Dictionary<string, bool>? _taskStateCache;
    private static DateTime _taskStateCacheTime;
    private static bool _taskStateCacheValid;

    public void InvalidateTaskCache()
    {
        _taskStateCache = null;
        _taskStateCacheTime = default;
        _taskStateCacheValid = false;
    }

    private BxState EvaluateItem(BxItem item, Func<string, BxTaskState> taskState, List<string>? diagnostics = null)
    {
        diagnostics ??= [];
        var regOps = item.Tweaks.Where(t => IsRegType(t.TweakType)).ToList();
        var taskOps = item.Tweaks.Where(t => t.TweakType == "TASK").ToList();
        var wevtOps = item.Tweaks.Where(t => t.TweakType == "WEVTUTIL").ToList();
        var svcOps = item.Tweaks.Where(t => t.TweakType == "SVC").ToList();
        var deviceOps = item.Tweaks.Where(t => t.TweakType == "DEVICE").ToList();

        var hasOp = false;
        var anyOn = false;
        var anyOff = false;
        var anyUnknown = false;
        var anyMissing = false;

        foreach (var op in regOps)
        {
            hasOp = true;
            var (on, off) = MatchReg(op);
            if (on is null && off is null)
            {
                diagnostics.Add($"{op.TweakType} {op.Path}\\{op.Key} 缺少可比较的 ON/DEFAULT 值");
                continue;
            }

            if (on == true)
            {
                anyOn = true;
            }
            else if (off == true)
            {
                anyOff = true;
            }
            else
            {
                // 当前值既不是优化值也不是预期的默认值（典型：键/值不存在 = 系统默认未配置，
                // 或存在第三方写入的其他值）→ 按“未禁用”处理，不落“未知”
                anyOff = true;
                diagnostics.Add($"{op.TweakType} {op.Path}\\{op.Key} 当前值不匹配优化值或默认值，按未优化处理");
            }
        }

        if (item.Name == "USBSelectiveSuspendOff")
        {
            hasOp = true;
            var powerDisabled = QueryUsbSelectiveSuspendDisabled();
            if (powerDisabled == true) anyOn = true;
            else if (powerDisabled == false) anyOff = true;
            else { anyUnknown = true; diagnostics.Add("USB 选择性暂停状态查询失败"); }
        }

        foreach (var op in taskOps)
        {
            hasOp = true;
            var state = taskState(op.Path);
            if (state == BxTaskState.Enabled)
            {
                anyOff = true;
            }
            else if (state == BxTaskState.Disabled)
            {
                anyOn = true;
            }
            else if (state == BxTaskState.Missing) { anyMissing = true; diagnostics.Add($"计划任务不存在：\\{op.Path.TrimStart('\\')}"); }
            else { anyUnknown = true; diagnostics.Add($"计划任务状态查询失败：\\{op.Path.TrimStart('\\')}（批量任务快照不可用）"); }
        }

        if (wevtOps.Count > 0)
        {
            hasOp = true;
            var enabled = QueryWevtEnabled(wevtOps[0].Path);
            if (enabled == true)
            {
                // 通道仍在启用 = 优化未应用（应用 = 关闭通道）
                anyOff = true;
            }
            else if (enabled == false)
            {
                anyOn = true;
            }
            else { anyUnknown = true; diagnostics.Add($"事件日志通道状态查询失败或通道不存在：{wevtOps[0].Path}"); }
        }

        if (svcOps.Count > 0)
        {
            hasOp = true;
            foreach (var op in svcOps)
            {
                var onV = PickAppliedEntry(op);
                var offV = PickDefaultEntry(op);
                if (onV?.Value is null || offV?.Value is null)
                {
                    diagnostics.Add($"服务 {op.Path} 缺少优化值/默认值定义");
                    continue;
                }

                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + op.Path);
                if (key?.GetValue("Start") is not int start)
                {
                    diagnostics.Add($"无法读取服务 {op.Path} 的 Start 注册表值");
                    continue;
                }

                if (start.ToString() == onV.Value)
                {
                    anyOn = true;
                }
                else if (start.ToString() == offV.Value)
                {
                    anyOff = true;
                }
            }
        }

        foreach (var op in deviceOps)
        {
            hasOp = true;
            var states = QueryPnpDeviceStates(op.Path);
            if (states is null || states.Count == 0)
            {
                anyUnknown = true;
                diagnostics.Add(states is null ? $"PnP 设备查询失败：{op.Path}" : $"未找到 PnP 设备：{op.Path}");
                continue;
            }

            if (!TryGetDeviceEnabledTarget(op, turnOn: true, out var appliedEnabled) ||
                !TryGetDeviceEnabledTarget(op, turnOn: false, out var defaultEnabled))
            {
                anyUnknown = true;
                diagnostics.Add($"PnP 设备操作定义无效：{op.Path}（仅支持 Enable/Disable）");
                continue;
            }

            foreach (var state in states)
            {
                if (state.Enabled is null)
                {
                    anyUnknown = true;
                    diagnostics.Add($"无法读取设备启用状态：{state.InstanceId}");
                }
                else if (state.Enabled.Value == appliedEnabled)
                {
                    anyOn = true;
                }
                else if (state.Enabled.Value == defaultEnabled)
                {
                    anyOff = true;
                }
                else
                {
                    anyUnknown = true;
                    diagnostics.Add($"设备状态不匹配启用/禁用目标：{state.InstanceId}");
                }
            }
        }

        // CMD 类型只参与“是否有操作”的判定，不参与状态推断（状态由其余操作给出）
        if (item.Tweaks.Any(t => t.TweakType == "CMD"))
        {
            hasOp = true;
        }

        // REGENUM：遍历父键下的数字子键（如网卡类实例 0000/0001…）统计优化/默认占比
        var regEnumOps = item.Tweaks.Where(t => t.TweakType == "REGENUM").ToList();
        if (regEnumOps.Count > 0)
        {
            hasOp = true;
            foreach (var op in regEnumOps)
            {
                var appliedEntry = PickAppliedEntry(op);
                var defaultEntry = PickDefaultEntry(op);
                if (appliedEntry?.Value is null || defaultEntry?.Value is null)
                {
                    continue;
                }

                var (appliedCount, totalCount) = CountRegEnumInstances(op, appliedEntry);
                if (totalCount == 0)
                {
                    // 没有可枚举的实例 = 无可优化对象，按“未禁用”处理
                    anyOff = true;
                    diagnostics.Add($"REGENUM {op.Path} 下没有数字实例子键，按无可优化实例处理");
                }
                else if (appliedCount == totalCount)
                {
                    anyOn = true;
                }
                else if (appliedCount == 0)
                {
                    anyOff = true;
                }
                else
                {
                    anyOn = true;
                    anyOff = true; // 部分实例已优化
                }
            }
        }

        if (item.Name.Equals("HPETName", StringComparison.OrdinalIgnoreCase))
        {
            // HPET 状态同时取启动配置与 PnP 设备状态；部分写入显示为 Mixed，避免误报完整生效。
            var hpetState = QueryHpetBcdState();
            diagnostics.Add($"HPET 启动配置与设备状态合并判定：{hpetState}");
            switch (hpetState)
            {
                case BxState.On:
                    anyOn = true;
                    break;
                case BxState.Off:
                    anyOff = true;
                    break;
                case BxState.Mixed:
                    anyOn = true;
                    anyOff = true;
                    break;
                default:
                    anyUnknown = true;
                    break;
            }
        }
        // 纯 CMD/无其他可评估操作时：走条目级只读状态查询（避免恒为“状态未知”）
        else if (!anyOn && !anyOff)
        {
            var custom = QueryCmdItemState(item.Name);
            if (custom.HasValue)
            {
                hasOp = true;
                if (custom.Value)
                {
                    anyOn = true;
                }
                else
                {
                    anyOff = true;
                }
            }
        }

        // APPX 类型：应用包存在 = 未应用（未移除），不存在 = 已应用（已移除）
        var appxOps = item.Tweaks.Where(t => t.TweakType == "APPX").ToList();
        if (appxOps.Count > 0)
        {
            hasOp = true;
            foreach (var op in appxOps)
            {
                var installed = IsAppxInstalled(op.Path);
                if (installed == true)
                {
                    anyOff = true;
                }
                else if (installed == false)
                {
                    anyOn = true;
                }
                else { anyUnknown = true; diagnostics.Add($"应用包状态查询失败：{op.Path}"); }
            }
        }

        if (!hasOp)
        {
            diagnostics.Add("条目没有本工具可评估的操作");
            return BxState.Unknown;
        }

        if (anyUnknown) return BxState.Unknown;
        if (anyMissing && !anyOn && !anyOff) return BxState.NotApplicable;
        if (anyMissing) return BxState.Mixed;

        if (anyOn && !anyOff)
        {
            return BxState.On;
        }

        if (anyOff && !anyOn)
        {
            return BxState.Off;
        }

        if (anyOn && anyOff) return BxState.Mixed;
        diagnostics.Add("没有任何操作提供可用的状态值");
        return BxState.Unknown;
    }

    internal BxState EvaluateItemForSnapshot(BxItem item, Func<string, BxTaskState> taskState) =>
        EvaluateItem(item, taskState);

    public Task<BxState> GetServiceGroupStateAsync(BxServiceGroup group) => Task.Run(() =>
    {
        var services = ExpandServicePatterns(group.UseBlockedDb
            ? BxCatalog.AllServices.Where(s => s.IsBlocked || s.IsBlocked11).Select(s => s.ServiceName)
            : group.Services);

        var existing = services.Where(ServiceExists).ToList();
        if (existing.Count == 0)
        {
            Log.Info($"服务组状态判定：{group.Id} → 未知；数据库成员在本机均不存在；候选数={services.Count}");
            return BxState.Unknown;
        }

        var disabled = 0;
        var unreadable = new List<string>();
        foreach (var name in existing)
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
            if (key?.GetValue("Start") is not int start)
            {
                unreadable.Add(name);
            }
            else if (start == 4)
            {
                disabled++;
            }
        }

        if (unreadable.Count > 0)
        {
            Log.Warn($"服务组状态判定：{group.Id} → 未知；无法读取 Start 值：{string.Join(",", unreadable)}");
            return BxState.Unknown;
        }

        var state = disabled == existing.Count ? BxState.On
            : disabled == 0 ? BxState.Off
            : BxState.Mixed;
        if (state == BxState.Mixed)
            Log.Info($"服务组状态判定：{group.Id} → 混合；已禁用={disabled}/{existing.Count}");
        return state;
    });

    /// <summary>
    /// 单个注册表操作：返回 (匹配优化值?, 匹配默认值?)。
    /// 值簇选择采用 DEFAULT 锚定：带 DEFAULT 标记的簇恒为“默认态”，
    /// 不带 DEFAULT 的簇为“优化态”（优化态标记在 BoosterX 数据库中可能是 BEST/OPTIMAL/ON/OFF 任一，不能按标记猜）。
    /// </summary>
    private (bool? On, bool? Off) MatchReg(BxTweak op)
    {
        var appliedValue = PickAppliedEntry(op);
        var defaultValue = PickDefaultEntry(op);
        if (appliedValue is null || defaultValue is null)
        {
            return (null, null);
        }

        var current = ReadReg(op);
        var onMatch = CompareReg(current, appliedValue, op.ValueFormat);
        var offMatch = CompareReg(current, defaultValue, op.ValueFormat);
        return (onMatch, offMatch);
    }

    /// <summary>默认态取值：优先带 DEFAULT 标记的簇，其次带 OFF 的簇。</summary>
    private static BxValueEntry? PickDefaultEntry(BxTweak op) =>
        PickValue(op, v => v.Contains("DEFAULT")) ?? PickValue(op, v => v.Contains("OFF"));

    /// <summary>优化态取值：不带 DEFAULT 标记的第一个簇（可能是 ON/BEST/OPTIMAL/OFF）。</summary>
    private static BxValueEntry? PickAppliedEntry(BxTweak op) =>
        PickValue(op, v => !v.Contains("DEFAULT"));

    private static BxValueEntry? PickValue(BxTweak op, Func<IEnumerable<string>, bool> predicate) =>
        op.Values.FirstOrDefault(v => predicate(v.ValueTypes));

    private static object? ReadReg(BxTweak op)
    {
        var (hive, sub) = ParsePath(op.Path);
        using var key = hive.OpenSubKey(sub);
        return key?.GetValue(op.Key ?? "");
    }

    private static bool? CompareReg(object? current, BxValueEntry expected, string? format)
    {
        var value = expected.Value;
        if (value is null)
        {
            return null;
        }

        if (value == "Null")
        {
            return current is null;
        }

        if (current is null)
        {
            return false;
        }

        if (format == "HEX")
        {
            try
            {
                var expectedNum = Convert.ToInt64(value, 16);
                return current switch
                {
                    int i => i == expectedNum,
                    long l => l == expectedNum,
                    _ => false,
                };
            }
            catch
            {
                return false;
            }
        }

        if (current is byte[] bytes)
        {
            var hex = Convert.ToHexString(bytes);
            return hex.Equals(value, StringComparison.OrdinalIgnoreCase);
        }

        if (current is int intCurrent)
        {
            return int.TryParse(value, out var intExpected) && intCurrent == intExpected;
        }

        return current.ToString()?.Equals(value, StringComparison.OrdinalIgnoreCase) == true;
    }

    // ---------------- 多档位（gear）支持 ----------------

    public Task<BxGearInfo?> GetGearInfoAsync(BxItem item) => Task.Run(() =>
    {
        // 白名单制：只有 meta.json 标记 "g":"multi" 的条目才是多档位，其余一律走开关
        if (!BxCatalog.IsGearItem(item.Name) || !IsGearItem(item))
        {
            return null;
        }

        var op = item.Tweaks[0];
        var options = new List<BxGearOption>();
        var currentIndex = -1;
        var current = ReadReg(op);

        for (var i = 0; i < op.Values.Count; i++)
        {
            var entry = op.Values[i];
            if (string.IsNullOrEmpty(entry.Value))
            {
                continue;
            }

            var tag = entry.ValueTypes.Count > 0 ? entry.ValueTypes[0] : "";
            var tagText = tag switch
            {
                "BEST" => "最优",
                "OPTIMAL" => "流畅",
                "DEFAULT" => "默认",
                "ON" => "开",
                "OFF" => "关",
                _ => tag,
            };
            options.Add(new BxGearOption { Label = $"{tagText}（{entry.Value}）", Value = entry.Value });

            if (CompareReg(current, entry, op.ValueFormat) == true)
            {
                currentIndex = options.Count - 1;
            }
        }

        return options.Count >= 2
            ? new BxGearInfo { Options = options, CurrentIndex = currentIndex }
            : null;
    });

    public async Task<OperationResult> ApplyGearAsync(BxItem item, int gearIndex) => await Task.Run(async () =>
    {
        if (!ElevationHelper.IsElevated)
        {
            Log.Warn($"扩展库档位应用拒绝：{item.Name}；原因=当前进程未提升为管理员");
            return OperationResult.Fail(NotElevated);
        }
        if (BxCatalog.ValidateItem(item) is { } gearError)
        {
            Log.Warn($"扩展库档位应用拒绝：{item.Name}；数据校验失败：{gearError}");
            return OperationResult.Fail("数据校验失败：" + gearError);
        }

        if (!IsGearItem(item) || gearIndex < 0 || gearIndex >= item.Tweaks[0].Values.Count)
        {
            Log.Warn($"扩展库档位判定失败：{item.Name}；index={gearIndex}；档位值数量={(item.Tweaks.Count == 0 ? 0 : item.Tweaks[0].Values.Count)}；IsGearItem={IsGearItem(item)}");
            return OperationResult.Fail("无效的档位。");
        }

        var entry = item.Tweaks[0].Values[gearIndex];
        if (string.IsNullOrEmpty(entry.Value))
        {
            Log.Warn($"扩展库档位判定失败：{item.Name}；index={gearIndex} 对应空值");
            return OperationResult.Fail("无效的档位值。");
        }

        var op = item.Tweaks[0];
        try
        {
            BackupReg(op);
            var (hive, sub) = ParsePath(op.Path);
            using var key = hive.CreateSubKey(sub);
            if (key is null)
            {
                Log.Warn($"扩展库档位写入失败：{item.Name}；注册表键无法打开：{op.Path}");
                return OperationResult.Fail("无法打开注册表键。");
            }

            if (entry.Value == "Null")
            {
                key.DeleteValue(op.Key ?? "", throwOnMissingValue: false);
            }
            else if (op.TweakType == "REG_SZ")
            {
                key.SetValue(op.Key ?? "", entry.Value, RegistryValueKind.String);
            }
            else
            {
                var num = op.ValueFormat == "HEX"
                    ? int.Parse(entry.Value, System.Globalization.NumberStyles.HexNumber)
                    : int.Parse(entry.Value, System.Globalization.NumberStyles.Integer);
                key.SetValue(op.Key ?? "", num, RegistryValueKind.DWord);
            }

            Log.Info($"扩展库档位应用成功：{item.Name}；index={gearIndex}；value={entry.Value}");
            return OperationResult.Ok($"已切换到「{entry.Value}」。");
        }
        catch (Exception ex)
        {
            Log.Error($"扩展库档位写入异常：{item.Name}；path={op.Path}；key={op.Key}", ex);
            return OperationResult.Fail($"切换档位失败：{ex.Message}");
        }
    });

    private static bool IsGearItem(BxItem item) =>
        item.Tweaks.Count == 1 && item.Tweaks[0].Values.Count >= 2;

    // ---------------- 应用（JSON 条目） ----------------

    public Task<OperationResult> ApplyItemAsync(BxItem item, bool turnOn) => Task.Run(async () =>
    {
        if (!ElevationHelper.IsElevated)
        {
            Log.Warn($"扩展库应用拒绝：{item.Name}；目标={(turnOn ? "优化" : "还原")}；原因=当前进程未提升为管理员");
            return OperationResult.Fail(NotElevated);
        }
        if (BxCatalog.ValidateItem(item) is { } validationError)
        {
            Log.Warn($"扩展库应用拒绝：{item.Name}；数据校验失败：{validationError}");
            return OperationResult.Fail("数据校验失败：" + validationError);
        }

        Log.Info($"扩展库应用开始：{item.Name}；目标={(turnOn ? "优化" : "还原")}");

        var regOps = item.Tweaks.Where(t => IsRegType(t.TweakType)).ToList();
        var taskOps = item.Tweaks.Where(t => t.TweakType == "TASK").ToList();
        var wevtOps = item.Tweaks.Where(t => t.TweakType == "WEVTUTIL").ToList();
        var svcOps = item.Tweaks.Where(t => t.TweakType == "SVC").ToList();
        var deviceOps = item.Tweaks.Where(t => t.TweakType == "DEVICE").ToList();
        var cmdOps = item.Tweaks.Where(t => t.TweakType == "CMD").ToList();
        var appxOps = item.Tweaks.Where(t => t.TweakType == "APPX").ToList();
        var regEnumOps = item.Tweaks.Where(t => t.TweakType == "REGENUM").ToList();

        // 支持性判定必须包含 REGENUM（网卡省电全禁等条目是纯 REGENUM，漏判会被误报"暂不支持"）
        if (regOps.Count == 0 && taskOps.Count == 0 && wevtOps.Count == 0 && svcOps.Count == 0
            && deviceOps.Count == 0 && cmdOps.Count == 0 && appxOps.Count == 0 && regEnumOps.Count == 0)
        {
            Log.Warn($"扩展库应用跳过：{item.Name}；没有本工具支持的操作类型；类型=[{string.Join(",", item.Tweaks.Select(t => t.TweakType))}]");
            return OperationResult.Fail("该条目由原工具内部脚本实现，本工具暂不支持。");
        }

        // DEVICE 操作先解析设备并备份原启用状态，避免先写 BCD 后发现设备不存在或状态无法恢复。
        var deviceStates = new Dictionary<BxTweak, List<PnpDeviceState>>();
        foreach (var op in deviceOps)
        {
            if (!TryGetDeviceEnabledTarget(op, turnOn, out _))
            {
                Log.Warn($"扩展库设备操作预检失败：{item.Name}；path={op.Path}；操作值仅支持 Enable/Disable");
                return OperationResult.Fail("设备操作定义无效：仅支持 Enable / Disable。");
            }

            var states = QueryPnpDeviceStates(op.Path);
            if (states is null || states.Count == 0 || states.Any(s => s.Enabled is null))
            {
                Log.Warn($"扩展库设备操作预检失败：{item.Name}；selector={op.Path}；原因={(states is null ? "查询失败" : states.Count == 0 ? "未找到设备" : "至少一个设备状态未知")}；未应用任何修改");
                return OperationResult.Fail("无法确认高精度事件计时器设备状态，未应用任何修改。");
            }

            deviceStates[op] = states;
        }

        if (deviceStates.Count > 0)
        {
            var deviceBackups = deviceStates.Values.SelectMany(states => states)
                .Select(state => new BxBackupEntry
                {
                    Kind = "Device",
                    Id = "Device|" + state.InstanceId,
                    KeyPath = state.InstanceId,
                    ValueKind = state.Enabled == true ? 1 : 0,
                })
                .ToList();
            BxBackupStore.SaveMany(deviceBackups);
            var savedIds = BxBackupStore.ReadAll().Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (deviceBackups.Any(e => !savedIds.Contains(e.Id)))
            {
                Log.Warn($"扩展库设备操作预检失败：{item.Name}；设备原状态备份校验失败；未应用任何修改");
                return OperationResult.Fail("高精度事件计时器原状态备份失败，未应用任何修改。");
            }
        }

        var done = 0;
        var failed = 0;
        var failureDetails = new List<string>();
        if (item.Name == "USBSelectiveSuspendOff" && cmdOps.Count > 0)
        {
            if (!ApplyCmd(cmdOps[0], item.Name, turnOn, out var powerError, out _))
            {
                Log.Warn($"扩展库电源计划操作失败：{item.Name}；{powerError}");
                return OperationResult.Fail("USB 电源计划未完成，电源值可能部分改变，原值备份已保留；注册表未修改：" + powerError);
            }
            done++;
            cmdOps.RemoveAt(0);
        }
        foreach (var op in regOps)
        {
            if (ApplyReg(op, turnOn, out var error))
            {
                done++;
            }
            else
            {
                failed++;
                var message = $"注册表操作失败：{op.TweakType} {op.Path}\\{op.Key} —— {error}";
                Log.Warn($"扩展库操作失败：{item.Name}；{message}");
                failureDetails.Add(message);
            }
        }

        if (taskOps.Count > 0)
        {
            var (ok, count) = ApplyTasks(taskOps, !turnOn);
            done += count;
            if (!ok)
            {
                failed++;
                var message = $"计划任务批量操作未全部完成：成功 {count}/{taskOps.Select(o => "\\" + o.Path.TrimStart('\\')).Distinct(StringComparer.OrdinalIgnoreCase).Count()} 项";
                Log.Warn($"扩展库操作失败：{item.Name}；{message}");
                failureDetails.Add(message);
            }
        }

        foreach (var op in wevtOps)
        {
            if (ApplyWevt(op.Path, !turnOn, out var error))
            {
                done++;
            }
            else
            {
                failed++;
                var message = $"事件日志通道操作失败：{op.Path} —— {error}";
                Log.Warn($"扩展库操作失败：{item.Name}；{message}");
                failureDetails.Add(message);
            }
        }

        foreach (var op in svcOps)
        {
            if (ApplySvc(op, turnOn, out var error))
            {
                done++;
            }
            else
            {
                failed++;
                var message = $"服务操作失败：{op.Path} —— {error}";
                Log.Warn($"扩展库操作失败：{item.Name}；{message}");
                failureDetails.Add(message);
            }
        }

        foreach (var op in item.Tweaks.Where(t => t.TweakType == "REGENUM"))
        {
            if (ApplyRegEnum(op, turnOn, out var error))
            {
                done++;
                if (!string.IsNullOrWhiteSpace(error))
                {
                    failed++;
                    Log.Warn($"扩展库 REGENUM 部分失败：{item.Name}；{op.Path}\\{op.Key} —— {error}");
                    failureDetails.Add("REGENUM 部分实例失败：" + error);
                }
            }
            else
            {
                failed++;
                var message = $"REGENUM 操作失败：{op.Path}\\{op.Key} —— {error}";
                Log.Warn($"扩展库操作失败：{item.Name}；{message}");
                failureDetails.Add(message);
            }
        }

        foreach (var op in cmdOps)
        {
            if (ApplyCmd(op, item.Name, turnOn, out var error, out var skipped))
            {
                if (!skipped) done++;
            }
            else
            {
                failed++;
                Log.Warn($"扩展库命令失败：{item.Name}；type={op.TweakType}；path={op.Path}；target={(turnOn ? "优化" : "还原")}；{error}");
                failureDetails.Add("命令：" + error);
            }
        }

        foreach (var op in deviceOps)
        {
            if (item.Name.Equals("HPETName", StringComparison.OrdinalIgnoreCase) && failed > 0)
            {
                failureDetails.Add("设备管理器状态未更改（HPET 引导配置失败，为避免部分生效已跳过设备操作）");
                break;
            }

            if (!TryGetDeviceEnabledTarget(op, turnOn, out var enabled))
            {
                failed++;
                var message = $"设备操作定义无效：{op.Path}，仅支持 Enable / Disable";
                Log.Warn($"扩展库操作失败：{item.Name}；{message}");
                failureDetails.Add(message);
                continue;
            }

            foreach (var state in deviceStates[op])
            {
                var deviceResult = SetPnpDeviceEnabled(state.InstanceId, enabled);
                if (deviceResult.Success)
                {
                    done++;
                }
                else
                {
                    failed++;
                    var message = $"设备管理器操作失败：{state.InstanceId} —— {deviceResult.Message}";
                    Log.Warn($"扩展库操作失败：{item.Name}；{message}");
                    failureDetails.Add(message);
                }
            }
        }

        foreach (var op in appxOps)
        {
            var appxResult = await ApplyAppxAsync(op.Path, turnOn);
            if (appxResult.Success)
            {
                done++;
            }
            else
            {
                failed++;
                var message = $"APPX {op.Path} —— {appxResult.Message}";
                Log.Warn($"扩展库操作失败：{item.Name}；{message}");
                failureDetails.Add(message);
            }
        }

        var result = failed > 0
            ? OperationResult.Fail($"完成 {done} 项，失败 {failed} 项。" +
                                   (failureDetails.Count > 0 ? " " + string.Join("；", failureDetails) : " 部分键可能需要更高权限。"))
            : OperationResult.Ok(turnOn ? "已应用。" : "已还原默认。");
        Log.Info($"扩展库应用完成：{item.Name}；目标={(turnOn ? "优化" : "还原")}；成功={done}；失败={failed}；结果={result.Message}");
        return result;
    });

    /// <summary>APPX 操作：移除/恢复 UWP 应用包（含预配置包，避免新建用户重新装回）。</summary>
    private static async Task<OperationResult> ApplyAppxAsync(string pattern, bool turnOn)
    {
        var safePattern = pattern.Replace("'", "''");
        if (turnOn)
        {
            var script =
                "$removed = 0; $failures = @(); " +
                "Get-AppxPackage -AllUsers -Name '" + safePattern + "' | ForEach-Object { $pkg=$_; try { Remove-AppxPackage -Package $pkg.PackageFullName -AllUsers -ErrorAction Stop; $removed++ } catch { $failures += ('Package ' + $pkg.PackageFullName + ': ' + $_.Exception.Message) } }; " +
                "Get-AppxProvisionedPackage -Online | Where-Object { $_.DisplayName -like '" + safePattern + "' } | ForEach-Object { $pkg=$_; try { Remove-AppxProvisionedPackage -Online -PackageName $pkg.PackageName -ErrorAction Stop | Out-Null; $removed++ } catch { $failures += ('Provisioned ' + $pkg.PackageName + ': ' + $_.Exception.Message) } }; " +
                "Write-Output ('__BX_APPX_COUNT__' + $removed); $failures | ForEach-Object { Write-Output ('__BX_APPX_FAIL__' + $_) }";

            var (code, stdout, stderr) = await Task.Run(() => RunPowerShell(script, TimeSpan.FromMinutes(5)));
            var (count, failures) = ParseAppxOutput(stdout);
            if (count > 0)
            {
                InvalidateAppxCache();
            }

            foreach (var failure in failures.Take(8))
                Log.Warn($"APPX 移除子操作失败：pattern={pattern}；{failure}");
            if (code != 0 || count == 0 || failures.Count > 0)
                Log.Warn($"APPX 移除判定：pattern={pattern}；removed={count}；subFailures={failures.Count}；exitCode={code}；stderr={stderr?.Trim()}");

            return count > 0
                ? OperationResult.Ok($"已移除（{count} 项，含预配置包）。")
                : OperationResult.Fail("未找到匹配的应用包（可能已移除）。");
        }

        // 恢复：优先从 WindowsApps 残留目录重新注册；失败则提示从商店安装
        var register =
            "$restored = 0; $failures = @(); " +
            "Get-AppxPackage -AllUsers -Name '" + safePattern + "' | ForEach-Object { $pkg=$_; " +
            "  $m = Join-Path $pkg.InstallLocation 'AppxManifest.xml'; " +
            "  if (Test-Path $m) { try { Add-AppxPackage -Register $m -DisableDevelopmentMode -ErrorAction Stop; $restored++ } catch { $failures += ($pkg.PackageFullName + ': ' + $_.Exception.Message) } } " +
            "}; Write-Output ('__BX_APPX_COUNT__' + $restored); $failures | ForEach-Object { Write-Output ('__BX_APPX_FAIL__' + $_) }";
        var (restoreCode, stdout2, restoreError) = await Task.Run(() => RunPowerShell(register, TimeSpan.FromMinutes(5)));
        var (restored, restoreFailures) = ParseAppxOutput(stdout2);
        foreach (var failure in restoreFailures.Take(8))
            Log.Warn($"APPX 恢复子操作失败：pattern={pattern}；{failure}");
        if (restoreCode != 0 || restored == 0 || restoreFailures.Count > 0)
            Log.Warn($"APPX 恢复判定：pattern={pattern}；restored={restored}；subFailures={restoreFailures.Count}；exitCode={restoreCode}；stderr={restoreError?.Trim()}");

        return restored > 0
            ? OperationResult.Ok($"已为本用户重新注册（{restored} 项）。")
            : OperationResult.Fail("该应用已彻底移除，请从 Microsoft Store 重新安装。");
    }

    private static (int Count, List<string> Failures) ParseAppxOutput(string? output)
    {
        var count = 0;
        var failures = new List<string>();
        foreach (var rawLine in (output ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("__BX_APPX_COUNT__", StringComparison.Ordinal) &&
                int.TryParse(line["__BX_APPX_COUNT__".Length..], out var parsed))
                count = parsed;
            else if (line.StartsWith("__BX_APPX_FAIL__", StringComparison.Ordinal))
                failures.Add(line["__BX_APPX_FAIL__".Length..]);
        }

        return (count, failures);
    }

    /// <summary>APPX 安装状态缓存（惰性拉取；可经 <see cref="InvalidateAppxCache"/> 失效以便每次进页重读）。</summary>
    private static HashSet<string>? _installedAppxCache;
    private static bool _appxCacheValid;
    private static readonly object _appxGate = new();

    public static void InvalidateAppxCache()
    {
        lock (_appxGate)
        {
            _installedAppxCache = null;
            _appxCacheValid = false;
        }
    }

    private static HashSet<string>? LoadInstalledAppx()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var (code, stdout, _) = RunPowerShell(
                "$ErrorActionPreference='Stop'; " +
                "Get-AppxPackage -AllUsers -ErrorAction Stop | ForEach-Object { Write-Output $_.Name }; " +
                "Get-AppxProvisionedPackage -Online -ErrorAction Stop | ForEach-Object { Write-Output $_.DisplayName }; " +
                "Write-Output '__BX_APPX_OK__'", TimeSpan.FromMinutes(3));
            if (code != 0 || stdout is null || !stdout.Contains("__BX_APPX_OK__", StringComparison.Ordinal))
            {
                return null;
            }
            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line != "__BX_APPX_OK__") set.Add(line);
            }
        }
        catch
        {
            return null;
        }

        return set;
    }

    private static HashSet<string>? GetInstalledAppx()
    {
        lock (_appxGate)
        {
            if (!_appxCacheValid)
            {
                _installedAppxCache = LoadInstalledAppx();
                _appxCacheValid = true;
            }
            return _installedAppxCache;
        }
    }

    void IBxService.InvalidateAppxCache() => InvalidateAppxCache();

    /// <summary>通配符匹配应用包是否已安装（* 任意段；无通配符按前缀段精确匹配）。</summary>
    private static bool? IsAppxInstalled(string pattern)
    {
        var set = GetInstalledAppx();
        if (set is null) return null;

        return MatchesAppxPattern(set, pattern);
    }

    internal static bool MatchesAppxPattern(IEnumerable<string> names, string pattern)
    {
        if (!pattern.Contains('*')) return names.Contains(pattern, StringComparer.OrdinalIgnoreCase);
        var regex = new System.Text.RegularExpressions.Regex(
            '^' + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + '$',
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return names.Any(regex.IsMatch);
    }

    private static bool TryGetDeviceEnabledTarget(BxTweak op, bool turnOn, out bool enabled)
    {
        var target = turnOn ? PickAppliedEntry(op)?.Value : PickDefaultEntry(op)?.Value;
        if (string.Equals(target, "Enable", StringComparison.OrdinalIgnoreCase))
        {
            enabled = true;
            return true;
        }

        if (string.Equals(target, "Disable", StringComparison.OrdinalIgnoreCase))
        {
            enabled = false;
            return true;
        }

        enabled = false;
        return false;
    }

    private static bool IsSafePnpSelector(string selector) =>
        !string.IsNullOrWhiteSpace(selector) && selector.Length <= 512 &&
        selector.All(c => (c is >= 'A' and <= 'Z') || (c is >= 'a' and <= 'z') ||
                          (c is >= '0' and <= '9') || c is '\\' or '&' or '_' or '.' or '-');

    /// <summary>按完整实例 ID 或设备 ID 前缀查询 PnP 设备状态；null 表示查询失败，空集合表示未找到。</summary>
    private static List<PnpDeviceState>? QueryPnpDeviceStates(string selector)
    {
        if (!IsSafePnpSelector(selector)) return null;

        var safeSelector = selector.Replace("'", "''");
        var script =
            "$ErrorActionPreference='Stop'; try { " +
            "$selector='" + safeSelector + "'; " +
            "$devices = @(Get-PnpDevice -ErrorAction Stop | Where-Object { $_.InstanceId -and " +
            "($_.InstanceId -ieq $selector -or $_.InstanceId.StartsWith($selector + [char]92, [System.StringComparison]::OrdinalIgnoreCase)) }); " +
            "if ($devices.Count -eq 0) { Write-Output 'NOT_FOUND'; exit 0 }; " +
            "foreach ($device in $devices) { " +
            "$state = 'UNKNOWN'; " +
            "try { $problem = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_ProblemCode' -ErrorAction Stop).Data; " +
            "if ($problem -eq 22) { $state = 'DISABLED' } elseif ($problem -eq 0) { $state = 'ENABLED' } } catch { }; " +
            "if ($state -eq 'UNKNOWN') { if ([string]$device.Status -eq 'OK') { $state = 'ENABLED' } " +
            "elseif ([string]$device.Problem -match '(^|[^0-9])22([^0-9]|$)|DISABLED') { $state = 'DISABLED' } }; " +
            "Write-Output ($state + '|' + $device.InstanceId) } " +
            "} catch { Write-Output ('ERROR|' + $_.Exception.Message); exit 1 }";

        var (code, stdout, _) = RunPowerShell(script, TimeSpan.FromSeconds(30));
        if (code != 0 || string.IsNullOrWhiteSpace(stdout) || stdout.Contains("ERROR|", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn($"PnP 设备状态查询失败：selector={selector}；exitCode={code}；output={stdout?.Trim()}");
            return null;
        }

        var states = new List<PnpDeviceState>();
        foreach (var line in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var value = line.Trim();
            if (value.Equals("NOT_FOUND", StringComparison.OrdinalIgnoreCase)) return [];
            var separator = value.IndexOf('|');
            if (separator <= 0 || separator == value.Length - 1) continue;

            var status = value[..separator];
            bool? enabled = status.Equals("ENABLED", StringComparison.OrdinalIgnoreCase) ? true
                : status.Equals("DISABLED", StringComparison.OrdinalIgnoreCase) ? false
                : null;
            states.Add(new PnpDeviceState(value[(separator + 1)..], enabled));
        }

        return states;
    }

    private static OperationResult SetPnpDeviceEnabled(string instanceId, bool enabled)
    {
        if (!ElevationHelper.IsElevated) return OperationResult.Fail(NotElevated);
        if (!IsSafePnpSelector(instanceId)) return OperationResult.Fail("设备实例 ID 无效。");

        var current = QueryPnpDeviceStates(instanceId);
        if (current is null || current.Count != 1 || current[0].Enabled is null)
            return OperationResult.Fail("无法读取设备当前状态：" + instanceId);
        if (current[0].Enabled == enabled) return OperationResult.Ok("设备状态已符合目标值。");

        var cmdlet = enabled ? "Enable-PnpDevice" : "Disable-PnpDevice";
        var safeId = instanceId.Replace("'", "''");
        var script =
            "$ErrorActionPreference='Stop'; try { " + cmdlet + " -InstanceId '" + safeId +
            "' -Confirm:$false -ErrorAction Stop; exit 0 } catch { Write-Output $_.Exception.Message; exit 1 }";
        var (code, stdout, _) = RunPowerShell(script, TimeSpan.FromSeconds(45));
        if (code != 0)
            return OperationResult.Fail(string.IsNullOrWhiteSpace(stdout) ? "PnP 操作失败。" : stdout.Trim());

        return OperationResult.Ok(enabled ? "设备已启用。" : "设备已禁用。");
    }

    private static BxState QueryHpetBcdState()
    {
        var (code, output) = RunCapture("bcdedit.exe", "/enum {current}", TimeSpan.FromSeconds(20));
        if (code != 0 || string.IsNullOrWhiteSpace(output))
        {
            Log.Warn($"HPET 引导状态查询失败：bcdedit 退出码={code}；输出={output.Trim()}");
            return BxState.Unknown;
        }

        bool IsEnabled(string key) => output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Any(parts => parts.Length >= 2 && parts[0].Equals(key, StringComparison.OrdinalIgnoreCase) &&
                          parts.Skip(1).Any(value => value.Equals("Yes", StringComparison.OrdinalIgnoreCase)));

        var dynamicTick = IsEnabled("disabledynamictick");
        var platformTick = IsEnabled("useplatformtick");
        if (dynamicTick && platformTick) return BxState.On;
        if (!dynamicTick && !platformTick) return BxState.Off;
        return BxState.Mixed;
    }

    /// <summary>
    /// 条目级只读状态查询：为仅有 CMD 操作（无注册表/服务/任务可推断）的条目提供真实状态。
    /// 返回 true=优化已应用；false=未应用；null=无法判断。查询全部为只读命令。
    /// </summary>
    private static bool? QueryCmdItemState(string itemName)
    {
        switch (itemName)
        {
            case "HandwritingEN":
            {
                var (_, stdout, _) = RunPowerShell(
                    "Get-WindowsCapability -Online | Where-Object { $_.Name -like 'Language.Handwriting*en-US*' } | ForEach-Object { Write-Output $_.State }",
                    TimeSpan.FromSeconds(60));
                var state = stdout?.Trim();
                if (string.IsNullOrEmpty(state))
                {
                    return null;
                }

                return !state.Contains("Installed", StringComparison.OrdinalIgnoreCase);
            }
            case "OneDrive":
            {
                try
                {
                    var locations = new[]
                    {
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "OneDrive", "OneDrive.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft OneDrive", "OneDrive.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft OneDrive", "OneDrive.exe"),
                    };
                    if (locations.Any(File.Exists)) return false;
                    foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
                    {
                        foreach (var suffix in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
                        {
                            using var uninstall = root.OpenSubKey(suffix);
                            if (uninstall is null) continue;
                            foreach (var subName in uninstall.GetSubKeyNames())
                            {
                                using var entry = uninstall.OpenSubKey(subName);
                                var display = entry?.GetValue("DisplayName")?.ToString();
                                if (display?.Equals("Microsoft OneDrive", StringComparison.OrdinalIgnoreCase) == true)
                                    return false;
                            }
                        }
                    }
                    return true;
                }
                catch
                {
                    return null;
                }
            }
            default:
                return null;
        }
    }

    /// <summary>SVC 操作：控制单项服务的启动模式（ON=禁用 Start 4 并停止，OFF=写回默认值）。</summary>
    private static bool ApplySvc(BxTweak op, bool turnOn, out string error)
    {
        error = "";
        var target = turnOn
            ? PickAppliedEntry(op)
            : PickDefaultEntry(op);
        if (target?.Value is null || !int.TryParse(target.Value, out var startValue))
        {
            error = "缺少有效的目标 Start 值";
            return false;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + op.Path, writable: true);
            if (key is null)
            {
                error = "服务注册表键不存在或无法写入";
                return false;
            }

            if (key.GetValue("Start") is int cur)
            {
                BxBackupStore.SaveOnce(new BxBackupEntry
                {
                    Kind = "Service",
                    Id = "Service|" + op.Path,
                    KeyPath = op.Path,
                    ValueKind = cur,
                });
            }

            key.SetValue("Start", startValue, RegistryValueKind.DWord);
            if (turnOn)
            {
                var stopCode = RunCmd($"sc.exe stop \"{op.Path}\" >nul 2>&1", TimeSpan.FromSeconds(20));
                if (stopCode == 1062)
                {
                    Log.Info($"服务操作判定：{op.Path} 已停止（SC 1062 / ERROR_SERVICE_NOT_ACTIVE），停止请求视为已满足");
                }
                else if (stopCode != 0)
                {
                    error = $"Start 已写入 {startValue}，但停止服务失败（退出码 {stopCode}）";
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>CMD 操作：执行外部数据文件里定义的命令（ON/OFF 各一条；空命令跳过）。</summary>
    private static bool ApplyCmd(BxTweak op, string itemName, bool turnOn, out string error, out bool skipped)
    {
        if (itemName == "USBSelectiveSuspendOff")
        {
            skipped = false;
            var powerResult = turnOn ? DisableUsbSelectiveSuspend() : RestoreUsbSelectiveSuspend();
            error = powerResult.Success ? "" : powerResult.Message;
            return powerResult.Success;
        }
        var target = turnOn
            ? PickAppliedEntry(op)
            : PickDefaultEntry(op);
        var command = target?.Value;
        if (string.IsNullOrWhiteSpace(command))
        {
            error = "";
            skipped = true;
            Log.Info($"扩展库命令跳过：{itemName}；目标={(turnOn ? "优化" : "还原")}；原因=数据库没有定义该方向的命令");
            return true;
        }

        skipped = false;
        BackupBcdValues(command);
        var (code, stderr) = RunCmdDetailed(command, TimeSpan.FromMinutes(2));
        error = code == 0 ? "" : code == -1 ? "启动失败或超时" : code == 1062
            ? "退出码 1062（ERROR_SERVICE_NOT_ACTIVE：停止目标服务时该服务已未运行；复合命令仍需检查其他步骤）"
            : $"退出码 {code}";
        if (code != 0 && !string.IsNullOrWhiteSpace(stderr))
            error += "：" + stderr.Trim();
        return code == 0;
    }

    private const string PowerSchemesPath = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
    private const string UsbSubgroup = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string UsbSuspendSetting = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";

    internal static List<BxBackupEntry> MakeUsbPowerBackup(string scheme, int? ac, int? dc)
    {
        var settingPath = $@"{PowerSchemesPath}\{scheme}\{UsbSubgroup}\{UsbSuspendSetting}";
        return
        [
            new() { Kind = "Power", Id = "Power|ActiveScheme", KeyPath = PowerSchemesPath,
                ValueName = "ActivePowerScheme", ValueKind = 1, Data = scheme },
            new() { Kind = "Power", Id = "Power|" + scheme + "|AC", KeyPath = settingPath,
                ValueName = "ACSettingIndex", ValueKind = ac.HasValue ? 1 : 0, Data = ac?.ToString() },
            new() { Kind = "Power", Id = "Power|" + scheme + "|DC", KeyPath = settingPath,
                ValueName = "DCSettingIndex", ValueKind = dc.HasValue ? 1 : 0, Data = dc?.ToString() },
        ];
    }

    private static OperationResult DisableUsbSelectiveSuspend()
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(PowerSchemesPath);
            var scheme = root?.GetValue("ActivePowerScheme")?.ToString();
            if (!Guid.TryParse(scheme, out _)) return OperationResult.Fail("无法读取当前电源计划，未修改 USB 设置。");
            var settingPath = $@"{PowerSchemesPath}\{scheme}\{UsbSubgroup}\{UsbSuspendSetting}";
            using var setting = Registry.LocalMachine.OpenSubKey(settingPath);
            int? ac = setting?.GetValue("ACSettingIndex") as int?;
            int? dc = setting?.GetValue("DCSettingIndex") as int?;
            var backup = MakeUsbPowerBackup(scheme!, ac, dc);
            BxBackupStore.SaveMany(backup);
            var savedIds = BxBackupStore.ReadAll().Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (backup.Any(e => !savedIds.Contains(e.Id))) return OperationResult.Fail("USB 电源计划原值备份失败，未修改设置。");

            foreach (var mode in new[] { "ac", "dc" })
            {
                var (code, _) = RunCapture("powercfg.exe", $"/set{mode}valueindex {scheme} {UsbSubgroup} {UsbSuspendSetting} 0", TimeSpan.FromSeconds(20));
                if (code != 0) return OperationResult.Fail($"USB {mode.ToUpperInvariant()} 设置失败（退出码 {code}）。");
            }
            var (activeCode, _) = RunCapture("powercfg.exe", $"/setactive {scheme}", TimeSpan.FromSeconds(20));
            return activeCode == 0 ? OperationResult.Ok("已关闭 USB 选择性暂停。")
                : OperationResult.Fail($"USB 电源计划刷新失败（退出码 {activeCode}）。");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("USB 电源设置失败：" + ex.Message);
        }
    }

    private static bool? QueryUsbSelectiveSuspendDisabled()
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(PowerSchemesPath);
            var scheme = root?.GetValue("ActivePowerScheme")?.ToString();
            if (!Guid.TryParse(scheme, out _)) return null;
            using var setting = Registry.LocalMachine.OpenSubKey($@"{PowerSchemesPath}\{scheme}\{UsbSubgroup}\{UsbSuspendSetting}");
            return setting?.GetValue("ACSettingIndex") is int ac && ac == 0 &&
                   setting.GetValue("DCSettingIndex") is int dc && dc == 0;
        }
        catch
        {
            return null;
        }
    }

    private static OperationResult RestoreUsbSelectiveSuspend()
    {
        var entries = BxBackupStore.ReadAll().Where(e => e.Kind == "Power").ToList();
        var active = entries.FirstOrDefault(e => e.Id == "Power|ActiveScheme")?.Data;
        if (!Guid.TryParse(active, out _)) return OperationResult.Fail("没有可用的 USB 电源计划原值备份。");
        try
        {
            foreach (var entry in entries.Where(e => e.ValueName is "ACSettingIndex" or "DCSettingIndex"))
            {
                using var key = Registry.LocalMachine.OpenSubKey(entry.KeyPath, writable: true)
                    ?? throw new IOException("电源计划设置不存在：" + entry.KeyPath);
                if (entry.ValueKind == 0) key.DeleteValue(entry.ValueName, throwOnMissingValue: false);
                else if (int.TryParse(entry.Data, out var value)) key.SetValue(entry.ValueName, value, RegistryValueKind.DWord);
                else throw new FormatException("电源计划备份值无效：" + entry.ValueName);
            }
            var (code, _) = RunCapture("powercfg.exe", $"/setactive {active}", TimeSpan.FromSeconds(20));
            if (code != 0) return OperationResult.Fail($"原电源计划激活失败（退出码 {code}），备份已保留。");
            BxBackupStore.RemoveIds(entries.Select(e => e.Id));
            var remaining = BxBackupStore.ReadAll().Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (entries.Any(e => remaining.Contains(e.Id)))
                return OperationResult.Fail("USB 电源值已写回，但备份清理失败；请检查备份文件。");
            return OperationResult.Ok("已恢复 USB 选择性暂停的原 AC/DC 值和电源计划。");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("USB 电源计划恢复失败，备份已保留：" + ex.Message);
        }
    }

    private static void BackupBcdValues(string command)
    {
        var keys = new[] { "hypervisorlaunchtype", "disabledynamictick", "useplatformtick" }
            .Where(k => command.Contains("bcdedit", StringComparison.OrdinalIgnoreCase) &&
                        command.Contains(k, StringComparison.OrdinalIgnoreCase)).ToList();
        if (keys.Count == 0) return;

        var (code, output) = RunCapture("bcdedit.exe", "/enum {current}", TimeSpan.FromSeconds(20));
        if (code != 0) return;
        foreach (var name in keys)
        {
            var line = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(l => l.TrimStart().StartsWith(name + " ", StringComparison.OrdinalIgnoreCase));
            var value = line is null ? null : line.Trim()[name.Length..].Trim();
            BxBackupStore.SaveOnce(new BxBackupEntry
            {
                Kind = "Bcd", Id = "Bcd|" + name, KeyPath = name,
                ValueKind = value is null ? 0 : 1, Data = value,
            });
        }
    }

    private static bool IsRegType(string t) =>
        t is "REG" or "REG_SZ" or "REG_BINARY" or "REG_EXPAND_SZ" or "REG_MULTI_SZ" or "REG_DWORD";

    // ---------------- REGENUM（遍历父键数字子键批量写值，如网卡类实例） ----------------

    /// <summary>枚举父键下形如 0000/0001 的实例子键（须存在 *IfType 值，即物理/虚拟网卡）。</summary>
    private static List<string> EnumerateNumSubKeys(string path)
    {
        var result = new List<string>();
        var (hive, sub) = ParsePath(path);
        using var root = hive.OpenSubKey(sub);
        if (root is null)
        {
            return result;
        }

        foreach (var name in root.GetSubKeyNames())
        {
            if (name.Length == 4 && name.StartsWith("00", StringComparison.Ordinal) && char.IsDigit(name[2]) && char.IsDigit(name[3]))
            {
                using var inst = root.OpenSubKey(name);
                if (inst?.GetValue("*IfType") is not null)
                {
                    result.Add(name);
                }
            }
        }

        return result;
    }

    private static (int Applied, int Total) CountRegEnumInstances(BxTweak op, BxValueEntry appliedEntry)
    {
        var (hive, sub) = ParsePath(op.Path);
        var applied = 0;
        var total = 0;
        foreach (var name in EnumerateNumSubKeys(op.Path))
        {
            total++;
            using var inst = hive.OpenSubKey(sub + "\\" + name);
            var current = inst?.GetValue(op.Key ?? "");
            if (CompareReg(current, appliedEntry, op.ValueFormat) == true)
            {
                applied++;
            }
        }

        return (applied, total);
    }

    /// <summary>REGENUM 操作：对全部实例子键批量备份并写优化值（ON）/ 还原默认值（OFF，Null=删除）。</summary>
    private static bool ApplyRegEnum(BxTweak op, bool turnOn, out string error)
    {
        error = "";
        var target = turnOn ? PickAppliedEntry(op) : PickDefaultEntry(op);
        if (target?.Value is null)
        {
            error = "缺少目标值";
            return false;
        }

        var (hive, sub) = ParsePath(op.Path);
        var names = EnumerateNumSubKeys(op.Path);
        var done = 0;
        var failures = new List<string>();
        foreach (var name in names)
        {
            try
            {
                var instPath = sub + "\\" + name;
                using var inst = hive.OpenSubKey(instPath, writable: true);
                if (inst is null)
                {
                    continue;
                }

                var current = inst.GetValue(op.Key ?? "");
                int kind = 0;
                if (current is not null)
                {
                    kind = (int)inst.GetValueKind(op.Key ?? "");
                }

                BxBackupStore.SaveOnce(new BxBackupEntry
                {
                    Kind = "Reg",
                    Id = $"Reg|{hive.Name}|{instPath}|{op.Key}",
                    KeyPath = instPath,
                    ValueName = op.Key ?? "",
                    ValueKind = kind,
                    StringData = current as string[],
                    Data = current switch
                    {
                        null => null,
                        int i => i.ToString(),
                        long l => l.ToString(),
                        byte[] b => Convert.ToHexString(b),
                        string s => s,
                        string[] values => string.Join(";", values),
                        _ => current.ToString(),
                    },
                });

                if (target.Value == "Null")
                {
                    inst.DeleteValue(op.Key ?? "", throwOnMissingValue: false);
                }
                else
                {
                    var num = int.Parse(target.Value, System.Globalization.NumberStyles.Integer);
                    inst.SetValue(op.Key ?? "", num, RegistryValueKind.DWord);
                }

                done++;
            }
            catch (Exception ex)
            {
                failures.Add(name + ": " + ex.Message);
            }
        }

        if (failures.Count > 0)
            error = $"成功写入 {done}/{names.Count} 个数字子键；失败：{string.Join("；", failures.Take(5))}";
        else if (names.Count == 0)
            error = "未找到数字实例子键";
        return done > 0;
    }

    private static bool ApplyReg(BxTweak op, bool turnOn, out string error)
    {
        error = "";
        var target = turnOn
            ? PickAppliedEntry(op)
            : PickDefaultEntry(op);
        if (target?.Value is null)
        {
            error = "缺少目标值";
            return false;
        }

        try
        {
            BackupReg(op);
            var (hive, sub) = ParsePath(op.Path);
            using var key = hive.CreateSubKey(sub);
            if (key is null)
            {
                error = "注册表键无法创建或打开";
                return false;
            }

            if (target.Value == "Null")
            {
                key.DeleteValue(op.Key ?? "", throwOnMissingValue: false);
                return true;
            }

            switch (op.TweakType)
            {
                case "REG_SZ":
                    key.SetValue(op.Key ?? "", target.Value, RegistryValueKind.String);
                    break;
                case "REG_EXPAND_SZ":
                    key.SetValue(op.Key ?? "", target.Value, RegistryValueKind.ExpandString);
                    break;
                case "REG_BINARY":
                    key.SetValue(op.Key ?? "", HexToBytes(target.Value), RegistryValueKind.Binary);
                    break;
                case "REG_MULTI_SZ":
                    key.SetValue(op.Key ?? "", target.Value.Split(';'), RegistryValueKind.MultiString);
                    break;
                default:
                {
                    // HEX 值（如 80000001）按十六进制解析，其余按十进制
                    var num = op.ValueFormat == "HEX"
                        ? int.Parse(target.Value, System.Globalization.NumberStyles.HexNumber)
                        : int.Parse(target.Value, System.Globalization.NumberStyles.Integer);
                    key.SetValue(op.Key ?? "", num, RegistryValueKind.DWord);
                    break;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void BackupReg(BxTweak op)
    {
        var (hive, sub) = ParsePath(op.Path);
        BxBackupEntry entry;
        try
        {
            entry = CaptureRegEntry(hive, sub, op.Key ?? "");
        }
        catch
        {
            entry = new BxBackupEntry
            {
                Kind = "Reg", Id = $"Reg|{hive.Name}|{sub}|{op.Key}",
                KeyPath = sub, ValueName = op.Key ?? "", ValueKind = 0,
            };
        }
        BxBackupStore.SaveOnce(entry);
    }

    internal static BxBackupEntry CaptureRegEntry(RegistryKey hive, string sub, string valueName)
    {
        using var key = hive.OpenSubKey(sub);
        var current = key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var kind = current is null ? 0 : (int)key!.GetValueKind(valueName);
        return new BxBackupEntry
        {
            Kind = "Reg", Id = $"Reg|{hive.Name}|{sub}|{valueName}",
            KeyPath = sub, ValueName = valueName, ValueKind = kind,
            StringData = current as string[],
            Data = current switch
            {
                null => null, int i => i.ToString(), long l => l.ToString(),
                byte[] b => Convert.ToHexString(b), string s => s,
                string[] values => string.Join(";", values), _ => current.ToString(),
            },
        };
    }

    internal static void RestoreRegEntry(BxBackupEntry entry)
    {
        var hiveName = entry.Id.Split('|').ElementAtOrDefault(1) ?? "HKEY_LOCAL_MACHINE";
        var root = hiveName.Equals("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase) ||
                   hiveName.Equals("HKCU", StringComparison.OrdinalIgnoreCase)
            ? Registry.CurrentUser : Registry.LocalMachine;
        using var key = root.OpenSubKey(entry.KeyPath, writable: true)
            ?? throw new InvalidOperationException($"注册表键不存在：{entry.KeyPath}");
        if (entry.ValueKind == 0)
            key.DeleteValue(entry.ValueName, throwOnMissingValue: false);
        else
            key.SetValue(entry.ValueName, DecodeValue(entry), (RegistryValueKind)entry.ValueKind);
    }

    // ---------------- 计划任务（批处理） ----------------

    private static (bool Ok, int Count) ApplyTasks(List<BxTweak> ops, bool enable)
    {
        _taskStateCache = null;
        _taskStateCacheValid = false;
        var snapshot = QueryAllTaskStates();
        if (snapshot is null)
        {
            Log.Warn($"计划任务操作预检失败：无法枚举系统任务；target={(enable ? "启用" : "禁用")}；requested={ops.Count}");
            return (false, 0);
        }
        var requested = ops.Select(o => "\\" + o.Path.TrimStart('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var paths = ops.Select(o => "\\" + o.Path.TrimStart('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(snapshot.ContainsKey).ToList();
        var missing = requested.Except(paths, StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count > 0)
            Log.Info($"计划任务操作判定：{missing.Count} 个数据库任务在本机不存在，跳过：{string.Join("、", missing)}");
        if (paths.Count == 0)
        {
            Log.Warn($"计划任务操作未执行：本机没有匹配的任务；target={(enable ? "启用" : "禁用")}；requested=[{string.Join("、", requested)}]");
            return (false, 0);
        }
        foreach (var path in paths)
        {
            BxBackupStore.SaveOnce(new BxBackupEntry
            {
                Kind = "Task", Id = "Task|" + path, KeyPath = path,
                ValueKind = snapshot[path] ? 1 : 0,
            });
        }

        var script = new System.Text.StringBuilder();
        script.AppendLine("$ErrorActionPreference='Stop'");
        script.AppendLine("$paths = @(");
        for (var i = 0; i < paths.Count; i++)
        {
            // 最后一行不能带尾逗号：PowerShell 5.1 不允许数组字面量尾逗号，
            // 解析错误会让整批任务操作静默失败（HANDOFF §23.5/§25 定位的失效根因）
            var comma = i < paths.Count - 1 ? "," : "";
            script.AppendLine($"  '{paths[i].Replace("'", "''")}'{comma}");
        }

        script.AppendLine(")");
        script.AppendLine("$done=0");
        script.AppendLine(
            enable
                ? "foreach($p in $paths){ $i=$p.LastIndexOf('\\'); $tp=$p.Substring(0,$i+1); $tn=$p.Substring($i+1); try { Enable-ScheduledTask -TaskPath $tp -TaskName $tn -ErrorAction Stop | Out-Null; $done++ } catch { Write-Output ('__BX_TASK_FAIL__' + $p + '|' + $_.Exception.Message) } }"
                : "foreach($p in $paths){ $i=$p.LastIndexOf('\\'); $tp=$p.Substring(0,$i+1); $tn=$p.Substring($i+1); try { Disable-ScheduledTask -TaskPath $tp -TaskName $tn -ErrorAction Stop | Out-Null; $done++ } catch { Write-Output ('__BX_TASK_FAIL__' + $p + '|' + $_.Exception.Message) } }");
        script.AppendLine("Write-Output $done");

        var (code, stdout, _) = RunPowerShell(script.ToString(), TimeSpan.FromMinutes(3));
        var lines = stdout?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()).ToList() ?? [];
        var count = lines.Select(line => int.TryParse(line, out var n) ? (int?)n : null).LastOrDefault(n => n.HasValue) ?? 0;
        var failures = lines.Where(line => line.StartsWith("__BX_TASK_FAIL__", StringComparison.Ordinal))
            .Select(line => line["__BX_TASK_FAIL__".Length..]).ToList();
        foreach (var failure in failures)
            Log.Warn($"计划任务操作失败：{failure}");
        if (code != 0 || count != paths.Count)
            Log.Warn($"计划任务批处理结果异常：target={(enable ? "启用" : "禁用")}；成功={count}/{paths.Count}；退出码={code}；输出={string.Join(" | ", lines.Take(8))}");
        return (code == 0 && count == paths.Count && failures.Count == 0, count);
    }

    /// <summary>
    /// 一次性查询系统全部计划任务的启用状态（路径带前导反斜杠 -> 是否启用）。
    /// 批量读取条目状态时用，避免每个条目单独起一个 PowerShell 进程。
    /// </summary>
    private static Dictionary<string, bool>? QueryAllTaskStates()
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var (code, stdout, stderr) = RunPowerShell(
            "$ErrorActionPreference='Stop'; Get-ScheduledTask -ErrorAction Stop | ForEach-Object { [string]$_.TaskPath + [string]$_.TaskName + '|' + [string]$_.State }; Write-Output '__BX_TASK_OK__'",
            TimeSpan.FromMinutes(2));

        if (code != 0 || stdout is null || !stdout.Contains("__BX_TASK_OK__", StringComparison.Ordinal))
        {
            Log.Warn($"计划任务状态快照失败：exitCode={code}；stdout={stdout?.Trim()}；stderr={stderr?.Trim()}");
            return null;
        }

        foreach (var rawLine in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            var sep = line.IndexOf('|');
            if (sep <= 0 || line == "__BX_TASK_OK__")
            {
                continue;
            }

            var path = line[..sep];
            var state = line[(sep + 1)..];
            result[path] = !state.Contains("Disabled", StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    // ---------------- 事件日志通道 ----------------

    private static bool ApplyWevt(string channel, bool enable, out string error)
    {
        BackupWevt(channel);
        var (code, output) = RunCapture("wevtutil.exe", $"sl \"{channel}\" /e:{(enable ? "true" : "false")}", TimeSpan.FromSeconds(20));
        error = code == 0 ? "" : $"wevtutil 退出码 {code}" + (string.IsNullOrWhiteSpace(output) ? "" : "：" + output.Trim());
        return code == 0;
    }

    private static void BackupWevt(string channel)
    {
        var enabled = QueryWevtEnabled(channel);
        if (enabled is null)
        {
            return;
        }

        BxBackupStore.SaveOnce(new BxBackupEntry
        {
            Kind = "Wevt",
            Id = "Wevt|" + channel,
            KeyPath = channel,
            ValueKind = enabled.Value ? 1 : 0,
        });
    }

    private static bool? QueryWevtEnabled(string channel)
    {
        var (code, stdout) = RunCapture("wevtutil.exe", $"gl \"{channel}\"", TimeSpan.FromSeconds(20));
        if (string.IsNullOrWhiteSpace(stdout))
        {
            Log.Warn($"事件日志通道状态查询失败：{channel}；wevtutil 退出码={code}；输出为空");
            return null; // 通道不存在或查询失败
        }

        // 不依赖键名（wevtutil 输出在部分本地化系统上键名会翻译）：输出里找 true/false 令牌
        var hasTrue = stdout.Contains("true", StringComparison.OrdinalIgnoreCase);
        var hasFalse = stdout.Contains("false", StringComparison.OrdinalIgnoreCase);
        if (hasTrue == hasFalse)
        {
            Log.Warn($"事件日志通道状态无法解析：{channel}；wevtutil 退出码={code}；输出={stdout.Trim()}");
            return null;
        }

        return hasTrue;
    }

    // ---------------- 服务组应用 ----------------

    /// <summary>
    /// 服务禁用保护名单（HANDOFF §23）：引导/系统启动驱动、内核运行时、系统核心服务与安全组件。
    /// 无用服务清单曾混入 ACPI/disk/DcomLaunch 等关键项导致虚拟机无法启动——数据层已清理，
    /// 这里再做引擎兜底：名单内的服务一律不允许批量禁用（恢复方向不受限）。
    /// </summary>
    private static readonly HashSet<string> ProtectedServiceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // 引导 / 系统启动驱动
        "acpi", "acpiex", "pci", "pciide", "atapi", "disk", "volmgr", "volmgrx", "volume", "vdrvroot",
        "fltmgr", "ntfs", "ksecdd", "ksecpkg", "cng", "clfs", "mountmgr", "partmgr", "pdc", "intelpep",
        "iorate", "fvevol", "wdfilter", "telemetry", "null", "ahcache", "bam", "csc", "dam", "filecrypt",
        "afunix", "basicdisplay", "basicrender", "ndiscap", "netbt", "npsvctrig", "mssmbios", "storahci",
        "stornvme", "vmbus", "rdyboost",
        // 系统核心服务（RPC/DCOM/登录/网络/加密/电源等）
        "dcomlaunch", "rpcss", "rpceptmapper", "lsm", "brokerinfrastructure", "gpsvc", "profsvc",
        "usermanager", "power", "winmgmt", "cryptsvc", "samss", "staterepository",
        "textinputmanagementservice", "coremessagingregistrar", "audiosrv", "audioendpointbuilder",
        "dhcp", "dnscache", "nsi", "mpssvc", "bfe", "eventlog", "schedule", "wpnuserservice",
        // 安装 / 部署 / 设备
        "appxsvc", "camsvc", "deviceinstall", "msiserver", "trustedinstaller", "plugplay", "netprofm",
        // 安全 / 授权
        "windefend", "wdnissvc", "sense", "wscsvc", "securityhealthservice", "sppsvc", "keyiso",
        // 网卡 / USB / 输入 / 虚拟化集成等高风险驱动
        "e1i65x64", "ebdrv", "gencounter", "ipfilterdriver", "ndfltr", "ndisimplatform", "ndisvirtualbus",
        "netadaptercx", "netvsc", "tcpip6", "rmsvc", "wfdsconmgrsvc", "genericusbfn", "iagpio", "intelpmax",
        "kbdhid", "ksthunk", "mouclass", "mouhid", "msgpiowin32", "udecx", "usbhub", "usbhub3", "usbohci",
        "usbxhci", "hdaudbus", "s3cap", "storflt", "synth3dvsc", "terminpt", "virtualrender", "vpci",
        "cnghwassist", "hvcrash", "dmvsc", "msrpc", "storqosflt", "peauth",
    };

    public Task<OperationResult> ApplyServiceGroupAsync(BxServiceGroup group, bool turnOn) => Task.Run(async () =>
    {
        if (!ElevationHelper.IsElevated)
        {
            return OperationResult.Fail(NotElevated);
        }

        var services = ResolveServiceGroup(group);

        var done = 0;
        var denied = new List<string>();
        var guarded = new List<string>();
        var stopTargets = new List<string>();
        foreach (var name in services)
        {
            // 逐服务容错：受保护服务键（TrustedInstaller/Defender 等 ACL）对管理员也拒绝写入，
            // 单个服务失败只记录跳过，不能中断整组（否则异常会炸穿上层批量应用）
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name, writable: true))
                {
                    if (key is null)
                    {
                        Log.Warn($"服务组注册表判定失败：{group.Id}/{name}；服务键不存在或无法以写入权限打开");
                        denied.Add(name);
                        continue;
                    }

                    // 安全护栏（HANDOFF §23）：引导/系统启动服务与保护名单内的服务绝不批量禁用。
                    if (turnOn)
                    {
                        if (key.GetValue("Start") is int currentStart && currentStart <= 1)
                        {
                            guarded.Add(name);
                            Log.Info($"服务组安全护栏：{group.Id}/{name}；Start={currentStart}（Boot/System），禁止批量禁用");
                            continue;
                        }

                        if (ProtectedServiceNames.Contains(name))
                        {
                            guarded.Add(name);
                            Log.Info($"服务组安全护栏：{group.Id}/{name}；命中受保护服务名单，禁止批量禁用");
                            continue;
                        }
                    }

                    var current = key.GetValue("Start");
                    if (current is int start)
                    {
                        var backup = new BxBackupEntry
                        {
                            Kind = "Service",
                            Id = "Service|" + name,
                            KeyPath = name,
                            ValueKind = start,
                        };
                        BxBackupStore.SaveOnce(backup);
                        if (!BxBackupStore.ReadAll().Any(e => e.Id.Equals(backup.Id, StringComparison.OrdinalIgnoreCase)))
                            throw new IOException("服务原值备份未能写入磁盘");
                    }
                    else
                    {
                        Log.Warn($"服务组注册表判定失败：{group.Id}/{name}；无法读取 DWORD Start 值");
                        denied.Add(name);
                        continue;
                    }

                    if (turnOn)
                    {
                        key.SetValue("Start", 4, RegistryValueKind.DWord);
                        stopTargets.Add(name);
                    }
                    else
                    {
                        // 恢复到数据库记录的默认启动模式（无记录则 3=手动）
                        var def = BxCatalog.AllServices.FirstOrDefault(s => s.ServiceName.Equals(name, StringComparison.OrdinalIgnoreCase))?.DefaultStartMode ?? 3;
                        key.SetValue("Start", def, RegistryValueKind.DWord);
                    }

                    done++;
                }
            }
            catch (System.Security.SecurityException ex)
            {
                Log.Warn($"服务组注册表写入被拒绝：{group.Id}/{name}；{ex.Message}");
                denied.Add(name);
            }
            catch (Exception ex)
            {
                Log.Warn($"扩展库服务组：{name} 跳过 —— {ex.Message}");
                denied.Add(name);
            }
        }

        var stopFailed = new List<string>();
        foreach (var name in stopTargets)
        {
            var (code, output) = RunCapture("sc.exe", $"stop \"{name}\"", TimeSpan.FromSeconds(20));
            if (code == 1062)
                Log.Info($"服务组停止判定：{name} 已停止（SC 1062 / ERROR_SERVICE_NOT_ACTIVE），停止目标已满足");
            else if (code != 0)
            {
                Log.Warn($"服务组停止失败：{name}；sc.exe stop 退出码={code}；输出={output.Trim()}");
                stopFailed.Add(name);
            }
        }

        if (guarded.Count > 0)
            Log.Info($"服务组安全护栏跳过：{group.Id}；count={guarded.Count}；services=[{string.Join(",", guarded)}]");
        if (denied.Count > 0)
            Log.Warn($"服务组写入未完成：{group.Id}；count={denied.Count}；services=[{string.Join(",", denied)}]");

        var result = SummarizeServiceGroup(services.Count, done, denied.Concat(guarded).Concat(stopFailed));
        Log.Info($"服务组应用判定：{group.Id}；target={(turnOn ? "禁用" : "恢复默认")}；requested={services.Count}；startValueWritten={done}；protectedOrBootGuardSkipped={guarded.Count}；deniedOrMissing={denied.Count}；stopFailed={stopFailed.Count}；result={result.Message}");
        return result;
    });

    internal static OperationResult SummarizeServiceGroup(int total, int done, IEnumerable<string> failed)
    {
        var failedNames = failed.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var summary = $"启动模式已写入 {done}/{total} 个服务";
        if (failedNames.Count > 0)
            return OperationResult.Fail($"{summary}；未处理：{string.Join("、", failedNames)}。");
        return done > 0 && done == total ? OperationResult.Ok(summary + "。")
            : OperationResult.Fail(total == 0 ? "没有找到组内可操作的服务。" : summary + "，部分成员未完成。");
    }

    // ---------------- 一键恢复 ----------------

    public Task<BxRestoreSummary> RestoreAllAsync() => Task.Run(() =>
    {
        if (!ElevationHelper.IsElevated)
        {
            return new BxRestoreSummary(0, 1, [ElevationHelper.NotElevatedMessage]);
        }

        var entries = BxBackupStore.ReadAll();
        var restored = 0;
        var errors = new List<string>();
        var restoredIds = new List<string>();

        var taskEntries = new List<BxBackupEntry>();
        if (entries.Any(e => e.Kind == "Power"))
        {
            var power = RestoreUsbSelectiveSuspend();
            if (power.Success) restored++;
            else errors.Add(power.Message);
        }
        foreach (var e in entries)
        {
            try
            {
                switch (e.Kind)
                {
                    case "Reg":
                    {
                        RestoreRegEntry(e);

                        restored++;
                        restoredIds.Add(e.Id);
                        break;
                    }
                    case "Task":
                        taskEntries.Add(e);
                        break;
                    case "Bcd":
                    {
                        var argument = e.ValueKind == 0
                            ? $"/deletevalue {e.KeyPath}"
                            : $"/set {e.KeyPath} {e.Data}";
                        var code = RunCapture("bcdedit.exe", argument, TimeSpan.FromSeconds(20)).Item1;
                        if (code == 0) { restored++; restoredIds.Add(e.Id); }
                        else errors.Add($"引导配置恢复失败：{e.KeyPath}");
                        break;
                    }
                    case "Wevt":
                        if (RunCapture("wevtutil.exe", $"sl \"{e.KeyPath}\" /e:{(e.ValueKind == 1 ? "true" : "false")}", TimeSpan.FromSeconds(20)).Item1 == 0)
                        {
                            restored++;
                            restoredIds.Add(e.Id);
                        }
                        else
                        {
                            errors.Add($"事件日志通道恢复失败：{e.KeyPath}");
                        }

                        break;
                    case "Service":
                    {
                        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + e.KeyPath, writable: true);
                        if (key is not null)
                        {
                            key.SetValue("Start", e.ValueKind, RegistryValueKind.DWord);
                            restored++;
                            restoredIds.Add(e.Id);
                        }
                        else
                        {
                            errors.Add($"服务不存在：{e.KeyPath}");
                        }

                        break;
                    }
                    case "Device":
                    {
                        var result = SetPnpDeviceEnabled(e.KeyPath, e.ValueKind == 1);
                        if (result.Success)
                        {
                            restored++;
                            restoredIds.Add(e.Id);
                        }
                        else
                        {
                            errors.Add($"设备状态恢复失败：{e.KeyPath}（{result.Message}）");
                        }

                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{e.Kind} {e.KeyPath}：{ex.Message}");
            }
        }

        if (taskEntries.Count > 0)
        {
            var enableOps = taskEntries.Where(e => e.ValueKind == 1).Select(e => new BxTweak { TweakType = "TASK", Path = e.KeyPath }).ToList();
            var disableOps = taskEntries.Where(e => e.ValueKind == 0).Select(e => new BxTweak { TweakType = "TASK", Path = e.KeyPath }).ToList();
            if (enableOps.Count > 0)
            {
                var (ok, count) = ApplyTasks(enableOps, enable: true);
                if (ok)
                {
                    restored += count;
                    restoredIds.AddRange(taskEntries.Where(e => e.ValueKind == 1).Select(e => e.Id));
                }
                else
                {
                    errors.Add("计划任务启用恢复失败");
                }
            }

            if (disableOps.Count > 0)
            {
                var (ok, count) = ApplyTasks(disableOps, enable: false);
                if (ok)
                {
                    restored += count;
                    restoredIds.AddRange(taskEntries.Where(e => e.ValueKind == 0).Select(e => e.Id));
                }
                else
                {
                    errors.Add("计划任务禁用恢复失败");
                }
            }
        }

        BxBackupStore.RemoveIds(restoredIds);
        return new BxRestoreSummary(restored, errors.Count, errors);
    });

    internal static object DecodeValue(BxBackupEntry entry) => (RegistryValueKind)entry.ValueKind switch
    {
        RegistryValueKind.DWord => int.Parse(entry.Data!),
        RegistryValueKind.QWord => long.Parse(entry.Data!),
        RegistryValueKind.Binary => HexToBytes(entry.Data!),
        RegistryValueKind.ExpandString => entry.Data!,
        RegistryValueKind.MultiString => entry.StringData ?? DecodeLegacyMultiString(entry.Data),
        _ => entry.Data!,
    };

    private static string[] DecodeLegacyMultiString(string? data) => data == "System.String[]"
        ? throw new FormatException("旧版 REG_MULTI_SZ 备份只保存了类型名，无法安全恢复原内容")
        : (data ?? "").Split(';');

    private static byte[] HexToBytes(string hex)
    {
        hex = hex.Trim();
        if (hex.Length % 2 != 0)
        {
            hex = "0" + hex;
        }

        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        return bytes;
    }

    private static (RegistryKey Hive, string Sub) ParsePath(string path)
    {
        var p = path.Replace("HKEY_LOCAL_MACHINE", "HKLM", StringComparison.OrdinalIgnoreCase)
                    .Replace("HKEY_CURRENT_USER", "HKCU", StringComparison.OrdinalIgnoreCase);
        p = p.TrimStart('\\');
        var idx = p.IndexOf('\\');
        var root = idx < 0 ? p : p[..idx];
        var sub = idx < 0 ? "" : p[(idx + 1)..];
        return (root.Equals("HKLM", StringComparison.OrdinalIgnoreCase) ? Registry.LocalMachine : Registry.CurrentUser, sub);
    }

    // ---------------- 进程调用 ----------------

    private static (int Code, string StdOut) RunCapture(string fileName, string arguments, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return (-1, "");
            }

            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return (-1, "");
            }

            Task.WaitAll(stdoutTask, stderrTask);
            var output = string.Join(Environment.NewLine,
                new[] { stdoutTask.Result.Trim(), stderrTask.Result.Trim() }
                    .Where(text => !string.IsNullOrWhiteSpace(text)));
            return (p.ExitCode, output);
        }
        catch
        {
            return (-1, "");
        }
    }

    internal static int RunCmd(string script, TimeSpan timeout) => RunCmdDetailed(script, timeout).Code;

    private static (int Code, string Error) RunCmdDetailed(string script, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/d /c " + script.Replace("\r\n", " & "),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return (-1, "进程启动失败");
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            try
            {
                p.WaitForExitAsync().WaitAsync(timeout).GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return (-1, "命令超时");
            }
            if (!Task.WaitAll([stdout, stderr], timeout))
                return (-1, "命令输出读取超时");
            var output = string.Join(Environment.NewLine,
                new[] { stdout.Result.Trim(), stderr.Result.Trim() }
                    .Where(text => !string.IsNullOrWhiteSpace(text)));
            return (p.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static (int Code, string? StdOut, string? StdErr) RunPowerShell(string command, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " +
                            "$ErrorActionPreference='SilentlyContinue'; " + command,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return (-1, null, "无法启动 powershell");
            }

            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            p.WaitForExitAsync().WaitAsync(timeout).GetAwaiter().GetResult();
            return (p.ExitCode, stdoutTask.GetAwaiter().GetResult(), null);
        }
        catch
        {
            return (-1, null, null);
        }
    }
}
