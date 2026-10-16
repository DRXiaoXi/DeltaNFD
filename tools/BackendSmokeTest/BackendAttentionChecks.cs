using System.Diagnostics;
using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

internal static class BackendAttentionChecks
{
    internal static void Run()
    {
        var count = 0;
        void Check(bool pass, string name) { if (!pass) throw new Exception(name); count++; Console.WriteLine("PASS: " + name); }
        var queue = new BackendAttentionQueue();
        Check(queue.Report("runtime", "first"), "新异常需要主动提示");
        Check(!queue.Report("runtime", "first"), "同一异常不重复排队");
        var before = queue.Snapshot(false);
        queue.Presented(before);
        Check(queue.Snapshot(true).Count == 0, "已显示异常不再自动重复弹窗");
        Check(queue.Snapshot(false).Count == 1, "显示过仍保留恢复入口，不等于解除证据");
        Check(queue.Report("runtime", "second"), "新一代异常仍需提示");
        queue.Presented(before);
        Check(queue.Snapshot(true).Single().Fingerprint == "second", "旧弹窗完成不吞掉随后出现的新异常");
        Check(queue.Report("cpuframe", "cpu"), "另一个模块单独进入队列");
        Check(queue.Snapshot(true).Count == 2, "并发模块聚合显示");
        queue.Presented(queue.Snapshot(false).Where(p => p.Module == "runtime"));
        Check(queue.Snapshot(true).Single().Module == "cpuframe", "一个模块的核对流程不能吞掉其他模块后台异常");
        queue.Forget("runtime");
        Check(queue.Snapshot(false).Single().Module == "cpuframe", "仅清理已消失模块的通知状态");
        Check(queue.Report("runtime", "first"), "恢复后再次发生同一内容也重新提示");

        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-attention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var id = Guid.NewGuid(); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        var record = BackendUnconfirmedStore.RecordPathForTests(BackendModules.Runtime);
        Directory.CreateDirectory(Path.GetDirectoryName(record)!);
        using var process = Process.GetCurrentProcess();
        void Write(string phase, string reason, int pid, long ticks) => File.WriteAllText(record, JsonSerializer.Serialize(new
        {
            Module = BackendModules.Runtime, Phase = phase, Reason = reason, Pid = pid, StartTicks = ticks,
            Command = BackendCommands.RuntimeRepair, RequestId = id.ToString("D"), Path = process.MainModule!.FileName,
        }));
        try
        {
            Check(BackendUnconfirmedStore.AttentionFingerprint(BackendModules.Runtime) is null, "没有证据不会误提示系统修改未知");
            Write("intent", "", process.Id, process.StartTime.ToUniversalTime().Ticks);
            Check(BackendUnconfirmedStore.AttentionFingerprint(BackendModules.Runtime) is null, "正常在途写入不当成报错");
            Write("unconfirmed", "ok", process.Id, process.StartTime.ToUniversalTime().Ticks);
            Check(BackendUnconfirmedStore.AttentionFingerprint(BackendModules.Runtime) is null, "成功暂存恢复记录不主动报错");
            Write("unconfirmed", "timeout", process.Id, process.StartTime.ToUniversalTime().Ticks);
            Check(BackendUnconfirmedStore.AttentionFingerprint(BackendModules.Runtime) is not null, "修改超时证据触发引导");
            var original = File.ReadAllBytes(record);
            BackendAttention.Report(BackendModules.Runtime);
            var pending = BackendAttention.Snapshot(); BackendAttention.Presented(pending);
            Check(BackendAttention.Snapshot(true).Count == 0 && BackendAttention.Snapshot().Count == 1, "主动弹窗去重后，手动错误弹窗仍有核对入口");
            Check(File.ReadAllBytes(record).SequenceEqual(original), "通知与展示绝不归档或修改恢复证据");
            Write("intent", "", int.MaxValue, 1);
            Check(BackendUnconfirmedStore.AttentionFingerprint(BackendModules.Runtime) is not null, "旧进程已退出的遗留 intent 需要提示");
            File.WriteAllText(record, "{invalid}");
            Check(BackendUnconfirmedStore.AttentionFingerprint(BackendModules.Runtime) is not null, "损坏证据仍提供核对引导");
            File.Delete(record);
            Check(BackendAttention.Snapshot().Count == 0, "恢复或归档后不再展示过期通知");
            Console.WriteLine($"Backend attention checks: {count} passed; isolated records only, no recovery or system writes.");
        }
        finally { Directory.Delete(root, true); }
    }
}
