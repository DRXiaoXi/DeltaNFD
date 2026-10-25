using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeltaNFD.Services;
using DeltaNFD.Updates;

internal static class FileUpdateChecks
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        int count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        var workerStatus = Path.Combine(root, "worker-status.json");
        FileUpdateRules.AtomicJson(workerStatus, new { state = "Ready", detail = "文件已核验，正在请求安全退出。" });
        var readyStatus = FileWorkerStatus.Read(workerStatus);
        using (var exclusive = new FileStream(workerStatus, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(exclusive.Length > 0, "读取 Ready 后释放状态句柄，等待期间允许更新器独占打开");
        foreach (var phase in new[] { "Applying", "Verifying", "Committed", "Ready", "Failed" })
        {
            FileUpdateRules.AtomicJson(workerStatus, new { state = phase, detail = "真实交接错误" });
            Check(FileWorkerStatus.Read(workerStatus).State == phase, "跨阶段原子替换状态文件：" + phase);
        }
        Check(FileWorkerStatus.Failure(readyStatus, 1, "Win32Exception: coordinator compile failed").Contains("coordinator compile failed")
            && !FileWorkerStatus.Failure(readyStatus, 1, "compile failed").Contains("正在请求安全退出"), "旧 Ready 不掩盖更新器标准错误");
        Check(FileWorkerStatus.Failure(FileWorkerStatus.Read(workerStatus), 1, "stderr fallback") == "真实交接错误", "失败状态优先显示已持久化原因");
        Check(FileWorkerStatus.Failure(readyStatus, 1, "").Contains("handover.txt"), "缺少标准错误时显示退出码与交接报告位置");
        var defaults = new AppSettings();
        Check(defaults.UpdateServer == UpdateServer.SelfHosted && defaults.UpdateMode == UpdateMode.ChangedFiles, "默认自建服务器和仅替换");
        var settingsPath = Path.Combine(root, "preferences.json");
        File.WriteAllText(settingsPath, "{}");
        Check(AppSettings.Load(settingsPath).UpdateServer == UpdateServer.SelfHosted && AppSettings.Load(settingsPath).UpdateMode == UpdateMode.ChangedFiles,
            "旧设置缺少字段时采用新默认值");
        foreach (var mode in new[] { UpdateMode.ChangedFiles, UpdateMode.FullInstaller })
        {
            Check(AppSettings.Save(settingsPath, new AppSettings { UpdateServer = UpdateServer.GitHub, UpdateMode = mode })
                && AppSettings.Load(settingsPath).UpdateServer == UpdateServer.GitHub && AppSettings.Load(settingsPath).UpdateMode == mode,
                "保存方式并保留用户服务器选择：" + mode);
        }
        Check(UpdateModePolicy.Normalize((UpdateMode)999) == UpdateMode.ChangedFiles, "未知更新方式回到仅替换");
        void Reject(Action action, string message)
        {
            try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
            { count++; return; }
            throw new Exception("Expected rejection: " + message);
        }
        string Fresh(string name)
        {
            var path = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path); return path;
        }
        void Put(string directory, string relative, byte[] bytes)
        {
            var path = FileUpdateRules.Under(directory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes);
        }
        var oldBytes = Encoding.UTF8.GetBytes("old official content");
        var newBytes = Encoding.UTF8.GetBytes("new official content");
        string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        FileUpdateEntry Entry(string path, byte[] bytes) => new()
        { Path = path, SizeBytes = bytes.Length, Sha256 = Hash(bytes), DownloadSize = bytes.Length,
            DownloadSha256 = Hash(bytes), Compression = "none", Urls = ["https://deltaxiaoxi.xin/test.bin"] };
        FileUpdateManifest Simple() => new() { Version = "9.0", Files = [Entry("a.dll", newBytes), Entry("b.dll", newBytes), Entry("added.dll", newBytes)],
            Remove = [new() { Path = "obsolete.dll", PreviousSha256 = Hash(oldBytes) }] };
        (string Install, string Payload, string Transaction) Fixture()
        {
            var directory = Fresh("transaction");
            var install = Path.Combine(directory, "install");
            var payload = Path.Combine(directory, "payload");
            Directory.CreateDirectory(install); Directory.CreateDirectory(payload);
            foreach (var name in new[] { "a.dll", "b.dll", "obsolete.dll" }) Put(install, name, oldBytes);
            Put(payload, Hash(newBytes).ToLowerInvariant(), newBytes);
            return (install, payload, Path.Combine(directory, "journal"));
        }
        bool OldIntact(string install) => new[] { "a.dll", "b.dll", "obsolete.dll" }.All(n =>
            File.ReadAllBytes(Path.Combine(install, n)).SequenceEqual(oldBytes)) && !File.Exists(Path.Combine(install, "added.dll"));

        var fallbackSession = Fresh("fallback-session");
        var fallbackRoot = Fresh("fallback-install");
        var intent = new FileFallbackIntent { Root = fallbackRoot, ManifestHash = Hash(oldBytes), ReleaseJson = "{}" };
        var intentPath = Path.Combine(fallbackSession, "full-fallback-intent.json");
        var approvalPath = Path.Combine(fallbackSession, "apply-approved.txt");
        FileUpdateRules.AtomicJson(intentPath, intent);
        Reject(() => FileUpdateFallback.ReadIntent(fallbackSession, fallbackRoot, intent.Nonce), "缺少本次批准不能自动更新");
        File.WriteAllText(approvalPath, intent.ManifestHash);
        Check(FileUpdateFallback.ReadIntent(fallbackSession, fallbackRoot, intent.Nonce).Nonce == intent.Nonce, "尚未开始替换可按原批准续接");
        Reject(() => FileUpdateFallback.ReadIntent(fallbackSession, fallbackRoot, Guid.NewGuid().ToString("N")), "不能替换续接nonce");
        Reject(() => FileUpdateFallback.ReadIntent(fallbackSession, root, intent.Nonce), "不能改变安装目录");
        intent.CreatedUtc = DateTimeOffset.UtcNow.AddHours(-3); FileUpdateRules.AtomicJson(intentPath, intent);
        Reject(() => FileUpdateFallback.ReadIntent(fallbackSession, fallbackRoot, intent.Nonce), "过期授权不能自动更新");
        intent.CreatedUtc = DateTimeOffset.UtcNow; FileUpdateRules.AtomicJson(intentPath, intent);
        Directory.CreateDirectory(Path.Combine(fallbackSession, "transaction"));
        Reject(() => FileUpdateFallback.ReadIntent(fallbackSession, fallbackRoot, intent.Nonce), "事务存在但无日志不能回退全量");
        var fallbackJournal = Path.Combine(fallbackSession, "transaction", "journal.json");
        foreach (var state in new[] { "Prepared", "Applying", "Verifying", "RollingBack", "RollbackFailed", "Committed", "Unknown" })
        {
            FileUpdateRules.AtomicJson(fallbackJournal, new FileJournal { Root = fallbackRoot, State = state });
            Reject(() => FileUpdateFallback.ReadIntent(fallbackSession, fallbackRoot, intent.Nonce), "未回滚或已提交不能自动重装：" + state);
        }
        FileUpdateRules.AtomicJson(fallbackJournal, new FileJournal { Root = root, State = "RolledBack" });
        Reject(() => FileUpdateFallback.ReadIntent(fallbackSession, fallbackRoot, intent.Nonce), "其他安装目录的回滚不能冒充完成");
        FileUpdateRules.AtomicJson(fallbackJournal, new FileJournal { Root = fallbackRoot, State = "RolledBack" });
        Check(FileUpdateFallback.ReadIntent(fallbackSession, fallbackRoot, intent.Nonce).Root == fallbackRoot, "明确回滚后可续接全量");
        var readyPath = Path.Combine(fallbackSession, "full-fallback-ready.json");
        FileUpdateRules.AtomicJson(readyPath, new FileFallbackReady { Nonce = intent.Nonce, WorkerPid = 123, WorkerStartUtcTicks = 456 });
        Reject(() => FileUpdateFallback.Claim(fallbackSession, "wrong"), "错误nonce不能消耗请求");
        Check(FileUpdateFallback.Claim(fallbackSession, intent.Nonce).WorkerPid == 123, "原请求原子消耗一次");
        Reject(() => FileUpdateFallback.Claim(fallbackSession, intent.Nonce), "中断或取消后不会形成自动循环");

        foreach (var path in new[] { "../escape", "C:/a.dll", "/a.dll", "Assets//x", "Assets/./x", "Assets/../x", "Assets/a:stream",
            "Assets/CON.dll", "Assets/LPT1", "Assets/x.", "Assets/x ", "settings.json", "update-source.json", "unins000.exe", "Plugins/a.dll",
            "Assets/logs/a.txt", "backups/x", "bad-directory/a.dll", "Assets\\x", "Controls/a.exe", "Themes/a.dll", "Controls/nested/a.xbf", "Themes/settings.json", "Controls/a.xbf.exe" })
            Reject(() => FileUpdateRules.Relative(path), "protected path " + path);
        foreach (var path in new[] { "DeltaNFD.dll", "Assets/RuntimeHighFps/vstor/Common Files/test.dll", "zh-CN/module.dll", "Microsoft.UI.Xaml/test.xbf", "Controls/PageHeader.xbf", "Themes/AppStyles.xbf", "Themes/TileStyles.xbf", "themes/SectionStyles.XBF" })
            Check(FileUpdateRules.Relative(path) == path, "允许正式组件和本地化资源路径");
        var duplicate = Simple(); duplicate.Files.Add(Entry("A.DLL", newBytes));
        Reject(() => FileUpdateRules.Validate(duplicate, false), "case-insensitive collision");
        var conflict = Simple(); conflict.Files.Add(Entry("Assets/a", newBytes)); conflict.Files.Add(Entry("Assets/a/b.dll", newBytes));
        Reject(() => FileUpdateRules.Validate(conflict, false), "file/directory collision");
        var missing = Simple(); missing.Files[0].Sha256 = "bad";
        Reject(() => FileUpdateRules.Validate(missing, false), "invalid hash");
        var inconsistent = Simple(); inconsistent.Files[1].DownloadSize++;
        Reject(() => FileUpdateRules.Validate(inconsistent, false), "conflicting shared payload");
        var badUrl = Simple(); badUrl.Files[0].Urls = ["http://deltaxiaoxi.xin/a"];
        Reject(() => FileUpdateRules.Validate(badUrl, false), "HTTP URL");
        var json = JsonSerializer.SerializeToUtf8Bytes(Simple(), FileUpdateRules.Json);
        Check(FileUpdateRules.Read(json, Hash(json), false).Files.Count == 3, "固定摘要清单可解析");
        Reject(() => FileUpdateRules.Read(json, new string('a', 64), false), "manifest tampering");
        Reject(() => FileUpdateRules.Read(json, Hash(json)), "missing product inventory");

        var good = Fixture();
        Put(good.Install, "custom-user-file.txt", oldBytes);
        FileTransaction.Apply(good.Install, good.Payload, Simple(), good.Transaction, () => Check(true, "startup probe invoked"));
        Check(new[] { "a.dll", "b.dll", "added.dll" }.All(n => File.ReadAllBytes(Path.Combine(good.Install, n)).SequenceEqual(newBytes)), "变化及新增文件已替换");
        Check(!File.Exists(Path.Combine(good.Install, "obsolete.dll")) && File.Exists(Path.Combine(good.Install, "custom-user-file.txt")), "仅删除匹配旧摘要的正式文件");
        Check(JsonSerializer.Deserialize<FileJournal>(File.ReadAllBytes(Path.Combine(good.Transaction, "journal.json")), FileUpdateRules.Json)!.State == "Committed", "成功事务已提交");
        FileTransaction.Rollback(good.Transaction, good.Install);
        Check(File.ReadAllBytes(Path.Combine(good.Install, "a.dll")).SequenceEqual(newBytes), "已提交事务不被误回滚");

        var startupFailure = Fixture();
        Reject(() => FileTransaction.Apply(startupFailure.Install, startupFailure.Payload, Simple(), startupFailure.Transaction,
            () => throw new IOException("synthetic startup failure")), "startup failure");
        Check(OldIntact(startupFailure.Install), "新版启动失败恢复旧文件及已删除文件，移除新增文件");
        Check(File.Exists(Path.Combine(startupFailure.Transaction, "backup", "0")), "恢复后备份保留");

        foreach (var failAt in new[] { 1, 2, 3, 4 })
        {
            var fault = Fixture();
            Reject(() => FileTransaction.Apply(fault.Install, fault.Payload, Simple(), fault.Transaction, () => { },
                index => { if (index == failAt) throw new IOException("synthetic write failure"); }), "write failure");
            Check(OldIntact(fault.Install), "第 " + failAt + " 次写入失败可恢复所有文件");
        }
        var locked = Fixture();
        FileStream? held = null;
        try
        {
            Reject(() => FileTransaction.Apply(locked.Install, locked.Payload, Simple(), locked.Transaction, () => { },
                index => { if (index == 1) held = File.Open(Path.Combine(locked.Install, "b.dll"), FileMode.Open, FileAccess.Read, FileShare.None); }), "locked second file");
        }
        finally { held?.Dispose(); }
        Check(OldIntact(locked.Install), "文件锁定不强杀，恢复此前替换");
        var tampered = Fixture(); Put(tampered.Payload, Hash(newBytes).ToLowerInvariant(), oldBytes);
        Reject(() => FileTransaction.Apply(tampered.Install, tampered.Payload, Simple(), tampered.Transaction, () => { }), "tampered staged file");
        Check(OldIntact(tampered.Install), "坏缓存未写入安装目录");
        var modified = Fixture(); Put(modified.Install, "obsolete.dll", newBytes);
        FileTransaction.Apply(modified.Install, modified.Payload, Simple(), modified.Transaction, () => { });
        Check(File.ReadAllBytes(Path.Combine(modified.Install, "obsolete.dll")).SequenceEqual(newBytes), "用户修改的旧文件被保留");

        var recovery = Fixture();
        FileTransaction.Apply(recovery.Install, recovery.Payload, Simple(), recovery.Transaction, () => { });
        var journalPath = Path.Combine(recovery.Transaction, "journal.json");
        var journal = JsonSerializer.Deserialize<FileJournal>(File.ReadAllBytes(journalPath), FileUpdateRules.Json)!;
        journal.State = "Verifying"; FileUpdateRules.AtomicJson(journalPath, journal); // crash before the final commit record
        FileTransaction.Rollback(recovery.Transaction, recovery.Install);
        Check(OldIntact(recovery.Install), "中断事务可从持久日志恢复");
        Reject(() => FileTransaction.Rollback(recovery.Transaction, Fresh("wrong-root")), "wrong recovery root");
        var changedExternally = Fixture();
        FileTransaction.Apply(changedExternally.Install, changedExternally.Payload, Simple(), changedExternally.Transaction, () => { });
        var externalJournal = Path.Combine(changedExternally.Transaction, "journal.json");
        var j = JsonSerializer.Deserialize<FileJournal>(File.ReadAllBytes(externalJournal), FileUpdateRules.Json)!;
        j.State = "Applying"; FileUpdateRules.AtomicJson(externalJournal, j);
        Put(changedExternally.Install, "a.dll", Encoding.UTF8.GetBytes("external edit"));
        Reject(() => FileTransaction.Rollback(changedExternally.Transaction, changedExternally.Install), "external modification during recovery");
        Check(File.ReadAllText(Path.Combine(changedExternally.Install, "a.dll")) == "external edit", "回滚不覆盖外部修改");
        var noOp = Fixture();
        var unchanged = Simple(); unchanged.Files[0] = Entry("a.dll", oldBytes); unchanged.Remove.Clear();
        Check(FileTransaction.Changed(noOp.Install, unchanged).Count == 2, "按真实摘要识别未变化文件");

        await DownloadChecks();
        Console.WriteLine($"File update checks: {count} passed. Isolated files and simulated HTTP; no installed program, registry or update worker execution.");

        async Task DownloadChecks()
        {
            var required = new List<string> { "DeltaNFD.exe", "DeltaNFD.dll", "DeltaNFD.deps.json", "DeltaNFD.runtimeconfig.json", "DeltaNFD.Shared.dll",
                "DeltaNFD.Contracts.dll", "DeltaNFD.UpdateCore.dll", "DeltaNFD.pri", "Tools/DeploymentControl.ps1", "Backends/DeltaNFD.FileUpdater.exe",
                "Backends/DeltaNFD.FileUpdater.dll", "Backends/DeltaNFD.FileUpdater.deps.json", "Backends/DeltaNFD.FileUpdater.runtimeconfig.json", "Backends/DeploymentControl.ps1" };
            foreach (var name in new[] { "Ace", "Gpu", "Runtime", "SystemMaintenance", "CpuFrame", "Procman" })
                foreach (var ext in new[] { ".exe", ".dll", ".runtimeconfig.json", ".deps.json" }) required.Add("Backends/DeltaNFD.Backend." + name + ext);
            byte[] Gzip(byte[] data)
            {
                using var memory = new MemoryStream();
                using (var compressor = new GZipStream(memory, CompressionLevel.Fastest, true)) compressor.Write(data);
                return memory.ToArray();
            }
            const string metaUrl = "https://deltaxiaoxi.xin/files.json";
            const string objectUrl = "https://deltaxiaoxi.xin/new.gz";
            const string fullUrl = "https://github.com/DRXiaoXi/DeltaNFD/releases/download/v9.0/setup.exe";
            var fileOnly = new UpdateManifest { Version = "9.0", DisplayVersion = "Beta9.0", Channel = "beta", Installers = [],
                FileUpdate = new() { Url = metaUrl, SizeBytes = 123, Sha256 = new string('a', 64) } };
            var fileOnlyService = new UpdateService(UpdateService.LoadConfig(null));
            var fileOnlyCheck = await fileOnlyService.CheckSourcesAsync(UpdateService.LoadConfig(null),
                new AppSettings { UpdateChannel = UpdateChannel.Public }, new Version(1, 0), true,
                (_, _) => Task.FromResult(JsonSerializer.Serialize(fileOnly)));
            Check(fileOnlyCheck.HasUpdate && fileOnlyCheck.Manifest!.Installers!.Count == 0, "自建仅文件清单不依赖整包地址也可发现更新");
            var fullBytes = new byte[65536]; new Random(1).NextBytes(fullBytes);
            async Task<(UpdateDownloadResult Result, List<string> Seen, string Install)> Run(string scenario, UpdateServer server = UpdateServer.Automatic, UpdateMode mode = UpdateMode.ChangedFiles)
            {
                var folder = Fresh("download"); var install = Path.Combine(folder, "install"); Directory.CreateDirectory(install);
                var raw = Encoding.UTF8.GetBytes(new string('n', 512));
                if (scenario == "oversized") { raw = new byte[100000]; new Random(42).NextBytes(raw); }
                var packed = Gzip(raw);
                if (scenario == "bomb") packed = Gzip(new byte[1048576]);
                var files = required.Select(n => Entry(n, oldBytes)).ToList();
                foreach (var file in files)
                {
                    Put(install, file.Path, oldBytes);
                    var compressed = Gzip(oldBytes);
                    file.Compression = "gzip"; file.DownloadSize = compressed.Length; file.DownloadSha256 = Hash(compressed);
                }
                if (scenario == "legacy") File.Delete(Path.Combine(install, "Backends", "DeltaNFD.FileUpdater.exe"));
                var change = files.Single(f => f.Path == "DeltaNFD.dll");
                change.SizeBytes = raw.Length; change.Sha256 = Hash(raw); change.Compression = "gzip";
                change.DownloadSize = packed.Length; change.DownloadSha256 = Hash(packed); change.Urls = [objectUrl];
                var inventory = new FileUpdateManifest { Version = "9.0", DisplayVersion = "Beta9.0", Channel = "beta", Files = files };
                if (scenario == "wrong-version") inventory.Version = "10.0";
                var body = JsonSerializer.SerializeToUtf8Bytes(inventory, FileUpdateRules.Json);
                var release = new UpdateManifest { Version = "9.0", DisplayVersion = "Beta9.0", Channel = "beta",
                    FileUpdate = new() { Url = metaUrl, SizeBytes = body.Length, Sha256 = scenario == "bad-metadata" ? new string('b', 64) : Hash(body) },
                    Installers = [new() { Url = fullUrl, SizeBytes = fullBytes.Length, Sha256 = Hash(fullBytes) }] };
                if (scenario is "no-metadata" or "full-self" or "full-failed" or "full-corrupt" or "no-full") release.FileUpdate = null;
                if (scenario == "no-full") release.Installers = [];
                if (scenario == "full-self") release.Installers = [new() { Url = "https://deltaxiaoxi.xin/updates/packages/9.0/setup.exe", SizeBytes = fullBytes.Length, Sha256 = Hash(fullBytes) }];
                AppSettingsStore.Update(s => { s.UpdateChannel = UpdateChannel.Public; s.UpdateServer = server; s.UpdateMode = mode; });
                var seen = new List<string>();
                using var cancellation = new CancellationTokenSource();
                using var client = new HttpClient(new Handler((request, token) =>
                {
                    var url = request.RequestUri!.AbsoluteUri; seen.Add(url);
                    if (url == metaUrl && scenario == "selection-changed")
                        AppSettingsStore.Update(s => s.UpdateServer = UpdateServer.GitHub);
                    if (url == metaUrl && scenario == "mode-changed") AppSettingsStore.Update(s => s.UpdateMode = UpdateMode.FullInstaller);
                    if (scenario == "cancel-object" && url == objectUrl || scenario == "cancel-full" && url == fullUrl)
                    { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                    if (url == objectUrl && scenario == "offline") throw new HttpRequestException("offline");
                    if (url == fullUrl && scenario == "full-failed") throw new HttpRequestException("full offline");
                    var data = url == metaUrl ? body : url == objectUrl ? scenario == "corrupt" ? new byte[packed.Length] : packed
                        : scenario == "full-corrupt" ? new byte[fullBytes.Length] : fullBytes;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
                }));
                var service = new UpdateService(UpdateService.LoadConfig(null), client, Path.Combine(folder, "cache"), install);
                if (scenario == "cancel") cancellation.Cancel();
                try
                {
                    var result = await service.DownloadAsync(release, cancellationToken: cancellation.Token);
                    if (scenario == "cache")
                    {
                        seen.Clear();
                        result = await service.DownloadAsync(release);
                    }
                    Check(AppSettingsStore.Read().UpdateMode == (scenario == "mode-changed" ? UpdateMode.FullInstaller : mode), "回退不更改持久更新方式：" + scenario);
                    if (result.Success && !result.IsIncremental && mode == UpdateMode.ChangedFiles)
                    {
                        var selection = AppSettingsStore.Read();
                        Check(service.HasFullFallbackPermit(release, result.InstallerPath, selection), "仅此次下载取得全量启动权限：" + scenario);
                        Check(!service.HasFullFallbackPermit(release, result.InstallerPath + ".other", selection), "其他安装包不能套用全量回退许可");
                        var changedSelection = selection.Copy(); changedSelection.UpdateChannel = UpdateChannel.Internal;
                        Check(!service.HasFullFallbackPermit(release, result.InstallerPath, changedSelection), "改变通道后全量回退许可失效");
                        var changedRelease = JsonSerializer.Deserialize<UpdateManifest>(JsonSerializer.Serialize(release))!;
                        changedRelease.Installers![0].Sha256 = new string('f', 64);
                        Check(!service.HasFullFallbackPermit(changedRelease, result.InstallerPath, selection), "不同哈希的同号安装包不能套用许可");
                        service.ResetCheckResult();
                        Check(!service.HasFullFallbackPermit(release, result.InstallerPath, selection), "重置更新选择即清除回退许可");
                    }
                    return (result, seen, install);
                }
                catch (OperationCanceledException) when (scenario.StartsWith("cancel"))
                { Check(scenario == "cancel-full" ? seen.Count(u => u == fullUrl) == 1 : !seen.Contains(fullUrl), "取消不触发或重试完整包回退：" + scenario); return (UpdateDownloadResult.Fail("cancelled"), seen, install); }
            }
            var result = await Run("normal");
            Check(result.Result.Success && result.Result.IsIncremental && result.Seen.SequenceEqual([metaUrl, objectUrl]), "只下载变化文件，不下载未变化运行时和完整包");
            Check(File.ReadAllBytes(Path.Combine(result.Install, "DeltaNFD.dll")).SequenceEqual(oldBytes), "下载阶段不替换正在使用的文件");
            var cache = await Run("cache");
            Check(cache.Result.Success && cache.Result.IsIncremental && cache.Seen.Count == 0, "重试复用已核验文件缓存");
            foreach (var scenario in new[] { "bad-metadata", "wrong-version", "offline", "corrupt", "bomb", "legacy", "no-metadata" })
            {
                var fallback = await Run(scenario);
                Check(fallback.Result.Success && !fallback.Result.IsIncremental && fallback.Seen.Contains(fullUrl), "文件更新失败自动回退完整包：" + scenario);
                Check(File.ReadAllBytes(Path.Combine(fallback.Install, "DeltaNFD.dll")).SequenceEqual(oldBytes), "回退前没有修改应用文件：" + scenario);
            }
            var github = await Run("normal", UpdateServer.GitHub);
            Check(github.Result.Success && !github.Result.IsIncremental && github.Seen.SequenceEqual([fullUrl]), "指定GitHub时回退GitHub整包，不访问自建增量源");
            var own = await Run("normal", UpdateServer.SelfHosted);
            Check(own.Result.Success && own.Result.IsIncremental && own.Seen.SequenceEqual([metaUrl, objectUrl]), "仅自建服务器增量正常");
            var full = await Run("bad-metadata", mode: UpdateMode.FullInstaller);
            Check(full.Result.Success && !full.Result.IsIncremental && full.Seen.SequenceEqual([fullUrl]), "全量更新跳过文件清单和对象下载");
            var fullOwn = await Run("full-self", UpdateServer.SelfHosted, UpdateMode.FullInstaller);
            Check(fullOwn.Result.Success && !fullOwn.Result.IsIncremental && fullOwn.Seen.SequenceEqual(["https://deltaxiaoxi.xin/updates/packages/9.0/setup.exe"]), "自建服务器允许全量更新");
            var fallbackOwn = await Run("full-self", UpdateServer.SelfHosted);
            Check(fallbackOwn.Result.Success && !fallbackOwn.Result.IsIncremental && fallbackOwn.Seen.All(u => u.StartsWith("https://deltaxiaoxi.xin/")), "自建服务器自动回退仍限定自建整包");
            var wrongServer = await Run("no-metadata", UpdateServer.SelfHosted);
            Check(!wrongServer.Result.Success && wrongServer.Seen.Count == 0, "自建服务器无整包时不能跨站下载GitHub");
            var noFull = await Run("no-full");
            Check(!noFull.Result.Success && noFull.Seen.Count == 0, "未提供完整安装包时明确停止");
            var bothFailed = await Run("full-failed", UpdateServer.GitHub);
            Check(!bothFailed.Result.Success && bothFailed.Seen.Count == 1, "全量失败只报告错误，不循环文件更新");
            var corruptFull = await Run("full-corrupt", UpdateServer.GitHub);
            Check(!corruptFull.Result.Success && corruptFull.Seen.Count == 1, "回退整包哈希错误时停止，不能交接安装");
            var larger = await Run("oversized");
            Check(larger.Result.Success && larger.Result.IsIncremental && !larger.Seen.Contains(fullUrl), "用户选择仅替换时体积较大也不擅自换模式");
            var modeChanged = await Run("mode-changed");
            Check(!modeChanged.Result.Success && !modeChanged.Seen.Contains(fullUrl), "下载期间修改方式拒绝迟到结果");
            AppSettingsStore.Update(s => s.UpdateMode = UpdateMode.FullInstaller);
            Check(!(await fileOnlyService.LaunchInstallerAsync(fileOnly, "files.json")).Success, "选择全量后不能启动先前文件更新");
            AppSettingsStore.Update(s => s.UpdateMode = UpdateMode.ChangedFiles);
            Check(!(await fileOnlyService.LaunchInstallerAsync(fileOnly, "fixture.exe")).Success, "选择仅替换后不能启动先前整包");
            fileOnlyService.ResetCheckResult();
            var delayed = new TaskCompletionSource<UpdateCheckResult>();
            var oldCheck = fileOnlyService.CheckAndPublishAsync(AppSettingsStore.Read(), () => delayed.Task);
            AppSettingsStore.Update(s => s.UpdateMode = UpdateMode.FullInstaller);
            delayed.SetResult(new() { Status = UpdateCheckStatus.UpdateAvailable, Manifest = fileOnly });
            Check(!(await oldCheck).HasUpdate, "切换方式丢弃迟到的更新检查结果");
            var switched = await Run("selection-changed");
            Check(!switched.Result.Success && !switched.Seen.Contains(fullUrl), "下载中服务器变化拒绝迟到结果，不跨站回退完整包");
            await Run("cancel");
            await Run("cancel-object");
            // No metadata forces the full download, cancellation is still propagated.
            await Run("cancel-full", UpdateServer.GitHub);
            AppSettingsStore.Update(s => s.UpdateServer = UpdateServer.Automatic);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handle) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(handle(request, token)); }
}
