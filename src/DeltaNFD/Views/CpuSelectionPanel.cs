using DeltaNFD.Backends;
using DeltaNFD.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DeltaNFD.Views;

/// <summary>逻辑 CPU 选择面板（进程管理与调度改善共用）：按处理器组列出逻辑 CPU，并在组掩码文本之间转换。</summary>
internal static class CpuSelectionPanel
{
    internal static void Build(StackPanel host, List<ToggleButton> processorToggles, ProcmanTopologyResult? source, bool showDetectionNote)
    {
        host.Children.Clear();
        processorToggles.Clear();
        if (source is not { Groups.Count: > 0 } topology)
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
        if (showDetectionNote && topology.DetectionNote.Length > 0)
            host.Children.Add(new TextBlock { Text = topology.DetectionNote, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        UpdateCount();
    }

    /// <summary>把勾选的逻辑 CPU 转为规则值 g{组}:0x{掩码};…（未勾选返回空串）。</summary>
    internal static string ToRuleValue(IEnumerable<ToggleButton> processorToggles)
    {
        var selected = processorToggles.Where(t => t.IsChecked == true).Select(t => (ProcmanGroupMask)t.Tag!);
        var masks = selected.GroupBy(m => m.Group).Select(g => new ProcmanGroupMask(g.Key, g.Aggregate(0UL, (value, cpu) => value | cpu.Mask)));
        return string.Join(";", masks.OrderBy(m => m.Group).Select(m => $"g{m.Group}:0x{m.Mask:X}"));
    }

    /// <summary>按规则值勾选逻辑 CPU；无法解析的片段跳过。</summary>
    internal static void Select(IEnumerable<ToggleButton> processorToggles, string value)
    {
        var toggles = processorToggles.ToList();
        foreach (var toggle in toggles) toggle.IsChecked = false;
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Trim().Split(':', 2);
            if (pieces.Length != 2 || !pieces[0].StartsWith('g') || !ushort.TryParse(pieces[0][1..], out var group)) continue;
            if (!ulong.TryParse(pieces[1].Replace("0x", "", StringComparison.OrdinalIgnoreCase), System.Globalization.NumberStyles.HexNumber, null, out var mask)) continue;
            foreach (var toggle in toggles.Where(t => t.Tag is ProcmanGroupMask cpu && cpu.Group == group))
                toggle.IsChecked = (mask & ((ProcmanGroupMask)toggle.Tag!).Mask) != 0;
        }
    }
}
