using System.Text.Json;
using DeltaNFD.Services;

internal static class UpdateChannelChecks
{
    internal static async Task RunAsync()
    {
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-update-channel-" + id.ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        AppSettingsStore.Update(s => s.UpdateServer = UpdateServer.Automatic);
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        var config = UpdateService.LoadConfig(null);
        config.ManifestUrls = [GitHubReleaseSource.ReleasesUrl];
        var service = new UpdateService(config);
        var internalSettings = new AppSettings { UpdateChannel = UpdateChannel.Internal, UpdateServer = UpdateServer.Automatic };
        var publicSettings = new AppSettings { UpdateChannel = UpdateChannel.Public, UpdateServer = UpdateServer.Automatic };
        Check(new AppSettings().UpdateChannel == UpdateChannel.Internal, "旧设置缺省保留内测行为");
        var testInstallPath = Path.Combine(root, "alpha-test-install.json");
        AppSettingsStore.Update(testInstallPath, s => { s.UpdateServer = UpdateServer.GitHub; s.UpdateChannel = UpdateChannel.Public; s.UpdateMode = UpdateMode.FullInstaller; });
        UpdateChannelInstallation.Apply(testInstallPath, new("193-test", "Alpha0.9193", "193-test"));
        Check(AppSettingsStore.Read(testInstallPath) is { UpdateServer: UpdateServer.SelfHosted, UpdateChannel: UpdateChannel.Internal, UpdateMode: UpdateMode.ChangedFiles }, "193 install initializes self-hosted internal changed-files");
        AppSettingsStore.Update(testInstallPath, s => { s.UpdateServer = UpdateServer.GitHub; s.UpdateMode = UpdateMode.FullInstaller; });
        UpdateChannelInstallation.Apply(testInstallPath, new("193-test", "Alpha0.9193", "193-test"));
        Check(AppSettingsStore.Read(testInstallPath).UpdateServer == UpdateServer.GitHub, "same installation preserves subsequent user changes");
        UpdateChannelInstallation.Apply(testInstallPath, new("194-test", "Alpha0.9194", "194-test"));
        Check(AppSettingsStore.Read(testInstallPath) is { UpdateServer: UpdateServer.SelfHosted, UpdateMode: UpdateMode.ChangedFiles }, "194 committed installation reinitializes defaults once");
        var path = Path.Combine(root, "channel-settings.json");
        Check(AppSettings.Save(path, publicSettings), "测试通道设置可写入隔离目录");
        Check(AppSettings.Load(path).UpdateChannel == UpdateChannel.Public,
            "通道设置可持久化往返，JSON=" + File.ReadAllText(path).Contains("\"UpdateChannel\":1"));
        var betaPath = Path.Combine(root, "first-beta.json");
        UpdateChannelInstallation.Apply(betaPath, new("beta-1", "Beta1.0", ""));
        Check(AppSettingsStore.Read(betaPath).UpdateChannel == UpdateChannel.Public, "首次安装 Beta 默认公测");
        AppSettingsStore.Update(betaPath, s => { s.UpdateChannel = UpdateChannel.Internal; s.AutoUpdateCheckEnabled = false; });
        UpdateChannelInstallation.Apply(betaPath, new("beta-2", "Beta1.1", ""));
        Check(AppSettingsStore.Read(betaPath).UpdateChannel == UpdateChannel.Internal && !AppSettingsStore.Read(betaPath).AutoUpdateCheckEnabled,
            "再次安装 Beta 保留内测选择及其他设置");
        AppSettingsStore.Update(betaPath, s => s.UpdateChannel = UpdateChannel.Public);
        UpdateChannelInstallation.Apply(betaPath, new("alpha-1", "Alpha1.2", "alpha-1"));
        Check(AppSettingsStore.Read(betaPath).UpdateChannel == UpdateChannel.Internal, "安装 Alpha 把已有公测切回内测");
        AppSettingsStore.Update(betaPath, s => s.UpdateChannel = UpdateChannel.Public);
        UpdateChannelInstallation.Apply(betaPath, new("alpha-1", "Alpha1.2", "alpha-1"));
        Check(AppSettingsStore.Read(betaPath).UpdateChannel == UpdateChannel.Public, "Alpha 普通重启不覆盖用户后来选择的公测");
        UpdateChannelInstallation.Apply(betaPath, new("beta-3", "Beta1.3", "alpha-1"));
        Check(AppSettingsStore.Read(betaPath).UpdateChannel == UpdateChannel.Public, "再次安装 Beta 保留公测选择");
        UpdateChannelInstallation.Apply(betaPath, new("alpha-2", "Alpha1.2", "alpha-2"));
        Check(AppSettingsStore.Read(betaPath).UpdateChannel == UpdateChannel.Internal, "同版本 Alpha 重新安装也切回内测");
        AppSettingsStore.Update(betaPath, s => s.UpdateChannel = UpdateChannel.Public);
        UpdateChannelInstallation.Apply(betaPath, new("beta-4", "Beta1.4", "alpha-3"));
        Check(AppSettingsStore.Read(betaPath).UpdateChannel == UpdateChannel.Internal,
            "Alpha 安装后未启动就安装 Beta，仍处理待应用的 Alpha 切换");
        var firstAlpha = Path.Combine(root, "first-alpha.json");
        UpdateChannelInstallation.Apply(firstAlpha, new("alpha-new", "Alpha1.0", "alpha-new"));
        Check(AppSettingsStore.Read(firstAlpha).UpdateChannel == UpdateChannel.Internal, "首次 Alpha 安装保持内测");
        var freshBetaAfterOtherUserAlpha = Path.Combine(root, "fresh-beta-after-alpha.json");
        UpdateChannelInstallation.Apply(freshBetaAfterOtherUserAlpha, new("beta-new", "Beta2.0", "old-alpha"));
        Check(AppSettingsStore.Read(freshBetaAfterOtherUserAlpha).UpdateChannel == UpdateChannel.Public,
            "当前用户没有旧设置时 Beta 默认公测，不受机器历史 Alpha 批次影响");
        var corruptSettings = Path.Combine(root, "corrupt-settings.json"); File.WriteAllText(corruptSettings, "{broken");
        var rejected = false;
        try { UpdateChannelInstallation.Apply(corruptSettings, new("alpha-bad", "Alpha1.0", "alpha-bad")); }
        catch (JsonException) { rejected = true; }
        Check(rejected && File.ReadAllText(corruptSettings) == "{broken", "安装规则不覆盖损坏的旧设置");
        var typoSettings = Path.Combine(root, "typo-install.json");
        UpdateChannelInstallation.Apply(typoSettings, new("typo", "AAlpha1.0", ""));
        Check(!File.Exists(typoSettings), "AAlpha 错字不新增安装阶段别名");
        var preAlphaKind = UpdateChannelPolicy.Classify("0.916", "PreAlpha0.916", "stable", false);
        Check(preAlphaKind == UpdateReleaseKind.Local && !UpdateChannelPolicy.Allows(UpdateChannel.Internal, preAlphaKind)
            && !UpdateChannelPolicy.Allows(UpdateChannel.Public, preAlphaKind), "PreAlpha0.916 仅本地构建，两个在线通道均不接收，不能被 stable 字段提升");
        var preAlphaSettings = Path.Combine(root, "prealpha-install.json");
        UpdateChannelInstallation.Apply(preAlphaSettings, new("prealpha-0916", "PreAlpha0.916", "prealpha-0916"));
        Check(AppSettingsStore.Read(preAlphaSettings).UpdateChannel == UpdateChannel.Internal,
            "首次本地 PreAlpha 安装默认内测，不当作线上 Alpha 安装批次");
        AppSettingsStore.Update(preAlphaSettings, settings => settings.UpdateChannel = UpdateChannel.Public);
        UpdateChannelInstallation.Apply(preAlphaSettings, new("prealpha-reinstall", "PreAlpha0.916", ""));
        Check(AppSettingsStore.Read(preAlphaSettings).UpdateChannel == UpdateChannel.Public,
            "已有用户安装本地 PreAlpha 保留其更新通道选择");
        foreach (var prefix in new[] { "Alpha2.0", "alphaV2.0", "Beta2.0", "Release2.0" })
        {
            var kind = UpdateChannelPolicy.Classify("2.0", prefix);
            Check(UpdateChannelPolicy.Allows(UpdateChannel.Internal, kind), "内测接收 " + prefix);
            Check(UpdateChannelPolicy.Allows(UpdateChannel.Public, kind) == !prefix.StartsWith("Alpha", StringComparison.OrdinalIgnoreCase), "公测按前缀过滤 " + prefix);
        }
        Check(!UpdateChannelPolicy.Allows(UpdateChannel.Internal, UpdateChannelPolicy.Classify("2.0", "AAlpha2.0", "alpha")), "不加入用户纠正的 AAlpha 别名");
        Check(!UpdateChannelPolicy.Allows(UpdateChannel.Public, UpdateChannelPolicy.Classify("2.0", "Nightly2.0", "stable")), "未知前缀不因 stable 标记进入公测");
        object Release(string tag, bool prerelease = false, string? name = null, bool valid = true, bool draft = false) => new
        {
            tag_name = tag, name = name ?? tag, prerelease, draft,
            published_at = "2026-10-10T00:00:00Z", html_url = "https://github.com/DRXiaoXi/DeltaNFD/releases/tag/" + tag,
            assets = new[] { new { name = "DeltaNFD_Setup_2.0.0_x64.exe", state = "uploaded", size = 123,
                digest = valid ? "sha256:" + new string('a', 64) : "",
                browser_download_url = "https://github.com/DRXiaoXi/DeltaNFD/releases/download/" + tag + "/DeltaNFD_Setup_2.0.0_x64.exe" } },
        };
        var mixed = JsonSerializer.Serialize(new[] { Release("Release2.0.0"), Release("Alpha9.0.0"), Release("Beta3.0.0", prerelease: true) });
        async Task<UpdateCheckResult> Run(string json, AppSettings settings, bool manual = false, Version? current = null)
            => await service.CheckSourcesAsync(config, settings, current ?? new Version(1, 0), manual, (_, _) => Task.FromResult(json));
        var alpha = await Run(mixed, internalSettings);
        Check(alpha.HasUpdate && alpha.Manifest?.Version == "9.0.0", "内测选择全部阶段中数值最高版本");
        var beta = await Run(mixed, publicSettings);
        Check(beta.HasUpdate && beta.Manifest?.Version == "3.0.0" && beta.Manifest.Channel == "beta", "公测跳过更高 Alpha，接受 prerelease 标记的 Beta");
        var domesticConfig = UpdateService.LoadConfig(null);
        var domesticJson = JsonSerializer.Serialize(new UpdateManifest { SchemaVersion = 1, Version = "3.0.0", DisplayVersion = "Beta3.0.0", Channel = "beta",
            Installers = [new() { Url = "https://deltaxiaoxi.xin/updates/packages/3.0.0/setup.exe", SizeBytes = 123, Sha256 = new string('a', 64) }] });
        var requested = new List<string>();
        async Task<UpdateCheckResult> Domestic(AppSettings settings, string? failUrl = null, bool invalid = false)
        {
            requested.Clear();
            return await service.CheckSourcesAsync(domesticConfig, settings, new Version(1, 0), false, (url, _) =>
            {
                requested.Add(url);
                if (url == failUrl) throw new HttpRequestException("simulated offline");
                return Task.FromResult(url == UpdateService.SelfHostedManifest(settings.UpdateChannel) ? invalid ? "{broken" : domesticJson : invalid ? "{broken" : mixed);
            });
        }
        Check((await Domestic(publicSettings)).Manifest?.Version == "3.0.0" && requested.SequenceEqual(new[] { GitHubReleaseSource.ReleasesUrl }),
            "公测优先 GitHub 且成功后不额外请求自建服务器");
        Check((await Domestic(internalSettings)).Manifest?.Version == "9.0.0" && requested.SequenceEqual(new[] { GitHubReleaseSource.ReleasesUrl }),
            "内测保留发现更高 Alpha 的能力，不被国内 Beta 清单遮挡");
        Check((await Domestic(publicSettings, GitHubReleaseSource.ReleasesUrl)).Manifest?.Version == "3.0.0" && requested.Count == 2,
            "GitHub 获取失败时公测回退自建服务器且仍过滤 Alpha");
        Check((await Domestic(internalSettings, GitHubReleaseSource.ReleasesUrl)).Manifest?.Version == "3.0.0" && requested.Count == 2,
            "GitHub 获取失败时内测可使用国内 Beta");
        Check((await Domestic(publicSettings, invalid: true)).Status == UpdateCheckStatus.ManifestInvalid && requested.Count == 2,
            "两个清单均损坏时明确报错");
        Check(UpdateService.TryReadConfigText("""{"manifestUrls":["https://custom.example/update.json"],"allowedHosts":["custom.example","deltaxiaoxi.xin"]}""", out var customConfig, out _), "旧自定义更新配置仍可解析");
        customConfig = UpdateService.Normalize(customConfig);
        requested.Clear();
        await service.CheckSourcesAsync(customConfig, internalSettings, new Version(1, 0), false, (url, _) => { requested.Add(url); return Task.FromResult(domesticJson); });
        Check(requested.SequenceEqual(new[] { "https://custom.example/update.json" }) && customConfig.InternalManifestUrls.Count == 0,
            "未声明内测专用来源的旧自定义配置保持原顺序，不强插官方站");
        var legacyConfig = UpdateService.LoadConfig(null);
        legacyConfig.ManifestUrls = [UpdateService.OfficialManifestUrl, "https://gh-proxy.com/" + GitHubReleaseSource.ReleasesUrl];
        var legacyService = new UpdateService(legacyConfig);
        Check(legacyService.Config.ManifestUrls!.SequenceEqual(UpdateService.DefaultManifestUrls)
            && legacyService.Config.InternalManifestUrls.SequenceEqual(UpdateService.DefaultInternalManifestUrls),
            "旧国内优先/加速配置自动迁移为两个通道 GitHub 优先");
        Check((await Run(mixed, publicSettings, manual: true)).Manifest?.Version == "3.0.0", "手动检查也服从所选通道");
        Check(!(await Run(mixed, publicSettings, current: new Version(4, 0))).HasUpdate, "切换通道不降级安装较旧公测版");
        var onlyAlpha = JsonSerializer.Serialize(new[] { Release("Alpha9.0.0", valid: false) });
        Check((await Run(onlyAlpha, publicSettings)).Status == UpdateCheckStatus.UpToDate, "仅有 Alpha 时公测正常显示暂无更新，不误报资源错误");
        Check((await Run("[]", publicSettings)).Status == UpdateCheckStatus.UpToDate, "空发布列表正常显示暂无更新");
        var invalidPublic = JsonSerializer.Serialize(new[] { Release("Alpha9.0.0"), Release("Beta3.0.0", valid: false), Release("Release2.0.0") });
        Check((await Run(invalidPublic, publicSettings)).Status == UpdateCheckStatus.ManifestInvalid, "通道内最高版本校验失败时不拿旧版替代");
        var named = JsonSerializer.Serialize(new[] { Release("v8.0.0", name: "Beta8.0.0", prerelease: true), Release("Alpha9.0.0") });
        Check((await Run(named, publicSettings)).Manifest?.Version == "8.0.0", "数字 tag 从发布名称识别 Beta");
        var conflicting = JsonSerializer.Serialize(new[] { Release("Alpha9.0.0", name: "Release9.0.0"), Release("Beta3.0.0") });
        Check((await Run(conflicting, publicSettings)).Manifest?.Version == "3.0.0", "Alpha tag 不能被 Release 标题伪装进入公测");
        var drafts = JsonSerializer.Serialize(new[] { Release("Beta99.0.0", draft: true), Release("Release2.0.0") });
        Check((await Run(drafts, publicSettings)).Manifest?.Version == "2.0.0", "草稿发布仍被过滤");
        var alphaManifest = new UpdateManifest { Version = "99.0.0", DisplayVersion = "Alpha99.0.0", Mandatory = true };
        Check(!service.Evaluate(alphaManifest, publicSettings, new Version(1, 0)).HasUpdate, "mandatory 不能绕过公测通道");
        Check(service.Evaluate(alphaManifest, internalSettings, new Version(1, 0)).HasUpdate, "内测仍接收 Alpha 强制更新");
        AppSettingsStore.Update(s => s.UpdateChannel = UpdateChannel.Public);
        Check(!(await service.DownloadAsync(alphaManifest)).Success, "旧 Alpha 提示无法绕过通道继续下载，无网络访问");
        Check(!(await service.LaunchInstallerAsync(alphaManifest, "missing.exe")).Success, "旧 Alpha 安装请求被通道门禁拒绝，无安装进程");

        AppSettingsStore.Update(s => s.UpdateChannel = UpdateChannel.Internal);
        var pending = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = service.CheckAndPublishAsync(internalSettings, () => pending.Task);
        AppSettingsStore.Update(s => s.UpdateChannel = UpdateChannel.Public);
        service.ResetCheckResult();
        var notified = new List<bool>(); service.AvailabilityChanged += notified.Add;
        pending.SetResult(new() { Status = UpdateCheckStatus.UpdateAvailable, Manifest = alphaManifest });
        Check(!(await checks).HasUpdate && service.LastResult?.HasUpdate != true && !notified.Contains(true), "通道切换后迟到的 Alpha 检查不能重新生成提示或徽标");
        var current = await service.CheckAndPublishAsync(publicSettings, () => Task.FromResult(beta));
        Check(current.HasUpdate && service.LastResult?.Manifest?.Channel == "beta" && notified.Last(), "新通道检查结果正常缓存并广播");
        service.ResetCheckResult();
        Check(service.LastResult?.Manifest is null && !notified.Last(), "切换清除旧清单和更新徽标");
        Console.WriteLine($"更新通道隔离检查通过：{count} 项；不联网、不下载、不安装。");
        UpdateChecks.Run(includeGameCompatibility: false);
    }
}
