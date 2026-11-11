using System.Buffers.Binary;
using System.Text.Json;
using DeltaNFD.AgentBridge;
using DeltaNFD.Backends;

static JsonElement Args(string text = "{}") => JsonDocument.Parse(text).RootElement.Clone();
if (args.Length == 1 && args[0] == "--fixture")
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows pipe fixture required.");
    using var fixtureIdentity = System.Security.Principal.WindowsIdentity.GetCurrent();
    var fixtureUserSid = fixtureIdentity.User?.Value;
    var ops = new AgentOperations((spec, arguments, token) => Task.FromResult(true),
        async (spec, arguments, progress) => { await Task.Delay(20); progress.Report("fixture progress"); return BackendResult.Ok(spec.Name, "simulated only", new { simulated = true }); });
    await using var server = new AgentPipeServer(AppContext.BaseDirectory, request => Task.FromResult<object>(request.Method switch
    {
        "status" => new { simulated = true, enabled = true, userSid = fixtureUserSid },
        "submit" => ops.Submit(request.Arguments.GetProperty("action").GetString()!, request.Arguments.GetProperty("arguments"), request.Arguments.GetProperty("requestId").GetString()!),
        "operation" => ops.Get(Guid.Parse(request.Arguments.GetProperty("operationId").GetString()!)),
        "cancel" => ops.Cancel(Guid.Parse(request.Arguments.GetProperty("operationId").GetString()!)),
        _ => throw new ArgumentException("unknown fixture method"),
    }));
    Console.WriteLine("READY " + AgentPipe.EndpointPath(AppContext.BaseDirectory));
    await Console.In.ReadLineAsync();
    ops.Close();
    return;
}

var count = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); count++; }
void Reject(Action action, string name) { try { action(); } catch (ArgumentException) { count++; return; } throw new Exception("FAIL accepted " + name); }
void Deny(Action action, string name) { try { action(); } catch (InvalidOperationException) { count++; return; } throw new Exception("FAIL admitted " + name); }
async Task<AgentOperation> Done(AgentOperations operations, Guid id)
{
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (true)
    {
        var status = operations.Get(id);
        if (status.State is "completed" or "partial" or "failed" or "cancelled" or "requires_review") return status;
        await Task.Delay(10, limit.Token);
    }
}
Check(AgentCatalog.Actions.Count == 112, "reviewed action count");
foreach (var action in AgentCatalog.Actions)
{
    Check(action.Module == "app" || BackendModules.IsAllowed(action.Module, action.Name), action.Name + " allowed");
    Check(action.Module == "app" || action.Mutating == BackendModules.IsMutating(action.Module, action.Name), action.Name + " mutation classification");
    Check(!action.Name.Contains("pendulum") && !action.Name.Contains("clock") && !action.Name.Contains("timer-ignore"), "public release restriction");
    AgentAdmission.Check(action, true, false, true, false); count++;
    Deny(() => AgentAdmission.Check(action, false, false, true, false), "disabled");
    Deny(() => AgentAdmission.Check(action, true, true, true, false), "maintenance");
    if (action.Mutating && action.Name != "app.review") Deny(() => AgentAdmission.Check(action, true, false, false, false), "admin");
    if (action.Module is "gpu" or "system" or "ace" or "runtime" || action.Name == "system.low-memory.status")
    {
        Deny(() => AgentAdmission.Check(action, true, false, true, true), "game running");
        Deny(() => AgentAdmission.Check(action, true, false, true, null), "game unknown");
    }
}
foreach (var action in new[] { "gpu.recover", "system.defender.exclusions.add", "procman.clock.apply", "cpuframe.power.lock", "system.pendulum.global.configure", "shell.exec", "app.shutdown" })
    Reject(() => AgentCatalog.Require(action), "excluded " + action);
Reject(() => AgentCatalog.Validate("app.status", Args("{\"command\":\"powershell\"}")), "unknown fields");
Reject(() => AgentCatalog.Validate("gpu.driver.apply", Args("{\"rebarWarningAccepted\":true}")), "agent cannot acknowledge UI warning");
Reject(() => AgentCatalog.Validate("gpu.driver.apply", Args("{\"changes\":[],\"changes\":[]}")), "duplicate fields");
Reject(() => AgentCatalog.Validate("system.power.hibernate.set", Args("{\"enable\":\"false\"}")), "typed boolean");
Reject(() => AgentCatalog.Validate("system.power.hibernate.set", Args("{\"Enable\":true}")), "strict property names");
Reject(() => AgentCatalog.Validate("runtime.guard.select-mode", Args("{\"mode\":\"unlisted\"}")), "enum");
Reject(() => AgentCatalog.Validate("gpu.driver.restore", Args("{\"ids\":[-1]}")), "unsigned IDs");
Reject(() => AgentCatalog.Validate("app.status", Args("null")), "object required");
Reject(() => AgentCatalog.Validate("system.power.hibernate.set", Args()), "writes require explicit values");
foreach (var capacity in new ulong?[] { 1, 16UL * 1024 * 1024 * 1024 })
    foreach (var tweak in new[] { DeltaNFD.Services.SystemTweak.MemoryCompression, DeltaNFD.Services.SystemTweak.PageCombining })
        Deny(() => AgentAdmission.CheckTweak("system.tweaks.apply", tweak, capacity), "low-memory UI restriction");
AgentAdmission.CheckTweak("system.tweaks.restore", DeltaNFD.Services.SystemTweak.MemoryCompression, 8UL * 1024 * 1024 * 1024); count++;
AgentAdmission.CheckTweak("system.tweaks.apply", DeltaNFD.Services.SystemTweak.MemoryCompression, 32UL * 1024 * 1024 * 1024); count++;
AgentCatalog.Validate("system.power.hibernate.set", Args("{\"enable\":false}")); count++;

var invoked = 0; var confirmed = 0;
var successOps = new AgentOperations((_, _, _) => { confirmed++; return Task.FromResult(true); },
    (_, _, progress) => { invoked++; progress.Report("progress"); return Task.FromResult(BackendResult.Ok("test", "done")); });
var read = successOps.Submit("app.status", Args(), "read-1");
Check((await Done(successOps, read.Id)).State == "completed" && confirmed == 0 && invoked == 1, "read without confirmation");
var write = successOps.Submit("gpu.driver.profile-reset", Args(), "write-1");
Check((await Done(successOps, write.Id)).State == "completed" && confirmed == 1 && invoked == 2, "write after confirmation");
Check(successOps.Submit("gpu.driver.profile-reset", Args(), "write-1").Id == write.Id && invoked == 2, "deduplicate completed write");
Reject(() => successOps.Submit("app.status", Args(), "write-1"), "id conflict");
Reject(() => successOps.Submit("app.status", Args(), ""), "empty id");
Reject(() => successOps.Get(Guid.NewGuid()), "unknown operation");
successOps.Close();
Deny(() => successOps.Submit("app.status", Args(), "after-close"), "closed admission");

var cancelledExecutions = 0;
var rejectOps = new AgentOperations((_, _, _) => Task.FromResult(false), (_, _, _) => { cancelledExecutions++; return Task.FromResult(BackendResult.Ok("test", "")); });
Check((await Done(rejectOps, rejectOps.Submit("gpu.driver.profile-reset", Args(), "cancel").Id)).State == "cancelled" && cancelledExecutions == 0, "local rejection writes nothing");

var confirmEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var waitOps = new AgentOperations(async (_, _, token) => { confirmEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return true; },
    (_, _, _) => { cancelledExecutions++; return Task.FromResult(BackendResult.Ok("test", "")); });
var waiting = waitOps.Submit("gpu.driver.profile-reset", Args(), "waiting"); await confirmEntered.Task;
Check(waitOps.Cancel(waiting.Id).State == "cancelled", "cancel confirmation");
Check((await Done(waitOps, waiting.Id)).State == "cancelled" && cancelledExecutions == 0, "cancel not executed");
waitOps.Close();

foreach (var (result, expected) in new (BackendResult, string)[]
{
    (BackendResult.PartialResult("test", "unknown", []), "requires_review"),
    (BackendResult.PartialResult("test", "known partial", [], requiresReview: false), "partial"),
    (BackendResult.Ok("test", "pending", pendingRestore: true), "requires_review"),
    (BackendResult.Ok("test", "nested failure", new { success = false }), "failed"),
    (BackendResult.Ok("test", "nested unknown", new { success = true, outcomeUnknown = true }), "requires_review"),
    (BackendResult.Ok("test", "nested partial", new { success = true, isPartial = true }), "partial"),
    (BackendResult.Failed("test", "failed"), "failed"),
    (BackendResult.Cancelled("test", "cancelled"), "cancelled"),
})
{
    var resultOps = new AgentOperations((_, _, _) => Task.FromResult(true), (_, _, _) => Task.FromResult(result));
    Check((await Done(resultOps, resultOps.Submit("gpu.driver.profile-reset", Args(), expected).Id)).State == expected, "honest result " + expected);
}
foreach (var ex in new Exception[] { new IOException("disconnect"), new OperationCanceledException() })
{
    var errorOps = new AgentOperations((_, _, _) => Task.FromResult(true), (_, _, _) => Task.FromException<BackendResult>(ex));
    Check((await Done(errorOps, errorOps.Submit("gpu.driver.profile-reset", Args(), "error").Id)).State == "requires_review", "interrupted write requires review");
}

var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var runningOps = new AgentOperations((_, _, _) => Task.FromResult(true), async (_, _, _) => { entered.TrySetResult(); await finish.Task; return BackendResult.Ok("test", "done"); });
var running = runningOps.Submit("gpu.driver.profile-reset", Args(), "running"); await entered.Task;
Check(runningOps.Cancel(running.Id).State == "running", "cannot kill in-flight write");
var queued = runningOps.Submit("app.status", Args(), "queued"); runningOps.Close();
Check(runningOps.Get(queued.Id).State == "cancelled", "close cancels queue");
Check(runningOps.Get(running.Id).State == "running", "close preserves in-flight write");
finish.TrySetResult(); Check((await Done(runningOps, running.Id)).State == "completed", "in-flight completes after disconnection");

foreach (var length in new[] { -1, 0, AgentPipe.MaxBytes + 1, int.MaxValue })
{
    using var stream = new MemoryStream(); var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, length); stream.Write(header); stream.Position = 0;
    try { await AgentPipe.ReadAsync<AgentResponse>(stream, default); throw new Exception("unbounded frame accepted"); }
    catch (InvalidDataException) { count++; }
}
using (var stream = new MemoryStream())
{
    await AgentPipe.WriteAsync(stream, new AgentResponse(true, Args("{\"text\":\"中文\"}"), ""), default); stream.Position = 0;
    var response = await AgentPipe.ReadAsync<AgentResponse>(stream, default);
    Check(response.Data!.Value.GetProperty("text").GetString() == "中文", "UTF8 framed roundtrip");
}
Console.WriteLine(JsonSerializer.Serialize(new { passed = count, systemWrites = false, gpuWrites = false }));
