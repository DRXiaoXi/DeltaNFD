using CommunityToolkit.Mvvm.ComponentModel;
using DeltaNFD.Services;

namespace DeltaNFD.ViewModels;

public partial class LabViewModel
{
    public bool ShowIntelPcores { get; private set; }
    public bool CanPreferIntelPcores => !IntelPcoresBusy && !IsLoading && !IsApplying && !SchemeBusy
        && HeteroItems.All(i => !i.IsBusy) && string.Equals(_topology?.Vendor, "Intel", StringComparison.OrdinalIgnoreCase);
    public bool IntelPcoreControlsEnabled => !IntelPcoresBusy && !IsLoading && !IsApplying && !SchemeBusy;
    [ObservableProperty] private bool intelPcoresBusy;
    [ObservableProperty] private string intelPcoresStatusText = "只修改当前计划的调度偏好，交流与电池同时设置；原值保留以便还原。";

    partial void OnIntelPcoresBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPreferIntelPcores));
        OnPropertyChanged(nameof(IntelPcoreControlsEnabled));
        OnPropertyChanged(nameof(CanImportScheme));
        OnPropertyChanged(nameof(CanSwitchScheme));
        OnPropertyChanged(nameof(CanRefresh));
    }

    public async Task SetIntelPcorePreferenceAsync(bool restore = false)
    {
        if (!IntelPcoreControlsEnabled) return;
        if (!restore && !CanPreferIntelPcores) { IntelPcoresStatusText = "请等待当前操作结束；该预设仅适用于本机 Intel 处理器。"; return; }
        IntelPcoresBusy = true;
        IntelPcoresStatusText = restore ? "正在按备份还原策略并核对…" : "正在备份当前计划并设置 0／2／2，完成后回读核对…";
        try
        {
            var result = restore ? await ServiceLocator.Power.RestoreIntelPcoresAsync() : await ServiceLocator.Power.PreferIntelPcoresAsync();
            IntelPcoresStatusText = result.Message;
            await RefreshHeteroPoliciesAsync();
        }
        catch (Exception ex) { IntelPcoresStatusText = "操作未完成，请核对记录或使用还原入口：" + ex.Message; }
        finally { IntelPcoresBusy = false; }
    }
}
