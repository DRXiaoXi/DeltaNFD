using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeltaNFD.Services;

internal static class UpdateSourceFallbackChecks
{
    private const string GitHubAsset = "https://github.com/DRXiaoXi/DeltaNFD/releases/download/v9.0/setup.exe";
    private const string ServerAsset = "https://deltaxiaoxi.xin/updates/packages/9.0/setup.exe";
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("synthetic installer; never executed");
    private static readonly string Hash = Convert.ToHexString(SHA256.HashData(Payload));

    internal static async Task RunAsync()
    {
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-update-fallback-" + id.ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        AppSettingsStore.Update(s => { s.UpdateChannel = UpdateChannel.Public; s.UpdateMode = UpdateMode.FullInstaller; });
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }

        UpdateInstallerInfo Installer(string url) => new() { Url = url, SizeBytes = Payload.Length, Sha256 = Hash };
        UpdateManifest Manifest(params string[] urls) => new()
        {
            Version = "9.0", DisplayVersion = "Beta9.0", Channel = "beta",
            Installers = urls.Select(Installer).ToList(),
        };
        HttpResponseMessage Bytes(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
        HttpResponseMessage Json(UpdateManifest manifest) => Bytes(JsonSerializer.SerializeToUtf8Bytes(manifest));
        async Task<(UpdateDownloadResult Result, List<string> Seen)> Download(UpdateManifest manifest,
            Func<string, CancellationToken, HttpResponseMessage> response, CancellationToken token = default,
            UpdateServer server = UpdateServer.Automatic)
        {
            AppSettingsStore.Update(settings => settings.UpdateServer = server);
            var seen = new List<string>();
            using var client = new HttpClient(new FakeHandler((request, ct) =>
            {
                var url = request.RequestUri!.AbsoluteUri;
                seen.Add(url);
                return response(url, ct);
            }));
            var service = new UpdateService(UpdateService.LoadConfig(null), client, Path.Combine(root, Guid.NewGuid().ToString("N")));
            return (await service.DownloadAsync(manifest, cancellationToken: token), seen);
        }

        var success = await Download(Manifest(GitHubAsset), (_, _) => Bytes(Payload));
        Check(success.Result.Success && success.Seen.SequenceEqual([GitHubAsset]), "GitHub 成功时不读取服务器清单或安装包");
        Check(File.ReadAllBytes(success.Result.InstallerPath).SequenceEqual(Payload), "直连下载写入通过哈希校验的内容");

        foreach (var failure in new[] { "404", "network", "timeout", "hash" })
        {
            var fallback = await Download(Manifest(GitHubAsset), (url, _) =>
            {
                if (url == UpdateService.OfficialManifestUrl) return Json(Manifest(ServerAsset));
                if (url == ServerAsset) return Bytes(Payload);
                return failure switch
                {
                    "404" => new(HttpStatusCode.NotFound),
                    "network" => throw new HttpRequestException("simulated connection failure"),
                    "timeout" => throw new TaskCanceledException("simulated idle timeout"),
                    _ => Bytes(Enumerable.Repeat((byte)0, Payload.Length).ToArray()),
                };
            });
            Check(fallback.Result.Success && fallback.Seen.SequenceEqual([GitHubAsset, UpdateService.OfficialManifestUrl, ServerAsset]),
                failure + " 后才获取自建清单并下载同一安装包");
            Check(File.ReadAllBytes(fallback.Result.InstallerPath).SequenceEqual(Payload)
                && !File.Exists(fallback.Result.InstallerPath + ".part"), "回退成功无坏包或临时文件残留：" + failure);
        }

        var reordered = await Download(Manifest(ServerAsset, GitHubAsset), (_, _) => Bytes(Payload));
        Check(reordered.Result.Success && reordered.Seen.SequenceEqual([GitHubAsset]), "旧清单服务器在前也优先从 GitHub 下载");
        var existingFallback = await Download(Manifest(ServerAsset, GitHubAsset), (url, _) =>
            url == GitHubAsset ? new(HttpStatusCode.BadGateway) : Bytes(Payload));
        Check(existingFallback.Result.Success && existingFallback.Seen.SequenceEqual([GitHubAsset, ServerAsset]),
            "已有同身份备用地址时直接回退，不重复获取清单");

        foreach (var mismatch in new[] { "older", "newer", "channel", "hash", "size", "host", "broken", "offline" })
        {
            var invalid = Manifest(ServerAsset);
            switch (mismatch)
            {
                case "older": invalid.Version = "8.0"; invalid.DisplayVersion = "Beta8.0"; break;
                case "newer": invalid.Version = "10.0"; invalid.DisplayVersion = "Beta10.0"; break;
                case "channel": invalid.Channel = "alpha"; invalid.DisplayVersion = "Alpha9.0"; break;
                case "hash": invalid.Installers![0].Sha256 = new string('a', 64); break;
                case "size": invalid.Installers![0].SizeBytes++; break;
                case "host": invalid.Installers![0].Url = "https://example.invalid/setup.exe"; break;
            }
            var rejected = await Download(Manifest(GitHubAsset), (url, _) =>
            {
                if (url != UpdateService.OfficialManifestUrl) return new(HttpStatusCode.NotFound);
                return mismatch switch
                {
                    "offline" => throw new HttpRequestException("server offline"),
                    "broken" => Bytes(Encoding.UTF8.GetBytes("{broken")),
                    _ => Json(invalid),
                };
            });
            Check(!rejected.Result.Success && rejected.Seen.SequenceEqual([GitHubAsset, UpdateService.OfficialManifestUrl]),
                "拒绝不匹配或不可用的备用清单：" + mismatch);
            Check(string.IsNullOrEmpty(rejected.Result.InstallerPath), "失败不返回可执行安装包路径：" + mismatch);
        }

        var badServer = await Download(Manifest(GitHubAsset), (url, _) => url == GitHubAsset ? new(HttpStatusCode.NotFound)
            : url == UpdateService.OfficialManifestUrl ? Json(Manifest(ServerAsset))
            : Bytes(Enumerable.Repeat((byte)0, Payload.Length).ToArray()));
        Check(!badServer.Result.Success, "服务器包内容损坏仍拒绝，不因回退跳过哈希");

        using var cts = new CancellationTokenSource();
        var cancellationSeen = new List<string>();
        var cancelled = false;
        try
        {
            await Download(Manifest(GitHubAsset), (url, _) =>
            {
                cancellationSeen.Add(url);
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }, cts.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && cancellationSeen.SequenceEqual([GitHubAsset]), "用户取消立即结束，不回退自建服务器");

        var migrated = await Download(Manifest("https://gh-proxy.com/" + GitHubAsset), (_, _) => Bytes(Payload));
        Check(migrated.Result.Success && migrated.Seen.SequenceEqual([GitHubAsset]), "旧安装包代理地址迁移直连");
        var config = UpdateService.LoadConfig(null);
        var serviceForChecks = new UpdateService(config);
        foreach (var channel in new[] { UpdateChannel.Public, UpdateChannel.Internal })
        {
            var requested = new List<string>();
            var result = await serviceForChecks.CheckSourcesAsync(config, new AppSettings { UpdateChannel = channel, UpdateServer = UpdateServer.Automatic }, new Version(1, 0), true,
                (url, _) =>
                {
                    requested.Add(url);
                    if (url == GitHubReleaseSource.ReleasesUrl) throw new HttpRequestException("offline");
                    return Task.FromResult(JsonSerializer.Serialize(Manifest(ServerAsset)));
                });
            Check(result.HasUpdate && requested.SequenceEqual([GitHubReleaseSource.ReleasesUrl, UpdateService.SelfHostedManifest(channel)]),
                "检查更新先 GitHub 失败后自建服务器：" + channel);
            requested.Clear();
            var invalidGithub = await serviceForChecks.CheckSourcesAsync(config, new AppSettings { UpdateChannel = channel, UpdateServer = UpdateServer.Automatic }, new Version(1, 0), true,
                (url, _) => { requested.Add(url); return Task.FromResult(url == GitHubReleaseSource.ReleasesUrl ? "{broken" : JsonSerializer.Serialize(Manifest(ServerAsset))); });
            Check(invalidGithub.HasUpdate && requested.Count == 2, "GitHub 清单无效时尝试有效备用清单：" + channel);
            requested.Clear();
            var noRelease = await serviceForChecks.CheckSourcesAsync(config, new AppSettings { UpdateChannel = channel, UpdateServer = UpdateServer.Automatic }, new Version(1, 0), true,
                (url, _) => { requested.Add(url); return Task.FromResult("[]"); });
            Check(noRelease.Status == UpdateCheckStatus.UpToDate && requested.Count == 1, "有效暂无更新不额外访问备用站：" + channel);
        }
        Check(new AppSettings().UpdateServer == UpdateServer.SelfHosted, "未配置服务器默认自建");
        Check(UpdateServerPolicy.Normalize((UpdateServer)99) == UpdateServer.SelfHosted, "未知服务器值回退自建服务器");
        var settingsPath = Path.Combine(root, "server-settings.json");
        foreach (var server in new[] { UpdateServer.Automatic, UpdateServer.GitHub, UpdateServer.SelfHosted })
        {
            Check(AppSettings.Save(settingsPath, new AppSettings { UpdateServer = server })
                && AppSettings.Load(settingsPath).UpdateServer == server, "服务器选择持久化：" + server);
            foreach (var channel in new[] { UpdateChannel.Public, UpdateChannel.Internal })
            {
                var requested = new List<string>();
                var chosen = await serviceForChecks.CheckSourcesAsync(config,
                    new AppSettings { UpdateChannel = channel, UpdateServer = server }, new Version(1, 0), true,
                    (url, _) =>
                    {
                        requested.Add(url);
                        return Task.FromResult(JsonSerializer.Serialize(Manifest(ServerAsset, GitHubAsset)));
                    });
                var expectedSource = server == UpdateServer.SelfHosted ? UpdateService.SelfHostedManifest(channel) : GitHubReleaseSource.ReleasesUrl;
                Check(chosen.HasUpdate && requested.SequenceEqual([expectedSource]), "检查使用所选来源：" + server + "/" + channel);
                Check(chosen.Manifest!.Installers!.All(i => UpdateServerPolicy.Allows(server, i.Url)), "检查结果只含所选服务器地址：" + server + "/" + channel);
            }
        }
        foreach (var server in new[] { UpdateServer.GitHub, UpdateServer.SelfHosted })
        {
            var expected = server == UpdateServer.GitHub ? GitHubAsset : ServerAsset;
            var selected = await Download(Manifest(ServerAsset, GitHubAsset), (_, _) => Bytes(Payload), server: server);
            Check(selected.Result.Success && selected.Seen.SequenceEqual([expected]), "下载仅访问选定服务器：" + server);
            var offline = await Download(Manifest(ServerAsset, GitHubAsset), (_, _) => new(HttpStatusCode.BadGateway), server: server);
            Check(!offline.Result.Success && offline.Seen.SequenceEqual([expected]), "所选服务器失败不自动换站：" + server);
            var wrongHostManifest = Manifest(server == UpdateServer.GitHub ? ServerAsset : GitHubAsset);
            var noMatchingInstaller = await Download(wrongHostManifest, (_, _) => throw new Exception("should not send"), server: server);
            Check(!noMatchingInstaller.Result.Success && noMatchingInstaller.Seen.Count == 0, "旧清单不能跨选定服务器下载：" + server);
            var requested = new List<string>();
            var failed = await serviceForChecks.CheckSourcesAsync(config,
                new AppSettings { UpdateServer = server, UpdateChannel = UpdateChannel.Public }, new Version(1, 0), true,
                (url, _) => { requested.Add(url); throw new HttpRequestException("offline"); });
            Check(failed.Status == UpdateCheckStatus.NetworkError && requested.Count == 1, "所选检查失败不切换清单源：" + server);
            var wrongInstaller = await serviceForChecks.CheckSourcesAsync(config,
                new AppSettings { UpdateServer = server, UpdateChannel = UpdateChannel.Public }, new Version(1, 0), true,
                (_, _) => Task.FromResult(JsonSerializer.Serialize(wrongHostManifest)));
            Check(wrongInstaller.Status == UpdateCheckStatus.ManifestInvalid, "所选站点无对应下载地址时明确提示：" + server);
        }
        var redirected = await Download(Manifest(GitHubAsset), (url, _) => url == GitHubAsset
            ? new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://release-assets.githubusercontent.com/synthetic.exe") } }
            : Bytes(Payload), server: UpdateServer.GitHub);
        Check(redirected.Result.Success && redirected.Seen.Count == 2, "GitHub 模式允许官方安装包 CDN 重定向");
        foreach (var server in new[] { UpdateServer.GitHub, UpdateServer.SelfHosted })
        {
            var destination = server == UpdateServer.GitHub ? ServerAsset : GitHubAsset;
            var forbiddenRedirect = await Download(Manifest(GitHubAsset, ServerAsset), (_, _) =>
                new(HttpStatusCode.Redirect) { Headers = { Location = new Uri(destination) } }, server: server);
            Check(!forbiddenRedirect.Result.Success && forbiddenRedirect.Seen.Count == 1, "重定向不得跨所选服务器：" + server);
        }
        var loop = await Download(Manifest(GitHubAsset), (_, _) =>
            new(HttpStatusCode.Redirect) { Headers = { Location = new Uri(GitHubAsset) } }, server: UpdateServer.GitHub);
        Check(!loop.Result.Success && loop.Seen.Count == 6, "更新下载重定向次数有界");
        var proxyRedirect = await Download(Manifest(GitHubAsset), (_, _) => new(HttpStatusCode.Redirect)
            { Headers = { Location = new Uri("https://gh-proxy.com/" + GitHubAsset) } }, server: UpdateServer.GitHub);
        Check(!proxyRedirect.Result.Success && proxyRedirect.Seen.Count == 1, "服务器选择不能恢复旧加速代理");

        AppSettingsStore.Update(settings => { settings.UpdateServer = UpdateServer.GitHub; settings.UpdateChannel = UpdateChannel.Public; });
        serviceForChecks.ResetCheckResult();
        var notifications = new List<bool>();
        serviceForChecks.AvailabilityChanged += notifications.Add;
        var delayed = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldCheck = serviceForChecks.CheckAndPublishAsync(AppSettingsStore.Read(), () => delayed.Task);
        AppSettingsStore.Update(settings => settings.UpdateServer = UpdateServer.SelfHosted);
        delayed.SetResult(new() { Status = UpdateCheckStatus.UpdateAvailable, Manifest = Manifest(GitHubAsset) });
        Check(!(await oldCheck).HasUpdate && !notifications.Contains(true), "切换服务器后迟到的检查不能发布旧提示");
        var freshCheck = await serviceForChecks.CheckAndPublishAsync(AppSettingsStore.Read(), () => Task.FromResult(
            new UpdateCheckResult { Status = UpdateCheckStatus.UpdateAvailable, Manifest = Manifest(ServerAsset) }));
        Check(freshCheck.HasUpdate && notifications.Last(), "新服务器的检查正常发布结果");
        serviceForChecks.ResetCheckResult();
        Check(serviceForChecks.LastResult?.Manifest is null && !notifications.Last(), "切换清除旧清单和徽标");
        AppSettingsStore.Update(settings => settings.UpdateServer = UpdateServer.Automatic);
        Console.WriteLine($"Update source fallback checks: {checks} passed. Synthetic HTTP and files only; no network or installer execution.");
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(response(request, token));
    }
}
