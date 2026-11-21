using System.Diagnostics;
using DeltaNFD.Services;

namespace DeltaNFD.Native;

/// <summary>Native-only residency. The first open enters the existing WinUI startup in this same process.</summary>
internal static class LightweightTray
{
    internal const string Argument = "--lightweight-tray-handoff";
    private const int ReadyTimeout = 8000;
    private const int TransferTimeout = 15000;

    internal static async Task<Handoff?> PrepareAsync()
    {
        var transfer = new Handoff(Guid.NewGuid());
        try
        {
            var image = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定本程序路径。");
            var start = new ProcessStartInfo(image) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            start.ArgumentList.Add(Argument);
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add(transfer.Id.ToString("N"));
            using var candidate = Process.Start(start) ?? throw new InvalidOperationException("无法启动轻量托盘进程。");
            if (await Task.Run(() => transfer.Ready.WaitOne(ReadyTimeout)) && !candidate.HasExited) return transfer;
            Log.Warn("轻量托盘未完成图标登记，保留现有界面进程。");
        }
        catch (Exception ex) { Log.Warn("轻量托盘启动失败，保留现有界面进程：" + ex.Message); }
        transfer.Dispose();
        return null;
    }

    internal static bool ShouldLaunchUi(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] != Argument) return true;
        if (args.Count != 3 || !int.TryParse(args[1], out var pid) || pid <= 0 || pid == Environment.ProcessId
            || !Guid.TryParseExact(args[2], "N", out var id) || id == Guid.Empty) return false;
        try
        {
            using var parent = Process.GetProcessById(pid);
            using var self = Process.GetCurrentProcess();
            if (parent.SessionId != self.SessionId
                || !string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)
                || parent.HasExited || SingleInstanceGuard.DeploymentInProgress()) return false;
            using var ready = EventWaitHandle.OpenExisting(EventName(id, "Ready"));
            using var commit = EventWaitHandle.OpenExisting(EventName(id, "Commit"));
            using var cancel = EventWaitHandle.OpenExisting(EventName(id, "Cancel"));
            using var open = new ManualResetEvent(false);
            using var exit = new ManualResetEvent(false);
            using var icon = new TrayIconWindow("三角帧不掉洲 · 轻量托盘", () => open.Set(), () => exit.Set(),
                iconLost: () => open.Set());
            if (!icon.Show()) return false;
            ready.Set();
            if (WaitHandle.WaitAny([cancel, commit], TransferTimeout) != 1 || !parent.WaitForExit(TransferTimeout)) return false;

            // The previous frontend owns both single-instance locks until its actual termination.
            // If setup or another GUI won the race, this candidate must not survive or activate it.
            if (SingleInstanceGuard.DeploymentInProgress() || !SingleInstanceGuard.TryBecomePrimary(activateExisting: false)) return false;
            Log.Info("轻量托盘：已接管托盘，未启动 WinUI 或维护后端。");
            while (!SingleInstanceGuard.DeploymentInProgress())
            {
                var action = SingleInstanceGuard.WaitForTrayActivation(exit, open, 1000);
                if (action == 0) break;
                if (action != WaitHandle.WaitTimeout) return !SingleInstanceGuard.DeploymentInProgress();
            }
        }
        catch (Exception ex) { Log.Warn("轻量托盘接管未完成：" + ex.Message); }
        return false;
    }

    private static string EventName(Guid id, string state) => $@"Local\DeltaNFD.LightweightTray.{id:N}.{state}";

    internal sealed class Handoff : IDisposable
    {
        internal Guid Id { get; }
        internal EventWaitHandle Ready { get; }
        private readonly EventWaitHandle _commit;
        private readonly EventWaitHandle _cancel;
        private bool _committed;

        internal Handoff(Guid id)
        {
            Id = id;
            Ready = new(false, EventResetMode.ManualReset, EventName(id, "Ready"));
            try
            {
                _commit = new(false, EventResetMode.ManualReset, EventName(id, "Commit"));
                try { _cancel = new(false, EventResetMode.ManualReset, EventName(id, "Cancel")); }
                catch { _commit.Dispose(); throw; }
            }
            catch { Ready.Dispose(); throw; }
        }

        internal void Commit()
        {
            _commit.Set();
            _committed = true;
        }

        public void Dispose()
        {
            if (!_committed) _cancel.Set();
            _cancel.Dispose();
            _commit.Dispose();
            Ready.Dispose();
        }
    }
}
