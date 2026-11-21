using DeltaNFD.Native;
using DeltaNFD.Services.CrashDiagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace DeltaNFD.Views;

public sealed partial class CrashDiagnosticsPage : Page
{
    private CancellationTokenSource? _run;
    public CrashDiagnosticsPage() => InitializeComponent();
    private CancellationTokenSource? _wheaRead;
    private enum AnalysisMode { Logs, Dump }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await RefreshWheaAsync();
    }
    private async void Whea_Click(object sender, RoutedEventArgs e) => await RefreshWheaAsync();
    private async Task RefreshWheaAsync()
    {
        if (_wheaRead != null) return;
        using var request = new CancellationTokenSource();
        _wheaRead = request;
        WheaButton.IsEnabled = false;
        WheaText.Text = "正在读取近 30 天的本机硬件记录…";
        try
        {
            var report = await Task.Run(() => WheaReport.Recent(request.Token), request.Token);
            if (ReferenceEquals(_wheaRead, request)) WheaText.Text = report;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(_wheaRead, request)) WheaText.Text = $"硬件记录未能读取（{ex.GetType().Name}），可点击刷新重试。";
        }
        finally
        {
            if (ReferenceEquals(_wheaRead, request)) { _wheaRead = null; WheaButton.IsEnabled = true; }
        }
    }
    private async void Analyze_Click(object sender, RoutedEventArgs e) => await RunAsync(AnalysisMode.Logs);
    private async void DumpPick_Click(object sender, RoutedEventArgs e) => await RunAsync(AnalysisMode.Dump);
    private void Cancel_Click(object sender, RoutedEventArgs e) => _run?.Cancel();
    protected override void OnNavigatedFrom(NavigationEventArgs e) {
        _run?.Cancel();
        var pending = _wheaRead;
        _wheaRead = null;
        pending?.Cancel();
        base.OnNavigatedFrom(e);
    }

    private async Task RunAsync(AnalysisMode mode)
    {
        bool fromDump = mode == AnalysisMode.Dump;
        if (_run != null || App.MainWindow == null) return;
        using var request = new CancellationTokenSource();
        _run = request;
        AnalyzeButton.IsEnabled = DumpPickButton.IsEnabled = DebuggerInput.IsEnabled = false;
        var ring = fromDump ? DumpBusyRing : BusyRing;
        var cancel = fromDump ? DumpCancelButton : CancelButton;
        try
        {
            var path = fromDump ? FolderPickerDialog.PickCrashDump(WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow)) : null;
            if (fromDump && path == null) return; // Dismissing the picker preserves the current report.
            request.Token.ThrowIfCancellationRequested();
            var debugger = DebuggerInput.Text;
            ring.IsActive = true;
            cancel.Visibility = Visibility.Visible;
            ReportText.Text = "正在分析…";
            StatusText.Text = fromDump ? "正在读取转储头及本机事件记录。" : "正在读取本机事件记录。";
            var report = await Task.Run(() => path == null ? CrashReport.Analyze(debugger, request.Token)
                : CrashReport.AnalyzeDump(KernelDumpReader.Read(path, request.Token), request.Token), request.Token);
            request.Token.ThrowIfCancellationRequested();
            ReportText.Text = report;
            StatusText.Text = "分析完成，可选中报告并复制。";
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        { ReportText.Text = "分析已取消。"; StatusText.Text = "未保存报告。"; }
        catch (Exception ex)
        {
            ReportText.Text = ex switch { InvalidDataException => ex.Message, UnauthorizedAccessException => "无权读取该文件。请将有权访问的转储副本放在本地目录后重试。",
                IOException => "文件无法读取，可能已移动、正被写入或被锁定。请稍后重试。", _ => $"分析未完成（{ex.GetType().Name}）。" };
            StatusText.Text = "未修改转储或系统设置。";
        }
        finally
        {
            _run = null;
            AnalyzeButton.IsEnabled = DumpPickButton.IsEnabled = DebuggerInput.IsEnabled = true;
            ring.IsActive = false;
            cancel.Visibility = Visibility.Collapsed;
        }
    }
}
