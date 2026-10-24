using DeltaNFD.Inspection;
using DeltaNFD.Services.TweakDb;

namespace DeltaNFD.Services;

/// <summary>Startup read-only preload. States are cached individually; there is no optimization score.</summary>
public static class OptimizationScan
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, BxState> Items = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, BxState> Groups = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<SystemTweak, TweakStatus> Tweaks = [];
    private static Task _scan = Task.CompletedTask;
    private static TaskCompletionSource _deepReady = Ready();
    private static TaskCompletionSource Ready() { var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); ready.SetResult(); return ready; }
    private static bool _started;
    private static int _generation;
    private static int _itemsEpoch, _groupsEpoch, _tweaksEpoch;
    public static bool IsScanning { get; private set; }
    public static string ProgressText { get; private set; } = "等待预读优化项目";
    public static DateTime? CompletedTime { get; private set; }
    public static string Error { get; private set; } = "";
    public static bool HasData { get { lock (Gate) return Items.Count > 0 || Groups.Count > 0 || Tweaks.Count > 0; } }
    public static event Action? Changed;
    public static Task RunIfNotScannedAsync() { lock (Gate) return _started ? _scan : RunAsync(); }
    public static Task RunAsync()
    {
        lock (Gate)
        {
            if (IsScanning) return _scan;
            _started = IsScanning = true; Error = "";
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _deepReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _scan = completion.Task;
            _ = ReadAllAsync(_generation, completion);
            return _scan;
        }
    }
    private static async Task ReadAllAsync(int generation, TaskCompletionSource completion)
    {
        var errors = new List<string>();
        bool Current() { lock (Gate) return generation == _generation; }
        try
        {
            ProgressText = "正在预读深度优化项目…"; FireChanged();
            try
            {
                int epoch; lock (Gate) epoch = _tweaksEpoch;
                var states = await new SystemTweakInspection().GetStatusesAsync();
                lock (Gate) if (generation == _generation && epoch == _tweaksEpoch) foreach (var state in states) Tweaks[state.Tweak] = state;
            }
            catch (Exception ex) { errors.Add("深度优化：" + ex.Message); }
            _deepReady.TrySetResult(); FireChanged();
            var reader = new BxInspection();
            foreach (var group in BxCatalog.ServiceGroups)
            {
                if (!Current()) return;
                ProgressText = "正在预读服务组：" + group.Name; FireChanged();
                BxState state;
                int epoch; lock (Gate) epoch = _groupsEpoch;
                try { state = await reader.GetServiceGroupStateAsync(group); }
                catch (Exception ex) { state = BxState.Unknown; errors.Add(group.Name + "：" + ex.Message); }
                lock (Gate) if (generation == _generation && epoch == _groupsEpoch) Groups[group.Id] = state;
                try { await SystemInspectionCache.ResolvedServices(group); } catch (Exception ex) { errors.Add(group.Name + "服务明细：" + ex.Message); }
                FireChanged();
            }
            bool? hdd = null;
            try { var drive = (await SystemInspectionCache.Drives()).FirstOrDefault(d => d.IsSystemDrive); hdd = drive is null || !drive.IsMechanical && !drive.IsSolidState ? null : drive.IsMechanical; }
            catch (Exception ex) { errors.Add("系统盘信息：" + ex.Message); }
            foreach (var section in BxCatalog.Sections.Where(s => s.JsonFile is not null))
            {
                if (!Current()) return;
                ProgressText = "正在预读优化项目：" + section.Title; FireChanged();
                if (!BxCatalog.Database.TryGetValue(section.Id, out var entries)) { errors.Add(section.Title + "：优化数据库未读取，待核对"); continue; }
                Dictionary<BxItem, BxState> states;
                int epoch; lock (Gate) epoch = _itemsEpoch;
                try { states = await reader.GetItemStatesAsync(entries); }
                catch (Exception ex) { states = entries.ToDictionary(item => item, _ => BxState.Unknown); errors.Add(section.Title + "：" + ex.Message); }
                lock (Gate)
                    if (generation == _generation && epoch == _itemsEpoch)
                        foreach (var item in entries)
                            Items[Key(section.Id, item.Name)] = BxCatalog.GetUnavailableReason(item, Environment.OSVersion.Version.Build, hdd) is not null
                                ? BxState.NotApplicable : states.GetValueOrDefault(item, BxState.Unknown);
                FireChanged();
                foreach (var item in entries)
                    try { await SystemInspectionCache.Gear(item); } catch (Exception ex) { errors.Add(item.Name + "档位：" + ex.Message); }
            }
            try { await SystemInspectionCache.PreloadAsync(); } catch (Exception ex) { errors.Add("辅助状态：" + ex.Message); }
            lock (Gate) if (generation == _generation) CompletedTime = DateTime.Now;
        }
        catch (Exception ex) { errors.Add(ex.Message); }
        finally
        {
            lock (Gate)
            {
                IsScanning = false;
                if (generation == _generation)
                {
                    Error = string.Join("；", errors);
                    var unknown = Items.Values.Count(s => s == BxState.Unknown) + Groups.Values.Count(s => s == BxState.Unknown) + Tweaks.Values.Count(s => !s.StateKnown);
                    ProgressText = errors.Count == 0 ? unknown == 0 ? "优化项目预读完成" : $"优化项目预读完成，{unknown} 项状态待核对" : "预读部分完成，读取失败的项目需核对";
                }
            }
            _deepReady.TrySetResult(); completion.TrySetResult(); FireChanged();
        }
    }
    public static async Task<Dictionary<BxItem, BxState>> ReadItemsAsync(IReadOnlyList<BxItem> items)
    {
        Task scan; lock (Gate) scan = _scan;
        if (!scan.IsCompleted) await scan;
        var result = new Dictionary<BxItem, BxState>(); var missing = new List<BxItem>();
        lock (Gate) foreach (var item in items)
            if (Items.TryGetValue(Key(item.SectionId, item.Name), out var state)) result[item] = state; else missing.Add(item);
        if (missing.Count > 0)
        {
            var fresh = await new BxInspection().GetItemStatesAsync(missing);
            foreach (var item in missing) { result[item] = fresh.GetValueOrDefault(item, BxState.Unknown); SetItemState(item.SectionId, item.Name, result[item]); }
        }
        return result;
    }
    public static async Task<BxState> ReadGroupAsync(BxServiceGroup group)
    {
        Task scan; lock (Gate) scan = _scan;
        if (!scan.IsCompleted) await scan;
        if (TryGetGroupState(group.Id, out var state)) return state;
        state = await new BxInspection().GetServiceGroupStateAsync(group); SetGroupState(group.Id, state); return state;
    }
    public static void Invalidate()
    {
        lock (Gate) { _generation++; _started = false; Items.Clear(); Groups.Clear(); Tweaks.Clear(); CompletedTime = null; }
        SystemInspectionCache.Invalidate(); FireChanged();
    }
    internal static async Task<List<TweakStatus>> ReadTweaksAsync()
    {
        Task scan; lock (Gate) scan = _deepReady.Task;
        if (!scan.IsCompleted) await scan;
        lock (Gate) if (Tweaks.Count == Enum.GetValues<SystemTweak>().Length) return Tweaks.Values.ToList();
        var states = await new SystemTweakInspection().GetStatusesAsync();
        lock (Gate) foreach (var state in states) Tweaks[state.Tweak] = state;
        return states;
    }
    internal static void ForgetItem(string section, string name)
    {
        lock (Gate) { _itemsEpoch++; Items.Remove(Key(section, name)); }
        SystemInspectionCache.ForgetGear(section, name);
        var changed = BxCatalog.Database.GetValueOrDefault(section)?.FirstOrDefault(i => i.Name == name);
        if (changed is not null)
        {
            var paths = changed.Tweaks.Select(TweakAddress).ToHashSet(StringComparer.OrdinalIgnoreCase);
            lock (Gate)
                foreach (var pair in BxCatalog.Database)
                    foreach (var item in pair.Value.Where(i => i.Tweaks.Any(t => paths.Contains(TweakAddress(t))))) Items.Remove(Key(pair.Key, item.Name));
        }
        lock (Gate) { _tweaksEpoch++; Tweaks.Clear(); } // Seven deep checks share one native read; aliases must be rechecked together.
        FireChanged();
    }
    internal static void ForgetGroups() { lock (Gate) { _groupsEpoch++; Groups.Clear(); } FireChanged(); }
    private static string TweakAddress(BxTweak tweak)
    {
        var parts = tweak.Path.Split('\\', 2);
        var hive = parts[0].ToUpperInvariant() switch { "HKEY_LOCAL_MACHINE" => "HKLM", "HKEY_CURRENT_USER" => "HKCU", _ => parts[0] };
        return hive + (parts.Length > 1 ? "\\" + parts[1] : "") + "|" + tweak.Key;
    }
    internal static void ForgetTweaks() { lock (Gate) { _tweaksEpoch++; Tweaks.Clear(); } FireChanged(); }
    private static string Key(string section, string name) => section + "|" + name;
    public static bool TryGetItemState(string section, string name, out BxState state) { lock (Gate) return Items.TryGetValue(Key(section, name), out state); }
    public static bool TryGetGroupState(string id, out BxState state) { lock (Gate) return Groups.TryGetValue(id, out state); }
    internal static bool TryGetTweakStatus(SystemTweak tweak, out TweakStatus? state) { lock (Gate) return Tweaks.TryGetValue(tweak, out state); }
    public static bool TryGetTweakState(SystemTweak tweak, out bool enabled)
    {
        lock (Gate) { enabled = Tweaks.TryGetValue(tweak, out var state) && state.IsOptimized; return state is not null && !Uncertain(state); }
    }
    internal static bool Uncertain(TweakStatus state) => !state.StateKnown;
    public static void SetItemState(string section, string name, BxState state) { lock (Gate) { _itemsEpoch++; Items[Key(section, name)] = state; } FireChanged(); }
    public static void SetGroupState(string id, BxState state) { lock (Gate) { _groupsEpoch++; Groups[id] = state; } FireChanged(); }
    public static void SetTweakState(SystemTweak tweak, bool enabled) { lock (Gate) if (!Tweaks.TryGetValue(tweak, out var state) || state.IsOptimized != enabled) { _tweaksEpoch++; Tweaks.Remove(tweak); } FireChanged(); }
    private static void FireChanged()
    {
        foreach (Action handler in Changed?.GetInvocationList() ?? []) try { handler(); } catch (Exception ex) { Log.Error("优化预读状态订阅失败", ex); }
    }
}
