using System.Diagnostics;
using System.IO.Pipes;
using DeltaNFD.Services;
using DeltaNFD.Services.Plugins;

internal static class PluginStabilityChecks
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD_PluginStability_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var manager = new PluginManagerService(Path.Combine(root, "Plugins"));
            Require(manager.DataRoot == Path.Combine(root, "PluginData"), "custom roots do not use real user PluginData");
            Require(manager.TryWriteConfig("org.example.probe", "{}", out _), "isolated config write");
            Require(File.Exists(Path.Combine(root, "PluginData/org.example.probe/config.json")), "config remains in fixture");
            var bad = Path.Combine(root, "not-a-file");
            Directory.CreateDirectory(bad);
            Require(!new PluginBackupStore(bad).TryList(out _, out _), "directory backup fails closed");
            Require(!new PluginHandoffStore(bad).TryList(out _, out _), "directory handoff fails closed");
            Require(!new PluginPackageIndex(bad).TryRead(out _, out _), "directory index fails closed");
            var runtime = new PluginRuntimeService(new PluginBackupStore(bad));
            Require((await runtime.RestoreAsync(new PluginIndexEntry { Id = "org.example.probe" })).Contains("读取"),
                "unreadable backup is not no-backup success");
            runtime = new PluginRuntimeService(manager.Backups, manager.Handoffs, manager.DataRoot);
            Require((await PluginLifecycleGuard.PrepareHandoverAsync(manager, runtime, "测试交接")).Length == 0,
                "empty isolated handover succeeds");
            Directory.CreateDirectory(Path.GetDirectoryName(manager.Backups.PathName)!);
            File.WriteAllText(manager.Backups.PathName, "null");
            Require((await PluginLifecycleGuard.PrepareHandoverAsync(manager, runtime, "测试交接")).Length > 0,
                "null repository blocks handover");
            File.Delete(manager.Backups.PathName);
            await CheckFramesAsync();
            Console.WriteLine("Plugin stability checks passed: isolated data, fail-closed stores, handover evidence, coalesced frames, total read deadline.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task CheckFramesAsync()
    {
        using var session = new PluginBackendSession();
        using var client = new NamedPipeClientStream(".", session.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var connect = client.ConnectAsync(5000);
        Require(await session.WaitForClientAsync(Environment.ProcessId, TimeSpan.FromSeconds(5), default), "pipe client PID");
        await connect;
        var id = Guid.NewGuid(); var firstId = Guid.NewGuid(); var secondId = Guid.NewGuid();
        var first = PluginIpc.Encode(PluginIpcType.Ping, id, firstId, "{}");
        var second = PluginIpc.Encode(PluginIpcType.Pong, id, secondId, "{}");
        var write = client.WriteAsync(first.Concat(second).ToArray()).AsTask();
        var one = await session.ReadFrameAsync(TimeSpan.FromSeconds(2), default);
        var two = await session.ReadFrameAsync(TimeSpan.FromSeconds(2), default);
        await write.WaitAsync(TimeSpan.FromSeconds(2));
        Require(one.Frame?.RequestId == firstId && two.Frame?.RequestId == secondId, "compaction cannot corrupt first coalesced frame");
        using var cancel = new CancellationTokenSource();
        var drip = Task.Run(async () =>
        {
            try
            {
                foreach (var b in first)
                {
                    await client.WriteAsync(new byte[] { b }, cancel.Token);
                    await Task.Delay(25, cancel.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
        var watch = Stopwatch.StartNew();
        try
        {
            await session.ReadFrameAsync(TimeSpan.FromMilliseconds(180), default);
            throw new Exception("trickle input bypassed total deadline");
        }
        catch (TimeoutException) { Require(watch.Elapsed < TimeSpan.FromSeconds(2), "one deadline across partial reads"); }
        finally { cancel.Cancel(); await drip; }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
