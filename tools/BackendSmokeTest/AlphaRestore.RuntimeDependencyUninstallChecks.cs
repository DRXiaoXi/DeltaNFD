using System.Text.Json;
using DeltaNFD.Services;

using DeltaNFD.Services.AlphaRestore;
using RuntimeGuardService = DeltaNFD.Services.AlphaRestore.RuntimeGuardService;

namespace DeltaNFD.Tests.AlphaRestore;

internal static class RuntimeDependencyUninstallChecks
{
    // Replay the identities and dependency shape from the reported failure, never the installed products.
    private const string Bundle = "{0b5169e3-39da-4313-808e-1f9c0407f3bf}";
    private const string Additional = "{c18fb403-1e88-43c8-ad8a-ced50f23de8b}";
    private const string Minimum = "{922480b5-caeb-4b1b-aaa4-9716efdce26b}";
    private const string Other = "{aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa}";

    private static VcRedistEntry Entry(string key = Bundle) => new()
    {
        KeyName = key, DisplayName = "Microsoft Visual C++ 2015-2022 Redistributable (x86) - 14.44.35211",
        DisplayVersion = "14.44.35211.0", Architecture = "x86", UninstallKind = "Bundle",
        UninstallString = "\"C:\\ProgramData\\Package Cache\\" + Bundle + "\\VC_redist.x86.exe\" /uninstall",
        QuietUninstallString = "\"C:\\ProgramData\\Package Cache\\" + Bundle + "\\VC_redist.x86.exe\" /uninstall /quiet",
    };

    internal static async Task<int> RunAsync(string root)
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; Console.WriteLine("PASS: " + name); }
        bool Refused(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
        var plan = new RuntimeUnifiedUninstallPlan(Entry(), [Additional, Minimum], []);
        var command = RuntimeUnifiedUninstallBackend.BuildUninstallCommand(plan, Additional, Path.Combine(root, "with spaces.log"));
        Check(command.Arguments.Single(a => a.StartsWith("IGNOREDEPENDENCIES=")) == "IGNOREDEPENDENCIES=" + Bundle,
            "生产命令仅排除已确认的所属 Bundle 引用，解决日志中的自锁");
        Check(!command.Arguments.Any(a => a.Contains("=ALL", StringComparison.OrdinalIgnoreCase))
            && command.CreateStartInfo().ArgumentList.Contains("IGNOREDEPENDENCIES=" + Bundle), "传给进程的参数不扩大依赖范围");
        Check(command.Arguments.Contains(Additional) && command.Arguments.Contains("/qn") && command.Arguments.Contains("/norestart"),
            "继续使用已确认产品码、静默和不自动重启参数");
        Check(command.Arguments.Contains(Path.Combine(root, "with spaces.log")), "带空格诊断路径作为单独参数传递");
        var direct = new VcRedistEntry { KeyName = Additional, DisplayName = "Microsoft Visual C++ 2022 X86 Additional Runtime",
            Architecture = "x86", UninstallKind = "Msi", UninstallString = "msiexec.exe /I" + Additional };
        var inf = new VcRedistEntry { KeyName = "vcred14_x86", DisplayName = "Microsoft Visual C++ 2026 Redistributable",
            Architecture = "x86", UninstallKind = "Inf", UninstallString = "rundll32.exe advpack.dll,LaunchINFSection vcred14_x86.inf,Uninstall" };
        foreach (var entry in new[] { direct, inf })
            Check(!RuntimeUnifiedUninstallBackend.BuildUninstallCommand(plan with { Entry = entry }, Additional, null).Arguments
                .Any(a => a.StartsWith("IGNOREDEPENDENCIES=")), "直接 MSI / INF 不猜测 Bundle 引用：" + entry.KeyName);
        foreach (var invalid in new[] { "ALL", Bundle + ";" + Other, "", Guid.Empty.ToString("B") })
            Check(Refused(() => RuntimeUnifiedUninstallBackend.BuildUninstallCommand(plan with { Entry = Entry(invalid) }, Additional, null)),
                "拒绝无效或扩大范围的 Bundle 身份：" + invalid);
        Check(Refused(() => RuntimeUnifiedUninstallBackend.BuildUninstallCommand(plan, Other, null)), "计划外 MSI 不允许卸载");
        Check(Refused(() => RuntimeUnifiedUninstallBackend.BuildUninstallCommand(plan with { RetainedProducts = [Additional] }, Additional, null)),
            "需要保留的新版本 MSI 不能进入卸载命令");
        RuntimeUnifiedUninstallBackend.ValidateBundleDependents(Bundle, [Bundle.ToUpperInvariant()]);
        Check(true, "相同 Bundle 引用允许大小写差异");
        foreach (var dependents in new[] { new[] { Bundle, Other }, new[] { Other }, Array.Empty<string>(), new[] { "unknown" } })
            Check(Refused(() => RuntimeUnifiedUninstallBackend.ValidateBundleDependents(Bundle, dependents)),
                "预检拒绝外部、缺失或无法确认的引用");

        Fake New() => new(Path.Combine(root, "dependency-" + Guid.NewGuid().ToString("N")));
        async Task<(RuntimeStepResult Result, RuntimeRepairDiagnostics Diagnostics)> Run(Fake fake, CancellationToken token = default)
        {
            var diagnostics = new RuntimeRepairDiagnostics(root);
            var result = await new RuntimeUnifiedUninstaller(fake, diagnostics, fake.Pending)
                .RunAsync(Entry(), "reported-case", TimeSpan.FromSeconds(5), null, token);
            return (result, diagnostics);
        }
        var old = New(); old.OmitScope = true;
        var (oldResult, oldDiagnostics) = await Run(old);
        Check(!oldResult.Success && oldResult.ExitCode == 0 && old.Calls == 1 && old.Cleanups == 0
            && old.Products[Additional].State == 5, "复现原故障：退出码零但 WiX 引用阻止实际卸载");
        using (var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(oldDiagnostics.DirectoryPath, "reported-case-msi-0-result.json"))))
            Check(!record.RootElement.GetProperty("Result").GetProperty("PostcheckPassed").GetBoolean()
                && record.RootElement.GetProperty("After").GetProperty("State").GetInt32() == 5, "单步诊断记录最终复检失败，避免与总结果矛盾");
        var fixedCase = New();
        var fixedResult = (await Run(fixedCase)).Result;
        Check(fixedResult.Success && fixedCase.Calls == 2 && fixedCase.Cleanups == 1
            && fixedCase.Products.Values.All(p => p.State == -1), "修复后两个组件真实移除再清理包装登记");
        Check(fixedCase.Scopes.All(s => s == "IGNOREDEPENDENCIES=" + Bundle), "整个卸载闭环只排除本 Bundle 引用");
        var foreign = New(); foreign.Dependents[Additional].Add(Other);
        Check(!(await Run(foreign)).Result.Success && foreign.Calls == 0 && foreign.Cleanups == 0
            && foreign.Dependents[Additional].Contains(Other), "其他安装包已引用时启动前拒绝，保留其登记");
        var raced = New(); raced.AddForeignAtStart = true;
        Check(!(await Run(raced)).Result.Success && raced.Calls == 1 && raced.Cleanups == 0
            && raced.Products[Additional].State == 5 && raced.Dependents[Additional].Contains(Other),
            "预检后的并发新增依赖仍由 WiX 拦截，不能被全局忽略");
        foreach (var failure in new[] { new RuntimeStepResult(1603, "fixture failure"), new RuntimeStepResult(1612, "missing cache"),
            new RuntimeStepResult(null, "timeout", OutcomeUnknown: true) })
        {
            var failed = New(); failed.Results.Add(failure);
            var result = (await Run(failed)).Result;
            Check(!result.Success && failed.Calls == 1 && failed.Cleanups == 0
                && failed.Dependents.Values.All(c => c.SequenceEqual(new[] { Bundle })) && Directory.GetFiles(failed.Pending, "*.json").Length == 1,
                "失败或未知结果保留原引用与计划，无需冒险回写恢复：" + failure.Details);
        }
        var partial = New(); partial.Results.Add(new(0)); partial.Results.Add(new(1603));
        Check(!(await Run(partial)).Result.Success && partial.Products[Additional].State == -1
            && partial.Products[Minimum].State == 5 && partial.Cleanups == 0, "部分卸载失败保留剩余组件引用及包装证据");
        Check((await Run(partial)).Result.Success && partial.Calls == 3 && partial.Resolves == 1 && partial.Cleanups == 1,
            "旧计划重试仍带精确依赖范围，跳过已卸载产品，继续闭环");
        var cancel = New(); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Check(!(await Run(cancel, canceled.Token)).Result.Success && cancel.Calls == 0 && cancel.Cleanups == 0,
            "启动前取消不会移除引用或运行 MSI");
        Console.WriteLine($"Runtime dependency uninstall checks: {count} passed. MSI and registry operations entirely simulated.");
        return count;
    }

    private sealed class Fake(string pending) : IRuntimeUnifiedUninstallBackend
    {
        internal string Pending => pending;
        internal Dictionary<string, RuntimeMsiProduct> Products = new(StringComparer.OrdinalIgnoreCase)
        {
            [Additional] = new(Additional, 5, "Microsoft Visual C++ 2022 X86 Additional Runtime - 14.44.35211", "Microsoft Corporation", "14.44.35211", "x86"),
            [Minimum] = new(Minimum, 5, "Microsoft Visual C++ 2022 X86 Minimum Runtime - 14.44.35211", "Microsoft Corporation", "14.44.35211", "x86"),
        };
        internal Dictionary<string, List<string>> Dependents = new(StringComparer.OrdinalIgnoreCase)
        { [Additional] = [Bundle], [Minimum] = [Bundle] };
        internal int Calls, Resolves, Cleanups;
        internal bool OmitScope, AddForeignAtStart;
        internal List<string> Scopes = [];
        internal List<RuntimeStepResult> Results = [];
        public RuntimeUnifiedUninstallPlan Resolve(VcRedistEntry entry)
        { Resolves++; return new(entry, [Additional, Minimum], [new("simulated-original-registration", 512, "preserved")]); }
        public RuntimeMsiProduct ReadProduct(string product) => Products[product];
        public void ValidateRegistration(RuntimeUnifiedUninstallPlan plan)
        {
            foreach (var code in plan.Products.Where(p => Products[p].State == 5))
                RuntimeUnifiedUninstallBackend.ValidateBundleDependents(plan.Entry.KeyName, Dependents[code]);
        }
        public Task<RuntimeStepResult> UninstallAsync(RuntimeUnifiedUninstallPlan plan, string product, string log,
            TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var command = RuntimeUnifiedUninstallBackend.BuildUninstallCommand(plan, product, log);
            var scope = OmitScope ? "" : command.Arguments.Single(a => a.StartsWith("IGNOREDEPENDENCIES="));
            Calls++; Scopes.Add(scope);
            if (AddForeignAtStart) Dependents[product].Add(Other);
            // WiX returns zero without uninstalling if any unhandled dependent remains.
            if (Dependents[product].Any(c => "IGNOREDEPENDENCIES=" + c != scope)) return Task.FromResult(new RuntimeStepResult(0, "WixDependencyCheck 259"));
            var result = Results.Count == 0 ? new(0) : Results[0];
            if (Results.Count > 0) Results.RemoveAt(0);
            if (result.Success || result.ExitCode == 1605) Products[product] = Products[product] with { State = -1 };
            return Task.FromResult(result);
        }
        public void CleanupRegistration(RuntimeUnifiedUninstallPlan plan)
        {
            if (Products.Values.Any(p => p.State != -1)) throw new IOException("product remains");
            Cleanups++; foreach (var clients in Dependents.Values) clients.Remove(Bundle);
        }
    }
}
