using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeltaNFD.Inspection;

namespace DeltaNFD.ViewModels;

public partial class AceViewModel
{
    public ObservableCollection<string> ManualGameRoots { get; } = [];
    private string _manualGamePath = "";
    public string ManualGamePath
    {
        get => _manualGamePath;
        set => SetProperty(ref _manualGamePath, value);
    }
    private bool manualRootsBusy;
    public bool ManualRootsBusy
    {
        get => manualRootsBusy;
        set
        {
            if (EqualityComparer<bool>.Default.Equals(manualRootsBusy, value))
            {
                return;
            }

            OnPropertyChanging(nameof(ManualRootsBusy));
            manualRootsBusy = value;
            OnManualRootsBusyChanged(value);
            OnPropertyChanged(nameof(ManualRootsBusy));
        }
    }
    partial void OnManualRootsBusyChanged(bool value);

    private string manualRootsStatusText = "";
    public string ManualRootsStatusText
    {
        get => manualRootsStatusText;
        set => SetProperty(ref manualRootsStatusText, value);
    }

    public string ManualRootCountText => $"手动检索目录（{ManualGameRoots.Count}/{AceManualGameInspection.MaxRoots}）";
    public bool CanManageManualRoots => !ManualRootsBusy && !IsBusy && !IsCleaning && !CoreCheckBusy && !DetectionPaused;

    public AceViewModel() : this(Services.ServiceLocator.Ace) { }
    internal AceViewModel(Services.IAceService ace)
    {
        _ace = ace;
        RefreshManualGameRoots();
    }

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
