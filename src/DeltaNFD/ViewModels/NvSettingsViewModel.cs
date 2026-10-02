using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeltaNFD.Services;

namespace DeltaNFD.ViewModels;

/// <summary>NVIDIA 显卡设置页的一个设置行。</summary>
public partial class NvSettingRowVm : ObservableObject
{
    public required NvSettingDef Def { get; init; }

    public uint Id => Def.Id;

    public string Title => Def.Title;

    public string Description => Def.Description;

    public ObservableCollection<string> OptionLabels { get; } = new();

    /// <summary>百分比缩放模式（33–100% 滑条任意调节，如 DLSS 超分渲染比例）。</summary>
    public bool IsScale => Def.Kind == NvSettingKind.PercentScale;

    /// <summary>下拉候选模式（非缩放行显示 ComboBox）。</summary>
    public bool ShowComboBox => !IsScale;

    /// <summary>缩放范围（供滑条 Min/Max 绑定）。</summary>
    public double ScaleMin => Def.ScaleMin;

    public double ScaleMax => Def.ScaleMax;

    /// <summary>驱动是否支持该设置（按读取结果推断；不支持时置灰显示说明）。</summary>
    [ObservableProperty] private bool supported = true;

    /// <summary>当前覆盖值对应的选项下标（-1 = 无覆盖/默认，或覆盖值不在候选列表中）。</summary>
    [ObservableProperty] private int selectedIndex = -1;

    // ---------------- 百分比缩放模式状态 ----------------

    /// <summary>是否覆盖渲染比例（关 = 默认，交还游戏内档位决定）。</summary>
    [ObservableProperty] private bool scaleOverride;

    /// <summary>滑条所选百分比（应用时四舍五入取整并夹回范围）。</summary>
    [ObservableProperty] private double selectedScale = 66;

    /// <summary>缩放行当前显示文本（跟随滑条实时刷新）。</summary>
    public string ScaleText => $"{ScaleToInt()}%";

    private bool _actualHasOverride;
    private int _actualScale;

    /// <summary>滑条值取整（夹回 33–100）。</summary>
    public int ScaleToInt()
    {
        var v = (int)Math.Round(SelectedScale);
        return Math.Clamp(v, Def.ScaleMin, Def.ScaleMax);
    }

    partial void OnSelectedScaleChanged(double value)
    {
        OnPropertyChanged(nameof(ScaleText));
        OnPropertyChanged(nameof(PendingChange));
    }

    partial void OnScaleOverrideChanged(bool value) => OnPropertyChanged(nameof(PendingChange));

    /// <summary>回填缩放行的系统真实状态。</summary>
    public void SetActualScale(bool hasOverride, uint value, string state)
    {
        _actualHasOverride = hasOverride;
        _actualScale = (int)value;
        ScaleOverride = hasOverride;
        if (hasOverride && value is >= 33 and <= 100)
        {
            SelectedScale = value;
        }

        StateText = state;
        OnPropertyChanged(nameof(PendingChange));
    }

    /// <summary>当前系统真实状态文字。</summary>
    [ObservableProperty] private string stateText = "读取中…";

    private int _actualIndex = -1;

    /// <summary>是否存在待应用更改。</summary>
    public bool PendingChange => IsScale
        ? ScaleOverride != _actualHasOverride || (ScaleOverride && ScaleToInt() != _actualScale)
        : SelectedIndex != _actualIndex;

    partial void OnSelectedIndexChanged(int value) => OnPropertyChanged(nameof(PendingChange));

    /// <summary>回填系统真实状态。</summary>
    public void SetActual(int index, string state)
    {
        _actualIndex = index;
        SelectedIndex = index;
        StateText = state;
        OnPropertyChanged(nameof(PendingChange));
    }
}

/// <summary>NVIDIA 显卡设置页的页面状态。</summary>
public partial class NvSettingsViewModel : ObservableObject
{
    private readonly INvProfileService _nv = ServiceLocator.NvProfile;

    public ObservableCollection<NvSettingRowVm> Rows { get; } = new();

    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool isApplying;
    [ObservableProperty] private string statusText = "正在检测 NVIDIA 环境…";
    [ObservableProperty] private string gpuSummaryText = "";
    [ObservableProperty] private bool isNvidia;
    [ObservableProperty] private bool nvApiAvailable;
    [ObservableProperty] private bool drsFilesLocked;
    [ObservableProperty] private string lockStatusText = "";
    [ObservableProperty] private bool lockBusy;

    public bool EnvironmentReady => IsNvidia && NvApiAvailable;

    partial void OnIsNvidiaChanged(bool value) => OnPropertyChanged(nameof(EnvironmentReady));
    partial void OnNvApiAvailableChanged(bool value) => OnPropertyChanged(nameof(EnvironmentReady));

    public int PendingCount => Rows.Count(r => r.PendingChange);

    public bool HasPendingChanges => PendingCount > 0;

    public bool ApplyEnabled => !IsApplying && !IsLoading && EnvironmentReady && HasPendingChanges && !DrsFilesLocked;

    public string ApplyButtonText => HasPendingChanges ? $"应用更改（{PendingCount}）" : "应用更改";

    partial void OnIsApplyingChanged(bool value) => RefreshFlags();
    partial void OnIsLoadingChanged(bool value) => RefreshFlags();
    partial void OnDrsFilesLockedChanged(bool value) => RefreshFlags();

    public void RefreshFlags()
    {
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(ApplyEnabled));
        OnPropertyChanged(nameof(ApplyButtonText));
    }

    /// <summary>行选项变化时由页面调用，刷新待应用计数。</summary>
    public void OnRowSelectionChanged() => RefreshFlags();

    public NvSettingsViewModel()
    {
        foreach (var def in _nv.GetSettingCatalog())
        {
            var row = new NvSettingRowVm { Def = def };
            foreach (var option in def.Options)
            {
                row.OptionLabels.Add(option.Label);
            }

            Rows.Add(row);
        }

        _ = LoadAsync();
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
            await LoadEnvironmentAsync();
            if (!EnvironmentReady)
            {
                StatusText = !IsNvidia
                    ? "未检测到 NVIDIA 显卡——本页功能仅面向 N 卡。"
                    : "NVAPI 不可用（nvapi64.dll 加载失败或驱动过旧）。";
                foreach (var row in Rows)
                {
                    if (row.IsScale)
                    {
                        row.SetActualScale(false, 0, "环境不可用");
                    }
                    else
                    {
                        row.SetActual(-1, "环境不可用");
                    }
                }

                return;
            }

            StatusText = $"正在读取 {ServiceLocator.GameTarget.Current.DisplayName} 的驱动配置…";
            var values = await _nv.GetGameSettingValuesAsync();
            if (values is null)
            {
                StatusText = "读取驱动配置失败（会话创建或数据库加载失败）。";
                foreach (var row in Rows)
                {
                    if (row.IsScale)
                    {
                        row.SetActualScale(false, 0, "读取失败");
                    }
                    else
                    {
                        row.SetActual(-1, "读取失败");
                    }
                }

                return;
            }

            foreach (var row in Rows)
            {
                if (!values.TryGetValue(row.Id, out var state))
                {
                    if (row.IsScale)
                    {
                        row.SetActualScale(false, 0, "未知");
                    }
                    else
                    {
                        row.SetActual(-1, "未知");
                    }

                    continue;
                }

                var (hasOverride, value) = state;
                var def = row.Def;

                // 百分比缩放行（DLSS 超分渲染比例）：按覆盖值 / 范围回填滑条状态
                if (row.IsScale)
                {
                    if (!hasOverride)
                    {
                        row.SetActualScale(false, 0, "当前：默认（游戏内档位决定）");
                    }
                    else if (value is >= 33 and <= 100)
                    {
                        row.SetActualScale(true, value, $"当前：已覆盖 → {value}%");
                    }
                    else
                    {
                        // 越界覆盖值：显示为默认态并说明，用户重新拨开覆盖后按滑条值重写
                        row.SetActualScale(false, 0, $"当前：已覆盖 → 自定义值 {value}（超出 33–100，重新开启覆盖后按滑条值重写）");
                    }

                    continue;
                }

                var index = -1;
                for (var i = 0; i < def.Options.Length; i++)
                {
                    if (def.Options[i].Value == value)
                    {
                        index = i;
                        break;
                    }
                }

                string text;
                if (!hasOverride)
                {
                    text = index >= 0
                        ? $"当前：默认（生效值 {def.Options[index].Label}）"
                        : "当前：默认（未覆盖）";
                    index = Math.Max(index, 0); // 默认态选中「默认」档；生效值未知时选第一项
                    // 生效值不在候选里且无覆盖：选 0（默认档）
                }
                else
                {
                    text = index >= 0
                        ? $"当前：已覆盖 → {def.Options[index].Label}"
                        : $"当前：已覆盖 → 自定义值 0x{value:X}";
                    if (index < 0)
                    {
                        index = 0;
                    }
                }

                row.SetActual(index, text);
            }

            StatusText = $"就绪 · 配置目标：{ServiceLocator.GameTarget.Current.DisplayName}（首次应用时自动创建独立配置）";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadEnvironmentAsync()
    {
        var env = await _nv.GetEnvironmentAsync();
        IsNvidia = env.IsNvidia;
        NvApiAvailable = env.NvApiAvailable;
        DrsFilesLocked = env.DrsFilesLocked;

        GpuSummaryText = env.IsNvidia
            ? $"显卡：{env.GpuName} · NVAPI：{(env.NvApiAvailable ? "可用" : "不可用")}"
            : "显卡：未检测到 NVIDIA 设备";

        LockStatusText = env.DrsFilesLocked
            ? "数据库锁定：已只读（驱动无法改写配置——优化不会被重置；修改设置前需先解锁）"
            : env.DrsFilesExist
                ? "数据库锁定：未锁定（驱动可正常保存配置）"
                : "数据库锁定：未找到 nvdrsdb 文件（驱动尚未生成配置数据库）";
    }

    /// <summary>应用全部待更改项。</summary>
    public async Task<OperationResult?> ApplyChangesAsync()
    {
        if (IsApplying || !ApplyEnabled)
        {
            return null;
        }

        var pending = Rows.Where(r => r.PendingChange).ToList();
        if (pending.Count == 0)
        {
            return null;
        }

        IsApplying = true;
        try
        {
            var changes = pending
                .Select(r => (r.Id, Value: SelectedValueOf(r)))
                .ToList();

            var result = await _nv.ApplyGameSettingsAsync(changes);
            StatusText = result.Message;
            if (result.Success)
            {
                // 重读真实状态
                await LoadAsync();
            }

            return result;
        }
        finally
        {
            IsApplying = false;
        }
    }

    private static uint? SelectedValueOf(NvSettingRowVm row)
    {
        // 百分比缩放行：关=删除覆盖（默认）；开=滑条值取整夹回范围
        if (row.IsScale)
        {
            return row.ScaleOverride ? (uint)row.ScaleToInt() : null;
        }

        if (row.SelectedIndex < 0 || row.SelectedIndex >= row.Def.Options.Length)
        {
            return null;
        }

        var value = row.Def.Options[row.SelectedIndex].Value;
        return value == 0 ? null : value; // 选中「默认」= 删除覆盖
    }

    /// <summary>全部恢复默认（删除全部覆盖项，含联动总开关）。</summary>
    public async Task<OperationResult?> ResetAllAsync()
    {
        if (IsApplying || !EnvironmentReady)
        {
            return null;
        }

        IsApplying = true;
        try
        {
            var changes = Rows.Select(r => (r.Id, (uint?)null)).ToList();
            // 联动总开关一并清理
            changes.Add((0x10E41E01, null));
            changes.Add((0x10E41E03, null));

            var result = await _nv.ApplyGameSettingsAsync(changes);
            StatusText = result.Message;
            if (result.Success)
            {
                await LoadAsync();
            }

            return result;
        }
        finally
        {
            IsApplying = false;
        }
    }

    /// <summary>切换 nvdrsdb 只读锁定。</summary>
    public async Task<OperationResult?> SetLockAsync(bool readOnly)
    {
        if (LockBusy)
        {
            return null;
        }

        LockBusy = true;
        try
        {
            var result = await _nv.SetDrsFilesReadOnlyAsync(readOnly);
            LockStatusText = result.Message;
            await LoadEnvironmentAsync();
            RefreshFlags();
            return result;
        }
        finally
        {
            LockBusy = false;
        }
    }
}
