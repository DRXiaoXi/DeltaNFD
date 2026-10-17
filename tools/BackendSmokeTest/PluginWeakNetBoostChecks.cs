using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using System.Text;
using DeltaNFD.Services;
using DeltaNFD.Services.Plugins;
using Plugin.WeakNetBoost;

namespace BackendSmokeTest;

/// <summary>
/// 「弱网加速」拓展插件检查。
///
/// 覆盖三件事：
///   1) 包合同：仓库内 manifest.json / ui.json / README / LICENSE 是否满足宿主解析器与规范要求；
///   2) 加速链路：把插件真实生成的加速配置交给宿主真实的 UpdateService 解析，验证
///      「历史清单迁移为直连 / 移除加速主机 / 历史镜像条目恢复为官方地址」；
///   3) 可恢复改写：用临时目录驱动真实的 WeakNetBoostProfile.Apply/Restore
///      （插件与测试编译同一份源码，不是各写一份），覆盖首次开启、原文件备份还原、
///      幂等、第三方改动冲突、证据丢失等路径。
///
/// 注意：这里**不会**调用 apply 操作的真实端到端，也不会向真实 %APPDATA% 写任何东西——
/// Environment.GetFolderPath(ApplicationData) 无法用环境变量重定向，真实调用会改写开发机上的
/// 用户级更新源。端到端只跑只读的 probe，以及会被 backupId 前缀校验直接拒绝的 restore。
/// </summary>
internal static class PluginWeakNetBoostChecks
{
    private const string DirectInstallerUrl =
        "https://github.com/DRXiaoXi/DeltaNFD/releases/download/v0.90.1/DeltaNFD_Setup_0.90.1_x64.exe";

    private const string Sha256Sample =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static int _failures;
    private static int _checks;
    private static Guid _lastRequestId;

    public static void Run()
    {
        RunPackageContractChecks();
        RunAcceleratedConfigChecks();
        RunMutationChecks();

        // 导入与端到端都需要真实后端产物；缺失只报一次，避免同一个原因刷两条失败。
        var exe = FindPluginBackend(FindRepoRoot());
        if (exe is null)
        {
            _checks++;
            _failures++;
            Console.WriteLine("  [失败] 未找到插件后端产物（先构建 src/Plugin.WeakNetBoost）：导入与端到端检查未执行。");
        }
        else
        {
            RunPackageImportCheck(exe);
            RunBackendEndToEnd(exe);
        }

        Console.WriteLine($"弱网加速插件检查完成：{_checks} 项，失败 {_failures} 项。");
        if (_failures > 0) Environment.ExitCode = 1;
    }

    private static void Check(string name, bool condition, string? detail = null)
    {
        _checks++;
        if (condition) return;
        _failures++;
        Console.WriteLine($"  [失败] {name}{(detail is null ? "" : "：" + detail)}");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DeltaNFD.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("未找到仓库根目录。");
    }

    // ---------------------------------------------------------------- 1) 包合同

    private static void RunPackageContractChecks()
    {
        Console.WriteLine("== 弱网加速插件：包合同 ==");
        var root = FindRepoRoot();
        var pluginDir = Path.Combine(root, "src", "Plugin.WeakNetBoost");
        var packageDir = Path.Combine(pluginDir, "package");

        var manifest = PluginManifestParser.Parse(File.ReadAllBytes(Path.Combine(packageDir, "manifest.json")));
        var ui = PluginUiParser.Parse(File.ReadAllBytes(Path.Combine(packageDir, "ui.json")), manifest);

        Check("manifest 为 schema 2 并声明侧栏入口",
            manifest.SchemaVersion == 2 &&
            manifest.Navigation is { Label: "弱网加速", Icon: "activity", PageId: "main" });
        Check("插件 ID 与 Profile 常量一致且未被保留前缀拦截", manifest.Id == WeakNetBoostProfile.PluginId,
            manifest.Id);
        Check("兼容当前宿主数值版本 0.90.1", manifest.IsCompatible(new Version(0, 90, 1)));
        Check("不提前宣称兼容 1.0.0", !manifest.IsCompatible(new Version(1, 0, 0)));
        Check("后端入口为包内 x64 EXE",
            manifest.Backend.Entry == "backend/Plugin.WeakNetBoost.exe" && manifest.Backend.Architecture == "x64");
        Check("协议版本为 1.0", manifest.ProtocolMajor == 1 && manifest.ProtocolMinor == 0);

        var apply = manifest.FindOperation("apply");
        var probe = manifest.FindOperation("probe");
        Check("apply 是声明冲突范围的可恢复修改操作",
            apply is { Mutating: true, Reversible: true, TargetScoped: false } &&
            apply.ResourceIds.SequenceEqual(["filesystem.tweak"]));
        Check("probe 是只读操作",
            probe is { Mutating: false, Reversible: false, TargetScoped: false } && probe.ResourceIds.Count == 0);
        Check("两个操作超时都在 1-300 秒内",
            manifest.Operations.All(o => o.TimeoutSeconds is >= 1 and <= 300));
        Check("不请求持续运行或脱机自主运行",
            !manifest.Capabilities.Continuous && !manifest.Capabilities.OfflineAutonomous);
        Check("权限类别仅为 files.modify 与 network.access",
            manifest.Permissions.Select(p => p.Id).OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual(["files.modify", "network.access"]));
        Check("每项权限用途非空", manifest.Permissions.All(p => p.Purpose.Trim().Length > 0));

        Check("ui.json 控件数为 7", ui.Controls.Count == 7, ui.Controls.Count.ToString());
        var toggle = ui.FindControl("enabled");
        Check("开关控件是可默认未勾选的 toggle",
            toggle is { Type: PluginControlType.Toggle, DefaultToggle: false });
        Check("按钮都绑定了已声明的操作",
            ui.Controls.Where(c => c.Type == PluginControlType.Button)
                .All(c => c.OperationId is not null && manifest.HasOperation(c.OperationId)) &&
            ui.Controls.Count(c => c.Type == PluginControlType.Button) == 2);

        foreach (var required in new[] { "manifest.json", "ui.json", "README.md", "LICENSE.txt" })
            Check($"包内包含 {required}", File.Exists(Path.Combine(packageDir, required)));

        var readme = File.ReadAllText(Path.Combine(packageDir, "README.md"));
        Check("README 说明作者/风险/恢复/脱机",
            readme.Contains("作者") && readme.Contains("风险") && readme.Contains("恢复") && readme.Contains("脱机"));
        Check("README 说明直接改写范围与备份位置",
            readme.Contains("update-source.json") && readme.Contains("PluginData"));
        Check("README 不宣传反作弊零风险", !readme.Contains("零封号") && !readme.Contains("反作弊认可"));

        var program = File.ReadAllText(Path.Combine(pluginDir, "Program.cs"));
        var profile = File.ReadAllText(Path.Combine(pluginDir, "WeakNetBoostProfile.cs"));
        Check("插件后端不引用宿主程序集",
            !program.Contains("using DeltaNFD") && !profile.Contains("using DeltaNFD"));
        Check("插件不写回包目录或安装目录",
            !program.Contains("AppContext.BaseDirectory") && !profile.Contains("AppContext.BaseDirectory"));

        Console.WriteLine("  包合同检查完成。");
    }

    // ---------------------------------------------------------------- 2) 加速配置交给宿主真实解析器

    private static void RunAcceleratedConfigChecks()
    {
        Console.WriteLine("== 弱网加速插件：加速配置与宿主更新链路 ==");
        var configText = WeakNetBoostProfile.ComposeAcceleratedConfig();

        Check("插件写的加速配置能被宿主 UpdateService 解析",
            UpdateService.TryReadConfigText(configText, out var config, out var reason), reason);
        var normalized = UpdateService.Normalize(config);
        var manifestUrls = normalized.ManifestUrls ?? [];

        Check("历史加速清单迁移为官方 API",
            manifestUrls.Count > 0 && manifestUrls[0] == GitHubReleaseSource.ReleasesUrl,
            manifestUrls.Count > 0 ? manifestUrls[0] : "(空)");
        Check("清单地址全部通过宿主白名单",
            manifestUrls.All(u => UpdateService.IsAllowedUrl(u, normalized, out _)));
        Check("历史加速主机不再进入宿主白名单",
            !UpdateService.IsAllowedHost("gh-proxy.com", normalized.AllowedHosts ?? []) &&
            !UpdateService.IsAllowedHost("gh-proxy.org", normalized.AllowedHosts ?? []));

        var acceleratedInstallerUrl = WeakNetBoostProfile.AcceleratorPrefix + DirectInstallerUrl;
        Check("历史加速地址不能绕过取消加速后的下载限制",
            !UpdateService.IsAllowedUrl(acceleratedInstallerUrl, normalized, out var installReason), installReason);
        Check("备用旧代理恢复为官方安装包地址",
            UpdateUrlRewriter.RestoreOfficialUrl(WeakNetBoostProfile.AcceleratorPrefixAlternate + DirectInstallerUrl) == DirectInstallerUrl);
        Check("两种旧代理混合嵌套仍恢复官方 API",
            UpdateUrlRewriter.RestoreOfficialUrl(WeakNetBoostProfile.AcceleratorPrefix + WeakNetBoostProfile.AcceleratorPrefixAlternate + GitHubReleaseSource.ReleasesUrl)
                == GitHubReleaseSource.ReleasesUrl);
        Check("旧代理备用地址即使被手工放入白名单也拒绝",
            !UpdateService.IsAllowedUrl(WeakNetBoostProfile.AcceleratorPrefixAlternate + DirectInstallerUrl,
                new UpdateConfig { AllowedHosts = ["gh-proxy.org"] }, out _));
        Check("代理相似域名不被误识别为旧代理",
            !UpdateUrlRewriter.IsLegacyProxyHost("gh-proxy.org.example.com"));

        // 历史镜像在前的清单仍可恢复为官方地址；不能重新启用已退休的加速。
        var mirrorOk = UpdateService.TryParseManifest(
            ManifestJson([acceleratedInstallerUrl, DirectInstallerUrl]), normalized,
            out var mirrorManifest, out var mirrorReason);
        Check("历史镜像在前的清单恢复为官方直连地址",
            mirrorOk && mirrorManifest?.Installers is { Count: > 0 } installed &&
            installed.All(i => i.Url == DirectInstallerUrl), mirrorReason);

        // 未发布镜像条目时的回退：仍然可用，不会把更新器弄坏（只是不走加速）。
        var fallbackOk = UpdateService.TryParseManifest(
            ManifestJson([DirectInstallerUrl]), normalized,
            out var fallbackManifest, out var fallbackReason);
        Check("未发布镜像条目时回退直连仍可用",
            fallbackOk && fallbackManifest?.Installers is { Count: > 0 } onlyInstalled &&
            !WeakNetBoostProfile.IsAcceleratedUrl(onlyInstalled[0].Url), fallbackReason);

        // 白名单是承载条件：不带 gh-proxy 的配置下，加速地址会被拒绝——证明插件必须同时写 allowedHosts。
        var withoutAccelerator = UpdateService.Normalize(new UpdateConfig
        {
            ManifestUrls = [WeakNetBoostProfile.AcceleratorPrefix + WeakNetBoostProfile.RawManifestUrl],
            AllowedHosts = [.. WeakNetBoostProfile.DirectHosts],
        });
        Check("缺少加速主机的白名单会拒绝加速地址（allowedHosts 是必须的）",
            !UpdateService.IsAllowedUrl(acceleratedInstallerUrl, withoutAccelerator, out _));

        Check("加速配置保留直连主机作为兜底",
            WeakNetBoostProfile.AllowedHosts.Contains("github.com") &&
            WeakNetBoostProfile.AllowedHosts.Contains("api.github.com"));
        Check("加速清单读取顺序：先加速静态清单，最后才退到直连",
            WeakNetBoostProfile.ManifestUrls[0] == WeakNetBoostProfile.AcceleratorPrefix + WeakNetBoostProfile.RawManifestUrl &&
            WeakNetBoostProfile.ManifestUrls[^1] == WeakNetBoostProfile.ReleasesApiUrl &&
            WeakNetBoostProfile.ManifestUrls.Contains(WeakNetBoostProfile.AcceleratorPrefixAlternate + WeakNetBoostProfile.RawManifestUrl) &&
            WeakNetBoostProfile.ManifestUrls.Count(u => !WeakNetBoostProfile.IsAcceleratedUrl(u)) == 1);
        Check("插件识别自己的写入标记",
            WeakNetBoostProfile.IsWrittenByPlugin(Encoding.UTF8.GetBytes(configText)) &&
            !WeakNetBoostProfile.IsWrittenByPlugin(Encoding.UTF8.GetBytes("{\"manifestUrls\":[]}")));
        Check("加速地址识别与主机描述",
            WeakNetBoostProfile.IsAcceleratedUrl(acceleratedInstallerUrl) &&
            WeakNetBoostProfile.DescribeUrlHost(acceleratedInstallerUrl) == "gh-proxy → github.com" &&
            WeakNetBoostProfile.DescribeUrlHost(DirectInstallerUrl) == "github.com");

        Console.WriteLine("  加速配置检查完成。");
    }

    private static string ManifestJson(IReadOnlyList<string> installerUrls)
    {
        var installers = string.Join(",", installerUrls.Select(u =>
            "{\"url\":\"" + u + "\",\"sizeBytes\":0,\"sha256\":\"" + Sha256Sample + "\"}"));
        return $$"""
            {
              "schemaVersion": 1,
              "version": "0.90.1",
              "displayVersion": "Beta0.9",
              "channel": "beta",
              "publishedAtUtc": "2026-10-03T09:40:25Z",
              "notes": "",
              "installers": [{{installers}}]
            }
            """.ReplaceLineEndings("");
    }

    // ---------------------------------------------------------------- 3) 可恢复改写（真实逻辑 + 临时目录）

    private static void RunMutationChecks()
    {
        Console.WriteLine("== 弱网加速插件：可恢复改写 ==");
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD_WeakNet_" + Guid.NewGuid().ToString("N"));
        var appDataRoot = Path.Combine(root, "appdata");
        var dataDirectory = Path.Combine(root, "plugin-data");
        var configPath = WeakNetBoostProfile.ConfigPathFor(appDataRoot);
        try
        {
            Check("用户级更新源路径拼接与宿主一致",
                configPath == Path.Combine(appDataRoot, "Delta NFD", "update-source.json"), configPath);
            Check("普通临时路径不被判定为不安全",
                !WeakNetBoostProfile.HasReparsePointInPath(configPath));
            Check("恢复标识同插件内唯一且带前缀",
                WeakNetBoostProfile.NewBackupId() != WeakNetBoostProfile.NewBackupId() &&
                WeakNetBoostProfile.NewBackupId().StartsWith(WeakNetBoostProfile.BackupIdPrefix, StringComparison.Ordinal));

            // 场景 1：开启前没有该文件 → 还原时必须删掉自己新建的文件。
            var enableA = WeakNetBoostProfile.Apply(configPath, dataDirectory, WeakNetBoostProfile.NewBackupId());
            Check("首次开启成功", enableA.Ok && enableA.Kind == WeakNetBoostProfile.ChangeKind.Applied, enableA.Message);
            Check("开启后写入的文件是本插件产物",
                File.Exists(configPath) &&
                WeakNetBoostProfile.IsWrittenByPlugin(File.ReadAllBytes(configPath)));
            Check("开启前无原件时留下“原值不存在”标记，而不是伪造备份",
                File.Exists(WeakNetBoostProfile.AbsentMarkerPathFor(dataDirectory)) &&
                !File.Exists(WeakNetBoostProfile.BackupPathFor(dataDirectory)));
            Check("开启后状态为已应用", WeakNetBoostProfile.IsApplied(configPath, dataDirectory));
            Check("重复开启不覆盖首次证据",
                WeakNetBoostProfile.Apply(configPath, dataDirectory, WeakNetBoostProfile.NewBackupId()).Kind ==
                WeakNetBoostProfile.ChangeKind.AlreadyApplied);

            var restoreA = WeakNetBoostProfile.Restore(configPath, dataDirectory);
            Check("关闭成功并删除新建文件",
                restoreA.Ok && !File.Exists(configPath) && !WeakNetBoostProfile.IsApplied(configPath, dataDirectory),
                restoreA.Message);
            Check("重复关闭是幂等的",
                WeakNetBoostProfile.Restore(configPath, dataDirectory).Kind ==
                WeakNetBoostProfile.ChangeKind.AlreadyRestored);

            // 场景 2：开启前有用户自己的配置 → 必须字节级还原（不能丢注释/格式）。
            var original = Encoding.UTF8.GetBytes(
                "// 用户自己写的更新源\r\n{\r\n  \"schemaVersion\": 1,\r\n  \"manifestUrls\": [\r\n    \"https://api.github.com/repos/DRXiaoXi/DeltaNFD/releases?per_page=100\"\r\n  ],\r\n  \"allowedHosts\": [\"api.github.com\"]\r\n}\r\n");
            WeakNetBoostProfile.WriteAtomic(configPath, original);
            var enableB = WeakNetBoostProfile.Apply(configPath, dataDirectory, WeakNetBoostProfile.NewBackupId());
            Check("有原件时开启成功", enableB.Ok, enableB.Message);
            Check("原件整份字节备份",
                File.Exists(WeakNetBoostProfile.BackupPathFor(dataDirectory)) &&
                File.ReadAllBytes(WeakNetBoostProfile.BackupPathFor(dataDirectory)).SequenceEqual(original) &&
                !File.Exists(WeakNetBoostProfile.AbsentMarkerPathFor(dataDirectory)));
            Check("开启后确实换成了加速配置",
                WeakNetBoostProfile.IsWrittenByPlugin(File.ReadAllBytes(configPath)));

            // 场景 3：第三方改动后拒绝覆盖（保留对方内容与待恢复标记）。
            var thirdParty = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"manifestUrls\":[\"https://example.com/x.json\"]}");
            WeakNetBoostProfile.WriteAtomic(configPath, thirdParty);
            var conflicting = WeakNetBoostProfile.Restore(configPath, dataDirectory);
            Check("第三方已改动时报告冲突且不覆盖",
                conflicting.Kind == WeakNetBoostProfile.ChangeKind.Conflict &&
                File.ReadAllBytes(configPath).SequenceEqual(thirdParty));
            Check("冲突后仍标记为待恢复，不假装完成",
                WeakNetBoostProfile.IsApplied(configPath, dataDirectory));

            // 恢复成插件写的内容后应能正常还原。
            WeakNetBoostProfile.WriteAtomic(configPath, Encoding.UTF8.GetBytes(WeakNetBoostProfile.ComposeAcceleratedConfig()));
            var restoreB = WeakNetBoostProfile.Restore(configPath, dataDirectory);
            Check("回到插件写入状态后可按备份还原", restoreB.Ok, restoreB.Message);
            Check("还原是字节级一致",
                File.ReadAllBytes(configPath).SequenceEqual(original));
            Check("还原后状态不是已应用", !WeakNetBoostProfile.IsApplied(configPath, dataDirectory));

            // 场景 4：备份哈希被破坏 → 拒绝还原。
            WeakNetBoostProfile.Apply(configPath, dataDirectory, WeakNetBoostProfile.NewBackupId());
            File.WriteAllBytes(WeakNetBoostProfile.BackupPathFor(dataDirectory), Encoding.UTF8.GetBytes("tampered"));
            var tampered = WeakNetBoostProfile.Restore(configPath, dataDirectory);
            Check("备份哈希不符时拒绝还原",
                tampered.Kind == WeakNetBoostProfile.ChangeKind.Failed && tampered.Message.Contains("哈希"), tampered.Message);

            // 场景 5：证据文件丢失 → 不猜原值。
            var restoreLost = WeakNetBoostProfile.Restore(configPath, dataDirectory);
            Check("缺少原值证据时不猜原值",
                restoreLost.Kind == WeakNetBoostProfile.ChangeKind.Failed, restoreLost.Kind.ToString());
            WeakNetBoostProfile.DeleteIfExists(WeakNetBoostProfile.BackupPathFor(dataDirectory));
            Check("证据彻底丢失时报告 MissingEvidence",
                WeakNetBoostProfile.Restore(configPath, dataDirectory).Kind ==
                WeakNetBoostProfile.ChangeKind.MissingEvidence);

            Console.WriteLine("  可恢复改写检查完成。");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // ---------------------------------------------------------------- 4) 真实导入器接受真实包

    /// <summary>
    /// 用「package/ 根文件 + 已构建后端产物」拼出与 tools\pack-plugin.ps1 相同的包结构，
    /// 交给宿主真实的 PluginPackageImporter 导入。这一步验证的是打包布局本身：
    /// 根文件齐备、backend 入口就位、清单与 ui 能过导入期校验。
    /// </summary>
    private static void RunPackageImportCheck(string exe)
    {
        Console.WriteLine("== 弱网加速插件：真实导入器接受真实包 ==");
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD_WeakNetImport_" + Guid.NewGuid().ToString("N"));
        try
        {
            var packageDir = Path.Combine(FindRepoRoot(), "src", "Plugin.WeakNetBoost", "package");
            var packageManifest = PluginManifestParser.Parse(File.ReadAllBytes(Path.Combine(packageDir, "manifest.json")));
            var staging = Path.Combine(root, "staging");
            Directory.CreateDirectory(Path.Combine(staging, "backend"));
            foreach (var name in new[] { "manifest.json", "ui.json", "README.md", "LICENSE.txt" })
                File.Copy(Path.Combine(packageDir, name), Path.Combine(staging, name));
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(exe)!))
                if (Path.GetExtension(file) is not (".pdb" or ".xml"))
                    File.Copy(file, Path.Combine(staging, "backend", Path.GetFileName(file)));

            var packagePath = Path.Combine(root, "weaknet-boost.dnfdplugin");
            ZipFile.CreateFromDirectory(staging, packagePath, CompressionLevel.Optimal, includeBaseDirectory: false);

            var pluginsRoot = Path.Combine(root, "Plugins");
            Directory.CreateDirectory(pluginsRoot);
            var index = new PluginPackageIndex(Path.Combine(pluginsRoot, "index.json"));
            var result = new PluginPackageImporter(index).Import(packagePath, pluginsRoot);

            Check("宿主导入器接受插件包", result.Succeeded, result.Error);
            Check("导入后默认禁用（需用户确认授权）",
                result.Entry is { State: PluginPackageState.ImportedDisabled });
            Check("安装目录与 backend 入口就位", result.Entry is not null && File.Exists(Path.Combine(
                pluginsRoot, WeakNetBoostProfile.PluginId, "1.0.0", "backend", "Plugin.WeakNetBoost.exe")));
            Check("索引记录包哈希并可按 ID 查询", result.Entry is not null &&
                index.TryGet(WeakNetBoostProfile.PluginId, out var indexed, out _) &&
                indexed!.PackageSha256 == result.Entry.PackageSha256 && indexed.Files.Count >= 5);
            Check("暂存目录已清理", !Directory.EnumerateDirectories(pluginsRoot, ".staging-*").Any());

            // 若已经用 tools\pack-plugin.ps1 打过包，顺带验证**实际要发布的那个文件**也能被导入。
            var shipped = Path.Combine(FindRepoRoot(), "dist",
                packageManifest.Id + "-" + packageManifest.Version + ".dnfdplugin");
            if (File.Exists(shipped))
            {
                var shippedRoot = Path.Combine(root, "ShippedPlugins");
                Directory.CreateDirectory(shippedRoot);
                var shippedIndex = new PluginPackageIndex(Path.Combine(shippedRoot, "index.json"));
                var shippedResult = new PluginPackageImporter(shippedIndex).Import(shipped, shippedRoot);
                Check("已打包的 dist 产物可被导入器接受", shippedResult.Succeeded, shippedResult.Error);
            }
            else
            {
                Console.WriteLine("  （dist 下没有已打包产物，跳过对实际发布包的导入验证；" +
                    "运行 tools\\pack-plugin.ps1 后会一并检查。）");
            }

            Console.WriteLine("  真实导入检查完成。");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // ---------------------------------------------------------------- 5) 只读端到端（IPC 协议）

    private static void RunBackendEndToEnd(string exe)
    {
        Console.WriteLine("== 弱网加速插件：后端只读端到端 ==");
        var pipeName = "DeltaNFD_WeakNetCheck_" + Guid.NewGuid().ToString("N");
        var session = Guid.NewGuid();
        var dataDirectory = Path.Combine(Path.GetTempPath(), "DeltaNFD_WeakNetData_" + Guid.NewGuid().ToString("N"));
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "--dnfd-pipe", pipeName, "--dnfd-session", session.ToString("D"),
                     "--dnfd-host-pid", Environment.ProcessId.ToString() })
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;
        try
        {
            Directory.CreateDirectory(dataDirectory);
            using (var connect = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                try { server.WaitForConnectionAsync(connect.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException)
                {
                    _failures++;
                    _checks++;
                    Console.WriteLine("  [失败] 插件后端未在 10 秒内连接：" +
                        (process.HasExited ? process.StandardError.ReadToEnd() : "(仍在运行)"));
                    return;
                }
            }

            _checks++;
            if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out var clientPid) || clientPid != process.Id)
            {
                _failures++;
                Console.WriteLine("  [失败] 管道客户端不是本次启动的插件后端。");
                return;
            }

            var nonce = Convert.ToBase64String(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());
            WriteServerFrame(server, "hello", session,
                "{\"nonce\":\"" + nonce + "\",\"hostVersion\":\"0.90.1\",\"dataDirectory\":\"" +
                dataDirectory.Replace("\\", "\\\\") + "\"}");
            var ready = ReadServerFrame(server, 15);
            Check("ready 帧类型与会话", ready.Type == PluginIpcType.Ready && ready.SessionId == session &&
                ready.RequestId == _lastRequestId);
            var readyPayload = PluginIpc.ParseReady(ready.Payload);
            Check("ready 回显 nonce 与插件身份",
                readyPayload.Nonce == nonce && readyPayload.PluginId == WeakNetBoostProfile.PluginId &&
                readyPayload.PluginVersion == "1.0.0" && readyPayload.Pid == process.Id);

            // 只读 probe：会联网探测加速节点，因此给足超时；结果不依赖网络是否可达。
            WriteServerFrame(server, "invoke", session, "{\"operationId\":\"probe\",\"values\":{\"enabled\":false},\"target\":null}");
            var probeResult = ReadResultFrame(server, session, 45);
            Check("probe 返回同 requestId 的 result",
                probeResult.Type == PluginIpcType.Result && probeResult.RequestId == _lastRequestId);
            var probe = PluginIpc.ParseResult(probeResult.Payload);
            Check("probe 结论结构合法",
                PluginContract.ResultStatuses.Contains(probe.Status) && probe.Code.Length > 0 && probe.Items.Count > 0);
            Check("probe 为只读：无待恢复、无备份标识", !probe.PendingRestore && probe.BackupIds.Count == 0);
            Check("probe 首项报告本地更新源状态",
                probe.Items[0].Id == "local" && probe.Items[0].Message.Contains("update-source.json"));

            WriteServerFrame(server, "invoke", session,
                "{\"operationId\":\"probe\",\"values\":{\"enabled\":false,\"unknown\":1},\"target\":null}");
            Check("拒绝未声明的输入字段",
                PluginIpc.ParseResult(ReadResultFrame(server, session, 15).Payload).Code == "PROTOCOL_ERROR");

            WriteServerFrame(server, "invoke", session, "{\"operationId\":\"apply\",\"values\":{},\"target\":null}");
            Check("apply 缺少 enabled 时被拒",
                PluginIpc.ParseResult(ReadResultFrame(server, session, 15).Payload).Code == "PROTOCOL_ERROR");

            WriteServerFrame(server, "invoke", session, "{\"operationId\":\"enable\",\"values\":{\"enabled\":true},\"target\":null}");
            Check("未声明的操作被拒",
                PluginIpc.ParseResult(ReadResultFrame(server, session, 15).Payload).Code == "UNAUTHORIZED");

            // restore 的 backupId 前缀校验：非本插件标识直接拒绝，因此不会碰到真实 %APPDATA%。
            WriteServerFrame(server, "restore", session, "{\"backupIds\":[\"not-ours\"]}");
            var foreignRestore = PluginIpc.ParseResult(ReadResultFrame(server, session, 15).Payload);
            Check("拒绝非本插件的恢复标识",
                foreignRestore.Status == "failed" && foreignRestore.Code == "RESTORE_FAILED" &&
                foreignRestore.Items.Any(i => i.Status == "failed"));

            WriteServerFrame(server, "ping", session, "{}");
            var pong = ReadServerFrame(server, 15);
            Check("pong 心跳关联", pong.Type == PluginIpcType.Pong && pong.RequestId == _lastRequestId &&
                pong.SessionId == session);

            WriteServerFrame(server, "offline.prepare", session, "{\"runId\":\"" + Guid.NewGuid().ToString("D") + "\",\"target\":null}");
            var offline = PluginIpc.ParseResult(ReadResultFrame(server, session, 15).Payload);
            Check("未声明脱机能力时拒绝 offline.prepare",
                offline.Status == "failed" && offline.Code == "OFFLINE_UNSUPPORTED");

            WriteServerFrame(server, "stop", session, "{}");
            Check("stop 确认成功",
                PluginIpc.ParseResult(ReadResultFrame(server, session, 15).Payload).Status == "success");
            process.WaitForExit(10_000);
            Check("后端正常退出", process.HasExited && process.ExitCode == 0,
                process.HasExited ? process.ExitCode.ToString() : "未退出");

            Console.WriteLine("  只读端到端检查完成（apply 的写入路径由上面的临时目录用例覆盖，未触碰真实 %APPDATA%）。");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); }
            server.Dispose();
            try { Directory.Delete(dataDirectory, recursive: true); } catch (IOException) { }
        }
    }

    private static string? FindPluginBackend(string root)
    {
        var projectDir = Path.Combine(root, "src", "Plugin.WeakNetBoost");
        foreach (var configuration in new[] { "Release", "Debug" })
            foreach (var platformSegment in new[] { "x64", "" })
            {
                var candidate = Path.Combine(projectDir, "bin", platformSegment, configuration, "net8.0-windows",
                    "Plugin.WeakNetBoost.exe");
                if (File.Exists(candidate)) return candidate;
            }
        var published = Path.Combine(projectDir, "artifacts", "publish-Release", "Plugin.WeakNetBoost.exe");
        return File.Exists(published) ? published : null;
    }

    private static void WriteServerFrame(NamedPipeServerStream pipe, string type, Guid session, string payloadJson)
    {
        _lastRequestId = Guid.NewGuid();
        var envelope = "{\"protocol\":\"1.0\",\"type\":\"" + type + "\",\"sessionId\":\"" + session.ToString("D") +
            "\",\"requestId\":\"" + _lastRequestId.ToString("D") + "\",\"payload\":" + payloadJson + "}";
        var frame = PluginIpc.EncodeFrame(envelope);
        pipe.Write(frame, 0, frame.Length);
        pipe.Flush();
    }

    /// <summary>读到本次请求的 result 为止，跳过 progress/pong（宿主就是这样处理的）。</summary>
    private static PluginIpcFrame ReadResultFrame(NamedPipeServerStream pipe, Guid session, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("等待 result 帧超时。");
            var frame = ReadServerFrame(pipe, (int)Math.Ceiling(remaining.TotalSeconds));
            if (frame.SessionId != session) throw new IOException("帧会话不匹配。");
            if (frame.Type == PluginIpcType.Result) return frame;
            if (frame.Type is PluginIpcType.Progress or PluginIpcType.Pong) continue;
            throw new IOException("等待 result 期间收到非预期消息：" + frame.Type);
        }
    }

    private static PluginIpcFrame ReadServerFrame(NamedPipeServerStream pipe, int timeoutSeconds)
    {
        var header = new byte[4];
        ReadExactly(pipe, header, timeoutSeconds);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > PluginContract.MaxFrameBytes) throw new IOException("帧长度非法。");
        var payload = new byte[length];
        ReadExactly(pipe, payload, timeoutSeconds);
        var full = new byte[4 + length];
        header.CopyTo(full, 0);
        payload.CopyTo(full, 4);
        try
        {
            if (!PluginIpc.ParseFrame(full, out var frame)) throw new IOException("帧解析失败。");
            return frame;
        }
        catch (Exception ex) when (ex is PluginContractException or IOException)
        {
            // 后端发出的原始帧不写日志，这里只在解析失败时回显前 400 字符，便于定位协议/转义问题。
            var preview = new UTF8Encoding(false, false).GetString(payload);
            throw new IOException($"后端帧不符合 IPC 协议：{ex.Message}｜原始内容：{preview[..Math.Min(preview.Length, 400)]}");
        }
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int timeoutSeconds)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        stream.ReadExactlyAsync(buffer, deadline.Token).AsTask().GetAwaiter().GetResult();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
}
