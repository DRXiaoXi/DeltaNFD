using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Views;

public sealed partial class RuntimePage
{
    private async Task<bool> HandleRuntimeFileUseAsync(CancellationToken token, RuntimeFileUseScope scope = RuntimeFileUseScope.Vc14)
    {
        var lastResult = "";
        while (true)
        {
            token.ThrowIfCancellationRequested();
            ViewModel.RepairStatusText = scope == RuntimeFileUseScope.FullRepair ? "正在检测 C++ 2005–2013 / 14.x 共享文件的进程占用…" : "正在检测 C++ 14.x 系统文件的进程占用…";
            var scan = await ViewModel.ScanFileUseAsync(token, scope);
            var noOwners = scan.Success && scan.Processes.Count == 0;
            var selected = new List<RuntimeFileUseProcess>();
            var refreshRequested = false;
            var body = new StackPanel { Spacing = 12 };
            body.Children.Add(FileUseText(noOwners ? "本次检测未发现正在使用这些共享运行库文件的进程。请仍先保存工作、关闭游戏，再确认继续修复；也可以重新检测。"
                : "下列程序正在使用本次修复范围内的 C++ 文件，但不一定阻止安装。请先保存工作，再选择程序请求正常退出；也可跳过本工具的占用处理。继续修复后，安装器可能自行关闭使用中的应用或要求重启。"));
            body.Children.Add(FileUseText(scan.Message));
            if (lastResult.Length > 0) body.Children.Add(FileUseText("上次处理结果：\n" + lastResult));
            var selectionHint = FileUseText(noOwners ? "未选择或结束任何进程。" : "请选择要退出的程序，默认不选中任何进程（每次最多 16 个）。");
            body.Children.Add(selectionHint);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = noOwners ? "运行库占用检测完成" : scan.Success ? "检测到运行库文件被使用" : "运行库文件占用尚未确认",
                PrimaryButtonText = noOwners ? "继续修复" : scan.Success ? "请求退出所选进程" : "重新检测",
                SecondaryButtonText = noOwners ? "" : "跳过占用处理，继续", CloseButtonText = "取消修复",
                DefaultButton = ContentDialogButton.Close, IsPrimaryButtonEnabled = noOwners || !scan.Success,
            };
            if (scan.Success)
            {
                var refresh = new Button { Content = "已手动退出程序，重新检测" };
                refresh.Click += (_, _) => { refreshRequested = true; dialog.Hide(); };
                body.Children.Add(refresh);
            }
            foreach (var process in scan.Processes.OrderBy(p => !p.CanClose).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                var details = new StackPanel { Spacing = 3 };
                details.Children.Add(FileUseText(process.Name + " · PID " + process.Pid));
                details.Children.Add(FileUseText(process.ImagePath.Length > 0 ? process.ImagePath : "进程路径未确认"));
                if (!process.CanClose)
                {
                    details.Children.Add(FileUseText("需手动处理：" + process.ProtectionReason));
                    body.Children.Add(details);
                    continue;
                }
                var choice = new CheckBox { Content = details, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(choice, "选择退出 " + process.Name + "，PID " + process.Pid);
                choice.Checked += (_, _) =>
                {
                    if (selected.Count >= 16) { choice.IsChecked = false; selectionHint.Text = "每次最多选择 16 个程序，请分批处理。"; return; }
                    selected.Add(process);
                    dialog.IsPrimaryButtonEnabled = selected.Count > 0;
                    selectionHint.Text = "已选择 " + selected.Count + " 个程序，将先请求正常退出。";
                };
                choice.Unchecked += (_, _) =>
                {
                    selected.Remove(process);
                    dialog.IsPrimaryButtonEnabled = selected.Count > 0;
                    selectionHint.Text = "已选择 " + selected.Count + " 个程序，将先请求正常退出。";
                };
                body.Children.Add(choice);
            }
            dialog.Content = new ScrollViewer { MaxHeight = 420, Content = body };
            var response = await dialog.ShowWithBackendRecoveryAsync();
            if (refreshRequested) continue;
            if (response == ContentDialogResult.Secondary) return true;
            if (response != ContentDialogResult.Primary)
            { ViewModel.RepairStatusText = "已取消修复；未继续结束进程或操作运行库。"; return false; }
            if (!scan.Success) continue;
            if (noOwners) return true;
            token.ThrowIfCancellationRequested();
            ViewModel.RepairStatusText = "正在请求所选程序正常退出，请处理可能出现的保存提示…";
            var result = await ViewModel.CloseFileUseAsync(new()
            {
                SnapshotId = scan.SnapshotId, ProcessIds = selected.Select(p => p.Pid).ToList(), Confirmed = true,
            }, token);
            lastResult = result.Message;
            ViewModel.RepairStatusText = result.Message;
            var pending = result.Actions.Where(a => a.Status == "still-running").Select(a => a.Pid).ToHashSet();
            if (pending.Count == 0) continue;
            var refreshed = await ViewModel.ScanFileUseAsync(token, scope);
            if (!refreshed.Success) continue;
            // Require the exact previously selected identity, including start time; PID reuse never
            // silently adds a new program to this second confirmation.
            var forceTargets = refreshed.Processes.Where(p => p.CanClose && pending.Contains(p.Pid)
                && selected.Any(old => old.Pid == p.Pid && old.StartFileTime == p.StartFileTime
                    && old.ImagePath.Equals(p.ImagePath, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (forceTargets.Length == 0) continue;
            var force = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "这些程序尚未退出，是否强制结束？",
                Content = new ScrollViewer { MaxHeight = 420, Content = FileUseText(
                    "请先保存工作。强制结束会立即终止下列程序，未保存的内容可能丢失；正常退出时的保存提示也会被中断。\n\n"
                    + string.Join("\n\n", forceTargets.Select(p => p.Name + " · PID " + p.Pid + "\n" + p.ImagePath))
                    + "\n\n仅操作本次列出的进程。结束后会重新检测文件占用，再由你确认运行库修复。") },
                PrimaryButtonText = "强制结束这些进程", CloseButtonText = "保留进程并返回",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await force.ShowWithBackendRecoveryAsync() != ContentDialogResult.Primary) continue;
            token.ThrowIfCancellationRequested();
            ViewModel.RepairStatusText = "正在结束本次确认的进程，并重新核对退出结果…";
            var forced = await ViewModel.CloseFileUseAsync(new()
            {
                SnapshotId = refreshed.SnapshotId, ProcessIds = forceTargets.Select(p => p.Pid).ToList(),
                Confirmed = true, Force = true, ForceConfirmed = true,
            }, token);
            lastResult = forced.Message;
            ViewModel.RepairStatusText = forced.Message;
        }
    }

    private static TextBlock FileUseText(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
}
