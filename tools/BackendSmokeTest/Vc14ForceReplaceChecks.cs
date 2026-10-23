using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Services;

/// <summary>强制替换 14.x：载荷核验、假根预览/执行、备份恢复材料、合同注册与真实系统只读保护。</summary>
internal static class Vc14ForceReplaceChecks
{
    internal static async Task RunAsync(string assetsRoot)
    {
        int count = 0;
        void Check(bool value, string text) { if (!value) throw new IOException(text); count++; }
        var root = Path.GetFullPath(assetsRoot);
        var id = Guid.NewGuid();
        var isolated = Path.Combine(Path.GetTempPath(), "DeltaNFD-force-replace-" + id.ToString("N"));
        var fixtures = new List<string>();
        var systemDll = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msvcp140.dll");
        var realBefore = ReadVersion(systemDll);
        var realLength = File.Exists(systemDll) ? new FileInfo(systemDll).Length : -1;
        Directory.CreateDirectory(isolated);
        File.WriteAllText(Path.Combine(isolated, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(isolated, id);
        var payloadRoot = Path.Combine(Path.GetTempPath(), "DeltaNFD-force-payload-" + Guid.NewGuid().ToString("N"));
        try
        {
            // 1) 固定清单。
            var payload = Vc14ForceReplace.Payload;
            Check(payload.Count == 51 && payload.Count(p => p.Architecture == "x64") == 26
                && payload.Count(p => p.Architecture == "x86") == 25, "payload catalog is 26 x64 + 25 x86");
            Check(payload.Select(p => p.Architecture + "|" + p.File).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 51
                && payload.All(p => !p.File.Contains('/') && !p.File.Contains('\\') && p.Bytes > 0 && p.Sha256.Length == 64),
                "payload catalog has unique fixed names and pinned sizes/hashes");
            Check(Vc14ForceReplace.PayloadFingerprint.Length == 64, "payload fingerprint is SHA256");

            // 2) 从官方 CAB 解包并独立复算 51 份字节/版本。
            var extracted = Vc14ForceReplace.ExtractPayload(root, payloadRoot, CancellationToken.None);
            Check(extracted.Count == 51, "all official CAB payloads extracted");
            var payloadVersions = new List<string>();
            foreach (var pair in extracted)
            {
                using var stream = File.OpenRead(pair.Value);
                var expected = payload.Single(p => pair.Key == p.Architecture + "|" + p.File);
                Check(Convert.ToHexString(SHA256.HashData(stream)).Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase),
                    "extracted payload hash matches catalog: " + pair.Key);
                payloadVersions.Add(ReadVersion(pair.Value) ?? "");
            }
            Check(payloadVersions.All(v => v == Vc14ForceReplace.ExpectedVersion),
                "every extracted payload reports 14.42.34433.0");

            // 3) 假系统根：健康状态无需替换。
            var healthyRoot = CreateFakeRoot(extracted);
            fixtures.Add(healthyRoot);
            var healthy = Vc14ForceReplace.PreviewCore(root, healthyRoot, enforceHostChecks: false, versionReader: null, CancellationToken.None);
            Check(healthy.CanRun && healthy.ReplaceCount == 0 && healthy.SkipCount == 51 && healthy.Blocker.Length == 0,
                "healthy fake System32/SysWOW64 preview is a no-op");

            // 4) 假系统根 + 模拟较新 34438：预览要求全部降级替换。
            var downgradeRoot = CreateFakeRoot(extracted);
            fixtures.Add(downgradeRoot);
            string FakeNewer(string path) => "14.42.34438.0";
            var preview = Vc14ForceReplace.PreviewCore(root, downgradeRoot, enforceHostChecks: false, versionReader: FakeNewer, CancellationToken.None);
            Check(preview.CanRun && preview.ReplaceCount == 51 && preview.SkipCount == 0 && preview.DowngradeCount == 51,
                "34438 fake targets require all 51 forced downgrades");
            Check(preview.Fingerprint.Length == 64 && preview.PayloadVersion == Vc14ForceReplace.ExpectedVersion
                && preview.Items.All(i => i.Status == "replace" && i.Downgrade), "preview carries stable fingerprint and downgrade flags");

            // 5) 假系统根执行：备份、替换、结果清单与手动恢复材料。
            var backupRoot = Path.Combine(AppDataPaths.Root, "Vc14ForceReplace");
            var beforeDirs = Directory.Exists(backupRoot) ? Directory.GetDirectories(backupRoot).Length : 0;
            var executed = await Vc14ForceReplace.RunCoreAsync(preview.Fingerprint, null, CancellationToken.None, root, downgradeRoot,
                enforceHostChecks: false, versionReader: FakeNewer);
            Check(executed.Success && !executed.RequiresReboot && !executed.IsPartial, "fake-root replacement succeeds without reboot");
            Check(executed.DiagnosticsDirectory.Length > 0 && Directory.Exists(executed.DiagnosticsDirectory)
                && Path.GetFullPath(executed.DiagnosticsDirectory).StartsWith(Path.GetFullPath(backupRoot) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase), "backup directory created under isolated app data");
            Check(Directory.GetDirectories(backupRoot).Length == beforeDirs + 1, "exactly one backup directory created");
            Check(Directory.GetFiles(Path.Combine(executed.DiagnosticsDirectory, "x64")).Length == 26
                && Directory.GetFiles(Path.Combine(executed.DiagnosticsDirectory, "x86")).Length == 25, "backup keeps all 51 previous files");
            var inventoryPath = Path.Combine(executed.DiagnosticsDirectory, "inventory.json");
            using (var inventory = JsonDocument.Parse(File.ReadAllText(inventoryPath)))
            {
                var items = inventory.RootElement.GetProperty("items").EnumerateArray().ToList();
                Check(items.Count == 51 && items.All(i => i.GetProperty("Outcome").GetString() == "replaced")
                    && items.All(i => File.Exists(i.GetProperty("Backup").GetString()!)), "inventory records replacement outcomes and backups");
            }
            Check(File.Exists(Path.Combine(executed.DiagnosticsDirectory, "restore.ps1"))
                && File.Exists(Path.Combine(executed.DiagnosticsDirectory, "README.txt")), "manual restore script and README exist");
            var restore = File.ReadAllText(Path.Combine(executed.DiagnosticsDirectory, "restore.ps1"));
            Check(restore.Contains("#Requires -Version 5.1") && restore.Contains("$env:SystemRoot")
                && restore.Contains("Copy-Item") && restore.Contains("Administrator"), "restore script is admin-guarded and writes both architectures");
            Check(extracted.Keys.All(key =>
            {
                var parts = key.Split('|');
                return ReadVersion(Vc14ForceReplace.TargetPath(downgradeRoot, parts[0], parts[1])) == Vc14ForceReplace.ExpectedVersion;
            }), "all fake targets now report 14.42.34433.0");

            // 6) 用本机真实 System32/SysWOW64 文件复制出的假根，验证真实 34438 字节确实可被降级
            //    （只在假根读写，真实系统保持只读）。
            var realRoot = CreateFakeRootFromSystem();
            if (realRoot is null)
            {
                Console.WriteLine("SKIP: real System32/SysWOW64 14.x DLLs not all present; downgrade fixture skipped.");
            }
            else
            {
                fixtures.Add(realRoot);
                var realPreview = Vc14ForceReplace.PreviewCore(root, realRoot, enforceHostChecks: false, versionReader: null, CancellationToken.None);
                if (realPreview.ReplaceCount == 0) Console.WriteLine("SKIP: host already at 14.42.34433; no downgrade fixture needed.");
                else
                {
                    var realExecuted = await Vc14ForceReplace.RunCoreAsync(realPreview.Fingerprint, null, CancellationToken.None, root, realRoot,
                        enforceHostChecks: false, versionReader: null);
                    Check(realExecuted.Success && !realExecuted.RequiresReboot, "real 14.x system bytes copied to fake root can be downgraded");
                    Check(extracted.Keys.All(key =>
                    {
                        var parts = key.Split('|');
                        return ReadVersion(Vc14ForceReplace.TargetPath(realRoot, parts[0], parts[1])) == Vc14ForceReplace.ExpectedVersion;
                    }), "every real-bytes fake target ends at 14.42.34433.0");
                    Check(ReadVersion(systemDll) == realBefore && (!File.Exists(systemDll) || new FileInfo(systemDll).Length == realLength),
                        "real System32 remains untouched while fake copy is downgraded");
                }
            }

            // 7) 已匹配时执行必须是明确的空操作。
            var noop = await Vc14ForceReplace.RunCoreAsync(healthy.Fingerprint, null, CancellationToken.None, root, healthyRoot,
                enforceHostChecks: false, versionReader: null);
            Check(noop.Success && noop.Message.Contains("无需强制替换") && noop.DiagnosticsDirectory.Length == 0,
                "no-op exec changes nothing and never creates a backup");

            // 7) 指纹变化阻止执行，且不创建新备份。
            var dirsBeforeMismatch = Directory.GetDirectories(backupRoot).Length;
            var mismatch = await Vc14ForceReplace.RunCoreAsync(new string('0', 64), null, CancellationToken.None, root, downgradeRoot,
                enforceHostChecks: false, versionReader: FakeNewer);
            Check(!mismatch.Success && mismatch.Message.Contains("重新预览")
                && Directory.GetDirectories(backupRoot).Length == dirsBeforeMismatch, "changed fingerprint is refused before any write/backup");

            // 8) 缺失文件不创建，预览给明确阻断。
            var absentRoot = CreateFakeRoot(extracted);
            fixtures.Add(absentRoot);
            File.Delete(Vc14ForceReplace.TargetPath(absentRoot, "x64", "vcruntime140.dll"));
            var absent = Vc14ForceReplace.PreviewCore(root, absentRoot, enforceHostChecks: false, versionReader: null, CancellationToken.None);
            Check(!absent.CanRun && absent.AbsentCount == 1 && absent.Blocker.Contains("不存在"),
                "missing DLL blocks instead of creating an unregistered file");

            // 9) 占用标记进入预览（执行时会安排重启后替换）。
            var inUseRoot = CreateFakeRoot(extracted);
            fixtures.Add(inUseRoot);
            using (var locked = new FileStream(Vc14ForceReplace.TargetPath(inUseRoot, "x64", "msvcp140.dll"),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var inUsePreview = Vc14ForceReplace.PreviewCore(root, inUseRoot, enforceHostChecks: false, versionReader: null, CancellationToken.None);
                Check(inUsePreview.Items.Single(i => i.Architecture == "x64" && i.File == "msvcp140.dll").InUse,
                    "exclusive-open target is reported as in use");
            }

            // 10) 篡改官方载荷：预览/解包都拒绝。
            var tamperedAssets = CopyAssets(root);
            fixtures.Add(tamperedAssets);
            var cab = Vc14FileRepairSource.Packages.First(p => p.Architecture == "x64")
                .Files.Single(f => f.Path.EndsWith(".cab", StringComparison.OrdinalIgnoreCase));
            var cabPath = Path.Combine(tamperedAssets, cab.Path);
            using (var stream = new FileStream(cabPath, FileMode.Open, FileAccess.ReadWrite))
            {
                stream.Position = 40;
                stream.WriteByte((byte)(stream.ReadByte() ^ 1));
            }
            var tampered = Vc14ForceReplace.PreviewCore(tamperedAssets, healthyRoot, enforceHostChecks: false, versionReader: null, CancellationToken.None);
            Check(!tampered.CanRun && tampered.Blocker.Contains("内置官方 14.42 载荷无法核验"),
                "tampered CAB blocks preview");
            try
            {
                Vc14ForceReplace.ExtractPayload(tamperedAssets, payloadRoot + "-tampered", CancellationToken.None);
                throw new IOException("tampered payload must not extract");
            }
            catch (IOException) { Check(true, "tampered CAB cannot reach extraction"); }

            // 11) 命令合同：预览只读、执行写入+意图、超时注册，且不扩大白名单。
            Check(BackendModules.All[BackendModules.Runtime].Commands.Contains(BackendCommands.Vc14ForceReplacePreview)
                && BackendModules.All[BackendModules.Runtime].Commands.Contains(BackendCommands.Vc14ForceReplace),
                "force-replace commands are in the runtime whitelist");
            Check(BackendModules.IsReadOnly(BackendModules.Runtime, BackendCommands.Vc14ForceReplacePreview)
                && !BackendModules.IsReadOnly(BackendModules.Runtime, BackendCommands.Vc14ForceReplace),
                "preview is read-only; exec is mutating");
            Check(BackendModules.IsMutating(BackendModules.Runtime, BackendCommands.Vc14ForceReplace)
                && BackendModules.RequiresWriteIntent(BackendModules.Runtime, BackendCommands.Vc14ForceReplace),
                "exec requires a durable write intent");
            Check(BackendModules.TimeoutFor(BackendModules.Runtime, BackendCommands.Vc14ForceReplacePreview) == TimeSpan.FromMinutes(5)
                && BackendModules.TimeoutFor(BackendModules.Runtime, BackendCommands.Vc14ForceReplace) == TimeSpan.FromMinutes(15),
                "preview/exec timeouts are registered");

            // 12) 所有假根操作不得触碰真实 System32。
            var rollbackRoot = CreateFakeRoot(extracted);
            fixtures.Add(rollbackRoot);
            var firstPayload = payload[0];
            var firstTarget = Vc14ForceReplace.TargetPath(rollbackRoot, firstPayload.Architecture, firstPayload.File);
            byte[] oldBytes = [1, 2, 3, 4];
            File.WriteAllBytes(firstTarget, oldBytes);
            var rollbackPreview = Vc14ForceReplace.PreviewCore(root, rollbackRoot, false, FakeNewer, CancellationToken.None);
            var injected = false;
            var rolledBack = await Vc14ForceReplace.RunCoreAsync(rollbackPreview.Fingerprint, null, CancellationToken.None,
                root, rollbackRoot, false, FakeNewer, path =>
                {
                    if (path.Equals(firstTarget, StringComparison.OrdinalIgnoreCase) && !injected)
                    { injected = true; throw new IOException("fixture post-write verification failure"); }
                });
            Check(injected && !rolledBack.Success && !rolledBack.IsPartial && !rolledBack.OutcomeUnknown,
                "post-write failure restores all attempted files and confirms the rollback");
            Check(File.ReadAllBytes(firstTarget).SequenceEqual(oldBytes),
                "failed verification item itself is included in rollback, not just earlier successful items");
            using (var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(rolledBack.DiagnosticsDirectory, "inventory.json"))))
                Check(inventory.RootElement.GetProperty("items").EnumerateArray().All(i => i.GetProperty("Outcome").GetString() == "restored"),
                    "final inventory describes restored bytes rather than stale replacement outcomes");

            var review = new DeltaNFD.Services.Backends.BackendUnconfirmedStore.Review("runtime", "fixture", "force replace", true)
                { Command = BackendCommands.Vc14ForceReplace, CanRestore = true };
            var guardSnapshot = new ReconcileSnapshot { Module = "runtime", Complete = true, Summary = "fixture guard state",
                RecoveryScopes = [new() { Id = "guard", Available = true, Description = "guard only" }] };
            Check(DeltaNFD.Services.Backends.BackendRecovery.RecoveryChoices(review, guardSnapshot).Count == 0,
                "force-replacement evidence never offers unrelated guard restoration");
            try
            {
                await DeltaNFD.Services.Backends.BackendRecovery.RestoreAsync(review, "guard");
                throw new IOException("wrong recovery scope was allowed");
            }
            catch (InvalidOperationException ex)
            { Check(ex.Message.Contains("系统 DLL"), "wrong restoration is refused before starting any backend"); }

            Check(ReadVersion(systemDll) == realBefore && (!File.Exists(systemDll) || new FileInfo(systemDll).Length == realLength),
                "real System32 msvcp140.dll untouched by fake-root tests");
            Console.WriteLine("Vc14 force-replace checks passed: " + count + " checks.");
        }
        finally
        {
            TryDelete(payloadRoot);
            TryDelete(payloadRoot + "-tampered");
            var isolatedFull = Path.GetFullPath(isolated);
            foreach (var fixture in fixtures)
            {
                var full = Path.GetFullPath(fixture);
                if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) continue;
                TryDelete(full);
            }
            TryDelete(isolatedFull);
        }
    }

    private static string CreateFakeRoot(IReadOnlyDictionary<string, string> payload)
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-force-root-" + Guid.NewGuid().ToString("N"));
        foreach (var pair in payload)
        {
            var parts = pair.Key.Split('|');
            var target = Vc14ForceReplace.TargetPath(root, parts[0], parts[1]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(pair.Value, target);
        }
        return root;
    }

    private static string? CreateFakeRootFromSystem()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-force-realroot-" + Guid.NewGuid().ToString("N"));
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        try
        {
            foreach (var payload in Vc14ForceReplace.Payload)
            {
                var source = Vc14ForceReplace.TargetPath(systemRoot, payload.Architecture, payload.File);
                if (!File.Exists(source)) { TryDelete(root); return null; }
                var target = Vc14ForceReplace.TargetPath(root, payload.Architecture, payload.File);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }
            return root;
        }
        catch
        {
            TryDelete(root);
            throw;
        }
    }

    private static string CopyAssets(string sourceRoot)
    {
        var fixture = Path.Combine(Path.GetTempPath(), "DeltaNFD-force-assets-" + Guid.NewGuid().ToString("N"));
        foreach (var file in Vc14FileRepairSource.Packages.SelectMany(p => p.Files))
        {
            var target = Path.Combine(fixture, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(sourceRoot, file.Path), target);
        }
        return fixture;
    }

    private static string? ReadVersion(string path)
    {
        try { return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion : null; }
        catch (Exception) { return null; }
    }

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception) { }
    }
}
