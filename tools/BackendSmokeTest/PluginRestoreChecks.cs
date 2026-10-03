using System.Diagnostics;
using DeltaNFD.Services;
using DeltaNFD.Services.Plugins;

namespace BackendSmokeTest;

/// <summary>
/// 拓展插件备份与恢复验证（待做清单第六节第 5 项）。
/// 覆盖：备份落账（mutating + backupIds）、状态机流转、恢复成功/失败/重试、
/// 恢复不删记录、资源冲突检测（内置功能/其他已授权插件）、无备份时恢复。
 /// 真实测试后端子进程执行；临时目录；不修改系统。
/// </summary>
internal static class PluginRestoreChecks
{
    private static int _failures;
    private static int _checks;

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD_PluginRestore_" + Guid.NewGuid().ToString("N"));
        var pluginsRoot = Path.Combine(root, "Plugins");
        var backupsPath = Path.Combine(root, "backups.json");
        Directory.CreateDirectory(pluginsRoot);
        try
        {
            var backups = new PluginBackupStore(backupsPath);
            await RunStoreChecks(backups);
            await RunRestoreFlowAsync(pluginsRoot, backups);
            await RunConflictChecks(pluginsRoot);
            await RunManagedExecutionChecks(Path.Combine(root, "Managed", "Plugins"));
        }
        finally
        {
            PluginTestCleanup.StopOwnedProcesses(root);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Console.WriteLine($"插件备份恢复检查完成：{_checks} 项，失败 {_failures} 项。");
        if (_failures > 0) Environment.ExitCode = 1;
    }

    private static void Check(string name, bool condition, string? detail = null)
    {
        _checks++;
        if (condition) return;
        _failures++;
        Console.WriteLine($"  [失败] {name}{(detail is null ? "" : "：" + detail)}");
    }

    // ---------- 仓库与状态机 ----------

    private static Task RunStoreChecks(PluginBackupStore backups) => Task.Run(() =>
    {
        Console.WriteLine("== 备份仓库 ==");
        Check("空仓库列表", backups.TryList(out var empty, out _) && empty.Count == 0);

        var entry = ValidEntry("bk-1");
        Check("记录备份", backups.TryRecord(entry, out var error), error);
        Check("重复 backupId 拒绝", !backups.TryRecord(entry, out var dupError) && dupError.Contains("已存在"));

        Check("PendingRestore→Restoring", backups.TryUpdateStatus("bk-1", PluginBackupStatus.Restoring, "发出", out _, out _));
        Check("Restoring→Restored", backups.TryUpdateStatus("bk-1", PluginBackupStatus.Restored, "完成", out _, out _));
        Check("Restored→Restoring 拒绝（不重开已恢复项）",
            !backups.TryUpdateStatus("bk-1", PluginBackupStatus.Restoring, "again", out _, out _));

        var retry = ValidEntry("bk-2");
        Check("记录第二条", backups.TryRecord(retry, out _));
        Check("PendingRestore→Restored 直接跳变拒绝",
            !backups.TryUpdateStatus("bk-2", PluginBackupStatus.Restored, "skip", out _, out _));
        Check("PendingRestore→RestoreFailed 拒绝（需先 Restoring）",
            !backups.TryUpdateStatus("bk-2", PluginBackupStatus.RestoreFailed, "skip", out _, out _));
        Check("PendingRestore 列表过滤",
            backups.TryListPending("org.example.restore-probe", out var pending, out _) && pending.Count == 1 &&
            pending[0].BackupId == "bk-2");
        Check("未知 backupId 更新拒绝", !backups.TryUpdateStatus("ghost", PluginBackupStatus.Restoring, "", out _, out _));

        // 失败重试路径
        Check("Restoring→RestoreFailed", backups.TryUpdateStatus("bk-2", PluginBackupStatus.Restoring, "go", out _, out _) &&
            backups.TryUpdateStatus("bk-2", PluginBackupStatus.RestoreFailed, "第三方冲突", out _, out _));
        Check("RestoreFailed→Restoring 重试",
            backups.TryUpdateStatus("bk-2", PluginBackupStatus.Restoring, "retry", out _, out _));
        Check("非法记录拒绝", !backups.TryRecord(ValidEntry("bk-bad") with { ResourceId = "magic.power" }, out _));
        var other = ValidEntry("bk-1") with { PluginId = "org.example.other" };
        Check("不同插件同名备份允许", backups.TryRecord(other, out _));
        Check("旧查询遇到歧义拒绝", !backups.TryGet("bk-1", out _, out _));
        Check("旧更新遇到歧义拒绝", !backups.TryUpdateStatus("bk-1", PluginBackupStatus.Restoring, "unsafe", out _, out _));
        Check("带插件 ID 更新隔离", backups.TryUpdateStatus(other.PluginId, "bk-1", PluginBackupStatus.Restoring, "scoped", out _, out _));
        Check("另一个插件状态不变", backups.TryGet(entry.PluginId, "bk-1", out var untouched, out _) && untouched!.Status == PluginBackupStatus.Restored);
        foreach (var id in new[] { entry.PluginId, other.PluginId })
        {
            backups.TryRecord(ValidEntry("shared-backend") with { PluginId = id }, out _);
            backups.TryRecord(ValidEntry("shared-intent") with { PluginId = id, IsInvocationIntent = true }, out _);
        }
        Check("旧执行意图查询拒绝歧义", !backups.TryResolveIntent("shared-intent", ["shared-backend"], out _));
        Check("带插件 ID 收敛自己的意图", backups.TryResolveIntent(entry.PluginId, "shared-intent", ["shared-backend"], out _));
        Check("另一个插件执行意图保留", backups.TryGet(other.PluginId, "shared-intent", out var otherIntent, out _) && otherIntent!.IsInvocationIntent);

        // 损坏 fail-closed
        File.WriteAllText(backups.PathName, "{ broken");
        Check("损坏仓库 fail-closed", !backups.TryList(out _, out var corrupt) && corrupt.Contains("损坏"));
        File.Delete(backups.PathName);
        Console.WriteLine("  仓库检查完成。");
    });

    // ---------- 恢复执行流 ----------

    private static async Task RunRestoreFlowAsync(string pluginsRoot, PluginBackupStore backups)
    {
        Console.WriteLine("== 恢复执行流 ==");
        const string pluginId = "org.example.restore-probe";
        var runtime = new PluginRuntimeService(backups, dataRoot: Path.Combine(Path.GetDirectoryName(pluginsRoot)!, "PluginData"));

        // 1. mutating invoke 落账备份
        var entry = MakeEntry(pluginsRoot, "normal", pluginId, "1.0.0", authorized: true);
        var tune = await runtime.InvokeOnceAsync(entry, "tune", new Dictionary<string, object?>());
        Check("修改操作成功", tune.Succeeded && tune.PendingRestore, tune.Code);
        Check("备份已落账", backups.TryGet("bk-test-1", out var recorded, out _) && recorded is
            { Status: PluginBackupStatus.PendingRestore, ResourceId: "cpu.sets" });
        Check("备份含插件身份", recorded!.PluginId == pluginId && recorded.PluginVersion == "1.0.0");
        var repeat = await runtime.InvokeOnceAsync(entry, "tune", new Dictionary<string, object?>());
        Check("持有待恢复备份时拒绝重复操作", repeat.Code == "RESTORE_REQUIRED" && repeat.PendingRestore);

        // 2. 恢复成功：状态 → Restored；记录保留
        var restoreSummary = await runtime.RestoreAsync(entry);
        Check("恢复完成文案", restoreSummary.Contains("恢复完成"), restoreSummary);
        Check("恢复后状态 Restored", backups.TryGet("bk-test-1", out var restored, out _) &&
            restored!.Status == PluginBackupStatus.Restored);
        Check("恢复后记录保留（审计）", backups.TryList(out var all, out _) &&
            all.Any(b => b.BackupId == "bk-test-1"));

        // 3. 无待恢复时恢复提示
        var nothing = await runtime.RestoreAsync(entry);
        Check("无待恢复提示", nothing.Contains("没有待恢复"));

        // 4. 恢复失败（第三方冲突）：标记 RestoreFailed、记录保留；重试成功路径
        var failEntry = MakeEntry(pluginsRoot, "restorefail", pluginId, "1.0.1", authorized: true);
        var failedTune = await runtime.InvokeOnceAsync(failEntry, "tune", new Dictionary<string, object?>());
        Check("第二次修改成功（新 backupId）", failedTune.Succeeded, failedTune.Code);
        var failedRestore = await runtime.RestoreAsync(failEntry);
        Check("恢复失败文案含冲突", failedRestore.Contains("RESTORE_FAILED"), failedRestore);
        Check("失败后状态 RestoreFailed", backups.TryGet("bk-test-2", out var failed, out _) &&
            failed!.Status == PluginBackupStatus.RestoreFailed);
        Check("失败记录保留", backups.TryListPending(pluginId, out var stillPending, out _) &&
            stillPending.Any(b => b.BackupId == "bk-test-2"));

        // 5. 失败重试：换正常后端恢复成功
        var wrongVersion = MakeEntry(pluginsRoot, "normal", pluginId, "1.0.2", authorized: true);
        Check("另一版本不能处理原备份", (await runtime.RestoreAsync(wrongVersion)).Contains("身份"));
        var retryEntry = MakeEntry(pluginsRoot, "normal", pluginId, "1.0.1", authorized: true);
        var retryRestore = await runtime.RestoreAsync(retryEntry);
        Check("失败重试恢复成功", retryRestore.Contains("恢复完成"), retryRestore);
        Check("重试后状态 Restored", backups.TryGet("bk-test-2", out var retried, out _) &&
            retried!.Status == PluginBackupStatus.Restored);

        // 6. 后端起不来时：记录保持、不抛异常
        var deadEntry = MakeEntry(pluginsRoot, "normal", pluginId, "1.0.3", authorized: true) with
        {
            EntryExecutable = Path.Combine(pluginsRoot, pluginId, "1.0.3", "backend", "Ghost.exe"),
        };
        var extra = await runtime.InvokeOnceAsync(deadEntry, "tune", new Dictionary<string, object?>());
        Check("死入口拒绝", !extra.Succeeded);

        var bad = MakeEntry(pluginsRoot, "badrestore", "org.example.badrestore", "1.0.0", authorized: true);
        Check("畸形恢复夹具先记录备份", (await runtime.InvokeOnceAsync(bad, "tune", new Dictionary<string, object?>())).Succeeded);
        var badSummary = await runtime.RestoreAsync(bad);
        Check("畸形恢复响应不抛异常", badSummary.Contains("恢复未确认"), badSummary);
        Check("畸形恢复仍保留义务", backups.TryListPending(bad.Id, out var badPending, out _) && badPending.Count > 0 && !runtime.IsRunning(bad.Id));
        foreach (var mode in new[] { "cancelbackup", "cancelwrong" })
        {
            var cancel = MakeEntry(pluginsRoot, mode, "org.example." + mode, "1.0.0", authorized: true);
            var cancelled = await runtime.InvokeOnceAsync(cancel, "tune", new Dictionary<string, object?>());
            backups.TryListPending(cancel.Id, out var remaining, out _);
            if (mode == "cancelbackup")
            {
                Check("取消返回备份可靠落盘", cancelled.Status == "cancelled" && remaining.Count == 1 && !remaining[0].IsInvocationIntent && remaining[0].BackupId == "bk-cancel", cancelled.Code);
                Check("取消后可以恢复", (await runtime.RestoreAsync(cancel)).Contains("恢复完成"));
            }
            else Check("错误会话取消不接受备份", cancelled.Code == "RESULT_UNKNOWN" && remaining.Count == 1 && remaining[0].IsInvocationIntent, cancelled.Code);
        }
        Console.WriteLine("  恢复流检查完成。");
    }

    // ---------- 冲突检测 ----------

    private static Task RunConflictChecks(string pluginsRoot) => Task.Run(() =>
    {
        Console.WriteLine("== 资源冲突 ==");
        var manager = new PluginManagerService(pluginsRoot);
        const string pluginId = "org.example.restore-probe";
        var entry = manager.Index.TryGet(pluginId, out var found, out _) && found is not null
            ? found
            : MakeEntry(pluginsRoot, "normal", pluginId, "1.0.0", authorized: true);
        manager.Index.TryUpsert(entry with { State = PluginPackageState.Authorized }, out _);

        var builtin = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cpu.sets"] = "脱机模式 CPU 调度",
        };
        var conflict = manager.FindResourceConflict(pluginId, ["cpu.sets"], builtin);
        Check("内置功能冲突提示", conflict.Contains("cpu.sets") && conflict.Contains("脱机模式"), conflict);

        var none = manager.FindResourceConflict(pluginId, ["hardware.read"], builtin);
        Check("只读资源无冲突", none.Length == 0, none);

        // 另一已授权插件声明相同修改资源
        var otherId = "org.example.other-plugin";
        var other = MakeEntry(pluginsRoot, "normal", otherId, "1.0.0", authorized: true);
        manager.Index.TryUpsert(other with { State = PluginPackageState.Authorized }, out _);
        var pluginConflict = manager.FindResourceConflict(pluginId, ["cpu.sets"], EmptyOwners);
        Check("插件间冲突提示", pluginConflict.Contains(otherId) && pluginConflict.Contains("cpu.sets"), pluginConflict);

        // 未授权插件不参与冲突判定
        manager.Index.TryUpsert(other with { State = PluginPackageState.ImportedDisabled }, out _);
        var disabledConflict = manager.FindResourceConflict(pluginId, ["cpu.sets"], EmptyOwners);
        Check("未授权插件不算冲突", disabledConflict.Length == 0, disabledConflict);
        Check("硬亲和性与 CPU Sets 同属 CPU 冲突", manager.FindResourceConflict(pluginId, ["cpu.affinity"], builtin).Contains("cpu.sets"));
        Console.WriteLine("  冲突检查完成。");
    });

    private static readonly IReadOnlyDictionary<string, string> EmptyOwners =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static async Task RunManagedExecutionChecks(string pluginsRoot)
    {
        var manager = new PluginManagerService(pluginsRoot);
        var entry = MakeEntry(pluginsRoot, "normal", "org.example.managed", "1.0.0", authorized: true)
            with { AuthorizedUtc = DateTimeOffset.UtcNow };
        manager.Index.TryUpsert(entry, out _);
        var runtime = new PluginRuntimeService(manager.Backups, manager.Handoffs, manager.DataRoot, manager.Authorizations, manager)
        { BuiltinResourceOwners = () => new Dictionary<string, string> { ["cpu.affinity"] = "测试锁核" } };
        var blocked = await runtime.InvokeOnceAsync(entry, "tune", new Dictionary<string, object?>());
        Check("真实调用在启动后端前拒绝内置资源冲突", blocked.Code == "RESOURCE_CONFLICT" && !runtime.IsRunning(entry.Id));
        runtime.BuiltinResourceOwners = () => EmptyOwners;
        var result = await runtime.InvokeOnceAsync(entry, "tune", new Dictionary<string, object?>());
        Check("服务无需页面即持久化待恢复状态", result.PendingRestore && manager.Index.TryGet(entry.Id, out var current, out _) &&
            current!.State == PluginPackageState.PendingRestore);
        var repeated = await runtime.InvokeOnceAsync(entry, "tune", new Dictionary<string, object?>());
        Check("陈旧授权快照不能绕过待恢复门禁", repeated.Code == "RESTORE_REQUIRED" && !runtime.IsRunning(entry.Id));
        Check("持久化待恢复后仍可恢复", (await runtime.RestoreAsync(entry)).Contains("恢复完成") && manager.TryConfirmRestored(entry.Id, out _));
        var other = MakeEntry(pluginsRoot, "normal", "org.example.conflicting", "1.0.0", authorized: true);
        manager.Index.TryUpsert(other, out _);
        blocked = await runtime.InvokeOnceAsync(entry, "tune", new Dictionary<string, object?>());
        Check("真实调用拒绝其他插件的资源冲突", blocked.Code == "RESOURCE_CONFLICT" && !runtime.IsRunning(entry.Id), blocked.Code + ": " + blocked.Message);
    }

    private static PluginBackupEntry ValidEntry(string backupId) => new()
    {
        BackupId = backupId,
        PluginId = "org.example.restore-probe",
        PluginVersion = "1.0.0",
        PackageSha256 = new string('a', 64),
        OperationId = "tune",
        ResourceId = "cpu.sets",
        CreatedUtc = DateTimeOffset.UtcNow.ToString("O"),
        OriginalValueJson = "{\"value\":\"X\"}",
        LastWrittenValueJson = "{\"value\":\"Y\"}",
    };

    /// <summary>复用运行时检查的夹具构造（manifest 含 report + tune 修改操作）。</summary>
    private static PluginIndexEntry MakeEntry(string pluginsRoot, string mode, string id, string version,
        bool authorized)
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
            .Replace("\"timeoutSeconds\": 30", "\"timeoutSeconds\": " + (mode.StartsWith("cancel", StringComparison.Ordinal) ? 1 : 30)));
return PluginTestPackage.Seal(new PluginIndexEntry
        {
            Id = id,
            Version = version,
            PackageSha256 = new string('a', 64),
            InstallDirectory = install,
            EntryExecutable = target,
            Name = "恢复探针",
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
          "name": "恢复探针",
          "author": "Smoke",
          "version": "VERSION",
          "protocolVersion": "1.0",
          "hostCompatibility": { "minInclusive": "0.83.0", "maxExclusive": "1.0.0" },
          "backend": { "entry": "backend/PluginChild.exe", "architecture": "x64" },
          "permissions": [
            { "id": "hardware.read", "purpose": "只读探针" },
            { "id": "system.modify", "purpose": "恢复流测试" }
          ],
          "capabilities": { "continuous": false, "offlineAutonomous": false },
          "operations": [
            { "id": "report", "title": "生成报告", "mutating": false,
              "reversible": false, "targetScoped": false, "timeoutSeconds": 30 },
            { "id": "tune", "title": "调优", "mutating": true,
              "reversible": true, "targetScoped": false, "timeoutSeconds": 30,
              "resourceIds": ["cpu.sets"] }
          ]
        }
        """;
}
