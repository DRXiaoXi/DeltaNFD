using DeltaNFD.Backends;
using DeltaNFD.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DeltaNFD.Views;

public sealed partial class ProcessPage
{
    private void BuildCpuSelectionPanel(StackPanel host, List<ToggleButton> processorToggles)
    {
        host.Children.Clear();
        processorToggles.Clear();
        if (_topology is not { Groups.Count: > 0 } topology)
        {
            host.Children.Add(new TextBlock { Text = "拓扑不可用，CPU 选择已禁用。", FontSize = 12 });
            return;
        }
        host.Children.Add(new TextBlock
        {
            Text = topology.ProcessorName.Length > 0 ? topology.ProcessorName : "本机 CPU",
            FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap
        });
        var selected = new TextBlock { FontSize = 11, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        void UpdateCount() => selected.Text = $"已选 {processorToggles.Count(t => t.IsChecked == true)} / {processorToggles.Count} 个逻辑 CPU";
        // 每行两项；窄详情面板中仍可完整显示中文标签并支持键盘。
        var presets = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        presets.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        presets.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var position = 0;
        void AddPreset(string label, Action select)
        {
            var button = new Button
            {
                Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11, Padding = new Thickness(6, 5, 6, 5), MinWidth = 0
            };
            button.Click += (_, _) => { select(); UpdateCount(); };
            if (position % 2 == 0) presets.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(button, position / 2); Grid.SetColumn(button, position % 2); position++;
            presets.Children.Add(button);
        }
        AddPreset("全选", () => processorToggles.ForEach(t => t.IsChecked = true));
        AddPreset("清空", () => processorToggles.ForEach(t => t.IsChecked = false));
        AddPreset("反选", () => processorToggles.ForEach(t => t.IsChecked = t.IsChecked != true));
        foreach (var preset in ProcmanCpuSelection.Presets(topology))
            AddPreset(preset.Label, () =>
            {
                foreach (var toggle in processorToggles)
                    toggle.IsChecked = toggle.Tag is ProcmanGroupMask cpu && preset.Masks.Any(m => m.Group == cpu.Group && (m.Mask & cpu.Mask) != 0);
            });
        host.Children.Add(presets);
        host.Children.Add(new TextBlock
        {
            Text = "快捷选择会替换当前勾选；选择后点击应用或使用所选 CPU 才会生效。C 为物理核，T 为该核线程；悬停查看完整拓扑。",
            FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap
        });
        host.Children.Add(selected);
        foreach (var group in topology.Groups)
        {
            var panel = new StackPanel { Spacing = 6 };
            var all = new Button { Content = $"选择整组 {group.Group}", FontSize = 11 };
            all.Click += (_, _) =>
            {
                foreach (var toggle in processorToggles.Where(t => t.Tag is ProcmanGroupMask m && m.Group == group.Group)) toggle.IsChecked = true;
                UpdateCount();
            };
            panel.Children.Add(all);
            var grid = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
            // 四列保证 CPU 63、CCD 和线程文字不挤在八列的窄面板里。
            for (var col = 0; col < 4; col++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var index = 0;
            for (var bit = 0; bit < 64; bit++)
            {
                var mask = 1UL << bit;
                if ((group.Mask & mask) == 0) continue;
                var cpu = topology.Processors.FirstOrDefault(c => c.Group == group.Group && c.Index == bit)
                    ?? new ProcmanLogicalCpu { Group = group.Group, Index = bit };
                var caption = $"CPU {bit}" + (cpu.CoreKind.Length > 0 ? $" · {cpu.CoreKind}" : "");
                caption += cpu.Core is { } core ? $"\nC{core} · T{cpu.Thread}" : "\n核未知";
                if (cpu.Ccd is { } ccd) caption += $"\nCCD{ccd}";
                else if (cpu.L3 is { } l3) caption += $"\nL3:{l3}";
                var toggle = new ToggleButton
                {
                    Content = new TextBlock { Text = caption, FontSize = 10, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                    MinWidth = 0, MinHeight = 48, Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Stretch,
                    Tag = new ProcmanGroupMask(group.Group, mask)
                };
                var description = ProcmanCpuSelection.Describe(cpu);
                ToolTipService.SetToolTip(toggle, description);
                AutomationProperties.SetName(toggle, description.Replace('\n', ' '));
                toggle.Checked += (_, _) => UpdateCount();
                toggle.Unchecked += (_, _) => UpdateCount();
                if (index % 4 == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetColumn(toggle, index % 4); Grid.SetRow(toggle, index / 4); index++;
                processorToggles.Add(toggle);
                grid.Children.Add(toggle);
            }
            panel.Children.Add(grid);
            host.Children.Add(panel);
        }
        if (host == RuleCpuGroupPanel && topology.DetectionNote.Length > 0)
            host.Children.Add(new TextBlock { Text = topology.DetectionNote, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        UpdateCount();
    }
}
