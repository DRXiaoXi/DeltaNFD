using DeltaNFD.Backends;
using DeltaNFD.Services;

internal static class AceCoreFileCleanupChecks
{
    internal static void Run()
    {
        var id = Guid.NewGuid();
        var data = Path.Combine(Path.GetTempPath(), "DeltaNFD-core-clean-" + id.ToString("N"));
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(data, id);
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        BackendResult Run(Fake environment, CancellationToken token = default, Func<bool>? elevated = null,
            Func<bool>? custom = null, Func<IReadOnlyList<string>>? blockers = null) =>
            new AceCoreFileCleanup(environment, new AceCleanupGuard(custom ?? (() => false), elevated ?? (() => true),
                blockers ?? (() => []))).Run(null, token);
        bool Ok(BackendResult result) => AceCleanupResults.ToOperationResult(result).Success;
        Fake New() => new();
        const string root = AceCoreFileCleanup.Root;

        Check(BackendModules.IsAllowed(BackendModules.Ace, BackendCommands.AceCoreClean), "专用清理命令在 ACE 白名单内");
        Check(BackendModules.RequiresWriteIntent(BackendModules.Ace, BackendCommands.AceCoreClean), "专用清理写前登记意图");
        Check(BackendModules.Require(BackendModules.Ace).ExitAfterCommands.Contains(BackendCommands.AceCoreClean), "文件清理后后端退出");
        Check(EmergencyPolicyVerifier.CanBlock(BackendCommands.AceCoreClean), "新清理命令受紧急策略管理");
        foreach (var name in new[] { "ACE-CORE.sys", "ACE-CORE-123.sys", "ace-core_v2.SYS" })
            Check(AceCoreFileCleanup.IsCoreSysFile(Path.Combine(root, name)), "接受 CORE SYS：" + name);
        foreach (var name in new[] { "ACE-BASE.sys", "xACE-CORE.sys", "ACE-CORE.dll", "ACE-CORE.sys.bak", "ACE-CORE.sys.txt" })
            Check(!AceCoreFileCleanup.IsCoreSysFile(Path.Combine(root, name)), "排除其他文件：" + name);

        var normal = New();
        normal.File("ACE-CORE.sys"); normal.Directory("version"); normal.File(@"version\ace-core-v2.SYS");
        foreach (var name in new[] { "ACE-BASE.sys", "xACE-CORE.sys", "ACE-CORE.dll", "ACE-CORE.sys.bak", @"version\data.bin" }) normal.File(name);
        var result = Run(normal);
        Check(Ok(result) && normal.Deleted.Count == 2, "只删除根目录和子目录的两个 CORE SYS");
        Check(normal.Deleted.All(AceCoreFileCleanup.IsCoreSysFile), "没有删除其他扩展名或其他 SYS");
        Check(normal.Nodes.ContainsKey(Path.Combine(root, "ACE-BASE.sys")) && normal.Nodes.ContainsKey(Path.Combine(root, "version")),
            "其他驱动与目录保持存在");
        Check(normal.Nodes.Keys.Count(p => p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) == 7, "所有非目标文件及目录保留");
        Check(!AceCleanupResults.ToOperationResult(result).RequiresReboot, "不登记延迟删除或重启要求");
        Check(Ok(Run(normal)) && normal.Deleted.Count == 2, "重复清理安全跳过，不重复删除");
        var missing = New(); missing.Nodes.Remove(root);
        Check(Ok(Run(missing)) && missing.Deleted.Count == 0, "未安装目录不创建任何文件");
        var empty = New(); empty.File("ACE-BASE.sys");
        Check(Ok(Run(empty)) && empty.Deleted.Count == 0, "没有 CORE SYS 时无操作");

        foreach (var blocked in new[] { "game", "guard", "not-elevated", "custom", "unknown" })
        {
            var fake = New(); fake.File("ACE-CORE.sys");
            var blockedResult = Run(fake, elevated: () => blocked != "not-elevated", custom: () => blocked == "custom",
                blockers: () => blocked == "unknown" ? throw new IOException("unknown process state")
                    : blocked is "game" or "guard" ? [blocked] : []);
            Check(!Ok(blockedResult) && fake.Reads == 0 && fake.Deleted.Count == 0, "前置拒绝不读取目录：" + blocked);
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var beforeCancel = New(); beforeCancel.File("ACE-CORE.sys");
        Check(Run(beforeCancel, cancelled.Token).Status == BackendResultStatuses.Cancelled && beforeCancel.Reads == 0,
            "启动前取消不访问 ACE 目录");

        foreach (var redirected in new[] { @"C:\Program Files", root, Path.Combine(root, "version"), Path.Combine(root, "ACE-CORE.sys") })
        {
            var fake = New(); fake.File("ACE-CORE.sys"); fake.Directory("version");
            fake.Nodes[redirected] |= FileAttributes.ReparsePoint;
            Check(!Ok(Run(fake)) && fake.Deleted.Count == 0, "祖先/根/子目录/目标文件链接拒绝：" + redirected);
        }
        var escape = New(); escape.File("ACE-CORE.sys"); escape.ExtraEntry = @"C:\Windows\ACE-CORE.sys";
        Check(!Ok(Run(escape)) && escape.Deleted.Count == 0, "完整预检拒绝目录外文件，尚未删除任何目标");
        var sibling = New(); sibling.ExtraEntry = @"C:\Program Files\AntiCheatExpert-other\ACE-CORE.sys";
        Check(!Ok(Run(sibling)) && sibling.Deleted.Count == 0, "同名前缀兄弟目录不属于清理范围");
        var relative = New(); relative.ExtraEntry = "ACE-CORE.sys";
        Check(!Ok(Run(relative)) && relative.Deleted.Count == 0, "不接受相对路径");
        var unreadable = New(); unreadable.File("ACE-CORE.sys"); unreadable.ReadFailure = root;
        Check(!Ok(Run(unreadable)) && unreadable.Deleted.Count == 0, "读取权限失败不能当作未安装");
        var scanFailed = New(); scanFailed.File("ACE-CORE.sys"); scanFailed.FailEnumeration = true;
        Check(!Ok(Run(scanFailed)) && scanFailed.Deleted.Count == 0, "扫描失败不执行部分扫描结果");

        var locked = New(); locked.File("ACE-CORE-1.sys"); locked.File("ACE-CORE-2.sys"); locked.Locked = Path.Combine(root, "ACE-CORE-2.sys");
        var partial = AceCleanupResults.ToOperationResult(Run(locked));
        Check(!partial.Success && partial.IsPartial && locked.Deleted.Count == 1 && locked.Nodes.ContainsKey(locked.Locked),
            "第二文件占用时保留该文件，准确报告部分完成");
        Check(!partial.RequiresReboot && partial.Message.Contains("未安排重启删除"), "占用不悄悄登记重启删除");
        var noEffect = New(); noEffect.File("ACE-CORE.sys"); noEffect.IgnoreDelete = true;
        Check(!Ok(Run(noEffect)) && noEffect.Nodes.ContainsKey(Path.Combine(root, "ACE-CORE.sys")), "删除调用未生效不能报成功");
        var postUnknown = New(); postUnknown.File("ACE-CORE.sys");
        postUnknown.AfterDelete = p => postUnknown.ReadFailure = p;
        var unknown = AceCleanupResults.ToOperationResult(Run(postUnknown));
        Check(!unknown.Success && unknown.OutcomeUnknown, "写后复检无法读取时保留未知结果");
        var race = New(); race.File("ACE-CORE-1.sys"); race.File("ACE-CORE-2.sys");
        var late = AceCleanupResults.ToOperationResult(Run(race, blockers: () => race.Deleted.Count > 0 ? ["sguard64"] : []));
        Check(!late.Success && late.IsPartial && race.Deleted.Count == 1, "删除期间守卫启动即停止后续文件");
        var cancelDuring = New(); cancelDuring.File("ACE-CORE-1.sys"); cancelDuring.File("ACE-CORE-2.sys");
        using var afterFirst = new CancellationTokenSource(); cancelDuring.AfterDelete = _ => afterFirst.Cancel();
        Check(Run(cancelDuring, afterFirst.Token).Status == BackendResultStatuses.Partial && cancelDuring.Deleted.Count == 1,
            "部分删除后取消不误报完整成功");
        var replaced = New(); replaced.File("ACE-CORE.sys"); replaced.ReparseOnSecondFileRead = true;
        Check(!Ok(Run(replaced)) && replaced.Deleted.Count == 0, "扫描后文件变为链接，写前复核仍拒绝");
        Console.WriteLine($"ACE-CORE file cleanup checks: {count} passed. File operations simulated; no real ACE files changed.");
    }

    private sealed class Fake : IAceCoreFileEnvironment
    {
        internal Dictionary<string, FileAttributes> Nodes = new(StringComparer.OrdinalIgnoreCase)
        { [@"C:\"] = FileAttributes.Directory, [@"C:\Program Files"] = FileAttributes.Directory, [AceCoreFileCleanup.Root] = FileAttributes.Directory };
        internal List<string> Deleted = [];
        internal int Reads;
        internal string? ExtraEntry, Locked, ReadFailure;
        internal bool FailEnumeration, IgnoreDelete, ReparseOnSecondFileRead;
        internal Action<string>? AfterDelete;
        private readonly Dictionary<string, int> _reads = new(StringComparer.OrdinalIgnoreCase);
        internal void File(string path) => Nodes[Path.Combine(AceCoreFileCleanup.Root, path)] = FileAttributes.Normal;
        internal void Directory(string path) => Nodes[Path.Combine(AceCoreFileCleanup.Root, path)] = FileAttributes.Directory;
        public FileAttributes GetAttributes(string path)
        {
            Reads++;
            if (path == ReadFailure) throw new UnauthorizedAccessException("fixture unreadable: " + path);
            if (!Nodes.TryGetValue(path, out var attributes)) throw new FileNotFoundException("fixture missing", path);
            _reads[path] = _reads.GetValueOrDefault(path) + 1;
            if (ReparseOnSecondFileRead && AceCoreFileCleanup.IsCoreSysFile(path) && _reads[path] > 1) return attributes | FileAttributes.ReparsePoint;
            return attributes;
        }
        public IReadOnlyList<string> EnumerateEntries(string directory)
        {
            if (FailEnumeration) throw new IOException("fixture enumeration failed");
            var entries = Nodes.Keys.Where(p => string.Equals(Path.GetDirectoryName(p), directory, StringComparison.OrdinalIgnoreCase)).ToList();
            if (ExtraEntry is not null) entries.Add(ExtraEntry);
            return entries;
        }
        public void DeleteFile(string path)
        {
            if (path == Locked) throw new IOException("fixture file in use: " + path);
            if (IgnoreDelete) return;
            Nodes.Remove(path); Deleted.Add(path); AfterDelete?.Invoke(path);
        }
    }
}
