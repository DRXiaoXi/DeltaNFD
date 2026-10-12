using System.Diagnostics;
using System.Security.Cryptography;
using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

internal static class RuntimeCompatibilityChecks
{
    internal static async Task RunAsync()
    {
        var id = Guid.NewGuid(); var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-compat-" + id.ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        var count = 0;
        void Check(bool value, string text) { if (!value) throw new Exception(text); count++; }
        async Task<OperationResult> Run(Fake fake, CancellationToken token = default, TimeSpan? budget = null) =>
            await new RuntimeCompatibilityRepair(fake, new RuntimeRepairDiagnostics(root), budget).RunAsync(null, token);
        var plan = RuntimeCompatibilityRepair.CppPlan();
        RuntimeSxsChecks.Run(root, Check);
        Check(plan.Count == 18 && plan.All(s => s.Kind == "msi"), "共享卸载阶段之后安装/修复 18 个 MSI，不执行包装安装或旧基线覆盖");
        foreach (var step in plan)
        {
            var msi = Path.GetFullPath(Path.Combine("src/DeltaNFD/Assets/VCRedistRepair", step.RelativeFile));
            var packageWithHashes = RuntimeMsiInstallBackend.WithSourceHashes(RuntimeMsiMetadata.ReadPackage(msi), Path.GetDirectoryName(msi)!);
            Check(packageWithHashes.Files.Count > 0 && packageWithHashes.Files.All(f => f.Sha256.Length == 64),
                "强力模式关键 DLL 哈希源完整且无歧义：" + step.Label);
        }
        var good = new Fake();
        Check((await Run(good)).Success && good.UninstallCalls == 1 && good.DxCalls == 1 && good.CppCalls == 18
            && good.Trace.IndexOf("uninstall") < good.Trace.IndexOf("dx"), "资源验证后先卸载全部 C++，再安装 DirectX 和 C++");
        var strong = new Fake();
        Check((await RuntimeStrongRepairChecks.WithSystemPhaseAsync(root, strong.VerifyAsync,
            new RuntimeCompatibilityRepair(strong, new RuntimeRepairDiagnostics(root)).RunAsync)).Success
            && strong.DxCalls == 1 && strong.CppCalls == 18,
            "强力系统组件阶段与兼容性 DirectX 和 18 项 C++ 流程集成通过");
        var healthy = new Fake { Before = [new("d3dx9_43.dll", "x64", true, "healthy")] };
        Check((await Run(healthy)).Success && healthy.DxCalls == 0, "DirectX 组件健康时跳过安装器");
        var corruptResource = new Fake { Verified = false };
        Check(!(await Run(corruptResource)).Success && corruptResource.UninstallCalls == 0 && corruptResource.DxCalls == 0 && corruptResource.CppCalls == 0, "资源损坏时在任何卸载或安装前停止");
        var failedRemoval = new Fake { UninstallResult = new(1603) };
        Check(!(await Run(failedRemoval)).Success && failedRemoval.DxCalls == 0 && failedRemoval.CppCalls == 0, "卸载失败不继续 DirectX/C++ 安装");
        var badBatch = new Fake { PrepareFailed = true };
        Check(!(await Run(badBatch)).Success && badBatch.UninstallCalls == 0 && badBatch.DxCalls == 0 && badBatch.CppCalls == 0,
            "兼容性整批预检失败必须先于任何卸载和安装");
        var residue = new Fake { KeepInstalled = true };
        Check(!(await Run(residue)).Success && residue.UninstallCalls == 1 && residue.DxCalls == 0 && residue.CppCalls == 0,
            "兼容性卸载复检残留时禁止 DirectX 和 C++ 安装");
        var unknownRemoval = new Fake { UninstallResult = new(null, OutcomeUnknown: true) };
        Check((await Run(unknownRemoval)).OutcomeUnknown && unknownRemoval.DxCalls == 0, "卸载事务未知保留证据并停止后续");
        var rebootRemoval = new Fake { UninstallResult = new(3010) };
        Check((await Run(rebootRemoval)).RequiresReboot && rebootRemoval.CppCalls == 0, "卸载要求重启时不继续安装");
        var delayedDx = new Fake { DirectXResult = new(3010) };
        Check((await Run(delayedDx)).RequiresReboot && delayedDx.CppCalls == 0, "DirectX 返回 3010 后重启要求不能继续其他安装");
        var delayedCpp = new Fake { CppResult = new(3010) };
        Check((await Run(delayedCpp)).RequiresReboot && delayedCpp.CppCalls == 1, "C++ 返回 3010 停止后续步骤");
        var preRemovalProbe = new Fake { Before = [] };
        Check(!(await Run(preRemovalProbe)).Success && preRemovalProbe.UninstallCalls == 0, "DirectX 预检清单无效时先拒绝卸载");
        var unknownDx = new Fake { DirectXResult = new(null, OutcomeUnknown: true) };
        Check((await Run(unknownDx)).OutcomeUnknown && unknownDx.CppCalls == 0, "DirectX 未确认结束时不启动 C++ 安装");
        var immediateDx = new Fake { DirectXResult = new(1641) };
        var rebootResult = await Run(immediateDx);
        Check(!rebootResult.Success && rebootResult.RequiresReboot && immediateDx.CppCalls == 0, "DirectX 发起重启时停止且不伪报整个流程完成");
        var brokenFiles = new Fake { After = [new("d3dx9_43.dll", "x64", false, "corrupt")] };
        var partial = await Run(brokenFiles);
        Check(!partial.Success && partial.IsPartial && brokenFiles.CppCalls == 18, "DirectX 仍异常时如实报告部分完成，普通失败仍可修复独立 C++ 组件");
        var wrongProbe = new Fake { After = [new("other.dll", "x64", true, "wrong identity")] };
        Check(!(await Run(wrongProbe)).Success, "同数量但不同 DirectX 项目的复检不能通过");
        var cppFailed = new Fake { CppResult = new(1603, "fixture") };
        Check(!(await Run(cppFailed)).Success && cppFailed.CppCalls == 18, "卸载后普通 C++ 安装失败汇总，不冒充成功");
        var unknownCpp = new Fake { CppResult = new(null, OutcomeUnknown: true) };
        Check((await Run(unknownCpp)).OutcomeUnknown && unknownCpp.CppCalls == 1, "未知 MSI 结果立即停止后续且保留证据");
        var immediateCpp = new Fake { CppResult = new(1641) };
        Check(!(await Run(immediateCpp)).Success && immediateCpp.CppCalls == 1, "第一个 MSI 发起重启不等于所有依赖修复完成");
        var busyCpp = new Fake { CppResult = new(1618) };
        Check(!(await Run(busyCpp)).Success && busyCpp.CppCalls == 1, "Installer 忙时不重复发起剩余 17 个包");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel(); var fake = new Fake();
            Check(!(await Run(fake, cancellation.Token)).Success && fake.DxCalls == 0 && fake.CppCalls == 0, "起始取消不启动安装器");
        }
        var expired = new Fake();
        Check(!(await Run(expired, budget: TimeSpan.Zero)).Success && expired.DxCalls == 0, "预算耗尽不启动安装器");

        var file = Path.Combine(root, "probe.dll"); File.Copy(Environment.ProcessPath!, file);
        var v = FileVersionInfo.GetVersionInfo(file);
        var version = new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart, v.FilePrivatePart).ToString();
        string Digest() { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
        var probe = new DirectXProbe("probe.dll", "x64", version, Digest(), "fixture.cab");
        Check(RuntimeCompatibilityEnvironment.Probe(probe, root).Passed, "真实 PE 文件的版本、架构和哈希匹配才通过");
        Check(!RuntimeCompatibilityEnvironment.Probe(probe with { Sha256 = new string('0', 64) }, root).Passed, "同版本文件哈希异常被检出");
        Check(!RuntimeCompatibilityEnvironment.Probe(probe with { Architecture = "x86" }, root).Passed, "错误的 DLL 架构被检出");
        Check(!RuntimeCompatibilityEnvironment.Probe(probe with { Version = "99.0.0.0" }, root).Passed, "DLL 版本过旧被检出");
        File.Delete(file);
        Check(!RuntimeCompatibilityEnvironment.Probe(probe, root).Passed, "DLL 缺失被检出");
        var source = Path.Combine(Directory.GetCurrentDirectory(), "src/DeltaNFD/Assets/DirectXRepair");
        var payload = RuntimeCompatibilityEnvironment.ReadVerifiedPayload(source, default);
        Check(payload.Files.Count == 157 && payload.Probes.Count == 182, "微软原始 157 个资源哈希和 182 个目标检测项目通过");
        Check(payload.Probes.All(p => p.Name is not ("d3dcompiler_47.dll" or "xinput9_1_0.dll") && !p.Name.StartsWith("api-ms-")), "不把 Windows 内置核心和 API Sets 当作旧组件替换");
        var command = RuntimeCompatibilityEnvironment.DirectXCommand(source);
        Check(command.FileName.EndsWith("DXSETUP.exe") && command.Arguments == "/silent" && !command.UseShellExecute,
            "生产入口调用微软组件安装器，不启动第三方 DX 修复工具");
        Check(BackendModules.IsMutating(BackendModules.Runtime, BackendCommands.RuntimeCompatibilityRepair)
            && BackendModules.TimeoutFor(BackendModules.Runtime, BackendCommands.RuntimeCompatibilityRepair) > TimeSpan.FromMinutes(26),
            "兼容性修复纳入官方后端修改证据与超时边界");
        var family = Guid.NewGuid().ToString("B");
        var sourcePackage = new RuntimeMsiPackage(Guid.NewGuid().ToString("B"), "Microsoft Visual C++ Minimum", "Microsoft Corporation", "14.51.0", "x64",
            [new(family, "vcruntime140.dll", "vcruntime140.dll", new Version(14, 51))]) { UpgradeCode = family };
        var newer = sourcePackage with { Product = Guid.NewGuid().ToString("B"), Version = "14.53.0" };
        Check(RuntimeCompatibilityEnvironment.CanRetainRelatedPackage(sourcePackage, newer), "不同产品码但同一升级系列的较新运行库可保留");
        Check(!RuntimeCompatibilityEnvironment.CanRetainRelatedPackage(sourcePackage, newer with { UpgradeCode = Guid.NewGuid().ToString("B") }), "不把无关升级系列当成可保留版本");
        Check(!RuntimeCompatibilityEnvironment.CanRetainRelatedPackage(sourcePackage, newer with { Architecture = "x86" }), "不使用较新 x86 产品替代 x64 依赖");
        Check(!RuntimeCompatibilityEnvironment.CanRetainRelatedPackage(sourcePackage, newer with { Name = "Visual C++ Debug Runtime" }), "Debug 开发组件不能代替发布运行库");
        var page = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "src/DeltaNFD/Views/RuntimePage.xaml"));
        var vm = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "src/DeltaNFD/ViewModels/RuntimeViewModel.cs"));
        Check(page.Contains("高帧性修复") && page.Contains("兼容性修复") && vm.Contains("_guard.RepairCompatibilityAsync")
            && vm.Contains("_guard.RepairVcRedistAsync"), "两种修复保留独立入口和原 C++ 路由");
        Check(vm.Contains("CompatibilityRepairSelected = compatibility") && vm.Contains("CanCancelCompatibilityRepair")
            && vm.Contains("GuardToggleEnabled"), "两种修复共享忙碌门禁并保持新版防护互斥");
        Console.WriteLine($"兼容性修复检查通过：{count} 项；安装均模拟，仅只读核对微软资源和自有 PE 文件。");
    }
    private sealed class Fake : IRuntimeCompatibilityEnvironment
    {
        internal bool Verified = true, PrepareFailed, KeepInstalled;
        internal int DxCalls, CppCalls, ProbeCalls;
        internal int UninstallCalls;
        internal List<string> Trace = [];
        internal RuntimeStepResult UninstallResult = new(0);
        internal IReadOnlyList<DirectXProbeResult> Before = [new("d3dx9_43.dll", "x64", false, "missing")];
        internal IReadOnlyList<DirectXProbeResult> After = [new("d3dx9_43.dll", "x64", true, "healthy")];
        internal RuntimeStepResult DirectXResult = new(0), CppResult = new(0);
        public Task<IReadOnlyList<VcRedistEntry>> PrepareUninstallBatchAsync(IReadOnlyList<VcRedistEntry> entries, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Trace.Add("prepare"); if (PrepareFailed) throw new IOException("fixture bad batch"); return Task.FromResult(entries); }
        public Task<OperationResult> VerifyAsync(IProgress<string>? progress, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(Verified ? OperationResult.Ok("verified") : OperationResult.Fail("bad resource")); }
        public Task<IReadOnlyList<DirectXProbeResult>> ProbeAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(ProbeCalls++ == 0 ? Before : After); }
        public Task<RuntimeStepResult> InstallDirectXAsync(TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
        { Trace.Add("dx"); DxCalls++; return Task.FromResult(DirectXResult); }
        public Task<RuntimeStepResult> RepairCppAsync(RuntimeRepairStep step, TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
        { CppCalls++; return Task.FromResult(CppResult); }
        public Task<VcRedistScanReport> ScanCppAsync(CancellationToken token) => Task.FromResult(new VcRedistScanReport
        { All = UninstallCalls == 0 || KeepInstalled ? [new() { DisplayName = "Microsoft Visual C++ 2005 Redistributable", Architecture = "x86", KeyName = "fixture", UninstallKind = "Msi" }] : [], Abnormal = [], Suboptimal = [], Advice = "" });
        public Task<RuntimeStepResult> UninstallCppAsync(VcRedistEntry entry, TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
        { Trace.Add("uninstall"); UninstallCalls++; return Task.FromResult(UninstallResult); }
    }
}
