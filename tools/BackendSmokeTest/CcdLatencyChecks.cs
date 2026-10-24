using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using DeltaNFD.Execution.Procman;
using DeltaNFD.Native;
using DeltaNFD.Services;

internal static class CcdLatencyChecks
{
    private static int _checks;
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); _checks++; }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [StructLayout(LayoutKind.Sequential)]
    private struct GroupMask { public nuint Mask; public ushort Group, R0, R1, R2; }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadSelectedCpuSetMasks(nint thread, [Out] GroupMask[] masks, ushort count, out ushort required);

    internal static void Child()
    {
        Console.WriteLine(GetCurrentThreadId());
        Console.Out.Flush();
        Console.ReadLine(); // Dedicated test child: never opens player data or changes settings.
    }

    internal static void Run()
    {
        _checks = 0;
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-ccd-latency-" + id.ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        var owners = Path.Combine(root, "procman-owners.json");
        var threads = new List<Thread>();
        var tids = new int[64];
        string? measurements = null;
        using var release = new ManualResetEventSlim();
        using var ready = new CountdownEvent(tids.Length);
        using var self = Process.GetCurrentProcess();
        var handle = CpuSets.OpenProcessForCpuSets(self.Id);
        Check(handle != 0 && CpuSets.TryGetDefaultCpuSetMasks(handle, out _), "query own original CPU sets");
        if (!CpuSets.TryGetDefaultCpuSetMasks(handle, out var originals)) throw new IOException("cannot read originals");
        try
        {
            Check(OfflineCpuSetCatalog.TryRead(out var catalog, out _), "read available CPU catalog");
            var candidate = catalog.First(x => x.Group == 0 && x.IsAvailable && x.LogicalProcessorIndex < 64
                && ((ulong)self.ProcessorAffinity & (1UL << x.LogicalProcessorIndex)) != 0);
            var mask = 1UL << candidate.LogicalProcessorIndex;
            for (var i = 0; i < tids.Length; i++)
            {
                var index = i;
                var thread = new Thread(() => { tids[index] = (int)GetCurrentThreadId(); ready.Signal(); release.Wait(); }) { IsBackground = true };
                threads.Add(thread); thread.Start();
            }
            Check(ready.Wait(TimeSpan.FromSeconds(10)), "owned worker threads ready");
            // Synthetic accumulated ownership history of comparable size, never a copy of player records.
            var history = Enumerable.Range(0, 14000).ToDictionary(i =>
                $"{1000000000 + i}|1|C:\\fixture\\historical-process-{i:D5}-with-a-long-synthetic-path.exe|cpuSet",
                _ => new ProcessPropertyGate.Claim());
            File.WriteAllText(owners, JsonSerializer.Serialize(history));
            var bytes = new FileInfo(owners).Length;
            Check(ProcmanIdentity.TryIdentify(self.Id) is not null, "read own complete process identity");
            using (var direct = ProcessPropertyGate.Default.Enter(ProcmanIdentity.TryIdentify(self.Id)!, "cpuSet"))
            { Check(direct.Owner == "system", "isolated ownership gate opens"); direct.Game(false); }
            using (var warm = ProcessPropertyGate.TryEnterGame(self.Id, "cpuSet")) Check(warm is not null, "write-ahead ownership succeeds");
            var reads = 0;
            ProcessPropertyGate.SnapshotReadForTests = () => Interlocked.Increment(ref reads);

            var time = Stopwatch.StartNew();
            using (var outer = ProcessPropertyGate.TryEnterGame(self.Id, "cpuSet"))
            {
                Check(outer is not null && CpuSets.SetDefaultByMask(handle, mask), "legacy default write");
                foreach (var tid in tids)
                {
                    var thread = CpuSets.OpenThreadForCpuSets(tid);
                    try { Check(CpuSets.SetThreadSelectedByMask(thread, mask), "legacy owned-thread write"); }
                    finally { CpuSets.CloseHandleSafe(thread); }
                }
            }
            var legacyMs = time.Elapsed.TotalMilliseconds;
            var legacyReads = reads;
            Check(legacyReads == tids.Length + 2, "legacy repeats one file read per native write plus outer gate");
            reads = 0; time.Restart();
            var session = CpuSets.WriteSession.TryOpen(self.Id);
            Check(session is not null && session.SetDefault(mask), "batch default write");
            using (session)
            {
                foreach (var tid in tids) Check(session!.SetThread(tid, mask), "batch owned-thread write");
                Check(!session!.SetDefault(0) && !session.SetThread(tids[0], 0), "zero masks rejected");
                Check(!Task.Run(() => session.SetDefault(mask)).GetAwaiter().GetResult(), "thread-affine lease cannot cross async threads");
            }
            var batchMs = time.Elapsed.TotalMilliseconds;
            Check(reads == 1, "whole process batch reads ownership once, independent of thread count");
            Check(!session!.SetDefault(mask) && !session.SetThread(tids[0], mask), "disposed session cannot write");
            Check(CpuSets.TryGetDefaultCpuSetMasks(handle, out var actual) && actual.SequenceEqual(new[] { new CpuSetGroupMask(0, mask) }), "batch process readback");
            foreach (var tid in tids)
            {
                var thread = CpuSets.OpenThreadForCpuSets(tid);
                try
                {
                    var buffer = new GroupMask[4];
                    Check(GetThreadSelectedCpuSetMasks(thread, buffer, 4, out var count) && count == 1
                        && buffer[0].Group == 0 && buffer[0].Mask == (nuint)mask, "existing-thread readback");
                }
                finally { CpuSets.CloseHandleSafe(thread); }
            }
            reads = 0;
            self.Refresh();
            Check(CpuTopologyService.TryApplyCcdSoft(self, mask) && reads == 1,
                "actual CCD apply/tick/revert helper uses one ownership snapshot for every existing thread");
            Check(JsonSerializer.Deserialize<Dictionary<string, ProcessPropertyGate.Claim>>(File.ReadAllText(owners))!.Count == history.Count + 1,
                "historical records are preserved, never pruned for performance");

            var identity = ProcmanIdentity.TryIdentify(self.Id)!;
            var gate = ProcessPropertyGate.Default;
            var differentProperty = gate.Enter(identity, "priority");
            using (differentProperty)
            {
                differentProperty.Game(false);
                Check(!differentProperty.AuthorizesGameWrite(self.Id, "cpuSet")
                    && !differentProperty.AuthorizesGameWrite(self.Id + 1, "priority"), "lease binds both identity and property");
            }
            Check(!differentProperty.AuthorizesGameWrite(self.Id, "priority"), "disposed gate loses authorization");
            using (var manual = gate.Enter(identity, "cpuSet")) manual.SetOwner("manual");
            Check(CpuSets.WriteSession.TryOpen(self.Id) is null && !CpuTopologyService.TryApplyCcdSoft(self, mask), "manual takeover prevents both session and CCD helper");
            using (var manual = gate.Enter(identity, "cpuSet")) manual.SetOwner(null);
            var held = CpuSets.WriteSession.TryOpen(self.Id)!;
            using var waiting = new ManualResetEventSlim();
            var takeover = Task.Factory.StartNew(() =>
            {
                waiting.Set();
                using var manual = gate.Enter(identity, "cpuSet");
                manual.SetOwner("manual");
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            waiting.Wait();
            Check(!takeover.Wait(75), "cross-thread takeover waits while process batch holds the same mutex");
            Check(held.SetThread(tids[0], mask), "batch completes before takeover");
            held.Dispose(); takeover.GetAwaiter().GetResult();
            Check(CpuSets.WriteSession.TryOpen(self.Id) is null, "next operation reloads changed ownership instead of stale cache");
            using (var manual = gate.Enter(identity, "cpuSet")) manual.SetOwner(null);

            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("--ccd-latency-child");
            using (var child = Process.Start(start)!)
            {
                try
                {
                    var childTid = int.Parse(child.StandardOutput.ReadLine()!);
                    using (var own = CpuSets.WriteSession.TryOpen(self.Id)!)
                        Check(!own.SetThread(childTid, mask), "foreign process TID cannot use another identity's lease");
                    using var childWriter = CpuSets.WriteSession.TryOpen(child.Id)!;
                    child.StandardInput.WriteLine(); child.StandardInput.Flush();
                    Check(child.WaitForExit(10000), "owned child exits naturally");
                    Check(!childWriter.SetDefault(mask) && !childWriter.SetThread(childTid, mask), "exited process identity is rechecked for each native write");
                }
                finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(); } }
            }
            var saved = File.ReadAllText(owners);
            File.WriteAllText(owners, "{");
            Check(CpuSets.WriteSession.TryOpen(self.Id) is null && !CpuTopologyService.TryApplyCcdSoft(self, mask), "malformed ownership remains fail-closed");
            File.WriteAllText(owners, saved);
            using (var fresh = CpuSets.WriteSession.TryOpen(self.Id)) Check(fresh is not null, "lease released after malformed store rejection");
            using (var restored = ProcessPropertyGate.TryEnterGame(self.Id, "cpuSet", restoring: true))
                Check(restored is not null, "release own claim before failed write-ahead test");
            var beforeDenied = File.ReadAllBytes(owners);
            File.SetAttributes(owners, FileAttributes.ReadOnly);
            try
            {
                Check(CpuSets.WriteSession.TryOpen(self.Id) is null, "write-ahead persistence failure prevents native writes");
                Check(File.ReadAllBytes(owners).SequenceEqual(beforeDenied), "failed persistence preserves original records");
            }
            finally { File.SetAttributes(owners, FileAttributes.Normal); }
            measurements = $"Synthetic owners={bytes} bytes; owned threads={tids.Length}; "
                + $"legacy={legacyMs:F1} ms/{legacyReads} reads; batch={batchMs:F1} ms/1 read. No player process/settings/records changed.";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("CCD fixture failure before cleanup: " + ex);
            throw;
        }
        finally
        {
            ProcessPropertyGate.SnapshotReadForTests = null;
            try { Check(CpuSets.TrySetDefaultCpuSetMasks(handle, originals), "restore own original default CPU sets"); }
            finally
            {
                CpuSets.CloseHandleSafe(handle);
                release.Set(); foreach (var thread in threads) thread.Join();
                var prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("fixture escaped temp");
                Directory.Delete(root, recursive: true);
            }
        }
        Console.WriteLine($"CCD latency checks: {_checks} passed. " + measurements);
    }
}
