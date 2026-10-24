using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeltaNFD.Services.AlphaRestore;

namespace DeltaNFD.Services;

/// <summary>内置官方 14.42.34433 载荷中的一个 DLL（清单固定大小与 SHA256）。</summary>
internal sealed record Vc14ForceReplacePayload(string Architecture, string File, long Bytes, string Sha256);

/// <summary>写入结果：replaced / renamed / scheduled / failed / cancelled。</summary>
internal sealed record Vc14ForceReplaceOutcome(string Architecture, string File, string Outcome, string Detail);

/// <summary>
/// 显式高风险操作：绕过 Windows Installer 的“较新共享文件不覆盖”规则，直接用内置官方
/// 14.42.34433 文件覆盖 System32/SysWOW64 的 51 个 14.x DLL（x64 26 / x86 25）。
/// 先备份再替换；被占用时先尝试改名替换，仍失败则登记到下次启动。不修改 MSI/ARP 安装登记。
/// 仅正常模式、管理员、无待核对事务时可用；预览指纹变化后拒绝执行。
/// </summary>
public static class Vc14ForceReplace
{
    internal const string ExpectedVersion = "14.42.34433.0";
    private const string CatalogResource = "Vc14ForceReplace.catalog.json";
    private const int PayloadCount = 51;
    private const uint MoveFileReplaceExisting = 0x1;
    private const uint MoveFileDelayUntilReboot = 0x4;
    private static readonly TimeSpan OperationBudget = TimeSpan.FromMinutes(15);

    private static readonly Lazy<IReadOnlyList<Vc14ForceReplacePayload>> PayloadLazy = new(LoadPayload);
    private static readonly Lazy<string> PayloadFingerprintLazy = new(ComputePayloadFingerprint);

    internal static IReadOnlyList<Vc14ForceReplacePayload> Payload => PayloadLazy.Value;
    internal static string PayloadFingerprint => PayloadFingerprintLazy.Value;
    internal static string WindowsRoot => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>只读预览：核对内置载荷、当前 51 个 DLL 版本与占用情况，生成执行指纹。</summary>
    public static Vc14ForceReplacePreview Preview(CancellationToken token = default) =>
        PreviewCore(Vc14FileRepairSource.InstalledRoot, WindowsRoot, enforceHostChecks: true, versionReader: null, token);

    /// <summary>执行强制替换；fingerprint 必须来自本次预览。</summary>
    public static Task<OperationResult> RunAsync(string fingerprint, IProgress<string>? progress, CancellationToken token) =>
        Task.Run(() => RuntimeGuardProtection.Exclusive(() =>
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(OperationBudget);
            return RunCoreAsync(fingerprint, progress, budget.Token, Vc14FileRepairSource.InstalledRoot, WindowsRoot,
                enforceHostChecks: true, versionReader: null).GetAwaiter().GetResult();
        }), token);

    // ---- 预览 -----------------------------------------------------------------

    internal static Vc14ForceReplacePreview PreviewCore(string sourceRoot, string windowsRoot, bool enforceHostChecks,
        Func<string, string?>? versionReader, CancellationToken token)
    {
        versionReader ??= ReadVersion;
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

        try { VerifyMedia(sourceRoot); }
        catch (Exception ex) { if (blocker.Length == 0) blocker = "内置官方 14.42 载荷无法核验：" + ex.Message; }

        var items = new List<Vc14ForceReplaceItem>(Payload.Count);
        foreach (var payload in Payload)
        {
            token.ThrowIfCancellationRequested();
            var target = TargetPath(windowsRoot, payload.Architecture, payload.File);
            var current = "";
            var status = "replace";
            var detail = "";
            var inUse = false;
            var downgrade = false;
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
                    if (IsExpectedVersion(current)) { status = "ok"; detail = "已是 " + ExpectedVersion + "。"; }
                    else if (current.Length == 0) detail = "版本无法读取；将尝试替换并复检。";
                    else if (IsNewerThanExpected(current))
                    {
                        downgrade = true;
                        detail = "当前 " + current + " 较新；强制替换会降级到 " + ExpectedVersion + "。";
                    }
                    else detail = "当前 " + current + "；将替换为 " + ExpectedVersion + "。";
                    inUse = IsInUse(target);
                }
            }
            catch (Exception ex) { status = "blocked"; detail = "无法检查目标文件：" + ex.Message; }
            items.Add(new(payload.Architecture, payload.File, current, ExpectedVersion, status, detail, inUse, downgrade));
        }

        token.ThrowIfCancellationRequested();
        var absent = items.Count(i => i.Status == "absent");
        var blocked = items.Count(i => i.Status == "blocked");
        if (blocker.Length == 0 && absent > 0)
            blocker = $"有 {absent} 个 14.x DLL 不存在，强制替换不会创建文件；请先用官方安装器安装 14.42 基线。";
        if (blocker.Length == 0 && blocked > 0)
            blocker = $"有 {blocked} 个 14.x DLL 无法检查或是链接，已停止。";

        return new Vc14ForceReplacePreview(
            CanRun: blocker.Length == 0,
            Blocker: blocker,
            Fingerprint: ComputeFingerprint(items, windowsRoot),
            Items: items,
            PayloadVersion: ExpectedVersion,
            ReplaceCount: items.Count(i => i.Status == "replace"),
            SkipCount: items.Count(i => i.Status == "ok"),
            AbsentCount: absent,
            DowngradeCount: items.Count(i => i.Downgrade));
    }

    private static string ComputeFingerprint(IReadOnlyList<Vc14ForceReplaceItem> items, string windowsRoot)
    {
        var text = new StringBuilder();
        text.Append("root:").Append(Path.GetFullPath(windowsRoot).ToLowerInvariant()).Append('\n');
        text.Append("payload:").Append(PayloadFingerprint).Append('\n');
        foreach (var item in items)
            text.Append(item.Architecture).Append('|').Append(item.File).Append('|')
                .Append(item.CurrentVersion).Append('|').Append(item.Status).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    // ---- 执行 -----------------------------------------------------------------

    internal static async Task<OperationResult> RunCoreAsync(string fingerprint, IProgress<string>? progress, CancellationToken token,
        string sourceRoot, string windowsRoot, bool enforceHostChecks, Func<string, string?>? versionReader,
        Action<string>? afterReplacementForTests = null)
    {
        var preview = PreviewCore(sourceRoot, windowsRoot, enforceHostChecks, versionReader, token);
        if (!preview.CanRun) return OperationResult.Fail(preview.Blocker);
        if (!string.Equals(preview.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            return OperationResult.Fail("14.x DLL 清单已变化，请重新预览确认；本次未修改任何文件。");
        if (preview.ReplaceCount == 0)
            return OperationResult.Ok("51 个 14.x DLL 已全部为 " + ExpectedVersion + "，无需强制替换。");

        token.ThrowIfCancellationRequested();
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var directory = Path.Combine(AppDataPaths.Root, "Vc14ForceReplace", stamp + "-" + Guid.NewGuid().ToString("N")[..8]);
        try { Directory.CreateDirectory(directory); }
        catch (Exception ex) { return OperationResult.Fail("无法创建备份目录，未修改系统文件：" + ex.Message); }

        var replaceItems = preview.Items.Where(i => i.Status == "replace").ToArray();
        var backups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        progress?.Report("正在备份当前 14.x DLL 到 " + directory + "…");
        try
        {
            token.ThrowIfCancellationRequested();
            foreach (var item in replaceItems)
            {
                var target = TargetPath(windowsRoot, item.Architecture, item.File);
                var backup = Path.Combine(directory, item.Architecture, item.File);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(target, backup, overwrite: false);
                backups[Key(item.Architecture, item.File)] = backup;
            }
        }
        catch (Exception ex)
        {
            return new OperationResult
            {
                Success = false, DiagnosticsDirectory = directory,
                Message = "备份未完成，未修改任何系统文件：" + ex.Message,
            };
        }

        WriteReadme(directory, windowsRoot, preview);
        WriteInventory(directory, preview, windowsRoot, backups, []);
        var outcomes = new List<Vc14ForceReplaceOutcome>();
        var attempted = new List<Vc14ForceReplaceItem>();
        var payloadRoot = Path.Combine(Path.GetTempPath(), "DeltaNFD-Vc14ForceReplace-" + Guid.NewGuid().ToString("N"));
        try
        {
            progress?.Report("正在核验并解包内置官方 14.42.34433 载荷…");
            var payload = ExtractPayload(sourceRoot, payloadRoot, token);
            token.ThrowIfCancellationRequested();
            var stagingRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp",
                "DeltaNFD", "Vc14ForceReplace", stamp + "-" + Guid.NewGuid().ToString("N")[..8]);
            var index = 0;
            foreach (var item in replaceItems)
            {
                token.ThrowIfCancellationRequested();
                index++;
                progress?.Report($"正在替换 {item.Architecture} {item.File}（{index}/{replaceItems.Length}）…");
                var target = TargetPath(windowsRoot, item.Architecture, item.File);
                var source = payload[Key(item.Architecture, item.File)];
                try
                {
                    attempted.Add(item); // Copy/rename can change bytes even when subsequent verification fails.
                    var action = ApplyReplacement(source, target, stagingRoot, item.Architecture, item.File);
                    afterReplacementForTests?.Invoke(target);
                    if (action is "replaced" or "renamed")
                    {
                        var actual = ReadVersion(target) ?? "";
                        if (!IsExpectedVersion(actual))
                            throw new IOException("写入后版本为 " + (actual.Length == 0 ? "无法读取" : actual) + "，不是 " + ExpectedVersion + "。");
                    }
                    outcomes.Add(new(item.Architecture, item.File, action, ""));
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

        var failed = outcomes.Where(o => o.Outcome == "failed").ToArray();
        var cancelled = outcomes.Any(o => o.Outcome == "cancelled");
        var scheduled = outcomes.Count(o => o.Outcome == "scheduled");
        var changed = outcomes.Count(o => o.Outcome is "replaced" or "renamed" or "scheduled");
        var replaced = outcomes.Count(o => o.Outcome is "replaced" or "renamed");

        var rollback = "";
        var rollbackFailed = false;
        var rollbackComplete = false;
        if (failed.Length > 0 && scheduled == 0 && attempted.Count > 0)
        {
            var failures = new List<string>();
            foreach (var item in attempted)
            {
                var target = TargetPath(windowsRoot, item.Architecture, item.File);
                try
                {
                    var backup = backups[Key(item.Architecture, item.File)];
                    File.Copy(backup, target, overwrite: true);
                    using var original = File.OpenRead(backup);
                    var digest = Convert.ToHexString(SHA256.HashData(original));
                    if (!HashMatches(target, digest)) throw new IOException("恢复后字节校验不一致。");
                    outcomes.RemoveAll(o => o.Architecture == item.Architecture && o.File == item.File);
                    outcomes.Add(new(item.Architecture, item.File, "restored", "本次尝试已按备份恢复并核验字节。"));
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
                ? "已用本目录备份自动回滚本次已写入的文件。"
                : "部分文件自动回滚失败：" + string.Join("；", failures);
        }

        var inventoryFailed = false;
        try { WriteInventory(directory, preview, windowsRoot, backups, outcomes); }
        catch (Exception ex) { inventoryFailed = true; Log.Error("强制替换：最终 inventory.json 写入失败，保留待核对", ex); }

        var reboot = scheduled > 0;
        var message = new StringBuilder();
        message.Append("强制替换 14.x：已替换 ").Append(replaced).Append(" 个");
        if (scheduled > 0) message.Append("，安排重启后替换 ").Append(scheduled).Append(" 个");
        message.Append("，失败 ").Append(failed.Length).Append(" 个，跳过 ").Append(preview.SkipCount).Append(" 个（已是目标版本）。");
        message.Append("\n备份与恢复脚本：").Append(directory);
        foreach (var failure in failed)
            message.Append("\n失败：").Append(failure.Architecture).Append(' ').Append(failure.File).Append(" · ").Append(failure.Detail);
        if (cancelled) message.Append("\n操作已取消；已替换的文件保留在备份目录，可用 restore.ps1 恢复。");
        if (rollback.Length > 0) message.Append('\n').Append(rollback);
        if (inventoryFailed) message.Append("\n最终恢复清单保存失败，备份保留，请人工核对后再继续。");
        if (reboot)
            message.Append("\n有文件被占用，已登记到下次启动替换；请重启后再运行“强制替换 14.x”预览确认。重启前不要清理系统 Temp。");
        message.Append("\n安装登记（MSI/ARP）不会改变；如需登记一致，请回正常模式执行“仅修复 C++ 14.x”。");

        if (failed.Length == 0 && !cancelled && !inventoryFailed)
        {
            return new OperationResult
            {
                Success = true, IsPartial = reboot, RequiresReboot = reboot,
                DiagnosticsDirectory = directory, Message = message.ToString(),
            };
        }
        return new OperationResult
        {
            Success = false, IsPartial = !rollbackComplete && (attempted.Count > 0 || changed > 0), RequiresReboot = reboot,
            OutcomeUnknown = rollbackFailed || inventoryFailed,
            DiagnosticsDirectory = directory, Message = message.ToString(),
        };
    }

    /// <summary>优先直接覆盖；失败则改名为目标（可绕过已加载 DLL 的写共享限制）；仍失败则登记重启后替换。</summary>
    private static string ApplyReplacement(string source, string target, string stagingRoot, string architecture, string file)
    {
        try { File.Copy(source, target, overwrite: true); return "replaced"; }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        var staged = Path.Combine(stagingRoot, architecture, file);
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.Copy(source, staged, overwrite: true);
        try
        {
            // 改名/重启替换会把暂存文件搬进 System32，必须把目标 DLL 的 ACL 一并带过去，
            // 否则新文件会继承暂存目录（Windows\Temp）的 ACL。复制失败时拒绝替换。
            var security = FileSystemAclExtensions.GetAccessControl(new FileInfo(target), AccessControlSections.Access);
            FileSystemAclExtensions.SetAccessControl(new FileInfo(staged), security);
        }
        catch (Exception ex)
        {
            try { File.Delete(staged); } catch { }
            throw new IOException("无法复制目标 DLL 的访问控制，已拒绝替换以免留下错误 ACL：" + ex.Message);
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

    private static IReadOnlyList<Vc14ForceReplacePayload> LoadPayload()
    {
        using var stream = typeof(Vc14ForceReplace).Assembly.GetManifestResourceStream(CatalogResource)
            ?? throw new IOException("缺少强制替换载荷清单。");
        var files = JsonSerializer.Deserialize<Vc14ForceReplacePayload[]>(stream) ?? throw new IOException("强制替换载荷清单无效。");
        if (files.Length != PayloadCount
            || files.Count(f => f.Architecture == "x64") != 26
            || files.Count(f => f.Architecture == "x86") != 25
            || files.Any(f => f.Architecture is not ("x64" or "x86") || string.IsNullOrWhiteSpace(f.File)
                || f.File.Contains('\\') || f.File.Contains('/') || f.Bytes <= 0 || f.Sha256.Length != 64))
            throw new IOException("强制替换载荷清单范围无效。");
        if (files.Select(f => Key(f.Architecture, f.File)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length)
            throw new IOException("强制替换载荷清单存在重复项。");
        return files;
    }

    private static string ComputePayloadFingerprint() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("\n", Payload.OrderBy(p => p.Architecture, StringComparer.Ordinal)
            .ThenBy(p => p.File, StringComparer.OrdinalIgnoreCase)
            .Select(p => $"{p.Architecture}|{p.File}|{p.Bytes}|{p.Sha256}")))));

    private static void VerifyMedia(string sourceRoot)
    {
        Vc14FileRepairSource.Verify(sourceRoot, "x64");
        Vc14FileRepairSource.Verify(sourceRoot, "x86");
    }

    /// <summary>从四个官方 CAB 解包并用固定清单逐项校验；返回 arch|file → 解包文件路径。</summary>
    internal static IReadOnlyDictionary<string, string> ExtractPayload(string sourceRoot, string destination, CancellationToken token)
    {
        VerifyMedia(sourceRoot);
        var expected = Payload.ToDictionary(p => Key(p.Architecture, p.File), StringComparer.OrdinalIgnoreCase);
        var extracted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var architecture in new[] { "x64", "x86" })
        {
            foreach (var package in Vc14FileRepairSource.Packages.Where(p => p.Architecture == architecture))
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
        if (extracted.Count != Payload.Count)
            throw new IOException($"内置载荷数量不符：{extracted.Count}/{Payload.Count}。");
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

    private static bool IsExpectedVersion(string? version) =>
        Version.TryParse(version, out var parsed) && parsed.Major == 14 && parsed.Minor == 42 && parsed.Build == 34433;

    private static bool IsNewerThanExpected(string version) =>
        Version.TryParse(version, out var parsed) && Version.TryParse(ExpectedVersion, out var expected) && parsed > expected;

    private static bool IsInUse(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static string Key(string architecture, string file) => architecture + "|" + file;

    private static void WriteInventory(string directory, Vc14ForceReplacePreview preview, string windowsRoot,
        IReadOnlyDictionary<string, string> backups, IReadOnlyList<Vc14ForceReplaceOutcome> outcomes)
    {
        var items = preview.Items.Select(item =>
        {
            var outcome = outcomes.FirstOrDefault(o => o.Architecture == item.Architecture && o.File == item.File);
            return new
            {
                item.Architecture, item.File,
                Target = TargetPath(windowsRoot, item.Architecture, item.File),
                item.CurrentVersion, item.ExpectedVersion, item.Status, item.InUse, item.Downgrade, item.Detail,
                Backup = backups.TryGetValue(Key(item.Architecture, item.File), out var backup) ? backup : "",
                Outcome = outcome?.Outcome ?? (item.Status == "replace" ? "pending" : "skipped"),
                OutcomeDetail = outcome?.Detail ?? "",
            };
        }).ToArray();
        var inventory = new
        {
            tool = "DeltaNFD", kind = "vc14-force-replace", createdAtUtc = DateTimeOffset.UtcNow,
            expectedVersion = ExpectedVersion, windowsRoot, preview.Fingerprint, preview.PayloadVersion, items,
        };
        File.WriteAllText(Path.Combine(directory, "inventory.json"),
            JsonSerializer.Serialize(inventory, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    private static void WriteReadme(string directory, string windowRoot, Vc14ForceReplacePreview preview)
    {
        var readme = $"""
            DeltaNFD 强制替换 14.x 备份目录
            ================================

            本目录保存了强制替换前的 14.x DLL，以及 inventory.json、restore.ps1。
            目标：用内置官方 14.42.34433 文件覆盖 %SystemRoot%\System32 与 %SystemRoot%\SysWOW64 中的
            14.x DLL（本次预览待替换 {preview.ReplaceCount} 个）。

            恢复方法（需要管理员）：
              1. 如果本次结果提示“已登记到下次启动替换”，先重启 Windows 让替换完成。
              2. 右键“以管理员身份运行”本目录中的 restore.ps1。
              3. 恢复后重新运行 DeltaNFD 的 14.x 检测。

            注意：
              - 强制替换不改变 Windows Installer/ARP 的安装登记；文件与登记可能不一致。
              - 恢复只写回文件字节，不还原 ACL、SharedDLLs 计数或已登记的待重启操作。
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
            Write-Host '备份 DLL 已写回。若文件被占用，请重启后重新运行本脚本。' -ForegroundColor Green
            """;
        File.WriteAllText(Path.Combine(directory, "restore.ps1"), restore, new UTF8Encoding(true));
    }

    private static void TryDeleteDirectory(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception ex) { Log.Warn("强制替换：清理临时载荷失败：" + directory + "：" + ex.Message); }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string existingFileName, string? newFileName, uint flags);
}
