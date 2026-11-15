using System.Text.Json;
using System.Xml.Linq;
using DeltaNFD.Backends;
using DeltaNFD.Inspection;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

internal static class StartupItemChecks
{
    internal static async Task RunIpcReadOnlyAsync(string layout)
    {
        var id = Guid.NewGuid(); var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-startup-ipc-owned", id.ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ROOT", Path.Combine(Path.GetFullPath(layout), "Backends"));
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT", root);
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ID", id.ToString("D"));
        var count = 0;
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); count++; }
        var client = await BackendClient.StartAsync(BackendModules.System, default);
        try
        {
            var result = await client.InvokeAsync(BackendCommands.SystemStartupList, new { }, null, default);
            var report = BackendResults.RequireData<StartupItemReport>(result, "read-only startup IPC");
            Check(result.Status == BackendResultStatuses.Ok && !result.PendingRestore && !result.RequiresReview, "read-only startup response does not create mutation obligation");
            Check(report.Items.All(i => StartupItemStore.ValidId(i.Id) && StartupItemStore.ValidId(i.Fingerprint)), "typed startup identifiers survive actual official backend IPC");
            Check(report.Items.All(i => !i.HasBackup && !i.CanRestore && !i.CanKeepCurrent), "isolated empty backup root cannot borrow player's originals");
            Check(!Directory.Exists(Path.Combine(root, "StartupItems")), "list does not create startup records");
            var reconcile = BackendResults.RequireData<ReconcileSnapshot>(await client.InvokeAsync(BackendCommands.SystemReconcile, new { }, null, default), "read-only startup reconcile");
            Check(reconcile.RecoveryScopes.Single(s => s.Id == "startup") is { Available: false, AffectedCount: 0 }, "official recovery advertises new scope with no imaginary backup");
            // 仅打开已有HKCU Run并读取一个随机不存在的名字；不调用Set/Delete/Commit或创建键。
            var probe = new StartupItemOriginal(StartupItemScope.UserRun, "DeltaNFD-readonly-probe-" + Guid.NewGuid().ToString("N"), 1, "unused");
            using (var native = StartupRunRegistration.OpenNative(probe, false))
            {
                Check(native.Read() is null, "native adapter opens existing user Run and reads only without writes or commit");
                Console.WriteLine("Native registry read-only adapter: " + native.GetType().Name);
            }
            Console.WriteLine($"Startup readonly IPC: {count} checks passed; actual startup metadata read only, isolated tool data; no startup/GPU/system writes or GUI. Item count={report.Items.Count}.");
        }
        finally { await client.DisposeAsync(); }
    }
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-startup-owned-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dataRoot = Path.Combine(root, "owned-tool-data"); Directory.CreateDirectory(dataRoot);
        var dataId = Guid.NewGuid(); File.WriteAllText(Path.Combine(dataRoot, "isolated-test.marker"), dataId.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(dataRoot, dataId);
        var count = 0;
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); count++; }
        void Refuse(Action action, string why)
        { try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or FormatException) { count++; return; } throw new Exception(why); }
        var serial = 0;
        (StartupItemStore Store, Fake Env, StartupItemEngine Engine, string Path) Fixture()
        {
            var path = Path.Combine(root, "case-" + serial++); var store = new StartupItemStore(path, "owned-test-user");
            var env = new Fake(store); return (store, env, new StartupItemEngine(store, env), path);
        }
        StartupItemOriginal Original(StartupItemScope scope = StartupItemScope.UserRun, int kind = 1, string name = "PlayerApp") =>
            scope is StartupItemScope.UserFolder or StartupItemScope.CommonFolder
                ? new(scope, name + ".lnk", 0, Convert.ToBase64String([1, 2, 3, 4]), DateTime.UnixEpoch.AddDays(10).Ticks, DateTime.UnixEpoch.AddDays(11).Ticks, (int)FileAttributes.Archive,
                    SecurityDescriptor: "D:(A;;FA;;;SY)(A;;FA;;;BA)")
                : new(scope, name, kind, kind == 2 ? @"%APPDATA%\Player\app.exe --startup" : "\"C:\\Apps\\Player.exe\" --startup");
        StartupItemChangeArgs Args(StartupItemStore store, Fake env, StartupItemOriginal item, StartupItemAction action) =>
            new(item.Id, StartupItemInspection.Fingerprint(item.Id, env.Read(item.Scope, item.Name), store.Read(item.Id)), action);

        foreach (var scope in Enum.GetValues<StartupItemScope>())
        {
            var f = Fixture(); var original = Original(scope); f.Env.Values[original.Id] = original;
            Check(f.Store.ReadAll().Count == 0 && !Directory.Exists(f.Path), "read-only empty store creates no files");
            var registered = StartupItemInspection.Present(original.Id, original, null);
            Check(registered.State == "登记存在" && registered.CanDisable && !registered.HasBackup, "registration is not reported as Windows enabled state");
            var result = f.Engine.Change(Args(f.Store, f.Env, original, StartupItemAction.Disable));
            Check(result.Success && !result.OutcomeUnknown && f.Env.Removes == 1 && !f.Env.Values.ContainsKey(original.Id), "selected registration disabled");
            var backup = f.Store.Read(original.Id)!;
            Check(backup.Original == original && backup.UserSid == "owned-test-user", "raw original and user identity preserved");
            var rawBackup = File.ReadAllBytes(Path.Combine(f.Path, original.Id + ".json"));
            var disabled = StartupItemInspection.Present(original.Id, null, backup);
            Check(disabled.HasBackup && disabled.CanRestore && !disabled.CanDisable && disabled.State == "本工具已停用", "disabled row is independently restorable");
            Check(f.Env.BackupSeenBeforeEveryWrite, "durable readable backup exists before target mutation");
            var second = f.Engine.Change(Args(f.Store, f.Env, original, StartupItemAction.Disable));
            Check(!second.Success && f.Env.Removes == 1 && File.ReadAllBytes(Path.Combine(f.Path, original.Id + ".json")).SequenceEqual(rawBackup), "repeat disable cannot overwrite first backup or replay delete");
            result = f.Engine.Change(Args(f.Store, f.Env, original, StartupItemAction.Restore));
            Check(result.Success && f.Env.Values[original.Id].SameContent(original) && f.Env.Creates == 1, "original registration restored");
            Check(f.Store.ReadAll().Count == 0 && Directory.GetFiles(Path.Combine(f.Path, "reviewed"), "*.json").Length == 1, "verified restore archives active backup");
            Check(File.ReadAllBytes(Directory.GetFiles(Path.Combine(f.Path, "reviewed"), "*.json").Single()).SequenceEqual(rawBackup), "archive retains exact original backup bytes");
        }
        foreach (var scope in new[] { StartupItemScope.UserRun, StartupItemScope.MachineRun, StartupItemScope.MachineRun32 })
        {
            var a = Original(scope); var transaction = new RegistryTransaction(a);
            StartupRunRegistration.Apply(a, false, (_, create) => { Check(!create, "disable opens existing transacted key"); return transaction; });
            Check(transaction.Current is null && transaction.Commits == 1 && transaction.Disposed, "registry disable commits one verified transaction");
            transaction = new RegistryTransaction(null, a);
            StartupRunRegistration.Apply(a, true, (_, create) => { Check(create, "restore may create transacted fixed Run key"); return transaction; });
            Check(transaction.Current == a && transaction.Commits == 1 && transaction.Disposed, "registry restore commits only exact original into absent name");
            transaction = new RegistryTransaction(a with { Data = "new registration" });
            Refuse(() => StartupRunRegistration.Apply(a, true, (_, _) => transaction), "transaction must refuse same-name registration");
            Check(transaction.Current!.Data == "new registration" && transaction.Writes == 0 && transaction.Commits == 0 && transaction.Disposed, "restore conflict never submits a write or commits");
            Refuse(() => StartupRunRegistration.Apply(a, false, (_, _) => transaction = new RegistryTransaction(a with { Data = "updated command" })), "transaction must refuse changed command before delete");
            Check(transaction.Current!.Data == "updated command" && transaction.Writes == 0 && transaction.Commits == 0, "transactional delete preserves changed command");
        }
        foreach (var restore in new[] { false, true })
        {
            var a = Original(); var transaction = new RegistryTransaction(restore ? null : a, a) { ConcurrentCommit = a with { Data = "racing application" } };
            Refuse(() => StartupRunRegistration.Apply(a, restore, (_, _) => transaction), "external transaction conflict must fail commit");
            Check(transaction.Current!.Data == "racing application" && transaction.Commits == 0 && transaction.Disposed, "conflicting commit does not overwrite or delete concurrent registration");
            transaction = new RegistryTransaction(restore ? null : a, a) { FailReadback = true };
            Refuse(() => StartupRunRegistration.Apply(a, restore, (_, _) => transaction), "transaction readback mismatch must not commit");
            Check(transaction.Current == (restore ? null : a) && transaction.Commits == 0 && transaction.Disposed, "failed transaction readback rolls back staged mutation");
        }
        {
            var f = Fixture(); var original = Original(kind: 2); f.Env.Values[original.Id] = original;
            Check(f.Engine.Change(Args(f.Store, f.Env, original, StartupItemAction.Disable)).Success, "expand-string disabled without expanding variables");
            Check(f.Engine.Change(Args(f.Store, f.Env, original, StartupItemAction.Restore)).Success && f.Env.Values[original.Id] == original, "expand-string kind and raw percent text restored exactly");
        }
        {
            var f = Fixture(); var a = Original(); var other = Original(StartupItemScope.MachineRun);
            f.Env.Values[a.Id] = a; f.Env.Values[other.Id] = other;
            Check(a.Id != other.Id && a.Id == (a with { Name = a.Name.ToUpperInvariant() }).Id, "scope distinct, Windows value-name case folded");
            f.Engine.Change(Args(f.Store, f.Env, a, StartupItemAction.Disable));
            Check(f.Env.Values.ContainsKey(other.Id) && f.Store.ReadAll().Count == 1, "same name in another scope is untouched");
        }
        foreach (var action in Enum.GetValues<StartupItemAction>())
        {
            var f = Fixture(); var a = Original(); f.Env.Values[a.Id] = a;
            var args = Args(f.Store, f.Env, a, action);
            Check(!f.Engine.Change(args with { Id = "../outside" }).Success && f.Env.Removes + f.Env.Creates == 0, "arbitrary identity/path rejected");
            Check(!f.Engine.Change(args with { Fingerprint = "bad" }).Success && f.Env.Removes + f.Env.Creates == 0, "invalid fingerprint rejected");
        }
        {
            var f = Fixture(); var a = Original(); f.Env.Values[a.Id] = a; var args = Args(f.Store, f.Env, a, StartupItemAction.Disable);
            Check(!f.Engine.Change(args with { Action = (StartupItemAction)999 }).Success, "unknown action rejected");
            f.Env.Values[a.Id] = a with { Data = "new application registration" };
            Check(!f.Engine.Change(args).Success && f.Store.ReadAll().Count == 0 && f.Env.Removes == 0, "stale UI cannot act on changed command");
        }
        foreach (var blocked in new[] { "game running", "game state unknown", "not administrator" })
        {
            var f = Fixture(); var a = Original(); f.Env.Values[a.Id] = a; var args = Args(f.Store, f.Env, a, StartupItemAction.Disable);
            f.Env.Blocked = blocked;
            Check(!f.Engine.Change(args).Success && f.Env.Removes == 0 && f.Store.ReadAll().Count == 0, "permission/game gate runs before backup or mutation");
            Check(!f.Engine.RestoreAll().Success, "restore-all respects gate even for no-op scope");
        }
        {
            var f = Fixture(); var a = Original(); f.Env.Values[a.Id] = a; var args = Args(f.Store, f.Env, a, StartupItemAction.Disable);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Check(!f.Engine.Change(args, cancelled.Token).Success && f.Store.ReadAll().Count == 0, "pre-cancel has no backup or target writes");
        }
        foreach (var change in new[] { "gate", "value" })
        {
            var f = Fixture(); var a = Original(); f.Env.Values[a.Id] = a;
            f.Env.OnGate = checks => { if (checks == 2) { if (change == "gate") f.Env.Blocked = "game started"; else f.Env.Values[a.Id] = a with { Data = "external update" }; } };
            var result = f.Engine.Change(Args(f.Store, f.Env, a, StartupItemAction.Disable));
            Check(!result.Success && !result.OutcomeUnknown && f.Env.Removes == 0 && f.Store.Read(a.Id)!.Original == a, "write-time recheck stops after durable backup without inventing unknown mutation");
        }
        foreach (var failure in new[] { "remove-before", "remove-after", "read-after" })
        {
            var f = Fixture(); var a = Original(); f.Env.Values[a.Id] = a; f.Env.Failure = failure;
            var result = f.Engine.Change(Args(f.Store, f.Env, a, StartupItemAction.Disable));
            Check(!result.Success && result.OutcomeUnknown && result.IsPartial && f.Store.Read(a.Id) is not null, "submitted failure preserves original and unknown outcome");
            f.Env.Failure = "";
            Check(f.Engine.Change(Args(f.Store, f.Env, a, StartupItemAction.Restore)).Success && f.Env.Values[a.Id].SameContent(a), "interrupted disable can be explicitly restored");
        }
        {
            var f = Fixture(); var a = Original(); f.Env.Values[a.Id] = a;
            f.Engine.Change(Args(f.Store, f.Env, a, StartupItemAction.Disable)); f.Env.Failure = "create-after";
            var result = f.Engine.Change(Args(f.Store, f.Env, a, StartupItemAction.Restore));
            Check(result.OutcomeUnknown && f.Store.Read(a.Id) is not null, "interrupted restore retains backup even after target creation");
            f.Env.Failure = ""; var creates = f.Env.Creates;
            Check(f.Engine.Change(Args(f.Store, f.Env, a, StartupItemAction.Restore)).Success && f.Env.Creates == creates, "retry recognizes restored content and only finishes backup record");
        }
        {
            var f = Fixture(); var a = Original(); f.Env.Values[a.Id] = a;
            f.Engine.Change(Args(f.Store, f.Env, a, StartupItemAction.Disable));
            var backup = f.Store.Read(a.Id)!; var newValue = a with { Data = "new version command" }; f.Env.Values[a.Id] = newValue;
            var conflicted = StartupItemInspection.Present(a.Id, newValue, backup);
            Check(!conflicted.CanRestore && conflicted.CanKeepCurrent && !conflicted.CanDisable, "conflicting new registration has explicit retain-current path");
            Check(!f.Engine.Change(Args(f.Store, f.Env, a, StartupItemAction.Restore)).Success && f.Env.Values[a.Id] == newValue, "restore refuses overwrite of application update");
            var stale = Args(f.Store, f.Env, a, StartupItemAction.KeepCurrent);
            f.Env.Values[a.Id] = newValue with { Data = "another external change" };
            Check(!f.Engine.Change(stale).Success && f.Store.Read(a.Id) is not null, "late retain confirmation cannot discard newer evidence");
            Check(f.Engine.Change(Args(f.Store, f.Env, a, StartupItemAction.KeepCurrent)).Success && f.Store.ReadAll().Count == 0
                && f.Env.Values[a.Id].Data == "another external change", "explicit retain archives old backup and never writes current target");
        }
        {
            var f = Fixture(); var a = Original(); f.Env.Values[a.Id] = a;
            f.Env.OnCreate = item => f.Env.Values[item.Id] = item with { Data = "racing application" };
            f.Engine.Change(Args(f.Store, f.Env, a, StartupItemAction.Disable));
            var result = f.Engine.Change(Args(f.Store, f.Env, a, StartupItemAction.Restore));
            Check(result.OutcomeUnknown && f.Env.Values[a.Id].Data == "racing application" && f.Store.Read(a.Id) is not null, "create race does not overwrite or lose backup");
        }
        {
            var f = Fixture(); var a = Original(); f.Env.Values[a.Id] = a;
            f.Store.Save(a); var creates = f.Env.Creates;
            Check(f.Engine.Change(Args(f.Store, f.Env, a, StartupItemAction.Restore)).Success && f.Env.Creates == creates, "shutdown after backup but before remove finishes without replay");
        }
        {
            var f = Fixture(); var a = Original(); f.Env.Values[a.Id] = a; f.Store.Save(a);
            var path = Path.Combine(f.Path, a.Id + ".json"); File.WriteAllText(path, "{broken");
            var bytes = File.ReadAllBytes(path);
            var result = f.Engine.Change(new(a.Id, new string('A', 64), StartupItemAction.Restore));
            Check(!result.Success && f.Env.Creates + f.Env.Removes == 0 && File.ReadAllBytes(path).SequenceEqual(bytes), "malformed backup neither becomes absent nor gets replaced");
        }
        {
            var f = Fixture(); var a = Original(); var saved = f.Store.Save(a);
            Refuse(() => new StartupItemStore(f.Path, "another-user").Read(a.Id), "foreign-user backup must be rejected");
            Refuse(() => f.Store.Save(a with { Data = "replacement" }), "first backup must not be overwritten");
            Refuse(() => f.Store.Archive(a.Id, new string('F', 64)), "changed backup token must be refused");
            Check(f.Store.Read(a.Id) == saved, "failed store operations preserve original bytes and identity");
        }
        {
            var f = Fixture(); var a = Original(StartupItemScope.UserFolder) with { Data = Convert.ToBase64String(Enumerable.Repeat((byte)0xfb, 1000000).ToArray()) };
            Check(f.Store.Save(a).Original == a && f.Store.Read(a.Id)!.Original == a, "large plus-containing shortcut backup remains under read limit and round-trips exactly");
        }
        {
            var f = Fixture(); var a = Original(); Directory.CreateDirectory(f.Path);
            using (var oversized = File.Create(Path.Combine(f.Path, new string('F', 64) + ".json"))) oversized.SetLength(16L * 1024 * 1024);
            Refuse(() => f.Store.Save(a), "capacity exhaustion must fail before publishing another active original");
            Check(!File.Exists(Path.Combine(f.Path, a.Id + ".json")), "failed size admission cannot create unreadable published state");
        }
        {
            var f = Fixture(); var a = Original(); var saved = 0;
            Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            {
                try { f.Store.Save(a with { Data = "owned race " + i }); Interlocked.Increment(ref saved); }
                catch (IOException) { }
            }))).GetAwaiter().GetResult();
            Check(saved == 1 && f.Store.ReadAll().Count == 1 && f.Store.Read(a.Id)!.Original.Data.StartsWith("owned race "), "concurrent writers retain exactly one first original");
        }
        {
            var f = Fixture(); var a = Original(); f.Env.Values[a.Id] = a;
            f.Engine.Change(Args(f.Store, f.Env, a, StartupItemAction.Disable));
            var stale = Args(f.Store, f.Env, a, StartupItemAction.Restore);
            var updated = new StartupItemBackup(1, "owned-test-user", a with { Data = "changed backup contents" });
            File.WriteAllText(Path.Combine(f.Path, a.Id + ".json"), JsonSerializer.Serialize(updated));
            Check(!f.Engine.Change(stale).Success && f.Env.Creates == 0 && !f.Env.Values.ContainsKey(a.Id), "backup-content changes invalidate old restore confirmation");
        }
        foreach (var name in new[] { "../outside.lnk", @"..\outside.lnk", @"C:\outside.lnk", "bad.exe", "a:b.lnk" })
            Refuse(() => StartupItemStore.Validate(Original(StartupItemScope.UserFolder) with { Name = name }), "folder traversal/unsupported file type rejected");
        Refuse(() => StartupItemStore.Validate(Original() with { Kind = 3 }), "unsupported registry kind rejected");
        Refuse(() => StartupItemStore.Validate(Original(StartupItemScope.UserFolder) with { Data = "not-base64" }), "corrupt shortcut payload rejected");
        Refuse(() => StartupItemStore.Validate(Original(StartupItemScope.UserFolder) with { SecurityDescriptor = "" }), "shortcut without captured DACL is not accepted for mutation");
        Refuse(() => StartupItemStore.Validate(Original(StartupItemScope.UserFolder) with { Attributes = (int)FileAttributes.Encrypted }), "unrestorable file metadata stays read-only");
        {
            var sourceDir = Path.Combine(root, "owned-shortcut-source"); var restoredDir = Path.Combine(root, "owned-shortcut-restore");
            Directory.CreateDirectory(sourceDir);
            var source = Path.Combine(sourceDir, "owned.url"); var restoredPath = Path.Combine(restoredDir, "owned.url");
            File.WriteAllText(source, "[InternetShortcut]\r\nURL=https://example.invalid/owned-fixture\r\n");
            File.WriteAllText(source + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            File.SetAttributes(source, FileAttributes.Hidden | FileAttributes.Archive);
            var captured = StartupItemInspection.ReadShortcutFile(source, StartupItemScope.UserFolder, "owned.url")!;
            Check(captured.ZoneIdentifier is not null && captured.SecurityDescriptor.Length > 0, "real owned shortcut captures origin mark and DACL, not just file bytes");
            StartupShortcutFiles.Create(restoredPath, captured);
            var after = StartupItemInspection.ReadShortcutFile(restoredPath, StartupItemScope.UserFolder, "owned.url")!;
            Check(after.SameContent(captured), "real owned shortcut content, origin mark and DACL restored together");
            Check(after.CreatedUtcTicks == captured.CreatedUtcTicks && after.WrittenUtcTicks == captured.WrittenUtcTicks && after.Attributes == captured.Attributes, "real owned shortcut timestamps and supported attributes preserved");
            Refuse(() => StartupShortcutFiles.Create(restoredPath, captured), "native create refuses existing target");
            Check(StartupItemInspection.ReadShortcutFile(restoredPath, StartupItemScope.UserFolder, "owned.url")!.SameContent(captured), "create conflict leaves existing content and metadata unchanged");
            var f = Fixture(); f.Store.Save(captured);
            Check(f.Store.Read(captured.Id)!.Original == captured, "native origin/ACL metadata survives durable backup serialization");
            File.WriteAllText(restoredPath + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=4\r\n");
            var changedZone = StartupItemInspection.ReadShortcutFile(restoredPath, StartupItemScope.UserFolder, "owned.url")!;
            Check(!changedZone.SameContent(captured), "security provenance change invalidates content-equivalence shortcut");
            Refuse(() => StartupShortcutFiles.Remove(restoredPath, captured), "native delete must refuse changed provenance");
            Check(File.Exists(restoredPath), "stale native delete leaves updated shortcut present");
            using (var writer = new FileStream(restoredPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                Refuse(() => StartupShortcutFiles.Remove(restoredPath, changedZone), "native delete refuses concurrently writable target");
            Check(File.Exists(restoredPath), "failed exclusive native snapshot leaves shortcut present");
            StartupShortcutFiles.Remove(restoredPath, changedZone);
            Check(!File.Exists(restoredPath) && File.Exists(source), "handle-bound delete affects only verified owned shortcut");
        }
        {
            var f = Fixture(); var a = Original(); var b = Original(StartupItemScope.MachineRun); f.Env.Values[a.Id] = a; f.Env.Values[b.Id] = b;
            f.Engine.Change(Args(f.Store, f.Env, a, StartupItemAction.Disable)); f.Engine.Change(Args(f.Store, f.Env, b, StartupItemAction.Disable));
            Check(f.Engine.RestoreAll().Success && f.Store.ReadAll().Count == 0 && f.Env.Values.Count == 2, "restore-all restores only original saved registrations");
        }
        {
            var f = Fixture(); var a = Original(); var b = Original(StartupItemScope.MachineRun); f.Env.Values[a.Id] = a; f.Env.Values[b.Id] = b;
            f.Engine.Change(Args(f.Store, f.Env, a, StartupItemAction.Disable)); f.Engine.Change(Args(f.Store, f.Env, b, StartupItemAction.Disable));
            f.Env.Failure = "create-after";
            Check(f.Engine.RestoreAll().OutcomeUnknown && f.Env.Creates == 1 && f.Store.ReadAll().Count == 2, "restore-all stops after first unknown write and retains every active original");
        }
        var wire = new StartupItemReport([StartupItemInspection.Present(Original().Id, Original(), null)], []);
        Check(JsonSerializer.Deserialize<StartupItemReport>(JsonSerializer.Serialize(wire))!.Items.Single().Fingerprint == wire.Items[0].Fingerprint, "report fingerprint round-trips without losing source identity");
        Check(BackendModules.IsReadOnly(BackendModules.System, BackendCommands.SystemStartupList)
            && !BackendModules.IsMutating(BackendModules.System, BackendCommands.SystemStartupList), "list command is read-only");
        foreach (var command in new[] { BackendCommands.SystemStartupChange, BackendCommands.SystemStartupRestoreAll })
            Check(BackendModules.IsAllowed(BackendModules.System, command) && BackendModules.RequiresWriteIntent(BackendModules.System, command)
                && BackendModules.RetainsStaticBackup(BackendModules.System, command), "write commands use original intent/static-backup lifecycle");
        Check(!BackendModules.IsAllowed(BackendModules.Gpu, BackendCommands.SystemStartupChange), "startup mutation cannot cross backend modules");
        foreach (var command in new[] { BackendCommands.SystemStartupChange, BackendCommands.SystemStartupRestoreAll })
        {
            var review = new BackendUnconfirmedStore.Review(BackendModules.System, "owned-fingerprint", "owned startup interruption", true)
                { Command = command, CanRestore = true };
            var snapshot = new ReconcileSnapshot { Module = BackendModules.System, Complete = true, Summary = "owned fixture",
                RecoveryScopes = [new() { Id = "startup", Description = "startup originals", Available = true, AffectedCount = 1 },
                    new() { Id = "tweaks", Description = "unrelated", Available = true }] };
            Check(BackendRecovery.RecoveryChoices(review, snapshot).Select(s => s.Id).SequenceEqual(["startup"]), "startup interruption cannot be cleared through unrelated system backup scope");
            Refuse(() => BackendUnconfirmedStore.ArchiveAfterRecovery(review, "tweaks", true, "owned", snapshot), "archive layer independently rejects unrelated scope before reading any real record");
            Refuse(() => BackendUnconfirmedStore.ArchiveAfterRecovery(review, "startup", true, "owned", snapshot), "available startup originals cannot be claimed fully recovered");
        }

        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "src/DeltaNFD/Views/StartupItemsPage.xaml"))) repo = repo.Parent;
        if (repo is null) throw new Exception("Startup source snapshot unavailable.");
        string Source(string path) => File.ReadAllText(Path.Combine(repo.FullName, path));
        var xaml = XDocument.Parse(Source("src/DeltaNFD/Views/StartupItemsPage.xaml"));
        Check(xaml.Descendants().Any(e => e.Name.LocalName == "ListView" && (string?)e.Attribute("ItemsSource") == "{x:Bind Rows}"), "startup list uses observable bound rows");
        var page = Source("src/DeltaNFD/Views/StartupItemsPage.xaml.cs");
        Check(page.Contains("DefaultButton = ContentDialogButton.Close") && page.Contains("row.Info.Fingerprint"), "default-cancel confirmation carries the observed fingerprint");
        Check(page.Contains("_queries.Cancel()") && page.Contains("generation != _generation") && page.Contains("if (_active && !_needsReview)"), "unload and unknown responses do not submit extra work");
        Check(Source("src/DeltaNFD/MainWindow.PageSessions.cs").Contains("typeof(StartupItemsPage)"), "hidden page retains system backend owner");
        var handler = Source("src/DeltaNFD.Backend.SystemMaintenance/SystemCommands.cs");
        Check(handler.Contains("case \"startup\"") && handler.Contains("StartupItemInspection.ReadBackups()"), "interrupted startup writes have explicit recover/reconcile scope");
        Check(Source("src/DeltaNFD/ViewModels/BxDbViewModel.cs").Contains("SystemStartupRestoreAll"), "global restore includes startup backups");
        Check(!Source("src/DeltaNFD.Execution/Execution/StartupItemService.cs").Contains("Process.Start")
            && !Source("src/DeltaNFD.Inspection/StartupItemInspection.cs").Contains("Process.Start"), "startup commands and shortcuts are never executed while inspecting or changing registration");
        Console.WriteLine($"Startup items: {count} checks passed; simulated registration/shortcut targets and owned backup files only; no real startup/GPU/system changes. Evidence: {root}");
    }

    private sealed class RegistryTransaction : IStartupRegistryEdit
    {
        internal StartupItemOriginal? Current;
        private StartupItemOriginal? _working;
        private readonly StartupItemOriginal? _target;
        internal StartupItemOriginal? ConcurrentCommit;
        internal bool FailReadback, Disposed;
        internal int Writes, Commits;
        internal RegistryTransaction(StartupItemOriginal? original, StartupItemOriginal? target = null) { Current = _working = original; _target = target ?? original; }
        public StartupItemOriginal? Read() => FailReadback && Writes > 0 ? throw new IOException("owned transaction readback conflict") : _working;
        public void Delete() { Writes++; _working = null; }
        public void Set() { Writes++; _working = _target ?? throw new IOException("missing owned transaction target"); }
        public void Commit()
        {
            if (ConcurrentCommit is not null) { Current = ConcurrentCommit; throw new IOException("owned registry transaction aborted by concurrent registration"); }
            Current = _working; Commits++;
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class Fake(StartupItemStore store) : IStartupItemEnvironment
    {
        internal readonly Dictionary<string, StartupItemOriginal> Values = new(StringComparer.Ordinal);
        internal string? Blocked;
        internal string Failure = "";
        internal int Removes, Creates, Gates;
        internal bool BackupSeenBeforeEveryWrite = true;
        internal Action<int>? OnGate;
        internal Action<StartupItemOriginal>? OnCreate;
        public string? Blocker() { OnGate?.Invoke(++Gates); return Blocked; }
        public IReadOnlyList<StartupItemOriginal> Discover() => Values.Values.ToArray();
        public StartupItemOriginal? Read(StartupItemScope scope, string name)
        {
            if (Failure == "read-after" && Removes > 0) throw new IOException("owned readback failure");
            return Values.Values.SingleOrDefault(i => i.Scope == scope && i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        public void Remove(StartupItemOriginal original)
        {
            BackupSeenBeforeEveryWrite &= store.Read(original.Id)?.Original == original;
            Removes++; if (Failure == "remove-before") throw new IOException("owned remove failure");
            Values.Remove(original.Id); if (Failure == "remove-after") throw new IOException("owned post-remove failure");
        }
        public void Create(StartupItemOriginal original)
        {
            BackupSeenBeforeEveryWrite &= store.Read(original.Id)?.Original == original;
            Creates++; OnCreate?.Invoke(original);
            if (!Values.TryAdd(original.Id, original)) throw new IOException("owned create-new conflict");
            if (Failure == "create-after") throw new IOException("owned post-create failure");
        }
    }
}
