using System.Diagnostics;
using System.Text;
using DeltaNFD.Services;

internal static class Vc14RegistrationChecks
{
    internal static void Replay(string detectionPath, string installerLog)
    {
        if (new FileInfo(detectionPath).Length > 8 * 1024 * 1024) throw new IOException("Snapshot too large");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(detectionPath));
        var root = doc.RootElement;
        var scan = System.Text.Json.JsonSerializer.Deserialize<Vc14Scan>(root.GetProperty("Scan"))!;
        var count = 0;
        foreach (var row in root.GetProperty("Registrations").EnumerateArray())
        {
            string Text(string name) => row.GetProperty(name).GetString() ?? "";
            if (!Vc14MsiOrphans.IsCandidate(Text("Name")) || scan.Products.Any(p => p.Code.Equals(Text("Key"), StringComparison.OrdinalIgnoreCase))) continue;
            var input = new Vc14MsiRegistration(Text("Hive"), Text("RegistryView"), Text("Key"), Text("Name"), Text("Version"), Text("Publisher"),
                row.GetProperty("WindowsInstaller").GetInt32(), Text("UninstallString"), Text("QuietUninstallString"), true, new string('A', 64));
            if (Vc14MsiOrphans.Describe(input, scan, _ => new(true, "SIMULATED: absence assumed for policy replay")) is not { Kind: "msi-orphan" })
                throw new Exception("Snapshot identity/command rejected: " + input.Key);
            count++;
        }
        if (count != 4) throw new Exception("Expected the four stale logs13 records");
        if (Vc14InstallerErrors.RegistryAccessIssue([Vc14DependencyPolicy.ReadInstallerLog(installerLog)]) is not { } issue)
            throw new Exception("Actual MSI access-denied error was not recognized");
        Console.WriteLine("Actual logs13 replay: four stale record identities/commands recognized under SIMULATED absence and shape; native contexts and complete registry values still need live verification. No writes.");
        Console.WriteLine(issue);
    }

    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        void Reject(Action operation, string message)
        { try { operation(); } catch (IOException) { Check(true, message); return; } throw new Exception(message); }
        var codes = new[] { "{4BF7CE18-E151-449F-9190-6CDBED0BB4A2}", "{582BA719-E6F1-4651-A873-049E6A0DDDAF}",
            "{5A0DFA55-3851-45BC-8B20-95EA4BF5812D}", "{773AD50D-AAE6-4BA1-AD01-B5A38874C840}" };
        var absent = new Vc14MsiAbsence(true, "simulated complete absence");
        var scan = new Vc14Scan([], [], []);
        var rows = codes.Select((code, i) => new Vc14MsiRegistration("LocalMachine", "Registry32", code,
            "Microsoft Visual C++ 2022 X86 " + (i % 2 == 0 ? "Minimum" : "Additional") + " Runtime - " + (i < 2 ? "14.51.36247" : "14.50.35719"),
            i < 2 ? "14.51.36247" : "14.50.35719", "Microsoft Corporation", 1, "MsiExec.exe /" + (i < 2 ? "X" : "I") + code, "", true, new string((char)('A' + i), 64))).ToArray();
        foreach (var row in rows)
        {
            var description = Vc14MsiOrphans.Describe(row, scan, _ => absent);
            Check(description is { Kind: "msi-orphan", CanClean: true } && description.RegistrationFingerprint == row.ValuesFingerprint,
                "logs13 stale MSI display record is recognized only with absence proof");
            Check(Vc14MsiOrphans.Describe(row, scan, _ => new(false, "all-user access denied")) is { Kind: "keep" }, "unknown MSI state retained");
            Check(Vc14MsiOrphans.Describe(row, scan, _ => throw new IOException("query failed")) is { Kind: "keep" }, "query failure retained");
        }
        var sample = rows[0];
        foreach (var bad in new[] {
            sample with { Key = "../outside" }, sample with { Key = Guid.Empty.ToString("B") }, sample with { Hive = "Users" },
            sample with { View = "Default" }, sample with { Publisher = "Other publisher" }, sample with { Publisher = "" },
            sample with { WindowsInstaller = 0 }, sample with { WindowsInstaller = "1" }, sample with { Version = "14.50.35719" },
            sample with { SimpleShape = false }, sample with { ValuesFingerprint = "" },
            sample with { Uninstall = "MsiExec.exe /X" + codes[1] }, sample with { QuietUninstall = "MsiExec.exe /X" + codes[1] },
            sample with { Uninstall = "MsiExec.exe /X" + codes[0] + " & another.exe" },
            sample with { Uninstall = "C:\\untrusted\\msiexec.exe /X" + codes[0] },
            sample with { Name = sample.Name.Replace("X86", "ARM64") }, sample with { Name = sample.Name + " Debug" },
            sample with { Name = "Third-party software" }, sample with { Name = sample.Name.Replace("2022", "2013") },
        })
        {
            var called = false;
            var item = Vc14MsiOrphans.Describe(bad, scan, _ => { called = true; return absent; });
            Check(item?.CanClean != true && !called, "bad registry identity must be rejected before MSI queries");
        }
        var installed = new Vc14Entry(sample.Key, sample.Name, sample.Version, "x86", "Minimum", Guid.NewGuid().ToString("B"));
        Check(Vc14MsiOrphans.Describe(sample, scan with { Products = [installed] }, _ => absent) is { Kind: "keep" }, "installed product protected");
        Check(Vc14MsiOrphans.Describe(sample, scan with { Issues = ["scan incomplete"] }, _ => absent) is { Kind: "keep" }, "incomplete scan protected");
        Check(Vc14MsiOrphans.EvaluateAbsence(-1, [259, 259, 259]).ConfirmedAbsent, "complete native absence");
        Check(Vc14MsiOrphans.EvaluateAbsence(-1, [1605, 259, 1605]).ConfirmedAbsent, "context-specific unknown products");
        foreach (var state in new[] { -2, 0, 1, 2, 5 })
            Check(!Vc14MsiOrphans.EvaluateAbsence(state, [259, 259, 259]).ConfirmedAbsent, "non-unknown product state protected");
        foreach (uint error in new uint[] { 0, 5, 87, 234, 1601, 1610, 1627 })
            for (var index = 0; index < 3; index++)
            { uint[] results = [259, 259, 259]; results[index] = error; Check(!Vc14MsiOrphans.EvaluateAbsence(-1, results).ConfirmedAbsent, "present/error context protected"); }
        Check(!Vc14MsiOrphans.EvaluateAbsence(-1, [259, 259]).ConfirmedAbsent, "all query scopes required");
        var approved = Vc14MsiOrphans.Describe(sample, scan, _ => absent)!;
        var events = new List<string>(); var exists = true;
        Vc14MsiOrphans.CleanLeaf(approved, () => { events.Add("read"); return exists ? approved : null; }, () => events.Add("backup"),
            () => { events.Add("delete"); exists = false; return true; }, () => false, () => events.Add("attempt"));
        Check(events.SequenceEqual(new[] { "read", "backup", "read", "attempt", "delete", "read" }), "backup and recheck precede deletion, followed by actual absence");
        var deletes = 0;
        Reject(() => Vc14MsiOrphans.CleanLeaf(approved, () => approved, () => throw new IOException("backup failed"), () => { deletes++; return true; }, () => false, () => { }), "backup failure stops deletion");
        Check(deletes == 0, "no delete without backup");
        var changed = false;
        Reject(() => Vc14MsiOrphans.CleanLeaf(approved, () => changed ? approved with { RegistrationFingerprint = new string('F', 64) } : approved,
            () => changed = true, () => { deletes++; return true; }, () => false, () => { }), "post-backup rewrite blocks deletion");
        Check(deletes == 0, "changed raw values cannot be deleted");
        Reject(() => Vc14MsiOrphans.CleanLeaf(approved, () => approved with { Kind = "keep" }, () => { }, () => { deletes++; return true; }, () => false, () => { }), "newly installed product blocks deletion");
        var blocked = false;
        Reject(() => Vc14MsiOrphans.CleanLeaf(approved, () => approved, () => blocked = true, () => { deletes++; return true; }, () => blocked, () => { }), "installer becoming busy blocks deletion");
        Check(deletes == 0, "no deletion after blocked state");
        Reject(() => Vc14MsiOrphans.CleanLeaf(approved, () => approved, () => { }, () => false, () => false, () => { }), "unconfirmed deletion cannot succeed");
        Reject(() => Vc14MsiOrphans.CleanLeaf(approved, () => approved, () => { }, () => true, () => false, () => { }), "recreated registration cannot succeed");

        Fake Environment() => new(rows.Select(r => Vc14MsiOrphans.Describe(r, scan, _ => absent)!).ToList());
        var env = Environment(); var preview = Vc14Reinstall.Preview(env, Vc14RepairMode.RegistrationCleanup);
        Check(preview.CanRepair && preview.RemovalPlan.Count == 4 && preview.RemovalPlan.All(p => !p.CanRemove), "registration-only preview never makes an uninstall plan");
        var result = await Vc14Reinstall.RunAsync(env, preview.Fingerprint, null, default, Vc14RepairMode.RegistrationCleanup);
        Check(result.Success && env.Products.Count == 4 && env.Items.Count == 0 && env.Cleanups == 1 && env.Checkpoint == env.OriginalCheckpoint,
            "independent cleanup preserves healthy components and pending checkpoint");
        Check(!Vc14Reinstall.Preview(env, Vc14RepairMode.RegistrationCleanup).CanRepair, "no stale records means no repeat mutation");
        var stale = Environment(); var consent = Vc14Reinstall.Preview(stale, Vc14RepairMode.RegistrationCleanup);
        stale.Items[0] = stale.Items[0] with { RegistrationFingerprint = new string('E', 64) };
        Check(!(await Vc14Reinstall.RunAsync(stale, consent.Fingerprint, null, default, Vc14RepairMode.RegistrationCleanup)).Success && stale.Cleanups == 0, "changed evidence invalidates confirmation");
        foreach (var failure in new[] { "safe", "blocked", "unknown" })
        {
            var denied = Environment(); denied.IsSafeMode = failure == "safe"; denied.Blocked = failure == "blocked"; denied.Unknown = failure == "unknown";
            Check(!Vc14Reinstall.Preview(denied, Vc14RepairMode.RegistrationCleanup).CanRepair && denied.Cleanups == 0, "unready state cannot mutate");
        }
        var postChanged = Environment(); postChanged.ChangeProductsAfterCleanup = true;
        var changedConsent = Vc14Reinstall.Preview(postChanged, Vc14RepairMode.RegistrationCleanup);
        Check(!(await Vc14Reinstall.RunAsync(postChanged, changedConsent.Fingerprint, null, default, Vc14RepairMode.RegistrationCleanup)).Success, "post-cleanup component change is not success");
        var cancelled = Environment(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Check(!(await Vc14Reinstall.RunAsync(cancelled, Vc14Reinstall.Preview(cancelled, Vc14RepairMode.RegistrationCleanup).Fingerprint, null,
            cancellation.Token, Vc14RepairMode.RegistrationCleanup)).Success && cancelled.Cleanups == 0, "cancel does not clean");

        const string deniedLog = "MSI (s): Product: Runtime -- Error 1402. Could not open key: UNKNOWN\\Components\\ABC\\DEF. System error 5. Verify that you have sufficient access to that key.";
        Check(Vc14InstallerErrors.RegistryAccessIssue([deniedLog]) is { } issue && issue.Contains("1402") && issue.Contains("UNKNOWN\\Components\\ABC\\DEF"), "registry error identifies the actual denied key");
        Check(Vc14InstallerErrors.RegistryAccessIssue(["Note: 1: 1402 2: UNKNOWN\\missing 3: 2"]) is null, "ordinary missing-key MSI note is not a permissions error");
        Check(Vc14InstallerErrors.RegistryAccessIssue([deniedLog.Replace("System error 5", "System error 2")]) is null, "other system errors not mislabeled denied");
        var work = Path.Combine(Path.GetTempPath(), "deltanfd-reg-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var path = Path.Combine(work, "bundle-guid-attempt.log");
            File.WriteAllText(path, "wrapper failed 1603");
            File.WriteAllText(Path.Combine(work, "bundle-guid-attempt_000_vcRuntimeAdditional_x64.log"), deniedLog, Encoding.Unicode);
            var command = new ProcessStartInfo("not-executed.exe"); command.ArgumentList.Add("/log"); command.ArgumentList.Add(path);
            var enriched = Vc14InstallerErrors.Explain(new(1603, Detail: "failed"), command, work);
            Check(!enriched.Success && !enriched.PostcheckPassed && enriched.Detail.Contains("注册表访问被拒绝"), "UTF16 child MSI log explains parent bundle 1603");
            Check(Vc14InstallerErrors.Explain(new(0), command, work).Detail.Length == 0, "success not poisoned by error text");
            Check(Vc14InstallerErrors.Explain(new(1603, true), command, work).Unknown, "unknown transaction preserved");
            File.WriteAllText(Path.Combine(work, "bundle-guid-attempt_000_vcRuntimeAdditional_x64.log"), "no access error");
            File.WriteAllText(Path.Combine(work, "unrelated-old_000.log"), deniedLog);
            Check(Vc14InstallerErrors.Explain(new(1603), command, work).Detail.Length == 0, "other attempt logs never used");
            Check(Vc14InstallerErrors.Explain(new(1603), command, Path.Combine(work, "different")).Detail.Length == 0, "outside diagnostic path not read");
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(work)) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
                || !Path.GetFileName(work).StartsWith("deltanfd-reg-checks-", StringComparison.Ordinal)) throw new IOException("Invalid test cleanup path");
            Directory.Delete(work, true);
        }
        Console.WriteLine($"VC14 stale registration and installer-error checks: {count} passed. Simulated registry deletion/contexts, isolated log files; no installers, registry or services changed.");
    }

    private sealed class Fake(List<Vc14ResidueItem> items) : IVc14ReinstallEnvironment
    {
        public List<Vc14ResidueItem> Items = items;
        public List<Vc14Entry> Products = Vc14Catalog.Families.Select((f, i) => new Vc14Entry("{00000000-0000-0000-0000-00000000000" + i + "}",
            "baseline", Vc14Reinstall.BaselineVersion, f.Arch, f.Role, f.Upgrade)).ToList();
        public bool IsSafeMode { get; set; }
        public bool Blocked, Unknown, ChangeProductsAfterCleanup;
        public int Cleanups;
        public readonly Vc14Continuation OriginalCheckpoint = new(Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow) { UninstallVerified = true };
        public Vc14Continuation Checkpoint => OriginalCheckpoint;
        public Vc14Scan Scan() => new(Products.ToArray(), [], Unknown ? ["read error"] : []);
        public string? Blocker() => Blocked ? "blocked" : null;
        public IReadOnlyList<Vc14ResidueItem> Residues(Vc14Scan scan) => Items.ToArray();
        public OperationResult CleanupRegistrations(Vc14Scan scan, IReadOnlyList<Vc14ResidueItem> confirmed, IProgress<string>? progress, CancellationToken token)
        {
            Cleanups++;
            if (Vc14ResidueCleanup.Fingerprint(Items) != Vc14ResidueCleanup.Fingerprint(confirmed)) throw new IOException("changed");
            Items.Clear(); if (ChangeProductsAfterCleanup) Products.Clear();
            return new() { Success = true, Message = "simulated leaf cleanup" };
        }
        public Vc14Continuation? PendingInstall() => Checkpoint;
        public void VerifyBaselines() => throw new Exception("must not access installer payloads");
        public void VerifyInstalled() => throw new Exception("must not run installation verification");
        public string DiagnosticsDirectory => "simulated";
        public void Trace(string stage, object value) { }
        public Task<Vc14StepResult> RemoveAsync(Vc14RemovalPlanItem item, IProgress<string>? progress, CancellationToken token) => throw new Exception("must not uninstall");
        public Task<Vc14StepResult> InstallAsync(string arch, IProgress<string>? progress, CancellationToken token) => throw new Exception("must not install");
    }
}
