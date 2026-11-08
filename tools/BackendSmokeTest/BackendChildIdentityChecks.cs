using System.Diagnostics;
using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

internal static class BackendChildIdentityChecks
{
    internal static void Run()
    {
        var checks = 0;
        void Check(bool ok, string why) { if (!ok) throw new IOException(why); checks++; }
        BackendProcessTree.Entry Row(int pid, int parent, long ticks) => new("process-" + pid, pid, parent, ticks);
        void Tree(long start, BackendProcessTree.Entry[] rows, bool ok, params int[] expected)
        {
            var result = BackendProcessTree.Capture(100, start, rows);
            Check(result.Ok == ok, "完整性判断不符：" + string.Join(",", expected));
            Check(result.Children.Select(p => p.Pid).SequenceEqual(expected), "父子身份集合不符：" + string.Join(",", expected));
        }
        // 后端 PID 被旧登录进程复用；旧 Explorer 分支即使有新浏览器也不应被遍历。
        Tree(1000, [Row(100, 1, 1000), Row(50, 100, 500), Row(51, 50, 2500), Row(200, 100, 2000), Row(201, 200, 3000)], true, 200, 201);
        // 必须逐层检查，不能仅在最终名单中按根进程时间过滤。
        Tree(1000, [Row(200, 100, 2000), Row(201, 200, 1500), Row(202, 201, 4000), Row(203, 200, 2100)], true, 200, 203);
        // 原后端已退出：真实孤立安装器保留；复用 PID 的新进程所生的孩子排除。
        Tree(1000, [Row(100, 1, 5000), Row(200, 100, 2000), Row(201, 200, 7000), Row(300, 100, 6000)], true, 200, 201);
        Tree(1000, [Row(200, 100, 2000), Row(201, 200, 3000)], true, 200, 201);
        Tree(1000, [], true);
        // 创建时间缺失时保留阻塞，不从未知分支继续扩张名单。
        Tree(1000, [Row(200, 100, 0), Row(201, 200, 3000)], false, 200);
        Tree(1000, [Row(100, 1, 0), Row(200, 100, 2000)], false);
        Tree(1000, [Row(99, 1, 0)], true);
        Tree(0, [Row(200, 100, 2000)], false);
        // WMI 微秒与进程 100ns 精度差异不应把同一时刻误判成旧进程。
        Tree(1009, [Row(100, 200, 1000), Row(200, 100, 1000)], true, 200);
        Tree(1000, [Row(100, 1, 500), Row(200, 100, 2000)], false);
        Tree(1000, [Row(100, 1, 1000), Row(100, 1, 1000), Row(200, 100, 2000)], false);

        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-child-identities-" + id.ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        var path = BackendUnconfirmedStore.RecordPathForTests(BackendModules.CpuFrame);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var self = Process.GetCurrentProcess();
        var selfTicks = self.StartTime.ToUniversalTime().Ticks;
        string Fixture(long backendTicks, long childTicks) => JsonSerializer.Serialize(new
        {
            Module = BackendModules.CpuFrame, Phase = "unconfirmed", Command = BackendCommands.CpuFramePowerUnlock,
            Reason = "disconnected", Pid = int.MaxValue, StartTicks = backendTicks, Path = Environment.ProcessPath,
            Children = new[] { new { Name = "test-self", Pid = self.Id, StartTicks = childTicks } },
        });
        try
        {
            BackendUnconfirmedStore.DescendantsForTests = _ => ([], true);
            var original = Fixture(selfTicks + TimeSpan.TicksPerSecond, selfTicks);
            File.WriteAllText(path, original);
            var stale = BackendUnconfirmedStore.ReviewAll().Single();
            Check(!stale.CanAcknowledge, "旧错误名单原先确实阻塞");
            Check(BackendUnconfirmedStore.RefreshChildren(BackendModules.CpuFrame), "可重新核对确定早于后端的进程");
            var corrected = BackendUnconfirmedStore.ReviewAll().Single();
            Check(corrected.ChildNames.Count == 0 && corrected.CanAcknowledge, "确定不可能的旧身份不再阻塞核对");
            Check(Directory.GetFiles(Path.Combine(root, "official-backend-reviewed"), "*-children-recheck-*.json")
                .Any(p => File.ReadAllText(p) == original), "剔除前原始字节必须归档");
            var refused = false;
            try { BackendUnconfirmedStore.AcknowledgeCurrentState(stale); } catch (IOException) { refused = true; }
            Check(refused && File.Exists(path), "名单变更后旧确认指纹不可复用");

            File.WriteAllText(path, Fixture(selfTicks - TimeSpan.TicksPerSecond, selfTicks));
            Check(BackendUnconfirmedStore.RefreshChildren(BackendModules.CpuFrame), "真实已知子进程重新核对成功");
            Check(!BackendUnconfirmedStore.ReviewAll().Single().CanAcknowledge, "真正存活的子进程仍然阻止放行");
            File.WriteAllText(path, Fixture(selfTicks - TimeSpan.TicksPerSecond, 0));
            Check(!BackendUnconfirmedStore.RefreshChildren(BackendModules.CpuFrame), "活进程身份缺失不可放行");
            Check(BackendUnconfirmedStore.ReviewAll().Single().ChildrenUnknown, "身份缺失保留未知状态");
            File.WriteAllText(path, original);
            BackendUnconfirmedStore.DescendantsForTests = _ => ([], false);
            Check(!BackendUnconfirmedStore.RefreshChildren(BackendModules.CpuFrame)
                && !BackendUnconfirmedStore.ReviewAll().Single().CanAcknowledge, "枚举失败不得靠清名单解除阻塞");
            Console.WriteLine($"Backend child identities: {checks} checks passed; synthetic PID reuse, orphan retention and isolated evidence only; no process killed or system changed.");
        }
        finally { BackendUnconfirmedStore.DescendantsForTests = null; Directory.Delete(root, true); }
    }
}
