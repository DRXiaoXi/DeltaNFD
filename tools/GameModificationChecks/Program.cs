using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using DeltaNFD.Backends;
using DeltaNFD.Services;

internal static class Program
{
    private static int _count;
    private static string _fixture = "";
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
        _count++; Console.WriteLine("PASS: " + message);
    }

    private sealed class Fixture
    {
        internal readonly string Root, Data, Icreate, Recorder;
        internal IEnumerable<string> Roots;
        internal string? Blocker;
        internal Action<string>? Fault;
        internal Fixture(string name, string? dataDirectory = null)
        {
            Root = Path.Combine(_fixture, name, "游戏 DeltaForce(2001918)");
            Data = dataDirectory ?? Path.Combine(_fixture, name, "data");
            Icreate = Path.Combine(Root, @"icreate\icreate-df\icreateDF.exe");
            Recorder = Path.Combine(Root, @"icreate\recorder-release\recorder.exe");
            WritePe(Path.Combine(Root, @"DeltaForce\Binaries\Win64\DeltaForceClient-Win64-Shipping.exe"), 1);
            WritePe(Icreate, 2); WritePe(Recorder, 3);
            Roots = [Root];
        }
        internal GameRecordingModification Engine() => new(Data, () => Roots, () => Blocker, s => Fault?.Invoke(s));
        internal GameRecordingItem Item(string id = "icreate") => Engine().Read().Items.Single(i => i.ComponentId == id);
        internal OperationResult Change(GameRecordingItem item, bool restore = false) => Engine().Change(new()
        { InstallationRoot = item.InstallationRoot, ComponentId = item.ComponentId, ExpectedFingerprint = item.Fingerprint, Restore = restore });
        internal string Journal => Directory.EnumerateFiles(Data, "*.json").Single();
        internal string Backup(string path)
        {
            var journalPath = Directory.EnumerateFiles(Data, "*.json").Single(p =>
                JsonNode.Parse(File.ReadAllText(p))!["ComponentId"]!.GetValue<string>() == (path == Recorder ? "recorder" : "icreate"));
            var j = JsonNode.Parse(File.ReadAllText(journalPath))!;
            return j["Version"]!.GetValue<int>() == 1 ? path + ".deltanfd-" + j["Token"]!.GetValue<string>() + ".bak"
                : Path.Combine(Data, "backups", j["Token"]!.GetValue<string>(), "original.bak");
        }
    }

    private static void WritePe(string path, byte marker)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
        var bytes = new byte[512]; bytes[0] = 0x4d; bytes[1] = 0x5a; bytes[0x3c] = 0x80;
        bytes[0x80] = 0x50; bytes[0x81] = 0x45; bytes[0x100] = marker;
        File.WriteAllBytes(path, bytes);
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void Main(string[] args)
    {
        if (args is ["--recording-status-readonly", var diagnosticData, var diagnosticInstallation])
        {
            // Diagnostic entry: read exactly the supplied installation/store; no Change/RestoreAll calls or settings migration.
            var engine = new GameRecordingModification(diagnosticData, () => [diagnosticInstallation], () => null);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(engine.Read()));
            return;
        }
        if (args is ["--host-recording-readonly"])
        {
            var probe = typeof(GameRecordingModification).GetMethod("Probe", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var store = Path.Combine(Path.GetDirectoryName(AppSettingsStore.DefaultPath)!, "GameModifications");
            foreach (var path in Directory.GetFiles(store, "*.json"))
            {
                var j = JsonNode.Parse(File.ReadAllText(path))!;
                var installationRoot = j["InstallationRoot"]!.GetValue<string>();
                var relative = j["ComponentId"]!.GetValue<string>() == "icreate" ? @"icreate\icreate-df\icreateDF.exe" : @"icreate\recorder-release\recorder.exe";
                var target = Path.Combine(installationRoot, relative);
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Record = j,
                    Target = probe.Invoke(null, [target]), Backup = probe.Invoke(null, [target + ".deltanfd-" + j["Token"]!.GetValue<string>() + ".bak"]) }));
            }
            return;
        }
        if (args.Length != 0) throw new ArgumentException("未知测试参数；未运行任何操作。");
        var root = FindRoot();
        var guid = Guid.NewGuid();
        _fixture = Path.GetFullPath(Path.Combine(root, "_buildcheck/recording-page-recovery-20261108/fixtures", guid.ToString("N")));
        Directory.CreateDirectory(_fixture);
        File.WriteAllText(Path.Combine(_fixture, "isolated-test.marker"), guid.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(_fixture, guid);
        Basic(); Failures(); Interruptions(); CentralAndLegacy(); StaleRecordReview(); CrossVolume(); Paths(); Wiring(root);
        Console.WriteLine($"TOTAL {_count} checks passed; only owned fixture files modified; no real registry, game, driver or timer changes.");
    }

    private static void Basic()
    {
        var f = new Fixture("basic");
        var original = Hash(f.Icreate); var recorder = Hash(f.Recorder);
        var attrs = File.GetAttributes(f.Icreate); var written = File.GetLastWriteTimeUtc(f.Icreate);
        var read = f.Engine().Read();
        Check(read.Complete && read.Items.Count == 2 && read.Items.All(i => i.CanApply && !i.HasRecord), "two exact components found");
        Check(!Directory.Exists(f.Data) && Hash(f.Icreate) == original && File.GetLastWriteTimeUtc(f.Icreate) == written, "status does not create data or change original");
        Check(f.Change(f.Item()).Success, "replace first component");
        Check(new FileInfo(f.Icreate).Length == 0 && File.GetAttributes(f.Icreate).HasFlag(FileAttributes.ReadOnly), "replacement is an empty readonly file");
        Check(Hash(f.Backup(f.Icreate)) == original && File.GetLastWriteTimeUtc(f.Backup(f.Icreate)) == written, "original bytes and modification time preserved");
        Check(Hash(f.Recorder) == recorder && f.Item().IsManaged && !f.Item().CanApply && f.Item().CanRestore, "independent component and correct managed state");
        Check(!f.Change(f.Item()).Success && Hash(f.Backup(f.Icreate)) == original, "repeated replace cannot backup the placeholder");
        Check(f.Change(f.Item("recorder")).Success && f.Engine().Read().Items.All(i => i.IsManaged), "both components replace independently");
        Check(f.Change(f.Item(), true).Success && Hash(f.Icreate) == original && File.GetAttributes(f.Icreate) == attrs, "fresh engine restores original bytes and attributes");
        Check(f.Item("recorder").IsManaged && !Directory.EnumerateFiles(Path.GetDirectoryName(f.Icreate)!, "*.deltanfd-*").Any(), "restore cleans only its own artifacts");
        Check(f.Engine().RestoreAll().Success && f.Engine().Read().Items.All(i => !i.HasRecord && i.CanApply), "recovery scope restores remaining component");
        var ro = new Fixture("readonly-original");
        File.SetAttributes(ro.Icreate, FileAttributes.ReadOnly | FileAttributes.Archive);
        var originalAttributes = File.GetAttributes(ro.Icreate);
        Check(ro.Change(ro.Item()).Success && ro.Change(ro.Item(), true).Success && File.GetAttributes(ro.Icreate) == originalAttributes, "readonly original also restores exactly");
        var multi = new Fixture("multi");
        multi.Roots = [multi.Root, Path.Combine(multi.Root, "DeltaForce")];
        Check(multi.Engine().Read().Items.Count == 2, "parent and DeltaForce child registrations deduplicate");
        var second = new Fixture("second-install"); multi.Roots = [multi.Root, second.Root];
        Check(multi.Engine().Read().Items.Count == 4, "two installations expose their own component rows");
        var chosen = multi.Engine().Read().Items.Single(i => string.Equals(i.InstallationRoot, Path.GetFullPath(second.Root), StringComparison.OrdinalIgnoreCase) && i.ComponentId == "recorder");
        Check(multi.Change(chosen).Success && new FileInfo(multi.Recorder).Length > 0 && new FileInfo(second.Recorder).Length == 0,
            "selected installation operation leaves other installation intact");
        Check(multi.Engine().RestoreAll().Success, "multi-install recovery restores the selected original");
        var direct = Path.Combine(_fixture, "direct-layout", "DeltaForce");
        WritePe(Path.Combine(direct, @"Binaries\Win64\DeltaForceClient-Win64-Shipping.exe"), 1);
        var directComponent = Path.Combine(direct, @"icreate\icreate-df\icreateDF.exe"); WritePe(directComponent, 2);
        var sibling = Path.Combine(Directory.GetParent(direct)!.FullName, @"icreate\icreate-df\icreateDF.exe"); WritePe(sibling, 4);
        var directEngine = new GameRecordingModification(Path.Combine(_fixture, "direct-data"), () => [direct], () => null);
        var directItem = directEngine.Read().Items.Single(i => i.ComponentId == "icreate");
        Check(directItem.FilePath == directComponent && directItem.CanApply, "standalone DeltaForce root keeps its own icreate directory");
        Check(directEngine.Change(new() { InstallationRoot = directItem.InstallationRoot, ComponentId = directItem.ComponentId, ExpectedFingerprint = directItem.Fingerprint }).Success
            && new FileInfo(sibling).Length > 0, "standalone layout never modifies parent's unrelated recording file");
        Check(directEngine.RestoreAll().Success, "standalone layout restoration works");
    }

    private static void Failures()
    {
        var f = new Fixture("stale"); var item = f.Item();
        WritePe(f.Icreate, 9);
        Check(!f.Change(item).Success && new FileInfo(f.Icreate).Length > 0 && !Directory.EnumerateFiles(f.Data, "*.json").Any(), "stale confirmation rejected before replacing changed original");
        var b = new Fixture("busy"); item = b.Item(); b.Blocker = "游戏运行";
        Check(!b.Item().CanApply && !b.Change(item).Success && !Directory.Exists(b.Data), "running or unknown state blocks direct writes");
        var invalid = new Fixture("invalid-args");
        Check(!invalid.Engine().Change(new() { InstallationRoot = invalid.Root, ComponentId = "../../other", ExpectedFingerprint = "x" }).Success, "component traversal rejected");
        Check(!invalid.Engine().Change(new() { InstallationRoot = _fixture, ComponentId = "icreate", ExpectedFingerprint = invalid.Item().Fingerprint }).Success, "unregistered root rejected");
        Check(!invalid.Engine().Change(new() { InstallationRoot = invalid.Root, ComponentId = "icreate" }).Success, "missing confirmation fingerprint rejected");
        var empty = new Fixture("foreign-empty"); File.WriteAllBytes(empty.Icreate, []);
        Check(!empty.Item().CanApply && !empty.Item().CanRestore && empty.Item().NeedsReview, "unknown preexisting empty file is never adopted");
        File.Delete(empty.Icreate);
        Check(!empty.Item().CanApply && !empty.Item().NeedsReview && empty.Item().State.Contains("未安装"), "missing component is not synthesized");
        var modified = new Fixture("game-update"); Check(modified.Change(modified.Item()).Success, "update fixture prepared");
        var backup = modified.Backup(modified.Icreate); var hash = Hash(backup);
        WritePe(modified.Icreate, 20); var latest = Hash(modified.Icreate);
        Check(!modified.Item().CanRestore && modified.Item().NeedsReview && !modified.Change(modified.Item(), true).Success, "game update never overwritten by old backup");
        Check(Hash(modified.Icreate) == latest && Hash(backup) == hash, "new game file and original backup both retained");
        var foreign = new Fixture("foreign-placeholder"); foreign.Change(foreign.Item());
        File.SetAttributes(foreign.Icreate, FileAttributes.Normal); File.Delete(foreign.Icreate); File.WriteAllBytes(foreign.Icreate, []);
        Check(!foreign.Item().CanRestore && foreign.Item().NeedsReview, "empty file with a different identity cannot be deleted during restore");
        var bad = new Fixture("bad-backup"); bad.Change(bad.Item()); WritePe(bad.Backup(bad.Icreate), 29);
        Check(!bad.Item().CanRestore && !bad.Change(bad.Item(), true).Success && new FileInfo(bad.Icreate).Length == 0, "changed backup blocks restoration");
        var gone = new Fixture("missing-backup"); gone.Change(gone.Item()); File.Delete(gone.Backup(gone.Icreate));
        Check(!gone.Item().CanRestore && gone.Item().HasRecord, "missing original keeps recovery evidence");
        var corrupt = new Fixture("corrupt-journal"); corrupt.Change(corrupt.Item()); File.WriteAllText(corrupt.Journal, "not-json");
        Check(!corrupt.Engine().Read().Complete && !corrupt.Item().CanRestore && corrupt.Item().HasRecord && corrupt.Engine().Read().RecordCount == 1, "corrupt journal remains counted and fails closed");
        var malformed = new Fixture("malformed-journal"); malformed.Change(malformed.Item());
        var json = JsonNode.Parse(File.ReadAllText(malformed.Journal))!; json["Token"] = "../../outside"; File.WriteAllText(malformed.Journal, json.ToJsonString());
        Check(!malformed.Engine().Read().Complete && !malformed.Item().CanRestore, "journal cannot select an arbitrary backup path");
        var orphan = new Fixture("orphan"); orphan.Change(orphan.Item()); orphan.Roots = [];
        Check(orphan.Engine().Read() is { Complete: false, UnmatchedRecords: 1 } && !orphan.Engine().RestoreAll().Success, "removed installation cannot make pending record disappear");
        var gate = new Fixture("concurrent"); item = gate.Item(); Directory.CreateDirectory(gate.Data);
        using var locked = new FileStream(Path.Combine(gate.Data, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Check(!gate.Change(item).Success && gate.Item().CanApply, "second writer rejected while first owns operation lock");
    }

    private static void Interruptions()
    {
        foreach (var phase in new[] { "prepared", "original-removed", "placeholder-moved" })
        {
            var f = new Fixture("interrupt-" + phase); var original = Hash(f.Icreate);
            f.Fault = s => { if (s == phase) throw new IOException("simulated interruption"); };
            Check(!f.Change(f.Item()).Success && f.Item().CanRestore && f.Item().HasRecord, "interruption retains recoverable state: " + phase);
            f.Fault = null;
            Check(f.Change(f.Item(), true).Success && Hash(f.Icreate) == original && !f.Item().HasRecord, "restart restores original after " + phase);
        }
        foreach (var phase in new[] { "restore-copy-prepared", "restore-placeholder-moved", "restore-original-moved", "restore-backup-removed" })
        {
            var f = new Fixture("interrupt-" + phase); var original = Hash(f.Icreate); f.Change(f.Item());
            f.Fault = s => { if (s == phase) throw new IOException("simulated restore interruption"); };
            Check(!f.Change(f.Item(), true).Success && f.Item().CanRestore, "restore interruption retains responsibility: " + phase);
            f.Fault = null;
            Check(f.Change(f.Item(), true).Success && Hash(f.Icreate) == original, "second restoration completes after " + phase);
        }
        var start = new Fixture("game-start-mid-operation"); var before = Hash(start.Icreate);
        start.Fault = s => { if (s == "original-removed") start.Blocker = "游戏刚启动"; };
        Check(!start.Change(start.Item()).Success && !File.Exists(start.Icreate) && Hash(start.Backup(start.Icreate)) == before, "game start recheck stops further moves and retains original");
        start.Fault = null; start.Blocker = null;
        Check(start.Change(start.Item(), true).Success && Hash(start.Icreate) == before, "restore works after game exits");
        var temp = new Fixture("foreign-temporary"); var tempOriginal = Hash(temp.Icreate);
        temp.Fault = s => { if (s == "prepared") throw new IOException("simulated stop"); };
        temp.Change(temp.Item()); temp.Fault = null;
        var tempPath = Directory.EnumerateFiles(Path.GetDirectoryName(temp.Icreate)!, "*.deltanfd-*.tmp").Single();
        File.SetAttributes(tempPath, FileAttributes.Normal); File.WriteAllText(tempPath, "unrelated content");
        Check(!temp.Item().CanRestore && !temp.Change(temp.Item(), true).Success && Hash(temp.Icreate) == tempOriginal,
            "unknown content in temporary file is never deleted");
    }

    private static void Paths()
    {
        var hard = new Fixture("hardlink");
        Check(CreateHardLink(Path.Combine(Path.GetDirectoryName(hard.Icreate)!, "other.exe"), hard.Icreate, IntPtr.Zero), "hardlink fixture created");
        Check(!hard.Item().CanApply && !hard.Engine().Read().Complete, "hardlinked originals refused");
        var junction = new Fixture("junction");
        var folder = Path.GetDirectoryName(junction.Icreate)!;
        var outside = folder + "-outside";
        Directory.Move(folder, outside);
        var process = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        process.ArgumentList.Add("/d"); process.ArgumentList.Add("/c"); process.ArgumentList.Add("mklink"); process.ArgumentList.Add("/J"); process.ArgumentList.Add(folder); process.ArgumentList.Add(outside);
        using var p = System.Diagnostics.Process.Start(process)!; p.WaitForExit();
        Check(p.ExitCode == 0, "junction fixture created");
        Check(!junction.Item().CanApply && !junction.Engine().Read().Complete, "linked component directory refused without altering outside bytes");
        Check(new FileInfo(Path.Combine(outside, "icreateDF.exe")).Length > 0, "outside file retained");
        // Remove only this directory entry, never recurse through the junction.
        Directory.Delete(folder);
        var ledger = new Fixture("linked-ledger");
        var ledgerTarget = Path.Combine(_fixture, "ledger-outside"); Directory.CreateDirectory(ledgerTarget);
        process.ArgumentList.Clear();
        foreach (var arg in new[] { "/d", "/c", "mklink", "/J", ledger.Data, ledgerTarget }) process.ArgumentList.Add(arg);
        using var ledgerProcess = System.Diagnostics.Process.Start(process)!; ledgerProcess.WaitForExit();
        Check(ledgerProcess.ExitCode == 0, "recovery store junction fixture created: " + ledgerProcess.StandardOutput.ReadToEnd() + ledgerProcess.StandardError.ReadToEnd());
        Check(ledger.Engine().Read() is { Complete: false, RecoveryReadFailed: true }, "linked recovery store reports unknown recovery state");
        Check(!ledger.Change(new() { InstallationRoot = ledger.Root, ComponentId = "icreate" }).Success && !Directory.EnumerateFiles(ledgerTarget).Any(), "linked recovery store never written");
        Directory.Delete(ledger.Data);
    }

    private static JsonNode ProbeNode(string path)
    {
        var probe = typeof(GameRecordingModification).GetMethod("Probe", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(probe.Invoke(null, [path])))!;
    }

    private static void CentralAndLegacy()
    {
        var f = new Fixture("central-location");
        var original = Hash(f.Icreate);
        File.WriteAllText(f.Icreate + ":nfd-fixture", "owned alternate stream");
        var creation = DateTime.UtcNow.AddYears(-1);
        File.SetCreationTimeUtc(f.Icreate, creation);
        Check(f.Change(f.Item()).Success, "central fixture replaced");
        Check(f.Backup(f.Icreate).StartsWith(Path.Combine(f.Data, "backups"), StringComparison.OrdinalIgnoreCase)
            && !Directory.EnumerateFiles(Path.GetDirectoryName(f.Icreate)!, "*.bak").Any(), "no persistent original backup remains in game directory");
        var journal = JsonNode.Parse(File.ReadAllText(f.Journal))!;
        Check(journal["Version"]!.GetValue<int>() == 2 && journal["OriginalIdentity"]!.GetValue<string>() != journal["BackupIdentity"]!.GetValue<string>(), "copied backup has its own persisted file identity");
        Check(f.Change(f.Item(), true).Success && Hash(f.Icreate) == original && File.GetCreationTimeUtc(f.Icreate) == creation,
            "central restoration preserves bytes and original creation time");
        Check(File.ReadAllText(f.Icreate + ":nfd-fixture") == "owned alternate stream", "backup and restoration preserve NTFS alternate stream bytes");

        var changed = new Fixture("same-bytes-new-backup"); changed.Change(changed.Item());
        var backup = changed.Backup(changed.Icreate); var other = backup + ".foreign";
        File.Copy(backup, other); File.Delete(backup); File.Move(other, backup);
        Check(!changed.Item().CanRestore && !changed.Change(changed.Item(), true).Success, "same bytes with a foreign backup identity are refused");

        foreach (var phase in new[] { "prepared", "original-moved", "placeholder-moved" })
        {
            var legacy = new Fixture("legacy-" + phase);
            File.SetCreationTimeUtc(legacy.Icreate, creation);
            var source = ProbeNode(legacy.Icreate);
            var token = Guid.NewGuid().ToString("N");
            var temp = legacy.Icreate + ".deltanfd-" + token + ".tmp";
            var saved = legacy.Icreate + ".deltanfd-" + token + ".bak";
            File.WriteAllBytes(temp, []); File.SetAttributes(temp, FileAttributes.ReadOnly);
            var empty = ProbeNode(temp);
            Directory.CreateDirectory(legacy.Data);
            var record = Path.Combine(legacy.Data, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(legacy.Icreate).ToUpperInvariant()))).ToLowerInvariant() + ".json");
            var j = new JsonObject { ["Version"] = 1, ["InstallationRoot"] = legacy.Root, ["ComponentId"] = "icreate", ["Token"] = token,
                ["OriginalIdentity"] = source["Identity"]!.GetValue<string>(), ["OriginalHash"] = source["Hash"]!.GetValue<string>(),
                ["OriginalLength"] = source["Length"]!.GetValue<long>(), ["OriginalAttributes"] = source["Attributes"]!.GetValue<int>(),
                ["PlaceholderIdentity"] = empty["Identity"]!.GetValue<string>() };
            File.WriteAllText(record, j.ToJsonString());
            if (phase != "prepared") File.Move(legacy.Icreate, saved);
            if (phase == "placeholder-moved")
            {
                File.Move(temp, legacy.Icreate);
                File.SetCreationTimeUtc(legacy.Icreate, creation); // reproduce documented NTFS tunneling exactly.
            }
            var before = File.ReadAllBytes(record);
            Check(legacy.Item().CanRestore && File.ReadAllBytes(record).SequenceEqual(before), "v1 read recognizes recorded files without migration writes: " + phase);
            Check(legacy.Change(legacy.Item(), true).Success && Hash(legacy.Icreate) == source["Hash"]!.GetValue<string>(), "v1 backup still restores after " + phase);
            Check(legacy.Change(legacy.Item()).Success && legacy.Backup(legacy.Icreate).StartsWith(legacy.Data, StringComparison.OrdinalIgnoreCase), "next explicit replacement uses central backup: " + phase);
        }
        var foreignTime = new Fixture("foreign-creation-time"); foreignTime.Change(foreignTime.Item());
        File.SetCreationTimeUtc(foreignTime.Icreate, creation.AddYears(-2));
        Check(!foreignTime.Item().CanRestore, "unrecorded creation-time changes are not treated as NTFS tunneling");

        var duringRestore = new Fixture("game-update-during-restore"); duringRestore.Change(duringRestore.Item());
        var savedHash = Hash(duringRestore.Backup(duringRestore.Icreate));
        duringRestore.Fault = phase => { if (phase == "restore-copy-prepared") WritePe(duringRestore.Icreate, 73); };
        Check(!duringRestore.Change(duringRestore.Item(), true).Success && Hash(duringRestore.Icreate) != savedHash
            && Hash(duringRestore.Backup(duringRestore.Icreate)) == savedHash, "game update during restoration is retained with the old backup");

        var changedRecord = new Fixture("journal-changed-during-restore"); changedRecord.Change(changedRecord.Item());
        var recordPath = changedRecord.Journal;
        changedRecord.Fault = phase => { if (phase == "restore-copy-prepared") File.WriteAllText(recordPath, "external replacement"); };
        Check(!changedRecord.Change(changedRecord.Item(), true).Success && new FileInfo(changedRecord.Icreate).Length == 0
            && File.ReadAllText(recordPath) == "external replacement", "a replaced journal is never overwritten or deleted during restore");
    }

    private static void StaleRecordReview()
    {
        OperationResult Archive(Fixture f, GameRecordingItem? item = null) => f.Engine().Change(new()
        {
            InstallationRoot = (item ?? f.Item()).InstallationRoot, ComponentId = (item ?? f.Item()).ComponentId,
            ExpectedFingerprint = (item ?? f.Item()).Fingerprint, ArchiveRecord = true,
        });
        Fixture Repaired(string name, int version = 1)
        {
            var f = new Fixture("review-" + name); var original = File.ReadAllBytes(f.Icreate);
            Check(f.Change(f.Item()).Success, "prepare stale record: " + name);
            var backup = f.Backup(f.Icreate); var record = f.Journal;
            var replacement = f.Icreate + ".launcher-copy";
            File.WriteAllBytes(replacement, original); // allocate a new identity before releasing the placeholder
            File.SetCreationTimeUtc(replacement, DateTime.UtcNow.AddDays(-3));
            File.SetAttributes(f.Icreate, FileAttributes.Normal); File.Delete(f.Icreate); File.Move(replacement, f.Icreate);
            File.SetAttributes(backup, FileAttributes.Normal); File.Delete(backup);
            var journal = JsonNode.Parse(File.ReadAllText(record))!; journal["Version"] = version;
            File.WriteAllText(record, journal.ToJsonString());
            return f;
        }
        foreach (var version in new[] { 1, 2 })
        {
            var f = Repaired("success-v" + version, version);
            var record = f.Journal; var raw = File.ReadAllBytes(record); var original = Hash(f.Icreate);
            var attrs = File.GetAttributes(f.Icreate); var creation = File.GetCreationTimeUtc(f.Icreate); var write = File.GetLastWriteTimeUtc(f.Icreate);
            var item = f.Item();
            Check(item.NeedsReview && item.CanArchiveRecord && !item.CanApply && !item.CanRestore,
                "new identity with exact original content offers metadata review only: v" + version);
            Check(File.ReadAllBytes(record).SequenceEqual(raw) && !Directory.Exists(Path.Combine(f.Data, "reviewed")), "status never auto-archives stale evidence: v" + version);
            Check(!f.Change(item, true).Success && !f.Change(item).Success, "strict restore/apply gates remain until explicit review: v" + version);
            Check(Archive(f, item).Success && !File.Exists(record) && f.Item().CanApply && !f.Item().HasRecord,
                "explicit reviewed archive releases new operation: v" + version);
            Check(Hash(f.Icreate) == original && File.GetAttributes(f.Icreate) == attrs
                && File.GetCreationTimeUtc(f.Icreate) == creation && File.GetLastWriteTimeUtc(f.Icreate) == write,
                "archive preserves executable bytes, attributes and timestamps: v" + version);
            var history = Directory.GetFiles(Path.Combine(f.Data, "reviewed"), "*.json");
            Check(history.Length == 2 && history.Any(p => File.ReadAllBytes(p).SequenceEqual(raw)), "old journal is archived byte-for-byte with receipt: v" + version);
            Check(history.Any(p => File.ReadAllText(p).Contains("original-content-confirmed-record-archived")), "archive receipt never labels action as restored: v" + version);
            Check(!Archive(f, item).Success && Hash(f.Icreate) == original, "old confirmation cannot be replayed after archive: v" + version);
            Check(f.Change(f.Item()).Success && f.Backup(f.Icreate).StartsWith(Path.Combine(f.Data, "backups"), StringComparison.OrdinalIgnoreCase),
                "next explicit replacement uses centralized software backup: v" + version);
            Check(f.Change(f.Item(), true).Success && Hash(f.Icreate) == original, "new replacement remains restorable: v" + version);
        }
        var changed = Repaired("changed-content"); WritePe(changed.Icreate, 47);
        var rawChanged = File.ReadAllBytes(changed.Journal);
        Check(!changed.Item().CanArchiveRecord && !Archive(changed).Success
            && File.ReadAllBytes(changed.Journal).SequenceEqual(rawChanged), "different game content cannot clear old evidence");
        var empty = new Fixture("review-empty"); empty.Change(empty.Item()); File.Delete(empty.Backup(empty.Icreate));
        Check(!empty.Item().CanArchiveRecord && !Archive(empty).Success && new FileInfo(empty.Icreate).Length == 0, "lost backup plus empty placeholder is never acknowledged as original");
        var missing = Repaired("missing"); File.Delete(missing.Icreate);
        Check(!missing.Item().CanArchiveRecord && !Archive(missing).Success, "missing executable cannot archive original evidence");
        var validBackup = new Fixture("review-valid-backup"); validBackup.Change(validBackup.Item());
        Check(validBackup.Item().CanRestore && !validBackup.Item().CanArchiveRecord && !Archive(validBackup).Success,
            "valid managed backup keeps original restore path");
        var unexpectedTemp = Repaired("temporary"); var j = JsonNode.Parse(File.ReadAllText(unexpectedTemp.Journal))!;
        File.WriteAllText(unexpectedTemp.Icreate + ".deltanfd-" + j["Token"]!.GetValue<string>() + ".tmp", "foreign");
        Check(!unexpectedTemp.Item().CanArchiveRecord && !Archive(unexpectedTemp).Success, "temporary file blocks stale-record retirement");
        var blocked = Repaired("game-running"); var before = File.ReadAllBytes(blocked.Journal); blocked.Blocker = "fixture game running";
        Check(!blocked.Item().CanArchiveRecord && !Archive(blocked).Success && File.ReadAllBytes(blocked.Journal).SequenceEqual(before), "running game gate blocks metadata review");
        var stale = Repaired("stale-confirmation"); var old = stale.Item(); File.SetLastWriteTimeUtc(stale.Icreate, DateTime.UtcNow.AddYears(-1));
        Check(!Archive(stale, old).Success && File.Exists(stale.Journal), "stale target metadata invalidates confirmation");
        var changedDuring = Repaired("changed-during-review"); var current = changedDuring.Item();
        changedDuring.Fault = phase => { if (phase == "archive-prepared") WritePe(changedDuring.Icreate, 39); };
        Check(!Archive(changedDuring, current).Success && File.Exists(changedDuring.Journal), "content change after confirmation prevents archive");
        var changedRecord = Repaired("record-changed-during-review"); current = changedRecord.Item(); var journalPath = changedRecord.Journal;
        changedRecord.Fault = phase => { if (phase == "archive-prepared") File.AppendAllText(journalPath, " "); };
        Check(!Archive(changedRecord, current).Success && File.Exists(journalPath), "journal changed during review is preserved");
        var busyDuring = Repaired("running-during-review"); current = busyDuring.Item();
        busyDuring.Fault = phase => { if (phase == "archive-prepared") busyDuring.Blocker = "fixture launched game"; };
        Check(!Archive(busyDuring, current).Success && File.Exists(busyDuring.Journal), "game starting during review prevents commit");
        foreach (var phase in new[] { "archive-prepared", "archive-record-moved" })
        {
            var f = Repaired("interrupted-" + phase); current = f.Item(); var record = f.Journal; var bytes = File.ReadAllBytes(record); var original = Hash(f.Icreate);
            f.Fault = p => { if (p == phase) throw new IOException("fixture interruption"); };
            Check(!Archive(f, current).Success && Hash(f.Icreate) == original, "archive interruption never changes game content: " + phase);
            f.Fault = null;
            Check(phase == "archive-prepared" ? File.ReadAllBytes(record).SequenceEqual(bytes) && Archive(f).Success
                : !File.Exists(record) && f.Item().CanApply
                    && Directory.GetFiles(Path.Combine(f.Data, "reviewed"), "*.json").Any(p => File.ReadAllBytes(p).SequenceEqual(bytes)),
                "archive interruption retains retry path or committed raw evidence: " + phase);
        }
        var contradiction = Repaired("conflicting-action"); current = contradiction.Item();
        Check(!contradiction.Engine().Change(new() { InstallationRoot = current.InstallationRoot, ComponentId = current.ComponentId,
            ExpectedFingerprint = current.Fingerprint, ArchiveRecord = true, Restore = true }).Success, "conflicting restore and archive actions rejected");
        var linked = Repaired("linked-archive"); var outside = Path.Combine(_fixture, "archive-junction-destination");
        Directory.CreateDirectory(outside);
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "/d", "/c", "mklink", "/J", Path.Combine(linked.Data, "reviewed"), outside }) start.ArgumentList.Add(arg);
        using var junction = System.Diagnostics.Process.Start(start)!; junction.WaitForExit();
        Check(junction.ExitCode == 0, "archive junction fixture created: " + junction.StandardOutput.ReadToEnd() + junction.StandardError.ReadToEnd());
        Check(!Archive(linked).Success && File.Exists(linked.Journal) && Directory.GetFiles(outside).Length == 0,
            "archive directory junction is rejected without writing destination or retiring active record");
    }

    private static void CrossVolume()
    {
        var owned = Path.Combine(Path.GetTempPath(), "DeltaNFD-recording-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(owned);
        File.WriteAllText(Path.Combine(owned, "isolated-test.marker"), "GameModificationChecks cross-volume owned fixture");
        var f = new Fixture("cross-volume", Path.Combine(owned, "data"));
        if (string.Equals(Path.GetPathRoot(f.Root), Path.GetPathRoot(f.Data), StringComparison.OrdinalIgnoreCase))
        { Console.WriteLine("SKIP: actual cross-volume case needs temp and workspace on different drives; same-volume copy cases ran above."); return; }
        Check(!string.Equals(Path.GetPathRoot(f.Root), Path.GetPathRoot(f.Data), StringComparison.OrdinalIgnoreCase), "cross-volume fixture uses different actual drive roots");
        var original = Hash(f.Icreate); var attributes = File.GetAttributes(f.Icreate);
        Check(f.Change(f.Item()).Success && Hash(f.Backup(f.Icreate)) == original, "actual cross-volume backup copies and verifies original");
        var content = File.ReadAllText(f.Journal);
        Check(f.Item().CanRestore && File.ReadAllText(f.Journal) == content, "cross-volume status is read only");
        Check(f.Change(f.Item(), true).Success && Hash(f.Icreate) == original && File.GetAttributes(f.Icreate) == attributes, "actual cross-volume restoration verifies bytes and attributes");
        Console.WriteLine("Cross-volume fixture retained: " + owned);
    }

    private static void Wiring(string root)
    {
        Check(BackendModules.IsReadOnly(BackendModules.System, BackendCommands.SystemGameRecordingStatus), "status is readonly protocol command");
        Check(BackendModules.RequiresWriteIntent(BackendModules.System, BackendCommands.SystemGameRecordingChange)
            && BackendModules.RetainsStaticBackup(BackendModules.System, BackendCommands.SystemGameRecordingChange), "writes have durable intent and persistent backup semantics");
        string Read(string p) => File.ReadAllText(Path.Combine(root, p));
        var settings = Read("src/DeltaNFD/Views/SettingsPage.xaml");
        Check(!settings.Contains("FsoDisableToggle") && settings.Contains("GameModifications_Click"), "settings has navigation instead of duplicate fullscreen switch");
        var page = XDocument.Parse(Read("src/DeltaNFD/Views/GameModificationsPage.xaml"));
        Check(page.Descendants().Any(e => e.Name.LocalName == "ToggleSwitch") && page.Descendants().Count(e => (string?)e.Attribute("Click") is "Replace_Click" or "Restore_Click") == 2, "page exposes fullscreen and separate replace/restore actions");
        var code = Read("src/DeltaNFD/Views/GameModificationsPage.xaml.cs");
        Check(code.Contains("DefaultButton = ContentDialogButton.Close") && code.Contains("ExpectedFingerprint = item.Fingerprint"), "confirmation defaults to cancel and binds exact inspected state");
        Check(page.Descendants().Any(e => (string?)e.Attribute("Click") == "ReviewRecording_Click"
            && (string?)e.Attribute("IsEnabled") == "{x:Bind NeedsReview}"), "review action remains available for disabled problem cards");
        Check(code.Contains("item.CanArchiveRecord") && code.Contains("ArchiveRecord = true") && code.Contains("MaxHeight = 420"),
            "review dialog presents bounded guidance and guarded explicit archive action");
        Check(code.Contains("GameCompatibilityService.DisableFullscreenOptimizations") && code.Contains("GameCompatibilityService.RestoreFullscreenOptimizations"), "fullscreen calls original implementation and backup logic");
        Check(Read("src/DeltaNFD/MainWindow.xaml").Contains("Tag=\"gamemods\"") && Read("src/DeltaNFD/MainWindow.xaml.cs").Contains("\"gamemods\" => typeof(GameModificationsPage)"), "navigation maps to new game modifications page");
        Check(Read("src/DeltaNFD/MainWindow.PageSessions.cs").Contains("page == typeof(GameModificationsPage) ? \"gamemods\"")
            && Read("src/DeltaNFD/Services/Backends/PageBackendSessions.cs").Contains("\"system\" or \"gamemods\" => [BackendModules.System]"), "page owns system backend and drains it when leaving");
        Check(Read("src/DeltaNFD.Backend.SystemMaintenance/SystemCommands.cs").Contains("Id = \"game-recording\"") && Read("src/DeltaNFD.Backend.SystemMaintenance/SystemCommands.cs").Contains("CreateDefault().RestoreAll()"), "unknown-operation recovery exposes recording backups");
    }
    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "src/DeltaNFD/DeltaNFD.csproj"))) return dir.FullName;
        throw new Exception("Repository not found");
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string file, string existing, IntPtr security);
}
