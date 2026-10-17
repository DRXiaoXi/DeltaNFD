using System.Net;
using System.Text;
using System.Text.Json;
using DeltaNFD.Services;

internal static class UpdateResponseChecks
{
    internal static async Task RunAsync(string? capturedResponse = null)
    {
        var checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        var config = UpdateService.LoadConfig(null);
        var api = GitHubReleaseSource.ReleasesUrl;
        var installer = new { url = "https://deltaxiaoxi.xin/updates/packages/9.0/setup.exe", sizeBytes = 1, sha256 = new string('a', 64) };
        var compact = JsonSerializer.Serialize(new { schemaVersion = 1, version = "9.0", displayVersion = "Beta9.0", channel = "beta", installers = new[] { installer } });
        var large = JsonSerializer.Serialize(new[] { new { tag_name = "v9.0", name = "Beta9.0", draft = false, prerelease = true,
            body = new string('x', 1_586_796), html_url = "https://github.com/DRXiaoXi/DeltaNFD/releases/tag/v9.0",
            assets = new[] { new { name = "DeltaNFD_Setup_9.0_x64.exe", state = "uploaded", size = 1,
                digest = "sha256:" + new string('a', 64), browser_download_url = "https://github.com/DRXiaoXi/DeltaNFD/releases/download/v9.0/setup.exe" } } } });

        async Task<string> Fetch(string url, byte[] data, bool knownLength = false, CancellationToken token = default)
        {
            using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK)
                { Content = knownLength ? new ByteArrayContent(data) : new StreamContent(new CountingStream(data)) }));
            return await UpdateService.FetchStringAsync(url, config, token, client);
        }
        async Task Reject(Func<Task> action, string message)
        {
            try { await action(); throw new Exception("Expected rejection: " + message); }
            catch (InvalidDataException) { checks++; }
        }

        foreach (var knownLength in new[] { false, true })
        {
            var text = await Fetch(api, Encoding.UTF8.GetBytes(large), knownLength);
            Check(text == large, "Large GitHub body is read through the production HTTP path");
            Check(UpdateService.TryParseUpdateSource(text, config, out var manifest, out _, UpdateChannel.Public)
                && manifest?.Version == "9.0.0", "Large release response selects the expected installer");
            await Reject(() => Fetch(UpdateService.OfficialManifestUrl, Encoding.UTF8.GetBytes(large), knownLength),
                "Ordinary manifests retain their small limit");
        }
        var multibyte = new string('\u6d4b', 200_000);
        Check(await Fetch(UpdateService.OfficialManifestUrl, Encoding.UTF8.GetBytes(multibyte)) == multibyte,
            "The original character limit still accepts multibyte UTF-8 within its byte bound");
        await Reject(() => Fetch(UpdateService.OfficialManifestUrl, Encoding.UTF8.GetBytes(new string('a', 262_145))),
            "Ordinary manifests keep the 262144-character bound");
        await Reject(() => Fetch(api, []), "Empty release responses are rejected");
        Check(await Fetch(api, Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("[]")).ToArray()) == "[]", "UTF-8 BOM remains supported");
        try { await Fetch(api, [0xff, 0xff]); throw new Exception("Invalid UTF-8 was accepted"); }
        catch (DecoderFallbackException) { checks++; }

        foreach (var knownLength in new[] { false, true })
        {
            var stream = new CountingStream(new byte[UpdateService.MaxGitHubResponseBytes + 32768]);
            using var client = new HttpClient(new Handler(_ =>
            {
                var content = new StreamContent(stream);
                if (knownLength) content.Headers.ContentLength = UpdateService.MaxGitHubResponseBytes + 32768;
                return new(HttpStatusCode.OK) { Content = content };
            }));
            await Reject(() => UpdateService.FetchStringAsync(api, config, default, client), "Oversized responses stop while streaming");
            Check(stream.BytesRead == (knownLength ? 0 : UpdateService.MaxGitHubResponseBytes + 1),
                "Known length is rejected before reading; unknown length reads at most limit plus one");
            Check(stream.Disposed, "Rejected response stream is disposed");
        }

        foreach (var channel in new[] { UpdateChannel.Public, UpdateChannel.Internal })
        {
            var seen = new List<string>();
            using var client = new HttpClient(new Handler(request =>
            {
                var url = request.RequestUri!.AbsoluteUri; seen.Add(url);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(url == api
                    ? new byte[UpdateService.MaxGitHubResponseBytes + 1] : Encoding.UTF8.GetBytes(compact)) };
            }));
            var service = new UpdateService(config, client);
            var result = await service.CheckSourcesAsync(config,
                new AppSettings { UpdateChannel = channel, UpdateServer = UpdateServer.Automatic }, new Version(0, 9192), true,
                (url, token) => UpdateService.FetchUpdateSourceAsync(url, config, token,
                    (address, cfg, ct) => UpdateService.FetchStringAsync(address, cfg, ct, client)));
            Check(result.HasUpdate && seen.SequenceEqual([api, UpdateService.SelfHostedManifest(channel)]),
                "Automatic mode falls back after a bounded HTTP rejection: " + channel);
            seen.Clear();
            result = await service.CheckSourcesAsync(config,
                new AppSettings { UpdateChannel = channel, UpdateServer = UpdateServer.GitHub }, new Version(0, 9192), true,
                (url, token) => UpdateService.FetchStringAsync(url, config, token, client, UpdateServer.GitHub));
            Check(!result.HasUpdate && seen.SequenceEqual([api]), "Explicit GitHub selection never silently crosses servers");
        }

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await Fetch(api, Encoding.UTF8.GetBytes(large), token: cancelled.Token); throw new Exception("Cancellation was ignored"); }
            catch (OperationCanceledException) { checks++; }
        }
        var page = JsonSerializer.Serialize(Enumerable.Repeat(new { tag_name = "v9.0", body = new string('x', 70_000) }, 100));
        var pageCount = 0;
        await Reject(() => UpdateService.FetchUpdateSourceAsync(api, config, default,
            (_, _, _) => { pageCount++; return Task.FromResult(page); }), "Aggregate pagination budget is enforced");
        Check(pageCount == 3, "Pagination stops as soon as the cumulative budget is exceeded");
        if (capturedResponse is not null)
        {
            var captured = await Fetch(api, File.ReadAllBytes(capturedResponse));
            Check(captured.Length > 262144, "Captured live response reproduces the old size failure");
            Check(UpdateService.TryParseUpdateSource(captured, config, out var actual, out _, UpdateChannel.Public)
                && actual?.Version == "0.9200.0", "Captured live GitHub response passes production HTTP reading and selects Beta0.92");
        }
        Console.WriteLine($"Update response checks: {checks} passed. Mock HTTP / captured data only; no installer execution.");
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var response = respond(request); response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class CountingStream(byte[] data) : Stream
    {
        public int BytesRead { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, data.Length - BytesRead);
            data.AsSpan(BytesRead, read).CopyTo(buffer.AsSpan(offset)); BytesRead += read; return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var read = Math.Min(buffer.Length, data.Length - BytesRead);
            data.AsMemory(BytesRead, read).CopyTo(buffer); BytesRead += read; return ValueTask.FromResult(read);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long length) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
