using System.Collections.ObjectModel;
using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using DeltaNFD.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DeltaNFD.Views;

/// <summary>
/// 进程管理页（P1）：列表（1 秒轮询，隐藏即停）+ 详情（身份复核加载）+ 手动控制。
/// 刷新保持选中行按「PID＋启动时间＋路径」匹配——PID 被复用时选中消失并明确提示，
/// 绝不让选中对象悄悄变成新进程。修改走后端 procman 模块（写前记录 + 回读确认）。
/// </summary>
public sealed partial class ProcessPage : Page
{
    private const int PageSize = 300;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly ObservableCollection<ProcessRowVm> _rows = [];
    private readonly ObservableCollection<CapabilityVm> _capabilities = [];
    private readonly ObservableCollection<HeldChangeVm> _held = [];
    private readonly DispatcherTimer _timer = new() { Interval = PollInterval };

    // 当前选中进程的身份三元组（不是 PID——刷新后按身份重找行）。
    private ProcessRowVm? _selected;
    private ProcmanTopologyResult? _topology;
    private string _search = "";
    private bool _suppressSelection;
    private bool _detachedFromWindow;
    private bool _listBusy, _listQueued;
    private long _listGeneration;
    private int _offset;
    private readonly List<ToggleButton> _processorToggles = [];
    private string _loadedDetailIdentity = "";
    private string _lastAffinityValue = "";
    private string _ruleTargetPath = "";
    private bool? _wideWorkspace;
    private bool _propertyBusy;
    private List<ProcmanRuleEntry> _savedRules = [];
    private bool IsAlways => ApplyScopePicker.SelectedIndex == 1;

    private async void ApplyScopePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ApplyScopeNote is null || ResetSelectedButton is null) return;
        ApplyScopeNote.Text = IsAlways
            ? "总是：按完整应用路径保存规则，作用于当前及以后启动的实例。点击某项应用按钮只保存该项；没有规则的选项先显示当前实际值。"
            : "当前：只调整选中的本次进程，不新增或修改总是规则。已有规则的属性需要确认接管。";
        AlwaysTakeoverCheck.Visibility = IsAlways ? Visibility.Visible : Visibility.Collapsed;
        ResetSelectedButton.Content = IsAlways ? "移除此应用的总是规则" : "还原当前进程修改";
        if (_selected is { } row)
            try { await LoadDetailAsync(row, syncControls: true); }
            catch (Exception ex) { ShowResult(false, "属性读取未完成：" + ex.Message); }
    }

    private void ShowAlwaysSettings()
    {
        if (_selected is not { } row) return;
        var rules = _savedRules.Where(r => r.ExecutablePath.Equals(row.ExecutablePath, StringComparison.OrdinalIgnoreCase)).ToList();
        SelectedAlwaysSummary.Text = rules.Count == 0 ? "此应用尚无总是规则。"
            : "已保存：" + string.Join("；", rules.Select(r => CapabilityTitle(r.Capability) + " · " + (r.Enabled ? "总是" : "已停用")));
        if (!IsAlways) return;
        foreach (var rule in rules.Where(r => r.Enabled))
        {
            switch (rule.Capability)
            {
                case "priority": PriorityCombo.SelectedIndex = rule.Value switch { "Idle" => 0, "BelowNormal" => 1, "Normal" => 2, "AboveNormal" => 3, "High" => 4, _ => -1 }; break;
                case "ecoQos": EcoQosToggle.IsOn = rule.Value == "true"; break;
                case "priorityBoost": PriorityBoostToggle.IsOn = rule.Value == "True"; break;
                case "ioPriority": IoPriorityCombo.SelectedIndex = rule.Value switch { "VeryLow" => 0, "Low" => 1, "Normal" => 2, "High" => 3, _ => -1 }; break;
                case "memoryPriority": MemoryPriorityCombo.SelectedIndex = int.TryParse(rule.Value, out var memory) && memory is >= 1 and <= 5 ? memory - 1 : -1; break;
                case "gpuPriority": GpuPriorityCombo.SelectedIndex = int.TryParse(rule.Value, out var gpu) && gpu is >= 0 and <= 4 ? gpu : -1; break;
            }
        }
        var maskRule = rules.FirstOrDefault(r => r.Enabled && r.Capability == "affinity")
            ?? rules.FirstOrDefault(r => r.Enabled && r.Capability == "cpuSet");
        if (maskRule is not null && maskRule.Value.Length > 0) SetAffinityToggles(maskRule.Value, primaryOnly: false);
    }

    private void ProcessWorkspace_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= 860;
        if (_wideWorkspace == wide) return;
        _wideWorkspace = wide;
        ListColumn.Width = new GridLength(wide ? 0.8 : 1, GridUnitType.Star);
        DetailColumn.Width = wide ? new GridLength(1.2, GridUnitType.Star) : new GridLength(0);
        ListRow.Height = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(260);
        DetailRow.Height = wide ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(DetailCard, wide ? 1 : 0);
        Grid.SetRow(DetailCard, wide ? 0 : 1);
    }

    public ProcessPage()
    {
        InitializeComponent();
        RebuildRuleValues();
        ProcessList.ItemsSource = _rows;
        CapabilityList.ItemsSource = _capabilities;
        HeldList.ItemsSource = _held;
        _timer.Tick += (_, _) => { if (AutoRefreshToggle.IsChecked == true) _ = RefreshListAsync(preserveSelection: true); };
        Loaded += (_, _) =>
        {
            _detachedFromWindow = false;
            _timer.Start();
            _ = RefreshListAsync(preserveSelection: false);
            _ = LoadTopologyAsync();
            _ = LoadRulesAsync();
        };
        Unloaded += (_, _) => StopPolling();
    }

    private void StopPolling()
    {
        _timer.Stop();
        _detachedFromWindow = true; // 页面隐藏：降低为手动刷新（规则引擎在后端独立运行，不受影响）
        _listGeneration++;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text.Trim();
        _offset = 0; _listGeneration++;
        _selected = null; ShowEmptyDetail();
        _ = RefreshListAsync(preserveSelection: false);
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => _ = RefreshListAsync(preserveSelection: true);

    // ---------------- 列表 ----------------

    private async Task RefreshListAsync(bool preserveSelection)
    {
        if (_detachedFromWindow) return;
        if (_listBusy) { _listQueued = true; return; }
        _listBusy = true;
        var generation = _listGeneration;
        try
        {
        ProcmanListResult result;
        try
        {
            result = await BackendProcmanService.Instance.ListAsync(_offset, PageSize,
                _search.Length == 0 ? null : _search);
        }
        catch (Exception ex) { ListNoteText.Text = "本次读取失败：" + ex.Message; return; }
        if (_detachedFromWindow || generation != _listGeneration) { _listQueued = true; return; }

        var rows = result.Processes.Select(p => new ProcessRowVm
        {
            Name = p.Name,
            Pid = p.Pid,
            StartTimeUtcTicks = p.StartTimeUtcTicks,
            ExecutablePath = p.ExecutablePath,
            WorkingSetMb = p.WorkingSetMb,
            CpuPercent = p.CpuPercent,
            OwnedPropertyCount = p.OwnedPropertyCount,
            Protected = IsProtectedPath(p.ExecutablePath),
        }).ToList();

        _suppressSelection = true;
        try
        {
            if (preserveSelection)
            {
                // Replace snapshots in place instead of resetting the entire list and scroll position.
                for (var i = 0; i < rows.Count; i++)
                    if (i < _rows.Count) _rows[i] = rows[i]; else _rows.Add(rows[i]);
                while (_rows.Count > rows.Count) _rows.RemoveAt(_rows.Count - 1);
            }
            else
            {
                _rows.Clear();
                foreach (var row in rows) _rows.Add(row);
            }

            // 选中保持：按完整身份重找（PID 复用/进程退出 → 选中消失，明确提示）
            ProcessRowVm? restored = null;
            if (preserveSelection && _selected is { } previous)
            {
                restored = rows.FirstOrDefault(r => r.Pid == previous.Pid
                    && r.StartTimeUtcTicks == previous.StartTimeUtcTicks
                    && string.Equals(r.ExecutablePath, previous.ExecutablePath, StringComparison.OrdinalIgnoreCase));
                if (restored is null && previous is not null)
                {
                    _selected = null;
                    ShowIdentityWarning("选中的进程已退出或 PID 已被新进程复用，已取消选中（不会误选新进程）。");
                    ShowEmptyDetail();
                }
            }
            _selected = restored;
            ProcessList.SelectedItem = restored;
            if (restored is null) ShowEmptyDetail();
        }
        finally { _suppressSelection = false; }

        ListNoteText.Text = result.Note;
        ListSummaryText.Text = $"共 {result.TotalCount} 个进程 · 第 {_offset / PageSize + 1} 页";
        PreviousPageButton.IsEnabled = _offset > 0;
        NextPageButton.IsEnabled = _offset + PageSize < result.TotalCount;
        if (_selected is { } selected) _ = LoadDetailAsync(selected);
        }
        finally
        {
            _listBusy = false;
            if (_listQueued && !_detachedFromWindow) { _listQueued = false; _ = RefreshListAsync(preserveSelection); }
        }
    }

    private void PreviousPage_Click(object sender, RoutedEventArgs e) { _offset = Math.Max(0, _offset - PageSize); _listGeneration++; _ = RefreshListAsync(false); }
    private void NextPage_Click(object sender, RoutedEventArgs e) { _offset += PageSize; _listGeneration++; _ = RefreshListAsync(false); }

    private static bool IsProtectedPath(string path)
    {
        try
        {
            if (path.Length == 0) return true;
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (name.StartsWith("DeltaNFD", StringComparison.OrdinalIgnoreCase)) return true;
            string[] critical = ["system", "lsass", "services", "winlogon", "smss", "csrss", "svchost", "dwm", "explorer", "wininit", "Memory Compression"];
            if (critical.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
            var windows = System.IO.Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) + System.IO.Path.DirectorySeparatorChar;
            return path.StartsWith(windows, StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    private async Task LoadTopologyAsync()
    {
        _topology = await BackendProcmanService.Instance.GetTopologyAsync();
        TopologyNoteText.Text = _topology is { Groups.Count: > 0 }
            ? $"本机处理器组：{string.Join("；", _topology.Groups.Select(g => $"组 {g.Group}（0x{g.Mask:X}）"))}。按处理器组设置；P/E 核与 NUMA 拓扑无法识别时不猜测标签。"
            : "处理器组拓扑读取失败：亲和性只能整组设置，不做猜测。";
        RebuildAffinityPanel();
        if (_lastAffinityValue.Length > 0) SetAffinityToggles(_lastAffinityValue, primaryOnly: true);
        if (IsAlways) ShowAlwaysSettings();
    }

    private void RebuildAffinityPanel()
    {
        AffinityGroupPanel.Children.Clear();
        _processorToggles.Clear();
        if (_topology is not { Groups.Count: > 0 })
        {
            var note = new TextBlock { Text = "拓扑不可用，亲和性设置已禁用。", FontSize = 11, Opacity = 0.7 };
            AffinityGroupPanel.Children.Add(note);
            return;
        }
        foreach (var group in _topology.Groups)
        {
            var panel = new StackPanel { Spacing = 8 };
            var label = new TextBlock
            {
                Text = $"组 {group.Group}",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 48,
            };
            var toggles = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
            for (var col = 0; col < 8; col++) toggles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var position = 0;
            // 按组内逻辑处理器编号生成开关（≤64/组）；掩码 0x1 << n
            for (var bit = 0; bit < 64; bit++)
            {
                var bitMask = 1UL << bit;
                if ((group.Mask & bitMask) == 0) continue;
                var toggle = new ToggleButton
                {
                    Content = bit.ToString(),
                    MinWidth = 0, MinHeight = 32, Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Stretch,
                    Tag = new ProcmanGroupMask(group.Group, bitMask),
                };
                if (position % 8 == 0) toggles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetColumn(toggle, position % 8); Grid.SetRow(toggle, position / 8); position++;
                ToolTipService.SetToolTip(toggle, $"处理器组 {group.Group} · 逻辑 CPU {bit}");
                _processorToggles.Add(toggle);
                toggles.Children.Add(toggle);
            }
            panel.Children.Add(label);
            var all = new Button { Content = "选择整组", FontSize = 12 };
            all.Click += (_, _) => { foreach (var toggle in EnumerateAffinityToggles(group.Group)) toggle.IsChecked = true; };
            panel.Children.Add(all);
            panel.Children.Add(toggles);
            AffinityGroupPanel.Children.Add(panel);
        }
    }

    // ---------------- 选中与详情 ----------------

    private void ProcessList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        _selected = ProcessList.SelectedItem as ProcessRowVm;
        if (_selected is null) { ShowEmptyDetail(); return; }
        _lastAffinityValue = "";
        AlwaysTakeoverCheck.IsChecked = false;
        SavedRuleEditor.Visibility = Visibility.Collapsed;
        SetRuleTarget(_selected.ExecutablePath);
        ShowAlwaysSettings();
        _ = LoadDetailAsync(_selected, syncControls: true);
    }

    private void ShowEmptyDetail()
    {
        DetailPanel.Visibility = Visibility.Collapsed;
        DetailEmptyPanel.Visibility = Visibility.Visible;
    }

    private async Task LoadDetailAsync(ProcessRowVm row, bool syncControls = false)
    {
        var identity = new ProcessIdentityArgs { Pid = row.Pid, StartTimeUtcTicks = row.StartTimeUtcTicks, ExecutablePath = row.ExecutablePath };
        var detail = await BackendProcmanService.Instance.DetailAsync(identity);
        if (_selected is not { } selected || selected.Pid != row.Pid || selected.StartTimeUtcTicks != row.StartTimeUtcTicks
            || !selected.ExecutablePath.Equals(row.ExecutablePath, StringComparison.OrdinalIgnoreCase) || _detachedFromWindow) return;

        if (detail is null || !detail.IdentityOk)
        {
            ShowIdentityWarning(detail?.IdentityError ?? "详情读取失败（后端不可达）。");
            return;
        }

        DetailEmptyPanel.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        DetailIdentityWarning.IsOpen = false;

        DetailNameText.Text = detail.Process.Name;
        DetailPathText.Text = detail.Process.ExecutablePath;
        DetailMetaText.Text = $"PID {detail.Process.Pid} · 内存 {(detail.Process.WorkingSetMb is { } mb ? $"{mb:0.#} MB" : "未知")}"
            + (IsProtectedPath(detail.Process.ExecutablePath) ? " · 受保护（只读）" : "");

        _capabilities.Clear();
        foreach (var capability in detail.Capabilities)
            _capabilities.Add(new CapabilityVm
            {
                Title = CapabilityTitle(capability.Capability),
                Support = capability.Support,
                CurrentValue = capability.CurrentValue,
                Owner = capability.Owner,
                Error = capability.Error,
            });

        _held.Clear();
        foreach (var held in detail.HeldChanges)
            _held.Add(new HeldChangeVm
            {
                Capability = CapabilityTitle(held.Capability),
                OriginalValue = held.OriginalValue,
                AppliedValue = held.AppliedValue,
                Owner = held.Owner,
                AppliedUtc = held.AppliedUtc,
            });
        HeldPanel.Visibility = _held.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var protectedProcess = IsProtectedPath(detail.Process.ExecutablePath);
        ControlPanel.Visibility = protectedProcess ? Visibility.Collapsed : Visibility.Visible;
        AdditionalControlPanel.Visibility = ControlPanel.Visibility;
        if (protectedProcess) return;

        _lastAffinityValue = detail.Capabilities.FirstOrDefault(c => c.Capability == "affinity")?.CurrentValue ?? "";

        var key = $"{row.Pid}:{row.StartTimeUtcTicks}:{row.ExecutablePath}";
        var synchronize = syncControls || _loadedDetailIdentity != key;
        _loadedDetailIdentity = key;
        // Periodic snapshots update status, but must not erase selections the user is editing.
        if (!synchronize) return;

        // 把当前实际值同步到控件（配置值与实际值分离：控件只呈现实际值）
        var priority = detail.Capabilities.FirstOrDefault(c => c.Capability == "priority");
        if (priority is { Support: "supported", Error.Length: 0 })
            PriorityCombo.SelectedIndex = priority.CurrentValue switch
            {
                "Idle" => 0, "BelowNormal" => 1, "Normal" => 2, "AboveNormal" => 3, "High" => 4, _ => 2,
            };

        var boost = detail.Capabilities.FirstOrDefault(c => c.Capability == "priorityBoost");
        if (boost is not null && boost.Error.Length == 0)
            PriorityBoostToggle.IsOn = boost.CurrentValue == "True";
        var eco = detail.Capabilities.FirstOrDefault(c => c.Capability == "ecoQos");
        EcoQosToggle.IsEnabled = eco is { Support: "supported", Error.Length: 0 };
        PriorityBoostToggle.IsEnabled = boost is { Support: "supported", Error.Length: 0 };
        if (eco is { Error.Length: 0 } && eco.CurrentValue.Split(':') is ["eco", _, var ecoState] && uint.TryParse(ecoState, out var ecoValue))
            EcoQosToggle.IsOn = (ecoValue & 1) != 0;
        var io = detail.Capabilities.FirstOrDefault(c => c.Capability == "ioPriority");
        if (io is { Error.Length: 0 }) IoPriorityCombo.SelectedIndex = io.CurrentValue switch { "VeryLow" => 0, "Low" => 1, "Normal" => 2, "High" => 3, _ => -1 };
        var preference = detail.Capabilities.FirstOrDefault(c => c.Capability == "gpuPreference");
        if (preference is { Error.Length: 0 }) GpuPreferenceCombo.SelectedIndex = preference.CurrentValue switch { "Windows 默认" => 0, "省电" => 1, "高性能" => 2, _ => -1 };
        var memory = detail.Capabilities.FirstOrDefault(c => c.Capability == "memoryPriority");
        MemoryPriorityCombo.IsEnabled = memory is { Support: "supported", Error.Length: 0 };
        MemoryPriorityCombo.SelectedIndex = memory is { Error.Length: 0 } && int.TryParse(memory.CurrentValue, out var memoryValue)
            && memoryValue is >= 1 and <= 5 ? memoryValue - 1 : -1;
        var gpu = detail.Capabilities.FirstOrDefault(c => c.Capability == "gpuPriority");
        GpuPriorityCombo.IsEnabled = gpu is { Support: "supported", Error.Length: 0 };
        if (gpu is { Error.Length: 0 } && int.TryParse(gpu.CurrentValue, out var gpuValue)) GpuPriorityCombo.SelectedIndex = gpuValue is >= 0 and <= 4 ? gpuValue : -1;

        // 亲和性开关：读取当前值后设置（多组进程只呈现主组；逐组开关由用户操作）
        var affinity = detail.Capabilities.FirstOrDefault(c => c.Capability == "affinity");
        if (affinity is { Error.Length: 0 })
        {
            SetAffinityToggles(affinity.CurrentValue, primaryOnly: true);
        }
        ShowAlwaysSettings();
    }

    private void SetAffinityToggles(string value, bool primaryOnly)
    {
        // 值格式：g{n}:0x{mask}（多组时有后缀说明）。只把掩码同步到对应组开关。
        foreach (var toggle in _processorToggles) toggle.IsChecked = false;
        foreach (var part in value.Split('（')[0].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = part.Trim();
            if (segment.StartsWith("g", StringComparison.OrdinalIgnoreCase) && !segment.StartsWith("group", StringComparison.OrdinalIgnoreCase)) segment = "group" + segment[1..];
            if (!segment.StartsWith("group", StringComparison.OrdinalIgnoreCase) || !segment.Contains(':')) continue;
            var split = segment.Split(':', 2);
            if (!ushort.TryParse(split[0]["group".Length..], out var group)) continue;
            var maskText = split[1].Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
            if (!ulong.TryParse(maskText, System.Globalization.NumberStyles.HexNumber, null, out var mask)) continue;
            foreach (var toggle in EnumerateAffinityToggles(group))
                toggle.IsChecked = (mask & ((ProcmanGroupMask)toggle.Tag!).Mask) != 0;
            if (primaryOnly) break;
        }
    }

    private IEnumerable<ToggleButton> EnumerateAffinityToggles(ushort group) =>
        _processorToggles
            .Where(t => t.Tag is ProcmanGroupMask m && m.Group == group);

    private static string CapabilityTitle(string capability) => capability switch
    {
        "priority" => "CPU 优先级",
        "affinity" => "CPU 亲和性",
        "cpuSet" => "CPU 集合",
        "memoryPriority" => "内存优先级",
        "ecoQos" => "效率模式（EcoQoS）",
        "priorityBoost" => "动态优先级提升",
        "ioPriority" => "I/O 优先级",
        "gpuPriority" => "GPU 调度优先级",
        "gpuPreference" => "GPU 首选项",
        _ => capability,
    };

    private void ShowIdentityWarning(string message)
    {
        ShowEmptyDetail();
        DetailIdentityWarning.Message = message;
        DetailIdentityWarning.IsOpen = true;
    }

    private void ShowResult(bool success, string message)
    {
        OperationResultBar.Severity = success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        OperationResultBar.Message = message;
        OperationResultBar.IsOpen = true;
    }

    private ProcessIdentityArgs? CurrentIdentity()
    {
        if (_selected is not { } row) { ShowResult(false, "请先选择进程。"); return null; }
        return new ProcessIdentityArgs { Pid = row.Pid, StartTimeUtcTicks = row.StartTimeUtcTicks, ExecutablePath = row.ExecutablePath };
    }

    // ---------------- 手动控制 ----------------

    private async void ApplyPriority_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentIdentity() is not { } identity) return;
        var priority = PriorityCombo.SelectedIndex switch
        {
            0 => "Idle", 1 => "BelowNormal", 2 => "Normal", 3 => "AboveNormal", 4 => "High", _ => null,
        };
        if (priority is null) { ShowResult(false, "未知优先级档位。"); return; }
        await ApplySetAsync(new ProcmanSetArgs { Identity = identity, CpuPriority = priority }, "CPU 优先级");
    }

    private async void ApplyAffinity_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentIdentity() is not { } identity) return;
        var masks = CollectAffinityMasks();
        if (masks.Count == 0) { ShowResult(false, "请至少勾选一个核心（或使用解除限制）。"); return; }
        await ApplySetAsync(new ProcmanSetArgs { Identity = identity, AffinityMasks = masks }, "CPU 亲和性");
    }

    private async void ClearAffinity_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentIdentity() is not { } identity) return;
        // 解除 = 还原该进程可见的全部处理器（后端按系统组掩码设置）
        if (_topology is not { Groups.Count: > 0 }) { ShowResult(false, "处理器组拓扑不可用。"); return; }
        var masks = _topology.Groups.Select(g => new ProcmanGroupMask(g.Group, g.Mask)).ToList();
        await ApplySetAsync(new ProcmanSetArgs { Identity = identity, AffinityMasks = [] }, "解除亲和性限制");
    }

    private List<ProcmanGroupMask> CollectAffinityMasks()
    {
        var byGroup = new Dictionary<ushort, ulong>();
        foreach (var toggle in _processorToggles)
        {
            if (toggle.Tag is not ProcmanGroupMask m || toggle.IsChecked != true) continue;
            byGroup[m.Group] = byGroup.TryGetValue(m.Group, out var existing) ? existing | m.Mask : m.Mask;
        }
        return byGroup.Select(kv => new ProcmanGroupMask(kv.Key, kv.Value)).ToList();
    }

    private async void ApplyToggles_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentIdentity() is not { } identity) return;
        await ApplySetAsync(new ProcmanSetArgs
        {
            Identity = identity,
            PriorityBoost = PriorityBoostToggle.IsEnabled ? PriorityBoostToggle.IsOn : null,
            EcoQosEnabled = EcoQosToggle.IsEnabled ? EcoQosToggle.IsOn : null,
        }, "开关设置");
    }

    private async void ApplyIoPriority_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentIdentity() is not { } identity) return;
        var value = IoPriorityCombo.SelectedIndex switch
        {
            0 => "VeryLow", 1 => "Low", 2 => "Normal", 3 => "High", _ => null,
        };
        if (value is null) { ShowResult(false, "未知 I/O 优先级档位。"); return; }
        await ApplySetAsync(new ProcmanSetArgs { Identity = identity, IoPriority = value }, "I/O 优先级");
    }

    private async void ApplyCpuSet_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentIdentity() is not { } identity) return;
        var masks = CollectAffinityMasks();
        if (masks.Count == 0) { ShowResult(false, "请至少选择一个 CPU；解除设置请使用“清除 CPU 集合”。"); return; }
        await ApplySetAsync(new() { Identity = identity, CpuSetMasks = masks }, "CPU 集合");
    }
    private async void ClearCpuSet_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentIdentity() is { } identity) await ApplySetAsync(new() { Identity = identity, CpuSetMasks = [] }, "清除 CPU 集合");
    }
    private async void ApplyMemoryPriority_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentIdentity() is { } identity && MemoryPriorityCombo.IsEnabled && MemoryPriorityCombo.SelectedIndex is >= 0 and <= 4)
            await ApplySetAsync(new() { Identity = identity, MemoryPriority = MemoryPriorityCombo.SelectedIndex + 1 }, "内存优先级");
    }
    private async void ApplyGpuPriority_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentIdentity() is { } identity && GpuPriorityCombo.IsEnabled && GpuPriorityCombo.SelectedIndex >= 0)
            await ApplySetAsync(new() { Identity = identity, GpuPriority = GpuPriorityCombo.SelectedIndex }, "GPU 调度优先级");
    }

    private async void ApplyGpuPreference_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentIdentity() is not { } identity) return;
        var value = GpuPreferenceCombo.SelectedIndex switch
        {
            0 => "Windows 默认", 1 => "省电", 2 => "高性能", _ => null,
        };
        if (value is null) { ShowResult(false, "未知 GPU 首选项。"); return; }
        await ApplySetAsync(new ProcmanSetArgs { Identity = identity, GpuPreference = value }, "GPU 首选项");
    }

    private async Task ApplySetAsync(ProcmanSetArgs args, string what)
    {
        if (_propertyBusy) return;
        _propertyBusy = true;
        var always = IsAlways && args.GpuPreference is null;
        var autoTakeover = AlwaysTakeoverCheck.IsChecked == true;
        DetailControlsHost.IsEnabled = ProcessList.IsEnabled = false;
        try
        {
            if (always) await SaveAlwaysSettingsAsync(args, what, autoTakeover);
            else await ApplyCurrentSetAsync(args, what);
        }
        catch (Exception ex) { ShowResult(false, what + "未完成：" + ex.Message); }
        finally
        {
            _propertyBusy = false;
            DetailControlsHost.IsEnabled = ProcessList.IsEnabled = true;
        }
    }

    private async Task SaveAlwaysSettingsAsync(ProcmanSetArgs args, string what, bool autoTakeover)
    {
        var rules = ProcmanRuleProjection.Create(args, autoTakeover);
        var saved = new List<string>();
        foreach (var rule in rules)
        {
            var result = await BackendProcmanService.Instance.InvokeAsync(BackendCommands.ProcmanRulesSave,
                new ProcmanRulesSaveArgs { Rule = rule });
            if (result.Status != BackendResultStatuses.Ok)
            {
                ShowResult(false, "总是设置未全部保存：" + result.Message
                    + (saved.Count > 0 ? "\n已保存：" + string.Join("、", saved) : ""));
                await LoadRulesAsync();
                return;
            }
            saved.Add(CapabilityTitle(rule.Capability));
        }
        await LoadRulesAsync();
        if (_selected is { } selected) await LoadDetailAsync(selected, syncControls: true);
        ShowResult(true, what + "已保存为总是规则。后台会应用到当前及未来实例，实际生效状态请查看“当前属性与控制方”。");
    }

    private async Task ApplyCurrentSetAsync(ProcmanSetArgs args, string what)
    {
        OperationResultBar.Message = $"正在应用{what}…";
        OperationResultBar.Severity = InfoBarSeverity.Informational;
        OperationResultBar.IsOpen = true;
        try
        {
            var result = await BackendProcmanService.Instance.SetAsync(args);
            if (result is null) { ShowResult(false, $"{what}未完成：后端无响应。"); return; }
            if (!result.IdentityOk) { ShowResult(false, $"{what}被拒绝：{result.IdentityError}"); return; }
            if (args.ConfirmedTakeover != true && result.Outcomes.Any(o => o.Status == "rejected" && o.Message.Contains("控制")))
            {
                var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "接管当前进程的属性？",
                    Content = string.Join("\n", result.Outcomes.Where(o => o.Status == "rejected").Select(o => o.Message)) + "\n只暂停冲突属性；还原后归还原控制方。仅授权当前进程实例。",
                    PrimaryButtonText = "接管当前实例", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
                if (await dialog.ShowWithBackendRecoveryAsync() == ContentDialogResult.Primary) { args.ConfirmedTakeover = true; await ApplyCurrentSetAsync(args, what); }
                return;
            }
            var applied = result.Outcomes.Where(o => o.Status == "applied").ToList();
            var rejected = result.Outcomes.Where(o => o.Status == "rejected").ToList();
            var failed = result.Outcomes.Where(o => o.Status == "failed").ToList();
            if (applied.Count > 0 && rejected.Count == 0 && failed.Count == 0)
                ShowResult(true, $"{what}已生效（回读一致）：{string.Join("；", applied.Select(o => o.Capability == "memoryPriority" ? ProcessPriorityDisplay.Memory(o.ActualValue) : o.ActualValue))}");
            else
                ShowResult(false, string.Join("；", result.Outcomes.Select(o => o.Message).Where(m => m.Length > 0)) +
                    (applied.Count > 0 ? $"（已生效 {applied.Count} 项）" : ""));
            if (_selected is { } row) await LoadDetailAsync(row, syncControls: true);
            _ = RefreshListAsync(preserveSelection: true);
        }
        catch (Exception ex)
        {
            ShowResult(false, $"{what}未完成：{ex.Message}");
        }
    }

    private async void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        if (_propertyBusy) return;
        if (CurrentIdentity() is not { } identity) return;
        if (IsAlways)
        {
            await RemoveAlwaysSettingsAsync(identity.ExecutablePath);
            return;
        }
        try
        {
            var result = await BackendProcmanService.Instance.ResetAsync(new ProcmanResetArgs { Identity = identity });
            if (result is null || !result.IdentityOk)
            {
                ShowResult(false, "还原未完成：" + (result?.IdentityError ?? "无响应"));
                return;
            }
            var restored = result.Outcomes.Count(o => o.Status == "restored");
            var conflict = result.Outcomes.Count(o => o.Status == "conflict");
            var failed = result.Outcomes.Count(o => o.Status == "failed");
            ShowResult(restored > 0 && conflict == 0 && failed == 0,
                $"还原：成功 {restored}，冲突 {conflict}，失败 {failed}。" +
                (conflict > 0 ? "冲突记录已保留，请人工核对（不盲目覆盖）。" : ""));
            if (_selected is { } row) await LoadDetailAsync(row, syncControls: true);
            _ = RefreshListAsync(preserveSelection: true);
        }
        catch (Exception ex)
        {
            ShowResult(false, "还原未完成：" + ex.Message);
        }
    }

    private async Task RemoveAlwaysSettingsAsync(string path)
    {
        _propertyBusy = true;
        DetailControlsHost.IsEnabled = ProcessList.IsEnabled = false;
        try
        {
            var result = await BackendProcmanService.Instance.InvokeAsync(BackendCommands.ProcmanRulesList, new { });
            if (result.Status != BackendResultStatuses.Ok || result.Data is not { } data)
            { ShowResult(false, "无法读取总是规则：" + result.Message); return; }
            var rules = BackendJson.Deserialize<ProcmanRulesListResult>(data).Rules
                .Where(r => r.ExecutablePath.Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (rules.Length == 0) { ShowResult(true, "此应用没有总是规则，无需移除。"); return; }
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "移除此应用的总是规则？",
                Content = Path.GetFileName(path) + "\n" + path + "\n\n" + string.Join("\n", rules.Select(r => CapabilityTitle(r.Capability)))
                    + "\n\n将移除以上规则并恢复规则持有的修改。单独的当前设置和其他应用规则保留；恢复冲突时保留记录。",
                PrimaryButtonText = "移除总是规则", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowWithBackendRecoveryAsync() != ContentDialogResult.Primary) return;
            var removed = 0;
            foreach (var rule in rules)
            {
                var deleted = await BackendProcmanService.Instance.InvokeAsync(BackendCommands.ProcmanRulesDelete,
                    new ProcmanRulesDeleteArgs { ExecutablePath = path, Capability = rule.Capability });
                if (deleted.Status != BackendResultStatuses.Ok)
                { ShowResult(false, $"已移除 {removed} 条，剩余规则未全部移除：" + deleted.Message); await LoadRulesAsync(); return; }
                removed++;
            }
            await LoadRulesAsync();
            if (_selected is { } selected) await LoadDetailAsync(selected, syncControls: true);
            ShowResult(true, $"已移除此应用的 {removed} 条总是规则。独立的当前设置保持原样。");
        }
        catch (Exception ex) { ShowResult(false, "移除总是规则未完成：" + ex.Message); }
        finally
        {
            _propertyBusy = false;
            DetailControlsHost.IsEnabled = ProcessList.IsEnabled = true;
        }
    }

    private async void Reconcile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await BackendProcmanService.Instance.ReconcileAsync();
            ReconcileText.Text = result.Data is { } data
                ? BackendJson.Deserialize<ProcmanReconcileResult>(data).Summary
                : result.Message;
        }
        catch (Exception ex)
        {
            ReconcileText.Text = "核对未完成：" + ex.Message;
        }
    }

    private async void RestoreAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await BackendProcmanService.Instance.RestoreAllAsync();
            ShowResult(result.Status == BackendResultStatuses.Ok, result.Message);
        }
        catch (Exception ex)
        {
            ShowResult(false, "全部还原未完成：" + ex.Message);
        }
    }

    // ---------------- P2：持久规则 ----------------

    private void SetRuleTarget(string path)
    {
        _ruleTargetPath = path;
        RuleTargetNameText.Text = path.Length == 0 ? "尚未选择目标进程" : Path.GetFileName(path);
        RuleTargetPathText.Text = path.Length == 0 ? "选择进程后即可创建规则。" : path;
    }

    private void RuleCapabilityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RuleValueCombo is not null) RebuildRuleValues();
    }

    private void RebuildRuleValues(string? selectedValue = null)
    {
        var mask = RuleCapabilityCombo.SelectedIndex is 3 or 4;
        RuleMaskPanel.Visibility = mask ? Visibility.Visible : Visibility.Collapsed;
        RuleValueCombo.Visibility = mask ? Visibility.Collapsed : Visibility.Visible;
        RuleValueCombo.Items.Clear();
        (string Label, string Value)[] choices = RuleCapabilityCombo.SelectedIndex switch
        {
            0 => [("低", "Idle"), ("低于正常", "BelowNormal"), ("正常", "Normal"), ("高于正常", "AboveNormal"), ("高", "High")],
            1 => [("开启效率模式", "true"), ("关闭效率模式", "false")],
            2 => [("启用动态提升", "True"), ("关闭动态提升", "False")],
            5 => [("很低", "VeryLow"), ("低", "Low"), ("正常", "Normal"), ("高", "High")],
            6 => [("很低", "1"), ("低", "2"), ("中等", "3"), ("低于正常", "4"), ("正常（最高可用）", "5")],
            7 => [("低", "0"), ("低于正常", "1"), ("正常", "2"), ("高于正常", "3"), ("高", "4")],
            _ => [],
        };
        foreach (var choice in choices) RuleValueCombo.Items.Add(new ComboBoxItem { Content = choice.Label, Tag = choice.Value });
        if (mask) { if (selectedValue is not null) RuleValueBox.Text = selectedValue; return; }
        var index = Array.FindIndex(choices, c => c.Value.Equals(selectedValue, StringComparison.OrdinalIgnoreCase));
        if (index < 0) index = RuleCapabilityCombo.SelectedIndex switch { 0 or 5 or 7 => 2, 6 => 4, _ => 0 };
        RuleValueCombo.SelectedIndex = choices.Length == 0 ? -1 : index;
    }

    private void UseSelectedCpus_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || !_selected.ExecutablePath.Equals(_ruleTargetPath, StringComparison.OrdinalIgnoreCase))
        { RuleNoteText.Text = "请先选择规则目标进程，再勾选其 CPU。"; return; }
        var masks = CollectAffinityMasks();
        RuleValueBox.Text = string.Join(";", masks.Select(m => $"g{m.Group}:0x{m.Mask:X}"));
        if (masks.Count == 0) RuleNoteText.Text = "请至少选择一个 CPU。";
    }

    private void GoToCpuSelection_Click(object sender, RoutedEventArgs e)
    {
        ProcessTabs.SelectedIndex = 0;
        AffinityExpander.IsExpanded = true;
    }

    private void LoadRules_Click(object sender, RoutedEventArgs e) => _ = LoadRulesAsync();

    private async Task LoadRulesAsync()
    {
        try
        {
            var result = await BackendProcmanService.Instance.InvokeAsync(BackendCommands.ProcmanRulesList, new { });
            if (result.Data is not { } data) { RuleNoteText.Text = "规则读取失败：" + result.Message; return; }
            var rules = BackendJson.Deserialize<ProcmanRulesListResult>(data);
            _savedRules = rules.Rules;
            RuleListPanel.Children.Clear();
            foreach (var rule in rules.Rules)
            {
                var line = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 8, 0, 8) };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var text = new TextBlock
                {
                    Text = $"{Path.GetFileName(rule.ExecutablePath)} · {CapabilityTitle(rule.Capability)} = {(rule.Capability == "memoryPriority" ? ProcessPriorityDisplay.Memory(rule.Value) : rule.Value)}" +
                           (rule.Enabled ? "（总是）" : "（已停用）"),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                ToolTipService.SetToolTip(text, rule.ExecutablePath);
                var delete = new Button { Content = "删除", Tag = rule, MinWidth = 56 };
                var edit = new Button { Content = "编辑", Tag = rule, MinWidth = 56 };
                edit.Click += (_, _) =>
                {
                    AlwaysRulesExpander.IsExpanded = true;
                    SavedRuleEditor.Visibility = Visibility.Visible;
                    SetRuleTarget(rule.ExecutablePath);
                    RuleCapabilityCombo.SelectedIndex = rule.Capability switch
                    { "priority" => 0, "ecoQos" => 1, "priorityBoost" => 2, "affinity" => 3, "cpuSet" => 4, "ioPriority" => 5, "memoryPriority" => 6, "gpuPriority" => 7, _ => -1 };
                    RebuildRuleValues(rule.Value); RuleEnabledToggle.IsOn = rule.Enabled;
                    RuleAutoTakeoverCheck.IsChecked = rule.AutoTakeover;
                    RuleNoteText.Text = "已载入该规则，调整后点击保存。";
                };
                Grid.SetColumn(edit, 1); Grid.SetColumn(delete, 2);
                delete.Click += RuleDelete_Click;
                line.Children.Add(text);
                line.Children.Add(edit);
                line.Children.Add(delete);
                RuleListPanel.Children.Add(line);
            }
            RuleCountText.Text = rules.Rules.Count == 0 ? "暂无规则" : $"已保存 {rules.Rules.Count} 条规则";
            // Refresh badges only; list refresh must not overwrite an in-progress property edit.
            if (_selected is { } selected)
                SelectedAlwaysSummary.Text = string.Join("；", _savedRules.Where(r => r.ExecutablePath.Equals(selected.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    .Select(r => CapabilityTitle(r.Capability) + (r.Enabled ? " · 总是" : " · 已停用")));
        }
        catch (Exception ex)
        {
            RuleNoteText.Text = "规则读取失败：" + ex.Message;
        }
    }

    private async void SaveRule_Click(object sender, RoutedEventArgs e)
    {
        if (_ruleTargetPath.Length == 0)
        {
            RuleNoteText.Text = "请先选择应用或编辑已有总是规则。";
            return;
        }
        var capability = RuleCapabilityCombo.SelectedIndex switch
        {
            0 => "priority", 1 => "ecoQos", 2 => "priorityBoost", 3 => "affinity", 4 => "cpuSet", 5 => "ioPriority", 6 => "memoryPriority", 7 => "gpuPriority", _ => null,
        };
        if (capability is null) { RuleNoteText.Text = "请选择能力。"; return; }
        var value = RuleCapabilityCombo.SelectedIndex is 3 or 4 ? RuleValueBox.Text.Trim()
            : (RuleValueCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        if (value.Length == 0) { RuleNoteText.Text = "请选择规则设置，CPU 规则需至少选择一个处理器。"; return; }

        var rule = new ProcmanRuleEntry
        {
            ExecutablePath = _ruleTargetPath,
            Capability = capability,
            Value = value,
            Enabled = RuleEnabledToggle.IsOn,
            AutoTakeover = RuleAutoTakeoverCheck.IsChecked == true,
        };
        try
        {
            var result = await BackendProcmanService.Instance.InvokeAsync(BackendCommands.ProcmanRulesSave,
                new ProcmanRulesSaveArgs { Rule = rule });
            RuleNoteText.Text = result.Status == BackendResultStatuses.Ok
                ? "规则已保存" + (rule.Enabled ? "（引擎会在后台应用到当前与未来实例）" : "（已停用）") + "。"
                : "规则保存失败：" + result.Message;
            await LoadRulesAsync();
        }
        catch (Exception ex)
        {
            RuleNoteText.Text = "规则保存未完成：" + ex.Message;
        }
    }

    private async void RuleDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProcmanRuleEntry rule }) return;
        try
        {
            var result = await BackendProcmanService.Instance.InvokeAsync(BackendCommands.ProcmanRulesDelete,
                new ProcmanRulesDeleteArgs { ExecutablePath = rule.ExecutablePath, Capability = rule.Capability });
            RuleNoteText.Text = result.Status == BackendResultStatuses.Ok
                ? "规则已删除，其拥有的修改已还原。"
                : "删除失败：" + result.Message;
            await LoadRulesAsync();
        }
        catch (Exception ex)
        {
            RuleNoteText.Text = "删除未完成：" + ex.Message;
        }
    }

    // ---------------- P4：后台抑制 ----------------

    private async void SaveResponsiveness_Click(object sender, RoutedEventArgs e)
    {
        var config = new ProcmanResponsivenessConfigArgs
        {
            Enabled = ResponsivenessToggle.IsOn,
            CpuBusyThreshold = (int)Math.Clamp(BusyThresholdBox.Value, 10, 99),
            CpuIdleThreshold = (int)Math.Clamp(IdleThresholdBox.Value, 5, 99),
            MaxAdjustPerRound = (int)Math.Clamp(MaxAdjustBox.Value, 1, 4),
            CooldownSeconds = (int)Math.Clamp(CooldownBox.Value, 0, 600),
        };
        if (config.CpuBusyThreshold <= config.CpuIdleThreshold)
        {
            ResponsivenessNoteText.Text = "触发阈值必须高于恢复阈值。";
            return;
        }
        try
        {
            var result = await BackendProcmanService.Instance.InvokeAsync(BackendCommands.ProcmanResponsivenessConfig, config);
            ResponsivenessNoteText.Text = result.Status == BackendResultStatuses.Ok
                ? (config.Enabled ? "已启用（保守策略；关闭后立即恢复全部调整）。" : "已保存（保持关闭）。")
                : "保存失败：" + result.Message;
        }
        catch (Exception ex)
        {
            ResponsivenessNoteText.Text = "保存未完成：" + ex.Message;
        }
    }

    private async void LoadResponsivenessEvents_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await BackendProcmanService.Instance.InvokeAsync(BackendCommands.ProcmanResponsivenessEvents, new { });
            if (result.Data is not { } data) { ResponsivenessNoteText.Text = "事件读取失败：" + result.Message; return; }
            var events = BackendJson.Deserialize<ProcmanResponsivenessEventResult>(data);
            ResponsivenessEventsText.Text = events.Events.Count == 0
                ? "暂无调整事件（保护未启用或未触发）。"
                : string.Join("\n", events.Events.TakeLast(8).Select(ev =>
                    $"{ev.TimeUtc.LocalDateTime:HH:mm:ss} {Path.GetFileName(ev.ExecutablePath)}（PID {ev.Pid}）：{(ev.Action == "demote" ? "临时降级" : "恢复")}——{ev.Reason}"));
        }
        catch (Exception ex)
        {
            ResponsivenessEventsText.Text = "事件读取未完成：" + ex.Message;
        }
    }
}
