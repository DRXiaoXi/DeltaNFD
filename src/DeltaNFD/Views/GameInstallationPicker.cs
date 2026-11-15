using DeltaNFD.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

internal static class GameInstallationPicker
{
    internal static async Task RunAsync(XamlRoot root, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            Log.Error("所选安装目录操作未完成", ex);
            await new ContentDialog { XamlRoot = root, Title = "操作未完成", Content = ex.Message,
                CloseButtonText = "知道了" }.ShowWithBackendRecoveryAsync();
        }
    }

    internal static async Task<GameInstallationSelection?> PickAsync(XamlRoot root, string operation, bool sharedOnly = false)
    {
        try
        {
            var inventory = await Task.Run(GameInstallationScope.ReadInventory);
            if (inventory.Length < 2) return GameInstallationScope.Select(inventory, null, all: true);
            var choices = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch,
                PlaceholderText = "请选择操作目录", MaxDropDownHeight = 320 };
            foreach (var path in inventory)
                choices.Items.Add(new ComboBoxItem { Tag = path, IsEnabled = !sharedOnly,
                    Content = new TextBlock { Text = path, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 } });
            choices.Items.Add(new ComboBoxItem { Tag = "", Content = $"全部 {inventory.Length} 个三角洲目录" });
            var content = new StackPanel { Spacing = 12, MinWidth = 320, MaxWidth = 500 };
            content.Children.Add(new TextBlock { Text = sharedOnly
                ? "此操作针对系统共享组件，无法按单个安装隔离。选择全部目录后，还需确认共享影响范围。"
                : "选择本次操作的三角洲安装目录，也可以一起操作全部目录。此选择不会更改默认游戏目录。",
                TextWrapping = TextWrapping.Wrap });
            content.Children.Add(choices);
            var dialog = new ContentDialog { XamlRoot = root, Title = operation + " · 选择目录", Content = content,
                PrimaryButtonText = "继续", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
                IsPrimaryButtonEnabled = false };
            choices.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = choices.SelectedItem is ComboBoxItem;
            if (await dialog.ShowWithBackendRecoveryAsync() != ContentDialogResult.Primary ||
                choices.SelectedItem is not ComboBoxItem { Tag: string selected }) return null;
            var selection = GameInstallationScope.Select(inventory, selected, all: selected.Length == 0);
            using var validation = GameInstallationScope.Enter(selection);
            return selection;
        }
        catch (Exception ex)
        {
            await new ContentDialog { XamlRoot = root, Title = "目录选择未完成", Content = ex.Message,
                CloseButtonText = "知道了" }.ShowWithBackendRecoveryAsync();
            return null;
        }
    }

    internal static GameInstallationSelection ConfirmShared(GameInstallationSelection selection) => new()
    {
        InventoryId = selection.InventoryId, Roots = selection.Roots.ToArray(),
        AllInstallations = selection.AllInstallations, SharedCleanupConfirmed = selection.AllInstallations,
    };

    internal static string Describe(GameInstallationSelection selection) =>
        (selection.AllInstallations ? "本次操作全部已识别安装：" : "本次仅操作：") + "\n" +
        (selection.Roots.Length == 0 ? "未找到安装目录" : string.Join("\n", selection.Roots)) + "\n\n";
}
