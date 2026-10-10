using DeltaNFD.Services;

internal static class RuntimeSilentUninstallChecks
{
    public static void Run()
    {
        var count = 0;
        void Check(bool condition, string description) { if (!condition) throw new Exception(description); count++; }
        VcRedistEntry Entry(string command, string quiet = "", string key = "vcredist09_x64") => new()
        { DisplayName = "Microsoft Visual C++ 2008 Redistributable (x64)", Architecture = "x64", KeyName = key,
            UninstallKind = "Unknown", UninstallString = command, QuietUninstallString = quiet };
        var inf = Entry("rundll32.exe advpack.dll,LaunchINFSection vcred09_x64.inf,Uninstall");
        Check(RuntimeUninstallCommand.Classify(inf.KeyName, inf.UninstallString, "") == "Inf", "INF 正确分类");
        Check(!RuntimeUninstallCommand.TryCreate(inf, null, out _, out _), "不执行 INF 原卸载入口");
        Check(RuntimeUninstallCommand.GetInfPath(inf).EndsWith("vcred09_x64.inf"), "只解析受支持系统 INF 路径");
        foreach (var bad in new[] { "evil.inf,Uninstall", "vcred09_x64.inf,Install", "vcred09_x64.inf,Uninstall & calc.exe", "C:\\other\\vcred09_x64.inf,Uninstall" })
        {
            var refused = false;
            try { RuntimeUninstallCommand.GetInfPath(Entry("rundll32.exe advpack.dll,LaunchINFSection " + bad)); }
            catch (InvalidDataException) { refused = true; }
            Check(refused, "未知/交互/外部 INF 拒绝：" + bad);
        }

        var guid = "{5FCE6D76-F5DC-37AB-B2B8-22AB8CEDB1D4}";
        var msi = Entry("MsiExec.exe /I" + guid, key: guid);
        Check(RuntimeUninstallCommand.TryCreate(msi, @"C:\中文 日志\msi.log", out var plan, out _), "MSI 安装入口转换静默卸载");
        Check(plan!.Arguments.Contains("/x") && plan.Arguments.Contains("/qn") && plan.Arguments.Contains("/norestart"), "MSI 静默与不重启");
        Check(plan.Arguments.Contains(@"C:\中文 日志\msi.log") && plan.CreateStartInfo().ArgumentList.Contains(@"C:\中文 日志\msi.log"), "中文和空格日志不拆分");
        Check(!RuntimeUninstallCommand.TryCreate(Entry("msiexec.exe /x invalid", key: "bad"), null, out _, out _), "非法产品码拒绝");
        Check(!RuntimeUninstallCommand.TryCreate(Entry("msiexec.exe /x " + guid + " {00000000-0000-0000-0000-000000000001}"), null, out _, out _), "多个产品码拒绝");
        Check(!RuntimeUninstallCommand.TryCreate(Entry("msiexec.exe /x " + guid, key: "{00000000-0000-0000-0000-000000000001}"), null, out _, out _), "条目键名与产品码不一致拒绝");

        var exe = "\"C:\\ProgramData\\Package Cache\\中文 包\\vc_redist.x64.exe\"";
        var bundle = Entry(exe + " /uninstall", exe + " /uninstall /quiet /norestart", guid);
        Check(RuntimeUninstallCommand.Classify(bundle.KeyName, bundle.UninstallString, bundle.QuietUninstallString) == "Bundle", "GUID 键不是 MSI 证明");
        Check(!RuntimeUninstallCommand.TryCreate(bundle, @"C:\中文 日志\bundle.log", out _, out var bundleError)
            && bundleError.Contains("统一卸载器"), "Bundle 必须解析关联 MSI，不启动 EXE 卸载器");
        Check(!RuntimeUninstallCommand.TryCreate(Entry(exe + " /uninstall", exe + " /uninstall /passive", guid), null, out _, out _), "非静默命令拒绝");
        Check(!RuntimeUninstallCommand.TryCreate(Entry(exe + " /uninstall", key: guid), null, out _, out _), "无静默参数的 EXE 不猜成 MSI");
        Check(!RuntimeUninstallCommand.TryCreate(Entry(exe, "cmd.exe /c " + exe + " /uninstall /quiet", guid), null, out _, out _), "间接 Shell 命令拒绝");
        var root = Path.Combine(Directory.GetCurrentDirectory(), "src", "DeltaNFD", "Assets", "VCRedistRepair");
        foreach (var year in new[] { "2005", "2008", "2010", "2012", "2013", "2022", "2026" })
        foreach (var arch in new[] { "x86", "x64" })
        {
            var dir = Path.Combine(root, year, arch);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir, "*.inf"))
                Check(RuntimeUninstallCommand.HasQuietInfSection(file), "现有资源的静默节有效：" + file);
        }
        var fixture = Path.Combine(Path.GetTempPath(), "DeltaNFD_QuietInf_" + Guid.NewGuid().ToString("N") + ".inf");
        try
        {
            var infText = "[Unattend]\nRunPreSetupCommands=Msi.Unattend:1\nDelReg=Registry.Uninstall\n[Msi.Unattend]\nmsiexec.exe /X%GuidCode% /quiet /norestart\n[Strings]\nGuidCode=\"" + guid + "\"\n";
            File.WriteAllText(fixture, infText);
            Check(RuntimeUninstallCommand.HasQuietInfSection(fixture), "临时结构化 INF 验证通过");
            Check(RuntimeUninstallCommand.ReadQuietInfProducts(fixture).Single().Equals(guid, StringComparison.OrdinalIgnoreCase), "关联 MSI 产品码读取");
            File.WriteAllText(fixture, infText.Replace("DelReg=", "BeginPrompt=Confirm\nDelReg="));
            Check(!RuntimeUninstallCommand.HasQuietInfSection(fixture), "静默节内含确认提示拒绝");
            File.WriteAllText(fixture, infText.Replace("[Strings]", "msiexec.exe /X%GuidCode% /passive /norestart\n[Strings]"));
            Check(!RuntimeUninstallCommand.HasQuietInfSection(fixture), "任一个交互子命令均拒绝");
            File.WriteAllText(fixture, infText.Replace(guid, "not-a-guid"));
            Check(!RuntimeUninstallCommand.HasQuietInfSection(fixture), "非法 INF 产品码拒绝");
        }
        finally { File.Delete(fixture); }
        Console.WriteLine($"静默卸载计划检查通过：{count} 项；没有运行任何真实卸载器。");
    }
}
