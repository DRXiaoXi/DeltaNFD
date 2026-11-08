using System.Text.Json;
using System.Text.Json.Nodes;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;
using DeltaNFD.Backends;

using DeltaNFD.Services.AlphaRestore;
using RuntimeGuardService = DeltaNFD.Services.AlphaRestore.RuntimeGuardService;

namespace DeltaNFD.Tests.AlphaRestore;

internal static class RuntimeInfUninstallChecks
{
    private const string Additional = "{6fa797cf-6b76-4b6d-87ee-768f92008720}";
    private const string Minimum = "{931a2cf0-2404-45ea-82f5-345735ae6a90}";
    private const string Bundle = "{aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa}";
    private static readonly VcRedistEntry Inf = new() { DisplayName = "Microsoft Visual C++ 2026 Redistributable (x64)",
        DisplayVersion = "14.51.36247.0", Architecture = "x64", KeyName = "vcredist14_x64", UninstallKind = "Inf",
        UninstallString = "rundll32.exe advpack.dll,LaunchINFSection vcred14_x64.inf,Uninstall" };
    private static readonly VcRedistEntry Owner = new() { DisplayName = "Microsoft Visual C++ 2026 Redistributable (x64) - 14.51.36247",
        DisplayVersion = "14.51.36247.0", Architecture = "x64", KeyName = Bundle, UninstallKind = "Bundle",
        UninstallString = "\"C:\\Package Cache\\" + Bundle + "\\vc_redist.x64.exe\" /uninstall",
        QuietUninstallString = "\"C:\\Package Cache\\" + Bundle + "\\vc_redist.x64.exe\" /uninstall /quiet" };

    internal static async Task<int> RunAsync(string root)
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; Console.WriteLine("PASS: " + name); }
        var owner = new RuntimeUnifiedUninstallPlan(Owner, [Additional, Minimum], [new("fixture-owner", 256, "original")]);
        var inf = new RuntimeUnifiedUninstallPlan(Inf, [Additional, Minimum], [new("fixture-inf", 256, "original")]) { RelatedBundles = [owner] };
        RuntimeUnifiedUninstallBackend.ValidateRelatedBundle(inf, owner);
        Check(true, "截图中 INF 两个产品可匹配同版本同架构的已验证包装");
        foreach (var invalid in new[] { owner with { Products = [Guid.NewGuid().ToString("B")] },
            owner with { RetainedProducts = [Minimum] }, owner with { RelatedBundles = [owner] },
            owner with { Entry = new VcRedistEntry { DisplayName = Owner.DisplayName, DisplayVersion = "14.44.35211", Architecture = "x64",
                KeyName = Bundle, UninstallKind = "Bundle", QuietUninstallString = Owner.QuietUninstallString, UninstallString = Owner.UninstallString } },
            owner with { Entry = new VcRedistEntry { DisplayName = Owner.DisplayName, DisplayVersion = Owner.DisplayVersion, Architecture = "x86",
                KeyName = Bundle, UninstallKind = "Bundle", QuietUninstallString = Owner.QuietUninstallString, UninstallString = Owner.UninstallString } } })
        {
            var rejected = false;
            try { RuntimeUnifiedUninstallBackend.ValidateRelatedBundle(inf, invalid); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "不同版本、架构、额外产品或递归包装不能扩大 INF 卸载范围");
        }
        Fake New() => new(Path.Combine(root, "inf-" + Guid.NewGuid().ToString("N")), inf, owner);
        async Task<RuntimeStepResult> Run(Fake fake, bool ownerOnly = false)
            => await new RuntimeUnifiedUninstaller(fake, fake.Diagnostics, fake.Pending)
                .RunAsync(ownerOnly ? Owner : Inf, ownerOnly ? "owner" : "inf", TimeSpan.FromSeconds(5), null, default);
        var normal = New();
        Check((await Run(normal)).Success && normal.Calls == 2 && normal.OwnerCleaned && normal.InfCleaned,
            "INF 先走真实包装作用域卸载两个 MSI，再清理自己的登记");
        Check(normal.ExecutedKinds.All(k => k == "Bundle") && normal.Arguments.All(a => a.Contains("IGNOREDEPENDENCIES=" + Bundle)),
            "INF 不猜测忽略依赖，实际 MSI 命令使用已验证包装范围");
        var partial = New(); partial.Results.Add(new(0)); partial.Results.Add(new(1603));
        Check(!(await Run(partial)).Success && partial.Calls == 2 && !partial.OwnerCleaned && !partial.InfCleaned,
            "INF 关联包装部分失败保留两层计划和登记");
        Check((await Run(partial)).Success && partial.Calls == 3 && partial.InfCleaned, "INF 重试跳过已移除的子 MSI 并完成两层清理");
        var pending = New(); pending.Results.Add(new(3010));
        Check((await Run(pending)).RequiresReboot && pending.Calls == 1 && !pending.InfCleaned, "INF 关联包装重启边界立即停止");
        Check((await Run(pending)).Success && pending.Calls == 2, "INF 重启续作不重复卸载已移除产品");
        var unknown = New(); unknown.Results.Add(new(null, "fixture alive", OutcomeUnknown: true));
        Check((await Run(unknown)).OutcomeUnknown && unknown.Calls == 1 && !unknown.OwnerCleaned && !unknown.InfCleaned,
            "INF 未知包装事务不清理记录、不启动第二个 MSI");
        var batch = New();
        var unified = new RuntimeUnifiedUninstaller(batch, batch.Diagnostics, batch.Pending);
        unified.PrepareBatch([Owner, Inf], default);
        Check((await Run(batch, true)).Success && (await Run(batch)).Success && batch.Calls == 2,
            "全量批次已移除包装后，INF 续作无需重新解析已删除的包装登记");
        var legacy = New();
        var installer = new RuntimeUnifiedUninstaller(legacy, legacy.Diagnostics, legacy.Pending);
        installer.Prepare(Inf);
        var legacyPath = Directory.GetFiles(legacy.Pending, "*.json").Single(path => JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("Entry").GetProperty("KeyName").GetString() == Inf.KeyName);
        var node = JsonNode.Parse(File.ReadAllText(legacyPath))!; node.AsObject().Remove("RelatedBundles"); File.WriteAllText(legacyPath, node.ToJsonString());
        Check((await Run(legacy)).Success && legacy.Calls == 2, "旧版单独 INF 计划重新核验后安全补齐包装证据");
        var denied = New(); denied.RefuseOwner = true;
        Check(!(await Run(denied)).Success && denied.Calls == 0 && File.Exists(Path.Combine(denied.Diagnostics.DirectoryPath, "events.jsonl")),
            "不可靠或共享归属在卸载前拒绝，失败时间线仍保留");

        var staleEntry = new VcRedistEntry { KeyName = Owner.KeyName, DisplayName = Owner.DisplayName, Architecture = Owner.Architecture,
            DisplayVersion = "14.42.34433", UninstallKind = "Bundle", UninstallString = Owner.UninstallString, QuietUninstallString = Owner.QuietUninstallString };
        var staleOwner = owner with { Entry = staleEntry, Products = [], RetainedProducts = [Additional, Minimum] };
        var staleInf = inf with { RelatedBundles = [staleOwner] };
        RuntimeUnifiedUninstallBackend.ValidateRelatedBundle(staleInf, staleOwner);
        Check(true, "本机旧 14.42 包装引用已升级 14.51 产品的关系可以安全核验");
        var stale = new Fake(Path.Combine(root, "stale-" + Guid.NewGuid().ToString("N")), staleInf, staleOwner);
        Check((await Run(stale)).Success && stale.Calls == 2 && stale.OwnerCleaned && stale.InfCleaned && stale.ExecutedKinds.All(k => k == "Inf"),
            "先只清理旧包装引用，再由 INF 卸载两个已明确选择的升级产品");

        var diagnostic = new RuntimeRepairDiagnostics(root);
        var failed = new OperationResult { Success = false, IsPartial = true, OutcomeUnknown = true, RequiresReboot = true,
            RequiresLogoff = true, RequiresDisplayConfirm = true, IsSkipped = true, Message = "fixture failed" };
        var attached = diagnostic.Complete(failed);
        Check(attached.DiagnosticsDirectory == diagnostic.DirectoryPath && failed.DiagnosticsDirectory == "" && attached.IsPartial
            && attached.OutcomeUnknown && attached.RequiresReboot && attached.RequiresLogoff && attached.RequiresDisplayConfirm && attached.IsSkipped,
            "结构化诊断目录保留所有结果标志且不改写原结果对象");
        var mapped = BackendRuntimeGuardService.Map(new(BackendResultStatuses.Failed, "fixture", "", [], true,
            JsonSerializer.SerializeToElement(attached, BackendJson.SerializerOptions)), "fixture");
        Check(mapped.DiagnosticsDirectory == diagnostic.DirectoryPath && mapped.OutcomeUnknown, "诊断目录穿过后端 JSON 与前端结果映射不丢失");
        Check(File.Exists(Path.Combine(diagnostic.DirectoryPath, "session.json")) && File.Exists(Path.Combine(diagnostic.DirectoryPath, "operation-result.json"))
            && File.Exists(Path.Combine(diagnostic.DirectoryPath, "请发送整个诊断文件夹.txt")), "诊断包含会话、最终结果和整包反馈说明");
        using (var session = JsonDocument.Parse(File.ReadAllText(Path.Combine(diagnostic.DirectoryPath, "session.json"))))
            Check(session.RootElement.GetProperty("ExecutionSha256").GetString()?.Length == 64, "版本号未变化时也能凭执行程序集哈希识别实际构建");
        var msiLog = Path.Combine(diagnostic.DirectoryPath, "dependency-msi.log");
        File.WriteAllText(msiLog, "WixDependencyCheck: Found dependent \"fixture\"\nCustomAction WixDependencyCheck returned actual error code 259\n"
            + string.Join("\n", Enumerable.Repeat("Property(S): unrelated", 80)) + "\nMainEngineThread is returning 0", System.Text.Encoding.Unicode);
        var evidence = diagnostic.ReadErrorDetails(msiLog);
        Check(evidence.Contains("259") && evidence.Contains("Found dependent") && evidence.Contains("returning 0"),
            "UTF-16 MSI 日志在长属性列表后仍保留依赖拦截与零退出码证据");
        foreach (var line in File.ReadLines(Path.Combine(normal.Diagnostics.DirectoryPath, "events.jsonl")))
            using (JsonDocument.Parse(line)) { }
        Check(File.ReadAllText(Path.Combine(normal.Diagnostics.DirectoryPath, "events.jsonl")).Contains("msi-postcheck"), "逐行时间线 JSON 可解析并记录实际产品复检");
        Console.WriteLine($"Runtime INF and diagnostic checks: {count} passed. All uninstall and registry operations simulated.");
        return count;
    }

    private sealed class Fake(string pending, RuntimeUnifiedUninstallPlan inf, RuntimeUnifiedUninstallPlan owner) : IRuntimeUnifiedUninstallBackend
    {
        internal string Pending => pending;
        internal RuntimeRepairDiagnostics Diagnostics = new(pending + "-diagnostics");
        internal int Calls; internal bool OwnerCleaned, InfCleaned, RefuseOwner;
        internal List<RuntimeStepResult> Results = [];
        internal List<string> ExecutedKinds = []; internal List<string[]> Arguments = [];
        private readonly Dictionary<string, RuntimeMsiProduct> _products = new(StringComparer.OrdinalIgnoreCase)
        {
            [Additional] = new(Additional, 5, "Microsoft Visual C++ 2026 X64 Additional Runtime", "Microsoft Corporation", "14.51.36247", "x64"),
            [Minimum] = new(Minimum, 5, "Microsoft Visual C++ 2026 X64 Minimum Runtime", "Microsoft Corporation", "14.51.36247", "x64"),
        };
        public RuntimeUnifiedUninstallPlan Resolve(VcRedistEntry entry)
        { if (RefuseOwner) throw new InvalidDataException("fixture shared or unconfirmed owner"); return entry.UninstallKind == "Inf" ? inf : owner; }
        public RuntimeMsiProduct ReadProduct(string product) => _products[product];
        public void ValidateRegistration(RuntimeUnifiedUninstallPlan plan)
        { foreach (var related in plan.RelatedBundles) RuntimeUnifiedUninstallBackend.ValidateRelatedBundle(plan, related); }
        public Task<RuntimeStepResult> UninstallAsync(RuntimeUnifiedUninstallPlan plan, string product, string log, TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
        {
            Calls++; ExecutedKinds.Add(plan.Entry.UninstallKind);
            var command = RuntimeUnifiedUninstallBackend.BuildUninstallCommand(plan, product, log); Arguments.Add(command.Arguments);
            if (plan.Entry.UninstallKind == "Inf" && !OwnerCleaned) return Task.FromResult(new RuntimeStepResult(0, "WixDependencyCheck 259"));
            var result = Results.Count == 0 ? new RuntimeStepResult(0) : Results[0]; if (Results.Count != 0) Results.RemoveAt(0);
            if (result.Success) _products[product] = _products[product] with { State = -1 };
            return Task.FromResult(result);
        }
        public void CleanupRegistration(RuntimeUnifiedUninstallPlan plan)
        {
            if (plan.RetainedProducts.Length == 0 && _products.Values.Any(p => p.State != -1)) throw new IOException("product remains");
            if (plan.Entry.UninstallKind == "Bundle") OwnerCleaned = true;
            else { if (!OwnerCleaned) throw new IOException("owner remains"); InfCleaned = true; }
        }
    }
}
