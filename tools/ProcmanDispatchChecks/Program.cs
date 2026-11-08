using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

var checks = 0;
void Check(bool value, string why) { if (!value) throw new Exception(why); checks++; }
var service = BackendProcmanService.Instance;
using var cancellation = new CancellationTokenSource();
var argument = new { Test = "forward unchanged" };
var expected = BackendResult.Ok("fixture", "forwarded");
BackendCaller.Dispatch = (_, _, _) => Task.FromResult(expected);
Console.WriteLine("Calling actual production procman wrapper through fake transport.");
var result = await service.InvokeAsync(BackendCommands.ProcmanActivate, argument, cancellation.Token);
Check(ReferenceEquals(result, expected), "Return original transport result");
Check(BackendCaller.Calls.Count == 1, "One service call must dispatch exactly once");
Check(BackendCaller.Calls[0].Command == BackendCommands.ProcmanActivate, "Forward original command");
Check(ReferenceEquals(BackendCaller.Calls[0].Args, argument), "Forward original arguments");
Check(BackendCaller.Calls[0].Token == cancellation.Token, "Forward cancellation token");

var current = new ProcmanStatusSnapshot { UpdatedUtc = DateTimeOffset.UtcNow,
    OptimalClock = new() { Resolution100ns = 5200 } };
BackendCaller.Dispatch = (_, _, _) => Task.FromResult(BackendResult.Ok("status", "current", current));
await service.InvokeAsync(BackendCommands.ProcmanStatus, new { });
Check(service.HasCurrentStatus, "Valid status is current");
Check(service.ClockState?.Resolution100ns == 5200, "Clock state still comes from backend snapshot");
Check(service.StatusError.Length == 0, "Successful status clears prior error");

BackendCaller.Dispatch = (_, _, _) => Task.FromResult(BackendResult.Failed("fixture", "read failed"));
await service.InvokeAsync(BackendCommands.ProcmanStatus, new { });
Check(!service.HasCurrentStatus && service.StatusError.Contains("read failed"), "Failed read cannot look current");
Check(service.ClockState?.Resolution100ns == 5200, "Failed read preserves historical snapshot");

BackendCaller.Dispatch = (_, _, _) => Task.FromResult(BackendResult.Ok("status", "wrong module",
    new ProcmanStatusSnapshot { Module = "system", UpdatedUtc = DateTimeOffset.UtcNow }));
await service.InvokeAsync(BackendCommands.ProcmanStatus, new { });
Check(!service.HasCurrentStatus && service.StatusError.Contains("模块不匹配"), "Wrong module remains rejected");

var failed = false;
BackendCaller.Dispatch = (_, _, _) => throw new IOException("fixture disconnected");
try { await service.InvokeAsync(BackendCommands.ProcmanStatus, new { }); }
catch (IOException) { failed = true; }
Check(failed && service.StatusError.Contains("fixture disconnected"), "Transport errors propagate and mark status unreadable");

var staleClient = BackendCaller.Client;
BackendCaller.Client = new();
BackendCaller.Dispatch = (_, _, _) => Task.FromResult(BackendResult.Ok("status", "replacement", current));
await service.InvokeAsync(BackendCommands.ProcmanStatus, new { });
staleClient.Publish(new() { UpdatedUtc = DateTimeOffset.UtcNow, OptimalClock = new() { Resolution100ns = 5300 } });
Check(service.ClockState?.Resolution100ns == 5200, "Old client cannot overwrite current state");
BackendCaller.Client.Publish(new() { UpdatedUtc = DateTimeOffset.UtcNow, OptimalClock = new() { Resolution100ns = 5250 } });
Check(service.ClockState?.Resolution100ns == 5250, "Current client status updates still work");

var startCalls = BackendCaller.Calls.Count;
AppSettingsStore.Current.TimerIgnoreExtraProcesses = ["fixture.exe"];
BackendCaller.Dispatch = (_, _, _) => Task.FromResult(expected);
await service.ActivateSavedRulesAsync();
Check(BackendCaller.Calls.Count == startCalls + 1 && BackendCaller.Calls[^1].Command == BackendCommands.ProcmanActivate,
    "Startup activation of saved ignore list dispatches once");
BackendCaller.Dispatch = (command, _, _) => Task.FromResult(command == BackendCommands.ProcmanStatus
    ? BackendResult.Ok("status", "current", current) : expected);
startCalls = BackendCaller.Calls.Count;
Check((await service.SyncFrameBackgroundAsync()).Success, "Frame background sync still succeeds");
Check(BackendCaller.Calls.Count == startCalls + 2, "Frame sync dispatches sync then status");
Check(BackendCaller.Calls[^2].Command == BackendCommands.ProcmanFrameBackgroundSync
    && BackendCaller.Calls[^1].Command == BackendCommands.ProcmanStatus, "Preserve sync/status order");
Console.WriteLine($"Procman dispatch: {checks} checks passed. Actual service, fake transport; no native or user-data writes.");
