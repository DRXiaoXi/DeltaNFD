using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Services;

internal static class PageBusyStatusChecks
{
    internal static Task<int> RunChildAsync(string[] args)
    {
        var module = BackendModules.Require(BackendModules.CpuFrame);
        return BackendHost.RunAsync(new BackendHostOptions
        {
            Module = module, StatusWhileBusy = true, StatusInterval = TimeSpan.FromMilliseconds(25),
            StatusSource = () => new BackendStatus(BackendMessages.StateIdle, null, JsonSerializer.SerializeToElement(new CpuFrameStatusSnapshot
            { MonitorRunning = true, IsCustomTarget = true, GameRunning = false, DeltaForceRunning = true, DeltaForceObservedUtc = DateTimeOffset.UtcNow }, BackendJson.SerializerOptions)),
            Handlers = module.Commands.ToDictionary(command => command, command => (Func<BackendCommandContext, Task<BackendResult>>)(async context =>
            {
                if (command == BackendCommands.CpuFrameTopology)
                {
                    if (args.Contains("--sync-before-await", StringComparer.Ordinal)) Thread.Sleep(400);
                    else await Task.Delay(400, context.CancellationToken);
                }
                return BackendResult.Ok(command, "isolated read-only fixture");
            })),
        }, args);
    }

    internal static async Task RunAsync(string root, Guid testId)
    {
        await RunOneAsync(root, testId, synchronous: false);
        await RunOneAsync(root, testId, synchronous: true);
    }

    private static async Task RunOneAsync(string root, Guid testId, bool synchronous)
    {
        var pipeName = "DeltaNFD.PageBusyFixture." + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--page-busy-status-fixture", "--backend", BackendModules.CpuFrame, "--pipe", pipeName, "--host-pid", Environment.ProcessId.ToString() }) start.ArgumentList.Add(arg);
        if (synchronous) start.ArgumentList.Add("--sync-before-await");
        start.Environment["DELTANFD_BACKEND_ISOLATED_ROOT"] = root;
        start.Environment["DELTANFD_BACKEND_ISOLATED_ID"] = testId.ToString("D");
        using var process = Process.Start(start) ?? throw new Exception("fixture start");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var session = Guid.NewGuid();
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            await pipe.WriteAsync(BackendMessages.EncodeHello(BackendModules.CpuFrame, session, Guid.NewGuid(), new BackendHello(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), "0.9223.0", root)), timeout.Token);
            var ready = await BackendFrameStream.ReadFrameAsync(pipe, timeout.Token);
            if (ready?.Type != BackendFrameType.Ready) throw new Exception("fixture ready");
            var request = Guid.NewGuid();
            await pipe.WriteAsync(BackendMessages.EncodeInvoke(BackendModules.CpuFrame, session, request,
                new BackendInvoke(BackendCommands.CpuFrameTopology, JsonSerializer.SerializeToElement(new { }))), timeout.Token);
            await pipe.WriteAsync(BackendProtocol.Encode(BackendFrameType.Ping, BackendModules.CpuFrame, session, Guid.NewGuid(), "{}"), timeout.Token);
            int busy = 0;
            var pongBeforeResult = false;
            while (true)
            {
                var frame = await BackendFrameStream.ReadFrameAsync(pipe, timeout.Token) ?? throw new EndOfStreamException();
                if (frame.Type == BackendFrameType.Pong) pongBeforeResult = true;
                if (frame.Type == BackendFrameType.Status)
                {
                    var status = BackendMessages.ParseStatus(frame.Payload);
                    if (status.State != BackendMessages.StateBusy) continue;
                    if (status.CurrentRequestId != request || status.Data is not { } data) throw new Exception("busy request identity");
                    var snapshot = BackendJson.Deserialize<CpuFrameStatusSnapshot>(data);
                    if (!snapshot.IsCustomTarget || snapshot.GameRunning || snapshot.DeltaForceRunning != true) throw new Exception("independent game observation");
                    busy++;
                }
                if (frame.Type == BackendFrameType.Result)
                {
                    if (frame.RequestId != request || BackendMessages.ParseResult(frame.Payload).Status != BackendResultStatuses.Ok) throw new Exception("ordered result");
                    break;
                }
            }
            if (busy == 0) throw new Exception("busy command hid game monitor snapshots");
            if (!pongBeforeResult) throw new Exception("synchronous handler blocked pipe control");
            await pipe.WriteAsync(BackendMessages.EncodeStop(BackendModules.CpuFrame, session, Guid.NewGuid()), timeout.Token);
            while (await BackendFrameStream.ReadFrameAsync(pipe, timeout.Token) is { } frame)
                if (frame.Type == BackendFrameType.Status && BackendMessages.ParseStatus(frame.Payload).State == BackendMessages.StateStopped) break;
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0) throw new Exception("fixture did not exit normally");
            Console.WriteLine($"忙碌监控真实管道检查通过（{(synchronous ? "同步阻塞" : "异步等待")}）：收到 {busy} 个忙碌快照，Ping、结果与退出帧正常；全部操作为自有夹具。");
        }
        finally
        {
            // This is exclusively the child created immediately above, never an external/game process.
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }
}
