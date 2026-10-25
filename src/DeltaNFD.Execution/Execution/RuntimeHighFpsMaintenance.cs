using System.Security.Cryptography;
using System.Text.Json;
using AlphaService = DeltaNFD.Services.AlphaRestore.RuntimeGuardService;

namespace DeltaNFD.Services;

/// <summary>Alpha0.916 高帧性流程的受控恢复；AIO 与覆盖安装均不含 2026 安装内容。</summary>
public static class RuntimeHighFpsMaintenance
{
    public static Task<RuntimeHighFpsPreview> PreviewAsync(bool strongMode, Vc14Baseline baseline = Vc14Baseline.V142) => Task.Run(async () =>
    {
        try
        {
            if (!baseline.IsSupported()) return new(false, "未知的 14.x 安装基线，未执行修复。", "", strongMode, []) { Baseline = baseline };
            var selected = Vc14BaselineInfo.For(baseline);
            if (strongMode) return new(false, "强力修复模式已移除，请使用全量修复重新预览确认。", "", false, []) { Baseline = baseline };
            if (Vc14InstallerReadiness.IsSafeMode)
                return new RuntimeHighFpsPreview(false, "全量修复包含安装步骤，请返回正常模式。安全模式仅使用 14.x 单独卸载入口，且必须先确认 Windows Installer 可用。", "", strongMode, []) { Baseline = baseline };
            if (new Vc14ReinstallEnvironment(baseline: baseline).Blocker() is { } blocked)
                return new RuntimeHighFpsPreview(false, blocked, "", strongMode, []) { Baseline = baseline };
            var service = new AlphaService();
            var ready = await service.CheckRepairEnvironmentAsync();
            if (!ready.CanProceed) return new(false, ready.Summary + "\n" + ready.Details, "", strongMode, []) { Baseline = baseline };
            var root = Path.Combine(BackendEnvironment.AssetsRoot, "RuntimeHighFps");
            var verified = AlphaService.VerifyRepairPackage(BackendEnvironment.AssetsRoot, root, null);
            if (!verified.Success) return new(false, verified.Message, "", strongMode, []) { Baseline = baseline };
            AlphaRestore.RuntimeCppOverwriteEnvironment.VerifyCppPayload(Path.Combine(BackendEnvironment.AssetsRoot, "RuntimeCppOfficial"), default, true);
            var scan = await service.ScanVcRedistsAsync();
            var pending = new Vc14ReinstallEnvironment(baseline: baseline).PendingInstall();
            var legacyFingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Entries = scan.All, Pending = pending })));
            var current = Vc14Catalog.Scan();
            if (current.Issues.Count > 0) return new(false, string.Join("\n", current.Issues), "", strongMode, []) { Baseline = baseline };
            var plan = Vc14Reinstall.BuildRemovalPlan(current, verifyDependencies: true);
            var needsUninstall = NeedsV14Uninstall(current, plan, pending, baseline);
            var v14Fingerprint = Vc14Reinstall.Fingerprint(plan, baseline);
            var fingerprint = Fingerprint(legacyFingerprint, v14Fingerprint, strongMode, baseline);
            return new(true, "", fingerprint, strongMode, AlphaRestore.OfficialCppCatalog.Packages.Select(p =>
                p.Year + " · " + p.Arch + " · 微软原包（" + "检测后安装或修复" + "，保留已核验的较新组件）")
                .Concat(new[] { needsUninstall ? "V14：先受控卸载，优先使用已核验微软原包装，再安装自带 " + selected.Label + " 双架构原包" : "V14：无待卸载项或续装已核验的部分基线，使用自带 " + selected.Label + " 双架构原包安装/修复", "辅助组件：安装/修复 VSTO 与旧 VC/VB，不执行显式卸载" })
                .Concat(needsUninstall ? plan.Select(p => "V14 卸载清单：" + p.Name + " · " + p.Architecture + " · " + p.Version + " · " + p.Method) : []).ToArray())
                { V14Fingerprint = v14Fingerprint, HasUninstallSteps = needsUninstall, Baseline = baseline };
        }
        catch (Exception ex) { return new(false, "修复前核对未完成：" + ex.Message, "", strongMode, []) { Baseline = baseline }; }
    });

    internal static string Fingerprint(string legacy, string v14, bool strongMode, Vc14Baseline baseline = Vc14Baseline.V142) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Legacy = legacy, V14 = v14, StrongMode = strongMode, Baseline = baseline })));

    internal static bool NeedsV14Uninstall(Vc14Scan scan, IReadOnlyList<Vc14RemovalPlanItem> plan, Vc14Continuation? pending = null,
        Vc14Baseline baseline = Vc14Baseline.V142)
    {
        if (scan.Products.Count == 0 && plan.Count == 0) return false;
        var version = Vc14BaselineInfo.For(baseline).Version;
        if (scan.Issues.Count > 0 || pending?.UninstallVerified != true || pending.Baseline != baseline
            || scan.Products.Any(p => !Vc14Catalog.SameVersion(p.Version, version))) return true;
        // Only resume a partially installed pinned baseline after a verified uninstall stage.
        return plan.Any(p => p.Kind is not ("msi" or "bundle") || !Vc14Catalog.SameVersion(p.Version, version)
            || !scan.Products.Any(c => c.Architecture == p.Architecture && Vc14Catalog.SameVersion(c.Version, p.Version)));
    }

    public static Task<OperationResult> RepairAsync(string fingerprint, bool strongMode,
        IProgress<string>? progress, CancellationToken token, Vc14Baseline baseline = Vc14Baseline.V142)
    {
        if (strongMode) return Task.FromResult(OperationResult.Fail("强力修复模式已移除，请使用全量修复重新预览确认。"));
        if (!baseline.IsSupported()) return Task.FromResult(OperationResult.Fail("未知的 14.x 安装基线，未执行修复。"));
        if (Vc14InstallerReadiness.IsSafeMode)
            return Task.FromResult(OperationResult.Fail("全量修复包含安装步骤，请返回正常模式执行；安全模式只使用 14.x 单独卸载入口并先确认 Installer 可用。"));
        return new AlphaService().RepairVcRedistAsync(progress, AlphaRestore.RuntimeRepairMode.FullReinstall, token, strongMode, fingerprint, baseline);
    }
}
