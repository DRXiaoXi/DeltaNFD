using System.Text.Json;
using DeltaNFD.Services;

/// <summary>
/// 自动更新的离线校验（--update-checks）：只跑纯逻辑与临时目录，不联网、不装任何东西。
/// 覆盖清单解析与安全护栏、版本比较、SHA256 校验、安装位置判定、节流与跳过语义。
/// </summary>
internal static class UpdateChecks
{
    private static readonly List<string> Failures = [];

    public static void Run()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "DeltaNFD-updatechecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            Console.WriteLine("=== 自动更新离线校验 ===");
            RunVersionChecks();
            RunUrlAndHostChecks();
            RunManifestChecks();
            RunReleaseChecks();
            RunFileChecks(tempRoot);
            RunLaunchSafetyChecks(tempRoot);
            RunInstallLocationChecks(tempRoot);
            RunThrottleChecks();
            RunSemanticsChecks();
            RunConfigChecks(tempRoot);
            RunEffectiveConfigChecks(tempRoot);
            RunEnvironmentReport();
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* 清理失败不影响结论 */ }
        }

        Console.WriteLine();
        if (Failures.Count > 0)
        {
            Console.WriteLine($"自动更新校验失败：{Failures.Count} 项");
            foreach (var failure in Failures)
            {
                Console.WriteLine("  · " + failure);
            }

            Environment.ExitCode = 1;
            return;
        }

        Console.WriteLine("自动更新校验全部通过。");
    }

    private static void RunVersionChecks()
    {
        Section("版本解析与比较");

        Check(UpdateService.TryParseVersion("0.83.0", out var v1) && v1 == new Version(0, 83, 0),
            "解析 0.83.0");
        Check(UpdateService.TryParseVersion("OpenAlphaV0.83", out var v2) && v2.Major == 0 && v2.Minor == 83,
            "解析 OpenAlphaV0.83（带品牌前缀）");
        Check(UpdateService.TryParseVersion("v0.82.1", out var v3) && v3.Build == 1,
            "解析 v0.82.1");
        Check(!UpdateService.TryParseVersion("", out _), "空版本号判非法");
        Check(!UpdateService.TryParseVersion("abc", out _), "纯字母版本号判非法");

        Check(UpdateService.CompareVersions(new Version(0, 83, 0), new Version(0, 82, 0)) > 0, "0.83.0 > 0.82.0");
        Check(UpdateService.CompareVersions(new Version(0, 9, 0), new Version(0, 10, 0)) < 0, "0.9.0 < 0.10.0（按数值不是按字符串）");
        Check(UpdateService.CompareVersions(new Version(0, 82), new Version(0, 82, 0, 0)) == 0, "0.82 与 0.82.0.0 视为同版本");
        Check(UpdateService.CompareVersions(new Version(0, 82, 0, 1), new Version(0, 82, 0)) > 0, "0.82.0.1 > 0.82.0");

        Check(UpdateService.IsBelowMinimum(new Version(0, 78, 0), "0.79.0"), "低于最低支持版本被识别");
        Check(!UpdateService.IsBelowMinimum(new Version(0, 82, 0), "0.79.0"), "不低于最低支持版本");
        Check(!UpdateService.IsBelowMinimum(new Version(0, 1, 0), ""), "空的最低版本不触发强制更新");
        Check(!UpdateService.IsBelowMinimum(new Version(0, 1, 0), "乱写的版本"), "无法解析的最低版本不触发强制更新");
    }

    private static void RunUrlAndHostChecks()
    {
        Section("地址与主机白名单");

        var config = UpdateService.LoadConfig(null);

        Check(UpdateService.IsAllowedHost("raw.githubusercontent.com", ["githubusercontent.com"]), "子域命中白名单");
        Check(UpdateService.IsAllowedHost("github.com", ["github.com"]), "精确主机命中白名单");
        Check(!UpdateService.IsAllowedHost("evil-githubusercontent.com", ["githubusercontent.com"]),
            "后缀伪装主机（evil-githubusercontent.com）被拒绝");
        Check(!UpdateService.IsAllowedHost("github.com.evil.net", ["github.com"]), "父域伪装主机被拒绝");

        Check(UpdateService.IsAllowedUrl("https://github.com/x/y.exe", config, out _), "https + 白名单主机通过");
        Check(!UpdateService.IsAllowedUrl("http://github.com/x/y.exe", config, out _), "http 被拒绝");
        Check(!UpdateService.IsAllowedUrl("https://example.com/y.exe", config, out _), "白名单外主机被拒绝");
        Check(!UpdateService.IsAllowedUrl("not a url", config, out _), "非 URL 被拒绝");
        Check(!UpdateService.IsAllowedUrl("", config, out _), "空地址被拒绝");

        Check(UpdateService.IsValidSha256(new string('a', 64)), "64 位十六进制 SHA256 通过");
        Check(!UpdateService.IsValidSha256(new string('a', 63)), "63 位 SHA256 被拒绝");
        Check(!UpdateService.IsValidSha256(new string('z', 64)), "非十六进制 SHA256 被拒绝");
        Check(!UpdateService.IsValidSha256(null), "缺失 SHA256 被拒绝");
    }

    private static void RunManifestChecks()
    {
        Section("清单解析与安全护栏");

        var config = UpdateService.LoadConfig(null);
        const string sha = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";

        var good = """
        {
          // 允许注释与尾逗号（与扩展优化库同一套解析选项）
          "schemaVersion": 1,
          "version": "0.83.0",
          "displayVersion": "OpenAlphaV0.83",
          "mandatory": false,
          "minimumSupportedVersion": "0.79.0",
          "releasePageUrl": "https://github.com/DRXiaoXi/DeltaNFD/releases/tag/v0.83.0",
          "notes": "测试更新说明",
          "installers": [
            { "url": "https://github.com/DRXiaoXi/DeltaNFD/releases/download/v0.83.0/setup.exe",
              "sizeBytes": "12345678", "sha256": "%SHA%" },
            { "url": "https://mirror.example.com/setup.exe", "sha256": "%SHA%" },
          ],
        }
        """.Replace("%SHA%", sha);

        Check(UpdateService.TryParseManifest(good, config, out var manifest, out var reason), "合法清单解析通过：" + reason);
        Check(manifest is not null && manifest.Version == "0.83.0", "清单版本读取正确");
        Check(manifest is not null && manifest.Installers is { Count: 1 }, "白名单外镜像地址被过滤，仅保留合法项");
        Check(manifest is not null && manifest.Installers![0].SizeBytes == 12345678, "字符串形式的 sizeBytes 可解析");
        Check(manifest is not null && manifest.Notes == "测试更新说明", "更新说明读取正确");

        Check(!UpdateService.TryParseManifest("{ \"schemaVersion\": 2, \"version\": \"0.83.0\" }", config, out _, out _),
            "不支持的 schemaVersion 被拒绝");
        Check(!UpdateService.TryParseManifest("{ \"schemaVersion\": 1, \"version\": \"\" }", config, out _, out _),
            "缺少版本号被拒绝");
        Check(!UpdateService.TryParseManifest("{ \"schemaVersion\": 1, \"version\": \"0.83.0\" }", config, out _, out _),
            "没有安装包地址被拒绝");
        Check(!UpdateService.TryParseManifest("{ ", config, out _, out _), "坏 JSON 被拒绝（不抛异常）");

        var noHash = $$"""
        { "schemaVersion": 1, "version": "0.83.0",
          "installers": [ { "url": "https://github.com/x/setup.exe" } ] }
        """;
        Check(!UpdateService.TryParseManifest(noHash, config, out _, out var noHashReason), "缺失 SHA256 的安装包被拒绝");
        Check(noHashReason.Contains("SHA256", StringComparison.Ordinal), "拒绝原因说明是 SHA256 问题：" + noHashReason);

        var insecure = $$"""
        { "schemaVersion": 1, "version": "0.83.0",
          "installers": [ { "url": "http://github.com/x/setup.exe", "sha256": "{{sha}}" } ] }
        """;
        Check(!UpdateService.TryParseManifest(insecure, config, out _, out _), "http 下载地址的清单被拒绝");
    }

    private static void RunReleaseChecks()
    {
        Section("GitHub 已发布最高版本与备用源（离线）");
        var config = UpdateService.LoadConfig(null);
        object Release(string tag, bool draft = false, string digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            string name = "DeltaNFD_Setup_0.83.0_x64.exe") => new
        {
            tag_name = tag, name = tag, draft, prerelease = true,
            published_at = "2026-09-30T00:00:00Z",
            html_url = "https://github.com/DRXiaoXi/DeltaNFD/releases/tag/" + tag,
            body = "release notes",
            assets = new[] { new { name, state = "uploaded", size = 123L, digest,
                browser_download_url = "https://github.com/DRXiaoXi/DeltaNFD/releases/download/" + tag + "/" + name } },
        };
        var json = JsonSerializer.Serialize(new[] { Release("v0.82.0"), Release("v0.83.0"), Release("v9.0.0", draft: true) });
        Check(UpdateService.TryParseUpdateSource(json, config, out var latest, out var reason) && latest?.Version == "0.83.0",
            "包含 Alpha、忽略草稿，最高版本不是返回列表的第一项：" + reason);
        var service = new UpdateService(config);
        Check(service.Evaluate(latest!, new AppSettings(), new Version(0, 82, 0)).HasUpdate, "远端 0.83 高于安装 0.82 时提供更新");
        Check(!service.Evaluate(latest!, new AppSettings(), new Version(0, 83, 0)).HasUpdate, "同版本不安装");
        Check(!service.Evaluate(latest!, new AppSettings(), new Version(0, 84, 0)).HasUpdate, "本机高于远端时不降级");
        var skipped = new AppSettings { SkippedUpdateVersion = "0.83.0" };
        Check(service.Evaluate(latest!, skipped, new Version(0, 82, 0), ignoreSkipped: true).HasUpdate,
            "手动检查可重新显示跳过的最新版");
        var numeric = JsonSerializer.Serialize(new[] { Release("v0.9.0"), Release("v0.10.0") });
        Check(UpdateService.TryParseUpdateSource(numeric, config, out var numericLatest, out _) && numericLatest?.Version == "0.10.0",
            "版本比较按数值而不是字典序");
        var invalid = JsonSerializer.Serialize(new[] { Release("v0.83.0"), Release("v0.84.0", digest: "") });
        Check(!UpdateService.TryParseUpdateSource(invalid, config, out _, out reason) && reason.Contains("SHA256"),
            "最新版缺少哈希时拒绝，不能选择旧版代替");
        Check(!UpdateService.TryParseUpdateSource("[]", config, out _, out _), "空发布列表不伪造可安装版本");
        Check(!UpdateService.TryParseUpdateSource("[null]", config, out _, out _), "异常发布条目不导致崩溃");
        var arm = JsonSerializer.Serialize(new[] { Release("v0.83.0", name: "DeltaNFD_Setup_0.83.0_arm64.exe") });
        Check(!UpdateService.TryParseUpdateSource(arm, config, out _, out _), "不会选错 ARM64 安装包");
        var chinese = JsonSerializer.Serialize(new[] { Release("v0.83.0", name: "三角帧不掉洲_DeltaNFD_安装包_0.83.0_x64.exe") });
        Check(UpdateService.TryParseUpdateSource(chinese, config, out _, out _), "支持中文安装包名称");

        var primary = GitHubReleaseSource.ReleasesUrl;
        var mirror = "https://mirror.example/update.json";
        config.ManifestUrls = [primary, mirror];
        config.AllowedHosts!.Add("mirror.example");
        var requests = new List<string>();
        Task<string> Fetch(string url, CancellationToken ct)
        {
            requests.Add(url);
            return url == primary ? Task.FromException<string>(new HttpRequestException("offline")) : Task.FromResult(json);
        }
        var fallback = service.CheckSourcesAsync(config, new AppSettings(), new Version(0, 82, 0), true, Fetch)
            .GetAwaiter().GetResult();
        Check(fallback.HasUpdate && requests.SequenceEqual(new[] { primary, mirror }), "主站访问失败时才尝试已配置备用源");
        requests.Clear();
        var healthy = service.CheckSourcesAsync(config, new AppSettings(), new Version(0, 84, 0), true,
            (url, ct) => { requests.Add(url); return Task.FromResult(json); }).GetAwaiter().GetResult();
        Check(healthy.Status == UpdateCheckStatus.UpToDate && requests.Count == 1, "主站可达且版本较低时不使用镜像制造更新");
        requests.Clear();
        var rejected = service.CheckSourcesAsync(config, new AppSettings(), new Version(0, 82, 0), true,
            (url, ct) => { requests.Add(url); return Task.FromResult(invalid); }).GetAwaiter().GetResult();
        Check(rejected.Status == UpdateCheckStatus.ManifestInvalid && requests.Count == 1, "主站新版校验缺失时不回退旧镜像");
    }

    private static void RunFileChecks(string tempRoot)
    {
        Section("安装包校验（临时目录）");

        var file = Path.Combine(tempRoot, "hello.bin");
        File.WriteAllText(file, "hello");
        const string expected = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";

        var actual = UpdateService.ComputeSha256Async(file).GetAwaiter().GetResult();
        Check(string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase), "SHA256 计算结果与预期一致");

        var matching = new UpdateInstallerInfo
        {
            Url = "https://github.com/x/y.exe",
            SizeBytes = 5,
            Sha256 = expected,
        };
        Check(UpdateService.DescribeMismatchAsync(file, matching).GetAwaiter().GetResult() is null, "哈希与大小都匹配时校验通过");

        var wrongHash = new UpdateInstallerInfo
        {
            Url = matching.Url,
            SizeBytes = 5,
            Sha256 = new string('a', 64),
        };
        var hashReason = UpdateService.DescribeMismatchAsync(file, wrongHash).GetAwaiter().GetResult();
        Check(hashReason is not null && hashReason.Contains("SHA256", StringComparison.Ordinal), "哈希不符被拒绝：" + hashReason);

        var wrongSize = new UpdateInstallerInfo
        {
            Url = matching.Url,
            SizeBytes = 99,
            Sha256 = expected,
        };
        var sizeReason = UpdateService.DescribeMismatchAsync(file, wrongSize).GetAwaiter().GetResult();
        Check(sizeReason is not null && sizeReason.Contains("大小", StringComparison.Ordinal), "大小不符被拒绝：" + sizeReason);

        var missing = new UpdateInstallerInfo { Url = matching.Url, Sha256 = expected };
        Check(UpdateService.DescribeMismatchAsync(Path.Combine(tempRoot, "nope.bin"), missing)
            .GetAwaiter().GetResult() is not null, "文件不存在时校验失败");

        Check(UpdateService.IsUnderUpdateRoot(Path.Combine(tempRoot, "update", "0.83.0", "a.exe"),
            Path.Combine(tempRoot, "update")), "更新目录内的路径被识别");
        Check(!UpdateService.IsUnderUpdateRoot(@"C:\Windows\System32\cmd.exe", Path.Combine(tempRoot, "update")),
            "更新目录外的路径被拒绝（清理护栏）");
    }

    private static void RunLaunchSafetyChecks(string tempRoot)
    {
        Section("安装前复检与执行文件锁（不启动安装程序）");
        var root = Path.Combine(tempRoot, "launch-cache");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "setup.exe");
        File.WriteAllText(path, "original");
        var manifest = new UpdateManifest
        {
            Version = "0.83.0",
            Installers = [new UpdateInstallerInfo
            {
                Url = "https://github.com/x/setup.exe",
                SizeBytes = new FileInfo(path).Length,
                Sha256 = UpdateService.ComputeSha256Async(path).GetAwaiter().GetResult()!,
            }],
        };
        var config = new UpdateConfig { AllowedHosts = ["github.com"] };
        using (var locked = UpdateService.OpenVerifiedInstallerAsync(path, manifest, root, config)
            .GetAwaiter().GetResult())
        {
            Check(locked.CanRead, "合法安装包通过启动前复检");
            Check(Throws<IOException>(() => File.WriteAllText(path, "tampered")), "持锁时拒绝改写安装包");
            Check(Throws<IOException>(() => File.Delete(path)), "持锁时拒绝删除或替换安装包");
            using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Check(reader.Length == locked.Length, "安装程序仍可以只读打开文件");
        }

        File.WriteAllText(path, "modified");
        Check(Throws<InvalidDataException>(() => UpdateService.OpenVerifiedInstallerAsync(path, manifest, root, config)
            .GetAwaiter().GetResult().Dispose()), "下载后同长度篡改被启动前复检拒绝");
        File.WriteAllText(path, "original");
        manifest.Installers[0].SizeBytes++;
        Check(Throws<InvalidDataException>(() => UpdateService.OpenVerifiedInstallerAsync(path, manifest, root, config)
            .GetAwaiter().GetResult().Dispose()), "启动前再次校验大小");
        manifest.Installers[0].SizeBytes--;
        Check(Throws<InvalidDataException>(() => UpdateService.OpenVerifiedInstallerAsync(path, manifest, tempRoot + "-other", config)
            .GetAwaiter().GetResult().Dispose()), "拒绝更新缓存外的可执行文件");
        manifest.Installers[0].Url = "https://untrusted.example/setup.exe";
        Check(Throws<InvalidDataException>(() => UpdateService.OpenVerifiedInstallerAsync(path, manifest, root, config)
            .GetAwaiter().GetResult().Dispose()), "启动前仍执行清单主机白名单检查");

        foreach (var directory in new[] { @"C:\Program Files\DeltaOptimizer\", @"D:\Games and Tools\Delta NFD" })
        {
            var arguments = UpdateService.BuildInstallerArguments(directory, Path.Combine(root, "install log.txt"));
            Check(arguments.Contains($"/DIR=\"{directory.TrimEnd('\\')}\"", StringComparison.Ordinal),
                "旧目录/自定义目录显式传给安装器：" + directory);
        }
        Check(Throws<ArgumentException>(() => UpdateService.BuildInstallerArguments("C:\\bad\" /DIR=C:\\other", path)),
            "拒绝安装参数引号注入");
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static void RunInstallLocationChecks(string tempRoot)
    {
        Section("安装位置判定");

        var installDir = Path.Combine(tempRoot, "ProgramFiles", "Delta NFD");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, UpdateService.ExeName), "");

        var otherDir = Path.Combine(tempRoot, "bin", "Debug");
        Directory.CreateDirectory(otherDir);
        File.WriteAllText(Path.Combine(otherDir, UpdateService.ExeName), "");

        Check(UpdateService.IsInstalledCopyAt(installDir, installDir), "安装目录与当前目录一致 → 支持自动更新");
        Check(UpdateService.IsInstalledCopyAt(installDir + Path.DirectorySeparatorChar, installDir), "结尾斜杠不影响判定");
        Check(!UpdateService.IsInstalledCopyAt(installDir, otherDir), "开发构建（目录不一致）→ 不支持自动更新");
        Check(!UpdateService.IsInstalledCopyAt(null, installDir), "没有安装记录 → 不支持自动更新");
        Check(!UpdateService.IsInstalledCopyAt(installDir, tempRoot), "任意目录不能冒充安装目录");
    }

    private static void RunThrottleChecks()
    {
        Section("自动检查节流");

        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        Check(!UpdateService.IsThrottled(null, 6, now), "从未检查过 → 不节流");
        Check(UpdateService.IsThrottled(now.AddHours(-1), 6, now), "1 小时前检查过 → 节流");
        Check(!UpdateService.IsThrottled(now.AddHours(-7), 6, now), "7 小时前检查过 → 不节流");
        Check(!UpdateService.IsThrottled(now.AddHours(2), 6, now), "时钟被回拨 → 不节流（避免永久不再检查）");
    }

    private static void RunSemanticsChecks()
    {
        Section("更新判定语义（跳过 / 强制 / 已是最新）");

        var service = new UpdateService(UpdateService.LoadConfig(null));

        var skipped = new AppSettings { SkippedUpdateVersion = "0.83.0" };
        var skippedResult = service.Evaluate(new UpdateManifest { Version = "0.83.0" }, skipped, new Version(0, 82, 0));
        Check(skippedResult.Status == UpdateCheckStatus.Skipped, "点过跳过的版本不再提示");

        var mandatoryResult = service.Evaluate(
            new UpdateManifest { Version = "0.83.0", Mandatory = true }, skipped, new Version(0, 82, 0));
        Check(mandatoryResult.Status == UpdateCheckStatus.UpdateAvailable && mandatoryResult.Mandatory,
            "强制更新忽略「跳过此版本」");

        var belowMinimum = service.Evaluate(
            new UpdateManifest { Version = "0.83.0", MinimumSupportedVersion = "0.83.0" },
            skipped, new Version(0, 82, 0));
        Check(belowMinimum.Status == UpdateCheckStatus.UpdateAvailable && belowMinimum.Mandatory,
            "当前版本低于最低支持版本 → 视为强制更新");

        var upToDate = service.Evaluate(
            new UpdateManifest { Version = "0.82.0" }, new AppSettings(), new Version(0, 82, 0));
        Check(upToDate.Status == UpdateCheckStatus.UpToDate, "同版本 → 已是最新");

        var older = service.Evaluate(
            new UpdateManifest { Version = "0.81.0" }, new AppSettings(), new Version(0, 82, 0));
        Check(older.Status == UpdateCheckStatus.UpToDate, "远端比本机旧 → 不提示更新");

        var available = service.Evaluate(
            new UpdateManifest { Version = "0.83.0", DisplayVersion = "OpenAlphaV0.83", Notes = "说明" },
            new AppSettings(), new Version(0, 82, 0));
        Check(available.Status == UpdateCheckStatus.UpdateAvailable, "远端更新 → 提示可更新");
        Check(available.AvailableVersionText == "OpenAlphaV0.83", "显示版本优先用 displayVersion");
        Check(available.Notes == "说明", "更新说明透传");

        var badVersion = service.Evaluate(
            new UpdateManifest { Version = "abc" }, new AppSettings(), new Version(0, 82, 0));
        Check(badVersion.Status == UpdateCheckStatus.ManifestInvalid, "非法版本号 → 清单不可用");

        Check(!service.IsInstalledCopy, "开发构建运行时 IsInstalledCopy = false（本机实测）");
        Check(!string.IsNullOrWhiteSpace(service.FallbackReleasePageUrl), "存在手动下载兜底地址");
        Check(service.UpdateRootDirectory.Contains("Delta NFD", StringComparison.Ordinal), "下载目录位于 Delta NFD 下");
    }

    private static void RunConfigChecks(string tempRoot)
    {
        Section("更新配置（Assets\\UpdateConfig.json）");

        var defaults = UpdateService.LoadConfig(null);
        Check(defaults.ManifestUrls is { Count: > 0 }, "配置缺失时使用内置清单地址");
        Check(defaults.AllowedHosts is { Count: > 0 }, "配置缺失时使用内置主机白名单");
        Check(defaults.AutoCheckThrottleHours == 6, "默认节流 6 小时");

        var broken = Path.Combine(tempRoot, "broken.json");
        File.WriteAllText(broken, "{ not json ");
        var fromBroken = UpdateService.LoadConfig(broken);
        Check(fromBroken.ManifestUrls is { Count: > 0 }, "配置损坏时回退内置默认值（不抛异常）");

        var partial = Path.Combine(tempRoot, "partial.json");
        File.WriteAllText(partial, """
        {
          // 只给清单地址，其余走默认
          "manifestUrls": [ "https://raw.githubusercontent.com/DRXiaoXi/DeltaNFD/main/update.json" ],
          "autoCheckThrottleHours": 0
        }
        """);
        var fromPartial = UpdateService.LoadConfig(partial);
        Check(fromPartial.ManifestUrls is { Count: 1 }, "自定义清单地址生效");
        Check(fromPartial.ManifestUrls![0] == GitHubReleaseSource.ReleasesUrl, "旧默认 main 清单配置自动迁移到发布列表");
        Check(fromPartial.AutoCheckThrottleHours == 6, "非法节流值回退默认");
        Check(fromPartial.AllowedHosts is { Count: > 0 }, "未声明的白名单回退默认");

        var shipped = Path.Combine(AppContext.BaseDirectory, "Assets", "UpdateConfig.json");
        var shippedConfig = UpdateService.LoadConfig(shipped);
        var shippedUrl = shippedConfig.ManifestUrls is { Count: > 0 } ? shippedConfig.ManifestUrls[0] : "(空)";
        Check(File.Exists(shipped), "随程序输出的 Assets\\UpdateConfig.json 存在：" + shipped);
        Check(shippedConfig.ManifestUrls is { Count: > 0 } &&
              UpdateService.IsAllowedUrl(shippedUrl, shippedConfig, out _),
            "随程序输出的清单地址通过自身白名单校验：" + shippedUrl);
        Check(UpdateService.IsAllowedUrl(shippedConfig.ReleasePageUrl, shippedConfig, out _),
            "随程序输出的兜底发布页面地址通过白名单校验");

        var manifestJson = JsonSerializer.Serialize(shippedConfig);
        Check(manifestJson.Length > 0 && !manifestJson.Contains("null", StringComparison.Ordinal), "配置可正常序列化");
    }

    private static void RunEffectiveConfigChecks(string tempRoot)
    {
        Section("更新源优先级（用户配置 > 程序内置 > 默认）");

        var shipped = Path.Combine(AppContext.BaseDirectory, "Assets", "UpdateConfig.json");
        var user = Path.Combine(tempRoot, "update-source.json");

        var shippedConfig = UpdateService.LoadEffectiveConfig(shipped, user, out var sourceShipped);
        Check(sourceShipped.StartsWith("程序内置配置", StringComparison.Ordinal),
            "没有用户配置时使用程序内置配置：" + sourceShipped);
        Check(shippedConfig.ManifestUrls is { Count: > 0 }, "程序内置配置可用");

        // 模拟用户后来加了国内镜像（Gitee raw 清单 + 镜像主机白名单）
        File.WriteAllText(user, """
        {
          "schemaVersion": 1,
          "manifestUrls": [ "https://gitee.com/example/DeltaNFD/raw/main/update.json" ],
          "allowedHosts": [ "gitee.com" ]
        }
        """);
        var userConfig = UpdateService.LoadEffectiveConfig(shipped, user, out var sourceUser);
        Check(sourceUser.StartsWith("用户配置", StringComparison.Ordinal), "存在用户配置时优先使用：" + sourceUser);
        Check(userConfig.ManifestUrls is { Count: 1 } &&
              userConfig.ManifestUrls[0].Contains("gitee.com", StringComparison.Ordinal),
            "用户配置的镜像清单地址生效");
        Check(UpdateService.IsAllowedUrl(userConfig.ManifestUrls![0], userConfig, out _),
            "用户配置里声明的镜像主机通过白名单校验");
        Check(userConfig.CheckTimeoutSeconds == 15 && userConfig.AutoCheckThrottleHours == 6,
            "用户配置缺字段时自动补齐默认值");

        File.WriteAllText(user, "{ not json");
        var brokenFallback = UpdateService.LoadEffectiveConfig(shipped, user, out var sourceBroken);
        Check(sourceBroken.StartsWith("程序内置配置", StringComparison.Ordinal), "用户配置损坏时退回程序内置配置");
        Check(brokenFallback.ManifestUrls is { Count: > 0 }, "退回后仍有清单地址");

        File.WriteAllText(user, """{ "schemaVersion": 1, "autoCheckThrottleHours": 12 }""");
        var noUrls = UpdateService.LoadEffectiveConfig(shipped, user, out var sourceNoUrls);
        Check(sourceNoUrls.StartsWith("程序内置配置", StringComparison.Ordinal), "用户配置没写清单地址时退回程序内置配置");
        Check(noUrls.ManifestUrls is { Count: > 0 }, "退回后仍有清单地址");

        var neither = UpdateService.LoadEffectiveConfig(
            Path.Combine(tempRoot, "no-such-shipped.json"),
            Path.Combine(tempRoot, "no-such-user.json"),
            out var sourceNone);
        Check(sourceNone == "内置默认值", "两级配置都缺失时使用内置默认值：" + sourceNone);
        Check(neither.ManifestUrls is { Count: > 0 } && neither.AllowedHosts is { Count: > 0 }, "内置默认值可用");

        var templatePath = Path.Combine(tempRoot, "template", "update-source.json");
        Check(UpdateService.WriteUserConfigTemplate(templatePath), "更新源配置模板写入成功");
        var templateConfig = UpdateService.LoadConfig(templatePath);
        Check(templateConfig.ManifestUrls is { Count: > 0 }, "写出的模板可被解析（带注释也能解析）");
        Check(UpdateService.IsAllowedUrl(templateConfig.ManifestUrls![0], templateConfig, out _),
            "模板里的清单地址通过白名单校验");
        Check(templateConfig.AllowedHosts is { Count: >= 4 }, "模板包含默认主机白名单");
        Check(UpdateService.TryReadConfigText(UpdateService.UserConfigTemplate, out var fromText, out var templateReason) &&
              fromText.ManifestUrls is { Count: > 0 },
            "模板文本可直接解析：" + templateReason);
    }

    private static void RunEnvironmentReport()
    {
        Section("本机环境信息（只读）");

        var service = new UpdateService(UpdateService.LoadConfig(Path.Combine(
            AppContext.BaseDirectory, "Assets", "UpdateConfig.json")));

        Console.WriteLine($"  当前版本：{service.CurrentVersionText}（{AppVersion.NumericText}，程序集 {service.CurrentVersion}）");
        Console.WriteLine($"  运行目录：{service.InstallDirectory}");
        Console.WriteLine($"  安装位置：{UpdateService.ReadInstalledLocation() ?? "(未登记)"}");
        Console.WriteLine($"  支持自动更新：{(service.IsInstalledCopy ? "是（安装版）" : "否（开发构建/手动解压）")}");
        Console.WriteLine($"  下载目录：{service.UpdateRootDirectory}");
        Console.WriteLine($"  更新源来自：{service.ConfigSourceDescription}");
        Console.WriteLine($"  用户配置文件：{service.UserConfigPath}（存在={File.Exists(service.UserConfigPath)}）");
        Console.WriteLine($"  清单地址：{string.Join("；", service.Config.ManifestUrls ?? new List<string>())}");
        Console.WriteLine($"  主机白名单：{string.Join("；", service.Config.AllowedHosts ?? new List<string>())}");
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine("-- " + title);
    }

    private static void Check(bool condition, string name)
    {
        Console.WriteLine((condition ? "  [通过] " : "  [失败] ") + name);
        if (!condition)
        {
            Failures.Add(name);
        }
    }
}
