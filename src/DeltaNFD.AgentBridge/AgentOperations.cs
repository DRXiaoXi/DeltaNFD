using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeltaNFD.Backends;

namespace DeltaNFD.AgentBridge;

public sealed record AgentOperation(Guid Id, string RequestId, string Action, string State,
    string Message, DateTimeOffset CreatedUtc, BackendResult? Result);

/// <summary>One queue per application. Transport disconnection never replays or kills a system write.</summary>
public sealed class AgentOperations(Func<AgentAction, JsonElement, CancellationToken, Task<bool>> confirm,
    Func<AgentAction, JsonElement, IProgress<string>, Task<BackendResult>> execute)
{
    private sealed class Work(AgentOperation operation, JsonElement args, string fingerprint)
    {
        public AgentOperation Value = operation;
        public JsonElement Args = args;
        public string Fingerprint = fingerprint;
        public CancellationTokenSource Cancel = new();
    }
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Work> _work = [];
    private readonly SemaphoreSlim _serial = new(1, 1);
    private bool _accepting = true;
    public event Action? Changed;
    public string LastMessage { get; private set; } = "尚无 Agent 操作。";

    public AgentOperation Submit(string action, JsonElement arguments, string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128) throw new ArgumentException("需要 1–128 字符的 requestId；重试必须沿用同一编号。");
        var args = AgentCatalog.Validate(action, arguments);
        var spec = AgentCatalog.Require(action);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(action + "\n" + args.GetRawText())));
        Work work;
        lock (_sync)
        {
            if (!_accepting) throw new InvalidOperationException("MCP 接入已关闭。");
            var old = _work.Values.FirstOrDefault(w => w.Value.RequestId == requestId);
            if (old is not null)
                return old.Fingerprint == hash ? old.Value : throw new ArgumentException("requestId 已用于不同的操作或参数。");
            if (_work.Count >= 256) throw new InvalidOperationException("本次接入已达 256 个操作；请等待完成后关闭并重新开启接入。");
            if (_work.Values.Count(w => !Terminal(w.Value.State)) >= 16) throw new InvalidOperationException("排队操作已满，请等待现有操作完成。");
            work = new(new(Guid.NewGuid(), requestId, action, "queued", "等待处理。", DateTimeOffset.UtcNow, null), args, hash);
            _work.Add(work.Value.Id, work);
        }
        _ = RunAsync(work, spec);
        return Get(work.Value.Id);
    }

    public AgentOperation Get(Guid id) { lock (_sync) return _work.TryGetValue(id, out var work) ? work.Value : throw new ArgumentException("未找到本次接入的操作。若主程序已重启，请先核对实际状态，不要自动重提写入。"); }
    public bool HasActive { get { lock (_sync) return _work.Values.Any(w => !Terminal(w.Value.State)); } }
    public AgentOperation Cancel(Guid id)
    {
        lock (_sync)
        {
            var w = _work.TryGetValue(id, out var found) ? found : throw new ArgumentException("操作不存在。");
            if (w.Value.State is "queued" or "awaiting_confirmation")
            { w.Cancel.Cancel(); w.Value = w.Value with { State = "cancelled", Message = "已取消，未开始执行。" }; }
            else if (w.Value.State == "running")
                return w.Value with { Message = "操作已开始，不能通过 MCP 强行中断；请等待后端结果或在主程序核对。" };
            return w.Value;
        }
    }

    public void Close()
    {
        lock (_sync)
        {
            _accepting = false;
            foreach (var w in _work.Values.Where(w => w.Value.State is "queued" or "awaiting_confirmation"))
            { w.Cancel.Cancel(); w.Value = w.Value with { State = "cancelled", Message = "接入关闭，未开始执行。" }; }
        }
    }

    private async Task RunAsync(Work work, AgentAction spec)
    {
        var acquired = false;
        try
        {
            await _serial.WaitAsync(work.Cancel.Token); acquired = true;
            if (spec.Mutating)
            {
                Update(work, "awaiting_confirmation", "等待主程序确认，Agent 的参数不代表用户确认。");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(work.Cancel.Token);
                timeout.CancelAfter(TimeSpan.FromMinutes(3));
                if (!await confirm(spec, work.Args, timeout.Token))
                { Update(work, "cancelled", "用户取消或确认超时，未开始执行。"); return; }
            }
            lock (_sync)
            {
                work.Cancel.Token.ThrowIfCancellationRequested();
                if (!_accepting) throw new OperationCanceledException();
                work.Value = work.Value with { State = "running", Message = "正在执行。" };
            }
            Notify();
            var result = await execute(spec, work.Args, new InlineProgress(message => Update(work, "running", message)));
            bool Flag(string key) => result.Data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
            var dataFailed = result.Data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False;
            var state = result.RequiresReview || result.PendingRestore || Flag("outcomeUnknown") ? "requires_review"
                : Flag("isPartial") ? "partial" : dataFailed ? "failed"
                : result.Status == BackendResultStatuses.Ok ? "completed"
                : result.Status == BackendResultStatuses.Partial ? "partial"
                : result.Status == BackendResultStatuses.Cancelled ? "cancelled" : "failed";
            lock (_sync) { work.Value = work.Value with { State = state, Message = result.Message, Result = result }; LastMessage = work.Value.Action + "：" + result.Message; }
            Notify();
        }
        catch (OperationCanceledException)
        {
            var running = Get(work.Value.Id).State == "running";
            Update(work, running && spec.Mutating ? "requires_review" : "cancelled",
                running ? "执行中断，请核对实际状态与未确认记录；未报告成功。" : "操作未开始，已取消。");
        }
        catch (Exception ex)
        {
            // Do not turn an executor exception into a claim that nothing was written.
            Update(work, Get(work.Value.Id).State == "running" && spec.Mutating ? "requires_review" : "failed",
                ex.Message + "；请在设置中核对未确认操作，勿自动重试修改。");
        }
        finally { if (acquired) _serial.Release(); }
    }
    private void Update(Work w, string state, string message)
    { lock (_sync) { if (w.Value.State == "cancelled" && state != "cancelled") return; w.Value = w.Value with { State = state, Message = message }; LastMessage = w.Value.Action + "：" + message; } Notify(); }
    private void Notify() { try { Changed?.Invoke(); } catch { /* Observer failures cannot change an operation's result. */ } }
    private static bool Terminal(string state) => state is "completed" or "failed" or "cancelled" or "partial" or "requires_review";
    private sealed class InlineProgress(Action<string> callback) : IProgress<string> { public void Report(string value) => callback(value); }
}
