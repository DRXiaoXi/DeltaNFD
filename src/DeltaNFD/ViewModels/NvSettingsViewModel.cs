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

    // DLSS 说明由标题后的帮助图标展示；不改变设置目录、选项和写入计划。
    public bool IsDlss => Id is 0x10E41DF3 or 0x10E41DF5 or 0x10E41DF1 or 0x10308298;
    public string TitleHelpText => IsDlss ? Description : "";
    public bool ShowDescription => !IsDlss;

    public ObservableCollection<string> OptionLabels { get; } = new();

    /// <summary>百分比缩放模式（33–100% 滑条任意调节，如 DLSS 超分渲染比例）。</summary>
    public bool IsScale => Def.Kind == NvSettingKind.PercentScale;

    /// <summary>下拉候选模式（非缩放行显示 ComboBox）。</summary>
    public bool ShowComboBox => !IsScale;

    /// <summary>缩放范围（供滑条 Min/Max 绑定）。</summary>
    public double ScaleMin => Def.ScaleMin;

    public double ScaleMax => Def.ScaleMax;

    /// <summary>驱动是否支持该设置（按读取结果推断；不支持时置灰显示说明）。</summary>
    private bool supported = true;
    public bool Supported
    {
        get => supported;
        set => SetProperty(ref supported, value);
    }

    /// <summary>当前覆盖值对应的选项下标（-1 = 无覆盖/默认，或覆盖值不在候选列表中）。</summary>
    private int selectedIndex = -1;
    public int SelectedIndex
    {
        get => selectedIndex;
        set
        {
            if (!EqualityComparer<int>.Default.Equals(selectedIndex, value))
            {
                OnPropertyChanging(nameof(SelectedIndex));
                selectedIndex = value;
                OnSelectedIndexChanged(value);
                OnPropertyChanged(nameof(SelectedIndex));
            }
        }
    }
    partial void OnSelectedIndexChanged(int value);

    // ---------------- 百分比缩放模式状态 ----------------

    /// <summary>是否覆盖渲染比例（关 = 默认，交还游戏内档位决定）。</summary>
    private bool scaleOverride;
    public bool ScaleOverride
    {
        get => scaleOverride;
        set
        {
            if (!EqualityComparer<bool>.Default.Equals(scaleOverride, value))
            {
                OnPropertyChanging(nameof(ScaleOverride));
                scaleOverride = value;
                OnScaleOverrideChanged(value);
                OnPropertyChanged(nameof(ScaleOverride));
            }
        }
    }
    partial void OnScaleOverrideChanged(bool value);

    /// <summary>滑条所选百分比（应用时四舍五入取整并夹回范围）。</summary>
    private double selectedScale = 66;
    public double SelectedScale
    {
        get => selectedScale;
        set
        {
            if (!EqualityComparer<double>.Default.Equals(selectedScale, value))
            {
                OnPropertyChanging(nameof(SelectedScale));
                selectedScale = value;
                OnSelectedScaleChanged(value);
                OnPropertyChanged(nameof(SelectedScale));
            }
        }
    }
    partial void OnSelectedScaleChanged(double value);

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
    private string stateText = "读取中…";
    public string StateText
    {
        get => stateText;
        set => SetProperty(ref stateText, value);
    }

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

    private bool isSelectingOption;
    public bool IsSelectingOption
    {
        get => isSelectingOption;
        private set { if (SetProperty(ref isSelectingOption, value)) RefreshFlags(); }
    }
    private readonly Dictionary<uint, uint?> _rebarAcknowledgements = [];
    private uint? _displayedDriverVersion;
    public bool CanEditOptions => EnvironmentReady && !IsLoading && !IsApplying && !IsSelectingOption && !LockBusy;
    public bool CanResetProfile => CanEditOptions && !DrsFilesLocked && !ServiceLocator.GameTarget.Current.IsCustom;

    private bool isLoading;
    public bool IsLoading
    {
        get => isLoading;
        set
        {
            if (!EqualityComparer<bool>.Default.Equals(isLoading, value))
            {
                OnPropertyChanging(nameof(IsLoading));
                isLoading = value;
                OnIsLoadingChanged(value);
                OnPropertyChanged(nameof(IsLoading));
            }
        }
    }
    partial void OnIsLoadingChanged(bool value);

    private bool isApplying;
    public bool IsApplying
    {
        get => isApplying;
        set
        {
            if (!EqualityComparer<bool>.Default.Equals(isApplying, value))
            {
                OnPropertyChanging(nameof(IsApplying));
                isApplying = value;
                OnIsApplyingChanged(value);
                OnPropertyChanged(nameof(IsApplying));
            }
        }
    }
    partial void OnIsApplyingChanged(bool value);

    private string statusText = "正在检测 NVIDIA 环境…";
    public string StatusText
    {
        get => statusText;
        set => SetProperty(ref statusText, value);
    }

    private string gpuSummaryText = "";
    public string GpuSummaryText
    {
        get => gpuSummaryText;
        set => SetProperty(ref gpuSummaryText, value);
    }

    private bool isNvidia;
    public bool IsNvidia
    {
        get => isNvidia;
        set
        {
            if (!EqualityComparer<bool>.Default.Equals(isNvidia, value))
            {
                OnPropertyChanging(nameof(IsNvidia));
                isNvidia = value;
                OnIsNvidiaChanged(value);
                OnPropertyChanged(nameof(IsNvidia));
            }
        }
    }
    partial void OnIsNvidiaChanged(bool value);

    private bool nvApiAvailable;
    public bool NvApiAvailable
    {
        get => nvApiAvailable;
        set
        {
            if (!EqualityComparer<bool>.Default.Equals(nvApiAvailable, value))
            {
                OnPropertyChanging(nameof(NvApiAvailable));
                nvApiAvailable = value;
                OnNvApiAvailableChanged(value);
                OnPropertyChanged(nameof(NvApiAvailable));
            }
        }
    }
    partial void OnNvApiAvailableChanged(bool value);

    private bool drsFilesLocked;
    public bool DrsFilesLocked
    {
        get => drsFilesLocked;
        set
        {
            if (!EqualityComparer<bool>.Default.Equals(drsFilesLocked, value))
            {
                OnPropertyChanging(nameof(DrsFilesLocked));
                drsFilesLocked = value;
                OnDrsFilesLockedChanged(value);
                OnPropertyChanged(nameof(DrsFilesLocked));
            }
        }
    }
    partial void OnDrsFilesLockedChanged(bool value);

    private string lockStatusText = "";
    public string LockStatusText
    {
        get => lockStatusText;
        set => SetProperty(ref lockStatusText, value);
    }

    private bool lockBusy;
    public bool LockBusy
    {
        get => lockBusy;
        set { if (SetProperty(ref lockBusy, value)) RefreshFlags(); }
    }

    private bool shaderCacheNonDefault;
    public bool ShaderCacheNonDefault
    {
        get => shaderCacheNonDefault;
        set
        {
            if (!EqualityComparer<bool>.Default.Equals(shaderCacheNonDefault, value))
            {
                OnPropertyChanging(nameof(ShaderCacheNonDefault));
                shaderCacheNonDefault = value;
                OnShaderCacheNonDefaultChanged(value);
                OnPropertyChanged(nameof(ShaderCacheNonDefault));
            }
        }
    }
    partial void OnShaderCacheNonDefaultChanged(bool value);

    private bool shaderCacheCheckFailed;
    public bool ShaderCacheCheckFailed
    {
        get => shaderCacheCheckFailed;
        set
        {
            if (!EqualityComparer<bool>.Default.Equals(shaderCacheCheckFailed, value))
            {
                OnPropertyChanging(nameof(ShaderCacheCheckFailed));
                shaderCacheCheckFailed = value;
                OnShaderCacheCheckFailedChanged(value);
                OnPropertyChanged(nameof(ShaderCacheCheckFailed));
            }
        }
    }
    partial void OnShaderCacheCheckFailedChanged(bool value);

    private string shaderCacheAdvice = "正在检测全局及三角洲着色器缓存大小…";
    public string ShaderCacheAdvice
    {
        get => shaderCacheAdvice;
        set => SetProperty(ref shaderCacheAdvice, value);
    }

    public bool EnvironmentReady => IsNvidia && NvApiAvailable;
    public bool ShowShaderCacheStatus => EnvironmentReady && !ShaderCacheNonDefault && !ShaderCacheCheckFailed;

    partial void OnIsNvidiaChanged(bool value) { OnPropertyChanged(nameof(EnvironmentReady)); OnPropertyChanged(nameof(ShowShaderCacheStatus)); }
    partial void OnNvApiAvailableChanged(bool value) { OnPropertyChanged(nameof(EnvironmentReady)); OnPropertyChanged(nameof(ShowShaderCacheStatus)); }
    partial void OnShaderCacheNonDefaultChanged(bool value) => OnPropertyChanged(nameof(ShowShaderCacheStatus));
    partial void OnShaderCacheCheckFailedChanged(bool value) => OnPropertyChanged(nameof(ShowShaderCacheStatus));

    public int PendingCount => Rows.Count(r => r.PendingChange);

    public bool HasPendingChanges => PendingCount > 0;

    public bool ApplyEnabled => CanEditOptions && HasPendingChanges && !DrsFilesLocked;

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
        OnPropertyChanged(nameof(CanEditOptions));
        OnPropertyChanged(nameof(CanResetProfile));
    }

    /// <summary>行选项变化时由页面调用，刷新待应用计数。</summary>
    public void OnRowSelectionChanged() => RefreshFlags();

    /// <summary>来自页面的用户选择。先确认再改待应用值；不写驱动配置。</summary>
    public async Task<bool> SelectOptionAsync(NvSettingRowVm row, int index, Func<string, Task<bool>>? confirmRebar = null)
    {
        if (!CanEditOptions || !Rows.Contains(row) || index < 0 || index >= row.Def.Options.Length) return false;
        if (index == row.SelectedIndex) return true;
        IsSelectingOption = true;
        try
        {
            var accepted = false;
            var selected = row.Def.Options[index].Value;
            if (row.Id == NvDriverSettingPolicy.RebarPolicyId
                && NvDriverSettingPolicy.Validate([(row.Id, selected)], _displayedDriverVersion, true) is { } validation)
            {
                StatusText = validation;
                return false;
            }
            if (NvDriverSettingPolicy.IsRebarEnable(row.Id, selected))
            {
                // 以页面已读取的版本立即提示；应用时仍重新读取实际版本，防止旧确认被复用。
                if (NvDriverSettingPolicy.RebarWarning(_displayedDriverVersion, [(row.Id, selected)]) is { } warning)
                {
                    if (confirmRebar is null || !await confirmRebar(warning))
                    {
                        StatusText = "已取消开启 ReBAR，保留原选项；未写入驱动配置。";
                        return false;
                    }
                    accepted = true;
                }
            }
            if (NvDriverSettingPolicy.IsRebarSetting(row.Id))
            {
                _rebarAcknowledgements.Remove(row.Id);
                if (accepted) _rebarAcknowledgements[row.Id] = _displayedDriverVersion;
            }
            row.SelectedIndex = index;
            return true;
        }
        catch (Exception ex)
        {
            StatusText = "未更改选项，请重试：" + ex.Message;
            return false;
        }
        finally { IsSelectingOption = false; }
    }

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
        if (IsLoading || IsSelectingOption)
        {
            return;
        }

        IsLoading = true;
        _rebarAcknowledgements.Clear();
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
                var v2Unavailable = row.Id == NvDriverSettingPolicy.RebarPolicyId
                    && !(_displayedDriverVersion >= NvDriverSettingPolicy.RebarMinimumDriver);
                const string v2VersionNote = "v2 需确认的 616.56 及以上驱动；当前仅可恢复驱动默认。";
                if (!values.TryGetValue(row.Id, out var state))
                {
                    if (row.IsScale)
                    {
                        row.SetActualScale(false, 0, "未知");
                    }
                    else
                    {
                        row.SetActual(-1, v2Unavailable ? v2VersionNote : "未知");
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
                    text = "当前：驱动默认（本游戏未覆盖此项）";
                    index = 0; // 继承值只供说明，下拉必须停在「删除覆盖」，不能伪装成显式覆盖。
                }
                else
                {
                    text = index >= 0
                        ? $"当前：已覆盖 → {def.Options[index].Label}"
                        : $"当前：未知或联动不一致的覆盖（0x{value:X}）；选择新值或恢复默认以处理";
                }

                row.SetActual(index, text + (v2Unavailable ? "\n" + v2VersionNote : ""));
            }

            StatusText = $"就绪 · 配置目标：{ServiceLocator.GameTarget.Current.DisplayName}（首次应用时自动创建独立配置）";
        }
        catch (Exception ex)
        {
            foreach (var row in Rows)
            {
                if (row.IsScale) row.SetActualScale(false, 0, "读取失败");
                else row.SetActual(-1, "读取失败");
            }
            StatusText = "驱动状态读取失败，请刷新重试：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadEnvironmentAsync()
    {
        NvGpuEnvironmentInfo env;
        try { env = await _nv.GetEnvironmentAsync(); }
        catch
        {
            _displayedDriverVersion = null;
            ShaderCacheNonDefault = false; ShaderCacheCheckFailed = true;
            ShaderCacheAdvice = "着色器缓存大小读取失败，无法确认全局及三角洲是否为驱动默认。请点「刷新状态」重试。";
            throw;
        }
        IsNvidia = env.IsNvidia;
        NvApiAvailable = env.NvApiAvailable;
        DrsFilesLocked = env.DrsFilesLocked;
        _displayedDriverVersion = env.DriverVersion;
        ShaderCacheNonDefault = env.ShaderCache?.HasNonDefault == true;
        ShaderCacheCheckFailed = env.IsNvidia && env.ShaderCache?.Complete != true && !ShaderCacheNonDefault;
        ShaderCacheAdvice = !env.IsNvidia ? "未检测到 NVIDIA 显卡，无需检查 N 卡着色器缓存大小。"
            : env.ShaderCache?.Advice ?? "后端未返回着色器缓存大小，无法确认是否为默认；请更新后端并刷新状态。";

        GpuSummaryText = env.IsNvidia
            ? $"显卡：{env.GpuName} · 驱动：{NvDriverSettingPolicy.FormatDriver(env.DriverVersion)} · NVAPI：{(env.NvApiAvailable ? "可用" : "不可用")}"
            : "显卡：未检测到 NVIDIA 设备";

        LockStatusText = env.DrsFilesLocked
            ? "数据库锁定：已只读（驱动无法改写配置——优化不会被重置；修改设置前需先解锁）"
            : env.DrsFilesExist
                ? "数据库锁定：未锁定（驱动可正常保存配置）"
                : "数据库锁定：未找到 nvdrsdb 文件（驱动尚未生成配置数据库）";
    }

    /// <summary>应用全部待更改项。</summary>
    public async Task<OperationResult?> ApplyChangesAsync(Func<string, Task<bool>>? confirmRebar = null)
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
        var submitted = false;
        try
        {
            var changes = pending
                .Select(r => (r.Id, Value: SelectedValueOf(r)))
                .ToList();

            var accepted = false;
            if (changes.Any(c => NvDriverSettingPolicy.IsRebarEnable(c.Id, c.Value)
                || c.Id == NvDriverSettingPolicy.RebarPolicyId && c.Value is not null))
            {
                // 应用时重新读取，不能依赖打开页面时缓存的版本。
                var env = await _nv.GetEnvironmentAsync();
                if (NvDriverSettingPolicy.Validate(changes, env.DriverVersion, true) is { } validation)
                {
                    StatusText = validation;
                    return OperationResult.Fail(validation);
                }
                if (NvDriverSettingPolicy.RebarWarning(env.DriverVersion, changes) is { } warning)
                {
                    accepted = changes.Where(c => NvDriverSettingPolicy.IsRebarEnable(c.Id, c.Value))
                        .All(c => _rebarAcknowledgements.TryGetValue(c.Id, out var acknowledgedDriver) && acknowledgedDriver == env.DriverVersion);
                    if (!accepted && (confirmRebar is null || !await confirmRebar(warning)))
                    {
                        StatusText = "已取消应用；待更改项已保留。";
                        return null;
                    }
                    accepted = true;
                }
            }
            submitted = true;
            var result = await _nv.ApplyGameSettingsAsync(changes, accepted);
            StatusText = result.Message;
            if (result.Success)
            {
                // 重读真实状态
                await LoadAsync();
            }

            return result;
        }
        catch (Exception ex)
        {
            StatusText = "未确认应用完成，请刷新核对：" + ex.Message;
            return new OperationResult { Success = false, OutcomeUnknown = submitted, Message = StatusText };
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
        return value; // null=删除覆盖；0=显式关闭/自适应/G-SYNC 等合法驱动值。
    }

    /// <summary>确认后完整重置游戏配置；旧的本页恢复默认仍保留原范围。</summary>
    public async Task<OperationResult?> ResetProfileForFlickerAsync(Func<Task<bool>> confirm, Func<bool> isCurrent)
    {
        if (!CanResetProfile || !isCurrent()) return null;
        IsApplying = true;
        var submitted = false;
        try
        {
            if (!await confirm() || !isCurrent()) return null;
            if (LockBusy || DrsFilesLocked || IsLoading || IsSelectingOption || !EnvironmentReady
                || ServiceLocator.GameTarget.Current.IsCustom) return null;
            StatusText = "正在备份并重置三角洲专用驱动配置…";
            submitted = true;
            var result = await _nv.ResetDeltaProfileAsync();
            _rebarAcknowledgements.Clear();
            if (isCurrent())
            {
                // Refresh all rows, even for an unknown outcome; never leave stale enabled values visible.
                await LoadAsync();
                if (isCurrent()) StatusText = result.Message;
            }
            return result;
        }
        catch (Exception ex)
        {
            var message = "防闪屏性重置未确认完成：" + ex.Message;
            if (isCurrent())
            {
                if (submitted)
                {
                    _rebarAcknowledgements.Clear();
                    foreach (var row in Rows)
                    {
                        if (row.IsScale) row.SetActualScale(false, 0, "未确认，待核对");
                        else row.SetActual(-1, "未确认，待核对");
                    }
                }
                StatusText = message;
            }
            return new OperationResult { Success = false, OutcomeUnknown = submitted, Message = message };
        }
        finally { IsApplying = false; }
    }

    /// <summary>本页管理项恢复默认（含联动总开关）。</summary>
    public async Task<OperationResult?> ResetAllAsync()
    {
        if (IsApplying || IsSelectingOption || !EnvironmentReady)
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
            changes.AddRange(NvDriverSettingPolicy.RetiredManagedIds.Select(id => (id, (uint?)null)));

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
        if (LockBusy || IsSelectingOption || IsApplying || IsLoading)
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
