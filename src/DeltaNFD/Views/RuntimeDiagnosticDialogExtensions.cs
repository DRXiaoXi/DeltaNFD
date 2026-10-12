using DeltaNFD.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

internal static class RuntimeDiagnosticDialogExtensions
{
    internal static Task<ContentDialogResult> ShowRuntimeResultAsync(this ContentDialog dialog, OperationResult result)
    {
        if (!result.Success || result.OutcomeUnknown || result.IsPartial || result.RequiresReboot)
        {
            var panel = new StackPanel { Spacing = 12 };
            var original = dialog.Content;
            dialog.Content = null;
            if (original is ScrollViewer { Content: UIElement inner } viewer) { viewer.Content = null; panel.Children.Add(inner); }
            else if (original is UIElement element) panel.Children.Add(element);
            else panel.Children.Add(new TextBlock { Text = original?.ToString() ?? result.Message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            var directory = RuntimeDiagnosticLocation.Resolve(result, Log.LogDirectory);
            var guidance = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
                Text = directory.Equals(Path.GetFullPath(Log.LogDirectory), StringComparison.OrdinalIgnoreCase)
                    ? "本次未提供可确认的独立诊断目录。请将主日志目录整体打包（包括 runtime-repair-* 子目录），不要只发送 settings.json。\n当前主日志：" + Log.CurrentPath
                    : "请打开日志目录，将本次 runtime-repair-* 文件夹整体压缩后反馈；不要只发送 settings.json 或单个恢复记录。\n" + directory };
            var open = new Button { Content = "打开日志目录", HorizontalAlignment = HorizontalAlignment.Left };
            open.Click += (_, _) =>
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
                    { UseShellExecute = true };
                    start.ArgumentList.Add(directory);
                    using var process = System.Diagnostics.Process.Start(start);
                }
                catch (Exception ex) { Log.Error("打开本次运行库诊断目录失败", ex); guidance.Text = "无法打开目录，请复制路径手动查看：\n" + directory + "\n" + ex.Message; }
            };
            // Keep the action visible before potentially very long MSI error details.
            panel.Children.Insert(0, guidance); panel.Children.Insert(1, open);
            dialog.Content = new ScrollViewer { MaxHeight = 440, Content = panel };
        }
        // Inline log action preserves the existing automatic backend-review primary button.
        return dialog.ShowWithBackendRecoveryAsync();
    }
}
