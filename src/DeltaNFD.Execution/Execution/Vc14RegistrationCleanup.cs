namespace DeltaNFD.Services;

/// <summary>Explicit registration-only operation; no installer, checkpoint or service preparation calls.</summary>
internal static class Vc14RegistrationCleanup
{
    private static IReadOnlyList<Vc14RemovalPlanItem> Plan(IReadOnlyList<Vc14ResidueItem> residues) => residues
        .Where(r => r.Kind == "msi-orphan").Select(r => new Vc14RemovalPlanItem(r.KeyName, r.Name.Contains(" X64 ", StringComparison.OrdinalIgnoreCase) ? "x64" : "x86", r.Name, r.Version,
            "msi-orphan", r.RegistrationFingerprint, "备份后仅删除此失效显示登记", r.Detail, Hive: r.HiveName, View: r.ViewName)).ToArray();

    internal static Vc14ReinstallPreview Preview(IVc14ReinstallEnvironment environment)
    {
        var scan = environment.Scan();
        try
        {
            var blocker = environment.Blocker();
            if (environment.IsSafeMode) blocker ??= "仅清理显示登记请在正常模式执行，无需进入安全模式或临时启用 Installer。";
            if (scan.Issues.Count > 0) blocker ??= "MSI 检测未完成，不能确认显示登记失效：\n" + string.Join("\n", scan.Issues);
            var residues = blocker is null ? environment.Residues(scan) : [];
            var plan = Plan(residues);
            if (plan.Count == 0) blocker ??= "未发现已确认可清理的失效 MSI 显示登记。\n" + string.Join("\n",
                residues.Where(r => r.Kind == "keep" && Vc14MsiOrphans.IsCandidate(r.Name)
                    && !scan.Products.Any(p => p.Code.Equals(r.KeyName, StringComparison.OrdinalIgnoreCase)))
                    .Select(r => r.Name + "：" + r.Detail).Take(8));
            return new(scan, plan, Vc14Reinstall.StageFingerprint(plan, Vc14RepairMode.RegistrationCleanup, environment.PendingInstall(), scan),
                blocker is null, blocker ?? "");
        }
        catch (Exception ex) { return new(scan, [], "", false, "失效登记核验未完成：" + ex.Message); }
    }

    internal static OperationResult Run(IVc14ReinstallEnvironment environment, string fingerprint, IProgress<string>? progress, CancellationToken token)
    {
        OperationResult? cleanup = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var preview = Preview(environment);
            if (!preview.CanRepair) return new() { Message = preview.Blocker };
            if (fingerprint != preview.Fingerprint) return new() { Message = "显示登记或 MSI 状态已变化，请重新预览确认。" };
            var confirmed = environment.Residues(preview.Scan).Where(r => r.Kind == "msi-orphan").ToArray();
            if (Vc14Reinstall.StageFingerprint(Plan(confirmed), Vc14RepairMode.RegistrationCleanup, environment.PendingInstall(), preview.Scan) != fingerprint)
                return new() { Message = "显示登记证据已变化，未执行清理，请重新预览。" };
            environment.Trace("confirmed-registration-cleanup", new { preview.Fingerprint, Items = confirmed });
            var result = cleanup = environment.CleanupRegistrations(preview.Scan, confirmed, progress, token);
            if (!result.Success) return result;
            var after = environment.Scan();
            environment.Trace("registration-cleanup-postcheck", after);
            if (after.Issues.Count > 0 || Vc14Catalog.Fingerprint(after) != Vc14Catalog.Fingerprint(preview.Scan)
                || environment.Residues(after).Any(r => confirmed.Any(c => c.HiveName == r.HiveName && c.ViewName == r.ViewName && c.KeyName == r.KeyName)))
                return new() { IsPartial = true, DiagnosticsDirectory = result.DiagnosticsDirectory,
                    Message = "清理后登记仍存在或 MSI 状态发生变化，未确认完成；保留备份，请核对。\n" + result.Message };
            return new() { Success = true, DiagnosticsDirectory = result.DiagnosticsDirectory,
                Message = result.Message + "\n仅处理了失效显示登记，已安装运行库组件保持原状；未执行运行库安装或卸载。" };
        }
        catch (OperationCanceledException) { return new() { IsPartial = cleanup is not null, OutcomeUnknown = cleanup is not null,
            DiagnosticsDirectory = cleanup?.DiagnosticsDirectory ?? environment.DiagnosticsDirectory, Message = "已停止后续登记清理，请核对保留的备份。" }; }
        catch (Exception ex) { return new() { IsPartial = cleanup is not null, OutcomeUnknown = cleanup is not null,
            DiagnosticsDirectory = cleanup?.DiagnosticsDirectory ?? environment.DiagnosticsDirectory, Message = "失效登记清理未完成：" + ex.Message }; }
    }
}
