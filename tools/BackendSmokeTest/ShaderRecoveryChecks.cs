using DeltaNFD.Backends;
using DeltaNFD.Native;
using DeltaNFD.Services;

internal static class ShaderRecoveryChecks
{
    internal static void Run()
    {
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
        foreach (var scope in new[] { "global", "game" })
            foreach (var value in NvShaderCacheChoices.Values) Check(NvShaderCacheChoices.IsValid(scope, value), "允许明确范围与候选值");
        foreach (var scope in new[] { "", "ALL", "profile", "../global", "Global" })
            Check(!NvShaderCacheChoices.IsValid(scope, null), "拒绝范围扩大或未知范围");
        Check(!NvShaderCacheChoices.IsValid("global", 1234567), "拒绝任意未授权容量");
        Check(NvShaderCacheChoices.Values[0] is null && NvShaderCacheChoices.Labels[0].Contains("推荐"), "默认首选不推荐固定容量");
        var invalid = NvApiDrs.SetShaderCacheSize("invalid", 0, _ => throw new Exception("backup should not run"), () => true);
        Check(!invalid.Success, "无效请求在 NVAPI 会话前拒绝");
        var trace = new List<string>(); var allowed = true; var writes = 0;
        (bool Success, string Message) Commit(uint? size = null, Func<IntPtr, uint?, int>? writer = null, Func<int>? save = null, Action? backup = null) =>
            NvApiDrs.CommitShaderCacheWrites([new IntPtr(1), new IntPtr(2)], size,
                backup ?? (() => trace.Add("backup")), () => allowed,
                writer ?? ((profile, value) => { writes++; trace.Add("write-" + profile); return 0; }),
                save ?? (() => { trace.Add("save"); return 0; }));
        Check(Commit().Success && trace.SequenceEqual(new[] { "backup", "write-1", "write-2", "save" }),
            "原值先落盘，全部设置成功后才保存会话");
        trace.Clear(); writes = 0; allowed = false;
        Check(!Commit().Success && writes == 0 && !trace.Contains("save"), "游戏运行或锁定时不写入、不保存");
        trace.Clear(); allowed = true;
        Check(!Commit(writer: (_, _) => { allowed = false; return 0; }).Success && !trace.Contains("save"),
            "两项之间启动游戏会停止，不能保存半批设置");
        trace.Clear(); allowed = true; var calls = 0;
        Check(!Commit(writer: (_, _) => ++calls == 1 ? 0 : -1).Success && !trace.Contains("save"),
            "第二配置写失败不保存第一配置的会话改动");
        trace.Clear(); allowed = true;
        Check(!Commit(writer: (_, _) => { calls++; if (calls == 4) allowed = false; return 0; }).Success && !trace.Contains("save"),
            "保存前条件变化不保存");
        trace.Clear(); allowed = true;
        Check(Commit(writer: (_, _) => NvApiDrs.StatusSettingNotFound).Success, "恢复默认没有该覆盖项仍可成功");
        trace.Clear();
        Check(!Commit(5120, writer: (_, _) => NvApiDrs.StatusSettingNotFound).Success && !trace.Contains("save"),
            "设置固定容量不支持时不能伪报成功");
        trace.Clear();
        Check(!Commit(save: () => -1).Success, "保存失败不能报告成功");
        Check(!Commit(save: () => throw new IOException("fixture save uncertain")).Success, "保存异常提供核对信息");
        trace.Clear(); writes = 0;
        try { Commit(backup: () => throw new IOException("fixture disk full")); Check(false, "备份失败被忽略"); }
        catch (IOException) { Check(writes == 0 && !trace.Contains("save"), "原值记录写入失败不执行设置"); }

        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-shader-recovery-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(root, "cache"); var external = Path.Combine(root, "outside");
        Directory.CreateDirectory(Path.Combine(cache, "nested")); Directory.CreateDirectory(external);
        var keep = Path.Combine(external, "keep.txt"); File.WriteAllText(keep, "outside");
        var one = Path.Combine(cache, "one.bin"); var two = Path.Combine(cache, "nested", "two.bin");
        File.WriteAllText(one, "cache1"); File.WriteAllText(two, "cache2");
        Check(!ShaderRecoveryCleanup.CleanRoots([cache], () => true).Success && File.Exists(one), "游戏运行时不能清缓存");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Check(!ShaderRecoveryCleanup.CleanRoots([cache], () => false, cancelled.Token).Success && File.Exists(one),
                "开始前取消不能删除文件");
        }
        var link = Path.Combine(cache, "linked");
        // A Windows junction needs no symbolic-link privilege; both paths belong to this fixture.
        var script = "New-Item -ItemType Junction -Path '" + link.Replace("'", "''") + "' -Target '" + external.Replace("'", "''") + "' -ErrorAction Stop | Out-Null";
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));
        using (var junction = System.Diagnostics.Process.Start(start) ?? throw new IOException("fixture junction creation failed"))
        {
            if (!junction.WaitForExit(10000) || junction.ExitCode != 0) throw new IOException("fixture junction creation failed");
        }
        var partial = ShaderRecoveryCleanup.CleanRoots([cache], () => false);
        Check(!partial.Success && partial.IsPartial && File.Exists(keep) && !File.Exists(one) && !File.Exists(two),
            "跳过符号链接，保留外部目标，并报告部分完成");
        var underLink = ShaderRecoveryCleanup.CleanRoots([link], () => false);
        Check(!underLink.Success && File.Exists(keep), "根目录重解析也拒绝");
        var allRoots = Path.Combine(root, "all-roots"); Directory.CreateDirectory(allRoots);
        var firstRootFile = Path.Combine(allRoots, "keep.bin"); File.WriteAllText(firstRootFile, "before validation");
        Check(!ShaderRecoveryCleanup.CleanRoots([allRoots, link], () => false).Success && File.Exists(firstRootFile),
            "所有根目录预检先于第一次删除");
        Directory.Delete(link); // Remove only the temporary link itself, never its target.
        Check(ShaderRecoveryCleanup.CleanRoots([cache], () => false).Success && Directory.Exists(cache) && File.Exists(keep),
            "重复清理可幂等，保留根目录和范围外文件");

        // 0 字节文件也必须被删除，不能因为总大小为 0 而整目录跳过。
        var zeroRoot = Path.Combine(root, "zero"); Directory.CreateDirectory(zeroRoot);
        var zeroFile = Path.Combine(zeroRoot, "zero.bin"); File.WriteAllBytes(zeroFile, []);
        var zero = ShaderCacheCleanup.CleanRoots([zeroRoot], () => false);
        Check(zero.DeletedFiles == 1 && zero.FreedMb == 0 && !zero.Partial && !File.Exists(zeroFile),
            "0 字节缓存文件仍会删除，结果不伪装成有释放");

        // 被占用/共享冲突要如实报告失败，不能显示为完整成功。
        var lockedRoot = Path.Combine(root, "locked"); Directory.CreateDirectory(lockedRoot);
        var lockedFile = Path.Combine(lockedRoot, "locked.bin"); File.WriteAllText(lockedFile, "fixture");
        using (var hold = new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var locked = ShaderCacheCleanup.CleanRoots([lockedRoot], () => false);
            Check(locked.FailedItems > 0 && locked.Partial && File.Exists(lockedFile),
                "被占用文件逐项报告，不显示为完整成功");
        }
        File.Delete(lockedFile);

        var stopCache = Path.Combine(root, "stop"); Directory.CreateDirectory(stopCache);
        for (var i = 0; i < 4; i++) File.WriteAllText(Path.Combine(stopCache, i + ".bin"), "fixture");
        var polls = 0;
        var stopped = ShaderRecoveryCleanup.CleanRoots([stopCache], () => ++polls > 3);
        Check(!stopped.Success && stopped.IsPartial && Directory.GetFiles(stopCache).Length == 3,
            "清理中游戏启动及时停止，不假装全部完成");
        var cleanupDto = new ShaderCacheCleanupResult
        {
            FreedMb = 1.5,
            DeletedFiles = 3,
            FailedItems = 1,
            SkippedLinks = 1,
            Partial = true,
            Message = "partial fixture",
            Skipped = ["fixture: locked"],
        };
        var cleanupResult = BackendResult.PartialResult("system.cleanup.shader-cache", "partial fixture", [], cleanupDto, requiresReview: false);
        Check(cleanupResult.Status == BackendResultStatuses.Partial && cleanupResult.RequiresReview == false && cleanupResult.Data is not null,
            "着色器清理 partial 结果已知且不需恢复核对");
        var cleanupData = cleanupResult.Data!.Value;
        var cleanupRoundTrip = BackendJson.Deserialize<ShaderCacheCleanupResult>(cleanupData);
        Check(cleanupRoundTrip.DeletedFiles == 3 && cleanupRoundTrip.FailedItems == 1
            && cleanupRoundTrip.SkippedLinks == 1 && cleanupRoundTrip.Partial
            && cleanupRoundTrip.Skipped.Count == 1, "着色器清理结果可被前端合同解析");

        Check(BackendModules.Require(BackendModules.Gpu).Commands.Contains(BackendCommands.GpuShaderCacheApply)
            && BackendModules.Require(BackendModules.System).Commands.Contains(BackendCommands.SystemShaderRecoveryClean)
            && !BackendModules.IsReadOnly(BackendModules.Gpu, BackendCommands.GpuShaderCacheApply), "新写入/清理命令注册并保留修改语义");
        Console.WriteLine($"Shader recovery checks: {count} passed. NVAPI writes simulated; only owned temporary cache fixtures deleted.");
    }
}
