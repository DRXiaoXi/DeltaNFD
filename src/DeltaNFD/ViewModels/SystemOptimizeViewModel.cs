using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeltaNFD.Services;

namespace DeltaNFD.ViewModels;

/// <summary>安全与隐私分组的行模型。</summary>
public partial class AdvancedTweakGroupVm : ObservableObject
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required AdvancedRisk Risk { get; init; }
    public required string CategoryText { get; init; }
    public required string RiskNote { get; init; }

    public string RiskText => Risk switch
    {
        AdvancedRisk.Low => "低风险",
        AdvancedRisk.Medium => "中风险",
        AdvancedRisk.High => "高风险",
        _ => "极高风险",
    };

    [ObservableProperty] private bool isApplied;
    [ObservableProperty] private string detailText = "";
    [ObservableProperty] private bool isBusy;

    public string StateText => IsApplied ? "已应用" : "未应用";

    partial void OnIsAppliedChanged(bool value) => OnPropertyChanged(nameof(StateText));
}

/// <summary>
/// 系统优化页 ViewModel：主体是 <see cref="Bx"/>（扩展优化库 + UP推荐 + 深度优化统一条目列表）。
/// 深度优化已并入 BxDbViewModel 的 UP推荐 条目列表（HANDOFF §25），本类只保留遗留分组 API 与虚拟内存设置。
/// </summary>
public partial class SystemOptimizeViewModel : ObservableObject
{
    private readonly IAdvancedTweakService _advanced = ServiceLocator.AdvancedTweaks;
    private readonly IPagefileService _pagefile = ServiceLocator.Pagefile;

    /// <summary>安全与隐私分组。</summary>
    public ObservableCollection<AdvancedTweakGroupVm> Groups { get; } = new();

    /// <summary>扩展优化库 子栏目（基本设置/安全性/自定义/服务组/精简/隐私/调整/任务/启用TSX/UP推荐）。</summary>
    public BxDbViewModel Bx { get; } = new();

    [ObservableProperty] private bool isLoadingGroups;
    [ObservableProperty] private string groupsStatusText = "正在读取分组状态…";

    // ---------------- 虚拟内存（页面文件）设置 ----------------

    [ObservableProperty] private string pagefileStatusText = "正在读取虚拟内存状态…";
    [ObservableProperty] private string pagefileMinText = "16";
    [ObservableProperty] private string pagefileMaxText = "32";
    [ObservableProperty] private bool pagefileBusy;
    [ObservableProperty] private int selectedDriveIndex;

    /// <summary>目标盘符下拉（固定磁盘，标签含 系统/游戏/介质标注）。</summary>
    public ObservableCollection<string> PagefileDriveChoices { get; } = new();

    /// <summary>页面通过此委托注入机械盘/游戏盘建议弹窗（true = 仍要执行）。</summary>
    public static Func<string, Task<bool>>? PagefileDriveWarningHook { get; set; }

    /// <summary>与 PagefileDriveChoices 平行的底层数据（应用时取盘符与类型判定）。</summary>
    private sealed record DriveChoice(char Letter, string Label, bool IsMechanical, bool IsGameDrive, bool IsSystem);

    private readonly List<DriveChoice> _pagefileDrives = new();

    /// <summary>当前所选盘符文本（如 "C:"，确认弹窗用；未就绪返回 null）。</summary>
    public string? PagefileSelectedDriveText =>
        SelectedDriveIndex >= 0 && SelectedDriveIndex < _pagefileDrives.Count
            ? $"{_pagefileDrives[SelectedDriveIndex].Letter}:"
            : null;

    /// <summary>枚举固定磁盘生成目标盘符下拉（系统盘排最前且默认选中）。</summary>
    public async Task LoadPagefileDrivesAsync()
    {
        try
        {
            var reports = await Task.Run(DriveInspector.GetFixedDriveReports);
            var choices = reports.Select(r =>
            {
                var tags = new List<string>();
                if (r.IsSystemDrive)
                {
                    tags.Add("系统");
                }

                if (r.IsGameDrive)
                {
                    tags.Add("游戏");
                }

                if (r.MediaLabel.Length > 0)
                {
                    tags.Add(r.MediaLabel);
                }

                return new DriveChoice(r.Letter,
                    tags.Count > 0 ? $"{r.Letter}:（{string.Join(" · ", tags)}）" : $"{r.Letter}:",
                    r.IsMechanical, r.IsGameDrive, r.IsSystemDrive);
            }).ToList();

            _pagefileDrives.Clear();
            _pagefileDrives.AddRange(choices);
            PagefileDriveChoices.Clear();
            foreach (var choice in choices)
            {
                PagefileDriveChoices.Add(choice.Label);
            }

            SelectedDriveIndex = Math.Max(0, choices.FindIndex(c => c.IsSystem));
        }
        catch (Exception ex)
        {
            Log.Info("虚拟内存：目标盘符列表加载失败 —— " + ex.Message);
        }
    }

    /// <summary>虚拟内存设置是否可操作。</summary>
    public bool PagefileApplyEnabled => !PagefileBusy;

    partial void OnPagefileBusyChanged(bool value) => OnPropertyChanged(nameof(PagefileApplyEnabled));

    public SystemOptimizeViewModel()
    {
        _ = LoadPagefileAsync();
        _ = LoadPagefileDrivesAsync();
    }

    /// <summary>读取当前虚拟内存状态（系统托管 / 手动大小）。</summary>
    public async Task LoadPagefileAsync()
    {
        try
        {
            var info = await _pagefile.GetInfoAsync();
            PagefileStatusText = info.Summary;
        }
        catch (Exception ex)
        {
            PagefileStatusText = "虚拟内存状态读取失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 应用自定义虚拟内存大小（GB）。约束：最小/最大均不得低于 16 GB，最大不小于最小。
    /// 返回 null 表示校验未通过（错误信息已写入状态文本）。
    /// </summary>
    public async Task<OperationResult?> ApplyPagefileAsync()
    {
        if (PagefileBusy)
        {
            return null;
        }

        if (!int.TryParse(PagefileMinText.Trim(), out var minGb) || minGb < 16)
        {
            var result = OperationResult.Fail("最小值不能低于 16 GB。");
            PagefileStatusText = result.Message;
            return result;
        }

        if (!int.TryParse(PagefileMaxText.Trim(), out var maxGb) || maxGb < 16)
        {
            var result = OperationResult.Fail("最大值不能低于 16 GB。");
            PagefileStatusText = result.Message;
            return result;
        }

        if (maxGb < minGb)
        {
            var result = OperationResult.Fail("最大值不能小于最小值。");
            PagefileStatusText = result.Message;
            return result;
        }

        PagefileBusy = true;
        try
        {
            if (SelectedDriveIndex < 0 || SelectedDriveIndex >= _pagefileDrives.Count)
            {
                var noDrive = OperationResult.Fail("请先选择目标盘符（正在加载或未检测到固定磁盘）。");
                PagefileStatusText = noDrive.Message;
                return noDrive;
            }

            var drive = _pagefileDrives[SelectedDriveIndex];
            if (drive.IsMechanical || drive.IsGameDrive)
            {
                var roleText = drive.IsMechanical && drive.IsGameDrive
                    ? "机械硬盘，且是三角洲游戏安装盘"
                    : drive.IsMechanical
                        ? "机械硬盘"
                        : "三角洲游戏安装盘";
                var warning = $"检测到所选盘符 {drive.Letter}: 是{roleText}。\n\n"
                              + "页面文件需要频繁随机读写：放在机械盘会明显拖慢加载，放在游戏盘会与游戏读写抢带宽。"
                              + "建议改选固态系统盘（默认项）。\n\n仍要写入该盘吗？";
                var proceed = PagefileDriveWarningHook is null || await PagefileDriveWarningHook.Invoke(warning);
                if (!proceed)
                {
                    PagefileStatusText = "已取消：建议在「目标盘符」中改选固态系统盘后再应用。";
                    return null;
                }
            }

            var result = await _pagefile.SetCustomAsync(minGb * 1024, maxGb * 1024, drive.Letter);
            Log.Info("虚拟内存：" + (result.Success ? "设置成功" : "设置失败") + " —— " + result.Message);
            PagefileStatusText = result.Success ? result.Message : "设置失败 —— " + result.Message;
            await LoadPagefileAsync();
            return result;
        }
        finally
        {
            PagefileBusy = false;
        }
    }

    /// <summary>恢复为系统托管（自动管理页面文件大小）。</summary>
    public async Task<OperationResult?> RestorePagefileAutomaticAsync()
    {
        if (PagefileBusy)
        {
            return null;
        }

        PagefileBusy = true;
        try
        {
            var result = await _pagefile.RestoreAutomaticAsync();
            Log.Info("虚拟内存：" + (result.Success ? "已恢复系统托管" : "恢复失败") + " —— " + result.Message);
            PagefileStatusText = result.Success ? result.Message : "恢复失败 —— " + result.Message;
            await LoadPagefileAsync();
            return result;
        }
        finally
        {
            PagefileBusy = false;
        }
    }

    public async Task<OperationResult?> RestorePagefileOriginalAsync()
    {
        if (PagefileBusy)
        {
            return null;
        }

        PagefileBusy = true;
        try
        {
            var result = await _pagefile.RestoreOriginalAsync();
            PagefileStatusText = result.Message;
            if (result.Success)
            {
                await LoadPagefileAsync();
            }

            return result;
        }
        finally
        {
            PagefileBusy = false;
        }
    }

    public async Task LoadGroupsAsync()
    {
        var groups = await _advanced.GetGroupsAsync();

        Groups.Clear();
        foreach (var g in groups)
        {
            Groups.Add(new AdvancedTweakGroupVm
            {
                Id = g.Id,
                DisplayName = g.DisplayName,
                Description = g.Description,
                Risk = g.Risk,
                CategoryText = g.Category switch
                {
                    AdvancedCategory.Telemetry => "遥测与隐私",
                    AdvancedCategory.Security => "安全开关",
                    _ => "Windows 更新",
                },
                RiskNote = g.RiskNote,
                IsApplied = g.IsApplied,
                DetailText = g.Detail,
            });
        }

        var appliedCount = groups.Count(g => g.IsApplied);
        GroupsStatusText = appliedCount == 0
            ? "未应用任何高级优化"
            : $"已应用 {appliedCount} / {groups.Count} 组优化（全部可还原）";
    }

    public async Task<OperationResult?> ApplyGroupAsync(AdvancedTweakGroupVm item)
    {
        if (item.IsBusy)
        {
            return null;
        }

        item.IsBusy = true;
        try
        {
            var result = await _advanced.ApplyAsync(item.Id);
            await LoadGroupsAsync();
            return result;
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    public async Task<OperationResult?> RevertGroupAsync(AdvancedTweakGroupVm item)
    {
        if (item.IsBusy)
        {
            return null;
        }

        item.IsBusy = true;
        try
        {
            var result = await _advanced.RevertAsync(item.Id);
            await LoadGroupsAsync();
            return result;
        }
        finally
        {
            item.IsBusy = false;
        }
    }
}
