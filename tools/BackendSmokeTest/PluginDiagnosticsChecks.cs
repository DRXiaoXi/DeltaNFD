using DeltaNFD.Services.Plugins;

internal static class PluginDiagnosticsChecks
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD_PluginDiagnostics_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var scope = PluginDiagnostics.UseDirectory(root);
            var original = new PluginLogEntry { Phase = "invoke", Status = "ok", RequestId = "real-request",
                ErrorCode = "OK request=forged-request", Detail = "nonce=secret-nonce token=secret-token Bearer secret-bearer C:\\Users\\Private\\game.exe",
                BackupId = "private-backup" };
            var line = PluginDiagnostics.FormatLine(original);
            Require(PluginDiagnostics.TryParse(line, out var parsed) && parsed.RequestId == original.RequestId, "field injection cannot change correlation");
            Require(parsed.ErrorCode == "invalid-field", "invalid metadata rejected");
            Require(parsed.BackupId != original.BackupId && PluginDiagnostics.TryParse(PluginDiagnostics.FormatLine(parsed), out var again) &&
                again.BackupId == parsed.BackupId, "stable backup pseudonym");
            Require(!line.Contains("secret-nonce") && !line.Contains("secret-token") && !line.Contains("secret-bearer") && !line.Contains("Private"), "sensitive detail redacted");
            Require(PluginDiagnostics.BackendCode("secret-token") == "PLUGIN_OTHER", "backend error text not trusted metadata");
            Require(!PluginDiagnostics.TryParse("{ broken", out _) && !PluginDiagnostics.TryParse("null", out _) &&
                !PluginDiagnostics.TryParse("{\"schemaVersion\":1,\"phase\":\"invoke\"}", out _), "bad/missing metadata rejected");
            Require(!PluginDiagnostics.TryParse(line.Replace("\"requestId\":", "\"requestId\":\"forged\",\"requestId\":"), out _), "duplicate JSON key rejected");
            Require(PluginDiagnostics.FormatLine(original with { Detail = new string('x', 100_000) }).Length < 16_384, "bounded third-party text");
            await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => PluginDiagnostics.Record("invoke", "ok", requestId: "parallel-" + i))));
            Require(PluginDiagnostics.ReadRecent().Count == 20, "concurrent records roundtrip");
            File.AppendAllText(PluginDiagnostics.CurrentPath, "{ broken\n");
            Require(PluginDiagnostics.ReadByRequest("parallel-3").Count == 1, "corrupt line isolated");
            Require(PluginDiagnostics.ReadSnapshot().Warning.Length > 0, "corrupt history is not presented as complete");
            var snapshot = new PluginHistorySnapshot(Enumerable.Range(0, 120).Select(i => new PluginLogEntry
            { Phase = "invoke", PluginId = i % 2 == 0 ? "org.example.a" : "org.example.b", Status = i % 3 == 0 ? "unknown" : "ok" }).ToArray(), "", false);
            var filtered = PluginHistoryQuery.Filter(snapshot, "org.example.a", "unknown");
            Require(filtered.Count == 20 && filtered.All(e => e.PluginId == "org.example.a" && e.Status == "unknown"), "plugin/status filtering");
            Require(PluginHistoryQuery.Page(snapshot.Entries, 0, 50).Count == 50 && PluginHistoryQuery.Page(snapshot.Entries, 100, 50).Count == 20,
                "bounded incremental pages");
            Require(PluginHistoryQuery.StatusKey(new PluginLogEntry { Phase = "invoke" }) == "started", "begin event is not a confirmed live process");
            for (var i = 0; i < 1700; i++) PluginDiagnostics.Record("invoke", "ok", detail: new string('中', 512), requestId: "rotation");
            Require(Directory.GetFiles(root, "plugins-*.jsonl").Length <= PluginDiagnostics.MaxArchives + 1, "bounded archive count");
            Require(Directory.GetFiles(root, "plugins-*.jsonl").All(p => new FileInfo(p).Length <= PluginDiagnostics.MaxFileBytes), "bounded file size");
            Require(PluginDiagnostics.ReadRecent().Count <= PluginDiagnostics.MaxReadEntries, "bounded history objects");
            var evidence = Path.Combine(root, "backups.json");
            File.WriteAllText(evidence, "preserve-evidence");
            PluginDiagnostics.Record("stop", "ok");
            Require(File.ReadAllText(evidence) == "preserve-evidence", "rotation never touches evidence");
            var blocked = Path.Combine(root, "blocked");
            File.WriteAllText(blocked, "not-a-directory");
            using (PluginDiagnostics.UseDirectory(blocked))
            {
                PluginDiagnostics.Record("invoke", "ok");
                Require(PluginDiagnostics.GetWriteError(blocked).Length > 0, "write failure surfaced without throwing");
                Require(PluginDiagnostics.ReadSnapshot().Warning.Length > 0, "unreadable history has explicit warning");
            }
            Require(PluginDiagnostics.LogDirectory == root, "scope restored after failure");
            Console.WriteLine("Plugin diagnostics checks passed: JSON correlation, duplicate rejection, redaction, concurrency, rotation, bounded reads, failure isolation.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
