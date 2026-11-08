using DeltaNFD.Inspection;
using DeltaNFD.Services;
using DeltaNFD.Services.TweakDb;
using System.Text.Json;

internal static class SecurityFeatureChecks
{
    internal static async Task RunAsync()
    {
        var isolated = Path.Combine(Path.GetTempPath(), "DeltaNFD-security-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolated);
        var token = Guid.NewGuid(); File.WriteAllText(Path.Combine(isolated, "isolated-test.marker"), token.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(isolated, token);
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
        var disabled = new SecurityServiceEvidence(true, 4, 1);
        var absent = new SecurityServiceEvidence(false, null, null);
        Check(SecurityFeatureInspection.Evaluate("DefenderName", disabled, absent).State == BxState.On, "Missing optional network component does not create partial disabled");
        Check(SecurityFeatureInspection.Evaluate("SecurityHealthService", disabled).State == BxState.On, "Stopped and disabled security service is On despite auxiliary startup settings");
        foreach (var running in new uint[] { 2, 3, 4 })
            Check(SecurityFeatureInspection.Evaluate("DefenderName", disabled with { RuntimeState = running }, absent).State == BxState.Mixed, "Configured disable alone does not prove stopped runtime");
        Check(SecurityFeatureInspection.Evaluate("DefenderName", disabled, new(true, 3, 4)).State == BxState.Mixed, "Running Defender network protection remains partial");
        Check(SecurityFeatureInspection.Evaluate("DefenderName", disabled, new(null, null, null)).State == BxState.Unknown, "Unknown optional service is not equivalent to absent");
        foreach (var evidence in new[] { new SecurityServiceEvidence(null, 4, null), new(true, 4, null), new(true, null, 1) })
            Check(SecurityFeatureInspection.Evaluate("SecurityHealthService", evidence).State == BxState.Unknown, "Unreadable primary evidence cannot report disabled");
        Check(SecurityFeatureInspection.Evaluate("DefenderName", absent).State == BxState.NotApplicable, "Absent primary is not fabricated success");
        Check(SecurityFeatureInspection.Evaluate("SecurityHealthService", new(true, 3, 4)).State == BxState.Off, "Configured running service is not disabled");
        var functional = SecurityFeatureInspection.Evaluate("DefenderName", disabled, absent);
        var result = BxService.SecurityApplyResult(functional, OperationResult.Fail("optional service exit 1060"), 2);
        Check(result.Success && result.IsPartial && !result.OutcomeUnknown && result.Message.Contains("核心服务已禁用"), "Nonzero auxiliary result reconciles with confirmed core disable without claiming all steps succeeded");
        var pending = BxService.SecurityApplyResult(new(BxState.Mixed, true, "pending runtime"), OperationResult.Fail("stop pending"), 1);
        Check(pending.Success && pending.IsPartial && pending.RequiresReboot, "Configured pending service explicitly requires reboot");
        var unknown = BxService.SecurityApplyResult(new(BxState.Unknown, true, "unknown runtime"), OperationResult.Ok("writes returned 0"), 1);
        Check(!unknown.Success && unknown.OutcomeUnknown, "Successful writes cannot mask unreadable runtime");
        var item = new BxItem { Name = "SecurityHealthService", Tweaks = [new() { TweakType = "REG", Path = @"HKLM\SYSTEM\CurrentControlSet\Services\SecurityHealthService", Key = "Start" }] };
        var mine = new BxBackupEntry { Id = @"Reg|HKEY_LOCAL_MACHINE|SYSTEM\CurrentControlSet\Services\SecurityHealthService|Start", Kind = "Reg", KeyPath = @"SYSTEM\CurrentControlSet\Services\SecurityHealthService", ValueName = "Start", ValueKind = 4, Data = "3" };
        var other = new BxBackupEntry { Id = "Service|wuauserv", Kind = "Service", KeyPath = "wuauserv", ValueKind = 3 };
        var touched = new List<string>(); var removed = new List<string>();
        var restore = BxService.RestoreSecurityEntries(item, [mine, other], e => touched.Add(e.Id), ids => removed.AddRange(ids));
        Check(restore.Success && restore.RequiresReboot && touched.SequenceEqual([mine.Id]) && removed.SequenceEqual([mine.Id]), "Individual restore only touches this item and removes only its verified backups");
        touched.Clear(); removed.Clear();
        var forged = new BxBackupEntry { Id = mine.Id, KeyPath = @"SOFTWARE\ThirdParty", ValueName = "Start" };
        Check(!BxService.RestoreSecurityEntries(item, [forged], e => touched.Add(e.Id), ids => removed.AddRange(ids)).Success && touched.Count == 0 && removed.Count == 0,
            "Forged backup identity cannot broaden restoration scope");
        Check(!BxService.RestoreSecurityEntries(item, [mine], _ => throw new IOException("third party current value"), ids => removed.AddRange(ids)).Success && removed.Count == 0,
            "Failed/conflicted restore retains original responsibility");
        Check(!BxService.RestoreSecurityEntries(item, [other], e => touched.Add(e.Id), _ => { }).Success && touched.Count == 0, "No local backup does not write fixed defaults");
        var inspector = new BxInspection(securityQuery: name => name == "DefenderName" ? functional : new(BxState.On, true, "core stopped"));
        var defender = new BxItem { Name = "DefenderName", Tweaks = [new() { TweakType = "REG", Path = "not-read", Key = "auxiliary" }] };
        var states = await inspector.GetItemStatesAsync([defender, item]);
        Check(states[defender] == BxState.On && states[item] == BxState.On, "Actual batch inspection uses core evidence instead of mismatched auxiliary registry values");
        BxCatalog.ConfigureAssetRoot(AppContext.BaseDirectory);
        bool initial = true, exclusionsFail = false; int coreQueries = 0;
        var core = functional;
        DefenderGuard.PowerShellForTests = command => command.Contains("Get-MpComputerStatus")
            ? initial ? (0, "{\"av\":true,\"rtp\":true,\"tamper\":true}", "") : (1, null, "service unavailable")
            : command.Contains("Get-MpPreference") ? exclusionsFail ? (1, null, "unavailable") : (0, "[]", "")
            : (0, "Windows Defender", "");
        DefenderGuard.SecurityFeatureForTests = () => { coreQueries++; return core; };
        try
        {
            const string ops = @"REG HKLM\SOFTWARE\Policies\Microsoft\Windows Defender DisableAntiSpyware";
            var pre = await DefenderGuard.PreflightAsync([new() { Name = "Defender", Ops = ops, TurnOn = true }]);
            Check(pre is { DefenderOffTweakInBatch: true, InitialRealTimeProtectionOn: true }, "Disable intent preserved in actual preflight");
            initial = false;
            var post = await DefenderGuard.PostApplyAsync();
            Check(post.CleanupSucceeded && post.DefenderLeftOff && !DefenderGuard.HasPendingSession,
                "Actual post-apply accepts confirmed intentional core stop when cmdlet is unavailable and no temporary exclusions remain");
            initial = true;
            pre = await DefenderGuard.PreflightAsync([new() { Name = "Defender", Ops = ops, TurnOn = false }]);
            Check(pre is { DefenderOffTweakInBatch: false, DefenderRestoreTweakInBatch: true }, "Restore intent not confused with another disable");
            initial = false; core = new(BxState.Mixed, false, "stopped pending boot");
            post = await DefenderGuard.PostApplyAsync();
            Check(post.CleanupSucceeded && post.RequiresReboot && !post.DefenderLeftOff && !DefenderGuard.HasPendingSession,
                "Restore with known startup state explicitly defers protection verification until reboot without fake temporary cleanup failure");
            initial = true;
            pre = await DefenderGuard.PreflightAsync([new() { Name = "Defender", Ops = ops, TurnOn = true }]);
            initial = false; core = new(BxState.Unknown, true, "unknown");
            post = await DefenderGuard.PostApplyAsync();
            Check(!post.CleanupSucceeded && DefenderGuard.HasPendingSession, "Unknown core and missing cmdlet retain responsibility");
            core = functional; await DefenderGuard.PostApplyAsync();
            Check(!DefenderGuard.HasPendingSession, "Later confirmed status can finish retained temporary responsibility");
            File.WriteAllText(Path.Combine(isolated, "defender-session.json"), JsonSerializer.Serialize(new { Paths = new[] { isolated }, Report = pre }));
            exclusionsFail = true; var queries = coreQueries;
            post = await DefenderGuard.PostApplyAsync();
            Check(!post.CleanupSucceeded && DefenderGuard.HasPendingSession && coreQueries == queries,
                "Confirmed disabled core never bypasses unverified temporary exclusion cleanup");
        }
        finally
        {
            DefenderGuard.PowerShellForTests = null; DefenderGuard.SecurityFeatureForTests = null;
            try { Directory.Delete(isolated, true); } catch { }
        }
        Console.WriteLine($"Security feature reconciliation: {count} checks passed; fake service/registry/restore boundaries only.");
    }
}
