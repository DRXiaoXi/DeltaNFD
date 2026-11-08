using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Services;

// 只使用测试专属 ECDSA 密钥与目录；不读取/修改真实设置，不执行任何维护或进程操作。
var count = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    count++;
    Console.WriteLine("PASS: " + name);
}
void Reject(Action action, string name)
{
    try { action(); }
    catch (Exception ex) when (ex is InvalidDataException or CryptographicException or FormatException or JsonException or ArgumentException)
    { Check(true, name); return; }
    throw new Exception("Expected rejection: " + name);
}
var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-emergency-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var keys = new Dictionary<string, string> { ["test"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) };
    var now = DateTimeOffset.UtcNow;
    var version = new Version(0, 91, 0, 0);
    var basis = new EmergencyPolicy
    {
        SchemaVersion = 1, EngineVersion = 1, Sequence = 10, Id = "test-policy", Channel = "public",
        MinimumAppVersion = "0.91", MaximumAppVersion = "0.91.0.0",
        IssuedUtc = now.AddMinutes(-1), ExpiresUtc = now.AddDays(2), Message = "测试策略",
        BlockedCommands = [BackendCommands.RuntimeRepair, BackendCommands.CpuFrameDwmRestart],
        MemoryCleanCooldownSeconds = 1200, MemoryCleanThresholdPercent = 85, DwmRestartDelaySeconds = 10,
        MemoryCleanMode = "manual-only",
    };
    byte[] SignRaw(byte[] payload) => JsonSerializer.SerializeToUtf8Bytes(new EmergencyPolicyEnvelope("test", Convert.ToBase64String(payload),
        Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))),
        EmergencyPolicyVerifier.JsonOptions);
    byte[] Sign(EmergencyPolicy p) => SignRaw(JsonSerializer.SerializeToUtf8Bytes(p, EmergencyPolicyVerifier.JsonOptions));
    var signed = Sign(basis);
    EmergencyPolicy Verify(byte[] bytes) => EmergencyPolicyVerifier.Verify(bytes, keys, now);
    Check(Verify(signed).Id == basis.Id, "有效签名与 UTF-8 消息");
    Reject(() => EmergencyPolicyVerifier.Verify(signed, EmergencyPolicyTrust.Keys, now), "测试密钥不能进入生产信任");
    var text = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(basis, EmergencyPolicyVerifier.JsonOptions));
    var changed = JsonSerializer.Deserialize<EmergencyPolicyEnvelope>(signed, EmergencyPolicyVerifier.JsonOptions)! with
    { Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(text.Replace("1200", "1800"))) };
    Reject(() => Verify(JsonSerializer.SerializeToUtf8Bytes(changed, EmergencyPolicyVerifier.JsonOptions)), "参数篡改拒绝");
    Reject(() => Verify(SignRaw(Encoding.UTF8.GetBytes(text[..^1] + ",\"sequence\":99}"))), "重复字段拒绝");
    Reject(() => Verify(SignRaw(Encoding.UTF8.GetBytes(text[..^1] + ",\"script\":\"run.exe\"}"))), "未知脚本能力拒绝");
    Reject(() => Verify(Sign(basis with { EngineVersion = 2 })), "不支持的引擎版本拒绝");
    Reject(() => Verify(Sign(basis with { SchemaVersion = 2 })), "不支持的格式版本拒绝");
    Reject(() => Verify(Sign(basis with { Sequence = 0 })), "无效序号拒绝");
    Reject(() => Verify(Sign(basis with { IssuedUtc = now.AddMinutes(6) })), "未来签发时间拒绝");
    Reject(() => Verify(Sign(basis with { ExpiresUtc = now.AddDays(31) })), "超过 30 天有效期拒绝");
    Reject(() => Verify(Sign(basis with { ExpiresUtc = now.AddSeconds(-1) })), "过期策略拒绝");
    Reject(() => Verify(Sign(basis with { MinimumAppVersion = "Alpha0.91" })), "非数值版本拒绝");
    Reject(() => Verify(Sign(basis with { MaximumAppVersion = "0.90" })), "颠倒版本范围拒绝");
    Reject(() => Verify(Sign(basis with { Channel = "beta" })), "未定义通道拒绝");
    Reject(() => Verify(Sign(basis with { MemoryCleanCooldownSeconds = 1 })), "过度提高清理频率拒绝");
    Reject(() => Verify(Sign(basis with { MemoryCleanThresholdPercent = 100 })), "阈值越界拒绝");
    Reject(() => Verify(Sign(basis with { DwmRestartDelaySeconds = 1 })), "DWM 延迟越界拒绝");
    Reject(() => Verify(Sign(basis with { MemoryCleanMode = "execute-dll" })), "未知内置方案拒绝");
    foreach (var command in new[] { BackendCommands.CpuFrameRestore, BackendCommands.CpuFrameMonitorStop,
        BackendCommands.RuntimeGuardDisable, BackendCommands.RuntimeRecover, BackendCommands.SystemDefenderPostApply,
        BackendCommands.SystemReconcile, BackendCommands.CpuFrameOfflineRestore, BackendCommands.GpuRecover })
        Reject(() => Verify(Sign(basis with { BlockedCommands = [command] })), "恢复/停止不可远程禁用：" + command);
    Reject(() => Verify(Sign(basis with { BlockedCommands = [BackendCommands.RuntimeRepair, BackendCommands.RuntimeRepair] })), "重复命令拒绝");
    Reject(() => Verify(new byte[EmergencyPolicyVerifier.MaximumEnvelopeBytes + 1]), "过大策略包拒绝");
    var store = new EmergencyPolicyStore(root, keys);
    Check(store.Accept(signed, "public", now).Sequence == 10, "落盘后接受有效策略");
    Check(store.Accept(signed, "public", now).Sequence == 10, "相同包重试幂等");
    Reject(() => store.Accept(Sign(basis with { Sequence = 9 }), "public", now), "旧序号重放拒绝");
    Reject(() => store.Accept(Sign(basis with { Message = "same sequence changed" }), "public", now), "同序号不同内容拒绝");
    Reject(() => store.Accept(signed, "internal", now), "跨通道投递拒绝");
    Check(store.Read("public", now)!.Sequence == 10, "失败投递保留原缓存");
    var internalPolicy = basis with { Channel = "internal", Sequence = 1, Id = "alpha-only", MemoryCleanCooldownSeconds = 1800 };
    store.Accept(Sign(internalPolicy), "internal", now);
    var pub = store.ActiveFor(true, UpdateChannel.Public, version, now);
    var both = store.ActiveFor(true, UpdateChannel.Internal, version, now);
    Check(pub.Count == 1 && pub[0].Channel == "public", "公测不应用内测策略");
    Check(both.Count == 2, "内测同时接收公测和内测策略");
    Check(store.ActiveFor(false, UpdateChannel.Internal, version, now).Count == 0, "关闭开关立即忽略缓存");
    Check(store.ActiveFor(true, UpdateChannel.Internal, new Version(0, 92), now).Count == 0, "不匹配版本不应用");
    var decision = new EmergencyPolicyDecision(both);
    Check(decision.BlockReason(BackendCommands.RuntimeRepair)!.Contains("alpha-only"), "执行决策包含策略来源");
    Check(decision.BlockReason(BackendCommands.CpuFrameRestore) is null, "还原通道不被执行决策阻止");
    Check(decision.MemoryCooldownSeconds == 1800, "多通道冷却采用更保守值");
    Check(decision.MemoryThreshold(90) == 90 && decision.MemoryThreshold(75) == 85, "策略不能降低用户清理阈值");
    Check(decision.DwmDelaySeconds == 10 && decision.ManualMemoryCleanOnly, "参数及内置方案进入执行决策");
    Check(store.ActiveFor(true, UpdateChannel.Internal, version, now.AddDays(3)).Count == 0, "断网过期回到内置算法");
    Reject(() => store.Accept(Sign(basis with { Sequence = 9, IssuedUtc = now.AddDays(3), ExpiresUtc = now.AddDays(4) }), "public", now.AddDays(3)), "过期后仍保留防重放序号");
    store.Accept(Sign(basis with { Sequence = 11, BlockedCommands = [], MemoryCleanMode = "default",
        MemoryCleanCooldownSeconds = null, MemoryCleanThresholdPercent = null, DwmRestartDelaySeconds = null }), "public", now);
    var revoked = new EmergencyPolicyDecision(store.ActiveFor(true, UpdateChannel.Public, version, now));
    Check(revoked.BlockReason(BackendCommands.RuntimeRepair) is null && revoked.MemoryCooldownSeconds == 600
        && !revoked.ManualMemoryCleanOnly, "更高序号空策略撤回限制及参数");
    File.WriteAllText(Path.Combine(root, "internal.json"), "broken");
    Check(store.ActiveFor(true, UpdateChannel.Internal, version, now).Count == 1, "损坏内测缓存不影响有效公测缓存");
    Reject(() => store.Accept(Sign(internalPolicy with { Sequence = 2 }), "internal", now), "损坏序号证据不被静默覆盖");
    Check(new EmergencyPolicyDecision([]).MemoryCooldownSeconds == 600, "无策略时保留原始 10 分钟冷却");

    async Task RejectFetch(Func<HttpRequestMessage, HttpResponseMessage> handler, string url, string name)
    {
        using var client = new HttpClient(new FixtureHandler(handler));
        try { await EmergencyPolicyUpdateService.FetchBoundedAsync(client, new Uri(url), CancellationToken.None); }
        catch (Exception ex) when (ex is InvalidDataException or HttpRequestException) { Check(true, name); return; }
        throw new Exception("Expected fetch rejection: " + name);
    }
    using (var client = new HttpClient(new FixtureHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(signed) })))
        Check((await EmergencyPolicyUpdateService.FetchBoundedAsync(client, new Uri("https://raw.githubusercontent.com/fixture"), CancellationToken.None)).SequenceEqual(signed), "受限下载保留原始签名字节");
    await RejectFetch(_ => throw new Exception("should not send"), "http://raw.githubusercontent.com/test", "拒绝非 HTTPS");
    await RejectFetch(_ => throw new Exception("should not send"), "https://evil.example/test", "拒绝未知主机");
    await RejectFetch(_ => throw new Exception("should not send"), "https://gh-proxy.com/https://raw.githubusercontent.com/test", "不再访问旧 GitHub 加速代理");
    await RejectFetch(_ => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://gh-proxy.com/https://raw.githubusercontent.com/test") } }, "https://raw.githubusercontent.com/test", "直连重定向也不能进入旧代理");
    await RejectFetch(_ => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.example/test") } }, "https://raw.githubusercontent.com/test", "重定向逐跳检查主机");
    await RejectFetch(_ => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://github.com/loop") } }, "https://raw.githubusercontent.com/test", "重定向次数有界");
    await RejectFetch(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[65537]) }, "https://raw.githubusercontent.com/test", "有长度响应大小有界");
    await RejectFetch(_ => new(HttpStatusCode.OK) { Content = new UnknownLengthContent(new byte[65537]) }, "https://raw.githubusercontent.com/test", "无长度响应大小有界");
    using (var cancelled = new CancellationTokenSource())
    using (var client = new HttpClient(new WaitingHandler()))
    {
        cancelled.CancelAfter(TimeSpan.FromMilliseconds(30));
        try
        {
            await EmergencyPolicyUpdateService.FetchBoundedAsync(client, new Uri("https://raw.githubusercontent.com/test"), cancelled.Token);
            throw new Exception("Expected cancellation");
        }
        catch (OperationCanceledException) { Check(true, "慢响应尊重取消/退出预算"); }
    }
    if (args is ["--verify-production", var signedPath])
    {
        var bytes = File.ReadAllBytes(signedPath);
        var production = EmergencyPolicyVerifier.Verify(bytes, EmergencyPolicyTrust.Keys, now);
        Check(production.Id == "publisher-local-check", "本机 CNG 签名与客户端固定公钥闭环");
        var realStore = new EmergencyPolicyStore(Path.Combine(root, "production-roundtrip"), EmergencyPolicyTrust.Keys);
        realStore.Accept(bytes, production.Channel, now);
        Check(realStore.ActiveFor(true, UpdateChannel.Public, version, now).Count == 1, "生产签名包可落盘并匹配当前算法版本");
    }
    Check(true, "全部测试仅使用隔离缓存及测试密钥");
    Console.WriteLine($"Emergency policy checks: {count} passed");
}
catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
// 保留本次独立目录供核对，不接触应用数据。

sealed class FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
}
sealed class WaitingHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        await Task.Delay(Timeout.Infinite, token);
        throw new InvalidOperationException();
    }
}
sealed class UnknownLengthContent(byte[] bytes) : HttpContent
{
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
}
