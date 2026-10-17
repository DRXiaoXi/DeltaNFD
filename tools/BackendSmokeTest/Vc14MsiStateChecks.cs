using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Services;

internal static class Vc14MsiStateChecks
{
    private const string Code = "{61B44572-8722-4DAF-8ACF-8E742D30BCC5}";
    private const string Current = "S-1-5-21-100";
    private const string Other = "S-1-5-21-200";
    private static int _checks;

    internal static void Run()
    {
        _checks = 0;
        foreach (var (state, name) in new[] { (-6, "BADCONFIG"), (-2, "INVALIDARG"), (-1, "UNKNOWN"), (0, "BROKEN"),
            (1, "ADVERTISED"), (2, "ABSENT"), (3, "LOCAL"), (4, "SOURCE"), (5, "DEFAULT"), (101, "UNRECOGNIZED") })
        {
            var platform = new Fake(); var session = new Vc14MsiDiagnosticSession(platform);
            var row = session.Observe(Code, state, "fixture");
            Check(row.StateCode == state && row.StateName == "INSTALLSTATE_" + name, "raw state and name retained " + state);
            Check(row.Contexts.Count == (state == 5 ? 0 : 5), "extra probes only for non-default or identity failure " + state);
            try
            {
                var read = Vc14Catalog.RequireInstalledState(Code, state, session, "policy");
                Check((state is -1 or 5) && read == (state == 5), "original installed-state decision " + state);
            }
            catch (Vc14MsiStateException ex)
            {
                Check(state is not -1 and not 5 && ex.Message.Contains("MSI 状态=" + state + "/") && ex.Diagnostic.StateCode == state,
                    "uncertain states still reject with exact code " + state);
            }
        }
        foreach (var invalid in new[] { "bad", "", Guid.Empty.ToString("B"), Guid.NewGuid().ToString("D") })
        {
            var fake = new Fake(); var rejected = false;
            try { Vc14MsiStateDiagnostics.Capture(invalid, 1, fake); } catch (ArgumentException) { rejected = true; }
            Check(rejected && fake.EnumCalls.Count == 0, "invalid identity never reaches native diagnostic reads");
        }

        var rich = new Fake
        {
            Enumeration = (code, sid, context, index) => index == 0 && (context == 4 || sid == "S-1-1-0" && context == 1)
                ? new(0, code, context, context == 4 ? "" : Other) : new(259, "", 0, ""),
        };
        var evidence = Vc14MsiStateDiagnostics.Capture(Code, 2, rich);
        Check(evidence.Contexts.Count == 5 && evidence.Contexts.All(c => c.Complete), "five installation scopes recorded");
        Check(rich.EnumCalls.Any(c => c.Sid is null && c.Context == 4) && rich.EnumCalls.Any(c => c.Sid == "S-1-1-0" && c.Context == 2),
            "correct machine and all-user scopes");
        Check(evidence.Contexts[0].Instances.Single().UserContext == "计算机级" && evidence.Contexts[3].Instances.Single().UserContext == "其他用户1", "machine and anonymous other-owner distinction");
        Check(rich.PropertyCalls.All(c => c.Context == 4 ? c.Sid is null : c.Sid == Other), "property reads target the exact enumerated context and owner");
        Check(evidence.Contexts[0].Instances[0].Properties.Any(p => p.Property == "State" && p.ReturnCode == 0 && p.Value == "5"), "per-instance State read");
        Check(evidence.Limitations.Contains("非托管仅通告") && evidence.Limitations.Contains("不能证明全机不存在"), "cross-user advertised-only limitation explicit");
        var wire = JsonSerializer.Serialize(evidence);
        Check(!wire.Contains(Current) && !wire.Contains(Other), "raw account SIDs not written to reports");

        var denied = new Fake { Enumeration = (_, sid, _, _) => new(sid is null ? 259u : 5u, "", 0, "") };
        var deniedResult = Vc14MsiStateDiagnostics.Capture(Code, 1, denied);
        Check(deniedResult.Contexts.Skip(3).All(c => !c.Complete && c.ReturnCode == 5 && c.Result == "ERROR_ACCESS_DENIED"), "access denied never becomes absence");
        Check(deniedResult.Summary.Contains("ERROR_ACCESS_DENIED") && deniedResult.Summary.Contains("查询未完成"), "failure shown in issue text");
        var partial = new Fake { Enumeration = (code, _, context, index) => context == 4
            ? index == 0 ? new(0, code, 4, "") : new(1610, "", 0, "") : new(1605, "", 0, "") };
        var partialResult = Vc14MsiStateDiagnostics.Capture(Code, 0, partial);
        Check(partialResult.Contexts[0] is { Complete: false, ReturnCode: 1610 } && partialResult.Contexts[0].Instances.Count == 1, "partial enumeration retains data and original failure");
        Check(partialResult.Contexts.Skip(1).All(c => c.Complete && c.ReturnCode == 1605), "no product distinguished from failure");
        var throws = new Fake { Enumeration = (_, _, _, _) => throw new IOException("fixture"), Property = (_, _, _, _) => throw new IOException("fixture") };
        var failed = Vc14MsiStateDiagnostics.Capture(Code, -6, throws);
        Check(failed.Contexts.All(c => !c.Complete && c.ReturnCode is null && c.Result == "QUERY_EXCEPTION") && failed.StateCode == -6, "probe exception never hides original state");
        var propertyFailure = new Fake { Enumeration = partial.Enumeration, Property = throws.Property };
        var incompleteProperties = Vc14MsiStateDiagnostics.Capture(Code, 2, propertyFailure);
        Check(incompleteProperties.Contexts[0].Instances[0].Properties.All(p => p.ReturnCode is null), "property exceptions preserved separately");
        Check(incompleteProperties.Summary.Contains("属性未确认"), "incomplete property data explained");
        foreach (var malformed in new[] { "code", "context", "sid", "owner" })
        {
            var bad = new Fake { Enumeration = (code, sid, context, index) => sid is null && context == 1 && index == 0
                ? new(0, malformed == "code" ? Guid.NewGuid().ToString("B") : code, malformed == "context" ? 4u : context,
                    malformed == "sid" ? "not-a-sid" : malformed == "owner" ? Other : Current) : new(259, "", 0, "") };
            var rejected = Vc14MsiStateDiagnostics.Capture(Code, 1, bad);
            Check(!rejected.Contexts[1].Complete && bad.PropertyCalls.Count == 0, "mismatching native record rejected: " + malformed);
        }
        var repeated = new Fake { Enumeration = (code, sid, context, _) => context == 4 ? new(0, code, 4, "") : new(259, "", 0, "") };
        Check(Vc14MsiStateDiagnostics.Capture(Code, 1, repeated).Contexts[0].Instances.Count == 1 && repeated.EnumCalls.Count < 20, "duplicate native rows cannot cause endless enumeration");

        var bounded = new Fake { Enumeration = (code, sid, context, index) => sid == "S-1-1-0" ? new(0, code, context, "S-1-5-21-" + (300 + index)) : new(259, "", 0, ""),
            Property = (_, _, _, property) => new(property, 0, "ERROR_SUCCESS", new string('中', 10000)) };
        var boundedResult = Vc14MsiStateDiagnostics.Capture(Code, 1, bounded);
        Check(boundedResult.Contexts.Skip(3).All(c => !c.Complete && c.Instances.Count == Vc14MsiStateDiagnostics.MaximumInstances), "per-scope enumeration cap explicit");
        var props = boundedResult.Contexts.SelectMany(c => c.Instances).SelectMany(i => i.Properties).ToArray();
        Check(props.Sum(p => p.Value.Length) == 4096 && props.Any(p => p.ValueTruncated), "aggregate property text bound with truncation evidence");
        var limitSession = new Vc14MsiDiagnosticSession(bounded);
        for (var i = 0; i < 40; i++) limitSession.Observe(Guid.NewGuid().ToString("B"), 1, "bounded fixture");
        var limitedRows = limitSession.Results;
        Check(limitSession.Truncated && limitedRows.Count < Vc14MsiDiagnosticSession.MaximumProducts &&
            JsonSerializer.SerializeToUtf8Bytes(limitedRows).Length <= Vc14MsiDiagnosticSession.MaximumReportBytes, "global evidence budget cannot overflow backend frame");
        var healthy = new Fake(); var healthySession = new Vc14MsiDiagnosticSession(healthy);
        healthySession.Observe(Code, 5, "normal"); healthySession.Observe(Code, 5, "normal again");
        Check(healthy.EnumCalls.Count == 0, "healthy state does not add context enumeration");
        healthySession.Observe(Code, 5, "identity read failed", expand: true);
        Check(healthy.EnumCalls.Count == 5 && healthySession.Results[0].Contexts.Count == 5, "identity failure expands a previously healthy state");
        healthySession.Observe(Code, 1, "changed");
        Check(healthy.EnumCalls.Count == 10 && healthySession.Results[0].ObservedStateCodes.SequenceEqual(new[] { 5, 1 }), "state change recorded and not served from stale evidence");

        // Logs16 identities only. The archive did not include actual state numbers, so states below are SIMULATED.
        var codes = new[] { Code, "{AECD4ED0-8A3B-41E9-92D1-6BEE0374CCAF}", "{5A0DFA55-3851-45BC-8B20-95EA4BF5812D}", "{773AD50D-AAE6-4BA1-AD01-B5A38874C840}" };
        var dedupPlatform = new Fake(); var dedup = new Vc14MsiDiagnosticSession(dedupPlatform); var issues = new List<string>();
        for (var path = 0; path < 2; path++)
        for (var i = 0; i < codes.Length; i++)
        {
            try { Vc14Catalog.RequireInstalledState(codes[i], new[] { 1, 2, 0, -6 }[i], dedup, path == 0 ? "升级家族" : "动态登记"); }
            catch (Exception ex) { dedup.AddIssue(issues, "fixture", ex, codes[i]); }
        }
        Check(issues.Count == 4 && dedup.Results.Count == 4 && dedupPlatform.EnumCalls.Count == 20, "duplicate discovery is four problems, one capture per product");
        Check(dedup.Results.All(d => d.Sources.Count == 2), "both discovery sources retained");
        var scan = new Vc14Scan([], [], issues) { MsiStateDiagnostics = dedup.Results };
        Check(!scan.IsMissing && scan.HasWarning && scan.Products.Count == 0, "diagnostics do not turn uncertain products into installed or missing");
        var registration = new Vc14MsiRegistration("LocalMachine", "Registry64", Code, "Microsoft Visual C++ 2022 X64 Minimum Runtime - 14.50.35719",
            "14.50.35719", "Microsoft Corporation", 1, "MsiExec.exe /I" + Code, "", true, new string('A', 64));
        var absenceRead = false;
        Check(Vc14MsiOrphans.Describe(registration, scan, _ => { absenceRead = true; return new(true, "must not be trusted"); }) is { Kind: "keep" } && !absenceRead,
            "uncertain diagnostic context can never authorize registration deletion");
        Check(Vc14Catalog.Fingerprint(scan) == Vc14Catalog.Fingerprint(scan with { MsiStateDiagnostics = [] }), "diagnostic details not converted into approval evidence");
        var roundTrip = JsonSerializer.Deserialize<Vc14Scan>(JsonSerializer.Serialize(scan, BackendJson.SerializerOptions), BackendJson.SerializerOptions)!;
        Check(roundTrip.MsiStateDiagnostics.Count == 4 && roundTrip.MsiStateDiagnostics[1].StateCode == 2, "structured state survives actual backend serialization options");
        Check(JsonSerializer.Deserialize<Vc14Scan>("{\"Products\":[],\"Notices\":[],\"Issues\":[]}")!.MsiStateDiagnostics.Count == 0, "old snapshots remain readable");
        Console.WriteLine($"MSI state diagnostics: {_checks} checks passed. States and contexts simulated; no install, uninstall, registry or service changes.");
    }

    internal static void ReadNative()
    {
        var result = Vc14MsiDiagnosticWindowsPlatform.ReadNative(Guid.NewGuid().ToString("B"));
        if (result.StateCode != -1 || result.Contexts.Count != 5) throw new Exception("Native diagnostic result unexpected");
        Console.WriteLine("Native MSI diagnostic read-only check: " + result.Summary);
        Console.WriteLine("Random absent product only; no install/uninstall, service, registry writes or identity changes.");
    }
    private static void Check(bool success, string message) { if (!success) throw new Exception(message); _checks++; }
    private sealed class Fake : IVc14MsiDiagnosticPlatform
    {
        public string? CurrentUserSid => Current;
        internal Func<string, string?, uint, uint, Vc14MsiEnumeration> Enumeration = (_, _, _, _) => new(259, "", 0, "");
        internal Func<string, string?, uint, string, Vc14MsiPropertyDiagnostic> Property = (_, _, _, property) => new(property, 0, "ERROR_SUCCESS",
            property == "State" ? "5" : property == "VersionString" ? "14.50.35719" : property == "LocalPackage" ? @"C:\Windows\Installer\fixture.msi" : "Microsoft Visual C++ fixture");
        internal List<(string? Sid, uint Context, uint Index)> EnumCalls { get; } = [];
        internal List<(string? Sid, uint Context, string Property)> PropertyCalls { get; } = [];
        public Vc14MsiEnumeration Enumerate(string code, string? sid, uint context, uint index)
        { EnumCalls.Add((sid, context, index)); return Enumeration(code, sid, context, index); }
        public Vc14MsiPropertyDiagnostic ReadProperty(string code, string? sid, uint context, string property)
        { PropertyCalls.Add((sid, context, property)); return Property(code, sid, context, property); }
    }
}
