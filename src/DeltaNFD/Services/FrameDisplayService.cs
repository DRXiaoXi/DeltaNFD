using System.Diagnostics;
using System.Text.Json;
using DeltaNFD.Native;

namespace DeltaNFD.Services;

internal static class FrameDisplayService
{
    private static readonly SemaphoreSlim Sync = new(1, 1);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(10);
    private static Process? _worker;
    private static bool _inputClosed;

    internal static Task<FrameDisplayReply> PreviewAsync(int width, int height) =>
        ExchangeAsync(FormattableString.Invariant($"apply {width} {height}"), start: true, stopWhenIdle: true);

    internal static Task<FrameDisplayReply> KeepAsync(long deadline) =>
        ExchangeAsync(FormattableString.Invariant($"keep {deadline}"), stopWhenIdle: true);

    internal static Task<FrameDisplayReply> CancelAsync(long deadline) =>
        ExchangeAsync(FormattableString.Invariant($"cancel {deadline}"), stopWhenIdle: true);

    internal static Task<FrameDisplayReply> StatusAsync() => ExchangeAsync("status", stopWhenIdle: true);

    internal static Task<FrameDisplayReply> RestoreAsync() => ExchangeAsync("restore", stopWhenIdle: true);

    private static async Task<FrameDisplayReply> ExchangeAsync(string command, bool start = false, bool stopWhenIdle = false)
    {
        await Sync.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_worker is null && !start)
                return new FrameDisplayReply(command != "keep", FrameDisplayState.Idle, "当前没有需要恢复的临时分辨率。");

            _worker ??= StartWorker();
            var worker = _worker;
            var restoring = command == "restore" || command.StartsWith("cancel ", StringComparison.Ordinal);
            if (_inputClosed) return await FinishWorkerAsync(worker, restoring).ConfigureAwait(false);

            try
            {
                await worker.StandardInput.WriteLineAsync(command).WaitAsync(ReplyTimeout).ConfigureAwait(false);
                var line = await worker.StandardOutput.ReadLineAsync().WaitAsync(ReplyTimeout).ConfigureAwait(false);
                var reply = line is null ? null : JsonSerializer.Deserialize<FrameDisplayReply>(line);
                if (reply is null) throw new IOException("分辨率进程未返回结果。");
                if (stopWhenIdle && reply.State == FrameDisplayState.Idle)
                {
                    CloseInput(worker);
                    var finished = await FinishWorkerAsync(worker, restoring: true).ConfigureAwait(false);
                    if (!finished.Success) return finished;
                }
                return reply;
            }
            catch (Exception ex)
            {
                Log.Warn("帧格分辨率进程通信失败：" + ex.Message);
                CloseInput(worker);
                return await FinishWorkerAsync(worker, restoring).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("帧格分辨率请求失败：" + ex.Message);
            return new FrameDisplayReply(false, FrameDisplayState.Idle, "无法启动分辨率切换进程。");
        }
        finally
        {
            Sync.Release();
        }
    }

    private static Process StartWorker()
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "DeltaNFD.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add(FrameDisplaySession.Argument);
        return Process.Start(start) ?? throw new IOException("分辨率进程启动失败。");
    }

    private static void CloseInput(Process worker)
    {
        _inputClosed = true;
        try { worker.StandardInput.Close(); }
        catch (IOException ex) { Log.Warn("帧格分辨率进程输入关闭失败：" + ex.Message); }
    }

    private static async Task<FrameDisplayReply> FinishWorkerAsync(Process worker, bool restoring)
    {
        try { await worker.WaitForExitAsync().WaitAsync(ReplyTimeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            return new(false, FrameDisplayState.RestorePending, "分辨率进程仍在收尾。请稍后重试关闭帧格。");
        }

        var restored = worker.ExitCode == 0;
        worker.Dispose();
        _worker = null;
        _inputClosed = false;
        if (!restored)
        {
            Log.Warn("帧格分辨率进程退出，原分辨率未核实。");
            return new(false, FrameDisplayState.Idle, "未能核实原分辨率已恢复。请在 Windows 显示设置中检查。");
        }
        return new(restoring, FrameDisplayState.Idle, "分辨率进程已结束，临时分辨率已恢复。");
    }
}
