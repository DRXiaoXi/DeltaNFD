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
