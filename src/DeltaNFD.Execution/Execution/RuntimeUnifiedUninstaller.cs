using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeltaNFD.Services;

internal sealed record RuntimeMsiProduct(string Code, int State, string Name = "", string Publisher = "", string Version = "", string Architecture = "");
internal sealed record RuntimeRegistrationSnapshot(string KeyPath, int View, string Content);
internal sealed record RuntimeUnifiedUninstallPlan(VcRedistEntry Entry, string[] Products, RuntimeRegistrationSnapshot[] Registrations)
{
    public string[] RetainedProducts { get; init; } = [];
}

internal interface IRuntimeUnifiedUninstallBackend
{
    RuntimeUnifiedUninstallPlan Resolve(VcRedistEntry entry);
    RuntimeMsiProduct ReadProduct(string product);
    void ValidateRegistration(RuntimeUnifiedUninstallPlan plan);
    Task<RuntimeStepResult> UninstallAsync(string product, string logPath, TimeSpan timeout, IProgress<string>? progress, CancellationToken token);
    void CleanupRegistration(RuntimeUnifiedUninstallPlan plan);
}

internal sealed class RuntimeUnifiedUninstaller(IRuntimeUnifiedUninstallBackend backend, RuntimeRepairDiagnostics diagnostics, string pendingRoot)
{
    internal static void ValidateProduct(VcRedistEntry entry, RuntimeMsiProduct product)
    {
        if (!Guid.TryParse(product.Code, out var id) || id == Guid.Empty) throw new InvalidDataException("MSI 产品码无效。");
        if (product.State == -1) return;
        if (product.State != 5) throw new InvalidDataException("MSI 安装状态无法确认：" + product.Code);
        if (!product.Publisher.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
            || !Regex.IsMatch(product.Name, @"^Microsoft Visual C\+\+ (?:2005|2008|2010|2012|2013|2015|2017|2019|2022|2026|v14)\b", RegexOptions.IgnoreCase)
            || !Regex.IsMatch(product.Name, @"\b(?:Redistributable|Minimum Runtime|Additional Runtime)\b", RegexOptions.IgnoreCase)
            || product.Name.Contains("Debug", StringComparison.OrdinalIgnoreCase)
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
        var pending = PendingPath(entry);
        RuntimeUnifiedUninstallPlan plan;
        if (File.Exists(pending))
        {
            if ((File.GetAttributes(pending) & FileAttributes.ReparsePoint) != 0 || new FileInfo(pending).Length > 1024 * 1024)
                throw new InvalidDataException("统一卸载记录无效，未覆盖。");
            plan = JsonSerializer.Deserialize<RuntimeUnifiedUninstallPlan>(File.ReadAllText(pending)) ?? throw new InvalidDataException("卸载记录为空。");
            if (JsonSerializer.Serialize(plan.Entry) != JsonSerializer.Serialize(entry)) throw new InvalidDataException("卸载记录条目已变化。");
        }
        else plan = backend.Resolve(entry);
        var all = plan.Products.Concat(plan.RetainedProducts).ToArray();
        if (all.Length is < 1 or > 4 || all.Distinct(StringComparer.OrdinalIgnoreCase).Count() != all.Length)
            throw new InvalidDataException("关联 MSI 数量异常或重复。");
        backend.ValidateRegistration(plan);
        foreach (var product in all) ValidateProduct(entry, backend.ReadProduct(product));
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
            diagnostics.Save(stepId + "-unified-plan.json", plan);
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
                var result = await backend.UninstallAsync(product.Code, Path.Combine(diagnostics.DirectoryPath, stepId + "-msi-" + i + ".log"), remaining, progress, token);
                reboot |= result.RequiresReboot;
                diagnostics.Save(stepId + "-msi-" + i + "-result.json", new { Product = product, Result = result });
                if (result.OutcomeUnknown || !result.Success && result.ExitCode != 1605)
                    return result with { Details = result.Details + $"\n本轮已确认移除 {removed} 个 MSI，后续组件已停止，未自动回滚；计划和原值证据保留。", RebootPending = reboot };
                var after = backend.ReadProduct(product.Code);
                if (after.State != -1) return result with { PostcheckPassed = false, RebootPending = reboot, Details = "MSI 已结束但产品仍登记：" + product.Code };
                if (result.ExitCode != 1605) removed++;
                if (result.ExitCode == 1641) return new(1641, "安装器发起重启，停止后续卸载和包装登记清理。", PostcheckPassed: false);
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
        catch (Exception ex) { Log.Error("工具统一卸载失败", ex); return new(-1, ex.Message + $"\n本轮已确认移除 {removed} 个 MSI，未自动回滚；不执行 EXE/INF 卸载器，证据保留。", PostcheckPassed: false, RebootPending: reboot); }
    }
}
