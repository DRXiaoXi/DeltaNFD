using System.Text.Json;
using System.Xml.Linq;
using DeltaNFD.Services;
using DeltaNFD.ViewModels;

internal static class Program
{
    private static int _checks;
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception(label);
        _checks++;
    }
    private static async Task Main()
    {
        var id = Guid.NewGuid();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DeltaNFD-maintenance-checks-" + id.ToString("N")));
        if (!root.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe fixture location");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        try
        {
            Settings(root);
            await AceFeedbackAsync();
            await FullDiskFeedbackAsync();
            CheckWiring();
            Console.WriteLine($"Maintenance interactions: {_checks} checks passed; actual view-model, isolated settings and fake ACE reads only.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Settings(string root)
    {
        var path = Path.Combine(root, "directory-settings.json");
        var manual = Path.Combine(root, "gone-game");
        var automatic = Path.Combine(root, "auto-game");
        AppSettingsStore.UpdateRequired(path, s =>
        {
            s.GameRootOverride = manual; s.GameInstallRoots = [manual + "\\", automatic];
            s.AceGameSearchRoots = ["keep-ace-bookmark"]; s.FrameModeActive = true; s.TempSpoofRestorePending = true;
        });
        var rows = GameInstallSettings.List([automatic], path);
        Check(rows.Count == 2 && rows.All(r => r.CanRemove), "stale and overlapping manual records are removable");
        Check(rows.Single(r => r.Path == manual).Source.Contains("旧手动指定"), "legacy and new sources appear together");
        Check(rows.Single(r => r.Path == automatic).Source.Contains("自动识别"), "automatic origin remains visible even for manual duplicates");
        GameInstallSettings.RemoveManual(manual.ToUpperInvariant(), path);
        var saved = AppSettingsStore.ReadRequired(path);
        Check(saved.GameRootOverride == "" && saved.GameInstallRoots.SequenceEqual([automatic]), "remove handles aliases and both legacy/new records");
        GameInstallSettings.ClearManual(path);
        saved = AppSettingsStore.ReadRequired(path);
        Check(saved.GameRootOverride == "" && saved.GameInstallRoots.Count == 0, "clear removes all manual directory fields");
        rows = GameInstallSettings.List([automatic], path);
        Check(rows.Count == 1 && !rows[0].CanRemove && rows[0].Source == "自动识别", "clearing manual records cannot pretend auto-discovered game disappeared");
        Check(saved.FrameModeActive && saved.TempSpoofRestorePending && saved.AceGameSearchRoots.SequenceEqual(["keep-ace-bookmark"]), "directory changes preserve frame/recovery and ACE bookmarks");
        GameInstallSettings.ClearManual(path);
        Check(GameInstallSettings.List([automatic], path).Count == 1, "clear is idempotent without blocking automatic discovery");
        File.WriteAllText(path, "{broken");
        var damaged = File.ReadAllBytes(path);
        try { GameInstallSettings.ClearManual(path); throw new Exception("Damaged settings should not be overwritten"); }
        catch (JsonException) { Check(damaged.SequenceEqual(File.ReadAllBytes(path)), "failed clear preserves corrupt configuration"); }
    }

    private static async Task AceFeedbackAsync()
    {
        var fake = new AceFake(); var vm = new AceViewModel(fake);
        vm.ManualGamePath = @"D:\中文游戏";
        Check(vm.ManualGamePath == @"D:\中文游戏", "ACE path can be entered without a native picker");
        fake.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = vm.CheckCoreFilesAsync();
        Check(vm.CoreCheckBusy && vm.CoreCheckStatusText.Contains("正在检测") && !vm.CanCheckCore, "button immediately reports an in-progress read");
        await vm.CheckCoreFilesAsync();
        Check(fake.Reads == 1, "repeat click cannot submit duplicate check while waiting");
        fake.Next.SetResult(Result()); await first;
        var firstStatus = vm.CoreCheckStatusText;
        Check(!vm.CoreCheckBusy && vm.CanCheckCore && firstStatus.Contains("已完成"), "successful read unlocks and reports completion");
        fake.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = vm.CheckCoreFilesAsync(); fake.Next.SetResult(Result()); await second;
        Check(vm.CoreCheckStatusText != firstStatus && vm.CoreCheckStatusText.Contains("第 2 次"), "identical repeated result still gives new completion feedback");
        fake.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = vm.CheckCoreFilesAsync(); fake.Next.SetException(new IOException("fixture denied")); await failed;
        Check(!vm.CoreCheckBusy && vm.CoreCheckLevel == AceCoreCheckLevel.Failed && vm.CoreCheckStatusText.Contains("fixture denied"), "read exception is visible and does not leave button busy");
        fake.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = vm.CheckCoreFilesAsync(); fake.Next.SetResult(Result(AceCoreCheckLevel.Blocked)); await blocked;
        Check(vm.DetectionPaused && !vm.CanCheckCore && vm.CoreCheckStatusText.Contains("已暂停"), "game blocker is explicit and retains detection gate");
        Check(fake.Writes == 0, "read buttons never invoke cleanup or system writes");
    }

    private static AceCoreCheckResult Result(AceCoreCheckLevel level = AceCoreCheckLevel.Normal) => new()
    { Level = level, SysCount = 0, Files = [], Summary = "same fixture result", Advice = "fixture", Games = [], Notes = [] };

    private static async Task FullDiskFeedbackAsync()
    {
        var fake = new AceFake(); var vm = new AceViewModel(fake);
        fake.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.CoreCheckLevel = AceCoreCheckLevel.Normal;
        var pending = vm.CheckCoreFilesAsync(fullDisk: true);
        Check(fake.FullReads == 1 && fake.Reads == 0, "explicit full scan calls full service, never quick fallback");
        Check(vm.CoreCheckBusy && vm.CanCancelCoreCheck && !vm.CanCheckCore && !vm.CanManageManualRoots,
            "full scan busy state exposes cancellation and prevents duplicate/manual changes");
        Check(!vm.CleanEnabled && !vm.CanRefresh && !vm.CleanRefreshEnabled, "cleanup and refresh blocked during full scan");
        Check(vm.CoreCheckLevel == AceCoreCheckLevel.NeedsReview && vm.CoreCheckText == "", "old green result cleared while walking");
        fake.Progress!.Report("fixture progress: D drive, 300 directories");
        for (var i = 0; i < 100 && !vm.CoreCheckStatusText.Contains("300 directories"); i++) await Task.Delay(10);
        Check(vm.CoreCheckStatusText.Contains("300 directories"), "real Progress callback displays live directory information");
        await vm.CheckCoreFilesAsync(fullDisk: true);
        Check(fake.FullReads == 1, "repeated click does not start a competing full scan");
        vm.CancelCoreCheck(); await pending;
        Check(fake.Token.IsCancellationRequested && !vm.CoreCheckBusy && !vm.CanCancelCoreCheck, "cancel token reaches service and busy state drains");
        Check(vm.CoreCheckLevel == AceCoreCheckLevel.NeedsReview && vm.CoreCheckStatusText.Contains("已取消"), "cancel never publishes old green or completed claim");
        fake.Progress.Report("late progress must not overwrite cancellation"); await Task.Delay(30);
        Check(vm.CoreCheckStatusText.Contains("已取消"), "late progress is ignored after cancellation");

        fake.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pending = vm.CheckCoreFilesAsync(fullDisk: true);
        fake.Next.SetResult(new AceCoreCheckResult
        {
            Level = AceCoreCheckLevel.NeedsReview, SysCount = 1, Files = [], Summary = "全盘检测未读全：fixture denied",
            Advice = "not complete", DiskScan = new() { Complete = false, SkippedDirectories = 1, ElapsedMilliseconds = 1234 },
        });
        await pending;
        Check(vm.CoreCheckStatusText.Contains("未读全") && vm.CoreCheckStatusText.Contains("1.2 秒"), "partial result displays uncertainty and measured time");
        fake.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pending = vm.CheckCoreFilesAsync(fullDisk: true); vm.DetachGameState(); await pending;
        Check(fake.Token.IsCancellationRequested && vm.CoreCheckStatusText.Contains("已取消"), "leaving page cancels full scan");
        using var owner = new CancellationTokenSource();
        fake.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pending = vm.CheckCoreFilesAsync(fullDisk: true, owner.Token); owner.Cancel(); await pending;
        Check(vm.CoreCheckStatusText.Contains("已取消"), "page owner cancellation linked to full scan");
        fake.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pending = vm.CheckCoreFilesAsync(fullDisk: true); fake.Next.SetException(new IOException("fixture failed full read")); await pending;
        Check(vm.CoreCheckLevel == AceCoreCheckLevel.Failed && !vm.CoreCheckBusy && vm.CoreCheckStatusText.Contains("fixture failed"), "full scan failure releases controls and remains explicit");
        Check(fake.Writes == 0, "full scan never invokes cleanup");
    }

    private static void CheckWiring()
    {
        var repo = new DirectoryInfo(Environment.CurrentDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "DeltaNFD.sln"))) repo = repo.Parent;
        if (repo is null) throw new Exception("Repository missing");
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var settings = XDocument.Load(Path.Combine(repo.FullName, "src/DeltaNFD/Views/SettingsPage.xaml"));
        Check(settings.Descendants(ns + "Button").Any(b => (string?)b.Attribute("Click") == "RemoveGameRoot_Click" && (string?)b.Attribute("Tag") == "{Binding Path}"), "each settings row exposes the actual removal handler");
        var ace = XDocument.Load(Path.Combine(repo.FullName, "src/DeltaNFD/Views/AcePage.xaml"));
        Check(ace.Descendants(ns + "TextBox").Any(b => ((string?)b.Attribute("Text"))?.Contains("ManualGamePath, Mode=TwoWay") == true), "ACE path textbox updates the actual view-model");
        Check(ace.Descendants(ns + "Button").Any(b => (string?)b.Attribute("Click") == "BrowseManualGameButton_Click")
            && ace.Descendants(ns + "Button").Any(b => (string?)b.Attribute("Click") == "AddManualGameButton_Click"), "browse and register/check have distinct wired actions");
        Check(ace.Descendants(ns + "TextBlock").Any(b => ((string?)b.Attribute("Text"))?.Contains("CoreCheckStatusText") == true), "live check feedback is actually displayed");
        Check(!ace.Descendants(ns + "Button").Any(b => (string?)b.Attribute("Click") == "CancelCoreCheckButton_Click"), "retired automatic-scan cancellation is not offered");
        var code = File.ReadAllText(Path.Combine(repo.FullName, "src/DeltaNFD/Views/AcePage.xaml.cs"));
        Check(code.Contains("CheckCoreFilesAsync(cancellationToken: _pageOwner!.QueueToken)"), "user check uses saved manual directories with page lifetime token");
    }

    private sealed class AceFake : IAceService
    {
        internal int Reads, FullReads, Writes;
        internal IProgress<string>? Progress;
        internal CancellationToken Token;
        internal TaskCompletionSource<AceCoreCheckResult> Next = new();
        public Task<AceCoreCheckResult> CheckCoreFilesAsync() { Reads++; return Next.Task; }
        public Task<AceCoreCheckResult> CheckAllDisksAsync(IProgress<string>? progress, CancellationToken token)
        { FullReads++; Progress = progress; Token = token; return Next.Task.WaitAsync(token); }
        public Task<AceScanResult> ScanAsync() => throw new Exception("Unexpected scan");
        public Task<OperationResult> CleanAsync(IProgress<string>? progress = null) { Writes++; throw new Exception("Unexpected cleanup"); }
        public Task<OperationResult> CleanCoreFilesAsync(IProgress<string>? progress = null) { Writes++; throw new Exception("Unexpected cleanup"); }
        public Task<AceVirtualizationCheckResult> CheckVirtualizationAsync() => throw new Exception("Unexpected virtualization access");
    }
}
