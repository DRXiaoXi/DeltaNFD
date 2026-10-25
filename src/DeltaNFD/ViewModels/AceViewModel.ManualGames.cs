using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeltaNFD.Inspection;

namespace DeltaNFD.ViewModels;

public partial class AceViewModel
{
    public ObservableCollection<string> ManualGameRoots { get; } = [];
    [ObservableProperty] private bool manualRootsBusy;
    [ObservableProperty] private string manualRootsStatusText = "";
    public string ManualRootCountText => $"手动检索目录（{ManualGameRoots.Count}/{AceManualGameInspection.MaxRoots}）";
    public bool CanManageManualRoots => !ManualRootsBusy && !IsBusy && !IsCleaning && !CoreCheckBusy && !DetectionPaused;

    public AceViewModel() => RefreshManualGameRoots();

    public bool RefreshManualGameRoots()
    {
        try
        {
            var roots = Services.AceGameSearchSettings.Read();
            ManualGameRoots.Clear();
            foreach (var root in roots) ManualGameRoots.Add(root);
            OnPropertyChanged(nameof(ManualRootCountText));
            return true;
        }
        catch (Exception ex) { ManualRootsStatusText = "无法读取手动检索目录：" + ex.Message; return false; }
    }

    partial void OnManualRootsBusyChanged(bool value) => RefreshFlags();
}
