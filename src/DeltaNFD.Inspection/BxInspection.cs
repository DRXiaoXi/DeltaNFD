using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using DeltaNFD.Services;
using DeltaNFD.Services.TweakDb;

namespace DeltaNFD.Inspection;
internal enum BxTaskState
{
    Enabled,
    Disabled,
    Missing,
    Unknown
}

/// <summary>Read-only inspection shared by the UI and backend. No system mutations.</summary>
public sealed class BxInspection
{
    internal const string FullHyperVItemName = BxCatalog.FullHyperVItemName;
    private readonly Func<Task<bool?>> _hypervisorQuery;
    public BxInspection(Func<Task<bool?>>? hypervisorQuery = null)
    {
        _hypervisorQuery = hypervisorQuery ?? (() => new SystemTweakInspection().IsHyperVFullyDisabledAsync());
    }

    private sealed record PnpDeviceState(string InstanceId, bool? Enabled);
    public Task<List<string>> ResolveServiceGroupAsync(BxServiceGroup group) => DeltaNFD.Inspection.InspectionScheduler.Run(() => ResolveServiceGroup(group));
    public List<string> ResolveServiceGroup(BxServiceGroup group)
    {
        var names = group.UseBlockedDb ? BxCatalog.AllServices.Where(s => s.IsBlocked || s.IsBlocked11).Select(s => s.ServiceName).ToList() : group.Services.ToList();
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

    public Task<Dictionary<BxItem, BxState>> GetItemStatesAsync(IReadOnlyList<BxItem> items) => DeltaNFD.Inspection.InspectionScheduler.Run(async () =>
    {
        // 批量查询：一次性拉取系统全部计划任务状态，再在内存里逐条评估。
        // 任务状态快照带 2 分钟 TTL 缓存（全库扫描/多栏目连续加载只起一次 PowerShell；
        // ApplyTasks 写操作与显式失效会立即清空）。
        var taskStates = _taskStateCache;
        if (items.Any(i => i.Tweaks.Any(t => t.TweakType == "TASK")) && (!_taskStateCacheValid || DateTime.Now - _taskStateCacheTime > TimeSpan.FromMinutes(2)))
        {
            taskStates = QueryAllTaskStates();
            _taskStateCache = taskStates;
            _taskStateCacheTime = DateTime.Now;
            _taskStateCacheValid = true;
        }

        var result = new Dictionary<BxItem, BxState>();
        bool? fullHyperVStatus = null;
        if (items.Any(i => i.Name == FullHyperVItemName))
        {
            try
            {
                fullHyperVStatus = await _hypervisorQuery();
            }
            catch (Exception ex)
            {
                Log.Error("完整 Hyper-V 状态读取失败", ex);
            }
        }

        foreach (var item in items)
        {
            if (item.Name == FullHyperVItemName)
            {
                result[item] = fullHyperVStatus is null ? BxState.Unknown : fullHyperVStatus.Value ? BxState.On : BxState.Off;
                continue;
            }

            BxTaskState Lookup(string path)
            {
                var normalized = "\\" + path.TrimStart('\\');
                if (taskStates is null)
                    return BxTaskState.Unknown;
                return taskStates.TryGetValue(normalized, out var enabled) ? enabled ? BxTaskState.Enabled : BxTaskState.Disabled : BxTaskState.Missing;
            }

            var diagnostics = new List<string>();
            var state = EvaluateItem(item, Lookup, diagnostics);
            result[item] = state;
            if (diagnostics.Count > 0 || state is BxState.Mixed or BxState.Unknown or BxState.NotApplicable)
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

    public Task InvalidateTaskCacheAsync()
    {
        InvalidateTaskCache();
        return Task.CompletedTask;
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
            var(on, off) = MatchReg(op);
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
            if (powerDisabled == true)
                anyOn = true;
            else if (powerDisabled == false)
                anyOff = true;
            else
            {
                anyUnknown = true;
                diagnostics.Add("USB 选择性暂停状态查询失败");
            }
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
            else if (state == BxTaskState.Missing)
            {
                anyMissing = true;
                diagnostics.Add($"计划任务不存在（不适用，跳过，不计为执行失败）：\\{op.Path.TrimStart('\\')}");
            }
            else
            {
                anyUnknown = true;
                diagnostics.Add($"计划任务状态查询失败：\\{op.Path.TrimStart('\\')}（批量任务快照不可用）");
            }
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
            else
            {
                anyUnknown = true;
                diagnostics.Add($"事件日志通道状态查询失败或通道不存在：{wevtOps[0].Path}");
            }
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
                if (key?.GetValue("Start")is not int start)
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

            if (!TryGetDeviceEnabledTarget(op, turnOn: true, out var appliedEnabled) || !TryGetDeviceEnabledTarget(op, turnOn: false, out var defaultEnabled))
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

                var(appliedCount, totalCount) = CountRegEnumInstances(op, appliedEntry);
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
                else
                {
                    anyUnknown = true;
                    diagnostics.Add($"应用包状态查询失败：{op.Path}");
                }
            }
        }

        if (!hasOp)
        {
            diagnostics.Add("条目没有本工具可评估的操作");
            return BxState.Unknown;
        }

        if (anyUnknown)
            return BxState.Unknown;
        if (anyMissing && !anyOn && !anyOff)
            return BxState.NotApplicable;
        if (anyOn && !anyOff)
        {
            return BxState.On;
        }

        if (anyOff && !anyOn)
        {
            return BxState.Off;
        }

        if (anyOn && anyOff)
            return BxState.Mixed;
        diagnostics.Add("没有任何操作提供可用的状态值");
        return BxState.Unknown;
    }

    public Task<BxState> GetServiceGroupStateAsync(BxServiceGroup group) => DeltaNFD.Inspection.InspectionScheduler.Run(() =>
    {
        var services = ExpandServicePatterns(group.UseBlockedDb ? BxCatalog.AllServices.Where(s => s.IsBlocked || s.IsBlocked11).Select(s => s.ServiceName) : group.Services);
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
            if (key?.GetValue("Start")is not int start)
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

        var state = disabled == existing.Count ? BxState.On : disabled == 0 ? BxState.Off : BxState.Mixed;
        if (state == BxState.Mixed)
            Log.Info($"服务组状态判定：{group.Id} → 混合；已禁用={disabled}/{existing.Count}");
        return state;
    });
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

    private static BxValueEntry? PickDefaultEntry(BxTweak op) => PickValue(op, v => v.Contains("DEFAULT")) ?? PickValue(op, v => v.Contains("OFF"));
    private static BxValueEntry? PickAppliedEntry(BxTweak op) => PickValue(op, v => !v.Contains("DEFAULT"));
    private static BxValueEntry? PickValue(BxTweak op, Func<IEnumerable<string>, bool> predicate) => op.Values.FirstOrDefault(v => predicate(v.ValueTypes));
    private static object? ReadReg(BxTweak op)
    {
        var(hive, sub) = ParsePath(op.Path);
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

    public Task<BxGearInfo?> GetGearInfoAsync(BxItem item) => DeltaNFD.Inspection.InspectionScheduler.Run(() =>
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

        return options.Count >= 2 ? new BxGearInfo
        {
            Options = options,
            CurrentIndex = currentIndex
        }

        : null;
    });
    private static bool IsGearItem(BxItem item) => item.Tweaks.Count == 1 && item.Tweaks[0].Values.Count >= 2;
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
            var(code, stdout, _) = RunPowerShell("$ErrorActionPreference='Stop'; " + "Get-AppxPackage -AllUsers -ErrorAction Stop | ForEach-Object { Write-Output $_.Name }; " + "Get-AppxProvisionedPackage -Online -ErrorAction Stop | ForEach-Object { Write-Output $_.DisplayName }; " + "Write-Output '__BX_APPX_OK__'", TimeSpan.FromMinutes(3));
            if (code != 0 || stdout is null || !stdout.Contains("__BX_APPX_OK__", StringComparison.Ordinal))
            {
                return null;
            }

            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line != "__BX_APPX_OK__")
                    set.Add(line);
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

    public Task InvalidateAppxCacheAsync()
    {
        InvalidateAppxCache();
        return Task.CompletedTask;
    }

    private static bool? IsAppxInstalled(string pattern)
    {
        var set = GetInstalledAppx();
        if (set is null)
            return null;
        return MatchesAppxPattern(set, pattern);
    }

    internal static bool MatchesAppxPattern(IEnumerable<string> names, string pattern)
    {
        if (!pattern.Contains('*'))
            return names.Contains(pattern, StringComparer.OrdinalIgnoreCase);
        var regex = new System.Text.RegularExpressions.Regex('^' + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + '$', System.Text.RegularExpressions.RegexOptions.IgnoreCase);
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

    private static bool IsSafePnpSelector(string selector) => !string.IsNullOrWhiteSpace(selector) && selector.Length <= 512 && selector.All(c => (c is >= 'A' and <= 'Z') || (c is >= 'a' and <= 'z') || (c is >= '0' and <= '9') || c is '\\' or '&' or '_' or '.' or '-');
    private static List<PnpDeviceState>? QueryPnpDeviceStates(string selector)
    {
        if (!IsSafePnpSelector(selector))
            return null;
        var safeSelector = selector.Replace("'", "''");
        var script = "$ErrorActionPreference='Stop'; try { " + "$selector='" + safeSelector + "'; " + "$devices = @(Get-PnpDevice -ErrorAction Stop | Where-Object { $_.InstanceId -and " + "($_.InstanceId -ieq $selector -or $_.InstanceId.StartsWith($selector + [char]92, [System.StringComparison]::OrdinalIgnoreCase)) }); " + "if ($devices.Count -eq 0) { Write-Output 'NOT_FOUND'; exit 0 }; " + "foreach ($device in $devices) { " + "$state = 'UNKNOWN'; " + "try { $problem = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_ProblemCode' -ErrorAction Stop).Data; " + "if ($problem -eq 22) { $state = 'DISABLED' } elseif ($problem -eq 0) { $state = 'ENABLED' } } catch { }; " + "if ($state -eq 'UNKNOWN') { if ([string]$device.Status -eq 'OK') { $state = 'ENABLED' } " + "elseif ([string]$device.Problem -match '(^|[^0-9])22([^0-9]|$)|DISABLED') { $state = 'DISABLED' } }; " + "Write-Output ($state + '|' + $device.InstanceId) } " + "} catch { Write-Output ('ERROR|' + $_.Exception.Message); exit 1 }";
        var(code, stdout, _) = RunPowerShell(script, TimeSpan.FromSeconds(30));
        if (code != 0 || string.IsNullOrWhiteSpace(stdout) || stdout.Contains("ERROR|", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn($"PnP 设备状态查询失败：selector={selector}；exitCode={code}；output={stdout?.Trim()}");
            return null;
        }

        var states = new List<PnpDeviceState>();
        foreach (var line in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var value = line.Trim();
            if (value.Equals("NOT_FOUND", StringComparison.OrdinalIgnoreCase))
                return[];
            var separator = value.IndexOf('|');
            if (separator <= 0 || separator == value.Length - 1)
                continue;
            var status = value[..separator];
            bool? enabled = status.Equals("ENABLED", StringComparison.OrdinalIgnoreCase) ? true : status.Equals("DISABLED", StringComparison.OrdinalIgnoreCase) ? false : null;
            states.Add(new PnpDeviceState(value[(separator + 1)..], enabled));
        }

        return states;
    }

    private static BxState QueryHpetBcdState()
    {
        var(code, output) = RunCapture("bcdedit.exe", "/enum {current}", TimeSpan.FromSeconds(20));
        if (code != 0 || string.IsNullOrWhiteSpace(output))
        {
            Log.Warn($"HPET 引导状态查询失败：bcdedit 退出码={code}；输出={output.Trim()}");
            return BxState.Unknown;
        }

        bool IsEnabled(string key) => output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim().Split((char[]? )null, StringSplitOptions.RemoveEmptyEntries)).Any(parts => parts.Length >= 2 && parts[0].Equals(key, StringComparison.OrdinalIgnoreCase) && parts.Skip(1).Any(value => value.Equals("Yes", StringComparison.OrdinalIgnoreCase)));
        var dynamicTick = IsEnabled("disabledynamictick");
        var platformTick = IsEnabled("useplatformtick");
        if (dynamicTick && platformTick)
            return BxState.On;
        if (!dynamicTick && !platformTick)
            return BxState.Off;
        return BxState.Mixed;
    }

    private static bool? QueryCmdItemState(string itemName)
    {
        switch (itemName)
        {
            case "HandwritingEN":
            {
                var(_, stdout, _) = RunPowerShell("Get-WindowsCapability -Online | Where-Object { $_.Name -like 'Language.Handwriting*en-US*' } | ForEach-Object { Write-Output $_.State }", TimeSpan.FromSeconds(60));
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
                    if (locations.Any(File.Exists))
                        return false;
                    foreach (var root in new[]
                    {
                        Registry.CurrentUser,
                        Registry.LocalMachine
                    }

                    )
                    {
                        foreach (var suffix in new[]
                        {
                            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                        }

                        )
                        {
                            using var uninstall = root.OpenSubKey(suffix);
                            if (uninstall is null)
                                continue;
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

    private const string PowerSchemesPath = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
    private const string UsbSubgroup = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string UsbSuspendSetting = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
    private static bool? QueryUsbSelectiveSuspendDisabled()
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(PowerSchemesPath);
            var scheme = root?.GetValue("ActivePowerScheme")?.ToString();
            if (!Guid.TryParse(scheme, out _))
                return null;
            using var setting = Registry.LocalMachine.OpenSubKey($@"{PowerSchemesPath}\{scheme}\{UsbSubgroup}\{UsbSuspendSetting}");
            return setting?.GetValue("ACSettingIndex")is int ac && ac == 0 && setting.GetValue("DCSettingIndex")is int dc && dc == 0;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsRegType(string t) => t is "REG" or "REG_SZ" or "REG_BINARY" or "REG_EXPAND_SZ" or "REG_MULTI_SZ" or "REG_DWORD";
    private static List<string> EnumerateNumSubKeys(string path)
    {
        var result = new List<string>();
        var(hive, sub) = ParsePath(path);
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
                if (inst?.GetValue("*IfType")is not null)
                {
                    result.Add(name);
                }
            }
        }

        return result;
    }

    private static (int Applied, int Total) CountRegEnumInstances(BxTweak op, BxValueEntry appliedEntry)
    {
        var(hive, sub) = ParsePath(op.Path);
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

    private static Dictionary<string, bool>? QueryAllTaskStates()
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var(code, stdout, stderr) = RunPowerShell("$ErrorActionPreference='Stop'; Get-ScheduledTask -ErrorAction Stop | ForEach-Object { [string]$_.TaskPath + [string]$_.TaskName + '|' + [string]$_.State }; Write-Output '__BX_TASK_OK__'", TimeSpan.FromMinutes(2));
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

    private static bool? QueryWevtEnabled(string channel)
    {
        var(code, stdout) = RunCapture("wevtutil.exe", $"gl \"{channel}\"", TimeSpan.FromSeconds(20));
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

    private static (RegistryKey Hive, string Sub) ParsePath(string path)
    {
        var p = path.Replace("HKEY_LOCAL_MACHINE", "HKLM", StringComparison.OrdinalIgnoreCase).Replace("HKEY_CURRENT_USER", "HKCU", StringComparison.OrdinalIgnoreCase);
        p = p.TrimStart('\\');
        var idx = p.IndexOf('\\');
        var root = idx < 0 ? p : p[..idx];
        var sub = idx < 0 ? "" : p[(idx + 1)..];
        return (root.Equals("HKLM", StringComparison.OrdinalIgnoreCase) ? Registry.LocalMachine : Registry.CurrentUser, sub);
    }

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
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return (-1, "");
            }

            Task.WaitAll(stdoutTask, stderrTask);
            var output = string.Join(Environment.NewLine, new[] { stdoutTask.Result.Trim(), stderrTask.Result.Trim() }.Where(text => !string.IsNullOrWhiteSpace(text)));
            return (p.ExitCode, output);
        }
        catch
        {
            return (-1, "");
        }
    }

    private static (int Code, string? StdOut, string? StdErr) RunPowerShell(string command, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " + "$ErrorActionPreference='SilentlyContinue'; " + command,
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
