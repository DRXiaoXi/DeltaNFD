using DeltaNFD.Backends;
using DeltaNFD.Services;

internal static class Vc14DetectionChecks
{
    internal static void Run()
    {
        int count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        foreach (var (name, version, severity) in new[] {
            ("Microsoft Visual C++ 2022", "14.51.36247.0", Vc14Severity.Abnormal),
            ("Microsoft Visual C++ v14", "14.42.34433.0", Vc14Severity.Abnormal),
            ("Microsoft Visual C++ v14", "unknown", Vc14Severity.Abnormal),
            ("Microsoft Visual C++ 2022", "14.44.0", Vc14Severity.Warning),
            ("Microsoft Visual C++ 2022", "14.42.34433.0", Vc14Severity.Normal) })
            Check(Vc14Catalog.Classify(name, version) == severity, "read-only compatibility classification changed");
        // 横幅放宽（2026-10-15）：红色只由真实 MSI 组件驱动；包装/残留登记最高 Warning；Appx 不进横幅。
        var noProducts = new List<Vc14Entry>();
        Check(Vc14Catalog.Classify("Microsoft Visual C++ v14 Redistributable (x64)", "14.51.36247.0", installedMsi: false, products: noProducts) == Vc14Severity.Warning,
            "wrapper registration must not drive the red banner");
        Check(Vc14Catalog.Classify("Microsoft Visual C++ 2026 Redistributable (x64)", "14.51.36247.0", installedMsi: false, products: noProducts) == Vc14Severity.Warning,
            "orphan registration must not drive the red banner");
        Check(Vc14Catalog.Classify("Visual C++ Library CRT Desktop Appx Package", "14.44.35208", installedMsi: false, products: noProducts) == Vc14Severity.Normal
            && Vc14Catalog.Classify("Visual C++ Library CRT ARM64 Appx Package", "14.51.36247", installedMsi: false, products: noProducts) == Vc14Severity.Normal,
            "Appx package must stay out of banners");
        Check(Vc14Catalog.IsAppxPackage("Visual C++ Library CRT Desktop Appx Package"), "Appx detection changed");
        foreach (var name in new[] { "Visual C++ Library CRT Appx Resource Package", "Visual C++ Library CRT Desktop Appx Resource Package",
            "Visual C++ Library CRT ARM64 Appx Resource Package" })
        {
            Check(Vc14Catalog.IsAppxPackage(name) && Vc14Catalog.Classify(name, "14.44.35208", false, noProducts) == Vc14Severity.Normal,
                "Appx Resource package remains outside Win32 detection warnings");
            Check(!Vc14BetaUninstall.InScope(name, "14.44.35208", "x86"), "Appx Resource package never becomes a Win32 uninstall target");
        }
        Check(!Vc14Catalog.IsAppxPackage("Microsoft Visual C++ 2022 X64 Minimum Runtime - 14.51.36247 Appx Resource Package"),
            "Appx text appended to real release CRT cannot bypass identity checks");
        var compatLog = "APPCOMPAT: (GetFileInstallState) Altered install mode for shared redist file targeting `C:\\Windows\\system32\\`\n"
            + "Disallowing installation of component: {F5E98D96-3C7F-37D7-821F-E4BB03D4384B} since the same component with higher versioned keyfile exists";
        var explanation = Vc14Diagnostics.ExplainDllMismatch(new[] { compatLog }, "actual 14.51 DLL");
        Check(explanation.Contains("APPCOMPAT") && explanation.Contains("actual 14.51 DLL") && explanation.Contains("不能报告修复成功"),
            "logs3 compatibility block is distinct from ordinary process/file use");
        Check(!Vc14Diagnostics.ExplainDllMismatch(new[] { "installer exit 0" }, "mismatch").Contains("APPCOMPAT"),
            "without actual compatibility markers do not guess an OS policy block");
        // Missing Win32 CRT is red only after a completed scan. Notices are not proof of presence.
        var baseline = new Vc14Entry("{A}", "Microsoft Visual C++ 2022 X64 Minimum Runtime - 14.42.34433", "14.42.34433", "x64", "Minimum", "{U}");
        var appxNotice = new Vc14Notice("Visual C++ Library CRT Desktop Appx Package", "14.44.35208", "x86", Vc14Severity.Normal, "系统组件包");
        var orphanNotice = new Vc14Notice("Microsoft Visual C++ 2026 Redistributable (x64)", "14.51.36247.0", "x64", Vc14Severity.Warning, "残留");
        var emptyScan = new Vc14Scan([], [], []);
        Check(emptyScan.IsMissing && emptyScan.HasAbnormal && !emptyScan.HasWarning, "complete empty scan is red missing");
        Check(emptyScan.AbnormalTitle.Contains("缺少") && emptyScan.GetAbnormalNotices().Single().Detail.Contains("无法启动"), "missing title/action describe launch risk");
        Check(emptyScan.GetAbnormalNotices().Single().Detail.Contains("14.42"), "missing guidance uses the pinned baseline");
        var appxOnly = new Vc14Scan([], [appxNotice], []);
        Check(appxOnly.IsMissing && appxOnly.HasAbnormal && !appxOnly.HasWarning, "Appx alone cannot hide missing Win32 runtime");
        var orphanOnly = new Vc14Scan([], [orphanNotice], []);
        Check(orphanOnly.IsMissing && orphanOnly.HasAbnormal && orphanOnly.HasWarning, "orphan only has missing red and separate residue yellow");
        var uncertain = new Vc14Scan([], [], ["读取失败"]);
        Check(!uncertain.IsMissing && !uncertain.HasAbnormal && uncertain.HasWarning && !uncertain.GetAbnormalNotices().Any(), "failed scan stays unknown not missing red");
        var uncertainOrphan = new Vc14Scan([], [orphanNotice], ["MSI 缓存身份未核对"]);
        Check(!uncertainOrphan.IsMissing && !uncertainOrphan.HasAbnormal && uncertainOrphan.HasWarning, "unverified identity cannot prove absence");
        var healthy = new Vc14Scan([baseline], [appxNotice], []);
        Check(!healthy.IsMissing && !healthy.HasAbnormal && !healthy.HasWarning, "Appx is harmless when verified CRT exists");
        var orphanWithBaseline = new Vc14Scan([baseline], [orphanNotice], []);
        Check(!orphanWithBaseline.IsMissing && !orphanWithBaseline.HasAbnormal && orphanWithBaseline.HasWarning, "orphan itself remains yellow only");
        foreach (var arch in new[] { "x64", "x86" })
        {
            var available = new Vc14Scan([baseline with { Architecture = arch }], [], []);
            Check(!available.IsMissing && !available.HasAbnormal, "one verified architecture is presence, no new partial-architecture rule: " + arch);
        }
        var incompatible = new Vc14Scan([baseline with { Version = "14.51.36247" }],
            [new("Microsoft Visual C++ v14", "14.51.36247", "x64", Vc14Severity.Abnormal, "兼容性提醒")], []);
        Check(!incompatible.IsMissing && incompatible.HasAbnormal && incompatible.AbnormalTitle.Contains("兼容性"), "existing incompatible version retains original red reason");
        var json = System.Text.Json.JsonSerializer.Serialize(emptyScan);
        var received = System.Text.Json.JsonSerializer.Deserialize<Vc14Scan>(json)!;
        Check(received.IsMissing && received.HasAbnormal && received.GetAbnormalNotices().Single().Severity == Vc14Severity.Abnormal, "backend JSON roundtrip preserves missing diagnosis");
        Check(System.Text.Json.JsonSerializer.Deserialize<Vc14Scan>(System.Text.Json.JsonSerializer.Serialize(uncertain)) is { IsMissing: false, HasAbnormal: false, HasWarning: true }, "backend JSON roundtrip preserves unknown diagnosis");
        // 残留清理门禁（纯函数部分）：名称形态与 CanClean 规则。
        Check(Vc14ResidueCleanup.OrphanNamePattern.IsMatch("Microsoft Visual C++ 2026 Redistributable (x64)")
            && Vc14ResidueCleanup.OrphanNamePattern.IsMatch("Microsoft Visual C++ 2015-2022 Redistributable (x86) - 14.44.35211")
            && Vc14ResidueCleanup.OrphanNamePattern.IsMatch("Microsoft Visual C++ v14 Redistributable (x64) - 14.51.36247")
            && !Vc14ResidueCleanup.OrphanNamePattern.IsMatch("Microsoft Visual C++ 2022 X64 Minimum Runtime - 14.51")
            && !Vc14ResidueCleanup.OrphanNamePattern.IsMatch("Some Other Product (x64)"), "orphan name pattern rules changed");
        Check(new Vc14ResidueItem("CurrentUser", "Registry64", "k", "n", "v", "inf-legacy", "d").CanClean
            && new Vc14ResidueItem("CurrentUser", "Registry64", "k", "n", "v", "orphan", "d").CanClean
            && !new Vc14ResidueItem("CurrentUser", "Registry64", "k", "n", "v", "keep", "d").CanClean, "residue kind rules changed");
        // 2026/v14 包装是旧版工具安装足迹：登记缺卸载命令仍可经清理阶段处理（不再被"有值才核验"的旧规则卡住）。
        Check(Vc14ResidueCleanup.OrphanNamePattern.IsMatch("Microsoft Visual C++ 2026 Redistributable (x64)")
            && Vc14ResidueCleanup.OrphanNamePattern.IsMatch("Microsoft Visual C++ 2015-2022 Redistributable (x86) - 14.44.35211")
            && !Vc14ResidueCleanup.OrphanNamePattern.IsMatch("Some Other Product (x64)"), "2026 wrapper belongs to cleanup scope");
        // 无路径的有效命令（如 MsiExec.exe /X{...}）不得被误判为失效（按系统目录/PATH 解析）。
        Check(Vc14Catalog.UninstallExecutableAvailable("MsiExec.exe /X{12345678-1234-1234-1234-123456789ABC}"),
            "pathless MsiExec command must resolve via system directories");
        Check(Vc14Catalog.UninstallExecutableAvailable("DefinitelyNotARealTool12345.exe /quiet") is false,
            "nonexistent executable must be treated as unavailable");
        Check(Vc14Catalog.UninstallExecutableAvailable("\"C:\\Definitely\\Not\\Here\\uninstall.exe\" /quiet") is false,
            "quoted missing absolute path must be unavailable");
        // 执行用解析结果必须是完整路径：活动记录存相对名会被核对入口拒绝（GPT 第三轮问题 1）。
        var resolved = Vc14Catalog.ResolveUninstallExecutable("MsiExec.exe /X{12345678-1234-1234-1234-123456789ABC}");
        Check(resolved is not null && Path.IsPathRooted(resolved) && Path.GetFileName(resolved).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase),
            "resolved executable must be a fully-qualified path");
        // 卸载命令指向的程序不存在时不得进入 registry 执行路径（由 BuildRemovalPlan 的存在性检查保证，此处锁定计划类型规则）。
        Check(new Vc14RemovalPlanItem("k", "x64", "n", "v", "registry", "cmd-missing.exe", "m", "d").CanRemove, "registry plan kind must be removable");
        Check(new Vc14RemovalPlanItem("k", "x64", "Microsoft Visual C++ 2026 Redistributable (x86)", "未知", "unknown", "k", "m", "d").CanRemove is false, "unknown plan kind must stay non-removable");
        // 卸载命令解析：带引号路径不得把引号残留在参数中（GPT 审查问题 2）。
        var (exe, args) = Vc14Catalog.SplitUninstallCommand("\"C:\\目录\\uninstall.exe\" /quiet /norestart");
        Check(exe == "C:\\目录\\uninstall.exe" && args == "/quiet /norestart", "quoted uninstall path parsing must strip quotes");
        var (exe2, args2) = Vc14Catalog.SplitUninstallCommand("C:\\path\\uninst.exe /x {CODE}");
        Check(exe2 == "C:\\path\\uninst.exe" && args2 == "/x {CODE}", "plain uninstall path parsing must split at .exe");
        var (exe3, args3) = Vc14Catalog.SplitUninstallCommand("\"C:\\Program Files\\Vendor\\Uninstall.exe\"");
        Check(exe3 == "C:\\Program Files\\Vendor\\Uninstall.exe" && args3.Length == 0, "quoted path without args");
        // 与基线同版本的正常包装登记不得报黄（GPT 审查问题 5）。
        var baselineProducts = new List<Vc14Entry> { new("{A}", "Microsoft Visual C++ 2022 X64 Minimum Runtime - 14.42.34433", "14.42.34433", "x64", "Minimum", "{U}") };
        Check(Vc14Catalog.Classify("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.42.34433", "14.42.34433.0", installedMsi: false, products: baselineProducts) == Vc14Severity.Normal,
            "baseline-matching wrapper must not warn");
        Check(Vc14Catalog.Classify("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.44.35211", "14.44.35211.0", installedMsi: false, products: baselineProducts) == Vc14Severity.Warning,
            "non-baseline wrapper must still warn");
        Check(Vc14Catalog.Classify("Microsoft Visual C++ 2026 Redistributable (x64)", "14.51.36247.0", installedMsi: false, products: baselineProducts) == Vc14Severity.Warning,
            "wrapper without matching installed MSI must warn");
        // 卸载动态家族 MSI 不再要求固定升级码（GPT 审查问题 1）：ReadAny 必须存在且不依赖 Families。
        Check(typeof(Vc14Catalog).GetMethod("ReadAny", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static) is not null
            || typeof(Vc14Catalog).GetMethod("ReadAny") is not null, "dynamic-family product read missing");
        // 扫描视图：必须覆盖 HKLM+HKCU 的 64/32 位视图（每用户包装登记可见）。
        var views = Vc14Catalog.RegistryViews().Select(v => v.Hive + ":" + v.View).ToArray();
        Check(views.Length == 4
            && views.Contains("LocalMachine:Registry64") && views.Contains("LocalMachine:Registry32")
            && views.Contains("CurrentUser:Registry64") && views.Contains("CurrentUser:Registry32"), "registry views must cover HKLM+HKCU x64+x86");
        // 动态 MSI 家族发现入口必须存在（修 1638：2026 产品线新升级码可被枚举卸载）。
        Check(typeof(Vc14Catalog).GetMethod("DiscoverMsiProducts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public) is not null
            || typeof(Vc14Catalog).GetMethod("DiscoverMsiProducts") is not null, "dynamic MSI family discovery missing");
        // 残留清理实现必须存在且为全新类型（不复用退休实现）。
        Check(typeof(Vc14ResidueCleanup) is not null, "residue cleanup implementation missing");
        var execution = typeof(Vc14Maintenance).Assembly;
        foreach (var type in new[] { "Vc14LegacyEnvironment", "Vc14LegacyCleanup", "Vc14LegacyPolicy" })
            Check(execution.GetType("DeltaNFD.Services." + type) is null, "retired legacy cleanup type still compiled: " + type);
        Check(BackendModules.IsAllowed(BackendModules.Runtime, BackendCommands.Vc14Scan)
            && !BackendModules.IsMutating(BackendModules.Runtime, BackendCommands.Vc14Scan), "scan not read-only");
        Check(BackendModules.IsAllowed(BackendModules.Runtime, BackendCommands.Vc14Preview)
            && !BackendModules.IsMutating(BackendModules.Runtime, BackendCommands.Vc14Preview), "preview not read-only");
        Check(BackendModules.IsAllowed(BackendModules.Runtime, BackendCommands.Vc14Repair)
            && BackendModules.IsMutating(BackendModules.Runtime, BackendCommands.Vc14Repair), "repair command must be registered as mutating");
        Check(BackendModules.IsAllowed(BackendModules.Runtime, BackendCommands.Vc14LegacyClean)
            && BackendModules.IsMutating(BackendModules.Runtime, BackendCommands.Vc14LegacyClean), "legacy-clean stub must stay registered for old clients");
        foreach (var method in new[] { "ScanAsync", "PreviewAsync", "RepairAsync" })
            Check(typeof(Vc14Maintenance).GetMethod(method) is not null, "reinstall facade missing: " + method);
        Check(typeof(Vc14Maintenance).GetMethod("CleanLegacyAsync") is null, "legacy cleanup facade must not exist");
        // 全新实现：旧定向修复类型不允许回归，新重装类型必须存在。
        var assembly = typeof(Vc14Maintenance).Assembly;
        foreach (var type in new[] { "Vc14Repair", "Vc14Environment", "Vc14LegacyEnvironment", "Vc14LegacyCleanup", "Vc14InstallPolicy" })
            Check(assembly.GetType("DeltaNFD.Services." + type) is null, "old scoped repair implementation still compiled: " + type);
        foreach (var type in new[] { "Vc14Reinstall", "Vc14ReinstallEnvironment", "IVc14ReinstallEnvironment" })
            Check(assembly.GetType("DeltaNFD.Services." + type) is not null, "reinstall implementation missing: " + type);
        // 基线固定：用户 2026-10-15 提供的 14.42.34433 双架构原包哈希。
        Check(Vc14Reinstall.BaselineVersion == "14.42.34433.0", "baseline version changed");
        Check(Vc14Reinstall.BaselineHashes["x64"] == "1821577409C35B2B9505AC833E246376CC68A8262972100444010B57226F0940"
            && Vc14Reinstall.BaselineHashes["x86"] == "DD1A8BE03398367745A87A5E35BEBDAB00FDAD080CF42AF0C3F20802D08C25D4", "baseline hashes changed");
        // 步骤结果规则：0/3010 成功，3010/1641 需要重启，未知不视为成功。
        var ok = new Vc14StepResult(0); var reboot = new Vc14StepResult(3010); var fail = new Vc14StepResult(1603); var unknown = new Vc14StepResult(0, true);
        Check(ok.Success && !ok.Reboot && reboot.Success && reboot.Reboot && !fail.Success && !unknown.Success, "step result rules changed");
        // 计划项可移除规则：仅 msi/bundle/registry 可卸载，unknown/baseline-bundle 保留。
        Check(new Vc14RemovalPlanItem("k", "x64", "n", "v", "msi", "i", "m", "d").CanRemove
            && new Vc14RemovalPlanItem("k", "x64", "n", "v", "bundle", "i", "m", "d").CanRemove
            && new Vc14RemovalPlanItem("k", "x64", "n", "v", "registry", "i", "m", "d").CanRemove
            && !new Vc14RemovalPlanItem("k", "x64", "n", "v", "unknown", "i", "m", "d").CanRemove
            && !new Vc14RemovalPlanItem("k", "x64", "n", "v", "baseline-bundle", "i", "m", "d").CanRemove, "removal plan kind rules changed");
        // 指纹稳定性：同计划同指纹，改动一项即变化。
        var plan = new List<Vc14RemovalPlanItem> { new("{A}", "x64", "N", "14.44.0", "msi", "{A}", "m", "d", "Minimum", "{U}") };
        var fingerprint = Vc14Reinstall.Fingerprint(plan);
        Check(fingerprint == Vc14Reinstall.Fingerprint(plan.ToList()), "fingerprint not stable");
        plan.Add(new("{B}", "x86", "N2", "unknown", "registry", "cmd", "m", "d"));
        Check(fingerprint != Vc14Reinstall.Fingerprint(plan), "fingerprint ignores plan items");
        Console.WriteLine($"VC14 reinstall checks: {count} checks passed.");
        Vc14BetaUninstallChecks.Run();
        Vc14DependencyChecks.Run();
    }
    internal static void ReadOnly()
    {
        var id = Guid.NewGuid(); var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-vc14-detect-" + id.ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        var scan = Vc14Catalog.Scan();
        var path = Vc14Diagnostics.Save(scan) ?? throw new Exception("Detection report missing");
        Console.WriteLine("Detection report: " + path);
        Console.WriteLine($"Verified MSI records: {scan.Products.Count}; issues: {scan.Issues.Count}. Read-only; no install/uninstall executed.");
    }
}
