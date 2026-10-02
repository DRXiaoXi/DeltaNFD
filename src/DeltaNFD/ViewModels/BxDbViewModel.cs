using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeltaNFD.Services;
using DeltaNFD.Services.TweakDb;

namespace DeltaNFD.ViewModels;

/// <summary>子栏目切换按钮。</summary>
public partial class BxSectionChipVm : ObservableObject
{
    public required BxSection Section { get; init; }
    public string Title => Section.Title;

    [ObservableProperty] private bool isSelected;
}

/// <summary>
/// 扩展优化库 优化条目行（JSON 条目 / 服务组 / 深度优化项）。
/// 深度优化项（DeepTweak 非空）作为普通行并入 UP推荐 栏目列表，统一计数与「应用更改」。
/// </summary>
public partial class BxItemVm : ObservableObject
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";

    public BxItem? Item { get; init; }
    public BxServiceGroup? Group { get; init; }

    /// <summary>非空 = 深度优化项（SystemTweakService 提供，意图开 = 禁用该功能）。</summary>
    public SystemTweak? DeepTweak { get; init; }

    /// <summary>状态描述模式：disable（默认）/ enable / remove（见 BxCatalog.ModeOf）。</summary>
    public string Mode { get; init; } = "disable";

    /// <summary>展开后的明细行（服务清单 / 任务清单 / 调整项数）。</summary>
    public ObservableCollection<string> Details { get; } = new();

    public bool HasDetails => Details.Count > 0;

    public BxItemVm()
    {
        // 明细行数变化时同步刷新「查看明细」按钮可见性
        Details.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDetails));
    }

    [ObservableProperty] private bool isOn;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string stateText = "";
    [ObservableProperty] private bool unsupported;
    [ObservableProperty] private bool detailsVisible;

    public string? UnavailableReason { get; init; }
    public BxState ActualState { get; private set; } = BxState.Unknown;
    public bool CanEdit => !Unsupported &&
        (ActualState is BxState.On or BxState.Off or BxState.Mixed) &&
        !(Id == "debloat/OneDrive" && ActualState == BxState.On);

    /// <summary>系统当前的真实状态（Fill 时写入）。IsOn = 用户意图，二者不同即存在待应用更改。</summary>
    public bool ActualOn { get; private set; }

    /// <summary>供 VM 内部回填真实状态。</summary>
    internal void SetActual(BxState state)
    {
        ActualState = state;
        ActualOn = state == BxState.On;
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(PendingChange));
        RefreshStateText(state);
    }

    /// <summary>开关拨动后尚未应用到系统。</summary>
    public bool PendingChange => CanEdit && (HasGears
        ? SelectedGearIndex != ActualGearIndex
        : IsOn != ActualOn);

    /// <summary>用户拨动过开关 / 换过档位但尚未应用——切换子栏目时携带保留，不随状态刷新被重置。</summary>
    public bool HasUserIntent { get; set; }

    /// <summary>当前系统实际生效的档位（-1 = 无匹配）。</summary>
    public int ActualGearIndex => _actualGearIndex;

    /// <summary>多档位条目的档位选项（非档位条目为空）。</summary>
    public ObservableCollection<BxGearOption> GearOptions { get; } = new();

    /// <summary>是否为多档位条目（显示下拉框而非开关）。</summary>
    public bool HasGears => GearOptions.Count >= 2;

    /// <summary>开关是否显示（多档位条目用下拉框代替）。</summary>
    public bool ShowToggle => !HasGears;

    /// <summary>当前选中的档位（-1 = 未选择）。</summary>
    [ObservableProperty] private int selectedGearIndex;

    private int _actualGearIndex = -1;

    /// <summary>回填档位信息（选项与当前档位）。存在未应用的档位意图时保留用户所选档位。</summary>
    public void SetGears(BxGearInfo info)
    {
        var keepSelection = HasUserIntent && SelectedGearIndex >= 0 && SelectedGearIndex != info.CurrentIndex;
        GearOptions.Clear();
        foreach (var opt in info.Options)
        {
            GearOptions.Add(opt);
        }

        _actualGearIndex = info.CurrentIndex;
        SelectedGearIndex = keepSelection ? SelectedGearIndex : info.CurrentIndex;
        RefreshStateText(BxState.Unknown);
        OnPropertyChanged(nameof(HasGears));
        OnPropertyChanged(nameof(ShowToggle));
    }

    partial void OnSelectedGearIndexChanged(int value) => OnPropertyChanged(nameof(PendingChange));

    partial void OnIsOnChanged(bool value) => OnPropertyChanged(nameof(PendingChange));

    public string DetailsButtonText => DetailsVisible ? "隐藏明细 ?" : "查看明细 ?";

    partial void OnDetailsVisibleChanged(bool value) => OnPropertyChanged(nameof(DetailsButtonText));

    public event EventHandler<bool>? ToggleRequested;

    public void RequestToggle(bool isOn) => ToggleRequested?.Invoke(this, isOn);

    /// <summary>
    /// 刷新状态文字：有未应用意图时显示「准备禁用 / 准备启用 / 准备移除 / 准备还原」（用户拨动的即时反馈）；
    /// 无意图时显示系统真实状态（未禁用 / 已禁用等，动词感知）。
    /// </summary>
    public void RefreshStateText(BxState actualState)
    {
        if (HasUserIntent && PendingChange)
        {
            if (HasGears)
            {
                StateText = "准备切换档位";
                return;
            }

            var word = Mode switch
            {
                "enable" => IsOn ? "启用" : "还原",
                "remove" => IsOn ? "移除" : "还原",
                _ => IsOn ? "禁用" : "还原",
            };
            StateText = "准备" + word;
            return;
        }

        StateText = UnavailableReason is not null ? "不适用" : DescribeState(ActualState, Mode, DisplayName);
    }

    /// <summary>
    /// 状态文字：动词感知。条目名以前导动作动词开头（禁用/关闭/拒绝/阻止/禁止/移除）时，
    /// 剥离动词得到作用对象，输出「对象：已完成态 / 未完成态」，消除「禁用 X」+「已禁用」的组合歧义。
    /// </summary>
    private static string DescribeState(BxState state, string mode, string displayName)
    {
        // 动词 → (应用态词, 未应用态词, 部分态词)
        var verb = displayName.Length >= 2
            ? displayName[..2]
            : "";
        var subject = verb switch
        {
            "禁用" or "关闭" or "禁止" or "阻止" or "移除" or "拒绝" => displayName[2..].TrimStart('　'),
            _ => null,
        };

        // 未命中动词前缀 → 按模式给出默认词
        var (on, off, mixed) = mode switch
        {
            "enable" => ("已启用", "未启用", "部分启用"),
            "remove" => ("已移除", "未移除", "部分移除"),
            _ => ("已禁用", "未禁用", "部分禁用"),
        };

        return state switch
        {
            BxState.On => subject is null ? on : $"{subject}：{on}",
            BxState.Off => subject is null ? off : $"{subject}：{off}",
            BxState.Mixed => subject is null ? mixed : $"{subject}：{mixed}",
            BxState.NotApplicable => "不适用",
            _ => "状态未知",
        };
    }
}

/// <summary>服务禁用影响警告（一批待应用的服务组，带翻译后的警告明细）。</summary>
public sealed record ServiceGroupWarning(string RowId, string GroupDisplayName, List<string> Lines);

/// <summary>系统优化页 扩展优化库 子栏目的页面状态。</summary>
public partial class BxDbViewModel : ObservableObject
{
    private readonly IBxService _bx = ServiceLocator.Bx;
    private readonly ISystemTweakService _tweaks = ServiceLocator.SystemTweaks;
    private readonly IPagefileService _pagefile = ServiceLocator.Pagefile;

    public ObservableCollection<BxSectionChipVm> Sections { get; } = new();
    public ObservableCollection<BxItemVm> Items { get; } = new();

    /// <summary>每个子栏目最近一次构建的条目行缓存（key = 栏目 Id）。用户勾选意图跨栏目保留。</summary>
    private readonly Dictionary<string, List<BxItemVm>> _sectionRows = new();

    /// <summary>全部子栏目的条目行（跨栏目统计待应用 / 应用更改时使用）。</summary>
    private IEnumerable<BxItemVm> AllRows => _sectionRows.Values.SelectMany(rows => rows);

    [ObservableProperty] private BxSectionChipVm? selectedSection;
    [ObservableProperty] private string sectionSubtitle = "";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string statusText = "";
    [ObservableProperty] private string backupSummary = "正在读取备份状态…";

    // ---------------- 应用过程实时进度 ----------------

    /// <summary>批量应用进行中（驱动进度条与按钮状态）。</summary>
    [ObservableProperty] private bool isApplyingBatch;

    /// <summary>应用进度（0-100）。</summary>
    [ObservableProperty] private double applyProgress;

    /// <summary>应用进度文本（"正在应用 12/128：鼠标加速…"）。</summary>
    [ObservableProperty] private string applyProgressText = "";

    partial void OnIsApplyingBatchChanged(bool value) => OnPropertyChanged(nameof(ApplyChangesEnabled));

    partial void OnSelectedSectionChanged(BxSectionChipVm? value)
    {
        SectionSubtitle = value?.Section.Subtitle ?? "";
        foreach (var chip in Sections)
        {
            chip.IsSelected = chip == value;
        }

        OnPropertyChanged(nameof(IsLegacySection));
        _ = LoadItemsAsync();
    }

    /// <summary>当前是否为 UP推荐 栏目（底部附虚拟内存设置卡）。</summary>
    public bool IsLegacySection => SelectedSection?.Section.Id == "pending";

    public BxDbViewModel()
    {
        foreach (var section in BxCatalog.Sections)
        {
            Sections.Add(new BxSectionChipVm { Section = section });
        }

        // 「pending」= UP推荐栏目（BxCatalog.Sections 末位）：5 个推荐条目 + 深度优化 20 项并入同一列表，
        // 共用「应用更改」统一应用（HANDOFF §25）

        UpdateBackupSummary();
        SelectedSection = Sections[0];
    }

    public void UpdateBackupSummary()
    {
        var bxCount = BxBackupStore.ReadAll().Count;
        var otherCount = TweakBackupStore.Default.GetAll().Count;
        BackupSummary = bxCount + otherCount == 0
            ? "尚未改动任何项——首次改动前会自动备份原始状态；APPX 和 OneDrive 移除不在一键恢复范围"
            : $"已记录 {bxCount + otherCount} 项原始状态（扩展库 {bxCount}，深度优化/虚拟内存 {otherCount}）；APPX 和 OneDrive 移除需手动重装";
    }

    /// <summary>加载序号：切换子栏目时自增，旧一轮加载的结果直接丢弃（修复加载中被切走后新栏目不加载的问题）。</summary>
    private int _loadGeneration;

    /// <summary>加载当前子栏目的全部条目与状态（主页扫描有缓存时直接顺延，避免重复查询）。</summary>
    public async Task LoadItemsAsync()
    {
        var section = SelectedSection?.Section;
        if (section is null)
        {
            return;
        }

        var generation = ++_loadGeneration;

        IsBusy = true;
        StatusText = "正在读取状态…";
        try
        {
            // 精简栏目：先作废 APPX 缓存，保证每次进页都按系统当前安装状态显示
            if (section.Id == "debloat")
            {
                _bx.InvalidateAppxCache();
            }

            // 服务组栏目
            if (section.JsonFile is null)
            {
                var rows = BxCatalog.ServiceGroups.Select(g => NewRow(new BxItemVm
                {
                    Id = "svcgroup/" + g.Id,
                    DisplayName = g.Name,
                    Description = g.Desc,
                    Group = g,
                })).ToList();

                foreach (var row in rows)
                {
                    foreach (var name in _bx.ResolveServiceGroup(row.Group!))
                    {
                        row.Details.Add("· " + name);
                    }
                }

                if (generation != _loadGeneration)
                {
                    return;
                }

                CarryOverIntent(section.Id, rows);
                _sectionRows[section.Id] = rows;
                ReplaceItems(rows);
                StatusText = "正在读取服务状态…";
                foreach (var row in rows)
                {
                    BxState state;
                    if (OptimizationScan.TryGetGroupState(row.Group!.Id, out var cached))
                    {
                        state = cached;
                    }
                    else
                    {
                        state = await _bx.GetServiceGroupStateAsync(row.Group!);
                        OptimizationScan.SetGroupState(row.Group!.Id, state);
                    }

                    if (generation != _loadGeneration)
                    {
                        return;
                    }

                    Fill(row, state);
                }

                StatusText = $"就绪 · {rows.Count} 组";
                return;
            }

            // JSON 数据库栏目
            if (!BxCatalog.Database.TryGetValue(section.Id, out var items) || items.Count == 0)
            {
                _sectionRows.Remove(section.Id);
                ReplaceItems(new List<BxItemVm>());
                StatusText = "数据库加载失败（Assets\\TweakDb 缺失）";
                return;
            }

            var systemDrive = DriveInspector.GetFixedDriveReports().FirstOrDefault(d => d.IsSystemDrive);
            bool? systemDriveIsHdd = systemDrive is null || (!systemDrive.IsMechanical && !systemDrive.IsSolidState)
                ? null : systemDrive.IsMechanical;
            var itemRows = items.Select(i =>
            {
                var (name, desc) = BxCatalog.Describe(i.Name);
                var unavailable = BxCatalog.GetUnavailableReason(i, Environment.OSVersion.Version.Build, systemDriveIsHdd);
                // 条目仅有 扩展优化库 内部脚本实现（无本工具支持的操作）时标记为不支持
                if (i.Tweaks.All(t => t.TweakType is "CHECK" or "EMBEDDEDTWEAK" or "EMBEDDED_FILE" or "BAT"))
                {
                    desc += "（该条目由原工具内部脚本实现，本工具暂不支持）";
                }
                if (unavailable is not null) desc += "（" + unavailable + "）";

                if (unavailable is not null)
                {
                    Log.Info($"扩展库适用性判定：{section.Id}/{i.Name} → 不适用；WindowsBuild={Environment.OSVersion.Version.Build}；原因={unavailable}；NoSupport=[{string.Join(",", i.NoSupport)}]");
                }

                var row = NewRow(new BxItemVm
                {
                    Id = section.Id + "/" + i.Name,
                    DisplayName = name,
                    Description = desc,
                    Item = i,
                    UnavailableReason = unavailable,
                    Unsupported = unavailable is not null || i.ValidationError is not null ||
                        i.Tweaks.All(t => t.TweakType is "CHECK" or "EMBEDDEDTWEAK" or "EMBEDDED_FILE" or "BAT"),
                    Mode = BxCatalog.ModeOf(i.Name, section.Id),
                });

                // 明细：枚举全部底层操作，让用户看清每一项到底动了什么
                if (i.Name == BxService.FullHyperVItemName)
                {
                    row.Details.Add("· 引导与安全设置：备份原值，关闭 hypervisor 启动、VBS、HVCI 和 Credential Guard");
                    row.Details.Add("· 可选功能：记录可读取的原状态，禁用已启用的 Hyper-V / 虚拟机平台功能");
                    row.Details.Add("· 已存在的 HvHost / vmms 服务：停止并改为按需启动，保留原启动类型备份");
                    row.Details.Add("· 还原：使用首次原状态备份，不统一启用全部功能；原值未知的项目不擅自修改");
                    return row;
                }
                foreach (var t in i.Tweaks)
                {
                    switch (t.TweakType)
                    {
                        case "TASK":
                            row.Details.Add("· 计划任务：" + t.Path);
                            break;
                        case "WEVTUTIL":
                            row.Details.Add("· 事件日志通道：" + t.Path);
                            break;
                        case "SVC":
                            row.Details.Add("· 服务：" + t.Path + "（启动模式 禁用/还原）");
                            break;
                        case "APPX":
                            row.Details.Add("· 应用包：" + t.Path);
                            break;
                        case "CMD":
                            var cmd = PickCmdText(t);
                            row.Details.Add(string.IsNullOrEmpty(cmd)
                                ? "· 命令：无（还原时不执行）"
                                : "· 命令：" + (cmd.Length > 100 ? cmd[..100] + "…" : cmd));
                            break;
                        case "DEVICE":
                        {
                            var deviceName = t.Path.StartsWith("ACPI\\PNP0103", StringComparison.OrdinalIgnoreCase)
                                ? "高精度事件计时器"
                                : t.Path;
                            row.Details.Add($"· 设备管理器：{deviceName}（应用时禁用 / 还原时启用）");
                            break;
                        }
                        case "CHECK" or "EMBEDDEDTWEAK" or "EMBEDDED_FILE" or "BAT":
                            break; // 不支持的类型不展示明细
                        default:
                            var valOn = t.Values.FirstOrDefault(v => v.ValueTypes.Contains("ON") || v.ValueTypes.Contains("BEST"));
                            var valOff = t.Values.FirstOrDefault(v => v.ValueTypes.Contains("OFF") || v.ValueTypes.Contains("DEFAULT"));
                            var onText = valOn?.Value == "Null" ? "删除" : valOn?.Value ?? "?";
                            var offText = valOff?.Value == "Null" ? "删除" : valOff?.Value ?? "?";
                            row.Details.Add("· 注册表 " + (t.Key is null or "" ? t.Path : t.Path + " → " + t.Key)
                                + $"：开={onText} / 默认={offText}");
                            break;
                    }
                }

                if (row.Details.Count == 0)
                {
                    row.Details.Add($"· 包含 {i.Tweaks.Count} 个系统设置写入");
                }

                return row;
            }).ToList();

            // UP推荐 栏目：深度优化 20 项并入条目列表（无独立卡片；统一计数与应用）
            List<TweakStatus>? deepStatuses = null;
            if (section.Id == "pending")
            {
                deepStatuses = await _tweaks.GetStatusesAsync();
                foreach (var status in deepStatuses)
                {
                    itemRows.Add(NewRow(new BxItemVm
                    {
                        Id = "deep/" + status.Tweak,
                        DisplayName = status.DisplayName,
                        Description = status.Description
                            + (string.IsNullOrEmpty(status.Detail) ? "" : $"（当前：{status.Detail}）"),
                        DeepTweak = status.Tweak,
                        Mode = status.Tweak == SystemTweak.HardwareGpuScheduling ? "enable" : "disable",
                    }));
                }
            }

            // 预填充多档位条目的档位选项（单条目多值 = 多档位）
            foreach (var row in itemRows)
            {
                if (row.Item is null)
                {
                    continue;
                }

                var gear = await _bx.GetGearInfoAsync(row.Item);
                if (gear is not null)
                {
                    row.SetGears(gear);
                }
            }

            if (generation != _loadGeneration)
            {
                return;
            }

            CarryOverIntent(section.Id, itemRows);
            _sectionRows[section.Id] = itemRows;
            ReplaceItems(itemRows);
            StatusText = "正在读取状态…";

            // 深度行：直接按真实状态回填（成本极低的注册表读，无需缓存）
            foreach (var row in itemRows.Where(r => r.DeepTweak is not null))
            {
                var status = deepStatuses!.First(s => s.Tweak == row.DeepTweak);
                Fill(row, status.IsOptimized ? BxState.On : BxState.Off);
            }

            // Bx 条目：主页扫描有缓存时顺延（跳过批量查询），缺失的条目单独查询并回填缓存
            var bxRows = itemRows.Where(r => r.DeepTweak is null).ToList();
            var cachedRows = new List<BxItemVm>();
            var queryItems = new List<BxItem>();
            if (OptimizationScan.HasData && !OptimizationScan.IsScanning)
            {
                foreach (var row in bxRows)
                {
                    if (row.Item is not null && OptimizationScan.TryGetItemState(section.Id, row.Item.Name, out var cached))
                    {
                        Fill(row, cached);
                        cachedRows.Add(row);
                    }
                    else
                    {
                        queryItems.Add(row.Item!);
                    }
                }
            }
            else
            {
                queryItems = bxRows.Where(r => r.Item is not null).Select(r => r.Item!).ToList();
            }

            if (queryItems.Count > 0)
            {
                var states = await _bx.GetItemStatesAsync(queryItems);
                if (generation != _loadGeneration)
                {
                    return;
                }

                foreach (var row in bxRows)
                {
                    if (row.Item is null || cachedRows.Contains(row))
                    {
                        continue;
                    }

                    if (states.TryGetValue(row.Item, out var state))
                    {
                        Fill(row, state);
                        OptimizationScan.SetItemState(section.Id, row.Item.Name, row.ActualState);
                    }
                }
            }
            else if (generation != _loadGeneration)
            {
                return;
            }

            // 多档位条目：回填档位信息与状态文字
            foreach (var row in itemRows)
            {
                if (row.HasGears)
                {
                    var gear = await _bx.GetGearInfoAsync(row.Item!);
                    if (gear is not null)
                    {
                        row.SetGears(gear);
                    }
                }
            }

            StatusText = $"就绪 · {itemRows.Count} 项";
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                IsBusy = false;
            }

            // 栏目加载完成后刷新跨栏目待应用计数（其他栏目带回的勾选意图会让按钮立即亮起）
            RefreshPendingFlags();
            UpdateBackupSummary();
        }
    }

    /// <summary>取 CMD 操作“开启”侧的命令文本（用于明细展示）。</summary>
    private static string PickCmdText(BxTweak t) =>
        t.Values.FirstOrDefault(v => v.ValueTypes.Contains("ON") || v.ValueTypes.Contains("BEST"))?.Value ?? "";

    private void Fill(BxItemVm row, BxState state)
    {
        row.SetActual(row.UnavailableReason is null ? state : BxState.NotApplicable);
        if (!row.HasUserIntent || row.IsOn == row.ActualOn)
        {
            // 无未应用意图（或意图恰好与系统状态一致）：跟随系统真实状态
            row.IsOn = row.ActualOn;
            row.HasUserIntent = false;
        }

        // 有未应用意图时保留 IsOn 不动（切换栏目回来仍是用户拨好的状态）；
        // 状态文字按意图显示「准备禁用 / 准备启用 / 准备还原」（HANDOFF §25）
        row.RefreshStateText(row.ActualState);
    }

    /// <summary>构建条目行时统一挂接开关事件（每次加载都新建行对象，不会重复订阅）。</summary>
    private BxItemVm NewRow(BxItemVm row)
    {
        row.ToggleRequested += async (_, on) => await ToggleAsync(row, on);
        return row;
    }

    /// <summary>把上一轮缓存中未应用的勾选意图带到新构建的行上（切换子栏目再回来不丢勾选）。</summary>
    private void CarryOverIntent(string sectionId, List<BxItemVm> rows)
    {
        if (!_sectionRows.TryGetValue(sectionId, out var oldRows))
        {
            return;
        }

        foreach (var row in rows)
        {
            var old = oldRows.FirstOrDefault(r => r.Id == row.Id);
            if (old is not { HasUserIntent: true })
            {
                continue;
            }

            row.HasUserIntent = true;
            row.IsOn = old.IsOn;
            if (row.HasGears && old.SelectedGearIndex >= 0)
            {
                row.SelectedGearIndex = old.SelectedGearIndex;
            }
        }
    }

    private void ReplaceItems(List<BxItemVm> rows)
    {
        Items.Clear();
        foreach (var row in rows)
        {
            Items.Add(row);
        }
    }

    /// <summary>开关拨动：仅记录用户意图，不写系统。真正应用由 ApplyChangesAsync 统一执行。</summary>
    public async Task ToggleAsync(BxItemVm row, bool isOn)
    {
        if (!row.CanEdit)
        {
            row.IsOn = row.ActualOn;
            return;
        }

        row.IsOn = isOn;
        row.HasUserIntent = true;
        ClearOtherHagsIntents(row);

        // 「关闭 Hyper-V/VBS」准备禁用时：督促先取消依赖 Windows Hello 的登录方式；取消则回拨开关
        if (isOn && !row.ActualOn && IsVbsFamily(row) && WindowsHelloGuidanceHook is not null
            && !await WindowsHelloGuidanceHook.Invoke())
        {
            row.IsOn = row.ActualOn;
            row.HasUserIntent = false;
        }

        row.RefreshStateText(BxState.Unknown);
        RefreshPendingFlags();
    }

    /// <summary>多档位条目档位切换：仅记录用户意图（页面 SelectionChanged 调用）。</summary>
    public void GearChanged(BxItemVm row)
    {
        if (row.SelectedGearIndex != row.ActualGearIndex)
        {
            row.HasUserIntent = true;
            ClearOtherHagsIntents(row);
        }

        row.RefreshStateText(BxState.Unknown);
        RefreshPendingFlags();
    }

    /// <summary>全部子栏目是否存在待应用更改 / 待应用行数（跨栏目累计）。</summary>
    public bool HasPendingChanges => AllRows.Any(i => i.PendingChange);

    public int PendingCount => AllRows.Count(i => i.PendingChange);

    public bool ApplyChangesEnabled => HasPendingChanges && !IsBusy && !IsApplyingBatch;

    public string ApplyChangesButtonText => HasPendingChanges ? $"应用更改（{PendingCount}）" : "应用更改";

    /// <summary>汇总刷新待应用相关绑定。</summary>
    public void RefreshPendingFlags()
    {
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(ApplyChangesEnabled));
        OnPropertyChanged(nameof(ApplyChangesButtonText));
    }

    private static bool IsDangerous(BxItemVm row) =>
        row.Item?.Risk?.Equals("High", StringComparison.OrdinalIgnoreCase) == true ||
        row.DeepTweak == SystemTweak.HyperVAndVbs;

    private static bool IsHagsRow(BxItemVm row) =>
        row.DeepTweak == SystemTweak.HardwareGpuScheduling ||
        row.Item is not null && BxCatalog.IsHagsItem(row.Item);

    private void ClearOtherHagsIntents(BxItemVm selected)
    {
        if (!IsHagsRow(selected) || !selected.PendingChange) return;
        foreach (var other in AllRows.Where(r => !ReferenceEquals(r, selected) && IsHagsRow(r)))
        {
            other.HasUserIntent = false;
            other.IsOn = other.ActualOn;
            other.RefreshStateText(other.ActualState);
        }
        RefreshPendingFlags();
    }

    /// <summary>「关闭 Hyper-V/VBS」家族条目（准备禁用前需引导用户先取消 Windows Hello 登录依赖）。</summary>
    private static bool IsVbsFamily(BxItemVm row) =>
        row.Id.EndsWith("/VBS") || row.Id.EndsWith("/VBSW11") ||
        row.DeepTweak == SystemTweak.HyperVAndVbs;

    /// <summary>页面通过此委托注入服务禁用影响警告弹窗（返回要排除的行 Id 集合；null = 取消应用）。</summary>
    public static Func<List<ServiceGroupWarning>, Task<HashSet<string>?>>? ServiceGroupWarningHook { get; set; }

    /// <summary>页面通过此委托注入 Windows Hello 操作引导弹窗（「关闭 Hyper-V/VBS」准备禁用时调用；true = 继续禁用）。</summary>
    public static Func<Task<bool>>? WindowsHelloGuidanceHook { get; set; }

    /// <summary>页面通过此委托注入 Defender 预检引导弹窗（批次含安全类条目时在应用前调用）。</summary>
    public static Func<DefenderPreflightReport, Task<DefenderGuardDecision>>? DefenderGuardHook { get; set; }

    /// <summary>页面通过此委托注入应用完成后的「重新开启防护」提醒弹窗。</summary>
    public static Func<DefenderPostApplyResult, Task>? DefenderRestoreRemindHook { get; set; }

    /// <summary>页面通过此委托注入确认弹窗（避免 ViewModel 依赖 UI 类型）。</summary>
    public static Func<string, Task<bool>>? ConfirmHook { get; set; }

    /// <summary>把条目的全部底层操作拼成一段文本，供 DefenderGuard 做安全类关键词匹配。</summary>
    private static string DescribeOpsForGuard(BxItemVm row)
    {
        try
        {
            if (row.Group is not null)
            {
                var g = row.Group;
                var services = g.UseBlockedDb
                    ? BxCatalog.AllServices.Where(s => s.IsBlocked || s.IsBlocked11).Select(s => s.ServiceName)
                    : g.Services;
                return string.Join(" ", services);
            }

            if (row.Item is null)
            {
                return row.Id;
            }

            return string.Join(" ", row.Item.Tweaks.Select(t =>
                t.TweakType + " " + t.Path + " " + t.Key + " " + string.Join(" ", t.Values.Select(v => v.Value ?? ""))));
        }
        catch
        {
            return row.Id;
        }
    }

    /// <summary>应用全部子栏目的待更改项（跨栏目统一执行；实时进度；服务组警告可排除部分项）。</summary>
    [RelayCommand]
    public async Task ApplyChangesAsync()
    {
        if (IsBusy || IsApplyingBatch)
        {
            return;
        }

        var pending = AllRows.Where(i => i.PendingChange).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        if (pending.Count(IsHagsRow) > 1)
        {
            StatusText = "HAGS 多个入口存在冲突，请只保留一个待应用操作。";
            return;
        }

        if (pending.Any(r => r.Id == "debloat/OneDrive" && r.IsOn) &&
            (ConfirmHook is null ||
             !await ConfirmHook("移除 OneDrive 后无法通过一键恢复自动重装，需从微软官网手动下载安装。仍要继续吗？")))
        {
            return;
        }

        // 批次含危险项时一次确认（列出全部危险项名称）
        var dangerous = pending.Where(IsDangerous).Select(i => i.DisplayName).ToList();
        if (dangerous.Count > 0)
        {
            var confirmed = ConfirmHook is not null &&
                await ConfirmHook("本次应用包含高危项：" + string.Join("、", dangerous));
            if (!confirmed)
            {
                return;
            }
        }

        // 服务禁用影响警告：批次包含要禁用的服务组时，把警告清单（数据库 WillBrake，已译中文）分组弹出让用户确认；
        // 用户可在弹窗里取消勾选部分组——被排除的组本次跳过（勾选意图保留，留待下次）。
        var serviceGroupPending = pending.Where(p => p.Group is not null && p.IsOn).ToList();
        if (serviceGroupPending.Count > 0)
        {
            var warnings = new List<ServiceGroupWarning>();
            foreach (var row in serviceGroupPending)
            {
                var lines = new List<string>();
                foreach (var name in _bx.ResolveServiceGroup(row.Group!))
                {
                    var willBrake = BxCatalog.GetServiceWillBrake(name);
                    if (willBrake.Count > 0)
                    {
                        lines.Add("· " + name + "：" + string.Join("、", willBrake));
                    }
                }

                if (lines.Count > 0)
                {
                    warnings.Add(new ServiceGroupWarning(row.Id, row.DisplayName, lines));
                }
            }

            if (warnings.Count > 0)
            {
                Log.Info($"扩展库：服务禁用影响警告 —— {warnings.Count} 组（{string.Join("；", warnings.SelectMany(w => w.Lines))}）");
                var excluded = ServiceGroupWarningHook is null
                    ? new HashSet<string>()
                    : await ServiceGroupWarningHook(warnings);
                if (excluded is null)
                {
                    Log.Info("扩展库：用户在服务禁用影响警告中取消应用");
                    StatusText = "已取消应用。";
                    return;
                }

                if (excluded.Count > 0)
                {
                    var excludedNames = pending.Where(p => excluded.Contains(p.Id)).Select(p => p.DisplayName).ToList();
                    Log.Info("扩展库：用户在警告弹窗中排除 " + excluded.Count + " 项（" + string.Join("、", excludedNames) + "）");
                    pending = pending.Where(p => !excluded.Contains(p.Id)).ToList();
                }
            }
        }

        if (pending.Count == 0)
        {
            StatusText = "全部服务组已被排除，没有需要应用的项目。";
            return;
        }

        // Defender 预检：批次含安全防护类条目且本机防护在位时，先引导用户临时关闭（优化完成后提醒恢复）。
        // 背景：Defender 篡改防护会拦截这类写入并记为病毒拦截，代码无法绕过，只能引导用户手动操作。
        DefenderPreflightReport? preflight = null;
        try
        {
            preflight = await DefenderGuard.PreflightAsync(
                pending.Select(p => (p.DisplayName, DescribeOpsForGuard(p))).ToList());
        }
        catch (Exception ex)
        {
            Log.Warn("DefenderGuard：预检异常（不阻塞应用）—— " + ex.Message);
        }

        if (preflight is { NeedsGuidance: true })
        {
            var decision = DefenderGuardHook is null
                ? DefenderGuardDecision.ProceedAnyway
                : await DefenderGuardHook(preflight);
            if (decision == DefenderGuardDecision.Cancel)
            {
                Log.Info("扩展库：用户在 Defender 预检引导中取消应用");
                StatusText = "已取消。可先在 Windows 安全中心临时关闭「实时保护 / 篡改防护」后再重新应用。";
                return;
            }
        }

        IsBusy = true;
        IsApplyingBatch = true;
        ApplyProgress = 0;
        var appliedOk = 0;
        var appliedFail = 0;
        var appliedSkipped = 0;
        Log.Info($"扩展库：应用 {pending.Count} 项（{string.Join("、", pending.Select(p => p.DisplayName))}）");
        try
        {
            for (var index = 0; index < pending.Count; index++)
            {
                var row = pending[index];
                ApplyProgressText = $"正在应用 {index + 1}/{pending.Count}：{row.DisplayName}…";
                ApplyProgress = index * 100.0 / pending.Count;
                row.IsBusy = true;
                try
                {
                    OperationResult result;
                    if (row.DeepTweak is not null)
                    {
                        // 深度优化行：意图开 = 禁用该功能，意图关 = 恢复默认
                        var deepTweak = row.DeepTweak.Value;
                        result = row.IsOn
                            ? await _tweaks.DisableAsync(deepTweak)
                            : await _tweaks.RestoreAsync(deepTweak);
                    }
                    else if (row.HasGears)
                    {
                        // 多档位条目：应用选中的档位
                        result = await _bx.ApplyGearAsync(row.Item!, row.SelectedGearIndex);
                    }
                    else if (row.Group is not null)
                    {
                        result = await _bx.ApplyServiceGroupAsync(row.Group, row.IsOn);
                    }
                    else
                    {
                        result = await _bx.ApplyItemAsync(row.Item!, row.IsOn);
                    }

                    if (result.IsSkipped)
                    {
                        appliedSkipped++;
                        Log.Info($"扩展库：{row.DisplayName} 安全跳过 —— {result.Message}");
                    }
                    else if (result.Success)
                    {
                        appliedOk++;
                    }
                    else
                    {
                        appliedFail++;
                        Log.Warn($"扩展库：{row.DisplayName} 失败 —— {result.Message}");
                        StatusText = $"{row.DisplayName}：{result.Message}";
                    }
                }
                catch (Exception ex)
                {
                    // 单项异常（如受保护注册表键拒绝访问）只计为该项失败，不中断整批
                    appliedFail++;
                    Log.Warn($"扩展库：{row.DisplayName} 异常 —— {ex.Message}");
                    StatusText = $"{row.DisplayName}：{ex.Message}";
                }
                finally
                {
                    row.IsBusy = false;
                }
            }

            ApplyProgress = 100;
            ApplyProgressText = $"完成：已应用 {appliedOk}，跳过 {appliedSkipped}，失败 {appliedFail}";

            Log.Info($"扩展库：应用完成，成功 {appliedOk} 项，安全跳过 {appliedSkipped} 项，失败 {appliedFail} 项");
            StatusText = $"已应用 {appliedOk} 项，安全跳过 {appliedSkipped} 项" + (appliedFail > 0 ? $"，失败 {appliedFail} 项（详情见上）。" : "。");
            UpdateBackupSummary();

            // 优化后收尾：移除会话排除项 + 需要时恢复实时保护（结果在状态刷新后提醒）
            DefenderPostApplyResult? guardResult = null;
            try
            {
                guardResult = await DefenderGuard.PostApplyAsync(preflight);
            }
            catch (Exception ex)
            {
                Log.Warn("DefenderGuard：收尾异常 —— " + ex.Message);
            }

            // 统一重读状态（真实状态回填 ActualOn，待应用标记自动消失；结果回写主页扫描缓存）：
            // 本轮应用的行（含其他子栏目）+ 当前栏目全部行；其他栏目未改动的行在下次进入时刷新
            var toRefresh = pending.Concat(Items).Concat(AllRows.Where(IsHagsRow)).Distinct().ToList();

            // 深度行：重读真实状态并回写扫描缓存
            List<TweakStatus>? freshDeep = null;
            if (toRefresh.Any(r => r.DeepTweak is not null))
            {
                freshDeep = await _tweaks.GetStatusesAsync();
            }

            var refreshItems = toRefresh.Where(r => r.Item is not null).Select(r => r.Item!).ToList();
            if (refreshItems.Count > 0)
            {
                var states = await _bx.GetItemStatesAsync(refreshItems);
                foreach (var row in toRefresh)
                {
                    if (row.Item is null)
                    {
                        continue;
                    }

                    if (states.TryGetValue(row.Item, out var state))
                    {
                        Fill(row, state);
                        var sectionId = row.Id[..row.Id.IndexOf('/')];
                        OptimizationScan.SetItemState(sectionId, row.Item.Name, row.ActualState);
                    }

                    // 多档位条目：回填档位信息与状态文字
                    var gear = await _bx.GetGearInfoAsync(row.Item);
                    if (gear is not null)
                    {
                        row.SetGears(gear);
                    }
                }
            }

            foreach (var row in toRefresh.Where(r => r.Group is not null))
            {
                var state = await _bx.GetServiceGroupStateAsync(row.Group!);
                Fill(row, state);
                OptimizationScan.SetGroupState(row.Group!.Id, state);
            }

            foreach (var row in toRefresh.Where(r => r.DeepTweak is not null))
            {
                var status = freshDeep!.First(s => s.Tweak == row.DeepTweak);
                Fill(row, status.IsOptimized ? BxState.On : BxState.Off);
                OptimizationScan.SetTweakState(status.Tweak, status.IsOptimized);
            }

            RefreshPendingFlags();

            // 提醒用户重新开启安全防护（引导关闭过防护 / 批次含关闭 Defender 条目时）
            if (guardResult is { Remind: true })
            {
                if (DefenderRestoreRemindHook is not null)
                {
                    await DefenderRestoreRemindHook(guardResult);
                }
                else
                {
                    StatusText += " 请重新开启实时保护/篡改防护与杀软，恢复系统防护。";
                }
            }
        }
        finally
        {
            IsBusy = false;
            IsApplyingBatch = false;
            RefreshPendingFlags();
        }
    }

    /// <summary>一键恢复全部 扩展优化库 备份。</summary>
    [RelayCommand]
    public async Task RestoreAllAsync()
    {
        if (IsBusy || IsApplyingBatch)
        {
            return;
        }

        var confirmed = ConfirmHook is null || await ConfirmHook("一键恢复全部备份");
        if (!confirmed)
        {
            return;
        }

        IsBusy = true;
        try
        {
            Log.Info("扩展库：一键恢复全部备份");
            StatusText = "正在一键恢复…";
            var bx = await _bx.RestoreAllAsync();
            var restored = bx.Restored;
            var errors = bx.Errors.ToList();
            foreach (var tweak in _tweaks.GetBackedUpTweaks())
            {
                var result = await _tweaks.RestoreAsync(tweak);
                if (result.Success) restored++;
                else errors.Add($"{tweak}：{result.Message}");
            }

            if (_pagefile.HasOriginalBackup)
            {
                var result = await _pagefile.RestoreOriginalAsync();
                if (result.Success) restored++;
                else errors.Add("虚拟内存：" + result.Message);
            }
            // 一键恢复后系统回到初始状态，全部栏目的勾选意图随之失效；扫描缓存全部作废
            foreach (var row in AllRows)
            {
                row.HasUserIntent = false;
            }

            OptimizationScan.Invalidate();
            var restoreMessage = restored == 0 && errors.Count == 0
                ? "没有可自动恢复的备份；已移除的 APPX 和 OneDrive 需手动重新安装。"
                : $"已恢复 {restored} 项，失败 {errors.Count} 项。" +
                  (errors.Count > 0 ? " " + string.Join("；", errors.Take(3)) : " 部分设置需重启生效。") +
                  " 已移除的 APPX 和 OneDrive 需手动重新安装。";
            UpdateBackupSummary();
            await LoadItemsAsync();
            StatusText = restoreMessage;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
