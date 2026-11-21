using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Services;

internal static class DisplayContainerWireProbe
{
    public static async Task RunAsync(string layout, string log)
    {
        const string module = BackendModules.Gpu;
        layout = Path.GetFullPath(layout);
        var path = Path.Combine(layout, "Backends", "DeltaNFD.Backend.Gpu.exe");
        // Verify the exact parser shipped with the front end, not just the test build's copy.
        var frontend = new AssemblyLoadContext("Published front-end contract", isCollectible: true);
        var contract = frontend.LoadFromAssemblyPath(Path.Combine(layout, "DeltaNFD.Contracts.dll"));
        var parseReady = contract.GetType(typeof(BackendMessages).FullName!, throwOnError: true)!.GetMethod(nameof(BackendMessages.ParseReady))!;
        var session = Guid.NewGuid();
        var data = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(log))!, "wire-probe-" + session.ToString("N"));
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "isolated-test.marker"), session.ToString("D"));
        var pipeName = "DeltaNFD.DisplayContainerReadOnly." + session.ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = timeout.Token;
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = data };
        start.Environment["DELTANFD_BACKEND_ISOLATED_ROOT"] = data;
        start.Environment["DELTANFD_BACKEND_ISOLATED_ID"] = session.ToString("D");
        foreach (var arg in new[] { "--backend", module, "--pipe", pipeName, "--host-pid", Environment.ProcessId.ToString() })
            start.ArgumentList.Add(arg);
        using var worker = Process.Start(start) ?? throw new InvalidOperationException("GPU backend did not start.");
        await pipe.WaitForConnectionAsync(token);
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await pipe.WriteAsync(BackendMessages.EncodeHello(module, session, Guid.NewGuid(), new(nonce, "0.9403.0", data)), token);
        var readyFrame = await BackendFrameStream.ReadFrameAsync(pipe, token);
        if (readyFrame?.Type != BackendFrameType.Ready) throw new InvalidOperationException("GPU backend did not send ready.");
        object shippedReady;
        try { shippedReady = parseReady.Invoke(null, [readyFrame.Payload])!; }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        { throw new InvalidOperationException("Published front-end parser: " + ex.InnerException.Message, ex.InnerException); }
        if (shippedReady.GetType().GetProperty(nameof(BackendReady.NvDisplayContainerVersion))?.GetValue(shippedReady)
            is not int shippedVersion || shippedVersion != NvDisplayContainerChange.ProtocolVersion)
            throw new InvalidOperationException("Published front end did not read the advertised capability.");
        var ready = BackendMessages.ParseReady(readyFrame.Payload);
        if (ready.Nonce != nonce || ready.Pid != worker.Id || ready.NvDisplayContainerVersion != NvDisplayContainerChange.ProtocolVersion)
            throw new InvalidOperationException("GPU backend identity or display container protocol differs.");

        async Task<JsonElement> ReadOnlyAsync(string command, object input)
        {
            if (!BackendModules.IsReadOnly(module, command)) throw new InvalidOperationException("The probe must not send write commands.");
            var request = Guid.NewGuid();
            await pipe.WriteAsync(BackendMessages.EncodeInvoke(module, session, request, new(command, JsonSerializer.SerializeToElement(input, BackendJson.SerializerOptions))), token);
            while (await BackendFrameStream.ReadFrameAsync(pipe, token) is { } frame)
            {
                if (frame.Type != BackendFrameType.Result || frame.RequestId != request) continue;
                var result = BackendMessages.ParseResult(frame.Payload);
                return result is { Status: BackendResultStatuses.Ok, Data: { } body, RequiresReview: false }
                    ? body : throw new InvalidOperationException(command + ": " + result.Message);
            }
            throw new EndOfStreamException(command);
        }
        var before = BackendJson.Deserialize<NvDisplayContainerStatus>(await ReadOnlyAsync(BackendCommands.GpuNvDisplayContainerStatus, new { }));
        var reviewed = BackendJson.Deserialize<ReconcileSnapshot>(await ReadOnlyAsync(BackendCommands.GpuReconcile, new RecoverArgs { Scope = "nv-display-container" }));
        var driver = await ReadOnlyAsync(BackendCommands.GpuDriverRead, new NvDriverRestoreArgs());
        if (driver.ValueKind != JsonValueKind.Object || !driver.EnumerateObject().Any())
            throw new InvalidOperationException("The NVIDIA game settings read returned no settings.");
        var after = BackendJson.Deserialize<NvDisplayContainerStatus>(await ReadOnlyAsync(BackendCommands.GpuNvDisplayContainerStatus, new { }));
        if (before.Fingerprint is null || before != after || !reviewed.Complete || reviewed.Module != module
            || reviewed.Items is not [{ Id: "nv-display-container", Kind: "service" }] || reviewed.Items[0].State != before.Detail)
            throw new InvalidOperationException("Invalid or changed status: " + before.Detail + " / " + reviewed.Summary + " / " + after.Detail);
        await pipe.WriteAsync(BackendMessages.EncodeStop(module, session, Guid.NewGuid()), token);
        while (await BackendFrameStream.ReadFrameAsync(pipe, token) is { } frame)
            if (frame.Type == BackendFrameType.Status && BackendMessages.ParseStatus(frame.Payload).State == BackendMessages.StateStopped) break;
        await worker.WaitForExitAsync(token);
        if (worker.ExitCode != 0) throw new InvalidOperationException("GPU backend did not exit normally.");
        File.WriteAllLines(log, [$"Elevated: {ElevationHelper.IsElevated}", $"NvDisplayContainerVersion: {ready.NvDisplayContainerVersion}",
            before.Detail, $"Restore available: {before.Restore}", $"NVIDIA game settings read: {driver.EnumerateObject().Count()} items",
            "PASS: packaged front-end parser + actual GPU backend + status + reconcile + game settings read + unchanged status + clean exit. No service or driver writes."]);
        frontend.Unload();
    }
}
