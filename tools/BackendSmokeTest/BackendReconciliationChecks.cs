using System.Diagnostics;
using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

/// <summary>
/// 未知操作恢复闭环（T1）检查：写前执行意图、结果落定、子进程关联、人工核对状态机。
/// 全部在隔离数据根内使用临时夹具与模拟进程身份，不触碰真实系统状态。
/// </summary>
internal static class BackendReconciliationChecks
{
    public static async Task RunAsync()
    {
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-backend-review", id.ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        void Check(bool value, string name) { if (!value) throw new Exception(name); }
        var source = Path.Combine(root, "official-backend-unconfirmed", "system.json");
        string Fixture(string module, int pid, long ticks, string path, object? children = null, string? command = "system.tweaks.apply") =>
            JsonSerializer.Serialize(new { Module = module, Pid = pid, StartTicks = ticks, Path = path, CreatedUtc = DateTimeOffset.UtcNow, Command = command, Children = children });
        try
        {
            // ---- 活进程：不能解除阻塞 ----
            using var self = Process.GetCurrentProcess();
            BackendUnconfirmedStore.Save("system", self);
            var live = BackendUnconfirmedStore.ReviewAll().Single();
            Check(!live.CanAcknowledge, "活动进程不能解除阻塞");
            try { BackendUnconfirmedStore.AcknowledgeCurrentState(live); throw new Exception("活动记录被移除"); }
            catch (IOException) { }
            Check(BackendUnconfirmedStore.HasLiveBackend("system"), "活进程被识别");

            // ---- 已退出：可供玩家核对（PID 复用与旧记录缺少命令同样按「已退出」处理） ----
            File.WriteAllText(source, Fixture("system", int.MaxValue, 1L, Environment.ProcessPath!, command: null));
            var exited = BackendUnconfirmedStore.ReviewAll().Single();
            Check(exited.CanAcknowledge, "已退出记录可供玩家核对");
            Check(exited.Description.Contains("旧记录未保存", StringComparison.Ordinal), "旧记录缺少命令时如实标注");

            // ---- PID 复用：同一 PID 但启动时间不符 → 视为已退出，可以核对 ----
            File.WriteAllText(source, Fixture("system", self.Id, self.StartTime.ToUniversalTime().Ticks + 12345, Environment.ProcessPath!,
                command: "system.pagefile.set"));
            var reused = BackendUnconfirmedStore.ReviewAll().Single();
            Check(reused.CanAcknowledge, "PID 复用记录可核对");
            Check(reused.RequestId.Length == 0, "旧记录缺少请求关联仍可展示");

            // ---- 记录损坏：继续保留阻塞，不能盲目解除 ----
            File.WriteAllText(source, "{bad");
            Check(!BackendUnconfirmedStore.ReviewAll().Single().CanAcknowledge, "损坏记录不能盲目解除");
            Check(BackendUnconfirmedStore.PendingModules().Count == 1, "损坏记录继续阻止交接");

            // ---- 孤立子进程：后端已退出但安装器仍在运行 → 禁止解除 ----
            File.WriteAllText(source, Fixture("system", int.MaxValue, 1L, Environment.ProcessPath!,
                children: new[] { new { Name = "vc_redist.x64.exe", Pid = self.Id, StartTicks = self.StartTime.ToUniversalTime().Ticks } }));
            var orphan = BackendUnconfirmedStore.ReviewAll().Single();
            Check(!orphan.CanAcknowledge && orphan.ChildrenAlive, "孤立子进程未结束不能解除阻塞");
            Check(!orphan.CanRestore, "子进程未结束不能按备份恢复");

            // ---- 子进程已退出：可核对 ----
            File.WriteAllText(source, Fixture("system", int.MaxValue, 1L, Environment.ProcessPath!,
                children: new[] { new { Name = "vc_redist.x64.exe", Pid = int.MaxValue, StartTicks = 1L } }));
            var childGone = BackendUnconfirmedStore.ReviewAll().Single();
            Check(childGone.CanAcknowledge, "子进程已退出可核对");

            // ---- 确认过期：记录变化后原确认指纹失效 ----
            File.WriteAllText(source, Fixture("system", int.MaxValue, 1L, Environment.ProcessPath!));
            var stale = BackendUnconfirmedStore.ReviewAll().Single();
            File.AppendAllText(source, " ");
            try { BackendUnconfirmedStore.AcknowledgeCurrentState(stale); throw new Exception("旧确认覆盖新记录"); }
            catch (IOException) { }
            Check(File.Exists(source), "确认过期保留证据");
            Check(BackendUnconfirmedStore.PendingModules().Count == 1, "变化后的记录仍待核对");

            // ---- 前端崩溃：只留下写前执行意图，重启后必须核对且不能执行新写入 ----
            File.Delete(source);
            var crashed = Guid.NewGuid();
            BackendUnconfirmedStore.BeginIntent(new BackendUnconfirmedStore.OperationIntent(
                "system", "system.tweaks.apply", crashed, Guid.NewGuid(), "abcd", int.MaxValue, 1, Environment.ProcessPath!));
            var intent = BackendUnconfirmedStore.ReviewAll().Single();
            Check(intent.Phase == BackendUnconfirmedStore.PhaseIntent, "写前意图被登记为执行意图");
            Check(intent.CanAcknowledge, "已退出进程的遗留意图可人工核对");
            try { BackendUnconfirmedStore.BeginIntent(new BackendUnconfirmedStore.OperationIntent(
                "system", "system.tweaks.apply", Guid.NewGuid(), Guid.NewGuid(), "ef01", int.MaxValue, 1, Environment.ProcessPath!));
                throw new Exception("待核对记录存在时仍允许新写入意图"); }
            catch (InvalidOperationException) { }
            // 原进程仍运行 → 复核通过前禁止启动重复执行者
            Check(!BackendUnconfirmedStore.HasLiveBackend("system"), "已退出时不误报活进程");
            File.WriteAllText(source, Fixture("system", self.Id, self.StartTime.ToUniversalTime().Ticks, Environment.ProcessPath!));
            Check(BackendUnconfirmedStore.HasLiveBackend("system"), "活进程仍阻止启动重复执行者");
            try { BackendUnconfirmedStore.Check("system"); throw new Exception("活进程时仍允许启动后端"); }
            catch (InvalidOperationException) { }

            // ---- 结果落定：ok / 已确认取消清除；partial / 需要还原保留 ----
            File.Delete(source);
            var req = Guid.NewGuid();
            BackendUnconfirmedStore.BeginIntent(new BackendUnconfirmedStore.OperationIntent(
                "system", "system.tweaks.apply", req, Guid.NewGuid(), "aa", int.MaxValue, 1, Environment.ProcessPath!));
            BackendUnconfirmedStore.ResolveIntent("system", req, BackendResultStatuses.Ok, pendingRestore: false);
            Check(BackendUnconfirmedStore.PendingModules().Count == 0, "成功结果清除执行意图");
            BackendUnconfirmedStore.BeginIntent(new BackendUnconfirmedStore.OperationIntent(
                "system", "system.tweaks.apply", req, Guid.NewGuid(), "bb", int.MaxValue, 1, Environment.ProcessPath!));
            BackendUnconfirmedStore.ResolveIntent("system", req, BackendResultStatuses.Partial, pendingRestore: false, completedItems: ["a:ok", "b:failed"]);
            var partial = BackendUnconfirmedStore.ReviewAll().Single();
            Check(partial.Phase == BackendUnconfirmedStore.PhaseUnconfirmed && partial.Reason == "partial", "部分完成保留未确认证据");
            Check(partial.Description.Contains("a:ok", StringComparison.Ordinal), "部分完成明细被保留");
            BackendUnconfirmedStore.ResolveIntent("system", Guid.NewGuid(), BackendResultStatuses.Ok, pendingRestore: false);
            Check(BackendUnconfirmedStore.PendingModules().Count == 1, "不匹配的 requestId 不会误删他人证据");

            // ---- 已知 partial（requiresReview=false）：逐项拒绝/已回滚必须正常落定，不得锁死模块 ----
            File.Delete(source);
            var knownPartialReq = Guid.NewGuid();
            BackendUnconfirmedStore.BeginIntent(new BackendUnconfirmedStore.OperationIntent(
                "system", "system.tweaks.apply", knownPartialReq, Guid.NewGuid(), "cc", int.MaxValue, 1, Environment.ProcessPath!));
            BackendUnconfirmedStore.ResolveIntent("system", knownPartialReq, BackendResultStatuses.Partial, pendingRestore: false,
                completedItems: ["a:rejected", "b:failed"], requiresReview: false);
            Check(BackendUnconfirmedStore.PendingModules().Count == 0, "已知部分完成不建立未确认阻塞");
            Check(BackendUnconfirmedStore.ReviewAll().Count == 0, "已知部分完成不进入人工核对队列");

            // ---- 超时：意图提升为未确认并关联子进程 ----
            File.Delete(source);
            Check(BackendUnconfirmedStore.PendingModules().Count == 0, "清理夹具记录");
            var timeoutReq = Guid.NewGuid();
            BackendUnconfirmedStore.BeginIntent(new BackendUnconfirmedStore.OperationIntent(
                "system", "system.pagefile.set", timeoutReq, Guid.NewGuid(), "cc", int.MaxValue, 1, Environment.ProcessPath!));
            BackendUnconfirmedStore.PromoteIntent("system", timeoutReq, "timeout", int.MaxValue, 1, Environment.ProcessPath!,
                "system.pagefile.set", [], childrenUnknown: false);
            var promoted = BackendUnconfirmedStore.ReviewAll().Single();
            Check(promoted.Phase == BackendUnconfirmedStore.PhaseUnconfirmed && promoted.Reason == "timeout", "超时意图提升为未确认");
            Check(promoted.Description.Contains("超时"), "超时原因如实展示");

            // ---- 恢复失败/重试：复核不通过保留阻塞；通过后才归档 ----
            var retryReview = BackendUnconfirmedStore.ReviewAll().Single();
            try { BackendUnconfirmedStore.ArchiveAfterRecovery(retryReview, "tweaks", verified: false, "未通过复核", null);
                throw new Exception("复核失败仍被归档"); }
            catch (IOException) { }
            Check(File.Exists(source), "恢复失败保留原始证据");
            Check(BackendUnconfirmedStore.ReviewAll().Single().CanRestore, "恢复失败后仍可重试");
            var snapshot = new ReconcileSnapshot { Module = "system", Summary = "复核通过", Complete = true };
            var archived = BackendUnconfirmedStore.ArchiveAfterRecovery(
                BackendUnconfirmedStore.ReviewAll().Single(), "tweaks", verified: true, "已还原", snapshot);
            Check(File.Exists(archived), "恢复成功归档复核证据");
            Check(!File.Exists(source), "恢复成功后原始记录归档而非删除");
            Check(BackendUnconfirmedStore.PendingModules().Count == 0, "恢复成功后解除阻塞");

            // ---- 记录路径被目录占用（写入失败）：继续阻止交接 ----
            var blockedModule = BackendModules.Gpu;
            Directory.CreateDirectory(BackendUnconfirmedStore.RecordPathForTests(blockedModule));
            try { BackendUnconfirmedStore.BeginIntent(new BackendUnconfirmedStore.OperationIntent(
                blockedModule, "gpu.spoof.apply", Guid.NewGuid(), Guid.NewGuid(), "dd", int.MaxValue, 1, Environment.ProcessPath!));
                throw new Exception("记录路径被目录占用时仍写入"); }
            catch (IOException) { }
            Check(BackendUnconfirmedStore.PendingModules().Contains(blockedModule), "写入失败仍阻止交接");
            Directory.Delete(BackendUnconfirmedStore.RecordPathForTests(blockedModule));

            // ---- 命令分类：恢复/核对命令不登记新意图，写入命令必须登记 ----
            Check(BackendModules.RequiresWriteIntent(BackendModules.Ace, BackendCommands.AceClean), "清理命令需要写前意图");
            Check(!BackendModules.RequiresWriteIntent(BackendModules.Ace, BackendCommands.AceScan), "只读扫描不登记意图");
            Check(!BackendModules.RequiresWriteIntent(BackendModules.Ace, BackendCommands.AceReconcile), "只读核对不登记意图");
            Check(BackendModules.IsMutating(BackendModules.Gpu, BackendCommands.GpuSpoofApply), "伪装命令被视为写入");
            Check(!BackendModules.RequiresWriteIntent(BackendModules.Gpu, BackendCommands.GpuRecover), "恢复命令不登记新意图");
            Check(!BackendModules.RequiresWriteIntent(BackendModules.System, BackendCommands.SystemRecover), "系统恢复命令不登记新意图");

            // Known static backups are not unfinished transactions, including old pendingRestore results.
            var executable = Path.Combine(root, BackendModules.Require(BackendModules.System).ExecutableName);
            foreach (var command in new[] { BackendCommands.SystemBxApply, BackendCommands.SystemBxGearApply, BackendCommands.SystemBxServiceGroupApply })
            {
                var request = Guid.NewGuid();
                BackendUnconfirmedStore.BeginIntent(new("system", command, request, Guid.NewGuid(), "fixture", int.MaxValue, 1, executable));
                BackendUnconfirmedStore.ResolveIntent("system", request, BackendResultStatuses.Ok, pendingRestore: true);
                Check(!File.Exists(source), "成功静态设置不锁死后续操作：" + command);
            }
            foreach (var status in new[] { BackendResultStatuses.Partial, BackendResultStatuses.Failed, BackendResultStatuses.Cancelled })
            {
                var request = Guid.NewGuid();
                BackendUnconfirmedStore.BeginIntent(new("system", BackendCommands.SystemBxApply, request, Guid.NewGuid(), "fixture", int.MaxValue, 1, executable));
                BackendUnconfirmedStore.ResolveIntent("system", request, status, pendingRestore: true);
                Check(File.Exists(source) && !BackendUnconfirmedStore.ArchiveCompletedStaticBackup("system"),
                    "有恢复义务的非成功结果继续阻止交接：" + status);
                File.Delete(source);
            }
            object Legacy(string reason = "ok", string command = BackendCommands.SystemBxApply, bool childrenUnknown = false,
                string? requestId = null, object? children = null, string phase = "unconfirmed", string[]? completedItems = null) =>
                new { Module = "system", Phase = phase, Reason = reason, Command = command,
                    RequestId = requestId ?? Guid.NewGuid().ToString("D"), SessionId = Guid.NewGuid().ToString("D"),
                    Pid = int.MaxValue, StartTicks = 1L, Path = executable, ChildrenUnknown = childrenUnknown,
                    Children = children, CompletedItems = completedItems };
            foreach (var bad in new[] { Legacy("timeout"), Legacy("partial"), Legacy("failed"), Legacy(phase: "intent"),
                Legacy(command: BackendCommands.SystemDefenderExclusionsAdd), Legacy(childrenUnknown: true), Legacy(requestId: "missing"),
                Legacy(children: new[] { new { Name = "fixture", Pid = int.MaxValue, StartTicks = 1L } }), Legacy(completedItems: ["item:failed"]) })
            {
                var original = JsonSerializer.Serialize(bad);
                File.WriteAllText(source, original);
                Check(!BackendUnconfirmedStore.ArchiveCompletedStaticBackup("system") && File.ReadAllText(source) == original,
                    "不归档真正未知、部分失败、临时排除或不完整的记录");
                Check(BackendLifecycle.DescribeBlocker(["system"], "退出").Contains("待核对的操作记录"), "提示区分未知记录与进程仍存活");
            }
            var knownSuccess = JsonSerializer.Serialize(Legacy());
            File.WriteAllText(source, knownSuccess);
            var reviewedDirectory = Path.Combine(root, "official-backend-reviewed");
            Directory.Move(reviewedDirectory, reviewedDirectory + "-fixture");
            File.WriteAllText(reviewedDirectory, "blocked");
            Check(!BackendUnconfirmedStore.ArchiveCompletedStaticBackup("system") && File.ReadAllText(source) == knownSuccess,
                "归档目录不可写时保留原记录和阻塞");
            File.Delete(reviewedDirectory);
            Directory.Move(reviewedDirectory + "-fixture", reviewedDirectory);
            BackendUnconfirmedStore.BeginIntent(new("system", BackendCommands.SystemBxApply, Guid.NewGuid(), Guid.NewGuid(), "new", int.MaxValue, 1, executable));
            Check(Directory.GetFiles(reviewedDirectory, "system-completed-static-backup-*.json")
                .Any(p => !p.EndsWith(".outcome.json") && File.ReadAllText(p) == knownSuccess), "开始新写入前兼容旧版成功记录并保留证据");
            File.Delete(source);

            await Task.CompletedTask;
            Console.WriteLine("Backend reconciliation passed: intent write-ahead, resolve/partial(known+unknown)/timeout, missing command, PID reuse, live process, orphan child, stale consent, corruption, write failure, recovery failure/retry, crash recovery. Isolated data only; no process killed or system modified.");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
