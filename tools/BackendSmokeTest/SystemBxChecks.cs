using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using DeltaNFD.Services.TweakDb;

namespace BackendSmokeTest;

/// <summary>
/// T3 系统维护迁移检查：验证「伪造/未知 Id 与坏参数被拒绝」「缺失资产不伪造条目」
/// 「备份读取失败如实标记为未知而不是零」以及扩展库/内存只读命令由真实后端返回。
/// 全部使用隔离数据根与只读调用，不写入真实系统（不调用 bx.apply / memory.purge / defender 实际查询）。
/// </summary>
internal static class SystemBxChecks
{
    private static int _passed;

    public static async Task RunAsync()
    {
        _passed = 0;
        LogicChecks();
        MissingAssetChecks();
        await BackupProvenanceChecksAsync();
        await BackendChecksAsync();
        Console.WriteLine($"系统维护 T3 检查全部通过（{_passed} 项断言）。");
    }

    private static void LogicChecks()
    {
        Check(BxCatalog.FindItem("", "") is null, "空的栏目标识/条目名不解析");
        Check(BxCatalog.FindItem("__evil__", "HAGS") is null, "未知栏目不解析条目");
        Check(BxCatalog.FindItem("basic", "__forged__") is null, "未知条目名不解析");
        Check(BxCatalog.FindServiceGroup("__evil__") is null, "未知服务组 Id 不解析");
        Check(BxCatalog.FindServiceGroup("svc-print") is not null, "内置服务组 Id 可解析");
    }

    /// <summary>资产缺失时数据库必须为空（不伪造条目、不返回可执行目标）。</summary>
    private static void MissingAssetChecks()
    {
        var savedRoot = BxCatalog.AssetRoot;
        var emptyRoot = Path.Combine(Path.GetTempPath(), "DeltaNFD_NoAssets_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(emptyRoot);
            BxCatalog.ConfigureAssetRoot(emptyRoot);
            Check(BxCatalog.FindItem("basic", "HAGS") is null, "资产缺失时条目解析为空");
            Check(BxCatalog.Database.Values.All(list => list.Count == 0), "资产缺失时数据库栏目均为空");
            Check(BxCatalog.AllServices.Count == 0, "资产缺失时服务数据库为空");
        }
        finally
        {
            BxCatalog.ConfigureAssetRoot(savedRoot);
            try { Directory.Delete(emptyRoot, recursive: true); } catch { /* 清理失败不影响结果 */ }
        }
    }

    /// <summary>备份读取区分「合法无备份」与「读取失败」，失败绝不冒充零/无备份。</summary>
    private static async Task BackupProvenanceChecksAsync()
    {
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD_SystemBx_" + id.ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);

        var backupFile = Path.Combine(AppDataPaths.Root, "optimize_backup.json");

        // 合法：文件不存在 = 无备份（Readable=true, Count=0）。
        var noBackup = await new BxService().GetBackupStatusAsync();
        Check(noBackup.Readable && noBackup.Count == 0, "无备份文件时如实报告可读且为零条");

        // 损坏：读取失败必须标记为未知，且不得返回伪造计数。
        File.WriteAllText(backupFile, "{ 这不是合法 JSON");
        var corrupt = await new BxService().GetBackupStatusAsync();
        Check(!corrupt.Readable, "损坏备份被标记为不可读（未知）");
        Check(corrupt.Error.Length > 0, "损坏备份给出读取失败原因");
        Check(corrupt.Count == 0 && corrupt.Entries.Count == 0, "读取失败不返回伪造条目");
        Check(!BxBackupStore.TryReadAll(out _, out var error) && error.Length > 0, "严格读取损坏备份返回失败与原因");

        try { Directory.Delete(root, recursive: true); } catch { /* 清理失败不影响结果 */ }
    }

    private static async Task BackendChecksAsync()
    {
        using var isolation = Isolation.Create();
        await using var client = await BackendClient.StartAsync(BackendModules.System, CancellationToken.None);

        // ---- 伪造 / 未知 Id 一律拒绝 ----
        Check((await client.InvokeAsync(BackendCommands.SystemBxStatus,
            new BxStatusArgs { Items = [new BxItemRef { SectionId = "basic", Name = "__forged__" }] },
            null, CancellationToken.None)).Status == BackendResultStatuses.Failed, "伪造条目名被拒绝");
        Check((await client.InvokeAsync(BackendCommands.SystemBxStatus,
            new BxStatusArgs { Items = [new BxItemRef { SectionId = "__evil__", Name = "HAGS" }] },
            null, CancellationToken.None)).Status == BackendResultStatuses.Failed, "伪造栏目 Id 被拒绝");
        Check((await client.InvokeAsync(BackendCommands.SystemBxStatus, new BxStatusArgs(), null, CancellationToken.None)).Status
            == BackendResultStatuses.Failed, "空条目列表被拒绝");
        Check((await client.InvokeAsync(BackendCommands.SystemBxApply,
            new BxApplyArgs { SectionId = "__evil__", Name = "x", TurnOn = true }, null, CancellationToken.None)).Status
            == BackendResultStatuses.Failed, "伪造条目应用被拒绝");
        Check((await client.InvokeAsync(BackendCommands.SystemBxGearApply,
            new BxGearApplyArgs { SectionId = "basic", Name = "__forged__", GearIndex = 0 }, null, CancellationToken.None)).Status
            == BackendResultStatuses.Failed, "伪造条目档位切换被拒绝");
        Check((await client.InvokeAsync(BackendCommands.SystemBxServiceGroupStatus,
            new BxServiceGroupArgs { GroupId = "__evil__" }, null, CancellationToken.None)).Status
            == BackendResultStatuses.Failed, "伪造服务组状态读取被拒绝");
        Check((await client.InvokeAsync(BackendCommands.SystemBxServiceGroupApply,
            new BxServiceGroupApplyArgs { GroupId = "__evil__", TurnOn = true }, null, CancellationToken.None)).Status
            == BackendResultStatuses.Failed, "伪造服务组应用被拒绝");
        Check((await client.InvokeAsync(BackendCommands.SystemBxCacheInvalidate,
            new BxCacheInvalidateArgs { What = "evil" }, null, CancellationToken.None)).Status
            == BackendResultStatuses.Failed, "未知缓存类型被拒绝");
        Check((await client.InvokeAsync(BackendCommands.SystemOptimizeRun,
            new OptimizeRunArgs(), null, CancellationToken.None)).Status
            == BackendResultStatuses.Failed, "一键优化未选择步骤被拒绝");
        Check((await client.InvokeAsync(BackendCommands.SystemDefenderPreflight,
            new DefenderPreflightArgs(), null, CancellationToken.None)).Status
            == BackendResultStatuses.Failed, "Defender 预检空批次被拒绝（不触发查询）");

        // ---- 合法只读命令由真实后端按自带数据库/系统读取返回 ----
        var valid = await client.InvokeAsync(BackendCommands.SystemBxStatus,
            new BxStatusArgs { Items = [new BxItemRef { SectionId = "basic", Name = "HAGS" }] }, null, CancellationToken.None);
        Check(valid.Status == BackendResultStatuses.Ok && valid.Data is not null, "合法条目状态读取成功（后端自带数据库解析）");
        Check(BackendJson.Deserialize<List<BxItemStateDto>>(valid.Data!.Value).Single().Name == "HAGS", "回读条目名一致");

        var resolve = await client.InvokeAsync(BackendCommands.SystemBxServiceGroupResolve,
            new BxServiceGroupArgs { GroupId = "svc-hdd" }, null, CancellationToken.None);
        Check(resolve.Status == BackendResultStatuses.Ok && resolve.Data is not null, "内置服务组可展开");

        // 多档位条目经 IPC 往返：验证 BxGearInfo 的 required/init 成员可反序列化。
        var gear = await client.InvokeAsync(BackendCommands.SystemBxGearInfo,
            new BxItemArgs { SectionId = "custom", Name = "ContextMenuDelay" }, null, CancellationToken.None);
        Check(gear.Status == BackendResultStatuses.Ok && gear.Data is not null, "多档位条目档位信息返回 ok");
        var gearInfo = BackendJson.Deserialize<BxGearInfo>(gear.Data!.Value);
        Check(gearInfo.Options.Count >= 2, "档位选项经 IPC 往返完整");

        var memory = await client.InvokeAsync(BackendCommands.SystemMemoryStatus, new { }, null, CancellationToken.None);
        Check(memory.Status == BackendResultStatuses.Ok && memory.Data is not null, "system.memory.status 返回 ok");
        Check(BackendJson.Deserialize<MemoryStatusDto>(memory.Data!.Value).TotalPhys > 0, "物理内存总量为正（真实只读读取）");

        var backups = await client.InvokeAsync(BackendCommands.SystemBxBackups, new { }, null, CancellationToken.None);
        Check(backups.Status == BackendResultStatuses.Ok && backups.Data is not null, "system.bx.backups 返回 ok");
        var backupStatus = BackendJson.Deserialize<BxBackupStatus>(backups.Data!.Value);
        Check(backupStatus.Readable && backupStatus.Count == 0, "隔离数据根下无可读扩展库备份（零条且可读）");

        await client.DrainAsync(TimeSpan.FromSeconds(10));
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
            throw new Exception("系统维护 T3 检查失败：" + description);
        _passed++;
    }

    /// <summary>为后端子进程设置隔离数据根（临时目录 + 标记文件 + 环境变量），Dispose 时恢复并清理。</summary>
    private sealed class Isolation : IDisposable
    {
        private readonly string _root;
        private readonly string? _previousRoot;
        private readonly string? _previousId;

        private Isolation(string root, Guid id)
        {
            _root = root;
            _previousRoot = Environment.GetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT");
            _previousId = Environment.GetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ID");
            Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT", root);
            Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ID", id.ToString("D"));
        }

        public static Isolation Create()
        {
            var id = Guid.NewGuid();
            var root = Path.Combine(Path.GetTempPath(), "DeltaNFD_SystemBxBackend_" + id.ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
            return new Isolation(root, id);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT", _previousRoot);
            Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ID", _previousId);
            try
            {
                var full = Path.GetFullPath(_root);
                var temp = Path.GetFullPath(Path.GetTempPath());
                if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) Directory.Delete(full, recursive: true);
            }
            catch
            {
                // 清理失败不影响测试结果
            }
        }
    }
}
