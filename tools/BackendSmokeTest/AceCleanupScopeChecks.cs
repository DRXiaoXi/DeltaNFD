using DeltaNFD.Backends;
using DeltaNFD.Services;
using Microsoft.Win32;

/// <summary>
/// ACE 清理「生产范围」模拟验收（T2）：把进程/服务/注册表/文件四类操作注入内存夹具，
/// 驱动真实的 <see cref="AceService.CleanPhasedAsync"/>，验证清理范围、逐阶段复核、
/// 启动竞态、取消、自保护拒绝、删除失败、文件占用与延迟删除，以及越界/重解析路径拒绝。
/// 全程不触碰真实 ACE、不结束真实进程、不改真实注册表。
/// </summary>
internal static class AceCleanupScopeChecks
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD_AceScopeChecks_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var passed = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); passed++; }
        try
        {
            var store = new TweakBackupStore(Path.Combine(root, "backups.json"));
            var settingsPath = Path.Combine(root, "settings.json");

            // ---- 正常清除：范围完整、逐项如实记录 ----
            var env = new FakeEnvironment();
            var service = new AceService(store, new GameTargetService(settingsPath), static () => false, env);
            var result = await service.CleanPhasedAsync(null, CancellationToken.None);
            Check(result.Status == BackendResultStatuses.Ok, "正常清理应成功");
            Check(env.ServiceKeys.Count == 0, "清理范围覆盖全部 ACE 服务");
            Check(env.RunningPids.Count == 0, "清理范围覆盖全部 ACE 进程");
            Check(env.RegistryKeys.SetEquals([FakeEnvironment.SharedRootLm, FakeEnvironment.SharedRootUsers, FakeEnvironment.ForeignKey]),
                "仅删已核验归属的 ACE 子键，保留共享根与其他厂商同名键");
            Check(env.Backups.Count == 2, "两个 ACE 子键删除前均已核验备份");
            Check(env.Directories.Count == 0, "清理范围覆盖系统与游戏 ACE 目录");
            Check(result.Message.Contains("清理完成", StringComparison.Ordinal), "完整清理如实报告完成");

            // ---- 非管理员：拒绝且不改动任何状态 ----
            var unelevated = new FakeEnvironment { Elevated = false };
            var r2 = await new AceService(store, new GameTargetService(settingsPath), static () => false, unelevated)
                .CleanPhasedAsync(null, CancellationToken.None);
            Check(r2.Status == BackendResultStatuses.Failed && r2.Code == "ace.not-elevated", "非管理员拒绝清理");
            Check(unelevated.Directories.Count == 3 && unelevated.ServiceKeys.Count == 6, "非管理员不触碰任何状态");

            // ---- 自定义主进程模式：整体 Delta-only 拒绝 ----
            File.WriteAllText(settingsPath, "{\"CustomGameModeEnabled\":true,\"CustomGameExecutablePath\":\"C:\\\\Games\\\\x.exe\"}");
            var custom = new FakeEnvironment();
            var r3 = await new AceService(store, new GameTargetService(settingsPath), static () => false, custom)
                .CleanPhasedAsync(null, CancellationToken.None);
            Check(r3.Status == BackendResultStatuses.Failed && r3.Code == "ace.custom-target", "自定义主进程模式拒绝清理");
            File.Delete(settingsPath);

            // ---- 游戏/守卫运行中：阶段守卫拒绝 ----
            var blocked = new FakeEnvironment { RunningNames = { "sguard64" } };
            var r4 = await new AceService(store, new GameTargetService(settingsPath), static () => false, blocked)
                .CleanPhasedAsync(null, CancellationToken.None);
            Check(r4.Status == BackendResultStatuses.Failed && r4.Code == "ace.blocked", "守卫运行中拒绝清理");
            Check(blocked.Directories.Count == 3, "被守卫拦下时不改文件");

            // ---- 启动竞态：清理途中守卫启动 → 停止后续并如实报告已完成项 ----
            var race = new FakeEnvironment { AfterFirstTerminate = () => { } };
            race.AfterFirstTerminate = () => race.RunningNames.Add("sguard64");
            var r5 = await new AceService(store, new GameTargetService(settingsPath), static () => false, race)
                .CleanPhasedAsync(null, CancellationToken.None);
            Check(r5.Status == BackendResultStatuses.Partial && r5.Code == "ace.blocked", "途中启动守卫转为部分完成");
            Check(r5.Items.Count > 0, "部分完成如实记录已完成项");
            Check(race.ServiceKeys.Count == 6, "中止后不再删除服务");

            // ---- 取消：停止后续步骤且不报告成功 ----
            var cancelled = new FakeEnvironment();
            var r6 = await new AceService(store, new GameTargetService(settingsPath), static () => false, cancelled)
                .CleanPhasedAsync(null, new CancellationToken(canceled: true));
            Check(r6.Status == BackendResultStatuses.Cancelled, "取消返回 cancelled");
            Check(cancelled.Directories.Count == 3, "取消后不改动文件");

            // ---- 删除失败：不得显示为已删除 ----
            var failing = new FakeEnvironment();
            failing.DeleteFails.Add("ACE-GAME");
            var r7 = await new AceService(store, new GameTargetService(settingsPath), static () => false, failing)
                .CleanPhasedAsync(null, CancellationToken.None);
            Check(r7.Status == BackendResultStatuses.Partial, "服务删除失败转为部分完成");
            Check(r7.Data is { } partialData && !BackendJson.Deserialize<OperationResult>(partialData).Success,
                "执行侧部分完成数据不能返回成功");
            Check(!AceCleanupResults.ToOperationResult(r7).Success && AceCleanupResults.ToOperationResult(r7).IsPartial,
                "服务删除失败的接口映射如实保留部分完成");
            Check(failing.ServiceKeys.Contains("ACE-GAME"), "删除失败的服务仍存在");
            Check(!r7.Items.Any(i => i.Id == "service:ACE-GAME" && i.Status == BackendResultStatuses.Ok), "失败服务不显示为已删除");

            // ---- 自保护拒绝：SDDL 接管失败如实记录，不静默当成功 ----
            var protectedService = new FakeEnvironment();
            protectedService.SdsetFails.Add("ACE-SSC-DRV64");
            var r8 = await new AceService(store, new GameTargetService(settingsPath), static () => false, protectedService)
                .CleanPhasedAsync(null, CancellationToken.None);
            Check(r8.Status == BackendResultStatuses.Partial, "自保护接管失败转为部分完成");
            Check(!r8.Items.Any(i => i.Id == "acl:ACE-SSC-DRV64"), "接管失败不记录为已完成");

            // ---- 文件占用：延迟删除计入需重启 ----
            var locked = new FakeEnvironment();
            locked.LockedDirectories.Add(locked.InstallDirectory);
            var r9 = await new AceService(store, new GameTargetService(settingsPath), static () => false, locked)
                .CleanPhasedAsync(null, CancellationToken.None);
            var operation = r9.Data is { } data ? BackendJson.Deserialize<OperationResult>(data) : null;
            Check(operation is { RequiresReboot: true }, "被占用文件计入需重启删除");
            Check(operation is { Success: false, IsPartial: true }, "文件占用不显示完整成功");
            Check(r9.Message.Contains("重启", StringComparison.Ordinal), "结果提示重启后删除");
            Check(r9.Status == BackendResultStatuses.Partial, "仍存在的目录不显示为已删除");
            Check(!r9.Items.Any(i => i.Id == "files:" + locked.InstallDirectory), "被占用目录不作为已删除项");
            Check(locked.Directories.Contains(locked.InstallDirectory), "被占用目录保留");

            // ---- 越界路径与重解析点：一律拒绝删除 ----
            var evil = new FakeEnvironment();
            evil.GameDirectories.Add(@"C:\Windows\System32\Drivers");
            evil.Directories.Add(@"C:\Windows\System32\Drivers");
            evil.ReparseDirectories.Add(evil.InstallDirectory);
            var r10 = await new AceService(store, new GameTargetService(settingsPath), static () => false, evil)
                .CleanPhasedAsync(null, CancellationToken.None);
            Check(evil.Directories.Contains(evil.InstallDirectory), "重解析点目录未被删除");
            Check(evil.Directories.Contains(@"C:\Windows\System32\Drivers"), "越界目录未被删除");
            Check(r10.Status == BackendResultStatuses.Partial, "越界/重解析点拒绝转为部分完成");
            Check(r10.Message.Contains("拒绝", StringComparison.Ordinal), "如实报告拒绝原因");

            using var partialCancel = new CancellationTokenSource();
            var cancelledAfterWrite = new FakeEnvironment { AfterFirstTerminate = () => partialCancel.Cancel() };
            var r11 = await new AceService(store, new GameTargetService(settingsPath), static () => false, cancelledAfterWrite)
                .CleanPhasedAsync(null, partialCancel.Token);
            Check(r11.Status == BackendResultStatuses.Partial && r11.Items.Count > 0, "已有改动再取消时保留部分完成状态和明细");
            Check(!AceCleanupResults.ToOperationResult(r11).Success, "部分完成后取消不能映射为成功");

            var unknown = new FakeEnvironment();
            const string unknownKey = @"LocalMachine|SOFTWARE\AppDataLow\Software\ACE-Unrelated";
            unknown.RegistryKeys.Add(unknownKey);
            var r12 = await new AceService(store, new GameTargetService(settingsPath), static () => false, unknown).CleanPhasedAsync(null, CancellationToken.None);
            Check(unknown.RegistryKeys.Contains(unknownKey) && r12.Status == BackendResultStatuses.Partial,
                "同名前缀但无安装路径归属证据的键保留，不宣称完全成功");
            var noBackup = new FakeEnvironment { BackupFails = true };
            var r13 = await new AceService(store, new GameTargetService(settingsPath), static () => false, noBackup).CleanPhasedAsync(null, CancellationToken.None);
            Check(noBackup.OwnedKeys.All(noBackup.RegistryKeys.Contains) && r13.Status == BackendResultStatuses.Partial,
                "备份失败时保留对应键");
            var changedOwner = new FakeEnvironment { OwnershipChangesAfterBackup = true };
            var r14 = await new AceService(store, new GameTargetService(settingsPath), static () => false, changedOwner).CleanPhasedAsync(null, CancellationToken.None);
            Check(changedOwner.RegistryKeys.Contains(FakeEnvironment.OwnedLm) && r14.Status == BackendResultStatuses.Partial,
                "备份后归属变化时拒绝删除");
            var unreadable = new FakeEnvironment { EnumerationFails = true };
            var r15 = await new AceService(store, new GameTargetService(settingsPath), static () => false, unreadable).CleanPhasedAsync(null, CancellationToken.None);
            Check(unreadable.RegistryKeys.Contains(FakeEnvironment.OwnedLm) && r15.Status == BackendResultStatuses.Partial,
                "共享键枚举失败不折算为清理完成");
            var exportRace = new FakeEnvironment();
            exportRace.AfterBackup = () => exportRace.RunningNames.Add("sguard64");
            var r16 = await new AceService(store, new GameTargetService(settingsPath), static () => false, exportRace).CleanPhasedAsync(null, CancellationToken.None);
            Check(exportRace.RegistryKeys.Contains(FakeEnvironment.OwnedLm) && r16.Status == BackendResultStatuses.Partial,
                "导出期间守卫启动，停止后续删除");

            Console.WriteLine($"ACE cleanup scope checks passed ({passed} assertions): full range, elevation/custom/guard gates, " +
                "startup race, cancel, delete failure, self-protection refusal, locked-file reboot, out-of-scope/reparse refusal. All mutations simulated.");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>内存夹具：模拟进程/服务/注册表/文件，永不触碰真实系统。</summary>
    private sealed class FakeEnvironment : IAceCleanupEnvironment
    {
        public bool Elevated { get; set; } = true;
        public string InstallDirectory { get; } = @"C:\Program Files\AntiCheatExpert";
        public List<string> GameDirectories { get; } = [@"C:\Games\DeltaForce\Binaries\Win64\AntiCheatExpert"];

        public HashSet<string> RunningNames { get; } = [];
        public Dictionary<int, string> RunningPids { get; } = new()
        {
            [101] = "ACE-Tray", [102] = "ACE-Service64", [103] = "ACE-Service32",
            [104] = "ACE-Setup64", [105] = "ACE-Setup32",
        };
        public HashSet<string> ServiceKeys { get; } = ["ACE-BASE", "ACE-GAME", "ACE-BOOT", "ACE-CORE", "ACE-SSC-DRV64", "ACE-ADVT"];
        public HashSet<string> RegistryKeys { get; } = [];
        public const string SharedRootLm = @"LocalMachine|SOFTWARE\AppDataLow";
        public const string SharedRootUsers = @"Users|.DEFAULT\SOFTWARE\AppDataLow";
        public const string OwnedLm = SharedRootLm + @"\Software\AntiCheatExpert";
        public const string OwnedUsers = SharedRootUsers + @"\Software\AntiCheatExpert";
        public const string ForeignKey = SharedRootLm + @"\Software\OtherVendor\ACE-Preferences";
        public HashSet<string> OwnedKeys { get; } = [OwnedLm, OwnedUsers];
        public List<string> Backups { get; } = [];
        public bool BackupFails { get; set; }
        public bool OwnershipChangesAfterBackup { get; set; }
        public bool EnumerationFails { get; set; }
        public Action? AfterBackup { get; set; }
        public HashSet<string> Directories { get; } = [];
        public HashSet<string> ReparseDirectories { get; } = [];
        public HashSet<string> LockedDirectories { get; } = [];
        public HashSet<string> DeleteFails { get; } = [];
        public HashSet<string> SdsetFails { get; } = [];
        public Action? AfterFirstTerminate { get; set; }
        private bool _terminated;

        public FakeEnvironment()
        {
            Directories.Add(InstallDirectory);
            Directories.Add(GameDirectories[0]);
            Directories.Add(@"C:\Games2\DeltaForce\Binaries\Win64\AntiCheatExpert");
            GameDirectories.Add(@"C:\Games2\DeltaForce\Binaries\Win64\AntiCheatExpert");
            RegistryKeys.Add(@"LocalMachine|SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{141E7813-148F-4451-A632-2DA347859F5D}");
            RegistryKeys.UnionWith([SharedRootLm, SharedRootUsers, OwnedLm, OwnedUsers, ForeignKey]);
        }

        public bool IsElevated => Elevated;
        public string AceInstallDirectory => InstallDirectory;
        public IReadOnlyList<string> GameAceDirectories() => GameDirectories;

        public bool IsProcessRunning(string name) =>
            RunningNames.Contains(name) || RunningPids.Values.Contains(name, StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<int> FindProcesses(string name) =>
            RunningPids.Where(p => string.Equals(p.Value, name, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToArray();

        public bool TerminateProcessTree(int pid)
        {
            RunningPids.Remove(pid);
            if (AfterFirstTerminate is not null && !_terminated)
            {
                _terminated = true;
                var action = AfterFirstTerminate;
                AfterFirstTerminate = null;
                action();
            }
            return true;
        }

        public bool ServiceKeyExists(string name) => ServiceKeys.Contains(name);

        public Task<(int Code, string StdOut, string StdErr)> RunCommandAsync(string fileName, string arguments, TimeSpan timeout)
        {
            var parts = arguments.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            var verb = parts[0];
            var name = parts.Length > 1 ? parts[1] : "";
            return Task.FromResult(verb switch
            {
                "sdset" => SdsetFails.Contains(name) ? (5, "", "Access is denied.") : (0, "", ""),
                "stop" => (0, "", ""),
                "delete" => DeleteFails.Contains(name) ? (5, "", "拒绝访问") : Remove(name),
                _ => (1, "", "unknown verb"),
            });
        }

        private (int, string, string) Remove(string name)
        {
            ServiceKeys.Remove(name);
            return (0, "", "");
        }

        public bool RegistryKeyExists(RegistryHive hive, string path) => RegistryKeys.Contains($"{hive}|{path}");
        public void DeleteRegistryTree(RegistryHive hive, string path, bool throwOnMissingSubKey)
        {
            var prefix = $"{hive}|{path}";
            var removed = RegistryKeys.RemoveWhere(k => k.Equals(prefix, StringComparison.OrdinalIgnoreCase) || k.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase));
            if (removed == 0 && throwOnMissingSubKey)
                throw new IOException("键不存在：" + path);
        }

        public IReadOnlyList<string> EnumerateSubKeyNames(RegistryHive hive, string path)
        {
            if (EnumerationFails) throw new IOException("simulated denied");
            var prefix = $"{hive}|{path}\\";
            return RegistryKeys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(k => k[prefix.Length..].Split('\\')[0]).Distinct().ToArray();
        }
        public bool HasAceRegistryOwnership(RegistryHive hive, string path) =>
            AceRegistryPolicy.IsCandidatePath(path) && OwnedKeys.Contains($"{hive}|{path}");
        public Task<OperationResult> BackupRegistryKeyAsync(RegistryHive hive, string path)
        {
            if (BackupFails) return Task.FromResult(OperationResult.Fail("simulated backup failure"));
            Backups.Add(path);
            if (OwnershipChangesAfterBackup) OwnedKeys.Remove($"{hive}|{path}");
            AfterBackup?.Invoke();
            return Task.FromResult(OperationResult.Ok("simulated verified backup"));
        }

        public bool DirectoryExists(string path) => Directories.Contains(path);
        public bool IsReparsePoint(string path) => ReparseDirectories.Contains(path);
        public double GetDirectorySizeMb(string path) => Directories.Contains(path) ? 128 : 0;

        public (double FreedMb, int RebootPending) DeleteDirectoryTree(string path)
        {
            if (LockedDirectories.Contains(path)) return (0, 2);
            Directories.Remove(path);
            return (128, 0);
        }
    }
}
