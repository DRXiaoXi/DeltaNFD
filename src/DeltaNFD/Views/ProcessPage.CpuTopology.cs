using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DeltaNFD.Views;

public sealed partial class ProcessPage
{
    private void BuildCpuSelectionPanel(StackPanel host, List<ToggleButton> processorToggles)
        => CpuSelectionPanel.Build(host, processorToggles, _topology, showDetectionNote: host == RuleCpuGroupPanel);
}
