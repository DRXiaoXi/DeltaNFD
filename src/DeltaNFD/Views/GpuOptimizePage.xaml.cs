using DeltaNFD.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class GpuOptimizePage : Page
{
    public GpuOptimizeViewModel ViewModel { get; } = new();

    public GpuOptimizePage()
    {
        InitializeComponent();
    }

    private void ApplyInfoBar_CloseButtonClick(InfoBar sender, object args)
    {
        ViewModel.IsApplyInfoVisible = false;
    }
}
