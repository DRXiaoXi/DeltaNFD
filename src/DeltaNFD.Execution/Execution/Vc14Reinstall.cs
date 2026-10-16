using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>单个安装/卸载步骤结果；3010/1641 视为需要重启。</summary>
public sealed record Vc14StepResult(int? ExitCode, bool Unknown = false, string Detail = "", bool PostcheckPassed = true)
{
    public bool Success => !Unknown && PostcheckPassed && ExitCode is 0 or 3010;
    public bool Reboot => ExitCode is 3010 or 1641;
}

public interface IVc14ReinstallEnvironment
{
    SafeInstallerPreparation? TemporaryInstaller() => null;
    void PrepareTemporaryInstaller(string fingerprint, CancellationToken token) => throw new NotSupportedException();
    OperationResult RestoreTemporaryInstaller() => OperationResult.Ok("");
    bool IsSafeMode => false;
    IReadOnlyList<Vc14RemovalPlanItem> Plan(Vc14Scan scan) => Vc14Reinstall.BuildRemovalPlan(scan);
    OperationResult Cleanup(Vc14Scan scan, IProgress<string>? progress, CancellationToken token) => Vc14ResidueCleanup.Run(scan, this, progress, token);
    Vc14Continuation? PendingInstall() => null;
    void SavePendingInstall(Vc14Continuation record) => throw new NotSupportedException("此执行环境不支持分阶段恢复记录。");
    void CompletePendingInstall() { }
    Vc14Scan Scan();
    string? Blocker();
    void VerifyBaselines();
    void VerifyInstalled();
    string DiagnosticsDirectory { get; }
    void Trace(string stage, object value);
    Task<Vc14StepResult> RemoveAsync(Vc14RemovalPlanItem item, IProgress<string>? progress, CancellationToken token);
    Task<Vc14StepResult> InstallAsync(string arch, IProgress<string>? progress, CancellationToken token);
}

/// <summary>
/// 全新 14.x 全量重装：按用户 2026-10-15 授权，卸载所有检测到的 x86/x64 C++ 14.x 登记
/// （含未知包装，按其登记的卸载命令执行），再安装内置 2015–2022 14.42.34433 基线并复检。
/// 颜色规则是项目游戏兼容性策略；重装不代表已验证帧率改善。
/// </summary>
public static partial class Vc14Reinstall
{
    public const string BaselineVersion = "14.42.34433.0";
    internal static readonly IReadOnlyDictionary<string, string> BaselineHashes = new Dictionary<string, string>
    {
        ["x64"] = "1821577409C35B2B9505AC833E246376CC68A8262972100444010B57226F0940",
        ["x86"] = "DD1A8BE03398367745A87A5E35BEBDAB00FDAD080CF42AF0C3F20802D08C25D4",
    };
    // Microsoft 14.42.34433 基线 Burn 包装码；已登记时安装阶段改用原包 /repair 补回组件。
    internal static readonly IReadOnlyDictionary<string, string> BaselineBundles = new Dictionary<string, string>
    {
        ["x64"] = "{804e7d66-ccc2-4c12-84ba-476da31d103d}",
        ["x86"] = "{e7802eac-3305-4da0-9378-e55d1ed05518}",
    };

    /// <summary>枚举全部 14.x 登记（MSI 产品、Burn 包装、仅登记卸载命令的未知条目），生成卸载计划。</summary>
    internal static IReadOnlyList<Vc14RemovalPlanItem> BuildRemovalPlan(Vc14Scan scan, bool verifyDependencies = true)
    {
        var plan = new List<Vc14RemovalPlanItem>();
        // 去重按 Hive+View+Key 三段：不同位置的相同键名（HKLM/HKCU、32/64 视图）是不同登记，
        // 只按键名去重会漏掉后面的条目（其卸载命令或版本可能不同）。
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var product in scan.Products)
        {
            if (!Vc14BetaUninstall.InScope(product.Name, product.Version, product.Architecture)) continue;
            var dependency = verifyDependencies ? Vc14DependencyPolicy.Read(product) : null;
            plan.Add(new(product.Code, product.Architecture, product.Name, product.Version, "msi", product.Code,
                "msiexec /x " + product.Code + (dependency?.Clients.Count > 0 ? "；仅放行核验过的自身包装引用：" + string.Join("；", dependency.Clients) : ""),
                "已核验 MSI 登记：" + product.Name + " · " + product.Version,
                product.Role, product.UpgradeCode, Command: dependency?.Argument ?? "", DependencyFingerprint: dependency?.Fingerprint ?? ""));
            covered.Add("msi:" + product.Code);
        }
        foreach (var (hive, view) in Vc14Catalog.RegistryViews())
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var root = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            foreach (var keyName in root?.GetSubKeyNames() ?? [])
            {
                var locationKey = hive + ":" + view + ":" + keyName;
                if (covered.Contains(locationKey)) continue;
                using var key = root!.OpenSubKey(keyName);
                var name = key?.GetValue("DisplayName") as string ?? "";
                var version = Vc14Catalog.RegistrationVersion(key);
                if (!Vc14Diagnostics.Relevant(name, version)) continue;
                covered.Add(locationKey);
                // 已核验 MSI 产品本身的登记键跳过（MSI 上下文登记随卸载移除，不重复处理）。
                if (scan.Products.Any(p => p.Code.Equals(keyName, StringComparison.OrdinalIgnoreCase))) continue;
                if (Vc14Catalog.IsAppxPackage(name))
                {
                    plan.Add(new(keyName, "arm64", name, version, "unknown", keyName, "无法处理",
                        "系统组件包（Appx），由 Windows 管理，不卸载。"));
                    continue;
                }
                var architecture = name.Contains("arm64", StringComparison.OrdinalIgnoreCase) ? "arm64"
                    : Regex.IsMatch(name, @"\bx64\b", RegexOptions.IgnoreCase) ? "x64" : "x86";
                if (architecture == "arm64")
                {
                    plan.Add(new(keyName, architecture, name, version, "unknown", keyName, "无法处理",
                        "ARM64 组件不在本次 x86/x64 重装范围，保留并说明。"));
                    continue;
                }
                if (!Vc14BetaUninstall.InScope(name, version, architecture))
                {
                    plan.Add(new(keyName, architecture, name, version, "unknown", keyName, "保留不处理",
                        "未确认属于 14.x release 运行库；仅评估已授权的旧 2026 残留，不执行卸载。"));
                    continue;
                }
                if (keyName.Equals("vcredist14_" + architecture, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (Vc14ResidueCleanup.DescribeLegacyInf(hive.ToString(), view.ToString(), keyName, key!, name, version)
                            is not { Kind: "inf-legacy" }) throw new IOException("旧 INF 包装身份未确认。");
                        var inf = Vc14BetaUninstall.LegacyInfPath(architecture);
                        Vc14BetaUninstall.ValidateInf(File.ReadAllText(inf), architecture, Vc14Catalog.ReadAny);
                        plan.Add(new(keyName, architecture, name, version, "inf", Vc14BetaUninstall.LegacyInfCommand(architecture),
                            "Beta0.9：cmd /c 执行旧 INF 卸载节", "固定模板与目标 14.x 产品码已核验；执行前备份登记及 INF/PNF。",
                            Hive: hive.ToString(), View: view.ToString()));
                    }
                    catch (Exception ex)
                    {
                        plan.Add(new(keyName, architecture, name, version, "unknown", keyName, "残留清理评估",
                            "未执行旧 INF 卸载节：" + ex.Message + "；卸载后保留备份清理路径。"));
                    }
                    continue;
                }
                var cache = key?.GetValue("BundleCachePath") as string;
                var uninstall = Vc14BetaUninstall.PreferredCommand(key?.GetValue("QuietUninstallString") as string ?? "",
                    key?.GetValue("UninstallString") as string ?? "");
                if (string.IsNullOrWhiteSpace(cache) && !string.IsNullOrWhiteSpace(uninstall)
                    && !Vc14BetaUninstall.IsMsiCommand(uninstall) && !Vc14BetaUninstall.IsUnsupportedScript(uninstall))
                    cache = Vc14Catalog.ResolveUninstallExecutable(uninstall); // Still requires Microsoft/version/architecture verification below.
                if (Vc14BetaUninstall.IsMsiCommand(uninstall))
                {
                    try
                    {
                        var target = Vc14BetaUninstall.MsiTarget(uninstall, Vc14Catalog.ReadAny)
                            ?? throw new IOException("登记的 MSI 产品已不安装；转入残留清理评估。");
                        if (!plan.Any(p => p.Kind == "msi" && p.Key.Equals(target.Code, StringComparison.OrdinalIgnoreCase)))
                        {
                            var dependency = verifyDependencies ? Vc14DependencyPolicy.Read(target) : null;
                            plan.Add(new(target.Code, target.Architecture, target.Name, target.Version, "msi", target.Code,
                                "Beta0.9：msiexec /x 产品码", "登记命令中的产品码已重新核验为 14.x。", target.Role, target.UpgradeCode,
                                Command: dependency?.Argument ?? "", DependencyFingerprint: dependency?.Fingerprint ?? ""));
                        }
                    }
                    catch (Exception ex) { plan.Add(new(keyName, architecture, name, version, "unknown", keyName, "保留不处理", ex.Message)); }
                    continue;
                }
                if (Guid.TryParse(keyName, out _) && Vc14PackageTrust.Verify(cache, version, architecture))
                {
                    plan.Add(new(keyName, architecture, name, version, "bundle", cache!,
                        "Beta0.9：cmd /c 执行包装卸载命令", "已核验微软签名包装缓存：" + cache,
                        Command: uninstall, Hive: hive.ToString(), View: view.ToString(),
                        CacheSha256: Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(cache!)))));
                    continue;
                }
                if (Guid.TryParse(keyName, out var code) && BaselineBundles.Values.Any(b => b.Equals(keyName, StringComparison.OrdinalIgnoreCase)))
                {
                    plan.Add(new(keyName, architecture, name, version, "baseline-bundle", code.ToString("B"),
                        "安装阶段由原包 /repair 处理", "基线 14.42 包装登记仍在但缓存缺失或未通过核验；不先卸载，安装阶段用原包修复。"));
                    continue;
                }
                // 卸载命令的可执行文件不可用时按命令执行必然失败，转入清理阶段评估。
                // 与执行阶段共用同一解析（含带引号路径与相对名按系统目录/PATH 解析）。
                var uninstallExecutableExists = !string.IsNullOrWhiteSpace(uninstall)
                    && Vc14Catalog.UninstallExecutableAvailable(uninstall);
                if (!string.IsNullOrWhiteSpace(uninstall) && uninstallExecutableExists
                    && !Vc14BetaUninstall.IsUnsupportedScript(uninstall))
                    plan.Add(new(keyName, architecture, name, string.IsNullOrWhiteSpace(version) ? "未知" : version, "registry",
                        uninstall, "Beta0.9：cmd /c 执行登记命令：" + uninstall,
                        "未知包装：无缓存可核验签名。按用户 2026-10-15 授权（严格全部，含未知）执行其登记的静默卸载命令。",
                        Command: uninstall, Hive: hive.ToString(), View: view.ToString()));
                else
                    plan.Add(new(keyName, architecture, name, string.IsNullOrWhiteSpace(version) ? "未知" : version, "unknown",
                        keyName, "无法处理", "无法处理：缺少可执行的卸载命令（或命令指向的程序不存在），且身份未经核验；卸载完成后将由残留清理阶段评估。"));
            }
        }
        return PreferVerifiedBundles(plan);
    }

    internal static string Fingerprint(IReadOnlyList<Vc14RemovalPlanItem> plan) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(plan.Select(p =>
            new { p.Key, p.Kind, p.Identity, p.Version, p.Role, p.UpgradeCode, p.Name, p.Architecture, p.Command, p.Hive, p.View, p.DependencyFingerprint,
                p.CacheSha256, p.BundleChildren }).ToArray()))));

    public static Vc14ReinstallPreview Preview(IVc14ReinstallEnvironment environment, Vc14RepairMode mode = Vc14RepairMode.Full)
    {
        var scan = environment.Scan();
        var blocker = environment.Blocker();
        if (!Enum.IsDefined(mode)) blocker ??= "未知的 14.x 修复阶段。";
        if (environment.IsSafeMode && mode != Vc14RepairMode.UninstallOnly)
            blocker ??= "安全模式仅支持单独卸载阶段；安装和完整重装请返回正常模式执行。";
        try { environment.VerifyBaselines(); } catch (Exception ex) { blocker ??= ex.Message; }
        if (blocker is null && scan.Issues.Count > 0)
            blocker = "14.x 检测存在问题，暂不能重装：\n" + string.Join("\n", scan.Issues);
        IReadOnlyList<Vc14RemovalPlanItem> plan;
        Vc14Continuation? pending;
        try
        {
            pending = environment.PendingInstall();
            plan = mode == Vc14RepairMode.InstallOnly ? [] : environment.Plan(scan);
            if (mode == Vc14RepairMode.InstallOnly)
            {
                if (pending is null) blocker ??= "没有待安装记录，请先预览完整重装或执行单独卸载阶段。";
                if (scan.Products.Any(p => !Vc14Catalog.SameVersion(p.Version, BaselineVersion)))
                    blocker ??= "旧 14.x 组件仍在，不能跳过卸载直接安装。请核对或继续卸载阶段。";
            }
        }
        catch (Exception ex) { return new(scan, [], "", false, "卸载前依赖核验未通过：" + ex.Message); }
        if (blocker is null && plan.Count == 0 && mode != Vc14RepairMode.InstallOnly)
            blocker = "未发现任何 C++ 14.x 登记，无需重装。";
        var temporary = environment.TemporaryInstaller();
        if (temporary is { Required: true, CanEnable: false }) blocker ??= temporary.Detail;
        return new(scan, plan, StageFingerprint(plan, mode, pending, scan, temporary), blocker is null, blocker ?? "") { TemporaryInstaller = temporary };
    }

    public static async Task<OperationResult> RunAsync(IVc14ReinstallEnvironment environment, string fingerprint, IProgress<string>? progress, CancellationToken token,
        Vc14RepairMode mode = Vc14RepairMode.Full)
    {
        var attempted = false;
        var result = await RunCoreAsync(environment, fingerprint, progress, token, mode, () => attempted = true);
        if (!attempted) return result;
        OperationResult restored;
        try { restored = environment.RestoreTemporaryInstaller(); }
        catch (Exception ex) { restored = new() { Message = "临时 Installer 恢复失败，记录保留：" + ex.Message, OutcomeUnknown = true }; }
        return new OperationResult
        {
            Success = result.Success && restored.Success, IsPartial = result.IsPartial || !restored.Success,
            OutcomeUnknown = result.OutcomeUnknown || !restored.Success, RequiresReboot = result.RequiresReboot,
            DiagnosticsDirectory = result.DiagnosticsDirectory, Message = result.Message + "\n" + restored.Message,
        };
    }

    private static async Task<OperationResult> RunCoreAsync(IVc14ReinstallEnvironment environment, string fingerprint, IProgress<string>? progress, CancellationToken token,
        Vc14RepairMode mode, Action preparing)
    {
        var changed = false; var attempted = false; var reboot = false; var logs = environment.DiagnosticsDirectory;
        OperationResult Fail(string text, bool unknown = false) => new()
        {
            Success = false, IsPartial = changed && !unknown, OutcomeUnknown = unknown, RequiresReboot = reboot,
            DiagnosticsDirectory = logs,
            Message = text + (changed ? "\n已尝试执行运行库操作，实际变更以日志和复检为准；不会继续后续步骤或强杀安装器。"
                : attempted ? "\n已调用安装器，但安装服务未能执行事务；未确认卸载或安装变更。" : "\n未执行卸载或安装。"),
        };
        try
        {
            token.ThrowIfCancellationRequested();
            var preview = Preview(environment, mode);
            if (!preview.CanRepair) return Fail(preview.Blocker);
            if (fingerprint != preview.Fingerprint) return Fail("14.x 登记清单已变化，请重新预览确认。");
            environment.Trace("confirmed-plan", new { Count = preview.RemovalPlan.Count, preview.Fingerprint });
            if (preview.TemporaryInstaller is { Required: true } temporary)
            {
                if (mode != Vc14RepairMode.UninstallOnly) return Fail("临时 Installer 仅用于安全模式单独卸载。");
                preparing();
                environment.PrepareTemporaryInstaller(temporary.Fingerprint, token);
            }
            if (mode != Vc14RepairMode.InstallOnly)
            {
                var pending = environment.PendingInstall() ?? new(Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow);
                environment.SavePendingInstall(pending with { UninstallVerified = false });
            }

            // 阶段一：逐项卸载全部可移除的 14.x 登记（含未知包装）。
            var handledInf = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in preview.RemovalPlan)
            {
                token.ThrowIfCancellationRequested();
                if (!item.CanRemove) continue; // unknown / baseline-bundle / arm64：保留并说明
                if (item.Kind == "inf" && !handledInf.Add(item.Key)) continue; // 多处登记共用同一个 INF，只执行一次
                var current = environment.Scan();
                if (current.Issues.Count > 0) return Fail("检测出现问题，已停止：" + string.Join("\n", current.Issues));
                if (environment.Blocker() is { } blocked) return Fail(blocked);
                progress?.Report("卸载 14.x：" + item.Name + "（" + item.Architecture + "）");
                attempted = true;
                var step = await environment.RemoveAsync(item, progress, token);
                changed |= step.ExitCode is not (1601 or 1652);
                reboot |= step.Reboot;
                environment.Trace("remove-result", new { item.Key, item.Kind, step.ExitCode, step.Unknown, step.PostcheckPassed, step.Detail });
                if (!step.Success || step.Reboot)
                    return Fail("14.x 卸载未完成或需要重启：" + item.Name + "\n" + step.Detail, step.Unknown);
                if (item.BundleChildren?.Count > 0)
                {
                    var checkedBundle = environment.Scan();
                    if (checkedBundle.Issues.Count > 0 || item.BundleChildren.Any(c => checkedBundle.Products.Any(p => p.Code == c.Product.Code)))
                        return Fail("微软包装已退出，但其 MSI 子组件仍在或状态无法确认，未继续后续步骤：" + item.Name);
                }
                if (item.Kind == "msi")
                {
                    var afterRemoval = environment.Scan();
                    environment.Trace("remove-postcheck", afterRemoval);
                    if (afterRemoval.Issues.Count > 0 || afterRemoval.Products.Any(p => p.Code.Equals(item.Key, StringComparison.OrdinalIgnoreCase)))
                        return Fail("卸载进程返回成功，但 MSI 组件仍在或状态无法确认，已停止后续卸载和安装：" + item.Name);
                }
            }

            // 阶段二：确认无 14.x MSI 组件残留后，先受控清理残留登记再安装基线
            // （旧版工具留下的 v14/2026 包装登记会阻止基线安装，即 MSI 1638 的成因之一）。
            var cleared = environment.Scan();
            if (cleared.Issues.Count > 0) return Fail("卸载后扫描未确认，未启动安装：" + string.Join("；", cleared.Issues));
            if (cleared.Products.Count > 0 && mode != Vc14RepairMode.InstallOnly)
                return Fail("仍有 14.x MSI 组件未卸载完成，未启动安装：" + string.Join("; ", cleared.Products.Select(p => p.Name)));
            var residue = mode == Vc14RepairMode.InstallOnly && cleared.Products.Count > 0
                ? OperationResult.Ok("基线部分已安装，保留登记并以原包修复；无可清理项。")
                : environment.Cleanup(cleared, progress, token);
            environment.Trace("residue-cleanup-result", new { residue.Success, residue.Message });
            if (!residue.Success && !residue.Message.Contains("无可清理"))
                return Fail("残留登记清理未完成，已停止（不会带着阻止安装的残留登记继续装基线）：\n" + residue.Message);
            if (mode == Vc14RepairMode.UninstallOnly)
            {
                var pending = environment.PendingInstall() ?? throw new IOException("卸载阶段的待安装记录缺失。");
                environment.SavePendingInstall(pending with { UninstallVerified = true });
                return new OperationResult { Success = true, DiagnosticsDirectory = logs,
                    Message = "14.x 卸载阶段已复检完成，尚未安装 14.42，不代表完整修复完成。\n待安装记录已保留。请手动重启回正常模式，打开运行库页，点击“继续安装 14.42”并重新确认；不会自动重启或续跑。" };
            }
            environment.VerifyBaselines();
            foreach (var arch in new[] { "x64", "x86" })
            {
                token.ThrowIfCancellationRequested();
                if (environment.Blocker() is { } blocked) return Fail(blocked);
                progress?.Report("安装 2015–2022 " + arch + " · " + BaselineVersion);
                changed = true;
                var step = await environment.InstallAsync(arch, progress, token);
                reboot |= step.Reboot;
                environment.Trace("install-result", new { arch, step.ExitCode, step.Unknown, step.Detail });
                if (step.ExitCode == 1638)
                    return Fail(arch + " 的 14.42 安装被其他已登记版本阻止（1638），可能仍有组件或包装登记未处理，未继续后续安装。\n" + step.Detail);
                if (!step.Success || step.Reboot)
                    return Fail(arch + " 基线安装未完成或需要重启。\n" + step.Detail, step.Unknown);
                var installed = environment.Scan();
                environment.Trace("install-postcheck", new { arch, Products = installed.Products.Count, Issues = installed.Issues });
                if (installed.Issues.Count > 0 || installed.Products.Count(p => p.Architecture == arch) != 2
                    || installed.Products.Any(p => p.Architecture == arch && !Vc14Catalog.SameVersion(p.Version, BaselineVersion)))
                    return Fail("安装器已返回成功，但 " + arch + " 的 14.42 组件未确认装齐，已停止后续安装。"
                        + "若旧包装登记仍存在，安装器可能跳过组件，请核对日志后重试。");
            }

            // 阶段三：最终复检（MSI 登记与系统 DLL 实际版本）。
            // MSI 登记齐全但 DLL 版本未对齐时（更高版本文件曾由范围外产品持有，MSI 不降级覆盖文件），
            // 对失败架构执行原包 /repair 强制恢复文件版本，再复检一次。
            var after = environment.Scan();
            var complete = after.Issues.Count == 0 && after.Products.Count == 4 && Vc14Catalog.Families.All(f =>
                after.Products.Count(p => p.Architecture == f.Arch && p.Role == f.Role && Vc14Catalog.SameVersion(p.Version, BaselineVersion)) == 1);
            if (!complete) return Fail("安装后未确认 x64/x86 的四个 14.42 基线组件，不能报告修复成功。");
            try { environment.VerifyInstalled(); }
            catch (Exception dllMismatch)
            {
                progress?.Report("系统 DLL 版本未对齐，执行原包修复以恢复文件版本…");
                changed = true;
                foreach (var arch in new[] { "x64", "x86" })
                {
                    token.ThrowIfCancellationRequested();
                    if (environment.Blocker() is { } blocked) return Fail(blocked);
                    var repair = await environment.InstallAsync(arch, progress, token); // 已登记包装时即 /repair
                    reboot |= repair.Reboot;
                    environment.Trace("dll-align-repair", new { arch, repair.ExitCode, repair.Unknown, repair.Detail });
                    if (!repair.Success || repair.Reboot)
                        return Fail(arch + " 文件版本对齐修复未完成或需要重启。\n" + repair.Detail, repair.Unknown);
                }
                try { environment.VerifyInstalled(); }
                catch
                {
                    return Fail("MSI 登记已是 14.42，但系统 DLL 实际版本仍未对齐（更高版本文件可能被其他产品持有）。"
                        + "原包 /repair 后仍未恢复，不能报告修复成功。\n" + dllMismatch.Message);
                }
                after = environment.Scan();
            }
            environment.Trace("verified-after", after);
            environment.CompletePendingInstall();

            var skipped = preview.RemovalPlan.Where(p => !p.CanRemove).ToArray();
            return new OperationResult { Success = true, DiagnosticsDirectory = logs,
                Message = "已卸载全部检测到的可移除 C++ 14.x 登记，并安装/复检 2015–2022 x64/x86 " + BaselineVersion + "。"
                    + (residue.Success && !residue.Message.Contains("无可清理") ? "\n" + residue.Message : "")
                    + (skipped.Length > 0 ? "\n以下登记在重装阶段保留（未构成安装阻碍）：\n" + string.Join("\n", skipped.Select(p => p.Name + " · " + p.Detail)) : "")
                    + (after.HasAbnormal || after.HasWarning ? "\n仍有其他包装登记或兼容性提醒，请核对详情。" : "")
                    + "\n未处理 2005–2013、SDK/Debug、ARM64 或 DirectX；此结果不代表已验证游戏帧率改善。" };
        }
        catch (OperationCanceledException) { return Fail("已取消等待或后续步骤。", changed); }
        catch (Exception ex) { return Fail("14.x 全量重装已停止：" + ex.Message, changed); }
    }
}

internal sealed class Vc14ReinstallEnvironment(bool allowTemporaryInstaller = false) : IVc14ReinstallEnvironment
{
    private bool _temporaryAttempted;
    private SafeInstallerEngine SafeInstaller => new(new SafeInstallerWindowsPlatform(), new SafeInstallerFileStore(root));
    public SafeInstallerPreparation? TemporaryInstaller() => allowTemporaryInstaller && IsSafeMode && !_temporaryAttempted ? SafeInstaller.Preview() : null;
    public void PrepareTemporaryInstaller(string fingerprint, CancellationToken token)
    {
        if (!allowTemporaryInstaller || !IsSafeMode) throw new IOException("未授权临时启用 Installer。");
        _temporaryAttempted = true;
        SafeInstaller.Enable(fingerprint, confirmed: true, token);
    }
    public OperationResult RestoreTemporaryInstaller() => SafeInstaller.Restore();
    public bool IsSafeMode => Vc14InstallerReadiness.IsSafeMode;
    public Vc14Continuation? PendingInstall() => new Vc14ContinuationStore(root).Read();
    public void SavePendingInstall(Vc14Continuation record) => new Vc14ContinuationStore(root).Save(record);
    public void CompletePendingInstall() => new Vc14ContinuationStore(root).Complete();
    private readonly string root = AppDataPaths.Root;
    private readonly string logs = Path.Combine(Log.LogDirectory, "vc14-reinstall-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
    public string DiagnosticsDirectory => logs;
    public void Trace(string stage, object value)
    {
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, stage + "-" + Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(value));
    }
    public Vc14Scan Scan() => Vc14Catalog.Scan();
    public string? Blocker()
    {
        if (!ElevationHelper.IsElevated) return ElevationHelper.NotElevatedMessage;
        if (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
            return "此修复只支持 x64 Windows 的 x86/x64 C++ 组件。";
        if (!_temporaryAttempted && (File.Exists(Path.Combine(root, "safe-installer-active.json")) || Directory.Exists(Path.Combine(root, "safe-installer-active.json"))))
            return "上次临时 Installer 设置仍待恢复，请先点击“恢复临时 Installer 设置”。";
        if (Vc14InstallerReadiness.Check() is { } installer
            && TemporaryInstaller() is not { Required: true, CanEnable: true }) return installer;
        var pending = new RuntimeInstallationReview(root).InspectPending(RuntimeInstallationReview.InstallerBusy(), out var ended);
        if (pending is not null || ended) return pending ?? "上次安装结果待核对，请先核对未完成操作。";
        if (RuntimeInstallationReview.InstallerBusy()) return "Windows Installer 仍在执行事务，请结束后重试。";
        var guard = new RuntimeGuardService().GetStatusAsync().GetAwaiter().GetResult();
        if (guard.IfeoCount > 0 || guard.Ue4PrereqDenied || guard.IfeoRestorePending || guard.Ue4RestorePending || guard.GuardIssue.Length > 0)
            return "请先关闭运行库防护并核对安装器拦截；不会自动关闭防护。";
        return null;
    }
    public void VerifyBaselines()
    {
        foreach (var item in Vc14Reinstall.BaselineHashes)
        {
            var path = Baseline(item.Key);
            using var stream = File.OpenRead(path);
            if (Convert.ToHexString(SHA256.HashData(stream)) != item.Value
                || FileVersionInfo.GetVersionInfo(path).FileVersion != Vc14Reinstall.BaselineVersion)
                throw new IOException("内置 14.42 原包校验失败：" + item.Key);
        }
    }
    public void VerifyInstalled()
    {
        foreach (var arch in new[] { "x64", "x86" })
        {
            var system = Environment.GetFolderPath(arch == "x64" ? Environment.SpecialFolder.System : Environment.SpecialFolder.SystemX86);
            foreach (var dll in arch == "x64" ? new[] { "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" } : new[] { "msvcp140.dll", "vcruntime140.dll" })
            {
                var file = Path.Combine(system, dll);
                var version = FileVersionInfo.GetVersionInfo(file);
                if (version.FileMajorPart != 14 || version.FileMinorPart != 42 || version.FileBuildPart != 34433)
                    throw new IOException(arch + " " + dll + " 实际版本未确认是 14.42.34433，不能报告修复成功。");
            }
        }
    }
    internal static string Baseline(string arch) => Path.Combine(BackendEnvironment.AssetsRoot, "Vc14Baseline14.42", "vc_redist." + arch + ".exe");

    public async Task<Vc14StepResult> RemoveAsync(Vc14RemovalPlanItem item, IProgress<string>? progress, CancellationToken token)
    {
        if (!Vc14BetaUninstall.InScope(item.Name, item.Version, item.Architecture))
            throw new IOException("卸载对象不属于 x86/x64 C++ 14.x，已拒绝：" + item.Name);
        if (item.Kind == "msi")
        {
            // 动态发现的家族（如 2026 产品线新升级码）不在固定 Families 内；
            // 卸载前重读产品信息并按名称/版本/架构/升级码复核身份即可，不要求固定家族。
            if (!Guid.TryParse(item.Key, out _)) throw new IOException("待卸载 MSI 产品码无效：" + item.Name);
            var current = Vc14Catalog.ReadAny(item.Key);
            if (current is null) return Vc14BetaUninstall.MsiResult(new(1605, false, "目标 MSI 已不安装。"));
            if (!current.Architecture.Equals(item.Architecture, StringComparison.OrdinalIgnoreCase)
                || !current.UpgradeCode.Equals(item.UpgradeCode, StringComparison.OrdinalIgnoreCase)
                || !Vc14Catalog.SameVersion(current.Version, item.Version))
                throw new IOException("待卸载 MSI 身份已变化：" + item.Name);
            var dependency = Vc14DependencyPolicy.Read(current);
            if (string.IsNullOrEmpty(item.DependencyFingerprint) || dependency.Fingerprint != item.DependencyFingerprint || dependency.Argument != item.Command)
                throw new IOException("MSI 共享依赖或包装身份已变化，请重新预览；未执行卸载。");
            Trace("verified-uninstall-dependencies", dependency);
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe"))
                { UseShellExecute = false, CreateNoWindow = true };
            var installerLog = NewLog(item.Key.Trim('{', '}'));
            foreach (var argument in new[] { "/x", item.Key, "/qn", "/norestart", "/L*V", installerLog })
                info.ArgumentList.Add(argument);
            if (dependency.Clients.Count > 0) info.ArgumentList.Add(dependency.Argument);
            Trace("verified-uninstall-command", new { current, Arguments = info.ArgumentList.ToArray() });
            var result = Vc14BetaUninstall.MsiResult(await RunProcessAsync(info, progress, token, TimeSpan.FromMinutes(5)));
            var verified = Vc14DependencyPolicy.VerifyRemoval(result, () => Vc14Catalog.ReadAny(item.Key), () => Vc14DependencyPolicy.ReadInstallerLog(installerLog));
            Trace("msi-removal-postcheck", verified);
            return verified;
        }
        if (item.Kind == "bundle")
        {
            if (!Vc14PackageTrust.Verify(item.Identity, item.Version, item.Architecture))
                throw new IOException("包装缓存身份已变化，未执行卸载。");
            if (string.IsNullOrEmpty(item.CacheSha256)
                || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(item.Identity))) != item.CacheSha256)
                throw new IOException("包装缓存字节已变化，请重新预览。");
            RegisteredBundleCommand(item); // Recheck the exact registration before using the trusted cache directly.
            foreach (var child in item.BundleChildren ?? [])
            {
                if (Vc14Catalog.ReadAny(child.Product.Code) is not { } current || current != child.Product)
                    throw new IOException("包装的 MSI 子组件身份已变化，请重新预览。");
                var dependency = Vc14DependencyPolicy.Read(current);
                if (dependency.Fingerprint != child.DependencyFingerprint || dependency.Argument != child.DependencyArgument)
                    throw new IOException("包装子组件的共享依赖已变化，请重新预览。");
            }
            var info = Vc14Reinstall.BuildBundleUninstall(item.Identity, NewLog("bundle" + item.Key.Trim('{', '}')));
            Trace("verified-bundle-uninstall", new { item.Key, item.Identity, item.CacheSha256, item.BundleChildren, Arguments = info.ArgumentList.ToArray() });
            var result = await RunProcessAsync(info, progress, token, TimeSpan.FromMinutes(10));
            if (!result.Success || result.Reboot) return result;
            using var hive = RegistryKey.OpenBaseKey(Enum.Parse<RegistryHive>(item.Hive), Enum.Parse<RegistryView>(item.View));
            using var remaining = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + item.Key);
            if (remaining is not null) return result with { PostcheckPassed = false, Detail = result.Detail + "；微软包装登记仍在，未确认卸载成功。" };
            if ((item.BundleChildren ?? []).Any(c => Vc14Catalog.ReadAny(c.Product.Code) is not null))
                return result with { PostcheckPassed = false, Detail = result.Detail + "；微软包装的 MSI 子组件仍在，未确认卸载成功。" };
            return result;
        }
        if (item.Kind == "registry")
        {
            // 登记的卸载命令按原样执行：不猜测参数、不静默改写、不强杀。
            // 相对名（如 MsiExec.exe）解析为完整路径再启动，活动记录须存完整路径供核对入口校验。
            if (Vc14BetaUninstall.IsUnsupportedScript(item.Identity) || Vc14BetaUninstall.IsMsiCommand(item.Identity))
                throw new IOException("该登记命令不属于已确认的 14.x EXE 卸载路径，请重新预览。");
            if (RegisteredBundleCommand(item) != item.Identity) throw new IOException("登记卸载命令已变化，请重新预览。");
            var info = Vc14BetaUninstall.Shell(Vc14BetaUninstall.ResolveCommand(item.Identity));
            return await RunProcessAsync(info, progress, token, TimeSpan.FromMinutes(10));
        }
        if (item.Kind == "inf")
        {
            var inf = Vc14BetaUninstall.LegacyInfPath(item.Architecture);
            if (!File.Exists(inf)) return new(0, false, "共用 INF 已不在；剩余登记将在残留清理阶段处理。");
            Vc14BetaUninstall.ValidateInf(File.ReadAllText(inf), item.Architecture, Vc14Catalog.ReadAny);
            Vc14ResidueCleanup.BackupBeforeInfUninstall(item.Key, logs);
            Vc14BetaUninstall.ValidateInf(File.ReadAllText(inf), item.Architecture, Vc14Catalog.ReadAny);
            return await RunProcessAsync(Vc14BetaUninstall.Shell(Vc14BetaUninstall.LegacyInfCommand(item.Architecture),
                Path.GetDirectoryName(inf)), progress, token, TimeSpan.FromMinutes(10));
        }
        throw new IOException("不支持的卸载类型：" + item.Kind);
    }

    private static string RegisteredBundleCommand(Vc14RemovalPlanItem item)
    {
        using var hive = RegistryKey.OpenBaseKey(Enum.Parse<RegistryHive>(item.Hive), Enum.Parse<RegistryView>(item.View));
        using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + item.Key)
            ?? throw new IOException("登记已不存在，请重新预览。");
        var name = key.GetValue("DisplayName") as string ?? "";
        var version = Vc14Catalog.RegistrationVersion(key);
        if (name != item.Name || (string.IsNullOrWhiteSpace(version) ? "未知" : version) != item.Version)
            throw new IOException("包装登记身份已变化，请重新预览。");
        var result = Vc14BetaUninstall.PreferredCommand(key.GetValue("QuietUninstallString") as string ?? "", key.GetValue("UninstallString") as string ?? "");
        if (result != item.Command) throw new IOException("包装登记命令已变化，请重新预览。");
        if (result.Length > 0 && (Vc14BetaUninstall.IsMsiCommand(result) || Vc14BetaUninstall.IsUnsupportedScript(result)))
            throw new IOException("包装登记命令不是可确认的 EXE 卸载命令，未执行。");
        return result;
    }

    public Task<Vc14StepResult> InstallAsync(string arch, IProgress<string>? progress, CancellationToken token)
    {
        var action = BaselineBundleRegistered(arch) ? "/repair" : "/install";
        Trace("verified-install-command", new { arch, action });
        progress?.Report(arch + (action == "/repair" ? "：旧 14.42 包装登记仍在，执行原包修复以补回组件。" : "：执行原包安装。"));
        var info = new ProcessStartInfo(Baseline(arch)) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { action, "/quiet", "/norestart", "/log", NewLog("install-" + arch) })
            info.ArgumentList.Add(argument);
        return RunProcessAsync(info, progress, token, TimeSpan.FromMinutes(10));
    }

    private static bool BaselineBundleRegistered(string arch)
    {
        if (!Vc14Reinstall.BaselineBundles.TryGetValue(arch, out var id)) throw new ArgumentException("不支持的原包架构。");
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + id);
            if (key is null) continue;
            var version = key.GetValue("BundleVersion") as string ?? "";
            if (!Vc14Catalog.SameVersion(version, Vc14Reinstall.BaselineVersion))
                throw new IOException("已登记的 14.42 包装版本异常：" + arch + " · " + version);
            return true;
        }
        return false;
    }

    private string NewLog(string label)
    {
        Directory.CreateDirectory(logs);
        var safe = string.Concat(label.TakeWhile(char.IsLetterOrDigit));
        return Path.Combine(logs, (safe.Length > 0 ? safe : "installer") + ".log");
    }

    /// <summary>启动安装/卸载进程并等待退出；全程写入活动事务记录，崩溃后可由核对入口复核，不强杀。</summary>
    private async Task<Vc14StepResult> RunProcessAsync(ProcessStartInfo info, IProgress<string>? progress, CancellationToken token, TimeSpan waitLimit)
    {
        token.ThrowIfCancellationRequested();
        var active = Path.Combine(root, "runtime-repair-active.json");
        Directory.CreateDirectory(root);
        using var owner = Process.GetCurrentProcess();
        var id = Guid.NewGuid().ToString("D");
        object Record(string stage, int pid, long ticks) => new
        {
            Id = id, Stage = stage, Pid = pid, StartTicks = ticks, Executable = info.FileName,
            OwnerPid = owner.Id, OwnerStartTicks = owner.StartTime.ToUniversalTime().Ticks, CreatedUtc = DateTimeOffset.UtcNow,
        };
        using (var file = new FileStream(active, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            JsonSerializer.Serialize(file, Record("prepared", 0, 0));
        Process? process = null;
        var started = false;
        try
        {
            process = Process.Start(info) ?? throw new IOException("安装器未启动。");
            started = true;
            var temporary = active + "." + id + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Record("running", process.Id, process.StartTime.ToUniversalTime().Ticks)));
            File.Move(temporary, active, true);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(waitLimit);
            progress?.Report("正在等待安装事务，PID " + process.Id);
            var wait = process.WaitForExitAsync(timeout.Token);
            while (!wait.IsCompleted)
            {
                var tick = Task.Delay(TimeSpan.FromSeconds(5), timeout.Token);
                if (await Task.WhenAny(wait, tick) == wait) break;
                await tick;
                progress?.Report("安装事务仍运行，正在等待 PID " + process.Id + "；日志：" + logs);
            }
            await wait;
            if (RuntimeInstallationReview.InstallerBusy())
                return new(process.ExitCode, true, "进程已退出，但 MSI 事务仍忙，记录保留。日志：" + logs);
            var archive = Path.Combine(root, "runtime-repair-history");
            Directory.CreateDirectory(archive);
            File.Move(active, Path.Combine(archive, id + "-exit-" + process.ExitCode + ".json"));
            return new(process.ExitCode, false, "退出码 " + process.ExitCode + "；日志：" + logs);
        }
        catch (Exception ex)
        {
            if (!started)
            {
                // 启动明确失败（文件不存在、权限等）：进程从未运行，无事务可等待。
                // 归档 prepared 记录为 launch-failed，避免核对入口对"启动结果未知"永久阻断。
                try
                {
                    var archive = Path.Combine(root, "runtime-repair-history");
                    Directory.CreateDirectory(archive);
                    File.Move(active, Path.Combine(archive, id + "-launch-failed.json"), true);
                }
                catch (Exception archiveError) { Log.Warn("启动失败记录未能归档：" + archiveError.Message); }
                return new(null, true, "安装器启动失败（进程未运行，无系统变更）：" + ex.Message + "；日志：" + logs);
            }
            return new(null, true, "安装器结果未确认，未强杀进程，记录保留：" + ex.Message + "；日志：" + logs);
        }
        finally { process?.Dispose(); }
    }
}
