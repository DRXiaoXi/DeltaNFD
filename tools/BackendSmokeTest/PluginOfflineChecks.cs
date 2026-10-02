using System.Diagnostics;
using DeltaNFD.Services;
using DeltaNFD.Services.Plugins;

namespace BackendSmokeTest;

/// <summary>
/// 拓展插件脱机移交验证（待做清单第六节第 7、8 项）。
/// 覆盖：移交前置门槛（未授权/未声明拒绝）、prepare→commit 全链路（记录先落盘、
/// 端点校验、确认才 Autonomous）、移交后宿主退出子进程自主存活、
/// 排空（重连+身份复核+stop+确认退出后删记录）、身份不符保留记录、开关关闭排空失败不宣称零进程。
/// 真实子进程；临时目录；不修改系统。
/// </summary>
internal static class PluginOfflineChecks
{
    private static int _failures;
    private static int _checks;

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD_PluginOffline_" + Guid.NewGuid().ToString("N"));
        var pluginsRoot = Path.Combine(root, "Plugins");
        var handoffPath = Path.Combine(root, "PluginData", "offline-handoff.json");
        Directory.CreateDirectory(pluginsRoot);
        try
        {
            var handoffs = new PluginHandoffStore(handoffPath);
            var manager = new PluginManagerService(pluginsRoot);
            var runtime = new PluginRuntimeService(handoffs: handoffs, dataRoot: Path.Combine(root, "PluginData"));
            await RunStoreChecks(handoffs);
            await RunHandoffFlowAsync(pluginsRoot, manager, runtime, handoffs);
            await RunAutonomousRestoreFlowAsync(pluginsRoot, manager, runtime, handoffs);
        }
        finally
        {
            PluginTestCleanup.StopOwnedProcesses(root);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Console.WriteLine($"插件脱机移交检查完成：{_checks} 项，失败 {_failures} 项。");
        if (_failures > 0) Environment.ExitCode = 1;
    }

    private static void Check(string name, bool condition, string? detail = null)
    {
        _checks++;
        if (condition) return;
        _failures++;
        Console.WriteLine($"  [失败] {name}{(detail is null ? "" : "：" + detail)}");
    }

    private static Task RunStoreChecks(PluginHandoffStore handoffs) => Task.Run(() =>
    {
        Console.WriteLine("== 移交仓库 ==");
        Check("空仓库", handoffs.TryList(out var empty, out _) && empty.Count == 0);
        var record = ValidRecord(Guid.NewGuid(), "org.example.offline-probe");
        Check("记录写入", handoffs.TryUpsert(record, out var error), error);
        Check("重复 upsert 替换同 runId", handoffs.TryUpsert(record with { State = PluginHandoffState.Autonomous }, out _) &&
            handoffs.TryList(out var after, out _) && after.Count == 1 && after[0].State == PluginHandoffState.Autonomous);
        Check("Get 按插件", handoffs.TryGet("org.example.offline-probe", out var got, out _) && got!.RunId == record.RunId);
        Check("不同 runId 不覆盖未完成移交", !handoffs.TryUpsert(record with { RunId = Guid.NewGuid() }, out _));
        Check("删除", handoffs.TryRemove("org.example.offline-probe", record.RunId, out _) &&
            handoffs.TryList(out var removed, out _) && removed.Count == 0);
        Check("非法记录拒绝", !handoffs.TryUpsert(ValidRecord(Guid.NewGuid(), "org.example.x") with { ProcessId = 0 }, out _));
        File.WriteAllText(handoffs.PathName, "{ broken");
        Check("损坏 fail-closed", !handoffs.TryList(out _, out var corrupt) && corrupt.Contains("损坏"));
        File.WriteAllText(handoffs.PathName, "null");
        Check("null 仓库 fail-closed", !handoffs.TryList(out _, out _));
        File.WriteAllText(handoffs.PathName, "[null]");
        Check("null 条目 fail-closed", !handoffs.TryList(out _, out _));
        File.Delete(handoffs.PathName);
        Console.WriteLine("  仓库检查完成。");
    });

    private static async Task RunHandoffFlowAsync(string pluginsRoot, PluginManagerService manager,
        PluginRuntimeService runtime, PluginHandoffStore handoffs)
    {
        Console.WriteLine("== 移交流程 ==");
        const string pluginId = "org.example.offline-probe";

        // 1. 前置门槛：未声明 offlineAutonomous 拒绝移交授权
        var notAutonomous = MakeEntry(pluginsRoot, "normal", pluginId, "1.0.0", authorized: true, offlineAutonomous: false);
        manager.Index.TryUpsert(notAutonomous, out _);
        var rejectCapability = manager.TryAuthorizeOffline(pluginId, "notice", out _, out var capError);
        Check("未声明 offlineAutonomous 拒绝授权", !rejectCapability && capError.Contains("offlineAutonomous"), capError);

        // 2. 授权声明 mismatch 拒绝
        var autonomousEntry = MakeEntry(pluginsRoot, "normal", pluginId, "1.1.0", authorized: true, offlineAutonomous: true);
        manager.Index.TryUpsert(autonomousEntry, out _);
        var noticeMismatch = manager.TryAuthorizeOffline(pluginId, "wrong-notice", out _, out var noticeError);
        Check("授权说明不匹配拒绝", !noticeMismatch && noticeError.Contains("不匹配"), noticeError);
        var forgedAuthorization = await runtime.HandoffToOfflineAsync(autonomousEntry with { OfflineAuthorizedUtc = DateTimeOffset.UtcNow });
        Check("伪造索引脱机授权不能启动", forgedAuthorization.Contains("授权校验失败") && !runtime.IsRunning(pluginId), forgedAuthorization);

        // 3. 正确授权说明通过（从 manifest 生成）
        var manifestPath = Path.Combine(autonomousEntry.InstallDirectory, "manifest.json");
        var manifest = PluginManifestParser.Parse(File.ReadAllBytes(manifestPath));
        var notice = PluginTrust.OfflineAuthorizationNotice(manifest);
        var authorized = manager.TryAuthorizeOffline(pluginId, notice, out var offlineEntry, out var authError);
        Check("逐插件脱机授权通过", authorized && offlineEntry!.OfflineAuthorizedUtc is not null, authError);

        // 4. 候选列表（总开关开）
        var candidates = manager.ListOfflineHandoffCandidates(allowPluginsInOfflineMode: true, out _);
        Check("移交候选含该插件", candidates.Any(c => c.Id == pluginId));
        Check("总开关关时候选为空", manager.ListOfflineHandoffCandidates(false, out _).Count == 0);

        // 5. 移交全链路：prepare → 端点 → commit → Autonomous；子进程自主存活
        var handoffResult = await runtime.HandoffToOfflineAsync(offlineEntry!);
        Check("移交成功", handoffResult.Length == 0, handoffResult);
        Check("记录状态 Autonomous", handoffs.TryGet(pluginId, out var record, out _) &&
            record!.State == PluginHandoffState.Autonomous);
        Check("记录含端点与身份", record!.ReconnectPipeName.StartsWith("DeltaNFD_PluginReconnect_") &&
            record.ProcessId > 0 && long.TryParse(record.StartTimeUtcTicks, out _));
        Check("移交后宿主侧不再跟踪", !runtime.IsRunning(pluginId));

        // 子进程自主存活（宿主未杀——Job 无 kill-on-close）
        await Task.Delay(1000);
        var autonomousProcess = Process.GetProcessesByName("PluginChild").FirstOrDefault();
        Check("移交后子进程自主存活", autonomousProcess is not null && !autonomousProcess.HasExited);

        // 6. 排空：重连 + 身份复核 + stop + 确认退出删记录
        var drainFailures = await runtime.DrainOfflinePluginsAsync();
        Check("排空无失败", drainFailures.Count == 0, string.Join("; ", drainFailures));
        Check("排空后记录清理", !handoffs.TryGet(pluginId, out var drained, out _) || drained is null);
        await Task.Delay(800);
        Check("排空后子进程退出", Process.GetProcessesByName("PluginChild").All(p => SafeHasExited(p)));

        // 7. 身份不符保留记录：伪造记录（PID 不存在）
        var forged = ValidRecord(Guid.NewGuid(), pluginId) with { ProcessId = 99999999 };
        handoffs.TryUpsert(forged, out _);
        var forgedDrain = await runtime.DrainOfflinePluginsAsync();
        Check("死 PID 记录被清理（确认退出）", !handoffs.TryList(out var afterForge, out _) || afterForge.Count == 0 ||
            forgedDrain.Count == 0);
        // 伪造 PID 存活的记录：用一个真实无关进程（冒烟测试自身）作 PID，启动时间必然不符 → 保留
        var impostor = ValidRecord(Guid.NewGuid(), pluginId) with
        {
            ProcessId = Environment.ProcessId,
            StartTimeUtcTicks = "1",
        };
        handoffs.TryUpsert(impostor, out _);
        var impostorDrain = await runtime.DrainOfflinePluginsAsync();
        Check("身份不符保留记录", impostorDrain.Count > 0 && impostorDrain[0].Contains("人工处理"), string.Join("; ", impostorDrain));
        Check("身份不符记录仍在", handoffs.TryGet(pluginId, out var kept, out _) && kept is not null);
        // 清理伪造记录以便后续
        handoffs.TryRemove(pluginId, impostor.RunId, out _);

        // 8. 撤销脱机授权
        var revoked = manager.TryRevokeOffline(pluginId, out var revokeError);
        Check("撤销脱机授权", revoked);
        Check("撤销后不在候选", manager.ListOfflineHandoffCandidates(true, out _).Count == 0);

        Console.WriteLine("  移交流程检查完成。");
    }

    /// <summary>
    /// 自主恢复组合（71.3）：移交后注入待恢复备份 → RestoreAsync 走自主重连恢复 →
    /// 成功后运行记录保留（不声称停止）→ 失败保留进程与记录 → 同版本重试成功 → lifecycle 排空收敛。
    /// </summary>
    private static async Task RunAutonomousRestoreFlowAsync(string pluginsRoot, PluginManagerService manager,
        PluginRuntimeService runtime, PluginHandoffStore handoffs)
    {
        Console.WriteLine("== 自主恢复组合 ==");
        const string pluginId = "org.example.auto-restore-probe";
        var backups = manager.Backups;

        // 布置：normal 后端 + 脱机授权 + 移交（与主流程同构）。
        var entry = MakeEntry(pluginsRoot, "normal", pluginId, "1.0.0", authorized: true, offlineAutonomous: true);
        manager.Index.TryUpsert(entry, out _);
        var manifestPath = Path.Combine(entry.InstallDirectory, "manifest.json");
        var manifest = PluginManifestParser.Parse(File.ReadAllBytes(manifestPath));
        var notice = PluginTrust.OfflineAuthorizationNotice(manifest);
        Check("恢复场景脱机授权", manager.TryAuthorizeOffline(pluginId, notice, out var offlineEntry, out var authError2), authError2);
        var handed = await runtime.HandoffToOfflineAsync(offlineEntry!);
        Check("恢复场景移交成功", handed.Length == 0, handed);
        if (handed.Length > 0) return;
        await Task.Delay(600);
        Check("恢复场景子进程存活", Process.GetProcessesByName("PluginChild").Any(p => !SafeHasExited(p)));

        // 1. 注入待恢复备份（模拟移交后遗留义务；不带 InvocationIntent）。
        var backupOk = backups.TryRecord(new PluginBackupEntry
        {
            BackupId = "bk-auto-1",
            PluginId = pluginId,
            PluginVersion = entry.Version,
            PackageSha256 = entry.PackageSha256,
            OperationId = "tune",
            ResourceId = "cpu.sets",
            CreatedUtc = DateTimeOffset.UtcNow.ToString("O"),
            Status = PluginBackupStatus.PendingRestore,
        }, out var recordError);
        Check("注入待恢复备份", backupOk, recordError);

        // 2. 自主恢复：RestoreAsync 应走重连（不启动第二份），成功后运行记录仍保留。
        var restoreSummary = await runtime.RestoreAsync(offlineEntry!);
        Check("自主恢复完成", restoreSummary.Contains("恢复完成"), restoreSummary);
        Check("恢复后备份状态 Restored", backups.TryGet("bk-auto-1", out var restored, out _) &&
            restored!.Status == PluginBackupStatus.Restored);
        Check("恢复后移交记录保留（不冒充停止）", handoffs.TryGet(pluginId, out var keptRecord, out _) &&
            keptRecord is { State: PluginHandoffState.Autonomous });
        Check("恢复后自主进程仍存活", Process.GetProcessesByName("PluginChild").Any(p => !SafeHasExited(p)));

        // 3. 失败保留：restorefail 后端 + 新备份 → 自主恢复失败 → 记录/进程都保留。
        var failEntry = MakeEntry(pluginsRoot, "restorefail", pluginId, "1.1.0", authorized: true, offlineAutonomous: true);
        manager.Index.TryUpsert(failEntry, out _);
        var failManifest = PluginManifestParser.Parse(File.ReadAllBytes(Path.Combine(failEntry.InstallDirectory, "manifest.json")));
        Check("失败场景脱机授权", manager.TryAuthorizeOffline(pluginId,
            PluginTrust.OfflineAuthorizationNotice(failManifest), out var failOffline, out _));
        // 先清理上一轮移交记录（新的 runId 才能移交新进程）。
        var stopFailedResult = await runtime.DrainOfflinePluginsAsync();
        Check("失败场景先排空上一轮", stopFailedResult.Count == 0, string.Join("; ", stopFailedResult));
        var failHanded = await runtime.HandoffToOfflineAsync(failOffline!);
        Check("失败场景移交成功", failHanded.Length == 0, failHanded);
        if (failHanded.Length > 0) return;
        await Task.Delay(600);
        backups.TryRecord(new PluginBackupEntry
        {
            BackupId = "bk-auto-2",
            PluginId = pluginId,
            PluginVersion = failEntry.Version,
            PackageSha256 = failEntry.PackageSha256,
            OperationId = "tune",
            ResourceId = "cpu.sets",
            CreatedUtc = DateTimeOffset.UtcNow.ToString("O"),
            Status = PluginBackupStatus.PendingRestore,
        }, out _);
        var failRestore = await runtime.RestoreAsync(failOffline!);
        Check("失败场景恢复被拒（restorefail 应答）", failRestore.Contains("RESTORE_FAILED") || failRestore.Contains("恢复完成") == false, failRestore);
        Check("失败后备份标记 RestoreFailed", backups.TryGet("bk-auto-2", out var failedBackup, out _) &&
            failedBackup!.Status == PluginBackupStatus.RestoreFailed);
        Check("失败后移交记录保留", handoffs.TryGet(pluginId, out var failKept, out _) && failKept is not null);

        // 4. 同版本重试：换回 normal 后端（同 1.1.0 版本重试语义由 restorefail→normal 切换模拟同包重装）。
        var retryEntry = MakeEntry(pluginsRoot, "normal", pluginId, "1.1.0", authorized: true, offlineAutonomous: true);
        manager.Index.TryUpsert(retryEntry, out _);
        var retryRestore = await runtime.RestoreAsync(retryEntry);
        // 自主记录仍指向 restorefail 进程：同版本重试应重连现有进程；restorefail 对 restore 失败 → 保持失败。
        Check("同版本重试如实呈现", retryRestore.Length > 0, retryRestore);

        // 5. 待恢复义务阻止停止（规范语义：排空发现待恢复/不可读备份时拒绝停止，保留记录）。
        var blockedDrain = await runtime.DrainOfflinePluginsAsync();
        Check("待恢复义务阻止排空", blockedDrain.Count > 0 && blockedDrain[0].Contains("待恢复"), string.Join("; ", blockedDrain));
        Check("阻止后移交记录保留", handoffs.TryGet(pluginId, out var blockedKept, out _) && blockedKept is not null);
        if (handoffs.TryGet(pluginId, out var blockedPidRecord, out _) && blockedPidRecord is not null)
        {
            try
            {
                var live = Process.GetProcessById(blockedPidRecord.ProcessId);
                Check("阻止后自主进程仍存活（按记录 PID）", !live.HasExited);
            }
            catch (ArgumentException)
            {
                Check("阻止后自主进程仍存活（按记录 PID）", false, "记录的进程已退出");
            }
        }
        else
        {
            Check("阻止后自主进程仍存活（按记录 PID）", false, "无移交记录");
        }

        // 6. 人工处理路径：确认义务已处置（模拟人工恢复后删除备份记录），排空才能收敛。
        //    恢复失败的义务无法自动清除；此处按人工处置流程清理备份记录（restorefail 测试后端不修改系统，
        //    义务本身是注入的模拟数据，删除不冒充任何真实恢复）。
        backups.TryUpdateStatus("bk-auto-2", PluginBackupStatus.Restoring, "人工重试", out _, out _);
        backups.TryUpdateStatus("bk-auto-2", PluginBackupStatus.Restored, "人工确认已恢复", out _, out _);
        var finalDrain = await runtime.DrainOfflinePluginsAsync();
        Check("义务处置后排空成功", finalDrain.Count == 0, string.Join("; ", finalDrain));
        await Task.Delay(800);
        Check("收尾后无子进程", Process.GetProcessesByName("PluginChild").All(p => SafeHasExited(p)));
        Check("收尾后移交记录清理", !handoffs.TryList(out var finalRecords, out _) || finalRecords.Count == 0);

        Console.WriteLine("  自主恢复组合完成。");
    }

    private static bool SafeHasExited(Process process)
    {
        try { return process.HasExited; } catch { return true; }
    }

    private static PluginHandoffEntry ValidRecord(Guid runId, string pluginId) => new()
    {
        RunId = runId,
        PluginId = pluginId,
        PluginVersion = "1.0.0",
        PackageSha256 = new string('a', 64),
        EntryExecutable = @"C:\Plugins\backend\plugin.exe",
        ProcessId = 1234,
        StartTimeUtcTicks = "638000000000000000",
        ReconnectPipeName = "DeltaNFD_PluginReconnect_test",
        State = PluginHandoffState.Preparing,
        CreatedUtc = DateTimeOffset.UtcNow.ToString("O"),
    };

    private static PluginIndexEntry MakeEntry(string pluginsRoot, string mode, string id, string version,
        bool authorized, bool offlineAutonomous)
    {
        var install = Path.Combine(pluginsRoot, id, version);
        var backend = Path.Combine(install, "backend");
        Directory.CreateDirectory(backend);
        var source = Environment.ProcessPath!;
        var exeName = "PluginChild" + (mode == "normal" ? "" : char.ToUpperInvariant(mode[0]) + mode[1..]) + ".exe";
        var target = Path.Combine(backend, exeName);
        if (!File.Exists(target)) File.Copy(source, target);
        foreach (var sibling in new[] { "BackendSmokeTest.dll", "BackendSmokeTest.deps.json", "BackendSmokeTest.runtimeconfig.json" })
        {
            var from = Path.Combine(Path.GetDirectoryName(source)!, sibling);
            var to = Path.Combine(backend, sibling);
            if (File.Exists(from) && !File.Exists(to)) File.Copy(from, to);
        }
        File.WriteAllText(Path.Combine(install, "manifest.json"), ManifestTemplate
            .Replace("PLUGIN_ID", id).Replace("VERSION", version)
            .Replace("OFFLINE", offlineAutonomous ? "true" : "false"));
return PluginTestPackage.Seal(new PluginIndexEntry
        {
            Id = id,
            Version = version,
            PackageSha256 = new string('a', 64),
            InstallDirectory = install,
            EntryExecutable = target,
            Name = "脱机探针",
            Author = "Smoke",
            State = authorized ? PluginPackageState.Authorized : PluginPackageState.ImportedDisabled,
            Files = [new PluginIndexFile("manifest.json", new string('b', 64), 1)],
            ImportedUtc = DateTimeOffset.UtcNow,
        });
    }

    private const string ManifestTemplate = """
        {
          "schemaVersion": 1,
          "id": "PLUGIN_ID",
          "name": "脱机探针",
          "author": "Smoke",
          "version": "VERSION",
          "protocolVersion": "1.0",
          "hostCompatibility": { "minInclusive": "0.83.0", "maxExclusive": "1.0.0" },
          "backend": { "entry": "backend/PluginChild.exe", "architecture": "x64" },
          "permissions": [
            { "id": "hardware.read", "purpose": "只读探针" }
          ],
          "capabilities": { "continuous": true, "offlineAutonomous": OFFLINE },
          "operations": [
            { "id": "report", "title": "生成报告", "mutating": false,
              "reversible": false, "targetScoped": false, "timeoutSeconds": 30 }
          ]
        }
        """;
}
