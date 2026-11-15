using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using DeltaNFD.Services.Plugins;
using Plugin.DpcTimerHold;

namespace BackendSmokeTest;

/// <summary>
/// 「DPC 计时器常驻（Win10 研究）」插件检查。
///
/// 覆盖五件事：
///   1) 包合同：仓库内 manifest.json / ui.json / README / LICENSE 是否满足宿主真实解析器与规范；
///   2) 规则与文件身份：Windows 10 版本门禁、原版 DPC 1.4.0 的字节数/SHA256 核验（真实文件存在时）；
///   3) 常驻记录：原子写入、损坏记录、字段校验；
///   4) 状态机：用假进程/假服务/假计时器驱动真实控制器，覆盖启动、超时、冲突、释放、遗留服务、身份不符；
///   5) 真实后端只读端到端 + 真实导入器接受真实包。
///
/// 本检查**不会**加载驱动、启动原版 DPC、修改服务或写注册表：真机路径在 Win11 或缺少原版文件时
/// 只走到「拒绝」分支；释放分支只读查询服务状态。
/// </summary>
internal static class PluginDpcTimerHoldChecks
{
    private static int _failures;
    private static int _checks;
    private static Guid _lastRequestId;

    public static void Run()
    {
        RunPackageContractChecks();
        RunRulesAndIdentityChecks();
        RunJournalChecks();
        RunControllerChecks();

        var exe = FindPluginBackend(FindRepoRoot());
        if (exe is null)
        {
            _checks++;
            _failures++;
            Console.WriteLine("  [失败] 未找到插件后端产物（先构建 src/Plugin.DpcTimerHold）：导入与端到端检查未执行。");
        }
        else
        {
            RunPackageImportCheck(exe);
            RunBackendEndToEnd(exe);
        }

        Console.WriteLine($"DPC 计时器常驻插件检查完成：{_checks} 项，失败 {_failures} 项。");
        if (_failures > 0) Environment.ExitCode = 1;
    }

    private static void Check(string name, bool condition, string? detail = null)
    {
        _checks++;
        if (condition) return;
        _failures++;
        Console.WriteLine($"  [失败] {name}{(detail is null ? "" : "：" + detail)}");
    }

    private static void Skip(string name, string reason) => Console.WriteLine($"  （跳过）{name}：{reason}");

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DeltaNFD.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("未找到仓库根目录。");
    }

    // ---------------------------------------------------------------- 1) 包合同

    private static void RunPackageContractChecks()
    {
        Console.WriteLine("== DPC 计时器常驻插件：包合同 ==");
        var root = FindRepoRoot();
        var pluginDir = Path.Combine(root, "src", "Plugin.DpcTimerHold");
        var packageDir = Path.Combine(pluginDir, "package");

        var manifest = PluginManifestParser.Parse(File.ReadAllBytes(Path.Combine(packageDir, "manifest.json")));
        var ui = PluginUiParser.Parse(File.ReadAllBytes(Path.Combine(packageDir, "ui.json")), manifest);

        Check("manifest 为 schema 2 并声明侧栏入口",
            manifest.SchemaVersion == 2 &&
            manifest.Navigation is { Label: "DPC计时器常驻", Icon: "tools", PageId: "main" });
        Check("插件 ID 与核心常量一致", manifest.Id == DpcRules.PluginId, manifest.Id);
        Check("名称/作者/版本符合预期",
            manifest.Name.Contains("DPC") && manifest.Author == "DRXiaoXi" && manifest.Version == "1.0.0");
        Check("兼容当前宿主数值版本 0.9350.0", manifest.IsCompatible(new Version(0, 9350, 0, 0)));
        Check("不提前宣称兼容 1.0.0", !manifest.IsCompatible(new Version(1, 0, 0)));
        Check("不兼容更早的 0.9349.0", !manifest.IsCompatible(new Version(0, 9349, 0)));
        Check("后端入口为包内 x64 EXE",
            manifest.Backend.Entry == "backend/Plugin.DpcTimerHold.exe" && manifest.Backend.Architecture == "x64");
        Check("协议版本为 1.0", manifest.ProtocolMajor == 1 && manifest.ProtocolMinor == 0);

        var probe = manifest.FindOperation("probe");
        var start = manifest.FindOperation("start");
        Check("probe 是只读操作", probe is { Mutating: false, Reversible: false, TargetScoped: false } &&
            probe.ResourceIds.Count == 0);
        Check("start 是声明 timer.resolution 的可恢复修改操作",
            start is { Mutating: true, Reversible: true, TargetScoped: false } &&
            start.ResourceIds.SequenceEqual(["timer.resolution"]));
        Check("两个操作超时都在 1-300 秒内",
            manifest.Operations.All(o => o.TimeoutSeconds is >= 1 and <= 300));
        Check("不请求持续运行或脱机自主运行",
            !manifest.Capabilities.Continuous && !manifest.Capabilities.OfflineAutonomous);
        Check("权限类别仅为 process.launch 与 system.modify",
            manifest.Permissions.Select(p => p.Id).OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual(["process.launch", "system.modify"]));
        Check("每项权限用途非空且说明实际对象",
            manifest.Permissions.All(p => p.Purpose.Trim().Length > 0) &&
            manifest.Permissions.Any(p => p.Purpose.Contains("DPC")) &&
            manifest.Permissions.Any(p => p.Purpose.Contains("dpclat_driver")));

        Check("ui.json 控件数为 8", ui.Controls.Count == 8, ui.Controls.Count.ToString());
        var toggle = ui.FindControl("risk");
        Check("风险确认是可默认未勾选的 toggle", toggle is { Type: PluginControlType.Toggle, DefaultToggle: false });
        var pathInput = ui.FindControl("exePath");
        Check("路径输入是可留空的 input", pathInput is { Type: PluginControlType.Input } &&
            string.IsNullOrEmpty(pathInput.DefaultInput));
        Check("按钮都绑定了已声明的操作",
            ui.Controls.Where(c => c.Type == PluginControlType.Button)
                .All(c => c.OperationId is not null && manifest.HasOperation(c.OperationId)) &&
            ui.Controls.Count(c => c.Type == PluginControlType.Button) == 2);

        foreach (var required in new[] { "manifest.json", "ui.json", "README.md", "LICENSE.txt" })
            Check($"包内包含 {required}", File.Exists(Path.Combine(packageDir, required)));

        var readme = File.ReadAllText(Path.Combine(packageDir, "README.md"));
        Check("README 说明作者/支持系统/风险/恢复/脱机",
            readme.Contains("作者") && readme.Contains("支持系统") && readme.Contains("风险") &&
            readme.Contains("恢复") && readme.Contains("脱机"));
        Check("README 说明前置条件与文件身份",
            readme.Contains("306928") && readme.Contains(DpcRules.ExeSha256) &&
            readme.Contains("不包含") && readme.Contains("不附带"));
        Check("README 说明已知漏洞与内核风险",
            readme.Contains("CVE-2025-51619") && readme.Contains("蓝屏") && readme.Contains("Secure Boot"));
        Check("README 说明 1.0000ms 与效果未验证",
            readme.Contains("1.0000ms") && readme.Contains("不保证") && readme.Contains("跨进程"));
        Check("README 说明直接改写范围与记录位置",
            readme.Contains("直接改写范围") && readme.Contains("PluginData") && readme.Contains("hold-state.json"));
        Check("README 不宣传反作弊零风险", !readme.Contains("零封号") && !readme.Contains("反作弊认可"));
        Check("README 说明与插件规范第 1 节的关系",
            readme.Contains("插件规范第 1 节") && readme.Contains("本地研究插件"));

        var program = File.ReadAllText(Path.Combine(pluginDir, "Program.cs"));
        var core = File.ReadAllText(Path.Combine(pluginDir, "DpcTimerCore.cs"));
        var native = File.ReadAllText(Path.Combine(pluginDir, "DpcTimerNative.cs"));
        Check("插件后端不引用宿主程序集",
            !program.Contains("using DeltaNFD") && !core.Contains("using DeltaNFD") && !native.Contains("using DeltaNFD"));
        Check("插件不写回包目录或安装目录",
            !program.Contains("AppContext.BaseDirectory") && !core.Contains("AppContext.BaseDirectory") &&
            !native.Contains("AppContext.BaseDirectory"));
        Check("插件源码不直接加载/请求内核计时器（无 ExSetTimerResolution/NtSetTimerResolution 调用）",
            !core.Contains("[DllImport") &&
            !native.Contains("ExSetTimerResolution") && !native.Contains("NtSetTimerResolution"));
        Check("插件只做只读计时器查询（NtQueryTimerResolution）",
            native.Contains("NtQueryTimerResolution") && !native.Contains("NtSetTimerResolution"));
        Check("插件源码不打开 DPC 设备或创建内核服务",
            !native.Contains("dpclat_static_device") && !native.Contains("CreateService"));
        Check("插件只按名称打开/查询/删除服务，不写注册表",
            !native.Contains("Registry") && !native.Contains("RegSetValue"));

        Console.WriteLine("  包合同检查完成。");
    }

    // ---------------------------------------------------------------- 2) 规则与真实文件身份

    private static void RunRulesAndIdentityChecks()
    {
        Console.WriteLine("== DPC 计时器常驻插件：规则与文件身份 ==");
        Check("Win10 1809 x64 支持", DpcRules.IsSupportedOs(10, 17763, true));
        Check("Win10 19045 x64 支持", DpcRules.IsSupportedOs(10, 19045, true));
        Check("Win10 21999 x64 支持", DpcRules.IsSupportedOs(10, 21999, true));
        Check("Win10 22000 拒绝", !DpcRules.IsSupportedOs(10, 22000, true));
        Check("Win10 17762 拒绝", !DpcRules.IsSupportedOs(10, 17762, true));
        Check("Win11 22631 拒绝", !DpcRules.IsSupportedOs(11, 22631, true));
        Check("32 位进程拒绝", !DpcRules.IsSupportedOs(10, 19045, false));
        Check("读数格式化 10000→1.0000ms", DpcRules.Format100ns(10000) == "1.0000ms", DpcRules.Format100ns(10000));
        Check("请求值格式化 1000 微秒→1.0000ms", DpcRules.FormatMicroseconds(1000) == "1.0000ms");
        Check("读数关系描述", DpcRules.Relation(10000, 10000) == "等于目标" &&
            DpcRules.Relation(5000, 10000) == "比目标更细" && DpcRules.Relation(156250, 10000) == "比目标更粗");
        Check("服务映像路径规范化", DpcRules.NormalizeImagePath(@"\??\C:\Windows\System32\drivers\dpclat.sys") ==
            @"C:\Windows\System32\drivers\dpclat.sys" &&
            DpcRules.NormalizeImagePath("C:/Windows/dpclat.sys") == @"C:\Windows\dpclat.sys");
        var backupId = DpcRules.NewBackupId();
        Check("恢复标识带固定前缀且不重复",
            backupId.StartsWith(DpcRules.BackupPrefix, StringComparison.Ordinal) &&
            backupId.Length <= 128 && backupId != DpcRules.NewBackupId());

        Check("身份核验接受精确字节数与 SHA",
            DpcInput.VerifyIdentity(DpcRules.ExeLength, DpcRules.ExeSha256).Ok);
        Check("身份核验拒绝错误长度",
            !DpcInput.VerifyIdentity(DpcRules.ExeLength - 1, DpcRules.ExeSha256).Ok);
        Check("身份核验拒绝错误 SHA",
            !DpcInput.VerifyIdentity(DpcRules.ExeLength, new string('0', 64)).Ok);

        var real = FindRealDpcExe(FindRepoRoot());
        if (real is null)
        {
            Skip("真实原版 DPC 文件身份", "工作区未找到原版 1.4.0 EXE（仅做合成身份核验）");
        }
        else
        {
            var identity = DpcInput.VerifyFile(real);
            Check("真实原版 DPC 文件通过身份核验", identity.Ok, identity.Error);
            Check("真实原版 DPC 长度与 SHA 精确匹配",
                identity.Length == DpcRules.ExeLength &&
                string.Equals(identity.Sha256, DpcRules.ExeSha256, StringComparison.OrdinalIgnoreCase));

            using (var locked = DpcInput.OpenLocked(real, out var lockedIdentity))
                Check("只读锁定的文件身份仍匹配", locked is not null && lockedIdentity.Ok, lockedIdentity.Error);

            var temp = Path.Combine(Path.GetTempPath(), "dnfd-dpc-tampered-" + Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                var bytes = File.ReadAllBytes(real);
                bytes[bytes.Length / 2] ^= 0x5A;
                File.WriteAllBytes(temp, bytes);
                Check("被修改一个字节的副本被拒绝", !DpcInput.VerifyFile(temp).Ok);
            }
            finally
            {
                try { File.Delete(temp); } catch (IOException) { }
            }
        }

        var tiny = Path.Combine(Path.GetTempPath(), "dnfd-dpc-tiny-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            File.WriteAllBytes(tiny, new byte[100]);
            Check("过小文件被拒绝", !DpcInput.VerifyFile(tiny).Ok);
            Check("路径解析拒绝相对路径",
                DpcInput.Resolve("DPCLatencyChecker_V1.4.exe", Path.GetTempPath(), out _) is null);
            Check("路径解析拒绝引号",
                DpcInput.Resolve("\"C:\\x\\y.exe\"", Path.GetTempPath(), out _) is null);
            Check("路径解析拒绝 UNC",
                DpcInput.Resolve(@"\\server\share\DPCLatencyChecker_V1.4.exe", Path.GetTempPath(), out _) is null);
        }
        finally
        {
            try { File.Delete(tiny); } catch (IOException) { }
        }

        Console.WriteLine("  规则与文件身份检查完成。");
    }

    private static string? FindRealDpcExe(string repoRoot)
    {
        var workspaceRoot = Path.GetFullPath(Path.Combine(repoRoot, "..", ".."));
        foreach (var root in new[] { repoRoot, workspaceRoot })
            foreach (var relative in new[]
                     {
                         @"_buildcheck\pendulum-dual-methods-20261104\downloads\dpclat-1.4.0-reference.exe",
                         @"_buildcheck\pendulum-dual-methods-20261104\network-verification\packages\dpclat-1.4.0.exe",
                         @"_buildcheck\pendulum-dual-methods-20261104\verification\test-data\packages\dpclat-1.4.0.exe",
                     })
            {
                var candidate = Path.Combine(root, relative);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    // ---------------------------------------------------------------- 3) 常驻记录

    private static void RunJournalChecks()
    {
        Console.WriteLine("== DPC 计时器常驻插件：常驻记录 ==");
        var dataDirectory = Path.Combine(Path.GetTempPath(), "dnfd-dpc-journal-" + Guid.NewGuid().ToString("N"));
        try
        {
            Check("缺失记录返回 null 且无错误", HoldJournalStore.Load(dataDirectory, out var missingError) is null &&
                missingError.Length == 0);
            var journal = new HoldJournal
            {
                Phase = HoldJournalStore.PhaseHolding,
                BackupId = DpcRules.NewBackupId(),
                Pid = 1234,
                StartTicksUtc = 638000000000000000,
                ExePath = @"C:\DPC\DPCLatencyChecker_V1.4.exe",
                ExeSha256 = DpcRules.ExeSha256,
                ServiceImagePath = @"C:\Windows\System32\drivers\dpclat.sys",
                RequestedMicroseconds = 1000,
                Current100nsAtStart = 10000,
                CreatedUtc = "2026-11-14T00:00:00Z",
                Detail = "test",
            };
            Check("记录原子写入成功", HoldJournalStore.Save(dataDirectory, journal, out var saveError), saveError);
            var loaded = HoldJournalStore.Load(dataDirectory, out var loadError);
            Check("记录往返字段一致", loaded is not null && loadError.Length == 0 &&
                loaded.Phase == HoldJournalStore.PhaseHolding && loaded.Pid == 1234 &&
                loaded.BackupId == journal.BackupId && loaded.ServiceImagePath == journal.ServiceImagePath &&
                loaded.Current100nsAtStart == 10000 && loaded.UpdatedUtc.Length > 0);
            Check("写入后没有遗留临时文件",
                !Directory.EnumerateFiles(dataDirectory, "hold-state.json.tmp-*").Any());

            var corrupt = Path.Combine(dataDirectory, DpcRules.JournalFileName);
            File.WriteAllText(corrupt, "{not json");
            Check("损坏记录被拒绝并给出错误", HoldJournalStore.Load(dataDirectory, out var corruptError) is null &&
                corruptError.Length > 0);

            File.WriteAllText(corrupt, "{\"schemaVersion\":1,\"phase\":\"holding\",\"pid\":0}");
            Check("holding 缺少进程身份被拒绝", HoldJournalStore.Load(dataDirectory, out _) is null);

            File.WriteAllText(corrupt, "{\"schemaVersion\":1,\"phase\":\"weird\"}");
            Check("未知 phase 被拒绝", HoldJournalStore.Load(dataDirectory, out _) is null);

            HoldJournalStore.Delete(dataDirectory);
            Check("删除记录后不再存在", !File.Exists(corrupt) &&
                HoldJournalStore.Load(dataDirectory, out _) is null);
        }
        finally
        {
            try { Directory.Delete(dataDirectory, recursive: true); } catch (IOException) { }
        }
        Console.WriteLine("  常驻记录检查完成。");
    }

    // ---------------------------------------------------------------- 4) 状态机（假进程/假服务/假计时器）

    private static void RunControllerChecks()
    {
        Console.WriteLine("== DPC 计时器常驻插件：状态机 ==");
        var root = Path.Combine(Path.GetTempPath(), "dnfd-dpc-controller-" + Guid.NewGuid().ToString("N"));
        try
        {
            // 只读检查：Win11 上明确提示不支持。
            var win11 = NewHarness(new FakeEnvironment { OsMajor = 11, OsBuild = 22631 });
            var probe = win11.Controller.Probe("", win11.DataDirectory);
            Check("Win11 probe 不报成功且提示系统不支持",
                probe.Status != "success" &&
                probe.Items.Any(i => i.Id == "os" && i.Message.Contains("Windows 10")));
            Check("Win11 probe 不启动进程", win11.Process.LaunchCount == 0);

            // 风险确认先于系统门禁。
            var noRisk = win11.Controller.Start("", false, win11.DataDirectory, CancellationToken.None);
            Check("未勾选风险确认被拒绝", noRisk.Status == "failed" && noRisk.Code == "RISK_NOT_ACCEPTED" &&
                noRisk.BackupIds.Count == 1 && noRisk.BackupIds[0].StartsWith(DpcRules.BackupPrefix, StringComparison.Ordinal));
            Check("风险拒绝不启动进程", win11.Process.LaunchCount == 0);

            var unsupported = win11.Controller.Start("", true, win11.DataDirectory, CancellationToken.None);
            Check("Win11 启动被拒绝", unsupported.Status == "failed" && unsupported.Code == "UNSUPPORTED_OS");
            Check("Win11 拒绝不启动进程", win11.Process.LaunchCount == 0);

            var nonAdmin = NewHarness(new FakeEnvironment { OsMajor = 10, OsBuild = 19045, IsAdmin = false });
            var nonAdminResult = nonAdmin.Controller.Start("", true, nonAdmin.DataDirectory, CancellationToken.None);
            Check("非管理员被拒绝", nonAdminResult.Status == "failed" && nonAdminResult.Code == "UNAUTHORIZED" &&
                nonAdmin.Process.LaunchCount == 0);

            var missing = NewHarness(new FakeEnvironment());
            missing.Input.Path = null;
            missing.Input.ResolveError = "没有文件";
            var missingResult = missing.Controller.Start("", true, missing.DataDirectory, CancellationToken.None);
            Check("缺少原版文件被拒绝", missingResult.Status == "failed" && missingResult.Code == "INPUT_MISSING" &&
                missing.Process.LaunchCount == 0);

            var mismatch = NewHarness(new FakeEnvironment());
            mismatch.Input.Identity = DpcInput.Identity.Fail("不是原版 DPC");
            var mismatchResult = mismatch.Controller.Start("", true, mismatch.DataDirectory, CancellationToken.None);
            Check("身份不匹配被拒绝", mismatchResult.Status == "failed" && mismatchResult.Code == "INPUT_MISMATCH" &&
                mismatch.Process.LaunchCount == 0);

            // 成功路径。
            var ok = NewHarness(new FakeEnvironment());
            var okResult = ok.Controller.Start(@"C:\DPC\DPCLatencyChecker_V1.4.exe", true, ok.DataDirectory,
                CancellationToken.None);
            Check("启动成功并返回恢复标识", okResult.Status == "success" && okResult.Code == "OK" &&
                okResult.PendingRestore && okResult.BackupIds.Count == 1);
            Check("启动结果包含服务/分辨率/释放说明",
                okResult.Items.Any(i => i.Id == "service" && i.Message.Contains("RUNNING")) &&
                okResult.Items.Any(i => i.Id == "resolution" && i.Message.Contains("1.0000ms")) &&
                okResult.Items.Any(i => i.Id == "release" && i.Message.Contains("恢复备份")));
            Check("成功路径只启动一次 DPC 且使用已核验路径",
                ok.Process.LaunchCount == 1 && ok.Process.LastPath == @"C:\DPC\DPCLatencyChecker_V1.4.exe");
            var okJournal = HoldJournalStore.Load(ok.DataDirectory, out var okJournalError);
            Check("成功路径写入 holding 记录",
                okJournal is not null && okJournalError.Length == 0 &&
                okJournal.Phase == HoldJournalStore.PhaseHolding && okJournal.Pid == ok.Process.LastOwned!.Pid &&
                okJournal.Current100nsAtStart == 10000 &&
                okJournal.ServiceImagePath == ok.Service.ImagePath);

            var again = ok.Controller.Start("", true, ok.DataDirectory, CancellationToken.None);
            Check("已常驻时再次启动被拒绝且不重复启动",
                again.Status == "failed" && again.Code == "ALREADY_HOLDING" && ok.Process.LaunchCount == 1);

            // 释放。
            var backupId = okJournal!.BackupId;
            var restore = ok.Controller.Restore(backupId, ok.DataDirectory, CancellationToken.None);
            Check("恢复成功并正常关闭 DPC", restore.Status == "success" && restore.Code == "OK" &&
                ok.Process.CloseCount == 1 && !ok.Process.Alive);
            Check("恢复后记录已删除且服务已不存在",
                !File.Exists(HoldJournalStore.JournalPath(ok.DataDirectory)) && !ok.Service.Query("dpclat_driver").Exists);
            var restoreAgain = ok.Controller.Restore(backupId, ok.DataDirectory, CancellationToken.None);
            Check("重复恢复是幂等的", restoreAgain.Status == "success" && restoreAgain.Code == "OK");
            var foreign = ok.Controller.Restore("not-ours", ok.DataDirectory, CancellationToken.None);
            Check("拒绝非本插件恢复标识", foreign.Status == "failed" && foreign.Code == "RESTORE_FAILED");

            // 服务冲突：已有 RUNNING 的 dpclat_driver 时不启动。
            var conflict = NewHarness(new FakeEnvironment());
            conflict.Service.PreExisting = true;
            conflict.Service.PreState = (uint)DpcServiceState.Running;
            var conflictResult = conflict.Controller.Start("", true, conflict.DataDirectory, CancellationToken.None);
            Check("已有服务时拒绝启动且不结束他人进程",
                conflictResult.Status == "failed" && conflictResult.Code == "SERVICE_CONFLICT" &&
                conflict.Process.LaunchCount == 0);

            // 服务查询失败时不启动。
            var queryFail = NewHarness(new FakeEnvironment());
            queryFail.Service.QueryThrows = true;
            var queryFailResult = queryFail.Controller.Start("", true, queryFail.DataDirectory, CancellationToken.None);
            Check("服务状态不可读时拒绝启动", queryFailResult.Status == "failed" &&
                queryFailResult.Code == "SERVICE_QUERY_FAILED" && queryFail.Process.LaunchCount == 0);

            // 超时：驱动未持有请求。
            var timeout = NewHarness(new FakeEnvironment());
            timeout.Timer.Value = 156250;
            var timeoutResult = timeout.Controller.Start("", true, timeout.DataDirectory, CancellationToken.None);
            Check("未观察到内核请求时超时失败", timeoutResult.Status == "failed" &&
                timeoutResult.Code == "HOLD_TIMEOUT" && timeout.Process.LaunchCount == 1);
            Check("超时后正常关闭本次启动的 DPC 并记录为已释放",
                timeout.Process.CloseCount == 1 && !timeout.Process.Alive &&
                !timeoutResult.PendingRestore &&
                HoldJournalStore.Load(timeout.DataDirectory, out _) is { Phase: HoldJournalStore.PhaseReleased });

            // DPC 进程提前退出。
            var exited = NewHarness(new FakeEnvironment());
            exited.Process.ExitAfterLaunch = true;
            var exitedResult = exited.Controller.Start("", true, exited.DataDirectory, CancellationToken.None);
            Check("DPC 提前退出时报错", exitedResult.Status == "failed" && exitedResult.Code == "DPC_EXITED");
            Check("提前退出后不留下常驻记录", !exitedResult.PendingRestore &&
                HoldJournalStore.Load(exited.DataDirectory, out _) is { Phase: HoldJournalStore.PhaseReleased });

            // 释放失败：不冒充成功。
            var closeFail = NewHarness(new FakeEnvironment());
            var closeFailStart = closeFail.Controller.Start("", true, closeFail.DataDirectory, CancellationToken.None);
            closeFail.Process.CloseResult = false;
            var closeFailRestore = closeFail.Controller.Restore(closeFailStart.BackupIds[0], closeFail.DataDirectory,
                CancellationToken.None);
            Check("DPC 未正常退出时恢复失败并保留记录",
                closeFailRestore.Status == "failed" && closeFailRestore.Code == "RESTORE_FAILED" &&
                closeFailRestore.PendingRestore &&
                File.Exists(HoldJournalStore.JournalPath(closeFail.DataDirectory)));

            // 遗留服务：DPC 异常退出后服务仍在，按记录身份清理。
            var leftover = NewHarness(new FakeEnvironment());
            var leftoverStart = leftover.Controller.Start("", true, leftover.DataDirectory, CancellationToken.None);
            leftover.Process.Alive = false;
            leftover.Service.LeaveServiceAfterExit = true;
            var leftoverRestore = leftover.Controller.Restore(leftoverStart.BackupIds[0], leftover.DataDirectory,
                CancellationToken.None);
            Check("遗留服务按记录身份停止并删除",
                leftoverRestore.Status == "success" && leftover.Service.StopDeleteCount == 1);

            // 遗留服务身份不符：不删除。
            var mismatchService = NewHarness(new FakeEnvironment());
            var mismatchStart = mismatchService.Controller.Start("", true, mismatchService.DataDirectory,
                CancellationToken.None);
            mismatchService.Process.Alive = false;
            mismatchService.Service.LeaveServiceAfterExit = true;
            mismatchService.Service.ImagePath = @"C:\Other\evil.sys";
            var mismatchRestore = mismatchService.Controller.Restore(mismatchStart.BackupIds[0],
                mismatchService.DataDirectory, CancellationToken.None);
            Check("遗留服务身份不符时拒绝删除并保留记录",
                mismatchRestore.Status == "failed" && mismatchRestore.Code == "RESTORE_FAILED" &&
                mismatchService.Service.StopDeleteCount == 0 &&
                File.Exists(HoldJournalStore.JournalPath(mismatchService.DataDirectory)));

            // 记录损坏：无服务时按已释放处理；有服务时拒绝。
            var corrupt = NewHarness(new FakeEnvironment());
            Directory.CreateDirectory(corrupt.DataDirectory);
            File.WriteAllText(HoldJournalStore.JournalPath(corrupt.DataDirectory), "{broken");
            var corruptRestore = corrupt.Controller.Restore(DpcRules.NewBackupId(), corrupt.DataDirectory,
                CancellationToken.None);
            Check("记录损坏且无服务时按已释放处理", corruptRestore.Status == "success");

            var corruptWithService = NewHarness(new FakeEnvironment());
            Directory.CreateDirectory(corruptWithService.DataDirectory);
            File.WriteAllText(HoldJournalStore.JournalPath(corruptWithService.DataDirectory), "{broken");
            corruptWithService.Service.PreExisting = true;
            corruptWithService.Service.PreState = (uint)DpcServiceState.Running;
            var corruptStart = corruptWithService.Controller.Start("", true, corruptWithService.DataDirectory,
                CancellationToken.None);
            Check("记录损坏且服务存在时拒绝启动", corruptStart.Status == "failed" &&
                corruptStart.Code == "JOURNAL_UNREADABLE" && corruptWithService.Process.LaunchCount == 0);

            // 常驻中 probe 报告进程与服务。
            var probing = NewHarness(new FakeEnvironment());
            var probingStart = probing.Controller.Start("", true, probing.DataDirectory, CancellationToken.None);
            var probingProbe = probing.Controller.Probe("", probing.DataDirectory);
            Check("常驻中 probe 报告记录与服务提示",
                probingProbe.Status == "partial" &&
                probingProbe.Items.Any(i => i.Id == "journal" && i.Message.Contains("进程仍在运行")) &&
                probingProbe.Items.Any(i => i.Id == "service" && i.Message.Contains("RUNNING")));
            Check("常驻中 probe 不启动/关闭进程",
                probing.Process.LaunchCount == 1 && probing.Process.CloseCount == 0 &&
                probingStart.Status == "success");

            Console.WriteLine("  状态机检查完成。");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class Harness
    {
        public required DpcHoldController Controller { get; init; }
        public required FakeProcessApi Process { get; init; }
        public required FakeServiceApi Service { get; init; }
        public required FakeTimerApi Timer { get; init; }
        public required FakeInputApi Input { get; init; }
        public required string DataDirectory { get; init; }
    }

    private static Harness NewHarness(FakeEnvironment environment)
    {
        var process = new FakeProcessApi();
        var service = new FakeServiceApi { Process = process };
        var timer = new FakeTimerApi();
        var input = new FakeInputApi();
        var dataDirectory = Path.Combine(Path.GetTempPath(), "dnfd-dpc-data-" + Guid.NewGuid().ToString("N"));
        var controller = new DpcHoldController(environment, process, service, timer, new FakeClock(), input);
        return new Harness
        {
            Controller = controller, Process = process, Service = service, Timer = timer, Input = input,
            DataDirectory = dataDirectory,
        };
    }

    private sealed class FakeEnvironment : IEnvironmentInfo
    {
        public int OsMajor { get; init; } = 10;
        public int OsBuild { get; init; } = 19045;
        public bool Is64BitProcess { get; init; } = true;
        public bool IsAdmin { get; init; } = true;
        public string SecureBootState { get; init; } = "关闭";
    }

    private sealed class FakeClock : IClock
    {
        private DateTimeOffset _now = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow => _now;
        public void Delay(int milliseconds) => _now = _now.AddMilliseconds(milliseconds);
    }

    private sealed class FakeInputApi : IInputApi
    {
        public string? Path { get; set; } = @"C:\DPC\DPCLatencyChecker_V1.4.exe";
        public string ResolveError { get; set; } = "";
        public DpcInput.Identity Identity { get; set; } =
            new(true, DpcRules.ExeLength, DpcRules.ExeSha256, "");

        public string? Resolve(string? raw, string dataDirectory, out string error)
        {
            error = ResolveError;
            return Path;
        }

        public DpcInput.Identity VerifyFile(string path) => Identity;

        public FileStream? OpenLocked(string path, out DpcInput.Identity identity)
        {
            identity = Identity;
            if (!Identity.Ok) return null;
            var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dnfd-dpc-lock-" + Guid.NewGuid().ToString("N"));
            return new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1,
                FileOptions.DeleteOnClose);
        }
    }

    private sealed class FakeProcessApi : IProcessApi
    {
        public bool Alive { get; set; } = true;
        public bool ExitAfterLaunch { get; set; }
        public int LaunchCount { get; private set; }
        public string? LastPath { get; private set; }
        public OwnedProcess? LastOwned { get; private set; }
        public bool CloseResult { get; set; } = true;
        public int CloseCount { get; private set; }

        public OwnedProcess Launch(string exePath)
        {
            LaunchCount++;
            LastPath = exePath;
            Alive = !ExitAfterLaunch;
            LastOwned = new OwnedProcess(4242, 638000000000000000, exePath);
            return LastOwned;
        }

        public bool IsAlive(OwnedProcess owned) => Alive;

        public bool CloseGracefully(OwnedProcess owned, int timeoutMs)
        {
            CloseCount++;
            if (!CloseResult) return false;
            Alive = false;
            return true;
        }
    }

    private sealed class FakeServiceApi : IServiceApi
    {
        public FakeProcessApi? Process { get; set; }
        public bool PreExisting { get; set; }
        public uint PreState { get; set; } = (uint)DpcServiceState.Running;
        public bool LeaveServiceAfterExit { get; set; }
        public bool QueryThrows { get; set; }
        public uint Type { get; set; } = 1;
        public uint StartType { get; set; } = 3;
        public string ImagePath { get; set; } = @"C:\Windows\System32\drivers\dpclat.sys";
        public bool StopDeleteResult { get; set; } = true;
        public int StopDeleteCount { get; private set; }

        public ServiceInfo Query(string name)
        {
            if (QueryThrows)
                return new ServiceInfo(false, 0, 0, 0, "", "fake service query failure");
            if (PreExisting) return new ServiceInfo(true, PreState, Type, StartType, ImagePath, "");
            if (Process is { LaunchCount: > 0 })
            {
                if (Process.Alive)
                    return new ServiceInfo(true, (uint)DpcServiceState.Running, Type, StartType, ImagePath, "");
                if (LeaveServiceAfterExit)
                    return new ServiceInfo(true, (uint)DpcServiceState.Stopped, Type, StartType, ImagePath, "");
            }
            return new ServiceInfo(false, 0, 0, 0, "", "");
        }

        public bool StopAndDelete(ServiceInfo expected, out string detail)
        {
            StopDeleteCount++;
            detail = "fake cleanup";
            if (!StopDeleteResult) return false;
            PreExisting = false;
            LeaveServiceAfterExit = false;
            return true;
        }
    }

    private sealed class FakeTimerApi : ITimerApi
    {
        public uint? Value { get; set; } = 10000;
        public uint? TryCurrent100ns(out string error)
        {
            error = "";
            return Value;
        }
    }

    // ---------------------------------------------------------------- 5) 真实导入器

    private static void RunPackageImportCheck(string exe)
    {
        Console.WriteLine("== DPC 计时器常驻插件：真实导入器接受真实包 ==");
        var root = Path.Combine(Path.GetTempPath(), "dnfd-dpc-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repoRoot = FindRepoRoot();
            var packageDir = Path.Combine(repoRoot, "src", "Plugin.DpcTimerHold", "package");
            var manifest = PluginManifestParser.Parse(File.ReadAllBytes(Path.Combine(packageDir, "manifest.json")));
            var staging = Path.Combine(root, "staging");
            Directory.CreateDirectory(Path.Combine(staging, "backend"));
            foreach (var name in new[] { "manifest.json", "ui.json", "README.md", "LICENSE.txt" })
                File.Copy(Path.Combine(packageDir, name), Path.Combine(staging, name));
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(exe)!))
                if (Path.GetExtension(file) is not (".pdb" or ".xml"))
                    File.Copy(file, Path.Combine(staging, "backend", Path.GetFileName(file)));

            var packagePath = Path.Combine(root, "dpc-timer-hold.dnfdplugin");
            ZipFile.CreateFromDirectory(staging, packagePath, CompressionLevel.Optimal, includeBaseDirectory: false);

            using (var archive = ZipFile.OpenRead(packagePath))
            {
                Check("包内不含原版 DPC EXE 或驱动字节",
                    archive.Entries.All(e => e.Length != DpcRules.ExeLength && e.Length != 21232) &&
                    archive.Entries.All(e => !e.FullName.Contains("dpclat", StringComparison.OrdinalIgnoreCase) &&
                                             !e.FullName.Contains("DPCLatencyChecker", StringComparison.OrdinalIgnoreCase)));
            }

            var backendExe = Path.Combine(staging, "backend", Path.GetFileName(exe));
            Check("后端入口是 x64 PE", IsX64Pe(backendExe));

            var pluginsRoot = Path.Combine(root, "Plugins");
            Directory.CreateDirectory(pluginsRoot);
            var index = new PluginPackageIndex(Path.Combine(pluginsRoot, "index.json"));
            var result = new PluginPackageImporter(index).Import(packagePath, pluginsRoot);

            Check("宿主导入器接受插件包", result.Succeeded, result.Error);
            Check("导入后默认禁用（需用户确认授权）",
                result.Entry is { State: PluginPackageState.ImportedDisabled });
            Check("安装目录与 backend 入口就位", result.Entry is not null && File.Exists(Path.Combine(
                pluginsRoot, DpcRules.PluginId, "1.0.0", "backend", "Plugin.DpcTimerHold.exe")));
            Check("索引记录包哈希并可按 ID 查询", result.Entry is not null &&
                index.TryGet(DpcRules.PluginId, out var indexed, out _) &&
                indexed!.PackageSha256 == result.Entry.PackageSha256 && indexed.Files.Count >= 5);
            Check("暂存目录已清理", !Directory.EnumerateDirectories(pluginsRoot, ".staging-*").Any());

            // 首次导入：真实 GUI 的插件根目录可能还不存在（Alpha0.935 首次导入失败的根因）。
            var freshRoot = Path.Combine(root, "FreshPlugins");
            var freshIndex = new PluginPackageIndex(Path.Combine(freshRoot, "index.json"));
            var freshResult = new PluginPackageImporter(freshIndex).Import(packagePath, freshRoot);
            Check("首次导入时插件安装根不存在也能自动创建",
                freshResult.Succeeded && Directory.Exists(freshRoot) && freshResult.Entry is not null,
                freshResult.Error);

            var shipped = Path.Combine(repoRoot, "dist", manifest.Id + "-" + manifest.Version + ".dnfdplugin");
            if (File.Exists(shipped))
            {
                var shippedRoot = Path.Combine(root, "ShippedPlugins");
                Directory.CreateDirectory(shippedRoot);
                var shippedIndex = new PluginPackageIndex(Path.Combine(shippedRoot, "index.json"));
                var shippedResult = new PluginPackageImporter(shippedIndex).Import(shipped, shippedRoot);
                Check("已打包的 dist 产物可被导入器接受", shippedResult.Succeeded, shippedResult.Error);
                using var archive = ZipFile.OpenRead(shipped);
                Check("dist 产物同样不含 DPC 载荷",
                    archive.Entries.All(e => e.Length != DpcRules.ExeLength && e.Length != 21232));
            }
            else
            {
                Console.WriteLine("  （dist 下没有已打包产物；运行 tools\\pack-plugin.ps1 后会一并检查。）");
            }

            Console.WriteLine("  真实导入检查完成。");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static bool IsX64Pe(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt16() != 0x5A4D) return false;
            stream.Position = 0x3C;
            var offset = reader.ReadInt32();
            if (offset <= 0 || offset + 6 > stream.Length) return false;
            stream.Position = offset;
            if (reader.ReadUInt32() != 0x00004550) return false;
            return reader.ReadUInt16() == 0x8664;
        }
        catch (IOException) { return false; }
    }

    // ---------------------------------------------------------------- 6) 真实后端只读端到端

    private static void RunBackendEndToEnd(string exe)
    {
        Console.WriteLine("== DPC 计时器常驻插件：后端只读端到端 ==");
        var pipeName = "DeltaNFD_DpcTimerCheck_" + Guid.NewGuid().ToString("N");
        var session = Guid.NewGuid();
        var dataDirectory = Path.Combine(Path.GetTempPath(), "dnfd-dpc-e2e-" + Guid.NewGuid().ToString("N"));
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var startInfo = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "--dnfd-pipe", pipeName, "--dnfd-session", session.ToString("D"),
                     "--dnfd-host-pid", Environment.ProcessId.ToString() })
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)!;
        try
        {
            Directory.CreateDirectory(dataDirectory);
            using (var connect = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                try { server.WaitForConnectionAsync(connect.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException)
                {
                    _checks++;
                    _failures++;
                    Console.WriteLine("  [失败] 插件后端未在 10 秒内连接：" +
                        (process.HasExited ? process.StandardError.ReadToEnd() : "(仍在运行)"));
                    return;
                }
            }

            _checks++;
            if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out var clientPid) || clientPid != process.Id)
            {
                _failures++;
                Console.WriteLine("  [失败] 管道客户端不是本次启动的插件后端。");
                return;
            }

            var nonce = Convert.ToBase64String(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());
            WriteServerFrame(server, "hello", session,
                "{\"nonce\":\"" + nonce + "\",\"hostVersion\":\"0.9350.0\",\"dataDirectory\":\"" +
                dataDirectory.Replace("\\", "\\\\") + "\"}");
            var ready = ReadServerFrame(server, 15);
            Check("ready 帧类型与会话", ready.Type == PluginIpcType.Ready && ready.SessionId == session &&
                ready.RequestId == _lastRequestId);
            var readyPayload = PluginIpc.ParseReady(ready.Payload);
            Check("ready 回显 nonce 与插件身份",
                readyPayload.Nonce == nonce && readyPayload.PluginId == DpcRules.PluginId &&
                readyPayload.PluginVersion == "1.0.0" && readyPayload.Pid == process.Id);

            WriteServerFrame(server, "invoke", session,
                "{\"operationId\":\"probe\",\"values\":{\"exePath\":\"\",\"risk\":false},\"target\":null}");
            var probeResult = ReadResultFrame(server, session, 45);
            var probe = PluginIpc.ParseResult(probeResult.Payload);
            Check("probe 返回结构合法的只读结果",
                PluginContract.ResultStatuses.Contains(probe.Status) && probe.Code.Length > 0 &&
                probe.Items.Count > 0 && !probe.PendingRestore && probe.BackupIds.Count == 0);
            Check("probe 报告系统/管理员/输入/服务/读数",
                probe.Items.Any(i => i.Id == "os") && probe.Items.Any(i => i.Id == "admin") &&
                probe.Items.Any(i => i.Id == "input") && probe.Items.Any(i => i.Id == "service") &&
                probe.Items.Any(i => i.Id == "timer"));

            WriteServerFrame(server, "invoke", session,
                "{\"operationId\":\"start\",\"values\":{\"exePath\":\"\",\"risk\":false},\"target\":null}");
            var noRisk = PluginIpc.ParseResult(ReadResultFrame(server, session, 60).Payload);
            Check("未勾选风险确认时拒绝启动",
                noRisk.Status == "failed" && noRisk.Code == "RISK_NOT_ACCEPTED" && noRisk.BackupIds.Count == 1);

            WriteServerFrame(server, "invoke", session,
                "{\"operationId\":\"start\",\"values\":{\"exePath\":\"\",\"risk\":true},\"target\":null}");
            var refused = PluginIpc.ParseResult(ReadResultFrame(server, session, 60).Payload);
            Check("真机启动被前置门禁拒绝（Win11 或缺少原版文件）",
                refused.Status == "failed" &&
                refused.Code is "UNSUPPORTED_OS" or "INPUT_MISSING" or "UNAUTHORIZED",
                refused.Code);
            Check("拒绝路径不留下常驻记录",
                !File.Exists(HoldJournalStore.JournalPath(dataDirectory)));

            WriteServerFrame(server, "invoke", session,
                "{\"operationId\":\"probe\",\"values\":{\"exePath\":\"\",\"risk\":false,\"unknown\":1},\"target\":null}");
            Check("拒绝未声明的输入字段",
                PluginIpc.ParseResult(ReadResultFrame(server, session, 15).Payload).Code == "PROTOCOL_ERROR");

            WriteServerFrame(server, "invoke", session,
                "{\"operationId\":\"nope\",\"values\":{\"exePath\":\"\",\"risk\":false},\"target\":null}");
            Check("拒绝未声明的操作",
                PluginIpc.ParseResult(ReadResultFrame(server, session, 15).Payload).Code is "PROTOCOL_ERROR" or "UNAUTHORIZED");

            WriteServerFrame(server, "restore", session, "{\"backupIds\":[\"not-ours\"]}");
            var foreignRestore = PluginIpc.ParseResult(ReadResultFrame(server, session, 30).Payload);
            Check("拒绝非本插件的恢复标识",
                foreignRestore.Status == "failed" && foreignRestore.Code == "RESTORE_FAILED");

            var dpcServiceExists = DpcServiceExists();
            WriteServerFrame(server, "restore", session,
                "{\"backupIds\":[\"" + DpcRules.NewBackupId() + "\"]}");
            var emptyRestore = PluginIpc.ParseResult(ReadResultFrame(server, session, 30).Payload);
            if (!dpcServiceExists)
            {
                Check("无记录且无服务时恢复按已释放处理",
                    emptyRestore.Status == "success" && emptyRestore.Items.All(i => i.Status == "success"));
            }
            else
            {
                Skip("无记录恢复", "本机已存在 dpclat_driver 服务，恢复应拒绝而不是清理他人服务");
                Check("已有服务时无记录恢复被拒绝", emptyRestore.Status == "failed");
            }

            WriteServerFrame(server, "status", session, "{}");
            var status = PluginIpc.ParseResult(ReadResultFrame(server, session, 30).Payload);
            Check("status 返回记录/服务/读数",
                status.Items.Any(i => i.Id == "journal") && status.Items.Any(i => i.Id == "service") &&
                status.Items.Any(i => i.Id == "timer"));

            WriteServerFrame(server, "ping", session, "{}");
            var pong = ReadServerFrame(server, 15);
            Check("pong 心跳关联", pong.Type == PluginIpcType.Pong && pong.RequestId == _lastRequestId &&
                pong.SessionId == session);

            WriteServerFrame(server, "offline.prepare", session,
                "{\"runId\":\"" + Guid.NewGuid().ToString("D") + "\",\"target\":null}");
            var offline = PluginIpc.ParseResult(ReadResultFrame(server, session, 15).Payload);
            Check("未声明脱机能力时拒绝 offline.prepare",
                offline.Status == "failed" && offline.Code == "OFFLINE_UNSUPPORTED");

            WriteServerFrame(server, "stop", session, "{}");
            Check("stop 确认成功",
                PluginIpc.ParseResult(ReadResultFrame(server, session, 15).Payload).Status == "success");
            process.WaitForExit(10_000);
            Check("后端正常退出", process.HasExited && process.ExitCode == 0,
                process.HasExited ? process.ExitCode.ToString() : "未退出");
            Check("端到端未写入常驻记录",
                !File.Exists(HoldJournalStore.JournalPath(dataDirectory)));

            Console.WriteLine("  只读端到端检查完成（未启动原版 DPC、未加载驱动、未改动服务）。");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); }
            server.Dispose();
            try { Directory.Delete(dataDirectory, recursive: true); } catch (IOException) { }
        }
    }

    private static bool DpcServiceExists()
    {
        var manager = OpenSCManager(null, null, 0x0001);
        if (manager == IntPtr.Zero) return false;
        var service = IntPtr.Zero;
        try
        {
            service = OpenService(manager, DpcRules.ServiceName, 0x0004);
            return service != IntPtr.Zero;
        }
        finally
        {
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            CloseServiceHandle(manager);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint access);

    [DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(IntPtr handle);

    private static string? FindPluginBackend(string root)
    {
        var projectDir = Path.Combine(root, "src", "Plugin.DpcTimerHold");
        // 优先使用打包脚本产出的自包含发布目录：端到端验证的才是实际随包分发的后端。
        var published = Path.Combine(projectDir, "artifacts", "publish-Release", "Plugin.DpcTimerHold.exe");
        if (File.Exists(published)) return published;
        foreach (var configuration in new[] { "Release", "Debug" })
            foreach (var platformSegment in new[] { "x64", "" })
            {
                var candidate = Path.Combine(projectDir, "bin", platformSegment, configuration, "net8.0-windows",
                    "Plugin.DpcTimerHold.exe");
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    // ---------------------------------------------------------------- 管道辅助

    private static void WriteServerFrame(NamedPipeServerStream pipe, string type, Guid session, string payloadJson)
    {
        _lastRequestId = Guid.NewGuid();
        var envelope = "{\"protocol\":\"1.0\",\"type\":\"" + type + "\",\"sessionId\":\"" + session.ToString("D") +
            "\",\"requestId\":\"" + _lastRequestId.ToString("D") + "\",\"payload\":" + payloadJson + "}";
        var frame = PluginIpc.EncodeFrame(envelope);
        pipe.Write(frame, 0, frame.Length);
        pipe.Flush();
    }

    private static PluginIpcFrame ReadResultFrame(NamedPipeServerStream pipe, Guid session, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("等待 result 帧超时。");
            var frame = ReadServerFrame(pipe, (int)Math.Ceiling(remaining.TotalSeconds));
            if (frame.SessionId != session) throw new IOException("帧会话不匹配。");
            if (frame.Type == PluginIpcType.Result) return frame;
            if (frame.Type is PluginIpcType.Progress or PluginIpcType.Pong) continue;
            throw new IOException("等待 result 期间收到非预期消息：" + frame.Type);
        }
    }

    private static PluginIpcFrame ReadServerFrame(NamedPipeServerStream pipe, int timeoutSeconds)
    {
        var header = new byte[4];
        ReadExactly(pipe, header, timeoutSeconds);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > PluginContract.MaxFrameBytes) throw new IOException("帧长度非法。");
        var payload = new byte[length];
        ReadExactly(pipe, payload, timeoutSeconds);
        var full = new byte[4 + length];
        header.CopyTo(full, 0);
        payload.CopyTo(full, 4);
        try
        {
            if (!PluginIpc.ParseFrame(full, out var frame)) throw new IOException("帧解析失败。");
            return frame;
        }
        catch (Exception ex) when (ex is PluginContractException or IOException)
        {
            var preview = new UTF8Encoding(false, false).GetString(payload);
            throw new IOException($"后端帧不符合 IPC 协议：{ex.Message}｜原始内容：{preview[..Math.Min(preview.Length, 400)]}");
        }
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int timeoutSeconds)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        stream.ReadExactlyAsync(buffer, deadline.Token).AsTask().GetAwaiter().GetResult();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
}
