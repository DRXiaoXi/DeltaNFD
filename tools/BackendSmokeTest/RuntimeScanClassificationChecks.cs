using DeltaNFD.Services;

internal static class RuntimeScanClassificationChecks
{
    internal static void Run(Action<bool, string> check)
    {
        VcRedistEntry Entry(string year, string version, string arch = "x64", string kind = "Bundle") => new()
        {
            DisplayName = $"Microsoft Visual C++ {year} Redistributable ({arch})", DisplayVersion = version,
            Architecture = arch, UninstallKind = kind, KeyName = "{33d1fd90-4274-48a1-9bc1-97e33d9c2d6f}",
        };
        VcRedistScanReport Scan(params VcRedistEntry[] entries) => RuntimeGuardService.BuildVcRedistScanReport(entries);
        var ordinary = Entry("2015-2022", "14.42.34433");
        var normal = Scan(ordinary);
        check(!normal.Suboptimal.Any(s => s.Branch is "2026 (x64)" or "2015-2026 (x64)"),
            "14.42 年份命名运行库满足统一分支，不因没有 2026 名称报告缺失");
        var namedV14 = Entry("v14", "14.51.36247");
        var mixed = Scan(ordinary, namedV14);
        check(mixed.Abnormal.Count == 1 && mixed.Abnormal[0] == namedV14,
            "保留用户确认的 v14 三角洲兼容性异常策略，不扩大到所有 14.x");
        check(Scan(namedV14).Suboptimal.Any(s => s.Branch == "2015-2026 (x64)" && s.Reason == "missing"),
            "只有异常 v14 时不能冒充已安装推荐的正常年份分支");
        var older = Scan(Entry("2012", "11.0.61030"));
        var advisory = older.Suboptimal.Single(s => s.Branch == "2012 (x64)");
        check(advisory.IsRecommendationOnly && advisory.Reason == "below-recommended"
            && advisory.InstalledVersion == "11.0.61030", "已有旧分支与热修复推荐版本差异仅作为建议");
        check(!Scan(Entry("2010", "10.0.40219")).Suboptimal.Any(s => s.Branch == "2010 (x64)"),
            "安装登记省略修订号不等同修订号零，避免 10.0.40219 虚低提醒");
        check(Scan(Entry("2010", "10.0.40218")).Suboptimal.Any(s => s.Branch == "2010 (x64)"),
            "缺少修订号仍可检测实际主版本/build 更旧");
        var unknown = Scan(Entry("2012", "bad" )).Suboptimal.Single(s => s.Branch == "2012 (x64)");
        check(unknown.Reason == "version-unknown" && !unknown.IsRecommendationOnly,
            "已有登记但损坏版本字段不能被说成未安装或正常");
        foreach (var invalid in new[] { "11.0", "14.51.36247" })
            check(Scan(Entry("2012", invalid)).Suboptimal.Any(s => s.Branch == "2012 (x64)" && s.Reason == "version-unknown"),
                "缺少 build 或与分支主版本不符时无法核对：" + invalid);
        var msi = Entry("2012", "11.0.61030", kind: "Msi");
        RuntimeMsiProduct Product(string version, int state = 5) => new(msi.KeyName, state, msi.DisplayName,
            "Microsoft Corporation", version, "x64");
        var accurate = RuntimeGuardService.BuildVcRedistScanReport([msi], _ => Product("11.0.61135.400"));
        check(!accurate.Suboptimal.Any(s => s.Branch == "2012 (x64)"),
            "使用 MSI 实际产品版本纠正旧 ARP 显示版本");
        var orphan = RuntimeGuardService.BuildVcRedistScanReport([msi], _ => Product("11.0.61135.400", -1));
        check(orphan.Suboptimal.Any(s => s.Branch == "2012 (x64)" && s.Reason == "missing"),
            "只有卸载表残留且 MSI 未安装，不能证明分支已存在");
        foreach (var product in new[] { Product("11.0.61135.400", 1), Product("11.0.61135.400") with { Publisher = "foreign" },
            Product("11.0.61135.400") with { Architecture = "x86" }, Product("invalid") })
        {
            var report = RuntimeGuardService.BuildVcRedistScanReport([msi], _ => product);
            check(report.Suboptimal.Any(s => s.Branch == "2012 (x64)" && s.Reason == "version-unknown"),
                "MSI 状态/发布者/架构/版本无法确认，保留无法核对提醒");
        }
        var denied = RuntimeGuardService.BuildVcRedistScanReport([msi], _ => throw new IOException("fixture denied"));
        check(denied.Suboptimal.Any(s => s.Reason == "version-unknown"), "MSI 读取失败不能当作健康或直接用旧字段掩盖");
        VcRedistScanReport Report(VcRedistSuboptimal issue) => new() { All = [], Abnormal = [], Suboptimal = [issue], Advice = "" };
        OperationResult Result(VcRedistSuboptimal issue) => RuntimeRepairDiagnostics.BuildResult(0,
            [new("fixture.msi", 0, "fixture.log", "")], [], Report(issue), false, "fixture", "");
        check(Result(advisory).Success && Result(advisory).Message.Contains("不作为修复失败"),
            "安装及复检成功时推荐版本差异不能使修复失败");
        check(!Result(unknown).Success && Result(unknown).Message.Contains("版本无法核对"),
            "无法核对的实际登记继续阻止宣称分支复检通过");
        check(!Result(new() { Branch = "2005 (x64)", RecommendedVersion = "8.0.61186", Reason = "missing" }).Success,
            "真实缺失分支仍报告失败");
    }
}
