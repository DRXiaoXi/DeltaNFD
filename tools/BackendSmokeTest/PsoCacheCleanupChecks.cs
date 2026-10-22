using System.Diagnostics;
using System.Text;
using DeltaNFD.Backends;
using DeltaNFD.Services;

internal static class PsoCacheCleanupChecks
{
    internal static void Run()
    {
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
        var testId = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-pso-cleanup-" + testId.ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), testId.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, testId);
        string Cache(string name) { var path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
        var linked = Path.Combine(root, "linked");
        try
        {
            var install = Cache("install");
            Directory.CreateDirectory(Path.Combine(install, "DeltaForce", "Content", "Paks"));
            var first = Path.Combine(install, "DeltaForce", "Saved", "PSOCache");
            var second = Path.Combine(install, "Saved", "PSOCache");
            Directory.CreateDirectory(first); Directory.CreateDirectory(second);
            File.WriteAllText(Path.Combine(first, "first.nvph"), "fixture");
            File.WriteAllText(Path.Combine(second, "second.nvph"), "fixture");
            var paths = DeltaForceLocator.FindPsoCachePaths([install, install, Path.Combine(install, "DeltaForce")]);
            Check(paths.Count == 2 && paths.Contains(first) && paths.Contains(second), "全部候选路径覆盖并去重");
            Check(DeltaForceLocator.FindPsoCachePaths([root]).Count == 0, "拒绝非游戏安装根");
            var all = ShaderService.ClearPsoCache(paths, () => false);
            Check(all.Success && !all.IsPartial && !File.Exists(Path.Combine(second, "second.nvph")), "实际服务清空两处缓存");
            Check(all.Message.Contains("2 处") && all.Message.Contains("剩余 0"), "实际结果包含目标数和残留核验");

            var versions = Cache("versions");
            foreach (var version in new[] { "GameVer_Old", "GameVer_New" })
            {
                var dir = Path.Combine(versions, version, "SM6", "DXCache");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "cache.nvph");
                File.WriteAllBytes(file, []);
                File.SetAttributes(file, FileAttributes.ReadOnly);
            }
            var cleaned = ShaderService.ClearPsoCache([versions], () => false);
            Check(cleaned.Success && !cleaned.IsPartial, "只读和零字节 PSOCache 可清理");
            Check(Directory.Exists(versions) && Directory.GetDirectories(versions).Length == 0, "保留缓存根，移除空版本目录");
            var service = new ShaderService(null, () => new ShaderDriverReading(ShaderGpuDetectionState.Nvidia, "596.49"));
            Check(service.DiagnoseAt(versions).Level == ShaderDiagLevel.Info, "清理后实际诊断不再报多版本残留");

            var blocked = Cache("blocked");
            var lockedFile = Path.Combine(blocked, "locked.nvph");
            var removable = Path.Combine(blocked, "normal.nvph");
            File.WriteAllText(lockedFile, "fixture"); File.WriteAllText(removable, "fixture");
            File.SetAttributes(lockedFile, FileAttributes.ReadOnly);
            using (var hold = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var partial = ShaderService.ClearPsoCache([blocked], () => false);
                Check(partial.Success && partial.IsPartial && File.Exists(lockedFile) && !File.Exists(removable), "持续占用如实返回部分完成");
                Check(partial.Message.Contains("剩余 1") && partial.Message.Contains("locked.nvph") && partial.Message.Contains("再次清理"), "告知剩余数、路径和重试方式");
                Check((File.GetAttributes(lockedFile) & FileAttributes.ReadOnly) != 0, "删除失败恢复原只读属性");
            }
            Check(ShaderService.ClearPsoCache([blocked], () => false).Success && !File.Exists(lockedFile), "释放占用后可再次清理");

            var retry = Cache("retry"); var retryFile = Path.Combine(retry, "cache.nvph");
            File.WriteAllText(retryFile, "fixture");
            using (var hold = new FileStream(retryFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var polls = 0; var released = false;
                var report = ShaderCacheCleanup.CleanRoots([retry], () =>
                {
                    if (++polls == 4) { hold.Dispose(); released = true; }
                    return false;
                }, options: ShaderCacheCleanupOptions.PsoCache);
                Check(released && report.DeletedFiles == 1 && !report.Partial, "短暂占用解除后通过有限重试删除");
                Check(report.VerificationComplete && report.RemainingFiles == 0, "重试成功后仍进行残留核验");
            }

            var cancelRoot = Cache("cancel-retry"); var cancelFile = Path.Combine(cancelRoot, "cache.nvph");
            File.WriteAllText(cancelFile, "fixture");
            using (var hold = new FileStream(cancelFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var cancel = new CancellationTokenSource())
            {
                var polls = 0;
                var report = ShaderCacheCleanup.CleanRoots([cancelRoot], () => { if (++polls == 4) cancel.Cancel(); return false; },
                    cancel.Token, options: ShaderCacheCleanupOptions.PsoCache);
                Check(report.Cancelled && report.Partial && !report.VerificationComplete && File.Exists(cancelFile), "占用重试期间取消立即停止且不伪报核验完成");
            }
            Check(!ShaderService.ClearPsoCache([cancelRoot], () => true).Success && File.Exists(cancelFile), "游戏运行拒绝清理");

            var raced = Cache("new-file");
            File.WriteAllText(Path.Combine(raced, "old.nvph"), "fixture");
            var generated = Path.Combine(raced, "generated.nvph"); var scanPolls = 0;
            var racedReport = ShaderCacheCleanup.CleanRoots([raced], () =>
            {
                if (++scanPolls == 3) File.WriteAllText(generated, "created after initial enumeration");
                return false;
            }, options: ShaderCacheCleanupOptions.PsoCache);
            Check(racedReport.DeletedFiles == 1 && racedReport.RemainingFiles == 1 && racedReport.Partial, "枚举后新生成的文件不能被漏报成功");
            Check(racedReport.VerificationComplete && racedReport.Skipped.Any(s => s.Contains("generated.nvph")), "核验给出新残留的具体路径");

            var vanishedRoot = Cache("vanished-during-verification");
            File.WriteAllText(Path.Combine(vanishedRoot, "old.nvph"), "fixture");
            var verificationPolls = 0; var vanishingFile = "";
            var vanishedReport = ShaderCacheCleanup.CleanRoots([vanishedRoot], () =>
            {
                switch (++verificationPolls)
                {
                    case 3:
                        File.WriteAllText(Path.Combine(vanishedRoot, "new-a.nvph"), "fixture");
                        File.WriteAllText(Path.Combine(vanishedRoot, "new-b.nvph"), "fixture");
                        break;
                    case 4: vanishingFile = Directory.GetFiles(vanishedRoot)[0]; break;
                    case 5: File.Delete(vanishingFile); break;
                }
                return false;
            }, options: ShaderCacheCleanupOptions.PsoCache);
            Check(vanishedReport.Partial && vanishedReport.VerificationComplete && vanishedReport.RemainingFiles == 1,
                "核验时单文件消失不跳过同目录内其他残留");

            var outside = Cache("outside"); var sentinel = Path.Combine(outside, "keep.nvph");
            File.WriteAllText(sentinel, "outside sentinel"); File.SetAttributes(sentinel, FileAttributes.ReadOnly);
            var script = "New-Item -ItemType Junction -Path '" + linked.Replace("'", "''") + "' -Target '" + outside.Replace("'", "''") + "' -ErrorAction Stop | Out-Null";
            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-NonInteractive"); psi.ArgumentList.Add("-EncodedCommand");
            psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            using (var junction = Process.Start(psi) ?? throw new IOException("fixture junction creation failed"))
                if (!junction.WaitForExit(10000) || junction.ExitCode != 0) throw new IOException("fixture junction creation failed");
            var valid = Cache("valid"); var keep = Path.Combine(valid, "keep.nvph"); File.WriteAllText(keep, "fixture");
            var refused = ShaderService.ClearPsoCache([valid, linked], () => false);
            Check(!refused.Success && File.Exists(keep) && File.Exists(sentinel), "根链接仍整批拒绝，预检先于删除");
            Check((File.GetAttributes(sentinel) & FileAttributes.ReadOnly) != 0, "绝不修改链接外目标的只读属性");

            var dto = BackendJson.Deserialize<ShaderCacheCleanupResult>(System.Text.Json.JsonSerializer.SerializeToElement(racedReport, BackendJson.SerializerOptions));
            Check(dto.RemainingFiles == 1 && dto.VerificationComplete && dto.Partial, "残留核验合同往返保持语义");
            ShaderRecoveryChecks.Run();
            ShaderCachePathChecks.Run();
            Console.WriteLine($"PSOCache cleanup checks: {count} passed. Actual linked production code; only owned fixtures deleted.");
        }
        finally
        {
            if (Directory.Exists(linked)) Directory.Delete(linked, false);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            // 仅本次带代次标记的临时目录；全部自建 junction 已先移除。
            if (File.ReadAllText(Path.Combine(root, "isolated-test.marker")) == testId.ToString("D")
                && Path.GetFileName(root) == "DeltaNFD-pso-cleanup-" + testId.ToString("N")) Directory.Delete(root, true);
        }
    }
}
