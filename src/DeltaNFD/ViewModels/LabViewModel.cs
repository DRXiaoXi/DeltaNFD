using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeltaNFD.Services;

namespace DeltaNFD.ViewModels;

/// <summary>CCD 行模型。</summary>
public sealed class CcdItemVm
{
    public string Text { get; init; } = "";
}

/// <summary>实验室处理器场景切换按钮。</summary>
public partial class LabScenarioVm : ObservableObject
{
    public required int Index { get; init; }
    public required string Title { get; init; }

    [ObservableProperty] private bool isSelected;
}

/// <summary>一个异类调度策略的下拉行（CPU实验室）。</summary>
public partial class HeteroPolicyVm : ObservableObject
{
    public required string SettingGuid { get; init; }
    public required string Title { get; init; }

    public ObservableCollection<string> Options { get; } = new();

    /// <summary>初始化完成标记：填充当前值期间的程序化 SelectedIndex 变更不触发应用。</summary>
    public bool Ready { get; set; }

    public bool Unsupported { get; set; }

    [ObservableProperty] private int selectedIndex = -1;
    [ObservableProperty] private string currentText = "";
    [ObservableProperty] private bool isBusy;
}

/// <summary>一个逻辑核心的亲和性勾选行（CPU 亲和性规则）。</summary>
public partial class AffinityCoreVm : ObservableObject
{
    public required int Index { get; init; }
    public required string Label { get; init; }

    [ObservableProperty] private bool isChecked;
}

/// <summary>实验室栏目（CPU 信息与核心调度）的页面状态。</summary>
public partial class LabViewModel : ObservableObject
{
    private readonly ICpuTopologyService _cpu = ServiceLocator.Cpu;

    public ObservableCollection<CcdItemVm> CcdItems { get; } = new();

    /// <summary>处理器场景切换按钮（0 自动 / 1 Intel / 2 AMD 单CCD / 3 AMD 双CCD）。</summary>
    public ObservableCollection<LabScenarioVm> Scenarios { get; } = new();

    /// <summary>CPU 亲和性规则的核心勾选行（每个逻辑处理器一项）。</summary>
    public ObservableCollection<AffinityCoreVm> AffinityCores { get; } = new();

    /// <summary>构建核心勾选期间抑制持久化（避免逐核写设置文件）。</summary>
    private bool _affinitySuppress;

    [ObservableProperty] private bool gameAffinityRuleEnabled;
    [ObservableProperty] private string gameAffinityStatusText = "";

    /// <summary>双CCD 调度的游戏 CCD 下拉选项（如 "CCD0（CPU0–15）"）。</summary>
    public ObservableCollection<string> DualCcdChoices { get; } = new();

    private CpuTopology? _topology;

    public bool AffinityRuleSupported => _topology?.AllMask != 0;

    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool isApplying;
    [ObservableProperty] private string cpuNameText = "正在检测处理器…";
    [ObservableProperty] private string vendorText = "";
    [ObservableProperty] private string topologyText = "";
    [ObservableProperty] private string ccdSectionText = "AMD CCD 判定";
    [ObservableProperty] private string ccdAdvice = "";
    [ObservableProperty] private bool ccdVisible;
    [ObservableProperty] private string gameStatusText = "";
    [ObservableProperty] private string hybridText = "检测中…";
    [ObservableProperty] private string htText = "检测中…";
    [ObservableProperty] private bool dualCcdVisible;
    [ObservableProperty] private bool dualCcdEnabled;
    [ObservableProperty] private int dualCcdGameCcdIndex;
    [ObservableProperty] private int dualCcdApplyModeIndex;
    [ObservableProperty] private string dualCcdStatusText = "";
    [ObservableProperty] private bool memoryLimitDetected;
    [ObservableProperty] private string memoryLimitText = "正在检测…";
    [ObservableProperty] private bool singleCcdVisible;
    [ObservableProperty] private bool singleCcdBusy;
    [ObservableProperty] private string singleCcdStatusText = "";
    [ObservableProperty] private int selectedScenarioIndex;
    [ObservableProperty] private string schemeStatusText = "正在读取无省电电源计划状态…";
    [ObservableProperty] private bool schemeBusy;
    [ObservableProperty] private bool schemeImported;

    /// <summary>无省电电源计划 GUID（设置里有缓存时可直接切换）。</summary>
    public string? SchemeGuid { get; private set; }

    public bool CanImportScheme => !SchemeBusy && (!SchemeImported || SchemeNeedsNameMigration);

    public bool CanSwitchScheme => !SchemeBusy && SchemeImported;

    public string ImportSchemeButtonText => SchemeNeedsNameMigration
        ? "修正计划名称" : SchemeImported ? "已导入" : "导入无省电电源计划";

    [ObservableProperty] private bool schemeNeedsNameMigration;

    partial void OnSchemeBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanImportScheme));
        OnPropertyChanged(nameof(CanSwitchScheme));
    }

    partial void OnSchemeImportedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanImportScheme));
        OnPropertyChanged(nameof(ImportSchemeButtonText));
        OnPropertyChanged(nameof(CanSwitchScheme));
    }

    partial void OnSchemeNeedsNameMigrationChanged(bool value)
    {
        OnPropertyChanged(nameof(CanImportScheme));
        OnPropertyChanged(nameof(ImportSchemeButtonText));
    }

    public bool IsGameRunning
    {
        get => _isGameRunning;
        private set
        {
            if (_isGameRunning == value)
            {
                return;
            }

            _isGameRunning = value;
            GameStatusText = value ? "三角洲运行中" : "三角洲未运行";
            OnPropertyChanged();
        }
    }

    private bool _isGameRunning;

    public bool CanRefresh => !IsLoading;

    public bool DualCcdApplyEnabled => !IsApplying;

    public bool DualCcdRevertEnabled => !IsApplying;

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(CanRefresh));

    partial void OnSingleCcdBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(SingleCcdApplyEnabled));
        OnPropertyChanged(nameof(SingleCcdRestoreEnabled));
    }

    partial void OnDualCcdGameCcdIndexChanged(int value) => SaveCcdChoice();

    partial void OnDualCcdApplyModeIndexChanged(int value) => SaveCcdChoice();

    private void SaveCcdChoice() => AppSettingsStore.Update(s =>
    {
        s.DualCcdGameCcdIndex = DualCcdGameCcdIndex;
        s.DualCcdApplyModeIndex = DualCcdApplyModeIndex;
    });

    // ---------------- 处理器场景切换 ----------------

    /// <summary>场景与本机硬件不一致时的提示可见性（功能会自行检测并在不适用时拒绝执行）。</summary>
    public bool ScenarioMismatchVisible => SelectedScenarioIndex != 0 && ScenarioMismatchText.Length > 0;

    public string ScenarioMismatchText { get; private set; } = "";

    /// <summary>各卡片可见性（按所选场景计算；自动模式 = 硬件检测结果）。</summary>
    public bool ShowCcdJudge { get; private set; }
    public bool ShowDualCcd { get; private set; }
    public bool ShowSingleCcd { get; private set; }

    partial void OnSelectedScenarioIndexChanged(int value)
    {
        foreach (var chip in Scenarios)
        {
            chip.IsSelected = chip.Index == value;
        }

        if (value > 0)
        {
            AppSettingsStore.Update(s => s.LabScenarioIndex = value);
        }

        RefreshScenarioVisibility();
    }

    /// <summary>按场景计算卡片可见性。</summary>
    private void RefreshScenarioVisibility()
    {
        var scenario = SelectedScenarioIndex;
        var isAmd = CcdVisible;           // 硬件检测：AMD
        var isDualCcd = DualCcdVisible;   // 硬件检测：AMD 多 CCD
        var isIntel = string.Equals(_topology?.Vendor, "Intel", StringComparison.OrdinalIgnoreCase); // 硬件检测：Intel

        if (scenario == 0)
        {
            // 自动检测（本机）：沿用硬件检测结果（单CCD 卡对全部 AMD 机型显示，含双CCD）
            ShowCcdJudge = isAmd;
            ShowDualCcd = isDualCcd;
            ShowSingleCcd = SingleCcdVisible;
            ScenarioMismatchText = "";
        }
        else if (scenario == 1)
        {
            // Intel 场景
            ShowCcdJudge = false;
            ShowDualCcd = false;
            ShowSingleCcd = false;
            ScenarioMismatchText = isIntel
                ? ""
                : "所选场景为 Intel 处理器，与本机处理器不一致——相关功能会自行检测并在不适用时拒绝执行。";
        }
        else if (scenario == 2)
        {
            // AMD 单CCD 场景
            ShowCcdJudge = true;
            ShowDualCcd = false;
            ShowSingleCcd = true;
            ScenarioMismatchText = isAmd
                ? ""
                : "所选场景为 AMD 处理器，与本机处理器不一致——相关功能会自行检测并在不适用时拒绝执行。";
        }
        else
        {
            // AMD 双CCD 场景（单CCD 优化调度「排除 CPU0」同样可用）
            ShowCcdJudge = true;
            ShowDualCcd = true;
            ShowSingleCcd = true;
            ScenarioMismatchText = isAmd && isDualCcd
                ? ""
                : "所选场景为 AMD 双CCD 处理器，与本机不一致——相关功能会自行检测并在不适用时拒绝执行。";
        }

        OnPropertyChanged(nameof(ShowCcdJudge));
        OnPropertyChanged(nameof(ShowDualCcd));
        OnPropertyChanged(nameof(ShowSingleCcd));
        OnPropertyChanged(nameof(ScenarioMismatchVisible));
        OnPropertyChanged(nameof(ScenarioMismatchText));
    }

    public LabViewModel()
    {
        foreach (var (index, title) in new[]
                 {
                     (0, "自动检测（本机）"),
                     (1, "Intel"),
                     (2, "AMD 单CCD"),
                     (3, "AMD 双CCD"),
                 })
        {
            Scenarios.Add(new LabScenarioVm { Index = index, Title = title });
        }

        // 场景索引直接读存档（0 自动 / 1 Intel / 2 AMD 单CCD / 3 AMD 双CCD），非法值回退自动检测。
        // 注意：不要再做任何"旧档归一化"映射——此前的 switch 每次启动都把新格式索引当旧格式
        // 重映射一遍（3→2→1），导致用户的「AMD 双CCD」选择随启动逐次降级、卡片消失。
        var settings = AppSettingsStore.Read();
        SelectedScenarioIndex = Math.Clamp(settings.LabScenarioIndex, 0, 3);
        gameAffinityRuleEnabled = settings.GameAffinityRuleEnabled;

        _ = LoadAsync();
    }

    public void AttachGameState()
    {
        ServiceLocator.Frame.PropertyChanged -= OnFramePropertyChanged;
        ServiceLocator.Frame.PropertyChanged += OnFramePropertyChanged;
        IsGameRunning = ServiceLocator.Frame.IsGameRunning;
    }

    public void DetachGameState() => ServiceLocator.Frame.PropertyChanged -= OnFramePropertyChanged;

    private void OnFramePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IFrameService.IsGameRunning))
        {
            IsGameRunning = ServiceLocator.Frame.IsGameRunning;
        }
    }

    public async Task LoadAsync()
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var topology = await _cpu.GetTopologyAsync();
            _topology = topology;
            OnPropertyChanged(nameof(AffinityRuleSupported));
            // 大小核相关绑定依赖 _topology，刷新后必须通知，否则 P/E 按钮与标签不会出现
            OnPropertyChanged(nameof(IsHybridCpu));
            OnPropertyChanged(nameof(AffinityHybridButtonsVisible));
            OnPropertyChanged(nameof(AffinityPCoreButtonText));
            OnPropertyChanged(nameof(AffinityECoreButtonText));
            OnPropertyChanged(nameof(HybridCoreDetailText));
            if (!AffinityRuleSupported && GameAffinityRuleEnabled)
                GameAffinityRuleEnabled = false;

            CpuNameText = topology.CpuName;
            VendorText = topology.Vendor;
            TopologyText = $"物理核心 {topology.PhysicalCores} · 逻辑处理器 {topology.LogicalProcessors} · 处理器组 {topology.GroupCount}";
            HybridText = topology.IsHybrid
                ? $"是（{topology.PCoreCount} 性能核 + {topology.ECoreCount} 效率核）"
                : "否";
            HtText = topology.HasHyperThreading ? "已启用" : "未启用";

            DualCcdVisible = topology.IsAmdMultiCcd && topology.Ccds.Count == 2 &&
                topology.GroupCount == 1 && topology.Ccds.All(c => c.Group == 0);

            // CPU 亲和性规则：按逻辑处理器数重建核心勾选（勾选状态从持久化掩码读回；0 = 视为全核）
            _affinitySuppress = true;
            var savedAffinityMask = AppSettingsStore.Read().GameAffinityRuleMask;
            if (savedAffinityMask == 0) savedAffinityMask = topology.AllMask;

            AffinityCores.Clear();
            for (var i = 0; i < 64; i++)
            {
                if ((topology.AllMask & (1UL << i)) == 0) continue;
                // 大小核机型在标签上标出 P/E，让用户一眼核对识别结果是否正确
                var kind = !topology.IsHybrid ? ""
                    : (topology.PCoreMask & (1UL << i)) != 0 ? " · P 核"
                    : (topology.ECoreMask & (1UL << i)) != 0 ? " · E 核"
                    : "";
                var core = new AffinityCoreVm { Index = i, Label = $"CPU {i}{kind}", IsChecked = (savedAffinityMask & (1UL << i)) != 0 };
                core.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(AffinityCoreVm.IsChecked))
                    {
                        PersistAffinitySelection();
                    }
                };
                AffinityCores.Add(core);
            }

            _affinitySuppress = false;
            UpdateAffinityStatusTextFromMask(savedAffinityMask);

            // 内存限制检测
            var (detected, removedMb, rawOutput) = await _cpu.DetectMemoryLimitAsync();
            MemoryLimitDetected = detected;
            MemoryLimitText = detected
                ? $"检测到 bcdedit removememory = {removedMb} MB（可用内存被限制为约 12 GB）"
                : "未检测到内存限制";

            CcdSectionText = topology.IsAmd ? "AMD CCD 判定" : "AMD CCD 判定（非 AMD 处理器不适用）";
            CcdAdvice = topology.CcdAdvice;
            CcdVisible = topology.IsAmd;

            // 单CCD 优化调度（排除 CPU0）：全部 AMD 机型可用——单CCD 机型与双CCD 机型
            //（双CCD 机型不做 CCD 隔离、只想把 CPU0 让给系统时同样适用）
            SingleCcdVisible = topology.IsAmd;

            // 按当前场景（含自动检测）刷新卡片可见性
            RefreshScenarioVisibility();

            // 异类调度策略状态（后台刷新不阻塞页面）
            _ = RefreshHeteroPoliciesAsync();

            // 无省电电源计划状态（后台刷新不阻塞页面）
            _ = RefreshSchemeStatusAsync();

            CcdItems.Clear();
            foreach (var ccd in topology.Ccds)
            {
                CcdItems.Add(new CcdItemVm
                {
                    Text = $"CCD{ccd.Index}：{ccd.CoreCount} 核 / {ccd.LogicalCount} 线程，L3 {ccd.L3CacheMb:0} MB（掩码 0x{ccd.Mask:X}）",
                });
            }

            // 掩码来自实际 Die/L3 覆盖，不能假设两组逻辑处理器均分且连续。
            DualCcdChoices.Clear();
            foreach (var ccd in topology.Ccds.Take(2))
            {
                DualCcdChoices.Add($"CCD{ccd.Index}（掩码 0x{ccd.Mask:X}）");
            }

            var stored = AppSettingsStore.Read();
            DualCcdApplyModeIndex = Math.Clamp(stored.DualCcdApplyModeIndex, 0, 1);
            DualCcdGameCcdIndex = Math.Clamp(stored.DualCcdGameCcdIndex, 0, Math.Max(0, DualCcdChoices.Count - 1));
            SingleCcdStatusText = stored.SingleCcdExcludeCpu0Enabled
                ? "单CCD规则已保存：工具启动后自动恢复，三角洲启动时排除 CPU0。"
                : "单CCD规则未启用。";
            DualCcdStatusText = stored.DualCcdArmed
                ? $"已登记帧格生效：帧格开启时游戏使用 CCD{stored.DualCcdGameCcdIndex}，退出帧格后撤销。"
                : stored.DualCcdImmediateEnabled
                    ? $"双CCD立即调度已保存：工具启动后重新应用，游戏使用 CCD{stored.DualCcdGameCcdIndex}。"
                    : "双CCD调度未启用。";

            IsGameRunning = await _cpu.IsGameRunningAsync();
            OnPropertyChanged(nameof(SingleCcdApplyEnabled));
            OnPropertyChanged(nameof(SingleCcdRestoreEnabled));
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ---------------- CPU 亲和性规则（Process Lasso 风格，仅三角洲进程） ----------------

    /// <summary>当前勾选核心的掩码。</summary>
    public ulong AffinityRuleMask
    {
        get
        {
            ulong mask = 0;
            foreach (var core in AffinityCores)
            {
                if (core.IsChecked)
                {
                    mask |= 1UL << core.Index;
                }
            }

            return mask;
        }
    }

    /// <summary>快捷预设：按掩码设置全部核心勾选（0x0 = 全部勾选）。</summary>
    public void ApplyAffinityCorePreset(ulong mask)
    {
        SetAffinitySelection(mask == 0 ? _topology?.AllMask ?? 0 : mask);
    }

    /// <summary>手动立即应用一次亲和性规则（互斥与冷却遵循服务逻辑）。</summary>
    public async Task ApplyAffinityNowAsync()
    {
        var mask = AppSettingsStore.Read().GameAffinityRuleMask;
        if (mask == 0)
        {
            GameAffinityStatusText = "尚未勾选任何核心（请先勾选允许三角洲运行的核心）。";
            return;
        }

        await Task.Run(() => CpuTopologyService.ApplyAffinityRuleTick(mask));
        GameAffinityStatusText = $"已应用掩码 0x{mask:X}（三角洲运行中持续保持）。";
    }

    /// <summary>亲和性快捷按钮（仅 CCD0 / 仅 CCD1）是否显示（双CCD 机型才有第二个 CCD）。</summary>
    public bool AffinityCcdButtonsVisible => DualCcdVisible;

    /// <summary>快捷预设：全部核心可运行。</summary>
    public void AffinitySetAllCores()
    {
        SetAffinitySelection(_topology?.AllMask ?? 0);
    }

    /// <summary>快捷预设：排除 CPU0（留给系统）。</summary>
    public void AffinityExcludeCpu0()
    {
        SetAffinitySelection((_topology?.AllMask ?? 0) & ~1UL);
    }

    /// <summary>快捷预设：仅勾选指定 CCD 的核心。</summary>
    public void AffinityOnlyCcd(int ccdIndex)
    {
        if (_topology is null || ccdIndex < 0 || ccdIndex >= _topology.Ccds.Count)
        {
            return;
        }

        var mask = _topology.Ccds[ccdIndex].Mask;
        SetAffinitySelection(mask);
    }

    // ---- 大小核（Intel P/E 核）快捷预设 ----

    /// <summary>当前 CPU 是否为大小核混合架构（决定 P/E 核快捷按钮是否显示）。</summary>
    public bool IsHybridCpu => _topology?.IsHybrid == true;

    /// <summary>P/E 核快捷按钮可见性（仅大小核机型显示）。</summary>
    public bool AffinityHybridButtonsVisible => IsHybridCpu && AffinityRuleSupported;

    /// <summary>P 核按钮文字（带核数，便于用户确认识别是否正确）。</summary>
    public string AffinityPCoreButtonText =>
        _topology is null || !_topology.IsHybrid ? "仅 P 核" : $"仅 P 核（{_topology.PCoreCount}）";

    /// <summary>E 核按钮文字（带核数）。</summary>
    public string AffinityECoreButtonText =>
        _topology is null || !_topology.IsHybrid ? "仅 E 核" : $"仅 E 核（{_topology.ECoreCount}）";

    /// <summary>大小核识别结果说明（给用户核对 P/E 判定是否符合预期）。</summary>
    public string HybridCoreDetailText
    {
        get
        {
            if (_topology is null || !_topology.IsHybrid)
            {
                return "";
            }

            return $"已识别大小核：P 核 {_topology.PCoreCount} 个（掩码 0x{_topology.PCoreMask:X}）、"
                + $"E 核 {_topology.ECoreCount} 个（掩码 0x{_topology.ECoreMask:X}）。"
                + "游戏建议锁定 P 核；E 核留给后台任务。";
        }
    }

    /// <summary>快捷预设：仅勾选 P 核（性能核）。</summary>
    public void AffinityOnlyPCores()
    {
        if (_topology?.IsHybrid != true || _topology.PCoreMask == 0)
        {
            GameAffinityStatusText = "当前 CPU 未识别到大小核架构，无法使用 P 核预设。";
            return;
        }

        SetAffinitySelection(_topology.PCoreMask);
    }

    /// <summary>快捷预设：仅勾选 E 核（效率核）。</summary>
    public void AffinityOnlyECores()
    {
        if (_topology?.IsHybrid != true || _topology.ECoreMask == 0)
        {
            GameAffinityStatusText = "当前 CPU 未识别到大小核架构，无法使用 E 核预设。";
            return;
        }

        SetAffinitySelection(_topology.ECoreMask);
    }

    private void SetAffinitySelection(ulong mask)
    {
        if (!AffinityRuleSupported) return;

        _affinitySuppress = true;
        foreach (var core in AffinityCores)
        {
            core.IsChecked = (mask & (1UL << core.Index)) != 0;
        }
        _affinitySuppress = false;
        PersistAffinitySelection();
    }

    partial void OnGameAffinityRuleEnabledChanged(bool value)
    {
        if (value && !AffinityRuleSupported)
        {
            GameAffinityRuleEnabled = false;
            return;
        }

        var mask = AffinityRuleMask;
        AppSettingsStore.Update(s =>
        {
            s.GameAffinityRuleEnabled = value;
            if (value) s.GameAffinityRuleMask = mask;
        });
        UpdateAffinityStatusTextFromMask(AffinityRuleMask);
        if (value)
        {
            _ = ApplyAffinityNowAsync();
        }
        else
        {
            CpuTopologyService.RestoreAffinityRule();
        }
    }

    /// <summary>把当前勾选持久化到设置并刷新状态文字。</summary>
    public void PersistAffinitySelection()
    {
        if (_affinitySuppress)
        {
            return;
        }

        var mask = AffinityRuleMask;
        AppSettingsStore.Update(s => s.GameAffinityRuleMask = mask);
        UpdateAffinityStatusTextFromMask(mask);
        if (mask == 0) CpuTopologyService.RestoreAffinityRule();
    }

    private void UpdateAffinityStatusTextFromMask(ulong mask)
    {
        var coreCount = 0;
        for (var i = 0; i < 64; i++)
        {
            if ((mask & (1UL << i)) != 0)
            {
                coreCount++;
            }
        }

        GameAffinityStatusText = !AffinityRuleSupported
            ? "当前处理器拓扑无法安全使用 64 位硬锁核（需要单处理器组且不超过 64 个逻辑处理器）"
            : mask == 0
            ? "尚未勾选任何核心"
            : $"已选 {coreCount} 核 · 掩码 0x{mask:X} · 游戏运行时自动应用硬锁核";
    }

    // ---------------- 双CCD 专属调度 ----------------

    /// <summary>应用双CCD专属调度：立即生效模式马上调整亲和性；帧格生效模式登记到帧格模式随开关自动启停。</summary>
    public async Task<OperationResult?> ApplyDualCcdAsync()
    {
        if (IsApplying)
        {
            return null;
        }

        // 硬件未检出多 CCD（虚拟机等环境）：给明确提示而不是静默无反应
        if (DualCcdVisible == false)
        {
            return OperationResult.Fail("本机未检测到可用于调度的 AMD 双 CCD 掩码，双CCD调度不适用。");
        }

        IsApplying = true;
        try
        {
            if (DualCcdApplyModeIndex == 1)
            {
                // 从立即模式切到帧格模式时，先撤销旧的立即调度，避免留下未登记的运行时状态。
                if (AppSettingsStore.Read().DualCcdImmediateEnabled)
                {
                    var revert = await _cpu.RevertDualCcdSchedulingAsync();
                    if (!revert.Success)
                    {
                        return revert;
                    }
                }

                // 帧格生效：只登记配置，开启帧格模式时由 FrameService 自动应用、关闭时自动撤销
                AppSettingsStore.Update(s =>
                {
                    s.DualCcdArmed = true;
                    s.DualCcdImmediateEnabled = false;
                    s.DualCcdGameCcdIndex = DualCcdGameCcdIndex;
                    s.DualCcdApplyModeIndex = 1;
                });
                return OperationResult.Ok(
                    $"已登记：开启帧格模式时自动把游戏独占到 {CcdLabel(DualCcdGameCcdIndex)}"
                    + "（其他进程推到另一个 CCD），关闭帧格模式时自动撤销。");
            }

            var result = await _cpu.ApplyDualCcdSchedulingAsync(DualCcdGameCcdIndex);
            // 立即生效模式：清除帧格联动登记，避免下次开帧格重复应用
            AppSettingsStore.Update(s =>
            {
                s.DualCcdArmed = false;
                s.DualCcdApplyModeIndex = 0;
                if (result.Success)
                {
                    s.DualCcdImmediateEnabled = true;
                    s.SingleCcdExcludeCpu0Enabled = false;
                }
            });
            if (result.Success)
            {
                _ = RefreshHeteroPoliciesAsync(); // 调度已强制覆写异类策略，刷新下拉显示
            }
            return result;
        }
        finally
        {
            IsApplying = false;
        }
    }

    /// <summary>撤销双CCD专属调度（恢复所有进程到全部核心，并清除帧格联动登记）。</summary>
    public async Task<OperationResult?> RevertDualCcdAsync()
    {
        if (IsApplying)
        {
            return null;
        }

        IsApplying = true;
        try
        {
            var result = await _cpu.RevertDualCcdSchedulingAsync();
            AppSettingsStore.Update(s =>
            {
                s.DualCcdArmed = false;
                s.DualCcdImmediateEnabled = false;
            });
            if (result.Success)
            {
                var settings = AppSettingsStore.Read();
                if (settings.SingleCcdExcludeCpu0Enabled)
                {
                    var singleCcd = await _cpu.ExcludeCpu0FromGameAsync();
                    if (!singleCcd.Success)
                    {
                        return OperationResult.Fail($"双CCD调度已撤销，但已保存的单CCD规则恢复失败：{singleCcd.Message}");
                    }
                }

                _ = RefreshHeteroPoliciesAsync(); // 异类策略已还原原值，刷新下拉显示
            }
            return result;
        }
        finally
        {
            IsApplying = false;
        }
    }

    private string CcdLabel(int index) =>
        index >= 0 && index < DualCcdChoices.Count ? DualCcdChoices[index] : $"CCD{index}";

    /// <summary>移除 bcdedit removememory 内存限制。</summary>
    public async Task<OperationResult?> RemoveMemoryLimitAsync()
    {
        if (IsApplying)
        {
            return null;
        }

        IsApplying = true;
        try
        {
            return await _cpu.RemoveMemoryLimitAsync();
        }
        finally
        {
            IsApplying = false;
        }
    }

    // ---------------- 单CCD 优化调度 ----------------

    /// <summary>按钮不再要求游戏在运行：未运行时点击=登记规则，游戏启动后由轮询自动应用并持续保持。</summary>
    public bool SingleCcdApplyEnabled => !SingleCcdBusy;

    public bool SingleCcdRestoreEnabled => !SingleCcdBusy;

    /// <summary>排除 CPU0（单CCD 优化调度）。</summary>
    public async Task<OperationResult?> ExcludeCpu0Async()
    {
        if (SingleCcdBusy)
        {
            return null;
        }

        SingleCcdBusy = true;
        try
        {
            if (CpuTopologyService.DualCcdSchedulingActive)
            {
                var dualCcd = await _cpu.RevertDualCcdSchedulingAsync();
                if (!dualCcd.Success)
                {
                    return dualCcd;
                }
            }

            var result = await _cpu.ExcludeCpu0FromGameAsync();
            if (result.Success)
            {
                AppSettingsStore.Update(s =>
                {
                    s.SingleCcdExcludeCpu0Enabled = true;
                    s.DualCcdImmediateEnabled = false;
                });
                _ = RefreshHeteroPoliciesAsync(); // 调度已强制覆写异类策略，刷新下拉显示
            }
            return result;
        }
        finally
        {
            SingleCcdBusy = false;
        }
    }

    /// <summary>恢复三角洲到全部核心。</summary>
    public async Task<OperationResult?> RestoreFullCoresAsync()
    {
        if (SingleCcdBusy)
        {
            return null;
        }

        SingleCcdBusy = true;
        try
        {
            var result = await _cpu.RestoreGameFullCoresAsync();
            if (result.Success)
            {
                AppSettingsStore.Update(s => s.SingleCcdExcludeCpu0Enabled = false);
                _ = RefreshHeteroPoliciesAsync(); // 异类策略已还原原值，刷新下拉显示
            }
            return result;
        }
        finally
        {
            SingleCcdBusy = false;
        }
    }

    // ---------------- 无省电电源计划（显示名「无省电释放模式」） ----------------

    /// <summary>只读刷新无省电电源计划状态（是否已导入 / 是否当前激活）。</summary>
    public async Task RefreshSchemeStatusAsync()
    {
        if (SchemeBusy)
        {
            return;
        }

        SchemeBusy = true;
        try
        {
            await RefreshSchemeStatusCoreAsync();
        }
        finally
        {
            SchemeBusy = false;
        }
    }

    private async Task<bool> RefreshSchemeStatusCoreAsync()
    {
        try
        {
            var schemes = await ServiceLocator.Power.GetSchemesAsync();
            var settings = AppSettingsStore.Read();
            var guid = settings.AtlasPowerSchemeGuid;
            var scheme = schemes.FirstOrDefault(s => !string.IsNullOrWhiteSpace(guid) && s.Guid.Equals(guid, StringComparison.OrdinalIgnoreCase))
                ?? schemes.FirstOrDefault(s => s.Name == PowerService.NoPowerSaveSchemeName ||
                    PowerService.LegacyNoPowerSaveNames.Contains(s.Name));
            SchemeGuid = scheme?.Guid ?? "";
            SchemeImported = scheme is not null;
            SchemeNeedsNameMigration = schemes.Any(s => PowerService.LegacyNoPowerSaveNames.Contains(s.Name));

            Log.Info($"CPU实验室·电源计划检测：缓存 GUID={(string.IsNullOrWhiteSpace(guid) ? "(空)" : guid)}，识别 GUID={(scheme?.Guid ?? "(无)")}，活动 GUID={(schemes.FirstOrDefault(s => s.IsActive)?.Guid ?? "(未知)")}，已导入={SchemeImported}，帧格激活={settings.FrameModeActive}，帧格电源锁定={settings.FramePowerLockEnabled}，锁定目标={(string.IsNullOrWhiteSpace(settings.FramePowerLockTargetGuid) ? "当前计划" : settings.FramePowerLockTargetGuid)}，锁定原计划={(string.IsNullOrWhiteSpace(settings.FramePowerPreviousSchemeGuid) ? "(空)" : settings.FramePowerPreviousSchemeGuid)}");

            SchemeStatusText = scheme is null
                ? "无省电电源计划未导入（导入后可在电源选项中查看）"
                : SchemeNeedsNameMigration
                    ? $"已导入（{scheme.Guid}）；检测到旧名称计划，可点击「修正计划名称」"
                    : scheme.IsActive
                        ? $"「{scheme.Name}」当前已启用（{scheme.Guid}）"
                        : $"「{scheme.Name}」已导入（{scheme.Guid}），当前未启用";
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("CPU实验室·电源计划状态读取失败", ex);
            SchemeGuid = "";
            SchemeImported = false;
            SchemeNeedsNameMigration = false;
            SchemeStatusText = $"读取无省电电源计划状态失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>导入内置的无省电电源计划（已导入则跳过）。全新导入成功后立即激活生效，完成后刷新状态。</summary>
    public async Task ImportSchemeAsync()
    {
        if (SchemeBusy)
        {
            return;
        }

        SchemeBusy = true;
        try
        {
            Log.Info("CPU实验室·电源计划：点击导入/修正名称");
            var wasImported = SchemeImported;
            var result = await ServiceLocator.Power.ImportNoPowerSaveSchemeAsync();
            Log.Info($"CPU实验室·电源计划：导入结果={(result.Success ? "成功" : "失败")}，说明={result.Message}");
            if (!result.Success)
            {
                SchemeStatusText = result.Message;
                return;
            }

            // 仅全新导入（或收养既有计划）时自动激活；「修正计划名称」不改变当前激活计划
            if (wasImported)
            {
                if (!await RefreshSchemeStatusCoreAsync())
                    SchemeStatusText = result.Message + "；" + SchemeStatusText;
                return;
            }

            // 帧格电源锁定开启期间激活会被锁定守卫立刻改回目标计划，跳过自动激活并说明
            var settings = AppSettingsStore.Read();
            if (settings.FrameModeActive && settings.FramePowerLockEnabled)
            {
                await RefreshSchemeStatusCoreAsync();
                SchemeStatusText = result.Message +
                    "；帧格电源锁定开启中，暂不自动激活——退出帧格或关闭电源锁定后再点击「切换到无省电电源计划」启用。" + SchemeStatusText;
                return;
            }

            var guid = settings.AtlasPowerSchemeGuid;
            if (string.IsNullOrWhiteSpace(guid))
            {
                guid = (await ServiceLocator.Power.GetSchemesAsync())
                    .FirstOrDefault(s => s.Name == PowerService.NoPowerSaveSchemeName)?.Guid;
            }

            if (string.IsNullOrWhiteSpace(guid))
            {
                if (!await RefreshSchemeStatusCoreAsync())
                    SchemeStatusText = result.Message + "；" + SchemeStatusText;
                return;
            }

            var activate = await ServiceLocator.Power.SetSchemeAsync(guid);
            Log.Info($"CPU实验室·电源计划：导入后自动激活结果={(activate.Success ? "成功" : "失败")}，目标 GUID={guid}，说明={activate.Message}");
            var activateNote = activate.Success
                ? "导入后已自动激活，立即生效。"
                : $"自动激活失败：{activate.Message}——可点击「切换到无省电电源计划」重试。";
            await RefreshSchemeStatusCoreAsync();
            SchemeStatusText = activateNote + "；" + SchemeStatusText;
        }
        catch (Exception ex)
        {
            Log.Error("CPU 实验室：导入无省电电源计划失败", ex);
            SchemeStatusText = $"导入无省电电源计划失败：{ex.Message}";
        }
        finally
        {
            SchemeBusy = false;
        }
    }

    /// <summary>切换到无省电电源计划（未导入时自动先导入）。立即生效。</summary>
    public async Task<OperationResult?> SwitchToSchemeAsync()
    {
        if (SchemeBusy)
        {
            return null;
        }

        SchemeBusy = true;
        try
        {
            var guid = SchemeGuid;
            Log.Info($"CPU实验室·电源计划：点击切换，页面 GUID={(string.IsNullOrWhiteSpace(guid) ? "(空)" : guid)}");
            if (string.IsNullOrWhiteSpace(guid))
            {
                var import = await ServiceLocator.Power.ImportNoPowerSaveSchemeAsync();
                Log.Info($"CPU实验室·电源计划：切换前自动导入结果={(import.Success ? "成功" : "失败")}，说明={import.Message}");
                if (!import.Success)
                {
                    SchemeStatusText = import.Message;
                    return import;
                }

                guid = AppSettingsStore.Read().AtlasPowerSchemeGuid;
            }

            if (string.IsNullOrWhiteSpace(guid))
            {
                SchemeStatusText = "未获得无省电电源计划 GUID，无法切换。";
                return OperationResult.Fail(SchemeStatusText);
            }

            var result = await ServiceLocator.Power.SetSchemeAsync(guid);
            Log.Info($"CPU实验室·电源计划：切换结果={(result.Success ? "成功" : "失败")}，目标 GUID={guid}，说明={result.Message}");
            if (result.Success)
            {
                if (!await RefreshSchemeStatusCoreAsync())
                    SchemeStatusText = result.Message + "；" + SchemeStatusText;
            }
            else SchemeStatusText = result.Message;
            return result;
        }
        catch (Exception ex)
        {
            Log.Error("CPU 实验室：切换无省电电源计划失败", ex);
            SchemeStatusText = $"切换无省电电源计划失败：{ex.Message}";
            return OperationResult.Fail(SchemeStatusText);
        }
        finally
        {
            SchemeBusy = false;
        }
    }

    // ---------------- 异类调度策略 ----------------

    /// <summary>「生效的异类策略」下拉选项（注册表权威枚举 0–4；Windows 官方只命名到策略编号，默认 4）。</summary>
    private static readonly string[] HeteroPolicyLabels =
    [
        "0 · 异类策略 0",
        "1 · 异类策略 1",
        "2 · 异类策略 2",
        "3 · 异类策略 3",
        "4 · 异类策略 4（系统默认）",
    ];

    /// <summary>两个线程调度策略下拉选项（注册表权威枚举 0–5；默认 5=自动）。</summary>
    private static readonly string[] HeteroThreadLabels =
    [
        "0 · 所有处理器",
        "1 · 性能处理器（仅）",
        "2 · 首选性能处理器",
        "3 · 能效处理器（仅）",
        "4 · 首选能效处理器",
        "5 · 自动（系统默认）",
    ];

    /// <summary>读取三个异类策略的当前值并填充下拉（值超出已知范围时显示原值文本）。</summary>
    public async Task RefreshHeteroPoliciesAsync()
    {
        try
        {
            var infos = await ServiceLocator.Power.GetHeteroPoliciesAsync();
            HeteroItems.Clear();
            foreach (var info in infos)
            {
                var labels = info.SettingGuid.Equals(HeteroPolicySettingsGuid, StringComparison.OrdinalIgnoreCase)
                    ? HeteroPolicyLabels
                    : HeteroThreadLabels;

                var vm = new HeteroPolicyVm
                {
                    SettingGuid = info.SettingGuid,
                    Title = info.Title,
                };

                if (!info.Supported || info.AcValue is null || info.DcValue is null)
                {
                    vm.CurrentText = "本平台不支持该设置";
                    vm.SelectedIndex = -1;
                    vm.Unsupported = true;
                }
                else
                {
                    var value = info.AcValue.Value;
                    vm.CurrentText = info.AcValue == info.DcValue
                        ? $"当前值：{value}"
                        : $"当前值：交流 {info.AcValue} / 直流 {info.DcValue}";
                    vm.SelectedIndex = value >= 0 && value < labels.Length ? value : -1;
                    if (vm.SelectedIndex < 0)
                    {
                        vm.CurrentText += "（超出常见取值范围，可手动选择后覆盖）";
                    }
                }

                foreach (var label in labels)
                {
                    vm.Options.Add(label);
                }

                vm.Ready = true; // 初始化完成前页面 SelectionChanged 不触发应用
                HeteroItems.Add(vm);
            }

            OnPropertyChanged(nameof(HasHeteroItems));
        }
        catch (Exception ex)
        {
            HeteroStatusText = $"读取异类策略失败：{ex.Message}";
        }
    }

    private const string HeteroPolicySettingsGuid = "7f2f5cfa-f10c-4823-b5e1-e93ae85f46b5";

    public ObservableCollection<HeteroPolicyVm> HeteroItems { get; } = new();

    [ObservableProperty] private string heteroStatusText = "";

    public bool HasHeteroItems => HeteroItems.Count > 0;

    /// <summary>用户在下拉选择新值后应用（交流/直流同时设置）。</summary>
    public async Task<OperationResult?> ApplyHeteroPolicyAsync(HeteroPolicyVm item)
    {
        if (!item.Ready || item.IsBusy || item.SelectedIndex < 0)
        {
            return null;
        }

        var value = item.SelectedIndex;
        item.IsBusy = true;
        try
        {
            var result = await ServiceLocator.Power.SetHeteroPolicyAsync(item.SettingGuid, value);
            HeteroStatusText = $"{item.Title}：{result.Message}";
            item.CurrentText = $"当前值：{value}";
            return result;
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    // ---------------- 优化方案：保存 / 导入（txt 分享） ----------------

    [ObservableProperty] private string planStatusText =
        "把当前「异类调度策略 + CPU 亲和性」导出为 txt，可分享给使用相同 CPU 的人导入。";

    /// <summary>
    /// 采集当前方案：CPU 型号 / 核心数 / 线程数 / 异类调度策略 / CPU 亲和性。
    /// 拓扑未就绪时返回 null（调用方给提示）。
    /// </summary>
    public async Task<CpuPlanService.CpuPlan?> CapturePlanAsync()
    {
        if (_topology is null)
        {
            PlanStatusText = "处理器信息尚未就绪，请稍后重试。";
            return null;
        }

        // 异类策略以**实时读取**为准，避免用户刚改完下拉、页面缓存还是旧值
        await RefreshHeteroPoliciesAsync();

        var selectedCores = AffinityCores.Where(c => c.IsChecked).Select(c => c.Index).ToList();
        var plan = new CpuPlanService.CpuPlan
        {
            CpuName = _topology.CpuName,
            PhysicalCores = _topology.PhysicalCores,
            LogicalProcessors = _topology.LogicalProcessors,
            AffinityRuleEnabled = GameAffinityRuleEnabled,
            AffinityMask = $"0x{AffinityRuleMask:X}",
            AffinityCores = selectedCores,
            SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            MachineName = Environment.MachineName,
            AppVersion = AppVersion.Text,
        };

        foreach (var item in HeteroItems)
        {
            if (item.Unsupported)
            {
                continue;
            }

            plan.Hetero.Add(new CpuPlanService.HeteroEntry(
                item.SettingGuid,
                item.Title,
                (item.SelectedIndex >= 0 ? item.SelectedIndex : 0).ToString(),
                (item.SelectedIndex >= 0 ? item.SelectedIndex : 0).ToString()));
        }

        return plan;
    }

    /// <summary>把方案写进文件（UTF-8 无 BOM，与项目其余文本文件一致）。</summary>
    public bool SavePlanToFile(CpuPlanService.CpuPlan plan, string path, out string message)
    {
        try
        {
            var text = CpuPlanService.Serialize(plan);
            File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
            message = $"方案已保存：{path}";
            PlanStatusText = message + $"（CPU {plan.CpuName} · {plan.PhysicalCores} 核 / {plan.LogicalProcessors} 线程 · 勾选 {plan.AffinityCores.Count} 核 · 异类策略 {plan.Hetero.Count} 项）";
            Log.Info($"CPU方案：已保存到 {path}（{plan.CpuName}，{plan.PhysicalCores}C/{plan.LogicalProcessors}T）");
            return true;
        }
        catch (Exception ex)
        {
            message = $"保存方案失败：{ex.Message}";
            PlanStatusText = message;
            Log.Error("CPU方案：保存失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 读取并校验方案文件。校验不通过（CPU/核心数/线程数不一致或格式错误）时返回 null 并给出原因，
    /// **不会**改动本机任何设置——导入必须先过校验，再单独调用 <see cref="ImportPlanAsync"/>。
    /// </summary>
    public CpuPlanService.CpuPlan? LoadAndValidatePlanFile(string path, out string message)
    {
        try
        {
            var text = File.ReadAllText(path, System.Text.Encoding.UTF8);
            if (!CpuPlanService.TryParse(text, out var plan, out var parseError))
            {
                message = parseError;
                PlanStatusText = "导入失败：" + parseError;
                return null;
            }

            if (_topology is null)
            {
                message = "处理器信息尚未就绪，请稍后重试。";
                PlanStatusText = message;
                return null;
            }

            var validation = CpuPlanService.ValidateForLocalMachine(
                plan!, _topology.CpuName, _topology.PhysicalCores, _topology.LogicalProcessors);
            if (!validation.Success)
            {
                message = validation.Message;
                PlanStatusText = "导入被拒绝：" + validation.Message;
                Log.Warn($"CPU方案：拒绝导入 {path}；{validation.Message}");
                return null;
            }

            message = "";
            return plan;
        }
        catch (Exception ex)
        {
            message = $"读取方案失败：{ex.Message}";
            PlanStatusText = message;
            Log.Error("CPU方案：读取失败", ex);
            return null;
        }
    }

    /// <summary>把已通过校验的方案应用到本机（亲和性勾选 + 规则开关 + 异类调度策略）。</summary>
    public async Task<bool> ImportPlanAsync(CpuPlanService.CpuPlan plan)
    {
        try
        {
            // 1. 亲和性：以「已勾选核心」为准重建掩码，避免掩码与核心列表互相矛盾
            ulong mask = 0;
            foreach (var core in plan.AffinityCores)
            {
                if (core is >= 0 and < 64)
                {
                    mask |= 1UL << core;
                }
            }

            // 掩码可能超出本机可用核心（同一 CPU 不同主板/BIOS 下逻辑处理器布局仍应一致，
            // 但保险起见与本机 AllMask 求交，绝不写入本机不存在的核心）
            var available = _topology?.AllMask ?? 0;
            if (available != 0)
            {
                mask &= available;
            }

            // 先写设置再回填 UI，避免 SetAffinitySelection 里的持久化把旧值盖回来
            AppSettingsStore.Update(s => s.GameAffinityRuleMask = mask);
            SetAffinitySelection(mask);

            if (plan.AffinityRuleEnabled != GameAffinityRuleEnabled)
            {
                GameAffinityRuleEnabled = plan.AffinityRuleEnabled;
            }

            // 2. 异类调度策略：按 GUID 匹配本机读取到的条目
            var applied = 0;
            foreach (var entry in plan.Hetero)
            {
                var target = HeteroItems.FirstOrDefault(
                    i => i.SettingGuid.Equals(entry.SettingGuid, StringComparison.OrdinalIgnoreCase));
                if (target is null || target.Unsupported)
                {
                    continue;
                }

                if (!int.TryParse(entry.AcValue, out var value))
                {
                    continue;
                }

                var result = await ServiceLocator.Power.SetHeteroPolicyAsync(entry.SettingGuid, value);
                if (result.Success)
                {
                    applied++;
                    target.CurrentText = $"当前值：{value}";
                }
            }

            await RefreshHeteroPoliciesAsync();

            PlanStatusText = $"方案已导入：亲和性掩码 0x{mask:X}（{plan.AffinityCores.Count} 核）"
                + $"，异类调度策略 {applied}/{plan.Hetero.Count} 项。"
                + (plan.AffinityRuleEnabled ? "亲和性规则已开启，游戏运行时自动应用。" : "亲和性规则为关闭状态（方案里即为关闭）。");
            Log.Info($"CPU方案：导入完成（掩码 0x{mask:X}，异类策略 {applied}/{plan.Hetero.Count}）");
            return true;
        }
        catch (Exception ex)
        {
            PlanStatusText = $"导入方案失败：{ex.Message}";
            Log.Error("CPU方案：导入失败", ex);
            return false;
        }
    }
}
