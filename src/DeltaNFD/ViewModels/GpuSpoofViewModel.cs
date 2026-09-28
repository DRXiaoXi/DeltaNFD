using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeltaNFD.Services;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.ViewModels;

/// <summary>显卡伪装页的行模型：一枚显示适配器。</summary>
public partial class AdapterVm : ObservableObject
{
    public required DisplayAdapterInfo Info { get; init; }

    public string DisplayName => Info.DisplayName;
    public string DeviceDesc => Info.DeviceDesc;
    public string RegistryPath => Info.RegistryPath;
    public bool IsMasked => Info.IsMasked;
    public bool HasBackup => Info.HasBackup;

    public string StatusText => Info.IsMasked
        ? (Info.HasBackup ? "已伪装 · 有备份" : "已伪装")
        : (Info.HasBackup ? "未伪装 · 有备份" : "未伪装");
}

public partial class GpuSpoofViewModel : ObservableObject
{
    private readonly IGpuSpoofService _spoof = ServiceLocator.GpuSpoof;

    /// <summary>帧格服务：临时伪装的登录自启动、重启与显卡重载。</summary>
    public IFrameService Frame { get; } = ServiceLocator.Frame;

    public ObservableCollection<AdapterVm> Adapters { get; } = new();

    public string[] Presets { get; }

    /// <summary>生效方式选项：永久生效 / 临时。</summary>
    public string[] EffectiveModeOptions { get; } = ["永久生效", "临时"];

    /// <summary>临时模式的生效时机选项：立即重载显卡（不重启）/ 重启生效（登录后自动还原原型号）。</summary>
    public string[] DirectApplyModeOptions { get; } = ["立即重载显卡（不重启）", "重启生效"];

    [ObservableProperty] private AdapterVm? selectedAdapter;
    [ObservableProperty] private int selectedPresetIndex;
    [ObservableProperty] private string customName = "";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string adapterSummaryText = "正在检测显卡…";
    [ObservableProperty] private bool isResultVisible;
    [ObservableProperty] private string resultText = "";
    [ObservableProperty] private InfoBarSeverity resultSeverity = InfoBarSeverity.Success;
    [ObservableProperty] private int effectiveModeIndex;
    [ObservableProperty] private int directApplyModeIndex;

    /// <summary>是否为永久生效模式（默认）。</summary>
    public bool IsPermanentMode => EffectiveModeIndex == 0;

    /// <summary>是否为临时模式。</summary>
    public bool IsTemporaryMode => EffectiveModeIndex == 1;

    /// <summary>临时模式是否选择"重启生效"（重启前伪装，登录后自动还原原型号）。</summary>
    public bool IsDirectApplyRestart => DirectApplyModeIndex == 1;

    /// <summary>根据选择的生效时机显示主操作按钮。</summary>
    public string PrimaryActionText => IsTemporaryMode
        ? (IsDirectApplyRestart ? "伪装并重启" : "伪装并重载")
        : "伪装为所选型号";

    partial void OnEffectiveModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsPermanentMode));
        OnPropertyChanged(nameof(IsTemporaryMode));
        OnPropertyChanged(nameof(PrimaryActionText));
        RefreshRiskInfo();
    }

    partial void OnDirectApplyModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsDirectApplyRestart));
        OnPropertyChanged(nameof(PrimaryActionText));
        RefreshRiskInfo();
    }

    /// <summary>重载显卡黑屏风险提示可见性（选择了「立即重载」类时机时显示）。</summary>
    public bool ShowReloadRisk => IsTemporaryMode && DirectApplyModeIndex == 0;

    private void RefreshRiskInfo() => OnPropertyChanged(nameof(ShowReloadRisk));

    /// <summary>实际生效的伪装型号：自定义输入非空时优先，否则用预置下拉框选择。</summary>
    public string EffectiveName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(CustomName))
            {
                return CustomName.Trim();
            }

            return SelectedPresetIndex >= 0 && SelectedPresetIndex < Presets.Length
                ? Presets[SelectedPresetIndex]
                : "";
        }
    }

    public bool CanRefresh => !IsBusy;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanRefresh));

    partial void OnCustomNameChanged(string value) => OnPropertyChanged(nameof(EffectiveName));

    partial void OnSelectedPresetIndexChanged(int value) => OnPropertyChanged(nameof(EffectiveName));

    public GpuSpoofViewModel()
    {
        Presets = _spoof.PresetNames.ToArray();
        _ = RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var selectedPath = SelectedAdapter?.RegistryPath;
            var adapters = await _spoof.GetAdaptersAsync();

            SelectedAdapter = null;
            Adapters.Clear();
            foreach (var adapter in adapters)
            {
                Adapters.Add(new AdapterVm { Info = adapter });
            }

            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                SelectedAdapter = Adapters.FirstOrDefault(a =>
                    a.RegistryPath.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
            }

            AdapterSummaryText = adapters.Count == 0
                ? "未检测到显示适配器"
                : $"共检测到 {adapters.Count} 个显示适配器，请先在列表中选中要操作的显卡";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>伪装选中的显卡。确认对话框由页面负责，这里只做业务校验与执行。</summary>
    public Task<OperationResult> SpoofSelectedAsync()
    {
        if (SelectedAdapter is null)
        {
            return Task.FromResult(OperationResult.Fail("请先在列表中选择要伪装的显卡。"));
        }

        var name = EffectiveName;
        if (string.IsNullOrWhiteSpace(name))
        {
            return Task.FromResult(OperationResult.Fail("请选择预置型号，或输入自定义型号。"));
        }

        return RunWithBusyAsync(() => _spoof.SpoofAsync(SelectedAdapter.Info, name));
    }

    /// <summary>恢复选中显卡的原始型号（需本工具备份）。</summary>
    public Task<OperationResult> RestoreSelectedAsync()
    {
        if (SelectedAdapter is null)
        {
            return Task.FromResult(OperationResult.Fail("请先在列表中选择要恢复的显卡。"));
        }

        if (!SelectedAdapter.HasBackup)
        {
            return Task.FromResult(OperationResult.Fail("该显卡没有本工具记录的备份（可能未用本工具伪装过）。"));
        }

        return RestoreByPathAsync(SelectedAdapter.RegistryPath);
    }

    /// <summary>按注册表路径恢复显卡，用于重启任务创建失败后的回滚。</summary>
    public Task<OperationResult> RestoreByPathAsync(string registryPath)
    {
        if (string.IsNullOrWhiteSpace(registryPath))
        {
            return Task.FromResult(OperationResult.Fail("显卡注册表路径无效，无法恢复。"));
        }

        return RunWithBusyAsync(() => _spoof.RestoreByPathAsync(registryPath));
    }

    public void ShowResult(OperationResult result)
    {
        IsResultVisible = true;
        ResultText = result.Message;
        ResultSeverity = result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
    }

    /// <summary>永久或临时伪装写入成功后，清除旧版保存的帧格伪装配置。</summary>
    public void ClearFrameSpoof()
    {
        Frame.ClearFrameSpoofConfig();
    }

    private async Task<OperationResult> RunWithBusyAsync(Func<Task<OperationResult>> action)
    {
        if (IsBusy)
        {
            return OperationResult.Fail("正在执行其他操作，请稍候。");
        }

        IsBusy = true;
        try
        {
            var result = await action();

            // 操作成功后重新枚举，让列表状态（已伪装 / 有备份）即时刷新
            if (result.Success)
            {
                IsBusy = false;
                await RefreshAsync();
                IsBusy = true;
            }

            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
