using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace DeltaNFD.Services.AlphaRestore;

/// <summary>
/// 显式高风险操作：仅安全模式使用，卸载检测到的全部 C++ 运行库（2005–2013 / 2015–2022 / 14.x），
/// 供回正常模式后由全量修复重新安装。不是默认全量修复流程；依赖阻塞、身份变化或结果未知时停止。
/// </summary>
public static class RuntimeSafeUninstall
{
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(90);

    public static Task<RuntimeSafeUninstallPreview> PreviewAsync(CancellationToken token) => Task.Run(async () =>
    {
        if (!Vc14InstallerReadiness.IsSafeMode)
            return new(false, "该操作只在 Windows 安全模式下可用；请先通过全量修复的安全模式引导进入。", "", []);
        if (!ElevationHelper.IsElevated)
            return new(false, ElevationHelper.NotElevatedMessage, "", []);
        token.ThrowIfCancellationRequested();
        var scan = await new RuntimeGuardService().ScanVcRedistsAsync().WaitAsync(token);
        return BuildPreview(scan, token);
    }, token);

    private static RuntimeSafeUninstallPreview BuildPreview(VcRedistScanReport scan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        IReadOnlyList<VcRedistEntry> candidates;
        try { candidates = RuntimeBulkUninstall.Preview(scan.All).Entries; }
        catch (Exception ex) { return new(false, "无法生成 C++ 卸载清单：" + ex.Message, "", []); }
        if (candidates.Count == 0)
            return new(false, "未发现可安全核验的 C++ 运行库登记，未执行卸载。", "", []);

        SafeInstallerPreparation preparation;
        try { preparation = new SafeInstallerEngine(new SafeInstallerWindowsPlatform(), new SafeInstallerFileStore(AppDataPaths.Root)).Preview(); }
        catch (Exception ex) { return new(false, "无法核对临时 Installer 状态：" + ex.Message, "", []); }

        var blocker = "";
        if (preparation.Required)
        {
            if (!preparation.CanEnable) blocker = preparation.Detail;
        }
        else if (Vc14InstallerReadiness.Check() is { } installer) blocker = installer;
        if (blocker.Length == 0)
        {
            try { blocker = RuntimeRepairPreflight.Read().Blocker ?? ""; }
            catch (Exception ex) { blocker = "运行库卸载环境无法确认：" + ex.Message; }
        }

        var entries = candidates.Select(e => new RuntimeSafeUninstallEntry(e.DisplayName, e.Architecture,
            string.IsNullOrWhiteSpace(e.DisplayVersion) ? "未知" : e.DisplayVersion, e.UninstallKind)).ToArray();
        return new RuntimeSafeUninstallPreview(blocker.Length == 0, blocker,
            Fingerprint(entries, preparation), entries)
        {
            RequiresTemporaryInstaller = preparation.Required,
            TemporaryInstallerCanEnable = preparation.CanEnable,
            TemporaryInstallerDetail = preparation.Detail,
        };
    }

    private static string Fingerprint(IReadOnlyList<RuntimeSafeUninstallEntry> entries, SafeInstallerPreparation preparation) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Entries = entries,
            preparation.Required,
            preparation.CanEnable,
            preparation.Fingerprint,
        })));

    public static Task<OperationResult> RunAsync(string fingerprint, bool allowTemporaryInstaller,
        IProgress<string>? progress, CancellationToken token)
        => Task.Run(() => RuntimeGuardProtection.Exclusive(() =>
            RunCoreAsync(fingerprint, allowTemporaryInstaller, progress, token).GetAwaiter().GetResult()), token);

    private static async Task<OperationResult> RunCoreAsync(string fingerprint, bool allowTemporaryInstaller,
        IProgress<string>? progress, CancellationToken token)
    {
        if (!Vc14InstallerReadiness.IsSafeMode)
            return OperationResult.Fail("该操作只在 Windows 安全模式下可用。");
        if (!ElevationHelper.IsElevated) return OperationResult.Fail(ElevationHelper.NotElevatedMessage);

        RuntimeRepairDiagnostics diagnostics;
        try { diagnostics = new RuntimeRepairDiagnostics(AppDataPaths.Root); }
        catch (Exception ex) { return OperationResult.Fail("无法创建运行库诊断目录，未执行卸载：" + ex.Message); }

        OperationResult? result = null;
        var temporary = false;
        OperationResult? restore = null;
        try
        {
            var service = new RuntimeGuardService();
            var before = await service.ScanVcRedistsAsync().WaitAsync(token);
            var preview = BuildPreview(before, token);
            if (!preview.CanRun) return Fail(diagnostics, preview.Blocker);
            if (!string.Equals(fingerprint, preview.Fingerprint, StringComparison.Ordinal))
                return Fail(diagnostics, "C++ 卸载清单或 Installer 状态已变化，请重新预览确认。");

            var safe = new SafeInstallerEngine(new SafeInstallerWindowsPlatform(), new SafeInstallerFileStore(AppDataPaths.Root));
            var preparation = safe.Preview();
            try
            {
                if (preparation.Required)
                {
                    if (!allowTemporaryInstaller)
                        return Fail(diagnostics, "必须明确同意临时启用 Windows Installer 后才能安全模式卸载全部 C++。");
                    if (!preparation.CanEnable)
                        return Fail(diagnostics, "无法临时启用 Windows Installer：" + preparation.Detail);
                    temporary = true; // Restore even if Enable throws after its durable journal was written.
                    safe.Enable(preparation.Fingerprint, true, token);
                }
                else if (Vc14InstallerReadiness.Check() is { } installer)
                {
                    return Fail(diagnostics, installer);
                }

                var environment = new RuntimeRepairEnvironment(service, diagnostics);
                var clock = Stopwatch.StartNew();
                TimeSpan Remaining(TimeSpan maximum)
                {
                    token.ThrowIfCancellationRequested();
                    var left = Budget - clock.Elapsed;
                    if (left <= TimeSpan.Zero) throw new OperationCanceledException(token);
                    return left < maximum ? left : maximum;
                }

                var removal = await RuntimePreUninstall.RunAsync(before,
                    (entries, ct) => environment.PrepareUninstallBatchAsync(entries, ct),
                    (entry, timeout, p, ct) => environment.ExecuteAsync(
                        new RuntimeRepairStep(entry.KeyName, entry.DisplayName, entry.Architecture, "uninstall", "", entry),
                        timeout, p, ct),
                    ct => environment.ScanAsync(ct),
                    Remaining, diagnostics, progress, token);

                result = MapRemoval(removal, diagnostics);
            }
            finally
            {
                if (temporary)
                {
                    try { restore = safe.Restore(); }
                    catch (Exception ex)
                    {
                        restore = new OperationResult
                        {
                            Success = false, OutcomeUnknown = true,
                            Message = "临时 Installer 恢复失败，记录保留：" + ex.Message,
                        };
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            result = new OperationResult
            {
                Success = false, OutcomeUnknown = false, DiagnosticsDirectory = diagnostics.DirectoryPath,
                Message = "C++ 全量卸载已取消；已完成的卸载不会自动回滚。请核对诊断目录后回正常模式重新确认。",
            };
        }
        catch (Exception ex)
        {
            result = new OperationResult
            {
                Success = false, OutcomeUnknown = true, IsPartial = true, DiagnosticsDirectory = diagnostics.DirectoryPath,
                Message = "C++ 全量卸载未完成：" + ex.Message + "\n已执行的卸载无法自动回滚；请核对诊断目录，不要重复强制卸载。",
            };
        }

        if (restore is not null)
        {
            result ??= OperationResult.Fail("C++ 全量卸载结果未确认。");
            result = new OperationResult
            {
                Success = result.Success && restore.Success,
                IsPartial = result.IsPartial || !restore.Success,
                OutcomeUnknown = result.OutcomeUnknown || !restore.Success,
                RequiresReboot = result.RequiresReboot,
                DiagnosticsDirectory = result.DiagnosticsDirectory,
                Message = result.Message + "\n" + restore.Message,
            };
        }
        return result ?? OperationResult.Fail("C++ 全量卸载结果未确认。");
    }

    private static OperationResult Fail(RuntimeRepairDiagnostics diagnostics, string message) => new()
    {
        Success = false, DiagnosticsDirectory = diagnostics.DirectoryPath, Message = message,
    };

    private static OperationResult MapRemoval(RuntimeRemovalResult removal, RuntimeRepairDiagnostics diagnostics)
    {
        var directory = diagnostics.DirectoryPath;
        if (removal.Stopped is { } stopped)
        {
            return new OperationResult
            {
                Success = false,
                IsPartial = removal.ExecutionStarted || removal.Completed > 0,
                OutcomeUnknown = stopped.OutcomeUnknown,
                RequiresReboot = removal.Reboot || stopped.RequiresReboot,
                DiagnosticsDirectory = directory,
                Message = $"C++ 全量卸载未完成：已处理 {removal.Completed} 项，随后停止。\n{stopped.Details}\n请核对诊断目录；不要重复强制卸载。",
            };
        }
        if (removal.Reboot)
        {
            return new OperationResult
            {
                Success = false, IsPartial = true, RequiresReboot = true, DiagnosticsDirectory = directory,
                Message = $"已处理 {removal.Completed} 项 C++ 运行库，但卸载器要求重启；未执行后续安装。请重启后回正常模式重新预览全量修复。",
            };
        }
        return new OperationResult
        {
            Success = true, DiagnosticsDirectory = directory,
            Message = $"已在安全模式卸载 {removal.Completed} 项 C++ 运行库（2005–2013 / 2015–2022 / 14.x，按核验清单处理）。"
                + "\n请手动重启回正常模式，再点击“预览全量修复”重新确认，由正常模式安装 C++ 基线；不会自动重启或续跑。",
        };
    }
}
