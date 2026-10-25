using System.Text;
using DeltaNFD.Backends;
using DeltaNFD.Services;

internal static class GpuDriverNameChecks
{
    internal static void Run(bool native, string? layout = null)
    {
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
        void Refuse(Action run, string message) { try { run(); } catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { count++; return; } throw new Exception(message); }
        var id = Guid.NewGuid(); var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-driver-name-" + id.ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        try
        {
            AppSettingsStore.UpdateRequired(AppSettingsStore.DefaultPath, s => s.FrameModeActive = false);
            if (native)
            {
                var backups = new TweakBackupStore(Path.Combine(root, "only-test-backups.json"));
                var adapters = new GpuSpoofService(backups).GetAdaptersAsync().GetAwaiter().GetResult();
                var found = 0; GpuDriverNamePreview? first = null;
                foreach (var adapter in adapters)
                {
                    try
                    {
                        var nativeEvidence = GpuDriverNameSource.Read(adapter.RegistryPath, backups);
                        Check(GpuSpoofRestoreState.IsNativeDescription(nativeEvidence.Description), "live canonical INF description");
                        first ??= nativeEvidence.Preview;
                        Console.WriteLine($"READ-ONLY: {nativeEvidence.DriverDescription}; {nativeEvidence.InfName}; INF/model/hardware binding verified"); found++;
                    }
                    catch (IOException ex) { Console.WriteLine("READ-ONLY: unsupported/unconfirmed: " + ex.Message); }
                }
                Check(found > 0, "no installed adapter could be independently verified");
                if (layout is not null)
                {
                    Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ROOT", Path.Combine(Path.GetFullPath(layout), "Backends"));
                    Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT", root);
                    Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ID", id.ToString("D"));
                    var client = DeltaNFD.Services.Backends.BackendClient.StartAsync(BackendModules.Gpu, default).GetAwaiter().GetResult();
                    try
                    {
                        var remoteResult = client.InvokeAsync(BackendCommands.GpuDriverNamePreview,
                            new GpuSpoofPathArgs { RegistryPath = first!.RegistryPath }, null, default).GetAwaiter().GetResult();
                        Check(remoteResult.Status == BackendResultStatuses.Ok && remoteResult.Data is not null, "actual GPU backend preview succeeds over owned pipe");
                        var remote = DeltaNFD.Services.Backends.BackendResults.RequireData<GpuDriverNamePreview>(remoteResult, "fixture");
                        Check(remote.DriverName == first.DriverName && remote.InfName == first.InfName && remote.Fingerprint == first.Fingerprint,
                            "typed candidate and confirmation fingerprint survive actual backend IPC");
                    }
                    finally { client.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                }
                Check(!File.Exists(Path.Combine(root, "only-test-backups.json")) && !Directory.Exists(Path.Combine(root, "GpuDriverNameRecovery")), "native preview created no backup or write evidence");
                Console.WriteLine($"Driver-name native read-only: {count} checks; no registry/ACL/driver writes."); return;
            }

            const string path = @"SYSTEM\CurrentControlSet\Enum\PCI\VEN_10DE&DEV_28E0&SUBSYS_12341043\4&fixture&0";
            const string matching = @"PCI\VEN_10DE&DEV_28E0&SUBSYS_12341043";
            const string model = "NVIDIA GeForce RTX 4060 Laptop GPU";
            var inf = Path.Combine(root, "oem123.inf");
            string Inf(string name = model, string hw = matching, string cls = "{4d36e968-e325-11ce-bfc1-08002be10318}", string extra = "") =>
                $"[Version]\r\nSignature=\"$Windows NT$\"\r\nClass=Display\r\nClassGuid={cls}\r\nProvider=%Vendor%\r\n[Manufacturer]\r\n%Vendor%=Models,NTamd64\r\n[Models.NTamd64]\r\n%GPU%=InstallGPU,{hw}\r\n{extra}\r\n[InstallGPU]\r\n[Strings]\r\nVendor=\"Fixture Vendor\"\r\nGPU=\"{name}\"\r\n";
            void Save(string text) => File.WriteAllText(inf, text, Encoding.Unicode);
            Save(Inf());
            var resolved = GpuDriverNameSource.ResolveInf(inf, "InstallGPU", matching, model);
            Check(resolved == "@oem123.inf,%GPU%;" + model, "real SetupAPI resolves matching INF/model/hardware identity");
            Check(!GpuSpoofService.IsMaskedDescription(resolved), "reconstructed INF reference is not a spoof");
            foreach (var real in new[] { "NVIDIA GeForce GTX 1050 Ti", "AMD Radeon RX 540", "NVIDIA GeForce GTX 750" })
            {
                Save(Inf(real));
                var restored = GpuDriverNameSource.ResolveInf(inf, "InstallGPU", matching, real);
                Check(!GpuSpoofService.IsMaskedDescription(restored) && GpuSpoofService.IsMaskedDescription(real), "real hardware matching a spoof preset remains correctly classified");
            }
            Save(Inf()); Refuse(() => GpuDriverNameSource.ResolveInf(inf, "wrong-section", matching, model), "wrong install section accepted");
            Refuse(() => GpuDriverNameSource.ResolveInf(inf, "InstallGPU", matching + "BAD", model), "wrong hardware accepted");
            Refuse(() => GpuDriverNameSource.ResolveInf(inf, "InstallGPU", matching, "NVIDIA GeForce RTX 5090"), "wrong model accepted");
            Save(Inf(cls: "{4d36e972-e325-11ce-bfc1-08002be10318}"));
            Refuse(() => GpuDriverNameSource.ResolveInf(inf, "InstallGPU", matching, model), "non-display INF accepted");
            Save(Inf(extra: $"\"Other Model\"=InstallGPU,{matching}"));
            Refuse(() => GpuDriverNameSource.ResolveInf(inf, "InstallGPU", matching, model), "ambiguous model accepted");
            foreach (var value in new[] { "", "@fake", "Unknown%Name", "a\nb", "Microsoft Basic Display Adapter" })
                Check(!GpuDriverNameSource.ValidName(value), "unsafe/unavailable driver name rejected");
            foreach (var value in new[] { @"SOFTWARE\target", path + @"\child", path + "\n", path.Replace("DEV_28E0", "DEV_ZZZZ") })
                Check(!GpuDriverNameSource.ValidPath(value), "unrelated/malformed registry path rejected");
            Check(GpuDriverNameSource.ValidPath(path), "valid PCI path accepted");
            Check(GpuDriverNameSource.MatchesHardware(path, matching, [matching]), "exact own-device hardware binding accepted");
            const string revisionMatch = @"PCI\VEN_10DE&DEV_28E0&REV_A1";
            Check(GpuDriverNameSource.MatchesHardware(path + "", revisionMatch, [revisionMatch]), "registered generic revision ID need not share SUBSYS suffix order");
            Check(!GpuDriverNameSource.MatchesHardware(path, matching, [revisionMatch]), "driver match absent from this device's HardwareID rejected");
            Check(!GpuDriverNameSource.MatchesHardware(path, @"PCI\VEN_1002&DEV_28E0", [@"PCI\VEN_1002&DEV_28E0"]), "other adapter vendor cannot be borrowed");
            Check(!BackendModules.IsMutating(BackendModules.Gpu, BackendCommands.GpuDriverNamePreview)
                && BackendModules.IsMutating(BackendModules.Gpu, BackendCommands.GpuDriverNameRestore), "preview read-only; confirmed restore retains write-ahead gate");

            var evidence = new GpuDriverNameSource.Evidence(path, "NVIDIA GeForce GTX 750", "display/0000", "oem123.inf", "InstallGPU", matching,
                model, "1.2.3.4", "INF-HASH", "@oem123.inf,%GPU%;" + model, new(true, path, "pending-generation"));
            var now = evidence; var calls = new List<string>(); string fail = "";
            GpuDriverNameRecovery Recovery() => new(_ => { calls.Add("read"); if (fail == "read") throw new IOException("read fail"); return now; },
                _ => { calls.Add("save"); if (fail == "save") throw new IOException("disk fail"); if (fail == "changed") now = now with { DriverVersion = "new" }; },
                _ => { calls.Add("write+verify"); if (fail == "write") throw new IOException("readback fail"); },
                _ => { calls.Add("complete"); if (fail == "complete") throw new IOException("settings fail"); });
            var policy = Recovery();
            var preview = policy.Preview(path); Check(calls.SequenceEqual(new[] { "read" }), "preview has no writes or saved evidence"); calls.Clear();
            var result = policy.Restore(path, preview.Fingerprint);
            Check(result.Success && result.RequiresReboot && !result.RequiresDisplayConfirm && result.Message.Contains("不是找回旧备份"), "successful reconstruction reports source and restart without auto reload");
            Check(calls.SequenceEqual(new[] { "read", "save", "read", "write+verify", "complete" }), "durable evidence/revalidation before write, completion after readback");
            foreach (var changed in new[] { evidence with { DriverVersion = "2" }, evidence with { CurrentDescription = "new" }, evidence with { InfSha256 = "new" }, evidence with { MatchingId = "changed" }, evidence with { Pending = new(true, path, "new-generation") }, evidence with { RegistryPath = path + "OTHER" } })
            {
                now = changed; calls.Clear(); result = policy.Restore(path, preview.Fingerprint);
                Check(!result.Success && !result.OutcomeUnknown && !calls.Contains("save") && !calls.Contains("write+verify"), "stale device/driver/current value/task confirmation rejected");
            }
            now = evidence;
            foreach (var error in new[] { "read", "save", "changed", "write", "complete" })
            {
                fail = error; now = evidence; calls.Clear(); result = policy.Restore(path, preview.Fingerprint);
                Check(!result.Success && result.OutcomeUnknown == (error is "write" or "complete"), "failure phase distinguishes no-write and unconfirmed results: " + error);
                Check(error is "write" or "complete" || !calls.Contains("write+verify"), "pre-write failure cannot touch adapter");
                Check(error != "write" || !calls.Contains("complete"), "failed readback cannot clear pending task");
            }
            fail = ""; calls.Clear();
            Check(!policy.Restore(path, "bad").Success && calls.Count == 0, "malformed confirmation never queries or writes");
            Check(!policy.Restore(@"SOFTWARE\other", preview.Fingerprint).Success && calls.Count == 0, "unrelated path never queries or writes");
            var gameBlocked = new GpuDriverNameRecovery(_ => evidence, _ => { },
                _ => throw new GpuDriverNameRecovery.BeforeWriteException("game running"), _ => throw new Exception("must not complete"));
            var stopped = gameBlocked.Restore(path, evidence.Fingerprint);
            Check(!stopped.Success && !stopped.OutcomeUnknown && !stopped.IsPartial, "game/unknown gate before ACL does not create a false unknown-write blocker");
            var uncertain = new OperationResult { Success = false, OutcomeUnknown = true, IsPartial = true, Message = "fixture" };
            var envelope = new BackendResult(BackendResultStatuses.Failed, BackendCommands.GpuDriverNameRestore, "fixture", [], true,
                System.Text.Json.JsonSerializer.SerializeToElement(uncertain, BackendJson.SerializerOptions));
            var mapped = DeltaNFD.Services.Backends.BackendGpuSpoofService.MapDriverNameResult(envelope);
            Check(!mapped.Success && mapped.OutcomeUnknown && mapped.IsPartial, "backend result preserves unconfirmed reconstruction over IPC");
            Console.WriteLine($"Driver-name reconstruction: {count} checks; native INF fixture reads and simulated writes only.");
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new IOException("fixture path escaped");
            Directory.Delete(root, true);
        }
    }
}
