using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DeltaNFD.Views;

public sealed partial class ProcessPage
{
    private bool _ruleSaveBusy;
    private readonly List<ToggleButton> _ruleProcessorToggles = [];

    private void UseRuleCpus_Click(object sender, RoutedEventArgs e)
    {
        RuleValueBox.Text = CpuSelectionPanel.ToRuleValue(_ruleProcessorToggles);
        if (RuleValueBox.Text.Length == 0) RuleNoteText.Text = "请至少选择一个逻辑 CPU。";
    }

    private void SetRuleCpuSelection(string value) => CpuSelectionPanel.Select(_ruleProcessorToggles, value);
}
