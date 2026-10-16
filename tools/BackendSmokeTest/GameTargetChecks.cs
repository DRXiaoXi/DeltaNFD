using System.Diagnostics;
using DeltaNFD.Services;
using DeltaNFD.Native;

internal static class GameTargetChecks
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var children = new List<Process>();
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            Console.WriteLine("PASS: " + message);
        }
        try
        {
            var settings = Path.Combine(root, "settings.json");
            File.WriteAllText(settings, "{}");
            var targets = new GameTargetService(settings);
            Check(!targets.IsCustom, "旧设置默认三角洲模式");
            var folders = new[] { Path.Combine(root, "游戏 A"), Path.Combine(root, "游戏 B") };
            foreach (var folder in folders)
            {
                Directory.CreateDirectory(folder);
                foreach (var file in Directory.GetFiles(AppContext.BaseDirectory))
                    if (file.EndsWith(".dll") || file.EndsWith(".json")) File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
                File.Copy(Path.Combine(AppContext.BaseDirectory, "BackendSmokeTest.exe"), Path.Combine(folder, "GameTargetTest.exe"));
            }
            var a = Path.Combine(folders[0], "GameTargetTest.exe");
            var b = Path.Combine(folders[1], "GameTargetTest.exe");
            Check(!(await targets.ChangeAsync(true, "")).Success && !targets.IsCustom, "无效选择不切换");
            Check(!(await targets.ChangeAsync(true, Environment.ProcessPath)).Success || targets.Current.ExecutablePath != Environment.ProcessPath,
                "工具自身不能作为目标");
            Check((await targets.ChangeAsync(true, a)).Success, "选择包含中文和空格的 EXE");
            var shader = new ShaderService(targets);
            Check((await shader.DiagnoseAsync()).Level == ShaderDiagLevel.NotApplicable &&
                (await shader.GetStatusAsync()).DriverState == ShaderDriverState.NotApplicable && !(await shader.ClearPsoCacheAsync()).Success,
                "自定义模式下着色器服务入口拒绝扫描及清理");
            var ace = new AceService(new TweakBackupStore(Path.Combine(root, "ace-backups.json")), targets);
            Check(!(await ace.ScanAsync()).CanClean && !(await ace.CleanAsync()).Success && (await ace.CheckCoreFilesAsync()).SysCount == 0,
                "自定义模式下 ACE 服务入口拒绝扫描及清理");
            Check(new GameTargetService(settings).Current.ExecutablePath == targets.Current.ExecutablePath, "重启恢复设置");
            var blockedFolder = Path.Combine(root, "blocked-parent");
            File.WriteAllText(blockedFolder, "not a directory");
            var unwritable = new GameTargetService(Path.Combine(blockedFolder, "settings.json"));
            Check(!(await unwritable.ChangeAsync(true, a)).Success && !unwritable.IsCustom, "写入失败不显示未持久化的目标");
            children.Add(Process.Start(new ProcessStartInfo(a, "--game-target-test-child \"" + b + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!);
            children.Add(Process.Start(new ProcessStartInfo(a, "--game-target-test-child") { UseShellExecute = false, CreateNoWindow = true })!);
            children.Add(Process.Start(new ProcessStartInfo(b, "--game-target-test-child") { UseShellExecute = false, CreateNoWindow = true })!);
            var descendant = int.Parse((await children[0].StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!);
            children.Add(Process.GetProcessById(descendant));
            await Task.Delay(500);
            var found = targets.GetProcesses();
            Check(found.Select(p => p.Id).Order().SequenceEqual(children.Take(2).Select(p => p.Id).Order()), "同名不同目录不命中，同一 EXE 多开均命中");
            foreach (var p in found) p.Dispose();
            Check(targets.Matches(children[0]) && !targets.Matches(children[2]), "实际进程路径验证");
            Check(!targets.Matches(children[3]), "所选游戏的不同 EXE 子进程不自动纳入");
            var identity = targets.Identify(children[0])!;
            Check(targets.Matches(children[0], identity), "PID、启动时间和目标代次验证");
            Check(GameTargetService.CanonicalPath(a.ToUpperInvariant()).Equals(targets.Current.ExecutablePath, StringComparison.OrdinalIgnoreCase), "路径大小写规范化");
            var junction = Path.Combine(root, "linked-directory");
            using (var link = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                "/c mklink /J \"" + junction + "\" \"" + folders[0] + "\"") { UseShellExecute = false, CreateNoWindow = true }))
            {
                await link!.WaitForExitAsync();
                if (link.ExitCode != 0) throw new Exception("Junction fixture creation failed");
            }
            Check(GameTargetService.CanonicalPath(Path.Combine(junction, "GameTargetTest.exe")) == GameTargetService.CanonicalPath(a), "目录联接规范化为实际文件路径");
            Directory.Delete(junction);
            foreach (var configure in new Action<AppSettings>[]
            {
                s => s.FrameModeActive = true, s => s.GamePriorityEnabled = true, s => s.GameAffinityRuleEnabled = true,
                s => s.SingleCcdExcludeCpu0Enabled = true, s => s.DualCcdArmed = true, s => s.DualCcdImmediateEnabled = true,
            })
            {
                AppSettingsStore.Update(settings, configure);
                Check(!(await targets.ChangeAsync(false)).Success && targets.IsCustom, "优化开启时禁止切换");
                AppSettingsStore.Update(settings, s =>
                {
                    s.FrameModeActive = s.GamePriorityEnabled = s.GameAffinityRuleEnabled = s.SingleCcdExcludeCpu0Enabled = s.DualCcdArmed = s.DualCcdImmediateEnabled = false;
                });
            }
            targets.SetRestoreFailure("测试还原", true);
            Check(!(await targets.ChangeAsync(false)).Success, "还原失败时继续阻止切换");
            targets.SetRestoreFailure("测试还原", false);
            var lease = targets.BeginOperation();
            var change = targets.ChangeAsync(true, b);
            Check(!change.IsCompleted, "切换等待在途操作结束");
            lease.Dispose();
            Check((await change).Success && !targets.Matches(children[0], identity), "切换后旧代次拒绝");
            Check((await targets.ChangeAsync(false)).Success && !targets.IsCustom, "关闭模式恢复默认目标");
            Check(AppSettingsStore.Read(settings).CustomGameExecutablePath == GameTargetService.CanonicalPath(b), "关闭模式保留自定义路径");
            Check(NvApiDrs.IsExactCustomApplication(a, a.ToUpperInvariant()) && !NvApiDrs.IsExactCustomApplication(a, b) &&
                !NvApiDrs.IsExactCustomApplication(a, "GameTargetTest.exe"), "NVAPI 不复用同名或其他目录关联");
            Check(GameTargetService.CustomProfileName(a) != GameTargetService.CustomProfileName(b), "不同目录 NVIDIA 配置隔离");
            Check(!(await targets.ChangeAsync(true, Path.Combine(root, "missing.exe"))).Success, "文件移动或删除后不能启用");
            try { GameTargetService.ReadProcessPath(int.MaxValue); throw new Exception("invalid PID accepted"); }
            catch (System.ComponentModel.Win32Exception) { Console.WriteLine("PASS: 不可读取的进程路径明确失败"); }
        }
        finally
        {
            foreach (var child in children)
            {
                try { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } } finally { child.Dispose(); }
            }
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Unsafe fixture cleanup path");
                    foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                    Directory.Delete(root, true);
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt == 4) { Console.WriteLine("WARNING: temporary fixture cleanup failed: " + root); break; }
                    await Task.Delay(500);
                }
            }
        }
        Console.WriteLine("Game target checks passed. No real game or system optimization was executed.");
    }
}
