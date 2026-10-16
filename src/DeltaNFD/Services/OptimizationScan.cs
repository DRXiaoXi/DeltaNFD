using System.Diagnostics;
using DeltaNFD.Services.TweakDb;

namespace DeltaNFD.Services;

/// <summary>
/// 主页「优化检测」：一次性扫描系统优化的全部优化项目（扩展优化库全栏目 + 服务组 + 深度优化），
/// 计算优化分数（已应用 / 总数 × 100）。扫描结果作为静态缓存顺延到系统优化页——
/// 各栏目加载时优先读取缓存状态，缺失条目才单独查询，加速视觉加载（HANDOFF §25）。
/// 应用更改 / 一键恢复后由 BxDbViewModel 回写 / 作废缓存，保证分数与真实状态一致。
/// </summary>
public static class OptimizationScan
{
    private static readonly object _gate = new();
    private static readonly Dictionary<string, BxState> _itemStates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, BxState> _groupStates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<SystemTweak, bool> _tweakStates = new();

    /// <summary>扫描进行中。</summary>
    public static bool IsScanning { get; private set; }

    /// <summary>扫描进度文本（"正在检测：安全性（2/10）…"）。</summary>
    public static string ProgressText { get; private set; } = "";

    /// <summary>优化分数（0-100；= 已应用项目 / 可统计项目）。</summary>
    public static double Score { get; private set; }

    /// <summary>已应用的项目数。</summary>
    public static int Applied { get; private set; }

    /// <summary>可统计的项目总数（状态未知 / 不支持的条目不计入）。</summary>
    public static int Total { get; private set; }

    /// <summary>本次扫描完成时间；null = 尚未完成过。</summary>
    public static DateTime? CompletedTime { get; private set; }

    /// <summary>是否存在可顺延的扫描缓存。</summary>
    public static bool HasData => _itemStates.Count > 0 || _groupStates.Count > 0 || _tweakStates.Count > 0;

    /// <summary>扫描进度 / 分数变化通知（主页卡订阅；页面退订防泄漏）。</summary>
    public static event Action? Changed;

    /// <summary>本会话尚未扫描过时启动一次扫描（幂等）。</summary>
    public static Task RunIfNotScannedAsync() => HasData || IsScanning ? Task.CompletedTask : RunAsync();

    /// <summary>执行全量扫描（重入安全；进行中时调用直接返回）。</summary>
    public static async Task RunAsync()
    {
        lock (_gate)
        {
            if (IsScanning)
            {
                return;
            }

            IsScanning = true;
        }

        Log.Info($"优化检测：开始全量扫描（扩展优化库 + 服务组 + 深度优化）；WindowsBuild={Environment.OSVersion.Version.Build}");
        var sw = Stopwatch.StartNew();
        try
        {
            lock (_gate)
            {
                _itemStates.Clear();
                _groupStates.Clear();
                _tweakStates.Clear();
                CompletedTime = null;
            }

            // 深度优化 20 项（纯注册表读，成本极低）
            var tweaks = await ServiceLocator.SystemTweaks.GetStatusesAsync();
            lock (_gate)
            {
                foreach (var s in tweaks)
                {
                    _tweakStates[s.Tweak] = s.IsOptimized;
                    Log.Info($"优化检测判定：深度优化/{s.Tweak} → {(s.IsOptimized ? "已优化" : "未优化")}");
                }
            }

            ProgressText = "深度优化检测完成";
            FireChanged();

            // 服务组（逐组读服务注册表）
            var bx = ServiceLocator.Bx;
            var groups = BxCatalog.ServiceGroups;
            for (var i = 0; i < groups.Length; i++)
            {
                ProgressText = $"正在检测服务组：{groups[i].Name}（{i + 1}/{groups.Length}）";
                FireChanged();
                var state = await bx.GetServiceGroupStateAsync(groups[i]);
                Log.Info($"优化检测判定：服务组/{groups[i].Id} → {state}");
                lock (_gate)
                {
                    _groupStates[groups[i].Id] = state;
                }

                FireChanged();
            }

            // 扩展优化库全栏目（批量：任务状态一次拉取；wevtutil / 注册表逐条）
            var sections = BxCatalog.Sections.Where(s => s.JsonFile is not null).ToList();
            var systemDrive = DriveInspector.GetFixedDriveReports().FirstOrDefault(d => d.IsSystemDrive);
            bool? systemDriveIsHdd = systemDrive is null || (!systemDrive.IsMechanical && !systemDrive.IsSolidState)
                ? null : systemDrive.IsMechanical;
            Log.Info($"优化检测环境：系统盘={(systemDrive is null ? "未识别" : systemDrive.Letter + ":")}；IsHdd={systemDriveIsHdd?.ToString() ?? "未知"}");
            for (var i = 0; i < sections.Count; i++)
            {
                var section = sections[i];
                ProgressText = $"正在检测：{section.Title}（{i + 1}/{sections.Count}）";
                FireChanged();

                if (!BxCatalog.Database.TryGetValue(section.Id, out var items) || items.Count == 0)
                {
                    Log.Warn($"优化检测跳过栏目：{section.Id}/{section.Title}；原因=数据库条目缺失或为空");
                    continue;
                }

                var states = await bx.GetItemStatesAsync(items);
                lock (_gate)
                {
                    foreach (var (item, state) in states)
                    {
                        var unavailable = BxCatalog.GetUnavailableReason(item, Environment.OSVersion.Version.Build, systemDriveIsHdd);
                        var finalState = unavailable is null ? state : BxState.NotApplicable;
                        _itemStates[section.Id + "|" + item.Name] = finalState;
                        if (unavailable is not null)
                        {
                            Log.Info($"优化检测适用性判定：{section.Id}/{item.Name} → 不适用；WindowsBuild={Environment.OSVersion.Version.Build}；原因={unavailable}；NoSupport=[{string.Join(",", item.NoSupport)}]");
                        }
                        else if (state is BxState.Mixed or BxState.Unknown or BxState.NotApplicable)
                        {
                            Log.Info($"优化检测状态判定：{section.Id}/{item.Name} → {state}；依据见扩展库状态诊断");
                        }
                    }
                }

                FireChanged();
            }

            CompletedTime = DateTime.Now;
            ProgressText = "检测完成";
            string stateSummary;
            lock (_gate)
            {
                var itemsSummary = string.Join("，", Enum.GetValues<BxState>().Select(state => $"{state}={_itemStates.Values.Count(value => value == state)}"));
                var groupsSummary = string.Join("，", Enum.GetValues<BxState>().Select(state => $"{state}={_groupStates.Values.Count(value => value == state)}"));
                var tweaksSummary = $"已优化={_tweakStates.Values.Count(value => value)}，未优化={_tweakStates.Values.Count(value => !value)}";
                stateSummary = $"条目[{itemsSummary}]；服务组[{groupsSummary}]；深度优化[{tweaksSummary}]";
            }
            Log.Info($"优化检测：完成（{sw.ElapsedMilliseconds} ms）；条目状态：{stateSummary}");
        }
        catch (Exception ex)
        {
            ProgressText = "检测失败：" + ex.Message;
            Log.Error("优化检测：全量扫描失败", ex);
        }
        finally
        {
            lock (_gate)
            {
                IsScanning = false;
                RecalculateLocked();
            }

            FireChanged();
        }
    }

    /// <summary>作废全部扫描缓存（「刷新状态」/ 一键恢复后调用）。</summary>
    public static void Invalidate()
    {
        lock (_gate)
        {
            _itemStates.Clear();
            _groupStates.Clear();
            _tweakStates.Clear();
            CompletedTime = null;
            Score = 0;
            Applied = 0;
            Total = 0;
        }

        FireChanged();
    }

    public static bool TryGetItemState(string sectionId, string itemName, out BxState state)
    {
        lock (_gate)
        {
            return _itemStates.TryGetValue(sectionId + "|" + itemName, out state);
        }
    }

    public static void SetItemState(string sectionId, string itemName, BxState state)
    {
        lock (_gate)
        {
            _itemStates[sectionId + "|" + itemName] = state;
            RecalculateLocked();
        }

        FireChanged();
    }

    public static bool TryGetGroupState(string groupId, out BxState state)
    {
        lock (_gate)
        {
            return _groupStates.TryGetValue(groupId, out state);
        }
    }

    public static void SetGroupState(string groupId, BxState state)
    {
        lock (_gate)
        {
            _groupStates[groupId] = state;
            RecalculateLocked();
        }

        FireChanged();
    }

    public static bool TryGetTweakState(SystemTweak tweak, out bool optimized)
    {
        lock (_gate)
        {
            return _tweakStates.TryGetValue(tweak, out optimized);
        }
    }

    public static void SetTweakState(SystemTweak tweak, bool optimized)
    {
        lock (_gate)
        {
            _tweakStates[tweak] = optimized;
            RecalculateLocked();
        }

        FireChanged();
    }

    /// <summary>重算分数（调用方须持有 _gate 锁）。</summary>
    private static void RecalculateLocked()
    {
        var applied = 0;
        var total = 0;
        foreach (var state in _itemStates.Values)
        {
            if (state is BxState.Unknown or BxState.NotApplicable)
            {
                continue; // 不支持 / 无操作的条目不参与统计
            }

            total++;
            if (state == BxState.On)
            {
                applied++;
            }
        }

        foreach (var state in _groupStates.Values)
        {
            if (state is BxState.Unknown or BxState.NotApplicable)
            {
                continue;
            }

            total++;
            if (state == BxState.On)
            {
                applied++;
            }
        }

        foreach (var optimized in _tweakStates.Values)
        {
            total++;
            if (optimized)
            {
                applied++;
            }
        }

        Applied = applied;
        Total = total;
        Score = total > 0 ? Math.Round(applied * 100.0 / total) : 0;
    }

    private static void FireChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // 订阅方异常不影响扫描
        }
    }
}
