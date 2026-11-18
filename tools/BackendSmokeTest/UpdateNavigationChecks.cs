using System.Threading.Channels;
using DeltaNFD.Services;
using DeltaNFD.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

internal static class UpdateNavigationChecks
{
    private static int _checks;
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); _checks++; }
    private static Task<bool> Confirm(string title, string message, string action) => Task.FromResult(true);
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    internal static async Task RunAsync(string sourceRoot)
    {
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-update-navigation-" + id.ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        Check(Path.GetFullPath(AppDataPaths.Root) == Path.GetFullPath(root), "All settings and logs use the isolated root.");
        var page = File.ReadAllText(Path.Combine(sourceRoot, "src/DeltaNFD/Views/SettingsPage.xaml.cs"));
        Check(page.Contains("public UpdateViewModel Update { get; } = UpdateViewModel.Current;"), "Recreated settings pages bind the application controller.");
        Check(page.Contains("Update.Attach(this, ConfirmUpdateAsync);") && page.Contains("Update.Detach(this);"), "Confirmation hooks are owned by each loaded page.");
        await SharedDownloadAndCancelAsync();
        await CompletionAndRetryAsync(fail: false, throws: false);
        await CompletionAndRetryAsync(fail: true, throws: false);
        await CompletionAndRetryAsync(fail: true, throws: true);
        await ConfirmationOwnershipAsync();
        await CheckNavigationAsync();
        await FullFallbackAsync();
        await ResumeFallbackAsync();
        await CancelBeforeLaunchAsync();
        Check(File.ReadAllText(Path.Combine(root, "isolated-test.marker")) == id.ToString("D"), "Isolation marker is preserved.");
        Console.WriteLine($"Update navigation checks passed: {_checks}; data: {root}");
    }

    private static async Task SharedDownloadAndCancelAsync()
    {
        var service = new FakeUpdateService();
        var queue = new DispatcherQueue();
        DispatcherQueue.CurrentForTests = queue;
        ServiceLocator.UpdateFactory = () => service;
        var vm = UpdateViewModel.Current;
        var owner = new object();
        vm.Attach(owner, Confirm);
        var job = vm.PrimaryActionCommand.ExecuteAsync(null);
        var transfer = await service.NextTransferAsync();
        Check(vm.IsBusy && vm.IsDownloading && vm.CancelEnabled && vm.ShowProgress, "An admitted transfer owns the shared busy/progress/cancel state.");
        transfer.Report(25, "first quarter"); queue.Drain();
        Check(vm.ProgressPercent == 25 && vm.StatusText == "first quarter", "Progress is dispatched to the actual controller.");
        var channel = vm.UpdateChannelIndex; var server = vm.UpdateServerIndex; var mode = vm.UpdateModeIndex;
        var settings = AppSettingsStore.Read();
        for (var i = 0; i < 12; i++)
        {
            vm.Detach(owner);
            Check(!transfer.Token.IsCancellationRequested, "Unloading a settings page cannot cancel its transfer.");
            owner = new object();
            var returned = UpdateViewModel.Current;
            returned.Attach(owner, Confirm);
            Check(ReferenceEquals(returned, vm) && returned.ProgressPercent == 25 && returned.CancelEnabled,
                "Every reentry uses the original task and progress.");
            await returned.PrimaryActionCommand.ExecuteAsync(null);
            await returned.CheckCommand.ExecuteAsync(null);
            await returned.ResumeFullFallbackAsync("unwanted", "unwanted");
            returned.SkipVersionCommand.Execute(null);
            returned.UpdateChannelIndex = channel == 0 ? 1 : 0;
            returned.UpdateServerIndex = (server + 1) % 3;
            returned.UpdateModeIndex = mode == 0 ? 1 : 0;
            returned.UpdateChannelIndex = returned.UpdateServerIndex = returned.UpdateModeIndex = -1;
            Check(service.Downloads == 1 && service.Checks == 1 && service.Resumes == 0 && service.MaxActive == 1,
                "Recreated page commands cannot start a duplicate file writer or check.");
        }
        Check(vm.UpdateAvailable && vm.UpdateChannelIndex == channel && vm.UpdateServerIndex == server && vm.UpdateModeIndex == mode
            && UpdateModePolicy.SameSelection(settings, AppSettingsStore.Read()) && service.Resets == 0,
            "Busy selection writes and skip commands cannot invalidate the active download.");
        vm.Detach(owner);
        transfer.Report(75, "download continues off page"); queue.Drain();
        Check(vm.ProgressPercent == 75 && vm.IsDownloading, "Transfer continues while no settings page is loaded.");
        vm.Attach(owner, Confirm);
        service.Notify(); // Deliberately leave a stale check notification queued until after cancellation.
        vm.CancelDownloadCommand.Execute(null);
        Check(transfer.Token.IsCancellationRequested, "The returned page cancels the original request.");
        await job.WaitAsync(Deadline);
        transfer.Report(99, "late progress after cancel"); queue.Drain();
        Check(!vm.IsBusy && !vm.CancelEnabled && vm.StatusText == "已取消下载。" && service.Launches == 0,
            $"Cancellation drains the original writer; stale check/progress cannot overwrite its result or launch an installer. Busy={vm.IsBusy}, cancel={vm.CancelEnabled}, launches={service.Launches}, status={vm.StatusText}");
        vm.Detach(owner); vm.Attach(new object(), Confirm);
        Check(vm.StatusText == "已取消下载。", "Reentry retains cancellation status.");
        Check(service.Subscribers == 1, "Page recreation does not multiply or remove controller subscriptions.");
    }

    private static async Task CompletionAndRetryAsync(bool fail, bool throws)
    {
        var service = new FakeUpdateService(); var queue = new DispatcherQueue();
        var vm = new UpdateViewModel(service, queue); var owner = new object();
        vm.Attach(owner, Confirm);
        var job = vm.PrimaryActionCommand.ExecuteAsync(null);
        var first = await service.NextTransferAsync();
        vm.Detach(owner);
        first.Report(50, "halfway while hidden"); queue.Drain();
        Check(vm.ProgressPercent == 50 && vm.IsBusy, "Hidden pages do not suppress active progress.");
        service.Notify(); first.Report(90, "queued before completion");
        if (throws) first.Completion.SetException(new IOException("isolated connection failure"));
        else first.Completion.SetResult(fail ? UpdateDownloadResult.Fail("isolated hash mismatch") : UpdateDownloadResult.Ok("fixture.exe", "verified"));
        await job.WaitAsync(Deadline);
        var terminal = vm.StatusText; var severity = vm.Severity;
        first.Report(99, "late previous transfer"); queue.Drain();
        vm.Attach(new object(), Confirm);
        Check(vm.StatusText == terminal && !vm.IsBusy && !vm.IsDownloading && !vm.CancelEnabled,
            "Completion/failure and its controls survive navigation and late callbacks.");
        Check(fail ? severity == InfoBarSeverity.Error && service.Launches == 0
            : severity == InfoBarSeverity.Success && service.Launches == 1, "Only a successful verified download reaches the fake launch boundary.");
        var retry = vm.PrimaryActionCommand.ExecuteAsync(null); var second = await service.NextTransferAsync();
        second.Report(10, "new transfer"); queue.Drain();
        first.Report(100, "old operation must not win"); queue.Drain();
        Check(vm.ProgressPercent == 10 && vm.StatusText == "new transfer", "Late progress cannot modify a subsequent attempt.");
        vm.CancelDownloadCommand.Execute(null); await retry.WaitAsync(Deadline);
        Check(service.MaxActive == 1, "Retry starts after the previous writer has finished.");
    }

    private static async Task ConfirmationOwnershipAsync()
    {
        var service = new FakeUpdateService(); var queue = new DispatcherQueue();
        var vm = new UpdateViewModel(service, queue); var firstOwner = new object(); var nextOwner = new object();
        var consent = Signal<bool>(); var calls = 0;
        vm.Attach(firstOwner, (_, _, _) => { calls++; return consent.Task; });
        var job = vm.PrimaryActionCommand.ExecuteAsync(null);
        Check(vm.IsBusy && !vm.IsDownloading && calls == 1, "Confirmation remains inside the single update operation.");
        await vm.CheckCommand.ExecuteAsync(null); await vm.ResumeFullFallbackAsync("unwanted", "unwanted");
        Check(service.Checks == 1 && service.Resumes == 0, "Confirmation cannot admit overlapping operations.");
        vm.Detach(firstOwner); vm.Attach(nextOwner, Confirm);
        consent.SetResult(true); await job.WaitAsync(Deadline);
        Check(service.Downloads == 0 && !vm.IsBusy, "Consent from a departed page cannot authorize a download.");
        vm.Detach(firstOwner); // Out-of-order old Unloaded must not clear the current page's hook.
        var next = vm.PrimaryActionCommand.ExecuteAsync(null); await service.NextTransferAsync();
        Check(service.Downloads == 1, "A stale page detach cannot remove the new page's confirmation hook.");
        vm.CancelDownloadCommand.Execute(null); await next.WaitAsync(Deadline);
        vm.Detach(nextOwner);
        await vm.PrimaryActionCommand.ExecuteAsync(null);
        Check(service.Downloads == 1, "There is no implicit consent without a loaded page.");
        vm.Attach(nextOwner, (_, _, _) => throw new InvalidOperationException("dialog unavailable"));
        await vm.PrimaryActionCommand.ExecuteAsync(null);
        Check(!vm.IsBusy && vm.Severity == InfoBarSeverity.Error && service.Downloads == 1, "Dialog failures release admission without starting a download.");
        var oldServer = vm.UpdateServerIndex;
        vm.UpdateServerIndex = -1; vm.UpdateChannelIndex = 20; vm.UpdateModeIndex = -1;
        Check(vm.UpdateServerIndex == oldServer && service.Resets == 0, "Uninitialized selector values cannot corrupt saved selection.");
        vm.UpdateServerIndex = (oldServer + 1) % 3;
        Check(service.Resets == 1 && (int)AppSettingsStore.Read().UpdateServer == vm.UpdateServerIndex,
            "Explicit valid selection changes still work after the operation ends.");
    }

    private static async Task CheckNavigationAsync()
    {
        var service = new FakeUpdateService { CheckGate = Signal<bool>() }; var queue = new DispatcherQueue();
        var vm = new UpdateViewModel(service, queue); var owner = new object(); vm.Attach(owner, Confirm);
        var job = vm.CheckCommand.ExecuteAsync(null);
        vm.Detach(owner); vm.Attach(new object(), Confirm);
        await vm.PrimaryActionCommand.ExecuteAsync(null); await vm.CheckCommand.ExecuteAsync(null);
        Check(vm.IsBusy && !vm.IsDownloading && service.Checks == 1 && service.Downloads == 0, "Navigation during a slow check preserves its single operation.");
        service.CheckGate.SetResult(true); await job.WaitAsync(Deadline); queue.Drain();
        Check(vm.UpdateAvailable && !vm.IsBusy && vm.StatusText == "fixture update available", "The completed check reaches the current page.");
    }

    private static async Task FullFallbackAsync()
    {
        var service = new FakeUpdateService(); var queue = new DispatcherQueue();
        service.LaunchResults.Enqueue(new UpdateLaunchResult { CanFallbackToFull = true, Message = "rolled back fixture" });
        var vm = new UpdateViewModel(service, queue); var owner = new object(); vm.Attach(owner, Confirm);
        var job = vm.PrimaryActionCommand.ExecuteAsync(null); var partial = await service.NextTransferAsync();
        partial.Completion.SetResult(new UpdateDownloadResult { Success = true, IsIncremental = true, InstallerPath = "files.json", Message = "verified files" });
        var full = await service.NextTransferAsync();
        vm.Detach(owner); vm.Attach(new object(), Confirm);
        full.Report(30, "full package"); queue.Drain();
        partial.Report(95, "stale incremental stage"); queue.Drain();
        Check(vm.IsDownloading && vm.ProgressPercent == 30 && vm.StatusText == "full package" && service.FullDownloads == 1,
            "Navigation preserves full fallback while late incremental progress is rejected.");
        full.Completion.SetResult(UpdateDownloadResult.Ok("fixture-full.exe", "full verified")); await job.WaitAsync(Deadline); queue.Drain();
        Check(service.Launches == 2 && vm.Severity == InfoBarSeverity.Success && service.MaxActive == 1,
            "Verified full fallback still follows the original launch policy.");
    }

    private static async Task ResumeFallbackAsync()
    {
        var service = new FakeUpdateService { ResumeGate = Signal<bool>() }; var queue = new DispatcherQueue();
        var vm = new UpdateViewModel(service, queue); var owner = new object(); vm.Attach(owner, Confirm);
        var job = vm.ResumeFullFallbackAsync("fixture-session", "fixture-nonce");
        vm.Detach(owner); vm.Attach(new object(), Confirm);
        await vm.ResumeFullFallbackAsync("duplicate", "duplicate");
        Check(vm.IsBusy && service.Resumes == 1 && service.Downloads == 0, "One-time fallback validation survives navigation without replay.");
        service.ResumeGate.SetResult(true); var transfer = await service.NextTransferAsync();
        Check(service.ResumeIdentity == "fixture-session/fixture-nonce" && service.FullDownloads == 1, "Original fallback identity and full-download path are retained.");
        vm.CancelDownloadCommand.Execute(null); await job.WaitAsync(Deadline);
        Check(transfer.Token.IsCancellationRequested && service.Launches == 0 && !vm.IsBusy, "Returned page can cancel a resumed full download.");
    }

    private static async Task CancelBeforeLaunchAsync()
    {
        var service = new FakeUpdateService { IgnoreDownloadCancellation = true }; var queue = new DispatcherQueue();
        var vm = new UpdateViewModel(service, queue); vm.Attach(new object(), Confirm);
        var job = vm.PrimaryActionCommand.ExecuteAsync(null); var transfer = await service.NextTransferAsync();
        vm.CancelDownloadCommand.Execute(null);
        transfer.Completion.SetResult(UpdateDownloadResult.Ok("fixture.exe", "completed at cancellation"));
        await job.WaitAsync(Deadline); queue.Drain();
        Check(service.Launches == 0 && vm.StatusText == "已取消下载。", "Cancellation at successful transfer completion still prevents launch.");
    }

    private sealed class Transfer(IProgress<UpdateProgressInfo>? progress, CancellationToken token)
    {
        internal CancellationToken Token => token;
        internal TaskCompletionSource<UpdateDownloadResult> Completion { get; } = Signal<UpdateDownloadResult>();
        internal void Report(long bytes, string stage) => progress?.Report(new UpdateProgressInfo
        { BytesReceived = bytes, TotalBytes = 100, BytesPerSecond = 10, Stage = stage });
    }

    private sealed class FakeUpdateService : IUpdateService
    {
        private readonly Channel<Transfer> _transfers = Channel.CreateUnbounded<Transfer>();
        private Action<bool>? _availability;
        private int _active;
        internal int Checks, Downloads, FullDownloads, Launches, Resumes, Resets, MaxActive, Subscribers;
        internal bool IgnoreDownloadCancellation;
        internal TaskCompletionSource<bool>? CheckGate, ResumeGate;
        internal string ResumeIdentity = "";
        internal Queue<UpdateLaunchResult> LaunchResults { get; } = new();
        private readonly UpdateManifest _manifest = new() { Version = "0.9500", DisplayVersion = "fixture", Installers = [new() { SizeBytes = 100 }] };
        public Version CurrentVersion => new(0, 9402);
        public string CurrentVersionText => "fixture current";
        public bool IsInstalledCopy => false; // Emergency policy refreshes must never access the network in this fixture.
        public string InstallDirectory => AppDataPaths.Root;
        public string UpdateRootDirectory => Path.Combine(AppDataPaths.Root, "downloads");
        public string FallbackReleasePageUrl => "https://example.invalid/fixture";
        public string ConfigSourceDescription => "isolated fake";
        public string UserConfigPath => Path.Combine(AppDataPaths.Root, "source.json");
        public void EnsureUserConfigTemplate() => throw new Exception("Unexpected config action.");
        public event Action<bool>? AvailabilityChanged
        { add { _availability += value; Subscribers++; } remove { _availability -= value; Subscribers--; } }
        internal FakeUpdateService() => LastResult = AvailableResult();
        private UpdateCheckResult AvailableResult() => new() { Status = UpdateCheckStatus.UpdateAvailable, Manifest = _manifest,
            Message = "fixture update available", AvailableVersionText = "fixture next" };
        public UpdateCheckResult? LastResult { get; private set; }
        public void ResetCheckResult() { Resets++; LastResult = new() { Message = "selection reset" }; Notify(); }
        internal void Notify() => _availability?.Invoke(true);
        public async Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken cancellationToken = default)
        { Checks++; if (CheckGate is not null) await CheckGate.Task.WaitAsync(cancellationToken); LastResult = AvailableResult(); Notify(); return LastResult; }
        public Task<UpdateDownloadResult> DownloadAsync(UpdateManifest manifest, IProgress<UpdateProgressInfo>? progress = null, CancellationToken cancellationToken = default)
        { Downloads++; return TransferAsync(progress, cancellationToken); }
        public Task<UpdateDownloadResult> DownloadFullFallbackAsync(UpdateManifest manifest, IProgress<UpdateProgressInfo>? progress = null, CancellationToken cancellationToken = default)
        { FullDownloads++; return TransferAsync(progress, cancellationToken); }
        private async Task<UpdateDownloadResult> TransferAsync(IProgress<UpdateProgressInfo>? progress, CancellationToken token)
        {
            _active++; MaxActive = Math.Max(MaxActive, _active);
            var transfer = new Transfer(progress, token); await _transfers.Writer.WriteAsync(transfer, token);
            try { return IgnoreDownloadCancellation ? await transfer.Completion.Task : await transfer.Completion.Task.WaitAsync(token); }
            finally { _active--; }
        }
        internal async Task<Transfer> NextTransferAsync() => await _transfers.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        public async Task<UpdateManifest> ResumeFullFallbackAsync(string session, string nonce, CancellationToken cancellationToken = default)
        { Resumes++; ResumeIdentity = session + "/" + nonce; if (ResumeGate is not null) await ResumeGate.Task.WaitAsync(cancellationToken); return _manifest; }
        public Task<UpdateLaunchResult> LaunchInstallerAsync(UpdateManifest manifest, string installerPath)
        { Launches++; return Task.FromResult(LaunchResults.Count > 0 ? LaunchResults.Dequeue() : new UpdateLaunchResult { Success = true, Message = "fake installer accepted" }); }
        public void CleanupPendingInstaller() => throw new Exception("Unexpected cleanup action.");
    }
}
