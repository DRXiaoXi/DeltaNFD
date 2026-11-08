using System.Diagnostics;
using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Services;

internal static class RuntimeBackendReliabilityChecks
{
    internal static void FailureChild()
    {
        using var stream = new MemoryStream();
        var writer = new BackendPipeWriter(stream);
        var session = Guid.NewGuid(); var request = Guid.NewGuid();
        var context = new BackendCommandContext("fixture", default, session, request, default,
            p => writer.SafeWriteProgress(BackendModules.Runtime, session, request, p));
        try { context.Progress("fixture").Report(new string('x', BackendProtocol.MaxFrameBytes + 1)); }
        catch (BackendContractException) { Console.WriteLine("callback caught within command"); return; }
        Thread.Sleep(1000); // 旧异步回调会在此期间越过命令 catch，终止这个隔离子进程。
        Environment.ExitCode = 2;
    }
    private sealed class Posted : SynchronizationContext
    {
        internal int Count;
        public override void Post(SendOrPostCallback callback, object? state) => Count++;
    }
    private sealed class Broken { public string Value => throw new IOException("serialization failed"); }
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
        var previous = SynchronizationContext.Current;
        var posted = new Posted();
        var messages = new List<string>();
        var thread = Environment.CurrentManagedThreadId;
        try
        {
            SynchronizationContext.SetSynchronizationContext(posted);
            var context = new BackendCommandContext("fixture", default, Guid.NewGuid(), Guid.NewGuid(), default,
                p => { Check(Environment.CurrentManagedThreadId == thread, "progress escaped command thread"); messages.Add(p.Stage + ":" + p.Message); });
            var progress = context.Progress("vc14-repair");
            progress.Report("prepared"); progress.Report("running");
            Check(messages.SequenceEqual(new[] { "vc14-repair:prepared", "vc14-repair:running" }) && posted.Count == 0, "progress queued after result");
            var broken = new BackendCommandContext("fixture", default, Guid.NewGuid(), Guid.NewGuid(), default, _ => throw new IOException("progress transport"));
            var caught = false;
            try { broken.Progress("fixture").Report("message"); } catch (IOException) { caught = true; }
            Check(caught && posted.Count == 0, "callback exception escaped command catch");
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }

        // Actual encoding failure remains catchable in the command. A subsequent ordinary frame can still be sent.
        using var stream = new MemoryStream();
        var writer = new BackendPipeWriter(stream);
        var session = Guid.NewGuid(); var request = Guid.NewGuid();
        var encoding = new BackendCommandContext("fixture", default, session, request, default,
            p => writer.SafeWriteProgress(BackendModules.Runtime, session, request, p));
        var rejected = false;
        try { encoding.Progress("fixture").Report(new string('x', BackendProtocol.MaxFrameBytes + 1)); }
        catch (BackendContractException) { rejected = true; }
        Check(rejected && stream.Length == 0, "oversized progress not contained");
        encoding.Progress("fixture").Report("still connected");
        Check(stream.Length > 0, "valid progress could not follow failed encoding");

        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-runtime-reliability", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "journal.json");
            RuntimeOperationJournal.Write(path, new { Stage = "prepared" });
            var bytes = File.ReadAllBytes(path);
            Check(JsonDocument.Parse(bytes).RootElement.GetProperty("Stage").GetString() == "prepared", "initial journal invalid");
            try { RuntimeOperationJournal.Write(path, new { Stage = "unexpected" }); } catch (IOException) { }
            Check(File.ReadAllBytes(path).SequenceEqual(bytes), "create overwrote existing responsibility");
            try { RuntimeOperationJournal.Write(path, new Broken(), overwrite: true); } catch (Exception) { }
            Check(File.ReadAllBytes(path).SequenceEqual(bytes), "serialization failure damaged old journal");
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { RuntimeOperationJournal.Write(path, new { Stage = "running" }, overwrite: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                Check(File.ReadAllBytes(path).SequenceEqual(bytes), "failed publish damaged old journal");
            }
            RuntimeOperationJournal.Write(path, new { Stage = "running" }, overwrite: true);
            Check(JsonDocument.Parse(File.ReadAllBytes(path)).RootElement.GetProperty("Stage").GetString() == "running", "replacement invalid");
            Check(Directory.GetFiles(root, "*.tmp").Length == 0, "temporary journal leaked");

            ProcessStartInfo Child(int delay, int code)
            {
                var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in new[] { "--runtime-reliability-child", delay.ToString(), code.ToString() }) info.ArgumentList.Add(arg);
                return info;
            }
            Vc14ReinstallEnvironment EnvironmentAt(string name, bool busy = false) => new(dataRoot: Path.Combine(root, name),
                diagnosticsDirectory: Path.Combine(root, name, "logs"), installerBusy: () => busy);
            var environment = EnvironmentAt("success");
            var step = await environment.RunProcessAsync(Child(60, 0), null, default, TimeSpan.FromSeconds(20));
            Check(step.Success && !step.Unknown, "harmless child normal exit");
            var active = Path.Combine(root, "success", "runtime-repair-active.json");
            Check(!File.Exists(active) && Directory.GetFiles(Path.Combine(root, "success", "runtime-repair-history")).Length == 1, "completed activity not archived");
            Check(Directory.GetFiles(environment.DiagnosticsDirectory, "installer-*.json").Length == 2, "launch/exit diagnostics missing");
            environment = EnvironmentAt("failed-launch");
            step = await environment.RunProcessAsync(new ProcessStartInfo(Path.Combine(root, "missing.exe")) { UseShellExecute = false }, null, default, TimeSpan.FromSeconds(10));
            Check(!step.Success && step.Unknown && !File.Exists(Path.Combine(root, "failed-launch", "runtime-repair-active.json")), "failed launch responsibility");
            environment = EnvironmentAt("busy", true);
            step = await environment.RunProcessAsync(Child(60, 0), null, default, TimeSpan.FromSeconds(20));
            Check(step.Unknown && File.Exists(Path.Combine(root, "busy", "runtime-repair-active.json")), "busy transaction responsibility cleared");
            environment = EnvironmentAt("callback-failed");
            var context = new BackendCommandContext("fixture", default, session, request, default, _ => throw new IOException("callback failed"));
            step = await environment.RunProcessAsync(Child(500, 0), context.Progress("fixture"), default, TimeSpan.FromSeconds(20));
            Check(step.Unknown && !step.Success, "callback failure reported success");
            var record = Path.Combine(root, "callback-failed", "runtime-repair-active.json");
            using (var data = JsonDocument.Parse(File.ReadAllBytes(record)))
            {
                Check(data.RootElement.GetProperty("Stage").GetString() == "running", "launched child identity lost");
                try { using var child = Process.GetProcessById(data.RootElement.GetProperty("Pid").GetInt32()); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (ArgumentException) { }
            }
            var tokenSource = new CancellationTokenSource(); tokenSource.Cancel();
            environment = EnvironmentAt("cancelled");
            var cancelled = false;
            try { await environment.RunProcessAsync(Child(1, 0), null, tokenSource.Token, TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && !Directory.Exists(Path.Combine(root, "cancelled")), "prelaunch cancellation wrote activity");
            var failureChild = new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            failureChild.ArgumentList.Add("--runtime-progress-failure-child");
            using var fixture = Process.Start(failureChild) ?? throw new Exception("failure fixture not started");
            var output = fixture.StandardOutput.ReadToEndAsync(); var error = fixture.StandardError.ReadToEndAsync();
            await fixture.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Check(fixture.ExitCode == 0 && (await output).Contains("callback caught within command") && (await error).Length == 0,
                "progress callback terminated isolated child");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"Runtime backend reliability: {count} checks passed; isolated journals and harmless self children, no installers executed.");
    }
}
