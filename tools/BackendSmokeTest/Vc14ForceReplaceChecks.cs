using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
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
            string FakeNewer(string path) => "14.44.35218.0";
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

            // 13) ACL / TrustedInstaller 能力（对照外部 Vc14RuntimeFix 1.2；全部在假根和隔离数据根内）。
            var aclRoot = CreateAclFakeRoot(extracted);
            fixtures.Add(aclRoot);
            var aclHealthy = Vc14ForceReplace.PreviewCore(root, aclRoot, enforceHostChecks: false, versionReader: null,
                CancellationToken.None, Vc14Baseline.V142, inspectAcl: true);
            Check(aclHealthy.CanRun && aclHealthy.SkipCount == 51 && aclHealthy.AclRepairCount == 0
                && aclHealthy.LockedCount == 0, "system-like fake ACL root is a no-op");

            // 13a) 仅权限被改坏（内容已匹配）：预览为 acl，执行只重置权限且不改字节。
            var aclOnlyFile = Vc14ForceReplace.TargetPath(aclRoot, "x64", "msvcp140.dll");
            var aclOnlyHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(aclOnlyFile)));
            SetFakeAcl(aclOnlyFile, usersRead: false, adminsWrite: true, grantCurrentUser: true);
            var aclOnlyPreview = Vc14ForceReplace.PreviewCore(root, aclRoot, false, null, CancellationToken.None,
                Vc14Baseline.V142, inspectAcl: true);
            var aclOnlyItem = aclOnlyPreview.Items.Single(i => i.Architecture == "x64" && i.File == "msvcp140.dll");
            Check(aclOnlyPreview.CanRun && aclOnlyItem.Status == "acl" && aclOnlyItem.AclBroken && !aclOnlyItem.InUse
                && aclOnlyPreview.AclRepairCount == 1, "broken users-read ACL is classified as acl repair, not in-use");
            var aclPreviewRoundTrip = JsonSerializer.Deserialize<Vc14ForceReplacePreview>(
                JsonSerializer.Serialize(aclOnlyPreview))!;
            Check(aclPreviewRoundTrip.AclRepairCount == 1 && aclPreviewRoundTrip.SkipCount == 50
                && aclPreviewRoundTrip.Items.Single(i => i.File == "msvcp140.dll" && i.Architecture == "x64").AclBroken,
                "preview contract round-trips AclRepairCount and AclBroken over JSON");
            var aclOnlyRun = await Vc14ForceReplace.RunCoreAsync(aclOnlyPreview.Fingerprint, null, CancellationToken.None,
                root, aclRoot, false, null, null, Vc14Baseline.V142, inspectAcl: true);
            Check(aclOnlyRun.Success && aclOnlyRun.Message.Contains("仅修复权限 1")
                && Vc14ForceReplaceAcl.Inspect(aclOnlyFile).UsersCanRead, "acl-only item is repaired without replacement");
            Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(aclOnlyFile))) == aclOnlyHash,
                "acl-only repair keeps DLL bytes unchanged");
            Check(File.Exists(Path.Combine(aclOnlyRun.DiagnosticsDirectory, "acl.json"))
                && File.Exists(Path.Combine(aclOnlyRun.DiagnosticsDirectory, "inventory.json")),
                "ACL snapshot and inventory are written before permission repair");
            Check(Directory.Exists(Path.Combine(aclOnlyRun.DiagnosticsDirectory, "registry"))
                && Directory.GetFiles(Path.Combine(aclOnlyRun.DiagnosticsDirectory, "registry"), "*.reg").Length > 0,
                "read-only registry snapshot is exported");
            using (var aclJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(aclOnlyRun.DiagnosticsDirectory, "acl.json"))))
            {
                var aclItems = aclJson.RootElement.GetProperty("items").EnumerateArray().ToList();
                Check(aclItems.Count == 1 && aclItems[0].GetProperty("sddl").GetString()!.Length > 0
                    && aclItems[0].GetProperty("ownerSid").GetString()!.Length > 0, "acl.json records owner SID and SDDL");
            }
            using (var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(aclOnlyRun.DiagnosticsDirectory, "inventory.json"))))
            {
                Check(inventory.RootElement.GetProperty("schema").GetInt32() == 2
                    && inventory.RootElement.GetProperty("aclSnapshotFile").GetString() == "acl.json"
                    && inventory.RootElement.GetProperty("registryExports").GetArrayLength() == 4,
                    "inventory schema 2 records ACL snapshot and registry exports");
            }

            // 13b) 管理员只读（TrustedInstaller 风格）且版本不符：临时接管后替换，并恢复原 ACL/属主。
            var lockedFile = Vc14ForceReplace.TargetPath(aclRoot, "x64", "vcruntime140.dll");
            var lockedOwner = Vc14ForceReplaceAcl.Inspect(lockedFile).OwnerSid;
            SetFakeAcl(lockedFile, usersRead: true, adminsWrite: false, grantCurrentUser: false);
            string LockedNewer(string path) => path.Equals(lockedFile, StringComparison.OrdinalIgnoreCase)
                ? "14.44.35218.0" : ReadVersion(path) ?? "";
            var lockedPreview = Vc14ForceReplace.PreviewCore(root, aclRoot, false, LockedNewer, CancellationToken.None,
                Vc14Baseline.V142, inspectAcl: true);
            var lockedItem = lockedPreview.Items.Single(i => i.Architecture == "x64" && i.File == "vcruntime140.dll");
            Check(lockedPreview.CanRun && lockedItem.Status == "replace" && lockedItem.Locked
                && lockedPreview.LockedCount == 1, "administrator read-only ACE requires ownership takeover");
            var lockedRun = await Vc14ForceReplace.RunCoreAsync(lockedPreview.Fingerprint, null, CancellationToken.None,
                root, aclRoot, false, LockedNewer, null, Vc14Baseline.V142, inspectAcl: true);
            var lockedAcl = Vc14ForceReplaceAcl.Inspect(lockedFile);
            Check(lockedRun.Success && lockedRun.Message.Contains("已替换 1"), "locked item is replaced in place");
            Check(lockedAcl.UsersCanRead && !lockedAcl.AdminsCanWrite
                && lockedAcl.OwnerSid.Equals(lockedOwner, StringComparison.OrdinalIgnoreCase),
                "original owner and administrator read-only ACL are restored after takeover");
            Check(ReadVersion(lockedFile) == Vc14ForceReplace.ExpectedVersion, "locked item ends at the target version");

            // 13c) 权限被改坏 + 版本不符：替换后重置为目录继承权限。
            var brokenReplaceFile = Vc14ForceReplace.TargetPath(aclRoot, "x86", "vcruntime140.dll");
            SetFakeAcl(brokenReplaceFile, usersRead: false, adminsWrite: true, grantCurrentUser: true);
            string BrokenNewer(string path) => path.Equals(brokenReplaceFile, StringComparison.OrdinalIgnoreCase)
                ? "14.44.35211.0" : ReadVersion(path) ?? "";
            var brokenPreview = Vc14ForceReplace.PreviewCore(root, aclRoot, false, BrokenNewer, CancellationToken.None,
                Vc14Baseline.V142, inspectAcl: true);
            var brokenItem = brokenPreview.Items.Single(i => i.Architecture == "x86" && i.File == "vcruntime140.dll");
            Check(brokenPreview.CanRun && brokenItem.Status == "replace" && brokenItem.AclBroken,
                "broken users-read ACL with version mismatch is a replace+repair item");
            var brokenRun = await Vc14ForceReplace.RunCoreAsync(brokenPreview.Fingerprint, null, CancellationToken.None,
                root, aclRoot, false, BrokenNewer, null, Vc14Baseline.V142, inspectAcl: true);
            Check(brokenRun.Success && Vc14ForceReplaceAcl.Inspect(brokenReplaceFile).UsersCanRead,
                "replace+broken ACL item is replaced and permissions reset to inherited");
            Check(ReadVersion(brokenReplaceFile) == Vc14ForceReplace.ExpectedVersion,
                "replace+broken ACL item ends at the target version");

            // 13e) 宽检测：Everyone 可读不误报；DENY Read 必须判为不可读并修复。
            var aclDetectFile = Vc14ForceReplace.TargetPath(aclRoot, "x86", "msvcp140.dll");
            SetEveryoneReadAcl(aclDetectFile);
            var everyoneAcl = Vc14ForceReplaceAcl.Inspect(aclDetectFile);
            Check(everyoneAcl.UsersCanRead && everyoneAcl.AdminsCanWrite,
                "Everyone/Authenticated-Users style read access counts as users-readable");
            AddDenyUsersRead(aclDetectFile);
            var deniedAcl = Vc14ForceReplaceAcl.Inspect(aclDetectFile);
            Check(!deniedAcl.UsersCanRead, "deny read data on Users overrides broad read access");
            var denyPreview = Vc14ForceReplace.PreviewCore(root, aclRoot, false, null, CancellationToken.None,
                Vc14Baseline.V142, inspectAcl: true);
            var denyItem = denyPreview.Items.Single(i => i.Architecture == "x86" && i.File == "msvcp140.dll");
            Check(denyPreview.CanRun && denyItem.Status == "acl" && denyItem.AclBroken,
                "deny-read file is classified as an acl repair, not a healthy/ok file");
            var denyRun = await Vc14ForceReplace.RunCoreAsync(denyPreview.Fingerprint, null, CancellationToken.None,
                root, aclRoot, false, null, null, Vc14Baseline.V142, inspectAcl: true);
            Check(denyRun.Success && Vc14ForceReplaceAcl.Inspect(aclDetectFile).UsersCanRead,
                "deny-read ACL is repaired by resetting to inherited permissions");

            // 13d) 替换后验证失败时，回滚必须同时恢复字节与原 ACL/属主。
            var aclRollbackRoot = CreateAclFakeRoot(extracted);
            fixtures.Add(aclRollbackRoot);
            var aclRollbackFile = Vc14ForceReplace.TargetPath(aclRollbackRoot, "x64", "mfc140.dll");
            var aclRollbackOwner = Vc14ForceReplaceAcl.Inspect(aclRollbackFile).OwnerSid;
            var aclRollbackBefore = File.ReadAllBytes(aclRollbackFile);
            SetFakeAcl(aclRollbackFile, usersRead: true, adminsWrite: false, grantCurrentUser: false);
            string RollbackNewer(string path) => path.Equals(aclRollbackFile, StringComparison.OrdinalIgnoreCase)
                ? "14.44.35220.0" : ReadVersion(path) ?? "";
            var aclRollbackPreview = Vc14ForceReplace.PreviewCore(root, aclRollbackRoot, false, RollbackNewer,
                CancellationToken.None, Vc14Baseline.V142, inspectAcl: true);
            Check(aclRollbackPreview.CanRun && aclRollbackPreview.LockedCount == 1,
                "ACL rollback fixture previews the locked item");
            var aclRollbackRun = await Vc14ForceReplace.RunCoreAsync(aclRollbackPreview.Fingerprint, null, CancellationToken.None,
                root, aclRollbackRoot, false, RollbackNewer, path =>
                {
                    if (path.Equals(aclRollbackFile, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("fixture post-write failure");
                }, Vc14Baseline.V142, inspectAcl: true);
            var aclRollbackAfter = Vc14ForceReplaceAcl.Inspect(aclRollbackFile);
            Check(!aclRollbackRun.Success && !aclRollbackRun.IsPartial && !aclRollbackRun.OutcomeUnknown,
                "ACL rollback fixture completes a confirmed rollback");
            Check(File.ReadAllBytes(aclRollbackFile).SequenceEqual(aclRollbackBefore)
                && aclRollbackAfter.UsersCanRead && !aclRollbackAfter.AdminsCanWrite
                && aclRollbackAfter.OwnerSid.Equals(aclRollbackOwner, StringComparison.OrdinalIgnoreCase),
                "rollback restores both bytes and the original ACL/owner");

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
                TryMakeTreeWritable(full);
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

    /// <summary>建立带 System32/SysWOW64 风格继承 ACL 的假根：SYSTEM/Administrators 完全控制、Users 读取。</summary>
    private static string CreateAclFakeRoot(IReadOnlyDictionary<string, string> payload)
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-force-aclroot-" + Guid.NewGuid().ToString("N"));
        foreach (var architecture in new[] { "x64", "x86" })
        {
            var directory = architecture == "x64" ? Path.Combine(root, "System32") : Path.Combine(root, "SysWOW64");
            Directory.CreateDirectory(directory);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            // 夹具进程是普通用户；假根目录必须允许它创建/替换文件（真实 System32 由管理员承接）。
            if (WindowsIdentity.GetCurrent().User is { } harnessUser)
                security.AddAccessRule(new FileSystemAccessRule(harnessUser, FileSystemRights.FullControl, inherit,
                    PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(security);
        }
        foreach (var pair in payload)
        {
            var parts = pair.Key.Split('|');
            var target = Vc14ForceReplace.TargetPath(root, parts[0], parts[1]);
            File.Copy(pair.Value, target);
        }
        return root;
    }

    /// <summary>给单个假 DLL 写显式 ACL，模拟权限被改坏或 TrustedInstaller 管理员只读状态。</summary>
    private static void SetFakeAcl(string path, bool usersRead, bool adminsWrite, bool grantCurrentUser)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            adminsWrite ? FileSystemRights.FullControl : FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        if (usersRead)
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        if (grantCurrentUser && WindowsIdentity.GetCurrent().User is { } user)
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static readonly SecurityIdentifier TestUsersSid = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier TestAdminsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier TestSystemSid = new(WellKnownSidType.LocalSystemSid, null);

    /// <summary>显式 ACL：SYSTEM/Administrators 完全控制、Everyone 读取、当前用户完全控制（只读测试夹具）。</summary>
    private static void SetEveryoneReadAcl(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(TestSystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(TestAdminsSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-1-0"),
            FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        if (WindowsIdentity.GetCurrent().User is { } user)
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static void AddDenyUsersRead(string path)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(TestUsersSid, FileSystemRights.ReadData, AccessControlType.Deny));
        info.SetAccessControl(security);
    }

    /// <summary>测试清理前把假根权限恢复为当前用户可删除，避免 ACL 夹具残留。</summary>
    private static void TryMakeTreeWritable(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            var user = WindowsIdentity.GetCurrent().User;
            if (user is null) return;
            foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Prepend(root))
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        var info = new DirectoryInfo(path);
                        var security = info.GetAccessControl(AccessControlSections.Access);
                        security.SetAccessRuleProtection(false, false);
                        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
                        info.SetAccessControl(security);
                    }
                    else
                    {
                        var info = new FileInfo(path);
                        var security = info.GetAccessControl(AccessControlSections.Access);
                        security.SetAccessRuleProtection(false, false);
                        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
                        info.SetAccessControl(security);
                    }
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
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
