using DeltaNFD.Services;

using DeltaNFD.Services.AlphaRestore;
using RuntimeGuardService = DeltaNFD.Services.AlphaRestore.RuntimeGuardService;

namespace DeltaNFD.Tests.AlphaRestore;

internal static class RuntimeUninstallBatchChecks
{
    private const string Bundle86 = "{0b5169e3-39da-4313-808e-1f9c0407f3bf}";
    private const string Bundle64 = "{d8bbe9f9-7c5b-42c6-b715-9ee898a2e515}";
    private const string Min86 = "{922480b5-caeb-4b1b-aaa4-9716efdce26b}";
    private const string Add86 = "{c18fb403-1e88-43c8-ad8a-ced50f23de8b}";
    private const string Min64 = "{43b0d101-a022-48f4-9d04-ba404ceb1d53}";
    private const string Add64 = "{86ab2cc9-08bd-4643-b0f9-f82d006d72ff}";

    internal static async Task<int> RunAsync(string root)
    {
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; Console.WriteLine("PASS: " + message); }
        Fake New() => new(Path.Combine(root, "batch-" + Guid.NewGuid().ToString("N")));
        async Task<RuntimeRemovalResult> Run(Fake fake)
        {
            var diagnostics = new RuntimeRepairDiagnostics(root);
            var uninstaller = new RuntimeUnifiedUninstaller(fake, diagnostics, fake.Pending);
            return await RuntimePreUninstall.RunAsync(fake.Scan(),
                (entries, token) => Task.FromResult(uninstaller.PrepareBatch(entries, token)),
                (entry, budget, progress, token) => uninstaller.RunAsync(entry, entry.KeyName, budget, progress, token),
                token => Task.FromResult(fake.Scan()), budget => budget, diagnostics, null, default);
        }
        var normal = New();
        var preview = RuntimeBulkUninstall.Preview(normal.Entries).Entries;
        Check(preview.Take(2).All(e => e.UninstallKind == "Bundle"), "真实六条混排清单优先展示两个包装计划");
        var result = await Run(normal);
        Check(result.Stopped is null && normal.Calls.Count == 4 && normal.WrapperCleanups == 2 && normal.Scan().All.Count == 0,
            "按用户日志六条 x86/x64 登记完成四个 MSI 卸载和两包装清理");
        Check(normal.Calls.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 4
            && normal.Scopes.All(s => s is Bundle86 or Bundle64), "父子去重后四个产品各执行一次，全部携带所属包装范围");
        Check(normal.PreparedAtFirstCall == 6 && normal.Resolved.Count == 6, "包括覆盖子项在内的整批身份核验先于第一项卸载");
        Check(Directory.GetFiles(Path.Combine(normal.Pending, "batches"), "*.json").Length == 1, "完整批次关联与选择结果在运行前持久化");
        foreach (var badKey in new[] { Bundle64, Min64, Add86 })
        {
            var invalid = New(); invalid.FailPrepare = badKey;
            var blocked = await Run(invalid);
            Check(blocked.Stopped is not null && invalid.Calls.Count == 0 && invalid.WrapperCleanups == 0,
                "后续包装或已覆盖子项预检失败时整批零卸载：" + badKey);
        }
        var pending = New(); pending.FirstResult = new(3010);
        var requiresRestart = await Run(pending);
        Check(requiresRestart.Reboot && requiresRestart.Stopped is not null && pending.Calls.Count == 1
            && pending.WrapperCleanups == 0, "第一个子 MSI 要求重启立即停止，不运行同包第二组件");
        pending.FirstResult = null;
        Check((await Run(pending)).Stopped is null && pending.Calls.Count == 4 && pending.WrapperCleanups == 2,
            "重启后按原计划跳过已移除组件，继续剩余产品和包装清理");
        var failure = New(); failure.FailProduct = Min64;
        Check((await Run(failure)).Stopped is not null && failure.Calls.Count == 3 && failure.WrapperCleanups == 1,
            "第二包装组件失败保留其计划，第一包装不重复处理");
        failure.FailProduct = null;
        Check((await Run(failure)).Stopped is null && failure.Calls.Count == 5 && failure.Scan().All.Count == 0,
            "部分批次失败重试只执行余下产品");
        var external = New(); external.LeaveExternalRegistration = true;
        Check((await Run(external)).Stopped is { PostcheckPassed: false } && external.Calls.Count == 4,
            "卸载完成后发现新增 C++ 登记，复检门禁禁止进入安装");

        var marker = new RuntimeUnifiedUninstallPlan(normal.Entries.Single(e => e.KeyName == Bundle86), [], []) { RetainedProducts = [Min86] };
        var child = new RuntimeUnifiedUninstallPlan(normal.Entries.Single(e => e.KeyName == Min86), [Min86], []);
        Check(RuntimeUnifiedUninstaller.SelectBatchPlans([child, marker]).Count == 2, "旧包装保留的新版本产品不能被去重漏卸载");
        Console.WriteLine($"Runtime uninstall batch checks: {count} passed. Full mixed registry and MSI lifecycle simulated.");
        return count;
    }

    private sealed class Fake
        : IRuntimeUnifiedUninstallBackend
    {
        internal string Pending { get; }
        internal List<VcRedistEntry> Entries = [];
        internal Dictionary<string, RuntimeMsiProduct> Products = new(StringComparer.OrdinalIgnoreCase);
        internal List<string> Calls = [], Scopes = [];
        internal HashSet<string> Resolved = new(StringComparer.OrdinalIgnoreCase), RemovedWrappers = new(StringComparer.OrdinalIgnoreCase);
        internal string? FailPrepare, FailProduct;
        internal RuntimeStepResult? FirstResult;
        internal bool LeaveExternalRegistration;
        internal int WrapperCleanups, PreparedAtFirstCall;
        internal Fake(string pending)
        {
            Pending = pending;
            foreach (var (key, architecture) in new[] { (Bundle86, "x86"), (Bundle64, "x64") })
                Entries.Add(new() { KeyName = key, DisplayName = "Microsoft Visual C++ 2015-2022 Redistributable (" + architecture + ") - 14.44.35211",
                    DisplayVersion = "14.44.35211.0", Architecture = architecture, UninstallKind = "Bundle",
                    UninstallString = "\"C:\\Package Cache\\" + key + "\\vc_redist." + architecture + ".exe\" /uninstall",
                    QuietUninstallString = "\"C:\\Package Cache\\" + key + "\\vc_redist." + architecture + ".exe\" /uninstall /quiet" });
            foreach (var (key, architecture, type) in new[] { (Min86, "x86", "Minimum"), (Add86, "x86", "Additional"), (Min64, "x64", "Minimum"), (Add64, "x64", "Additional") })
            {
                var name = "Microsoft Visual C++ 2022 " + architecture.ToUpperInvariant() + " " + type + " Runtime - 14.44.35211";
                Entries.Add(new() { KeyName = key, DisplayName = name, DisplayVersion = "14.44.35211", Architecture = architecture,
                    UninstallKind = "Msi", UninstallString = "msiexec.exe /I" + key });
                Products[key] = new(key, 5, name, "Microsoft Corporation", "14.44.35211", architecture);
            }
        }
        internal VcRedistScanReport Scan()
        {
            var all = Entries.Where(e => e.UninstallKind == "Bundle" ? !RemovedWrappers.Contains(e.KeyName) : Products[e.KeyName].State == 5).ToList();
            if (LeaveExternalRegistration && Products.Values.All(p => p.State == -1))
                all.Add(new() { KeyName = "external", DisplayName = "Microsoft Visual C++ 2013 Redistributable", Architecture = "x86", UninstallKind = "Msi" });
            return new() { All = all, Abnormal = [], Suboptimal = [], Advice = "" };
        }
        public RuntimeUnifiedUninstallPlan Resolve(VcRedistEntry entry)
        {
            if (entry.KeyName == FailPrepare) throw new IOException("fixture missing cache or unconfirmed identity");
            Resolved.Add(entry.KeyName);
            return new(entry, entry.KeyName switch { Bundle86 => [Min86, Add86], Bundle64 => [Min64, Add64], _ => [entry.KeyName] },
                entry.UninstallKind == "Bundle" ? [new("fixture-original-wrapper", 512, "preserved")] : []);
        }
        public RuntimeMsiProduct ReadProduct(string product) => Products[product];
        public void ValidateRegistration(RuntimeUnifiedUninstallPlan plan)
        {
            foreach (var product in plan.Products.Where(p => Products[p].State == 5))
                if (plan.Entry.UninstallKind == "Bundle")
                    RuntimeUnifiedUninstallBackend.ValidateBundleDependents(plan.Entry.KeyName, [Products[product].Architecture == "x86" ? Bundle86 : Bundle64]);
        }
        public Task<RuntimeStepResult> UninstallAsync(RuntimeUnifiedUninstallPlan plan, string product, string log,
            TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
        {
            if (Calls.Count == 0) PreparedAtFirstCall = Resolved.Count;
            var command = RuntimeUnifiedUninstallBackend.BuildUninstallCommand(plan, product, log);
            var scope = command.Arguments.FirstOrDefault(a => a.StartsWith("IGNOREDEPENDENCIES="))?["IGNOREDEPENDENCIES=".Length..] ?? "";
            Scopes.Add(scope); Calls.Add(product);
            if (scope != (Products[product].Architecture == "x86" ? Bundle86 : Bundle64)) return Task.FromResult(new RuntimeStepResult(0));
            var result = FirstResult ?? (product == FailProduct ? new RuntimeStepResult(1603) : new(0));
            FirstResult = null;
            if (result.Success) Products[product] = Products[product] with { State = -1 };
            return Task.FromResult(result);
        }
        public void CleanupRegistration(RuntimeUnifiedUninstallPlan plan)
        {
            if (plan.Products.Any(p => Products[p].State != -1)) throw new IOException("MSI remains");
            if (plan.Entry.UninstallKind == "Bundle") { RemovedWrappers.Add(plan.Entry.KeyName); WrapperCleanups++; }
        }
    }
}
