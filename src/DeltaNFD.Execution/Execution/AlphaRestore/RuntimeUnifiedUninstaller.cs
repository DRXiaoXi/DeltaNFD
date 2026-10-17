using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeltaNFD.Services.AlphaRestore;

internal sealed record RuntimeMsiProduct(string Code, int State, string Name = "", string Publisher = "", string Version = "", string Architecture = "");
internal sealed record RuntimeRegistrationSnapshot(string KeyPath, int View, string Content);
internal sealed record RuntimeUnifiedUninstallPlan(VcRedistEntry Entry, string[] Products, RuntimeRegistrationSnapshot[] Registrations)
{
    public string[] RetainedProducts { get; init; } = [];
    public RuntimeUnifiedUninstallPlan[] RelatedBundles { get; init; } = [];
    public bool AllowNonstandard { get; init; }
}

internal interface IRuntimeUnifiedUninstallBackend
{
    RuntimeUnifiedUninstallPlan Resolve(VcRedistEntry entry);
    RuntimeMsiProduct ReadProduct(string product);
    void ValidateRegistration(RuntimeUnifiedUninstallPlan plan);
    Task<RuntimeStepResult> UninstallAsync(RuntimeUnifiedUninstallPlan plan, string product, string logPath, TimeSpan timeout, IProgress<string>? progress, CancellationToken token);
    void CleanupRegistration(RuntimeUnifiedUninstallPlan plan);
}

internal sealed class RuntimeUnifiedUninstaller(IRuntimeUnifiedUninstallBackend backend, RuntimeRepairDiagnostics diagnostics, string pendingRoot)
{
    internal static readonly TimeSpan MaximumEntryBudget = TimeSpan.FromMinutes(12);

    internal IReadOnlyList<VcRedistEntry> PrepareBatch(IReadOnlyList<VcRedistEntry> entries, CancellationToken token)
    {
        // Resolve every entry (including children later covered by wrappers) before any MSI can run.
        var plans = new List<RuntimeUnifiedUninstallPlan>();
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            plans.Add(Prepare(entry));
        }
        var selected = SelectBatchPlans(plans);
        var folder = Path.Combine(pendingRoot, "batches");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            JsonSerializer.Serialize(file, new { Plans = plans, Selected = selected });
            file.Flush(true);
        }
        diagnostics.Save("uninstall-batch.json", new { Plans = plans, Selected = selected, Evidence = path });
        return selected.Select(p => p.Entry).ToArray();
    }

    internal static IReadOnlyList<RuntimeUnifiedUninstallPlan> SelectBatchPlans(IEnumerable<RuntimeUnifiedUninstallPlan> plans)
    {
        var ordered = plans.OrderBy(p => p.Entry.UninstallKind == "Bundle" ? 0 : p.Entry.UninstallKind == "Inf" ? 1 : 2)
            .ThenBy(p => p.Entry.KeyName, StringComparer.OrdinalIgnoreCase).ToArray();
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<RuntimeUnifiedUninstallPlan>();
        foreach (var plan in ordered)
        {
            if (plan.Entry.UninstallKind == "Msi" && plan.Products.All(covered.Contains)) continue;
            // Keep each wrapper for its own validated registration cleanup; never skip a retained MSI.
            result.Add(plan);
            foreach (var product in plan.Products) covered.Add(product);
        }
        return result;
    }

    internal static void ValidateProduct(VcRedistEntry entry, RuntimeMsiProduct product)
    {
        if (!Guid.TryParse(product.Code, out var id) || id == Guid.Empty) throw new InvalidDataException("MSI 产品码无效。");
        if (product.State == -1) return;
        if (product.State != 5) throw new InvalidDataException("MSI 安装状态无法确认：" + product.Code);
        if (!product.Publisher.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
            || !Regex.IsMatch(product.Name, @"^Microsoft Visual C\+\+ (?:2005|2008|2010|2012|2013|2015|2017|2019|2022|2026|v14)\b", RegexOptions.IgnoreCase)
            || !Regex.IsMatch(product.Name, @"\b(?:Redistributable|Minimum Runtime|Additional Runtime)\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(product.Name, @"\b(?:Debug|SDK|VSTO|Tools)\b", RegexOptions.IgnoreCase)
            || !System.Version.TryParse(product.Version, out var version))
            throw new InvalidDataException("不是可确认的微软 C++ 可再发行产品：" + product.Code);
        if (entry.Architecture is not ("x64" or "x86") || product.Architecture != entry.Architecture)
            throw new InvalidDataException("MSI 架构与所选运行库不一致：" + product.Code);
        var major = entry.DisplayName.Contains("2005") ? 8 : entry.DisplayName.Contains("2008") ? 9
            : entry.DisplayName.Contains("2010") ? 10 : entry.DisplayName.Contains("2012") ? 11
            : entry.DisplayName.Contains("2013") ? 12 : 14;
        if (version.Major != major) throw new InvalidDataException("MSI 分支与所选运行库不一致：" + product.Code);
    }

    internal RuntimeUnifiedUninstallPlan Prepare(VcRedistEntry entry)
    {
        diagnostics.Event("uninstall-prepare", new { entry.KeyName, entry.DisplayName, entry.UninstallKind });
        var pending = PendingPath(entry);
        RuntimeUnifiedUninstallPlan plan;
        if (File.Exists(pending))
        {
            if ((File.GetAttributes(pending) & FileAttributes.ReparsePoint) != 0 || new FileInfo(pending).Length > 1024 * 1024)
                throw new InvalidDataException("统一卸载记录无效，未覆盖。");
            var json = File.ReadAllText(pending);
            plan = JsonSerializer.Deserialize<RuntimeUnifiedUninstallPlan>(json) ?? throw new InvalidDataException("卸载记录为空。");
            if (JsonSerializer.Serialize(plan.Entry) != JsonSerializer.Serialize(entry)) throw new InvalidDataException("卸载记录条目已变化。");
            using var document = JsonDocument.Parse(json);
            if (entry.UninstallKind == "Inf" && !document.RootElement.TryGetProperty(nameof(plan.RelatedBundles), out _))
            {
                var current = backend.Resolve(entry);
                if (!plan.Products.SequenceEqual(current.Products, StringComparer.OrdinalIgnoreCase)
                    || JsonSerializer.Serialize(plan.Registrations) != JsonSerializer.Serialize(current.Registrations)
                    || plan.RetainedProducts.Length != 0)
                    throw new IOException("旧 INF 计划与当前身份不一致，保留原记录，未继续。");
                plan = current;
                var temporary = pending + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    { JsonSerializer.Serialize(file, plan); file.Flush(true); }
                    File.Move(temporary, pending, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        else plan = backend.Resolve(entry);
        var all = plan.Products.Concat(plan.RetainedProducts).ToArray();
        if (all.Length is < 1 or > 4 || all.Distinct(StringComparer.OrdinalIgnoreCase).Count() != all.Length)
            throw new InvalidDataException("关联 MSI 数量异常或重复。");
        backend.ValidateRegistration(plan);
        foreach (var product in all) ValidateProduct(entry, backend.ReadProduct(product));
        if (plan.RelatedBundles.Length > 4) throw new InvalidDataException("INF 包装关联数量超限。");
        foreach (var owner in plan.RelatedBundles)
        {
            RuntimeUnifiedUninstallBackend.ValidateRelatedBundle(plan, owner);
            if (owner.Products.All(p => backend.ReadProduct(p).State == -1)) continue;
            var preparedOwner = Prepare(owner.Entry);
            if (JsonSerializer.Serialize(preparedOwner) != JsonSerializer.Serialize(owner))
                throw new IOException("INF 关联包装的恢复计划不一致，未继续。");
        }
        Directory.CreateDirectory(pendingRoot);
        if (!File.Exists(pending))
        {
            using var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(file, plan); file.Flush(true);
        }
        return plan;
    }

    private string PendingPath(VcRedistEntry entry) => Path.Combine(pendingRoot,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry)))) + ".json");

    internal async Task<RuntimeStepResult> RunAsync(VcRedistEntry entry, string stepId, TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        var reboot = false; var removed = 0;
        var pending = PendingPath(entry);
        var id = Path.GetFileNameWithoutExtension(pending);
        try
        {
            token.ThrowIfCancellationRequested();
            var plan = Prepare(entry);
            diagnostics.Event("uninstall-plan-verified", new { entry.KeyName, plan.Products, RelatedBundles = plan.RelatedBundles.Select(b => b.Entry.KeyName).ToArray() });
            var productBudget = TimeSpan.FromMinutes(3 * Math.Clamp(plan.Products.Length, 1, 4));
            if (timeout > productBudget) timeout = productBudget;
            diagnostics.Save(stepId + "-unified-plan.json", plan);
            for (var ownerIndex = 0; ownerIndex < plan.RelatedBundles.Length; ownerIndex++)
            {
                token.ThrowIfCancellationRequested();
                var owner = plan.RelatedBundles[ownerIndex];
                backend.ValidateRegistration(owner);
                if (owner.Products.All(p => backend.ReadProduct(p).State == -1))
                {
                    backend.CleanupRegistration(owner);
                    continue;
                }
                var left = timeout - watch.Elapsed;
                if (left <= TimeSpan.Zero) return new(null, "INF 关联包装卸载预算已耗尽，计划保留。", PostcheckPassed: false);
                var ownerResult = await RunAsync(owner.Entry, stepId + "-owner-" + ownerIndex, left, progress, token);
                reboot |= ownerResult.RequiresReboot;
                diagnostics.Save(stepId + "-owner-" + ownerIndex + "-result.json", ownerResult);
                if (!ownerResult.Success || ownerResult.RequiresReboot)
                    return ownerResult with { Details = "INF 关联的已验证包装卸载未完成：" + owner.Entry.DisplayName + "\n" + ownerResult.Details };
            }
            Log.Info($"工具统一卸载计划：条目={entry.DisplayName}；来源={entry.UninstallKind}；MSI={string.Join(",", plan.Products)}；证据={pending}");
            for (var i = 0; i < plan.Products.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                backend.ValidateRegistration(plan);
                var product = backend.ReadProduct(plan.Products[i]);
                ValidateProduct(entry, product);
                if (product.State == -1) { Log.Info("统一卸载：产品已不存在，跳过：" + product.Code); continue; }
                var remaining = timeout - watch.Elapsed;
                if (remaining <= TimeSpan.Zero) return new(null, $"统一卸载总预算到期；本轮已确认移除 {removed} 个 MSI，未执行后续组件，计划保留。", RebootPending: reboot);
                progress?.Report($"工具统一卸载 [{i + 1}/{plan.Products.Length}]：{product.Name}");
                var result = await backend.UninstallAsync(plan, product.Code, Path.Combine(diagnostics.DirectoryPath, stepId + "-msi-" + i + ".log"), remaining, progress, token);
                diagnostics.Event("msi-step-result", new { Product = product.Code, result.ExitCode, result.OutcomeUnknown });
                reboot |= result.RequiresReboot;
                if (result.OutcomeUnknown || !result.Success && result.ExitCode != 1605)
                {
                    diagnostics.Save(stepId + "-msi-" + i + "-result.json", new { Product = product, Result = result });
                    return result with { Details = result.Details + $"\n本轮已确认移除 {removed} 个 MSI，后续组件已停止，未自动回滚；计划和原值证据保留。", RebootPending = reboot };
                }
                var after = backend.ReadProduct(product.Code);
                diagnostics.Event("msi-postcheck", new { Product = product.Code, after.State, Removed = after.State == -1,
                    DependencyBlocked = result.Details.Contains("WixDependencyCheck", StringComparison.OrdinalIgnoreCase) && result.Details.Contains("259", StringComparison.Ordinal) });
                if (after.State != -1) result = result with { PostcheckPassed = false, RebootPending = reboot,
                    Details = "MSI 已结束但产品仍登记：" + product.Code + "；未确认卸载成功，已停止后续步骤。\n" + result.Details };
                diagnostics.Save(stepId + "-msi-" + i + "-result.json", new { Product = product, After = after, Result = result });
                if (!result.PostcheckPassed) return result;
                if (result.ExitCode != 1605) removed++;
                if (result.RequiresReboot) return new(result.ExitCode,
                    $"本轮已确认移除 {removed} 个 MSI；安装器要求重启，已停止后续组件和包装登记清理。重启后可按保存计划继续。",
                    PostcheckPassed: false, RebootPending: true);
            }
            token.ThrowIfCancellationRequested();
            foreach (var product in plan.Products)
                if (backend.ReadProduct(product).State != -1) throw new IOException("清理包装登记前发现 MSI 重新登记，停止。");
            backend.CleanupRegistration(plan);
            var archive = Path.Combine(pendingRoot, "completed"); Directory.CreateDirectory(archive);
            File.Move(pending, Path.Combine(archive, id + "-" + Guid.NewGuid().ToString("N") + ".json"));
            return new(reboot ? 3010 : 0, $"工具统一 MSI 卸载及包装登记复检通过；本轮移除 {removed} 个 MSI，未直接删除 DLL、INF 文件或安装缓存。"
                + (plan.RetainedProducts.Length == 0 ? "" : $"已保留 {plan.RetainedProducts.Length} 个已升级的 MSI，仅清理旧包装登记。"),
                Skipped: removed == 0 && plan.Registrations.Length == 0);
        }
        catch (OperationCanceledException) { return new(null, $"已停止后续卸载；本轮已确认移除 {removed} 个 MSI，未自动回滚；计划保留。", PostcheckPassed: false, RebootPending: reboot); }
        catch (Exception ex) { diagnostics.Event("uninstall-failed", new { Entry = entry.KeyName, Removed = removed, Error = ex.ToString() }); Log.Error("工具统一卸载失败", ex); return new(-1, ex.Message + $"\n本轮已确认移除 {removed} 个 MSI，未自动回滚；不执行 EXE/INF 卸载器，证据保留。", PostcheckPassed: false, RebootPending: reboot); }
    }
}
