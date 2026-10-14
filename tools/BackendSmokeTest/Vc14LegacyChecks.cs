using DeltaNFD.Services;
using DeltaNFD.Backends;

internal static class Vc14LegacyChecks
{
    internal static void Run()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        foreach (var arch in new[] { "x64", "x86" })
        {
            var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "LegacyVc14", "vcred14_" + arch + ".inf"));
            var valid = Vc14LegacyPolicy.Validate(text, arch);
            Check(valid.Version == "14.51.36247.0" && valid.Name.Contains("2026"), "legacy fixture identity changed");
            Vc14LegacyPolicy.ValidateRegistration(valid.Name, valid.Version, "Microsoft Corporation",
                "rundll32.exe advpack.dll,LaunchINFSection vcred14_" + arch + ".inf,Uninstall", arch, valid); count++;
            foreach (var changed in new[] { text.Replace("msiexec.exe /X", "evil.exe /X"), text + "\n[Extra]\nRun=malicious.exe",
                text.Replace("14.51.36247.0", "12.0.21005.0"), text.Replace(valid.Minimum, valid.Additional),
                text.Replace("Microsoft Visual C++ 2026", "Other product"), text.Replace("DelReg                 =Registry.Uninstall", "DelReg=Other.Key") })
            {
                var rejected = false; try { Vc14LegacyPolicy.Validate(changed, arch); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "modified INF accepted");
            }
            var older = text.Replace("14.51.36247.0", "14.44.35211.0").Replace("Visual C++ 2026", "Visual C++ 2022");
            Check(Vc14LegacyPolicy.Validate(older, arch).Version == "14.44.35211.0", "verified template cannot handle older data fields");
            foreach (var publisher in new[] { "Other", "" })
            {
                var rejected = false;
                try { Vc14LegacyPolicy.ValidateRegistration(valid.Name, valid.Version, publisher,
                    "rundll32.exe advpack.dll,LaunchINFSection vcred14_" + arch + ".inf,Uninstall", arch, valid); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected, "wrong registration publisher accepted");
            }
            foreach (var command in new[] { "evil.exe", "rundll32.exe advpack.dll,LaunchINFSection other.inf,Uninstall" })
            {
                var rejected = false; try { Vc14LegacyPolicy.ValidateRegistration(valid.Name, valid.Version, "Microsoft Corporation", command, arch, valid); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected, "untrusted registration command accepted");
            }
        }
        Check(BackendModules.RequiresWriteIntent(BackendModules.Runtime, BackendCommands.Vc14LegacyClean), "metadata cleanup missing write-ahead intent");
        var env = new Fake(); var good = Vc14LegacyCleanup.Run(env, "before", null, CancellationToken.None);
        Check(good.Success && env.Calls.SequenceEqual(new[] { "backup", "remove:x64", "remove:x86" }), "backup or cleanup ordering wrong");
        Check(env.Core == "core", "current core changed");
        env = new(); Check(!Vc14LegacyCleanup.Run(env, "stale", null, CancellationToken.None).Success && env.Calls.Count == 0, "stale preview wrote state");
        foreach (var variant in new[] { "busy", "legacy-present", "bad-template", "backup", "core-before" })
        {
            env = new() { Failure = variant };
            var result = Vc14LegacyCleanup.Run(env, "before", null, CancellationToken.None);
            Check(!result.Success && !env.Calls.Any(s => s.StartsWith("remove")), "preflight/backup failure removed metadata: " + variant);
        }
        env = new() { Failure = "remove" }; var partial = Vc14LegacyCleanup.Run(env, "before", null, CancellationToken.None);
        Check(!partial.Success && partial.OutcomeUnknown && partial.DiagnosticsDirectory.Length > 0 && env.Calls.Count == 2, "partial cleanup lost evidence or continued");
        env = new() { Failure = "core-after" }; var changedCore = Vc14LegacyCleanup.Run(env, "before", null, CancellationToken.None);
        Check(!changedCore.Success && changedCore.OutcomeUnknown && env.Calls.Count == 2, "core change ignored");
        env = new() { Failure = "retained" }; Check(!Vc14LegacyCleanup.Run(env, "before", null, CancellationToken.None).Success, "remaining wrapper reported success");
        env = new(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Check(!Vc14LegacyCleanup.Run(env, "before", null, cancelled.Token).Success && env.Calls.Count == 0, "cancelled request changed state");
        Console.WriteLine($"VC14 legacy: {count} policy/transaction checks passed; fixtures and simulated metadata only.");
    }
    internal static void ReadOnly()
    {
        var id = Guid.NewGuid(); var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-legacy-backup-test", id.ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        var environment = new Vc14LegacyEnvironment(); var report = environment.Detect();
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report));
        if (report.Issues.Count > 0) throw new Exception(string.Join("\n", report.Issues));
        Console.WriteLine("Core fingerprint: " + environment.CaptureCore());
        Console.WriteLine("Verified backup in isolated temporary directory: " + environment.Backup());
        Console.WriteLine("No Remove call, registry/file/installer changes were not executed.");
    }
    private sealed class Fake : IVc14LegacyEnvironment
    {
        public string Failure = "", Core = "core";
        public List<string> Calls = [];
        private readonly List<Vc14LegacyEntry> _entries = [new("x64", "Legacy x64", "14.51", true, "validated"), new("x86", "Legacy x86", "14.51", true, "validated")];
        public Vc14LegacyReport Detect() => new("before", Failure == "legacy-present" ? [_entries[0] with { CanProcess = false }]
            : _entries.ToArray(), Failure == "bad-template" ? ["untrusted INF"] : []);
        public string? Blocker() => Failure == "busy" ? "installer busy" : null;
        public string CaptureCore() { if (Failure == "core-before") throw new IOException("core unavailable"); return Core; }
        public string Backup() { Calls.Add("backup"); if (Failure == "backup") throw new IOException("backup failed"); return "isolated-evidence"; }
        public void Remove(Vc14LegacyEntry entry)
        {
            Calls.Add("remove:" + entry.Architecture);
            if (Failure == "remove") throw new IOException("write failed");
            if (Failure == "core-after") Core = "changed";
            if (Failure != "retained") _entries.Remove(entry);
        }
    }
}
