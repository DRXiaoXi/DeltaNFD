using DeltaNFD.Services;

internal static class RuntimeUnifiedUninstallChecks
{
    private static readonly string First = "{11111111-1111-1111-1111-111111111111}";
    private static readonly string Second = "{22222222-2222-2222-2222-222222222222}";

    internal static async Task RunAsync(bool readOnly = false)
    {
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD_Unified_" + id.ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
        VcRedistEntry Entry(string kind = "Inf") => new() { DisplayName = "Microsoft Visual C++ 2008 Redistributable (x86)",
            DisplayVersion = "9.0.30729.7523", Architecture = "x86", KeyName = "fixture", UninstallKind = kind };
        RuntimeMsiProduct Product(string code) => new(code, 5, "Microsoft Visual C++ 2008 Redistributable", "Microsoft Corporation", "9.0.30729.7523", "x86");
        async Task<RuntimeStepResult> Run(Fake fake, CancellationToken token = default, TimeSpan? budget = null)
            => await new RuntimeUnifiedUninstaller(fake, new RuntimeRepairDiagnostics(root), fake.Pending)
                .RunAsync(Entry(), "fixture", budget ?? TimeSpan.FromSeconds(5), null, token);
        Fake New() => new(Path.Combine(root, Guid.NewGuid().ToString("N")), Entry(), Product(First), Product(Second));

        if (readOnly)
        {
            var diagnostics = new RuntimeRepairDiagnostics(root);
            var backend = new RuntimeUnifiedUninstallBackend(diagnostics);
            foreach (var entry in (await new RuntimeGuardService().ScanVcRedistsAsync()).All)
            {
                try
                {
                    var plan = backend.Resolve(entry);
                    backend.ValidateRegistration(plan);
                    foreach (var product in plan.Products.Concat(plan.RetainedProducts)) RuntimeUnifiedUninstaller.ValidateProduct(entry, backend.ReadProduct(product));
                    Console.WriteLine("READONLY OK: " + entry.DisplayName + " -> 卸载=" + string.Join(",", plan.Products) + "; 保留=" + string.Join(",", plan.RetainedProducts));
                }
                catch (Exception ex) { Console.WriteLine("READONLY REFUSED: " + entry.DisplayName + " -> " + ex.Message); }
            }
            Console.WriteLine("只读映射完成；未运行 MSI、EXE、INF 或登记清理。");
            return;
        }

        var ok = New();
        Check((await Run(ok)).Success && ok.Calls.Count == 2 && ok.Cleanups == 1, "两个关联 MSI 逐个移除后才清理包装登记");
        Check(!Directory.GetFiles(ok.Pending, "*.json").Any() && Directory.GetFiles(Path.Combine(ok.Pending, "completed")).Length == 1, "完成计划归档而非抹去证据");
        var resumed = New(); resumed.Results.Add(new(0)); resumed.Results.Add(new(1603));
        Check(!(await Run(resumed)).Success && resumed.Cleanups == 0 && resumed.Calls.Count == 2, "第二组件失败保留包装登记和计划");
        Check((await Run(resumed)).Success && resumed.Calls.Count == 3 && resumed.Resolves == 1, "重试使用保存计划，跳过已移除产品，不扩大范围");
        foreach (var code in new[] { 1603, 1618, 1612, 1638, 1641 })
        {
            var fake = New(); fake.Results.Add(new(code));
            Check(!(await Run(fake)).Success && fake.Calls.Count == 1 && fake.Cleanups == 0, "关键错误/立即重启停止后续：" + code);
        }
        var unknown = New(); unknown.Results.Add(new(null, "running", OutcomeUnknown: true));
        Check((await Run(unknown)).OutcomeUnknown && unknown.Calls.Count == 1 && unknown.Cleanups == 0, "未知事务不被清理或当成完成");
        var still = New(); still.LeaveInstalled = true;
        Check(!(await Run(still)).Success && still.Calls.Count == 1 && still.Cleanups == 0, "退出零但仍登记不能算卸载成功");
        var invalid = New(); invalid.Products[Second] = Product(Second) with { Publisher = "not Microsoft" };
        Check(!(await Run(invalid)).Success && invalid.Calls.Count == 0, "全部身份预检先于第一项卸载");
        foreach (var wrong in new[] { Product(First) with { Architecture = "x64" }, Product(First) with { Version = "14.1.1" },
            Product(First) with { Name = "Microsoft Visual C++ 2008 Debug Runtime" }, Product(First) with { State = 2 } })
        {
            var fake = New(); fake.Products[First] = wrong;
            Check(!(await Run(fake)).Success && fake.Calls.Count == 0, "架构/分支/Debug/未知状态拒绝");
        }
        var reboot = New(); reboot.Results.Add(new(3010)); reboot.Results.Add(new(1603));
        Check((await Run(reboot)).RequiresReboot, "后续失败不丢先前重启需求");
        var unknownRelation = New(); unknownRelation.ResolveFailed = true;
        Check(!(await Run(unknownRelation)).Success && unknownRelation.Calls.Count == 0, "缺少可靠关联时拒绝，不猜产品码");
        var changed = New(); changed.ChangeAfterFirst = true;
        Check(!(await Run(changed)).Success && changed.Calls.Count == 1 && changed.Cleanups == 0, "关联/登记外部变化停止后续");
        var cleanup = New(); cleanup.CleanupFailed = true;
        Check(!(await Run(cleanup)).Success && Directory.GetFiles(cleanup.Pending, "*.json").Length == 1, "登记清理失败计划保留");
        cleanup.CleanupFailed = false;
        Check((await Run(cleanup)).Success && cleanup.Calls.Count == 2, "登记清理可续作，不重复卸载已不存在产品");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var cancel = New(); Check(!(await Run(cancel, canceled.Token)).Success && cancel.Calls.Count == 0, "启动前取消不运行 MSI");
        var timed = New(); Check(!(await Run(timed, budget: TimeSpan.Zero)).Success && timed.Calls.Count == 0, "耗尽总预算不启动 MSI");
        var skipped = New(); skipped.Products[First] = Product(First) with { State = -1 }; skipped.Products[Second] = Product(Second) with { State = -1 };
        Check((await Run(skipped)).Success && skipped.Calls.Count == 0 && skipped.Cleanups == 1, "已不存在组件只复核并清理确认的包装登记");
        var superseded = New(); superseded.Superseded = true;
        Check((await Run(superseded)).Success && superseded.Calls.Count == 0 && superseded.Products.Values.All(p => p.State == 5)
            && superseded.Cleanups == 1, "旧包装登记清理不卸载其已升级的关联 MSI");
        Check(!RuntimeUnifiedUninstallBackend.ShouldRetainNewerMsi(Entry(), Product(First) with { Version = "9.0.30729.7523" }), "同版本产品才进入正常卸载");
        Check(RuntimeUnifiedUninstallBackend.ShouldRetainNewerMsi(Entry(), Product(First) with { Version = "9.0.30729.9999" }), "真正新版 MSI 标记保留");
        var lowerRefused = false;
        try { RuntimeUnifiedUninstallBackend.ShouldRetainNewerMsi(Entry(), Product(First) with { Version = "9.0.30729.1" }); }
        catch (InvalidDataException) { lowerRefused = true; }
        Check(lowerRefused, "无法解释的旧版本关联拒绝，不直接卸载");
        var msi = Directory.GetFiles(Path.Combine(Directory.GetCurrentDirectory(), "src/DeltaNFD/Assets/VCRedistRepair"), "*.msi", SearchOption.AllDirectories)
            .Where(f => new DirectoryInfo(Path.GetDirectoryName(f)!).Name is "x86" or "x64");
        foreach (var file in msi)
            Check(RuntimeMsiMetadata.ReadArchitecture(file) == new DirectoryInfo(Path.GetDirectoryName(file)!).Name, "真实资源 MSI 架构只读核对：" + file);
        count += await RuntimeDependencyUninstallChecks.RunAsync(root);
        count += await RuntimeUninstallBatchChecks.RunAsync(root);
        count += await RuntimeInfUninstallChecks.RunAsync(root);
        Console.WriteLine($"统一卸载检查通过：{count} 项；卸载和登记清理均为模拟，只读读取 MSI 元数据。");
    }

    private sealed class Fake(string pending, VcRedistEntry entry, params RuntimeMsiProduct[] products) : IRuntimeUnifiedUninstallBackend
    {
        internal string Pending => pending;
        internal readonly Dictionary<string, RuntimeMsiProduct> Products = products.ToDictionary(p => p.Code);
        internal List<string> Calls = []; internal List<RuntimeStepResult> Results = [];
        internal int Cleanups, Resolves; internal bool ResolveFailed, LeaveInstalled, ChangeAfterFirst, CleanupFailed, Superseded;
        public RuntimeUnifiedUninstallPlan Resolve(VcRedistEntry target)
        { Resolves++; if (ResolveFailed) throw new IOException("missing relation"); return Superseded
                ? new(entry, [], [new("fixture",256,"original")]) { RetainedProducts = [First, Second] }
                : new(entry, [First, Second], [new("fixture",256,"original")]); }
        public RuntimeMsiProduct ReadProduct(string product) => Products[product];
        public void ValidateRegistration(RuntimeUnifiedUninstallPlan plan)
        { if (ChangeAfterFirst && Calls.Count > 0) throw new IOException("changed"); }
        public Task<RuntimeStepResult> UninstallAsync(RuntimeUnifiedUninstallPlan plan, string product, string log, TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
        {
            Calls.Add(product); var result = Results.Count == 0 ? new RuntimeStepResult(0) : Results[0]; if (Results.Count > 0) Results.RemoveAt(0);
            if (!LeaveInstalled && (result.Success || result.ExitCode == 1605)) Products[product] = Products[product] with { State = -1 };
            return Task.FromResult(result);
        }
        public void CleanupRegistration(RuntimeUnifiedUninstallPlan plan)
        { if (CleanupFailed) throw new IOException("cleanup failed"); Cleanups++; }
    }
}
