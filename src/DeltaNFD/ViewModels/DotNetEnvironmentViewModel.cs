using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeltaNFD.Services;

namespace DeltaNFD.ViewModels;

public partial class DotNetEnvironmentViewModel : ObservableObject
{
    // Keep progress and the in-flight installer visible when navigating away and back.
    public static DotNetEnvironmentViewModel Current { get; } = new();
    [ObservableProperty] private string status = "尚未检测";
    [ObservableProperty] private string details = "";
    [ObservableProperty] private bool isBusy;
    public bool CanAct => !IsBusy;
    private CancellationTokenSource? _cancel;
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanAct));

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true; Status = "正在检查工具所需 .NET 文件…";
        try
        {
            var result = await DotNetEnvironmentService.CheckAsync();
            Status = result.Summary; Details = result.Details;
        }
        catch (Exception ex) { Status = ".NET 检测失败：" + ex.Message; }
        finally { IsBusy = false; }
    }

    public async Task RepairAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        using var cancellation = new CancellationTokenSource(); _cancel = cancellation;
        try
        {
            Status = await DotNetRepairService.RepairAsync(new Progress<string>(message => Status = message), cancellation.Token);
            var result = await DotNetEnvironmentService.CheckAsync();
            Details = result.Details;
        }
        catch (Exception ex) { Status = ".NET 检测失败：" + ex.Message; }
        finally { _cancel = null; IsBusy = false; }
    }

    [RelayCommand] private void CancelDownload() => _cancel?.Cancel();
}
