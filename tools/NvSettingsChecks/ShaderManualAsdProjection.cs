// This standalone suite covers NVIDIA settings; the separate manual-ASD suite covers its state machine.
namespace DeltaNFD.ViewModels;
public partial class ShaderViewModel
{
    public bool ManualAsdBusy => false;
    private void RefreshManualFlags() { }
    private Task LoadManualAsdAsync() => Task.CompletedTask;
}
