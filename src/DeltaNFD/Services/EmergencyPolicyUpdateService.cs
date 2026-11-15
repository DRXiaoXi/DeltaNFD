using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace DeltaNFD.Services;

/// <summary>签名策略与整包安装分开交付，复用用户所选通道；只在前端运行期间轮询。</summary>
internal static class EmergencyPolicyUpdateService
{
    private const string SourceRoot = "https://raw.githubusercontent.com/DRXiaoXi/DeltaNFD/main/emergency/";
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = EmergencyPolicyVerifier.MaximumEnvelopeBytes };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly CancellationTokenSource Lifetime = new();
    private static int _started;
    private static string _status = "尚未检查紧急策略。";
    public static string Status => _status + " " + EmergencyPolicies.Status;
    public static event Action? Changed;

    public static void Start(bool installed)
    {
        if (!installed || Interlocked.Exchange(ref _started, 1) != 0) return;
        _ = LoopAsync();
    }

    public static void Stop() => Lifetime.Cancel();

    // 整包检查不等待策略源超时；两种交付的结果与失败互不覆盖。
    public static void RequestCheck(bool installed) => _ = RequestCheckAsync(installed);

    private static async Task RequestCheckAsync(bool installed)
    {
        try { await CheckAsync(installed).ConfigureAwait(false); }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (Exception ex) { SetStatus("紧急策略检查未完成：" + ex.Message); }
    }

    private static async Task LoopAsync()
    {
        try
        {
            while (!Lifetime.IsCancellationRequested)
            {
                await CheckAsync(installed: true, Lifetime.Token).ConfigureAwait(false);
                // 只刷新状态，不额外联网：已打开的设置页也能显示过期/通道变化后的实际状态。
                for (var minute = 0; minute < 15; minute++)
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), Lifetime.Token).ConfigureAwait(false);
                    NotifyChanged();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn("紧急策略轮询停止：" + ex.Message); }
    }

    public static async Task CheckAsync(bool installed, CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, Lifetime.Token);
        if (!await Gate.WaitAsync(0, linked.Token).ConfigureAwait(false)) return;
        try
        {
            if (!installed) { SetStatus("当前为开发或解压运行实例，未接收远端紧急策略。"); return; }
            var settings = AppSettingsStore.Read();
            if (!settings.EmergencyPolicyEnabled) { SetStatus("紧急策略接收已关闭。"); return; }
            SetStatus("正在检查紧急策略…");
            var channels = settings.UpdateChannel == UpdateChannel.Internal ? new[] { "public", "internal" } : new[] { "public" };
            var results = new List<string>();
            foreach (var channel in channels)
            {
                try
                {
                    var bytes = await FetchAsync(SourceRoot + channel + ".json", linked.Token).ConfigureAwait(false);
                    var current = AppSettingsStore.Read();
                    if (!current.EmergencyPolicyEnabled || current.UpdateChannel != settings.UpdateChannel) return;
                    var policy = EmergencyPolicies.Store.Accept(bytes, channel, DateTimeOffset.UtcNow);
                    results.Add($"{channel} #{policy.Sequence} 已校验");
                    Log.Info($"紧急策略：{channel} #{policy.Sequence} {policy.Id} 已接收。");
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    results.Add(channel + " 未更新（" + ex.Message + "）");
                    Log.Warn("紧急策略：" + channel + " 检查未完成，继续使用有效缓存或内置算法：" + ex.Message);
                }
            }
            SetStatus("检查时间 " + DateTime.Now.ToString("HH:mm:ss") + "：" + string.Join("；", results));
        }
        finally { Gate.Release(); }
    }

    internal static async Task<byte[]> FetchBoundedAsync(HttpClient client, Uri source, CancellationToken token)
    {
        // 不依赖整包更新的可编辑 allowedHosts；签名是内容信任根，固定主机限制传输边界。
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            if (!Allowed(source)) throw new InvalidDataException("紧急策略地址不受支持。");
            using var request = new HttpRequestMessage(HttpMethod.Get, source);
            request.Headers.UserAgent.ParseAdd("DeltaNFD-EmergencyPolicy/1.0");
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.PermanentRedirect or HttpStatusCode.SeeOther)
            {
                source = response.Headers.Location is { } location ? new Uri(source, location)
                    : throw new InvalidDataException("策略重定向缺少地址。");
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > EmergencyPolicyVerifier.MaximumEnvelopeBytes)
                throw new InvalidDataException("策略响应过大。");
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                var count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
                if (count == 0) return output.ToArray();
                if (output.Length + count > EmergencyPolicyVerifier.MaximumEnvelopeBytes)
                    throw new InvalidDataException("策略响应超过大小限制。");
                output.Write(buffer, 0, count);
            }
        }
        throw new InvalidDataException("策略重定向次数过多。");
    }

    private static bool Allowed(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort
        && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && uri.Host is "raw.githubusercontent.com" or "github.com" or "objects.githubusercontent.com"
            or "release-assets.githubusercontent.com";

    private static async Task<byte[]> FetchAsync(string url, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        return await FetchBoundedAsync(Client, new Uri(url), deadline.Token).ConfigureAwait(false);
    }

    private static void SetStatus(string message)
    {
        _status = message;
        NotifyChanged();
    }

    private static void NotifyChanged()
    {
        try { Changed?.Invoke(); } catch (Exception ex) { Log.Warn("紧急策略界面刷新失败：" + ex.Message); }
    }
}
