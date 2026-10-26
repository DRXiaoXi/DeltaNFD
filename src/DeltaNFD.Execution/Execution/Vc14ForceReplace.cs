using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeltaNFD.Services.AlphaRestore;

namespace DeltaNFD.Services;

/// <summary>内置官方 14.x 载荷中的一个 DLL（清单固定大小与 SHA256）。</summary>
internal sealed record Vc14ForceReplacePayload(string Architecture, string File, long Bytes, string Sha256);

/// <summary>某一基线对应的固定载荷清单、版本与指纹。</summary>
internal sealed record Vc14ForceReplacePayloadSet(Vc14BaselineInfo Baseline,
    IReadOnlyList<Vc14ForceReplacePayload> Files, string Fingerprint)
{
    internal string ExpectedVersion => Baseline.Version;
}

/// <summary>写入结果：replaced / renamed / scheduled / acl-repaired / failed / restored / acl-unknown。</summary>
internal sealed record Vc14ForceReplaceOutcome(string Architecture, string File, string Outcome, string Detail);

/// <summary>只读注册表备份结果（导出失败只记录，不阻断文件替换）。</summary>
internal sealed record Vc14ForceReplaceRegistryExport(string Name, string Key, bool Ok, string Detail);

/// <summary>
/// 显式高风险操作：绕过 Windows Installer 的“较新共享文件不覆盖”规则，直接用内置官方
/// 14.44 文件覆盖 System32/SysWOW64 的 51 个 14.x DLL（x64 26 / x86 25）。
/// 对照外部 Vc14RuntimeFix 1.2 补齐：普通用户不可读的权限修复、属主 TrustedInstaller /
/// 管理员只读时的临时接管与替换后原属主/ACL 恢复、ACL 与注册表备份、替换后权限复核。
/// 先备份再替换；被占用时先尝试改名替换，仍失败则登记到下次启动。不修改 MSI/ARP 安装登记。
/// 仅正常模式、管理员、无待核对事务时可用；预览指纹变化后拒绝执行。
/// </summary>
public static class Vc14ForceReplace
{
    /// <summary>默认基线（14.44）目标版本；兼容既有固定检查。</summary>
    internal const string ExpectedVersion = "14.44.35211.0";
    private const int PayloadCount = 51;
    private const uint MoveFileReplaceExisting = 0x1;
    private const uint MoveFileDelayUntilReboot = 0x4;
    private static readonly TimeSpan OperationBudget = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>与外部包一致的三层备份中的注册表导出（只读备份，不做任何写入）。</summary>
    private static readonly (string Key, string Name)[] RegistryBackups =
    [
        (@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs", "SharedDLLs.reg"),
        (@"HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes", "VCRuntimes-x64view.reg"),
        (@"HKLM\SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes", "VCRuntimes-x86view.reg"),
        (@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "Uninstall.reg"),
    ];

    private static readonly Lazy<IReadOnlyDictionary<Vc14Baseline, Vc14ForceReplacePayloadSet>> PayloadSets =
        new(LoadPayloadSets);

    internal static Vc14ForceReplacePayloadSet PayloadSet(Vc14Baseline baseline) =>
        PayloadSets.Value.TryGetValue(baseline, out var set) ? set
            : throw new IOException("缺少 " + Vc14BaselineInfo.For(baseline).Label + " 的强制替换载荷清单。");
    internal static IReadOnlyList<Vc14ForceReplacePayload> Payload => PayloadSet(Vc14Baseline.V144).Files;
    internal static string PayloadFingerprint => PayloadSet(Vc14Baseline.V144).Fingerprint;
    internal static string WindowsRoot => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>只读预览：核对内置载荷、当前 51 个 DLL 版本/占用/ACL 状态，生成执行指纹。</summary>
    public static Vc14ForceReplacePreview Preview(CancellationToken token = default) =>
        PreviewCore(Vc14FileRepairSource.InstalledRoot, WindowsRoot, enforceHostChecks: true, versionReader: null, token,
            Vc14Baseline.V144, inspectAcl: true);

    public static Vc14ForceReplacePreview Preview(Vc14Baseline baseline, CancellationToken token = default) =>
        PreviewCore(Vc14BaselineInfo.For(baseline).FileRepairRoot, WindowsRoot, enforceHostChecks: true, versionReader: null, token,
            baseline, inspectAcl: true);

    /// <summary>执行强制替换；fingerprint 必须来自同一基线的本次预览。</summary>
    public static Task<OperationResult> RunAsync(string fingerprint, IProgress<string>? progress, CancellationToken token,
        Vc14Baseline baseline = Vc14Baseline.V144) =>
        Task.Run(() => RuntimeGuardProtection.Exclusive(() =>
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(OperationBudget);
            return RunCoreAsync(fingerprint, progress, budget.Token, Vc14BaselineInfo.For(baseline).FileRepairRoot, WindowsRoot,
                enforceHostChecks: true, versionReader: null, baseline: baseline, inspectAcl: true).GetAwaiter().GetResult();
        }), token);

    // ---- 预览 -----------------------------------------------------------------

    internal static Vc14ForceReplacePreview PreviewCore(string sourceRoot, string windowsRoot, bool enforceHostChecks,
        Func<string, string?>? versionReader, CancellationToken token, Vc14Baseline baseline = Vc14Baseline.V144,
        bool inspectAcl = false)
    {
        versionReader ??= ReadVersion;
        if (!baseline.IsSupported()) throw new IOException(baseline.UnsupportedMessage());
        var set = PayloadSet(baseline);
        var expectedVersion = set.ExpectedVersion;
        var info = set.Baseline;
        var blocker = "";
        if (enforceHostChecks)
        {
            if (Vc14InstallerReadiness.IsSafeMode) blocker = "强制替换只能在正常模式执行；请先重启回正常模式。";
            else if (!ElevationHelper.IsElevated) blocker = ElevationHelper.NotElevatedMessage;
            else if (RuntimeInstallationReview.InstallerBusy()) blocker = "Windows Installer 仍在执行事务，请结束后重试。";
            else
            {
                try
                {
                    var pending = new RuntimeInstallationReview(AppDataPaths.Root).InspectPending(
                        RuntimeInstallationReview.InstallerBusy(), out var ended);
                    if (pending is not null || ended) blocker = pending ?? "上次运行库操作结果待核对，请先核对未完成操作。";
                }
                catch (Exception ex) { blocker = "运行库事务状态无法确认：" + ex.Message; }
            }
        }

        try { VerifyMedia(sourceRoot, baseline); }
        catch (Exception ex) { if (blocker.Length == 0) blocker = "内置官方 " + info.Label + " 载荷无法核验：" + ex.Message; }

        var items = new List<Vc14ForceReplaceItem>(set.Files.Count);
        foreach (var payload in set.Files)
        {
            token.ThrowIfCancellationRequested();
            var target = TargetPath(windowsRoot, payload.Architecture, payload.File);
            var current = "";
            var status = "replace";
            var detail = "";
            var inUse = false;
            var downgrade = false;
            var aclBroken = false;
            var locked = false;
            try
            {
                if (!File.Exists(target))
                {
                    status = "absent";
                    detail = "文件不存在；强制替换不会创建文件，请先用官方安装器补齐。";
                }
                else if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                {
                    status = "blocked";
                    detail = "目标是重解析点/链接，拒绝替换。";
                }
                else
                {
                    current = versionReader(target) ?? "";
                    if (inspectAcl)
                    {
                        try
                        {
                            var acl = Vc14ForceReplaceAcl.Inspect(target);
                            aclBroken = !acl.UsersCanRead;
                            locked = !acl.AdminsCanWrite;
                        }
                        catch (Exception ex)
                        {
                            // ACL 连读都读不到时按"权限待修复"处理：执行阶段会先尝试接管并重置/恢复。
                            aclBroken = true;
                            locked = true;
                            detail = "文件权限无法读取（" + ex.Message + "），执行时先尝试修复权限。";
                        }
                    }

                    if (IsExpectedVersion(current, expectedVersion))
                    {
                        if (aclBroken)
                        {
                            status = "acl";
                            detail = AppendDetail(detail, "内容已是 " + expectedVersion + "，但普通用户无法读取（权限被改坏）；将重置为目录继承权限。");
                        }
                        else
                        {
                            status = "ok";
                            detail = AppendDetail(detail, locked
                                ? "已是 " + expectedVersion + "；管理员只读的属主不影响普通程序加载。"
                                : "已是 " + expectedVersion + "。");
                        }
                    }
                    else if (inspectAcl && aclBroken && current.Length == 0)
                    {
                        // 权限被改坏到连版本号都读不到：先按"仅修权限"处理，执行时重置权限后
                        // 再复读版本，仍不匹配才继续替换（与外部包先修 ACL 再判定一致）。
                        status = "acl";
                        detail = AppendDetail(detail, "权限被改坏导致版本无法读取；将先重置权限再复判是否需要替换。");
                    }
                    else
                    {
                        status = "replace";
                        if (current.Length == 0) detail = AppendDetail(detail, "版本无法读取；将尝试替换并复检。");
                        else if (IsNewerThanExpected(current, expectedVersion))
                        {
                            downgrade = true;
                            detail = AppendDetail(detail, "当前 " + current + " 较新；强制替换会降级到 " + expectedVersion + "。");
                        }
                        else detail = AppendDetail(detail, "当前 " + current + "；将替换为 " + expectedVersion + "。");
                        if (aclBroken) detail = AppendDetail(detail, "普通用户无法读取该文件，将一并修复权限。");
                        if (locked) detail = AppendDetail(detail, "管理员组无写权限，替换前将临时接管所有权。");
                    }
                    inUse = IsInUse(target);
                    if (inUse) detail = AppendDetail(detail, "文件被占用，将安排到下次启动替换。");
                }
            }
            catch (Exception ex) { status = "blocked"; detail = "无法检查目标文件：" + ex.Message; }
            items.Add(new(payload.Architecture, payload.File, current, expectedVersion, status, detail, inUse, downgrade,
                aclBroken, locked));
        }

        token.ThrowIfCancellationRequested();
        var absent = items.Count(i => i.Status == "absent");
        var blocked = items.Count(i => i.Status == "blocked");
        if (blocker.Length == 0 && absent > 0)
            blocker = $"有 {absent} 个 14.x DLL 不存在，强制替换不会创建文件；请先用官方安装器安装 {info.Label} 基线。";
        if (blocker.Length == 0 && blocked > 0)
            blocker = $"有 {blocked} 个 14.x DLL 无法检查或是链接，已停止。";

        var replaceCount = items.Count(i => i.Status == "replace");
        var aclRepairCount = items.Count(i => i.Status == "acl");
        return new Vc14ForceReplacePreview(
            CanRun: blocker.Length == 0,
            Blocker: blocker,
            Fingerprint: ComputeFingerprint(items, windowsRoot, set),
            Items: items,
            PayloadVersion: expectedVersion,
            ReplaceCount: replaceCount,
            SkipCount: items.Count(i => i.Status == "ok"),
            AbsentCount: absent,
            DowngradeCount: items.Count(i => i.Downgrade),
            AclRepairCount: aclRepairCount,
            LockedCount: items.Count(i => (i.Status is "replace" or "acl") && i.Locked)) { Baseline = baseline };
    }

    private static string AppendDetail(string current, string addition) =>
        current.Length == 0 ? addition : current + " " + addition;

    private static string ComputeFingerprint(IReadOnlyList<Vc14ForceReplaceItem> items, string windowsRoot, Vc14ForceReplacePayloadSet set)
    {
        var text = new StringBuilder();
        text.Append("root:").Append(Path.GetFullPath(windowsRoot).ToLowerInvariant()).Append('\n');
        text.Append("baseline:").Append(set.Baseline.Version).Append('\n');
        text.Append("payload:").Append(set.Fingerprint).Append('\n');
        foreach (var item in items)
            text.Append(item.Architecture).Append('|').Append(item.File).Append('|')
                .Append(item.CurrentVersion).Append('|').Append(item.Status).Append('|')
                .Append(item.AclBroken ? '1' : '0').Append('|').Append(item.Locked ? '1' : '0').Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    // ---- 执行 -----------------------------------------------------------------

    internal static async Task<OperationResult> RunCoreAsync(string fingerprint, IProgress<string>? progress, CancellationToken token,
        string sourceRoot, string windowsRoot, bool enforceHostChecks, Func<string, string?>? versionReader,
        Action<string>? afterReplacementForTests = null, Vc14Baseline baseline = Vc14Baseline.V144,
        bool inspectAcl = false)
    {
        versionReader ??= ReadVersion;
        var preview = PreviewCore(sourceRoot, windowsRoot, enforceHostChecks, versionReader, token, baseline, inspectAcl);
        if (!preview.CanRun) return OperationResult.Fail(preview.Blocker);
        if (!string.Equals(preview.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            return OperationResult.Fail("14.x DLL 清单已变化，请重新预览确认；本次未修改任何文件。");
        var set = PayloadSet(baseline);
        var info = set.Baseline;
        var expectedVersion = set.ExpectedVersion;
        var replaceItems = preview.Items.Where(i => i.Status == "replace").ToArray();
        var aclItems = preview.Items.Where(i => i.Status == "acl").ToArray();
        var actionItems = replaceItems.Concat(aclItems).ToArray();
        if (actionItems.Length == 0)
            return OperationResult.Ok(set.Files.Count + " 个 14.x DLL 已全部为 " + expectedVersion + "，无需强制替换。");

        token.ThrowIfCancellationRequested();
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var directory = Path.Combine(AppDataPaths.Root, "Vc14ForceReplace", stamp + "-" + Guid.NewGuid().ToString("N")[..8]);
        try { Directory.CreateDirectory(directory); }
        catch (Exception ex) { return OperationResult.Fail("无法创建备份目录，未修改系统文件：" + ex.Message); }

        // 1) ACL/属主快照：必须在任何写入/接管之前完成；无法保存时拒绝继续，避免改了权限却无恢复依据。
        var aclSnapshots = new Dictionary<string, Vc14FileAcl>(StringComparer.OrdinalIgnoreCase);
        if (inspectAcl)
        {
            var snapshotFailures = new List<string>();
            foreach (var item in actionItems)
            {
                token.ThrowIfCancellationRequested();
                var target = TargetPath(windowsRoot, item.Architecture, item.File);
                try { aclSnapshots[Key(item.Architecture, item.File)] = Vc14ForceReplaceAcl.Inspect(target); }
                catch (Exception ex) { snapshotFailures.Add(item.File + "（" + ex.Message + "）"); }
            }
            if (snapshotFailures.Count > 0)
                return OperationResult.Fail("无法读取以下文件的 ACL/属主，缺少恢复依据，未修改任何系统文件："
                    + string.Join("；", snapshotFailures) + "。请先用官方安装器或管理员权限修复这些文件的权限后重试。");
            try { WriteAclSnapshot(directory, windowsRoot, aclSnapshots); }
            catch (Exception ex) { return OperationResult.Fail("无法保存原 ACL/属主快照，未修改系统文件：" + ex.Message); }
        }

        // 2) 逐字节备份（包含仅修复权限的文件：权限处理失败时可回滚字节；最坏情况也能人工恢复）。
        var backups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var backupHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var currentHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var preRepairedAcl = new List<string>();
        progress?.Report("正在备份当前 14.x DLL 到 " + directory + "…");
        try
        {
            token.ThrowIfCancellationRequested();
            foreach (var item in actionItems)
            {
                var target = TargetPath(windowsRoot, item.Architecture, item.File);
                var backup = Path.Combine(directory, item.Architecture, item.File);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                try
                {
                    File.Copy(target, backup, overwrite: true);
                    var sourceHash = HashFile(target);
                    var backupHash = HashFile(backup);
                    if (!sourceHash.Equals(backupHash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("备份字节校验不一致：" + item.File);
                    backups[Key(item.Architecture, item.File)] = backup;
                    currentHashes[Key(item.Architecture, item.File)] = sourceHash;
                    backupHashes[Key(item.Architecture, item.File)] = backupHash;
                }
                catch (Exception ex) when (item.Status == "acl")
                {
                    // 权限被改坏到连提权进程也读不到字节：先重置为继承权限（ACL 快照已在步骤 1 保存），
                    // 再重试备份。只修权限的文件本不改变字节，修复后可正常备份。
                    progress?.Report($"备份前先修复 {item.Architecture} {item.File} 的权限…");
                    // 先登记再改：即使 ResetInherited 部分完成即失败，退出时也要尝试按快照恢复。
                    preRepairedAcl.Add(Key(item.Architecture, item.File));
                    if (!Vc14ForceReplaceAcl.ResetInherited(target, out var resetDetail))
                        throw new IOException("文件权限被改坏且无法备份：" + item.File + "（" + resetDetail + "；" + ex.Message + "）");
                    File.Copy(target, backup, overwrite: true);
                    var sourceHash = HashFile(target);
                    var backupHash = HashFile(backup);
                    if (!sourceHash.Equals(backupHash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("修复权限后备份字节校验不一致：" + item.File);
                    backups[Key(item.Architecture, item.File)] = backup;
                    currentHashes[Key(item.Architecture, item.File)] = sourceHash;
                    backupHashes[Key(item.Architecture, item.File)] = backupHash;
                }
            }
        }
        catch (Exception ex)
        {
            // 备份阶段可能为读取文件而临时重置过极少数 ACL：退出前恢复快照，保持“未替换任何字节”的承诺。
            var aclRestoreFailures = new List<string>();
            foreach (var key in preRepairedAcl)
            {
                if (!inspectAcl || !aclSnapshots.TryGetValue(key, out var snapshot) || snapshot.Sddl.Length == 0) continue;
                var parts = key.Split('|');
                var target = TargetPath(windowsRoot, parts[0], parts[1]);
                if (!Vc14ForceReplaceAcl.Restore(target, snapshot, out var restoreDetail))
                    aclRestoreFailures.Add(parts[1] + "：" + restoreDetail);
            }
            return new OperationResult
            {
                Success = false, DiagnosticsDirectory = directory, OutcomeUnknown = aclRestoreFailures.Count > 0,
                Message = "备份未完成，未替换任何 DLL 字节：" + ex.Message
                    + (preRepairedAcl.Count == 0 ? ""
                        : aclRestoreFailures.Count == 0
                            ? "\n备份前临时重置过 " + preRepairedAcl.Count + " 个文件的权限，已按 acl.json 快照恢复。"
                            : "\n备份前临时重置过 " + preRepairedAcl.Count + " 个文件的权限，以下恢复失败，请按 acl.json 人工核对："
                                + string.Join("；", aclRestoreFailures)),
            };
        }

        // 3) 注册表只读备份（与外部包一致；失败只记录并继续）。
        progress?.Report("正在导出注册表备份（SharedDLLs / VC Runtimes / Uninstall）…");
        var registryExports = ExportRegistryBackups(directory);

        WriteReadme(directory, windowsRoot, preview);
        WriteInventory(directory, preview, windowsRoot, backups, currentHashes, backupHashes, aclSnapshots, registryExports, []);
        var outcomes = new List<Vc14ForceReplaceOutcome>();
        var attempted = new List<Vc14ForceReplaceItem>();
        var payloadRoot = Path.Combine(Path.GetTempPath(), "DeltaNFD-Vc14ForceReplace-" + Guid.NewGuid().ToString("N"));
        try
        {
            progress?.Report("正在核验并解包内置官方 " + info.Label + " " + expectedVersion + " 载荷…");
            var payload = ExtractPayload(sourceRoot, payloadRoot, token, baseline);
            token.ThrowIfCancellationRequested();
            var stagingRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp",
                "DeltaNFD", "Vc14ForceReplace", stamp + "-" + Guid.NewGuid().ToString("N")[..8]);
            var index = 0;
            foreach (var item in actionItems)
            {
                token.ThrowIfCancellationRequested();
                index++;
                var target = TargetPath(windowsRoot, item.Architecture, item.File);
                try
                {
                    attempted.Add(item);
                    var source = payload[Key(item.Architecture, item.File)];
                    outcomes.Add(ExecuteItem(item, source, target, stagingRoot, aclSnapshots, inspectAcl, versionReader,
                        expectedVersion, progress, index, actionItems.Length, afterReplacementForTests, token));
                }
                catch (Exception ex)
                {
                    outcomes.Add(new(item.Architecture, item.File, "failed", ex.Message));
                }
            }
        }
        catch (OperationCanceledException)
        {
            outcomes.Add(new("-", "-", "cancelled", "操作已取消；备份目录保留，可手动恢复。"));
        }
        catch (Exception ex)
        {
            outcomes.Add(new("-", "-", "failed", "载荷准备失败，替换未开始：" + ex.Message));
        }
        finally
        {
            TryDeleteDirectory(payloadRoot);
        }

        var failed = outcomes.Where(o => o.Outcome is "failed" or "acl-unknown").ToArray();
        var aclUnknown = outcomes.Count(o => o.Outcome == "acl-unknown");
        var cancelled = outcomes.Any(o => o.Outcome == "cancelled");
        var scheduled = outcomes.Count(o => o.Outcome == "scheduled");
        var replaced = outcomes.Count(o => o.Outcome is "replaced" or "renamed");
        var aclRepaired = outcomes.Count(o => o.Outcome == "acl-repaired");

        // 4) 失败时用本次备份把已尝试、且未登记重启排程的文件字节与 ACL 都恢复。
        //    排程项无法在本次回滚（PendingFileRenameOperations 已登记），但其 ACL 在排程前已恢复。
        var rollback = "";
        var rollbackFailed = false;
        var rollbackComplete = false;
        if (failed.Length > 0 && attempted.Count > 0)
        {
            var failures = new List<string>();
            foreach (var item in attempted)
            {
                var attemptedOutcome = outcomes.FirstOrDefault(o => o.Architecture == item.Architecture && o.File == item.File);
                if (attemptedOutcome?.Outcome == "scheduled") continue;
                var key = Key(item.Architecture, item.File);
                var target = TargetPath(windowsRoot, item.Architecture, item.File);
                try
                {
                    var aclRestored = false;
                    if (backups.TryGetValue(key, out var backup))
                    {
                        File.Copy(backup, target, overwrite: true);
                        if (!HashMatches(target, backupHashes[key])) throw new IOException("恢复后字节校验不一致。");
                    }
                    if (inspectAcl)
                    {
                        if (aclSnapshots.TryGetValue(key, out var snapshot) && snapshot.Sddl.Length > 0)
                        {
                            if (!Vc14ForceReplaceAcl.Restore(target, snapshot, out var restoreDetail))
                                throw new IOException("恢复原 ACL/属主失败：" + restoreDetail);
                            aclRestored = true;
                        }
                        else
                        {
                            throw new IOException("缺少原 ACL/属主快照，无法确认权限恢复；请按 acl.json/README 人工核对。");
                        }
                    }
                    outcomes.RemoveAll(o => o.Architecture == item.Architecture && o.File == item.File);
                    outcomes.Add(new(item.Architecture, item.File, "restored", "本次尝试已按备份恢复字节" +
                        (aclRestored ? "与权限/属主" : "") + "并核验。"));
                }
                catch (Exception ex)
                {
                    failures.Add(item.File + "：" + ex.Message);
                    outcomes.RemoveAll(o => o.Architecture == item.Architecture && o.File == item.File);
                    outcomes.Add(new(item.Architecture, item.File, "restore-failed", ex.Message));
                }
            }
            rollbackFailed = failures.Count > 0;
            rollbackComplete = !rollbackFailed;
            rollback = failures.Count == 0
                ? "已用本目录备份自动回滚本次已写入且未排程重启的文件（含权限）。"
                : "部分文件自动回滚失败：" + string.Join("；", failures);
        }

        var inventoryFailed = false;
        try
        {
            WriteInventory(directory, preview, windowsRoot, backups, currentHashes, backupHashes, aclSnapshots, registryExports,
                outcomes);
        }
        catch (Exception ex) { inventoryFailed = true; Log.Error("强制替换：最终 inventory.json 写入失败，保留待核对", ex); }

        var reboot = scheduled > 0;
        var message = new StringBuilder();
        message.Append("强制替换 " + info.Label + "：已替换 ").Append(replaced).Append(" 个");
        if (aclRepaired > 0) message.Append("，仅修复权限 ").Append(aclRepaired).Append(" 个");
        if (scheduled > 0) message.Append("，安排重启后替换 ").Append(scheduled).Append(" 个");
        message.Append("，失败 ").Append(failed.Length).Append(" 个，跳过 ").Append(preview.SkipCount)
            .Append(" 个（已是 " + expectedVersion + "）。");
        message.Append("\n备份目录（inventory.json、acl.json、restore.ps1）：").Append(directory);
        if (registryExports.Count > 0)
            message.Append("\n注册表备份：").Append(registryExports.Count(r => r.Ok)).Append('/')
                .Append(registryExports.Count).Append(" 个导出成功（registry 子目录，只读备份）。");
        foreach (var failure in failed)
            message.Append("\n失败：").Append(failure.Architecture).Append(' ').Append(failure.File).Append(" · ").Append(failure.Detail);
        if (cancelled) message.Append("\n操作已取消；已替换的文件保留在备份目录，可用 restore.ps1 恢复。");
        if (rollback.Length > 0) message.Append('\n').Append(rollback);
        if (inventoryFailed) message.Append("\n最终恢复清单保存失败，备份保留，请人工核对后再继续。");
        if (reboot)
            message.Append("\n有文件被占用，已登记到下次启动替换；请重启后再运行“强制替换 14.x”预览确认。重启前不要清理系统 Temp。")
                .Append("\n重启后换入的文件使用安全默认权限；如需严格核对原 ACL/属主，请重启后再次预览。");
        message.Append("\n安装登记（MSI/ARP）不会改变；如需登记一致，请回正常模式执行“仅修复 C++ 14.x”。");

        if (failed.Length == 0 && !cancelled && !inventoryFailed && aclUnknown == 0)
        {
            return new OperationResult
            {
                Success = true, IsPartial = reboot, RequiresReboot = reboot,
                DiagnosticsDirectory = directory, Message = message.ToString(),
            };
        }
        return new OperationResult
        {
            Success = false, IsPartial = !rollbackComplete && (attempted.Count > 0 || replaced + aclRepaired + scheduled > 0),
            RequiresReboot = reboot,
            OutcomeUnknown = rollbackFailed || inventoryFailed || aclUnknown > 0,
            DiagnosticsDirectory = directory, Message = message.ToString(),
        };
    }

    /// <summary>
    /// 处理单个预览项：status=acl 时只重置权限并复读版本；status=replace 时按需接管权限后替换，
    /// 替换成功后恢复原属主/ACL 或修复被改坏的权限。
    /// </summary>
    private static Vc14ForceReplaceOutcome ExecuteItem(Vc14ForceReplaceItem item, string? source, string target,
        string stagingRoot, IReadOnlyDictionary<string, Vc14FileAcl> aclSnapshots, bool inspectAcl,
        Func<string, string?> versionReader, string expectedVersion, IProgress<string>? progress, int index, int total,
        Action<string>? afterReplacementForTests, CancellationToken token)
    {
        var key = Key(item.Architecture, item.File);
        aclSnapshots.TryGetValue(key, out var original);
        original ??= Vc14FileAcl.Unknown;
        var detail = new StringBuilder();

        if (item.Status == "acl")
        {
            progress?.Report($"正在修复 {item.Architecture} {item.File} 的权限…");
            if (!Vc14ForceReplaceAcl.CanWrite(target))
                _ = Vc14ForceReplaceAcl.EnsureWritable(target, out _, out _);
            if (!Vc14ForceReplaceAcl.ResetInherited(target, out var resetDetail))
                throw new IOException("重置文件权限失败：" + resetDetail);
            // 仅为修复权限而临时接管过所有权/组时，把原属主/组放回；DACL 保持已修复的继承权限。
            var afterReset = Vc14ForceReplaceAcl.InspectOrUnknown(target);
            if (!afterReset.UsersCanRead) throw new IOException("权限修复后普通用户仍无法读取该文件。");
            if ((original.OwnerSid.Length > 0 || original.GroupSid.Length > 0)
                && !Vc14ForceReplaceAcl.RestoreIdentity(target, original.OwnerSid, original.GroupSid, out var ownerDetail))
                return new(item.Architecture, item.File, "acl-unknown",
                    "权限已重置，但原属主/组恢复失败：" + ownerDetail);
            var version = versionReader(target) ?? "";
            if (IsExpectedVersion(version, expectedVersion))
                return new(item.Architecture, item.File, "acl-repaired",
                    "内容已是 " + expectedVersion + "，仅重置了被改坏的权限。" +
                    (resetDetail.Length > 0 ? " " + resetDetail : "") + detail);
            detail.Append("权限修复后版本为 ").Append(version.Length == 0 ? "无法读取" : version).Append("，继续替换。");
        }

        if (inspectAcl && (item.Locked || item.AclBroken || item.Status == "acl"))
        {
            progress?.Report($"正在准备 {item.Architecture} {item.File} 的写权限…");
            if (Vc14ForceReplaceAcl.EnsureWritable(target, out var tookOwnership, out var writeDetail))
            {
                if (tookOwnership) detail.Append("已临时接管所有权并取得写权限。");
                else if (writeDetail.Length > 0) detail.Append(writeDetail).Append('。');
            }
            else
            {
                detail.Append("无法提前取得写权限，将尝试改名/重启后替换（").Append(writeDetail).Append("）。");
            }
        }

        token.ThrowIfCancellationRequested();
        progress?.Report($"正在替换 {item.Architecture} {item.File}（{index}/{total}）…");
        if (source is null) throw new IOException("缺少载荷文件：" + item.File);
        // 预览到替换之间目标可能被外部改动：覆盖前复核存在性与链接，避免 File.Copy(overwrite) 悄悄新建文件。
        if (!File.Exists(target)) throw new IOException("目标文件在替换前消失，已停止替换该文件。");
        if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("目标文件在替换前变成重解析点/链接，已停止替换该文件。");
        // 重启后替换的暂存副本必须使用安全默认 ACL（与外部包一致），不能把 Windows\Temp 的 ACL 带进 System32。
        var action = ApplyReplacement(source, target, stagingRoot, item.Architecture, item.File, safeStagingAcl: inspectAcl);
        if (action is "replaced" or "renamed")
        {
            afterReplacementForTests?.Invoke(target);
            var actual = ReadVersion(target) ?? "";
            if (!IsExpectedVersion(actual, expectedVersion))
                throw new IOException("写入后版本为 " + (actual.Length == 0 ? "无法读取" : actual) + "，不是 " + expectedVersion + "。");
            if (inspectAcl)
            {
                var aclOk = FinalizeAcl(target, original, item, out var aclDetail);
                if (aclDetail.Length > 0) detail.Append(aclDetail);
                if (!aclOk)
                    return new(item.Architecture, item.File, "acl-unknown",
                        "字节已替换为 " + expectedVersion + "，但权限/属主恢复结果无法确认：" + aclDetail);
            }
            return new(item.Architecture, item.File, action, detail.ToString());
        }

        detail.Append("文件被占用，已登记到下次启动替换。重启后请重新预览核对权限。");
        if (inspectAcl && original.Sddl.Length > 0)
        {
            // 重启后替换不等待本次进程：若刚才为取得写权限临时改过 ACL/属主，必须现在恢复，
            // 不能把"管理员可写"的临时权限留在系统 DLL 上。
            var now = Vc14ForceReplaceAcl.InspectOrUnknown(target);
            var changed = now.UsersCanRead != original.UsersCanRead || now.AdminsCanWrite != original.AdminsCanWrite
                || now.DaclProtected != original.DaclProtected
                || (original.OwnerSid.Length > 0 && !now.OwnerSid.Equals(original.OwnerSid, StringComparison.OrdinalIgnoreCase))
                || (original.GroupSid.Length > 0 && !now.GroupSid.Equals(original.GroupSid, StringComparison.OrdinalIgnoreCase));
            if (changed)
            {
                if (!Vc14ForceReplaceAcl.Restore(target, original, out var restoreDetail))
                    return new(item.Architecture, item.File, "acl-unknown",
                        "已登记重启后替换，但临时取得的所有权/ACL 无法恢复：" + restoreDetail);
                detail.Append("已恢复临时接管的 ACL/属主。");
            }
        }
        return new(item.Architecture, item.File, "scheduled", detail.ToString());
    }

    /// <summary>替换成功后复核/恢复权限：被改坏的重置继承；原本正常的恢复原属主与 DACL。</summary>
    private static bool FinalizeAcl(string target, Vc14FileAcl original, Vc14ForceReplaceItem item, out string detail)
    {
        detail = "";
        if (item.AclBroken)
        {
            if (!Vc14ForceReplaceAcl.ResetInherited(target, out var resetDetail))
            {
                detail = "重置继承权限失败：" + resetDetail;
                return false;
            }
            if (!Vc14ForceReplaceAcl.InspectOrUnknown(target).UsersCanRead)
            {
                detail = "重置权限后普通用户仍无法读取。";
                return false;
            }
            // 替换前可能为取得写权限临时接管过所有权；权限已重置，但原属主/组仍需放回。
            if ((original.OwnerSid.Length > 0 || original.GroupSid.Length > 0)
                && !Vc14ForceReplaceAcl.RestoreIdentity(target, original.OwnerSid, original.GroupSid, out var identityDetail))
            {
                detail = "权限已重置，但原属主/组恢复失败：" + identityDetail;
                return false;
            }
            detail = "已重置被改坏的权限。";
            return true;
        }

        var current = Vc14ForceReplaceAcl.InspectOrUnknown(target);
        if (original.Sddl.Length == 0)
        {
            if (current.UsersCanRead)
            {
                detail = "未取得原 ACL 快照，写入后普通用户可读。";
                return true;
            }
            if (!Vc14ForceReplaceAcl.ResetInherited(target, out var fallbackDetail))
            {
                detail = "未取得原 ACL 快照且重置权限失败：" + fallbackDetail;
                return false;
            }
            detail = "未取得原 ACL 快照，已重置为继承权限。";
            return Vc14ForceReplaceAcl.InspectOrUnknown(target).UsersCanRead;
        }

        var preserved = current.UsersCanRead == original.UsersCanRead
            && current.AdminsCanWrite == original.AdminsCanWrite
            && current.DaclProtected == original.DaclProtected
            && (original.OwnerSid.Length == 0
                || current.OwnerSid.Equals(original.OwnerSid, StringComparison.OrdinalIgnoreCase))
            && (original.GroupSid.Length == 0
                || current.GroupSid.Equals(original.GroupSid, StringComparison.OrdinalIgnoreCase));
        if (preserved) return true;

        if (Vc14ForceReplaceAcl.Restore(target, original, out var restoreDetail))
        {
            detail = "已恢复原属主与 ACL。";
            return true;
        }
        var after = Vc14ForceReplaceAcl.InspectOrUnknown(target);
        if (!after.UsersCanRead && Vc14ForceReplaceAcl.ResetInherited(target, out var inheritedDetail))
        {
            detail = "恢复原 ACL 失败，已改为目录继承权限：" + restoreDetail;
            return Vc14ForceReplaceAcl.InspectOrUnknown(target).UsersCanRead;
        }
        detail = restoreDetail;
        return false;
    }

    /// <summary>优先直接覆盖；失败则改名为目标（可绕过已加载 DLL 的写共享限制）；仍失败则登记重启后替换。</summary>
    private static string ApplyReplacement(string source, string target, string stagingRoot, string architecture, string file,
        bool safeStagingAcl)
    {
        try { File.Copy(source, target, overwrite: true); return "replaced"; }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        var staged = Path.Combine(stagingRoot, architecture, file);
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.Copy(source, staged, overwrite: true);
        try
        {
            // 改名/重启替换会把暂存文件搬进 System32，必须带上合适 ACL，不能继承 Windows\Temp 的权限。
            // 原权限被改坏时使用安全默认 ACL；否则复制目标 DACL 以保持原权限。
            if (safeStagingAcl)
            {
                if (!Vc14ForceReplaceAcl.ApplySafeAcl(staged, out var safeDetail))
                    throw new IOException("无法为暂存文件写入安全 ACL：" + safeDetail);
            }
            else
            {
                var security = FileSystemAclExtensions.GetAccessControl(new FileInfo(target), AccessControlSections.Access);
                FileSystemAclExtensions.SetAccessControl(new FileInfo(staged), security);
            }
        }
        catch (Exception ex)
        {
            try { File.Delete(staged); } catch { }
            throw new IOException("无法为暂存文件准备访问控制，已拒绝替换以免留下错误 ACL：" + ex.Message);
        }

        if (MoveFileEx(staged, target, MoveFileReplaceExisting))
        {
            try { File.Delete(staged); } catch { }
            return "renamed";
        }
        var renameError = Marshal.GetLastWin32Error();
        if (MoveFileEx(staged, target, MoveFileReplaceExisting | MoveFileDelayUntilReboot)) return "scheduled";
        var scheduleError = Marshal.GetLastWin32Error();
        throw new IOException("CopyFile/改名/重启排程均失败（" + renameError + "/" + scheduleError + "）。");
    }

    // ---- 载荷 -----------------------------------------------------------------

    private static IReadOnlyDictionary<Vc14Baseline, Vc14ForceReplacePayloadSet> LoadPayloadSets()
    {
        var resources = new Dictionary<Vc14Baseline, string>
        {
            [Vc14Baseline.V144] = "Vc14ForceReplace14.44.catalog.json",
        };
        var result = new Dictionary<Vc14Baseline, Vc14ForceReplacePayloadSet>();
        foreach (var (baseline, resource) in resources)
        {
            var info = Vc14BaselineInfo.For(baseline);
            using var stream = typeof(Vc14ForceReplace).Assembly.GetManifestResourceStream(resource)
                ?? throw new IOException("缺少 " + info.Label + " 强制替换载荷清单（" + resource + "）。");
            var files = JsonSerializer.Deserialize<Vc14ForceReplacePayload[]>(stream)
                ?? throw new IOException(info.Label + " 强制替换载荷清单无效。");
            if (files.Length != PayloadCount
                || files.Count(f => f.Architecture == "x64") != 26
                || files.Count(f => f.Architecture == "x86") != 25
                || files.Any(f => f.Architecture is not ("x64" or "x86") || string.IsNullOrWhiteSpace(f.File)
                    || f.File.Contains('\\') || f.File.Contains('/') || f.Bytes <= 0 || f.Sha256.Length != 64))
                throw new IOException(info.Label + " 强制替换载荷清单范围无效。");
            if (files.Select(f => Key(f.Architecture, f.File)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length)
                throw new IOException(info.Label + " 强制替换载荷清单存在重复项。");
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Join("\n", files.OrderBy(p => p.Architecture, StringComparer.Ordinal)
                    .ThenBy(p => p.File, StringComparer.OrdinalIgnoreCase)
                    .Select(p => $"{p.Architecture}|{p.File}|{p.Bytes}|{p.Sha256}")))));
            result[baseline] = new Vc14ForceReplacePayloadSet(info, files, fingerprint);
        }
        return result;
    }

    private static void VerifyMedia(string sourceRoot, Vc14Baseline baseline)
    {
        Vc14FileRepairSource.Verify(sourceRoot, "x64", baseline);
        Vc14FileRepairSource.Verify(sourceRoot, "x86", baseline);
    }

    /// <summary>从四个官方 CAB 解包并用固定清单逐项校验；返回 arch|file → 解包文件路径。</summary>
    internal static IReadOnlyDictionary<string, string> ExtractPayload(string sourceRoot, string destination, CancellationToken token,
        Vc14Baseline baseline = Vc14Baseline.V144)
    {
        VerifyMedia(sourceRoot, baseline);
        var set = PayloadSet(baseline);
        var expected = set.Files.ToDictionary(p => Key(p.Architecture, p.File), StringComparer.OrdinalIgnoreCase);
        var extracted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var architecture in new[] { "x64", "x86" })
        {
            foreach (var package in Vc14FileRepairSource.PackagesFor(baseline).Where(p => p.Architecture == architecture))
            {
                token.ThrowIfCancellationRequested();
                var media = package.Files.Single(f => f.Path.EndsWith(".cab", StringComparison.OrdinalIgnoreCase));
                var cab = OfficialCppCatalog.Resolve(sourceRoot, media.Path);
                var directory = Path.Combine(destination, architecture, package.Role);
                Directory.CreateDirectory(directory);
                RunExpand(cab, directory);
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    var name = Path.GetFileName(file);
                    if (!TryParsePayloadName(name, out var dll, out var suffixArchitecture) || suffixArchitecture != architecture)
                        throw new IOException("内置载荷解包出现未预期文件：" + name);
                    var key = Key(architecture, dll);
                    if (!expected.TryGetValue(key, out var payload))
                        throw new IOException("内置载荷包含清单外文件：" + name);
                    if (new FileInfo(file).Length != payload.Bytes || !HashMatches(file, payload.Sha256))
                        throw new IOException("内置载荷校验失败：" + key);
                    if (!extracted.TryAdd(key, file)) throw new IOException("内置载荷出现重复文件：" + key);
                }
            }
        }
        token.ThrowIfCancellationRequested();
        if (extracted.Count != set.Files.Count)
            throw new IOException($"内置载荷数量不符：{extracted.Count}/{set.Files.Count}。");
        return extracted;
    }

    private static bool TryParsePayloadName(string fileName, out string dll, out string architecture)
    {
        dll = "";
        architecture = "";
        var marker = fileName.LastIndexOf(".dll_", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return false;
        dll = fileName[..(marker + 4)];
        var suffix = fileName[(marker + 5)..];
        architecture = suffix.Equals("amd64", StringComparison.OrdinalIgnoreCase) ? "x64"
            : suffix.Equals("x86", StringComparison.OrdinalIgnoreCase) ? "x86" : "";
        return architecture.Length > 0;
    }

    private static void RunExpand(string cabinet, string destination)
    {
        var expand = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "expand.exe");
        var info = new ProcessStartInfo(File.Exists(expand) ? expand : "expand.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        info.ArgumentList.Add("-F:*");
        info.ArgumentList.Add(cabinet);
        info.ArgumentList.Add(destination);
        using var process = Process.Start(info) ?? throw new IOException("无法启动 expand.exe。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new IOException("内置 CAB 解包超时。");
        }
        _ = stdout.Result;
        if (process.ExitCode != 0) throw new IOException("内置 CAB 解包失败：" + stderr.Result.Trim());
    }

    private static bool HashMatches(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    // ---- 备份与结果 -------------------------------------------------------------

    private static void WriteAclSnapshot(string directory, string windowsRoot, IReadOnlyDictionary<string, Vc14FileAcl> snapshots)
    {
        var items = snapshots.Select(pair =>
        {
            var parts = pair.Key.Split('|');
            var acl = pair.Value;
            return new
            {
                architecture = parts[0],
                file = parts[1],
                target = TargetPath(windowsRoot, parts[0], parts[1]),
                ownerSid = acl.OwnerSid,
                owner = Vc14ForceReplaceAcl.DescribeOwnerSid(acl.OwnerSid),
                groupSid = acl.GroupSid,
                group = Vc14ForceReplaceAcl.DescribeOwnerSid(acl.GroupSid),
                daclProtected = acl.DaclProtected,
                usersCanRead = acl.UsersCanRead,
                adminsCanWrite = acl.AdminsCanWrite,
                sddl = acl.Sddl,
            };
        }).ToArray();
        var snapshot = new
        {
            tool = "DeltaNFD", kind = "vc14-force-replace-acl", createdAtUtc = DateTimeOffset.UtcNow,
            windowsRoot, items,
        };
        File.WriteAllText(Path.Combine(directory, "acl.json"), JsonSerializer.Serialize(snapshot, JsonOptions),
            new UTF8Encoding(false));
    }

    private static IReadOnlyList<Vc14ForceReplaceRegistryExport> ExportRegistryBackups(string directory)
    {
        var results = new List<Vc14ForceReplaceRegistryExport>(RegistryBackups.Length);
        var regDirectory = Path.Combine(directory, "registry");
        try { Directory.CreateDirectory(regDirectory); }
        catch (Exception ex)
        {
            return RegistryBackups.Select(r => new Vc14ForceReplaceRegistryExport(r.Name, r.Key, false,
                "无法创建 registry 备份目录：" + ex.Message)).ToArray();
        }
        var reg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe");
        foreach (var (key, name) in RegistryBackups)
        {
            var file = Path.Combine(regDirectory, name);
            try
            {
                var info = new ProcessStartInfo(File.Exists(reg) ? reg : "reg.exe")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                info.ArgumentList.Add("export");
                info.ArgumentList.Add(key);
                info.ArgumentList.Add(file);
                info.ArgumentList.Add("/y");
                using var process = Process.Start(info) ?? throw new IOException("无法启动 reg.exe。");
                var stderr = process.StandardError.ReadToEndAsync();
                _ = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(60_000))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    throw new IOException("reg export 超时。");
                }
                if (process.ExitCode != 0 || !File.Exists(file))
                    throw new IOException("reg export 退出码 " + process.ExitCode + "：" + stderr.Result.Trim());
                results.Add(new(name, key, true, ""));
            }
            catch (Exception ex)
            {
                try { if (File.Exists(file)) File.Delete(file); } catch { }
                results.Add(new(name, key, false, ex.Message));
            }
        }
        return results;
    }

    private static void WriteReadme(string directory, string windowRoot, Vc14ForceReplacePreview preview)
    {
        var aclNote = preview.LockedCount > 0 || preview.AclRepairCount > 0
            ? $"""

            权限处理：
              - {preview.AclRepairCount} 个文件的内容已匹配但普通用户无法读取，将重置为目录继承权限。
              - {preview.LockedCount} 个文件属主为 TrustedInstaller / 管理员只读，替换前临时接管所有权，
                替换成功后恢复原属主与 ACL。
              - 原 ACL/属主快照见 acl.json；restore.ps1 只写回文件字节，不还原 ACL/属主。
            """
            : "";

        var readme = $"""
            DeltaNFD 强制替换 14.x 备份目录
            ================================

            本目录保存了强制替换前的 14.x DLL、原 ACL/属主快照与注册表导出的只读备份，
            以及 inventory.json、restore.ps1。
            目标：用内置官方 {preview.PayloadVersion}（{Vc14BaselineOption.Display(preview.Baseline)}）文件覆盖
            %SystemRoot%\System32 与 %SystemRoot%\SysWOW64 中的 14.x DLL（本次预览待替换 {preview.ReplaceCount} 个，
            仅修复权限 {preview.AclRepairCount} 个）。{aclNote}

            恢复方法（需要管理员）：
              1. 如果本次结果提示“已登记到下次启动替换”，先重启 Windows 让替换完成。
              2. 右键“以管理员身份运行”本目录中的 restore.ps1。
              3. 恢复后重新运行 DeltaNFD 的 14.x 检测。

            注意：
              - 强制替换不改变 Windows Installer/ARP 的安装登记；文件与登记可能不一致。
              - 恢复只写回文件字节，不还原 ACL/属主、SharedDLLs 计数或已登记的待重启操作；
                原 ACL/属主保存在 acl.json，registry 子目录是替换前的只读注册表快照。
              - 被占用文件会登记到下次启动替换；重启后换入的文件使用安全默认 ACL，
                无法在重启前恢复原 ACL，请重启后再次预览核对。
              - 本目录是恢复凭据，操作后请保留；不要只删除目录。

            预览指纹：{preview.Fingerprint}
            目标根目录：{windowRoot}
            """;
        File.WriteAllText(Path.Combine(directory, "README.txt"), readme, new UTF8Encoding(true));

        const string restore = """
            #Requires -Version 5.1
            # DeltaNFD 强制替换 14.x 的备份恢复脚本。请以管理员身份运行。
            $ErrorActionPreference = 'Stop'
            $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
            if (-not (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
                [Security.Principal.WindowsBuiltInRole]::Administrator)) {
                Write-Host '请以管理员身份运行此脚本。' -ForegroundColor Red
                exit 5
            }
            $root = Split-Path -Parent $MyInvocation.MyCommand.Path
            $pairs = @(
                @{ Source = (Join-Path $root 'x64'); Target = (Join-Path $env:SystemRoot 'System32') },
                @{ Source = (Join-Path $root 'x86'); Target = (Join-Path $env:SystemRoot 'SysWOW64') }
            )
            $failed = @()
            foreach ($pair in $pairs) {
                if (-not (Test-Path -LiteralPath $pair.Source)) { continue }
                foreach ($file in Get-ChildItem -LiteralPath $pair.Source -File) {
                    try {
                        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $pair.Target $file.Name) -Force
                    } catch {
                        $failed += ($file.Name + '：' + $_.Exception.Message)
                    }
                }
            }
            if ($failed.Count -gt 0) {
                Write-Host ('部分文件恢复失败：' + ($failed -join '；')) -ForegroundColor Red
                exit 2
            }
            Write-Host '备份 DLL 已写回；ACL/属主未自动还原（原值见 acl.json）。若文件被占用，请重启后重新运行本脚本。' -ForegroundColor Green
            """;
        File.WriteAllText(Path.Combine(directory, "restore.ps1"), restore, new UTF8Encoding(true));
    }

    private static void WriteInventory(string directory, Vc14ForceReplacePreview preview, string windowsRoot,
        IReadOnlyDictionary<string, string> backups, IReadOnlyDictionary<string, string> currentHashes,
        IReadOnlyDictionary<string, string> backupHashes,
        IReadOnlyDictionary<string, Vc14FileAcl> aclSnapshots,
        IReadOnlyList<Vc14ForceReplaceRegistryExport> registryExports,
        IReadOnlyList<Vc14ForceReplaceOutcome> outcomes)
    {
        var items = preview.Items.Select(item =>
        {
            var key = Key(item.Architecture, item.File);
            var outcome = outcomes.FirstOrDefault(o => o.Architecture == item.Architecture && o.File == item.File);
            aclSnapshots.TryGetValue(key, out var acl);
            acl ??= Vc14FileAcl.Unknown;
            return new
            {
                item.Architecture, item.File,
                Target = TargetPath(windowsRoot, item.Architecture, item.File),
                item.CurrentVersion, item.ExpectedVersion, item.Status, item.InUse, item.Downgrade,
                item.AclBroken, item.Locked,
                OwnerSid = acl.OwnerSid,
                Owner = Vc14ForceReplaceAcl.DescribeOwnerSid(acl.OwnerSid),
                GroupSid = acl.GroupSid,
                Group = Vc14ForceReplaceAcl.DescribeOwnerSid(acl.GroupSid),
                AclProtected = acl.DaclProtected,
                acl.UsersCanRead, acl.AdminsCanWrite, AclSddl = acl.Sddl,
                Sha256 = currentHashes.TryGetValue(key, out var sha) ? sha : "",
                Backup = backups.TryGetValue(key, out var backup) ? backup : "",
                BackupSha256 = backupHashes.TryGetValue(key, out var backupSha) ? backupSha : "",
                Outcome = outcome?.Outcome ?? (item.Status is "replace" or "acl" ? "pending" : "skipped"),
                OutcomeDetail = outcome?.Detail ?? "",
                item.Detail,
            };
        }).ToArray();
        var inventory = new
        {
            schema = 2,
            tool = "DeltaNFD", kind = "vc14-force-replace", createdAtUtc = DateTimeOffset.UtcNow,
            baseline = preview.Baseline, expectedVersion = preview.PayloadVersion, windowsRoot,
            preview.Fingerprint, preview.PayloadVersion, preview.ReplaceCount, preview.AclRepairCount,
            aclSnapshotFile = "acl.json", registryExports, items,
        };
        File.WriteAllText(Path.Combine(directory, "inventory.json"),
            JsonSerializer.Serialize(inventory, JsonOptions), new UTF8Encoding(false));
    }

    private static void TryDeleteDirectory(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception ex) { Log.Warn("强制替换：清理临时载荷失败：" + directory + "：" + ex.Message); }
    }

    // ---- 目标与结果 -------------------------------------------------------------

    internal static string TargetDirectory(string windowsRoot, string architecture) => Path.Combine(windowsRoot,
        architecture switch { "x64" => "System32", "x86" => "SysWOW64", _ => throw new IOException("架构无效：" + architecture) });

    internal static string TargetPath(string windowsRoot, string architecture, string file) =>
        Path.Combine(TargetDirectory(windowsRoot, architecture), file);

    private static string? ReadVersion(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).FileVersion; }
        catch (Exception) { return null; }
    }

    private static bool IsExpectedVersion(string? version, string expectedVersion) =>
        Version.TryParse(version, out var parsed) && Version.TryParse(expectedVersion, out var expected)
        && parsed.Major == expected.Major && parsed.Minor == expected.Minor && parsed.Build == expected.Build;

    private static bool IsNewerThanExpected(string version, string expectedVersion) =>
        Version.TryParse(version, out var parsed) && Version.TryParse(expectedVersion, out var expected) && parsed > expected;

    private static bool IsInUse(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return false; } // 权限/ACL 问题不是占用，单独由 AclBroken/Locked 表达
    }

    private static string Key(string architecture, string file) => architecture + "|" + file;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string existingFileName, string? newFileName, uint flags);
}
