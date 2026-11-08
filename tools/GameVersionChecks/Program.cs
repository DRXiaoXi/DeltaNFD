using System.Text;
using System.Text.Json;
using DeltaNFD.Services;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL: " + label);
        _checks++;
        Console.WriteLine("PASS: " + label);
    }
    private static void Reject(Action run, string label)
    {
        try { run(); } catch (Exception ex) when (ex is IOException or JsonException) { Check(true, label); return; }
        throw new Exception("FAIL: " + label);
    }
    private static string Rail(string root) => Path.Combine(root, "rail_files", "rail_game_build_info.dat");
    private static string Resource(string payload) => Path.Combine(payload, "Saved", "Config", "WindowsClient", "GameResVersion.ini");
    private static void Write(string path, string text, Encoding? encoding = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, encoding ?? new UTF8Encoding(false));
    }
    private static void Build(string install, string version = "1.101.37117.36.9", string build = "227060") =>
        Write(Rail(install), JsonSerializer.Serialize(new { version_name = version, repo_build_id = build }));
    private static void Res(string payload, string version = "1.101.37117.40", string pending = "1.999.99999.99") =>
        Write(Resource(payload), $"[/Script/GameCore.GameResVersion]\r\nGameResVersionStr={version}\r\nGameResNewVersionStr={pending}\r\nPSOVersionStr={version}\r\n");
    private static string Payload(string install)
    {
        var payload = Path.Combine(install, "DeltaForce");
        Directory.CreateDirectory(Path.Combine(payload, "Content", "Paks"));
        return payload;
    }
    private static (string, string)[] Tokens(GameVersionCheckResult result) =>
        result.Entries.Where(e => e.MaintenancePending).Select(e => (e.Current.GameRoot, e.ChangeToken)).ToArray();

    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--inspect-readonly")
        {
            Console.WriteLine(JsonSerializer.Serialize(GameVersionTracker.ReadObservation(args[1])));
            return;
        }
        var scratch = Path.Combine(Path.GetTempPath(), "DeltaNFD-GameVersionChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var install = Path.Combine(scratch, "main");
            var payload = Payload(install);
            Build(install); Res(payload);
            var state = Path.Combine(scratch, "tool-data", "game-versions.json");
            var tracker = new GameVersionTracker(state);
            var roots = new[] { install, payload };
            var initial = tracker.Refresh(roots);
            Check(initial.Entries.Count == 1 && initial.Errors.Count == 0, "wrapper/direct aliases share one install identity");
            Check(!initial.Entries[0].MaintenancePending && File.Exists(state), "first observation persists baseline without update warning");
            Check(initial.Entries[0].Current.LauncherVersion == "1.101.37117.36.9" && initial.Entries[0].Current.ResourceVersion == "1.101.37117.40", "launcher and hot-update resource versions are tracked separately");
            var bytes = File.ReadAllBytes(state);
            tracker.Refresh(roots);
            Check(bytes.SequenceEqual(File.ReadAllBytes(state)), "unchanged observation does not rewrite state");
            Write(Path.Combine(payload, "Saved", "PSOCache", "GameVer_OLD", "old.cache"), "user-cache");
            Write(Path.Combine(payload, "Saved", "Config", "WindowsClient", "PSOCacheConfigHD.ini"), "[PSOCache]\nGameVersion=0.0.0.1\n");
            Check(!tracker.Refresh(roots).Entries[0].MaintenancePending, "old shader-cache metadata cannot impersonate current game version");
            Res(payload, "1.102.40000.1");
            var changed = tracker.Refresh(roots);
            Check(!changed.Entries[0].MaintenancePending, "resource-only change does not raise maintenance reminder");
            Check(changed.Entries[0].Current.ResourceVersion == "1.102.40000.1", "resource-only change remains available as reference");
            Build(install, build: "2270601");
            changed = tracker.Refresh(roots);
            Check(changed.Entries[0].MaintenancePending, "launcher build change raises maintenance reminder");
            Check(changed.Entries[0].PreviousVersion.Contains("1.102.40000.1"), "old observation retained for change comparison");
            var oldTokens = Tokens(changed);
            tracker = new GameVersionTracker(state);
            Check(tracker.Refresh(roots).Entries[0].MaintenancePending, "pending reminder survives process restart");
            Check(tracker.Acknowledge([(payload, "wrong")]) == 0, "stale or invalid acknowledgement cannot clear reminder");
            Build(install, build: "227061");
            Check(tracker.Acknowledge(oldTokens) == 0, "source changed before next poll cannot be acknowledged as the displayed update");
            changed = tracker.Refresh(roots);
            Check(Tokens(changed)[0].Item2 != oldTokens[0].Item2, "build-only change rotates acknowledgement identity");
            Check(tracker.Acknowledge(oldTokens) == 0, "old dialog cannot dismiss a newer recorded update");
            Check(tracker.Acknowledge(Tokens(changed)) == 1 && !tracker.Refresh(roots).Entries[0].MaintenancePending, "explicit current-version acknowledgement dismisses only that reminder");
            Res(payload, "1.100.20000.1");
            Build(install, "1.100.20000.1.1", "227000");
            changed = tracker.Refresh(roots);
            Check(changed.Entries[0].MaintenancePending, "game rollback is also a version change");
            var goodRail = File.ReadAllBytes(Rail(install));
            File.Delete(Rail(install));
            var missing = tracker.Refresh(roots);
            Check(missing.Errors.Count == 1 && missing.Entries[0].MaintenancePending, "temporarily missing known source retains old version and reminder");
            Check(tracker.Acknowledge(Tokens(missing)) == 0, "incomplete source cannot confirm maintenance");
            File.WriteAllBytes(Rail(install), goodRail);
            Write(Rail(install), "{\"version_name\":");
            var corrupt = tracker.Refresh(roots);
            Check(corrupt.Errors.Count == 1 && corrupt.Entries[0].MaintenancePending, "in-progress or malformed launcher update never resets baseline");
            Write(Rail(install), new string('x', 65537));
            Check(tracker.Refresh(roots).Errors.Count == 1, "oversized metadata rejected");
            Write(Rail(install), "{\"version_name\":\"1.2\",\"version_name\":\"2.3\",\"repo_build_id\":\"1\"}");
            Check(tracker.Refresh(roots).Errors.Count == 1, "duplicate version keys rejected");
            File.WriteAllBytes(Rail(install), goodRail);
            Write(Resource(payload), "[/Script/GameCore.GameResVersion]\nGameResVersionStr=1.2\nGameResVersionStr=2.3\n");
            var duplicateResource = tracker.Refresh(roots);
            Check(duplicateResource.Errors.Count == 0 && duplicateResource.Entries[0].Current.ResourceVersion == "", "duplicate resource fields cannot block valid launcher version");
            Write(Resource(payload), "[OtherSection]\nGameResVersionStr=1.2\n");
            Check(tracker.Refresh(roots).Entries[0].Current.ResourceVersion == "", "unrelated INI section is not a resource version");
            Res(payload, "1.100.20000.1");
            var expected = Tokens(tracker.Refresh(roots));
            Check(tracker.Acknowledge(expected) == 1, "pending state is recoverable after source repair");
            var alternate = Path.Combine(scratch, "experimental");
            var secondPayload = Payload(alternate); Build(alternate, "2.100.1.1.1", "300000"); Res(secondPayload, "2.100.1.1");
            var allRoots = new[] { install, payload, alternate, secondPayload };
            Check(tracker.Refresh(allRoots).Entries.Count == 2, "parallel regular/experimental installations keep separate baselines");
            Build(alternate, "2.101.1.1.1", "300001");
            var two = tracker.Refresh(allRoots);
            Check(two.Entries.Count(e => e.MaintenancePending) == 1 && two.Entries.Single(e => e.MaintenancePending).Current.GameRoot == secondPayload, "one installation updating does not flag another");
            Check(tracker.Refresh([install]).Entries.All(e => !e.MaintenancePending), "unregistered install reminder is not attributed to active install");
            Check(tracker.Refresh(allRoots).Entries.Any(e => e.MaintenancePending), "temporarily unselected install retains reminder when rediscovered");
            Res(payload, "1.100.20000.2", "9.9.9.9");
            Build(install, "1.103.1.1.1", "227062");
            two = tracker.Refresh(allRoots);
            Check(two.Entries.Count(e => e.MaintenancePending) == 2, "multiple updates may remain pending independently");
            var firstToken = Tokens(two).Where(t => t.Item1 == payload).ToArray();
            Check(tracker.Acknowledge(firstToken) == 1 && tracker.Refresh(allRoots).Entries.Count(e => e.MaintenancePending) == 1, "acknowledgement is scoped to selected installation");
            Check(File.ReadAllText(Path.Combine(payload, "Saved", "PSOCache", "GameVer_OLD", "old.cache")) == "user-cache", "version mechanism never deletes real cache or edits game files");
            var direct = Path.Combine(scratch, "direct"); Directory.CreateDirectory(Path.Combine(direct, "Content", "Paks")); Res(direct, "3.1.2.3");
            Check(tracker.Refresh([direct]).Entries.Single().Current.ResourceVersion == "3.1.2.3", "resource metadata supports direct/non-WeGame layout");
            Build(direct, "3.1.2.3.1", "5");
            Check(!tracker.Refresh([direct]).Entries.Single().MaintenancePending, "adding a newly available version source enriches baseline without phantom update");
            var noVersion = Path.Combine(scratch, "no-version"); Directory.CreateDirectory(Path.Combine(noVersion, "Content", "Paks"));
            var no = tracker.Refresh([noVersion]);
            Check(no.Entries.Count == 0 && no.Errors.Count == 1, "unrecognized installation never fabricates a version");
            Write(Resource(direct), "[/Script/GameCore.GameResVersion]\nGameResVersionStr=3.1.2.4\n", Encoding.Unicode);
            var unicodeResource = tracker.Refresh([direct]).Entries.Single();
            Check(unicodeResource.Current.ResourceVersion == "3.1.2.4" && !unicodeResource.MaintenancePending, "UTF-16 resource reference is supported without an update reminder");
            VerifyPrewarmAndMigration(scratch);
            var before = File.ReadAllBytes(state);
            foreach (var invalid in new[] { "{", "{}", "null", "{\"SchemaVersion\":2,\"Entries\":[]}", "{\"SchemaVersion\":1,\"Entries\":null}", "{\"SchemaVersion\":1,\"Entries\":[{}]}" })
            {
                Write(state, invalid); var original = File.ReadAllBytes(state);
                Reject(() => tracker.Refresh(roots), "corrupt/unsupported state preserved: " + invalid);
                Check(original.SequenceEqual(File.ReadAllBytes(state)), "failed load cannot overwrite prior record");
            }
            File.WriteAllBytes(state, before);
            Console.WriteLine($"TOTAL {_checks} checks passed; only isolated fixtures were modified.");
        }
        finally
        {
            if (!Path.GetFullPath(scratch).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(scratch).StartsWith("DeltaNFD-GameVersionChecks-", StringComparison.Ordinal))
                throw new IOException("Unexpected fixture cleanup path.");
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static void VerifyPrewarmAndMigration(string scratch)
    {
        var install = Path.Combine(scratch, "prewarm"); var payload = Payload(install);
        Build(install, "1.101.37119.18.4", "232607"); Res(payload, "1.101.37119.9");
        var path = Path.Combine(scratch, "prewarm-state.json");
        var tracker = new GameVersionTracker(path); tracker.Refresh([install]);
        foreach (var version in new[] { "1.101.37119.10", "1.101.37119.9", "1.101.37119.11" })
        {
            Res(payload, version);
            Check(!tracker.Refresh([install]).Entries.Single().MaintenancePending, "config rewrite alone remains quiet: " + version);
        }
        File.Delete(Resource(payload));
        Check(tracker.Refresh([install]).Errors.Count == 0, "prewarm can remove resource config without losing launcher evidence");
        Write(Resource(payload), "being rewritten");
        var transient = tracker.Refresh([install]);
        Check(transient.Errors.Count == 0 && !transient.Entries.Single().MaintenancePending, "partial resource config does not create update or read-error banner");
        Res(payload, "1.101.37119.10");
        Build(install, "1.101.37119.18.4", "232608");
        var changed = tracker.Refresh([install]); var expected = Tokens(changed);
        Res(payload, "1.101.37119.11");
        Check(Tokens(tracker.Refresh([install])).SequenceEqual(expected), "resource refresh cannot rotate a real update token");
        Res(payload, "1.101.37119.12");
        Check(tracker.Acknowledge(expected) == 1, "resource rewrite during dialog cannot invalidate launcher-scoped acknowledgement");
        Build(install, "1.101.37119.18.4", "232607"); tracker.Refresh([install]);
        Build(install, "1.101.37119.18.4", "232608");
        Check(tracker.Refresh([install]).Entries.Single().MaintenancePending, "real build rollback keeps reminder even if it returns to earlier build");

        var current = GameVersionTracker.ReadObservation(payload);
        foreach (var row in new[]
        {
            ("启动器 1.101.37119.18.4（构建 232608）；资源 1.101.37119.9", false),
            ("启动器 1.101.37119.18.4（构建 232607）；资源 1.101.37119.9", true),
            ("来源不明的旧记录", true),
            ("资源 1.101.37119.9", false),
        })
        {
            var migration = Path.Combine(scratch, "migration-" + Guid.NewGuid().ToString("N") + ".json");
            Write(migration, JsonSerializer.Serialize(new GameVersionState { Entries = [new()
            { Current = current, PreviousVersion = row.Item1, MaintenancePending = true, ChangeToken = Guid.NewGuid().ToString("N"), RecordedUtc = DateTimeOffset.UtcNow }] }));
            var result = new GameVersionTracker(migration).Refresh([install]);
            Check(result.Entries.Single().MaintenancePending == row.Item2, "legacy reminder migration: " + row.Item1);
            Check(JsonSerializer.Deserialize<GameVersionState>(File.ReadAllText(migration))!.UpdateDetectionPolicy == 1, "migration records policy once");
        }
    }
}
