using System.Text.Json;
using System.Xml.Linq;
using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

internal static class GpuShaderGuidanceChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
        var session = new GpuShaderGuidanceSession();
        Check(session.Revision is null, "new process/session has no reminder");
        foreach (var result in new[]
        {
            OperationResult.Ok("unrelated"), OperationResult.Fail("failed"), OperationResult.Skip("unchanged"),
            new OperationResult { SuggestShaderMaintenance = true },
            new OperationResult { Success = true, IsSkipped = true, SuggestShaderMaintenance = true },
            new OperationResult { Success = true, IsPartial = true, SuggestShaderMaintenance = true },
            new OperationResult { Success = true, OutcomeUnknown = true, SuggestShaderMaintenance = true },
        }) Check(!session.Observe(result) && session.Revision is null, "unrelated, failed, skipped or unknown result does not invent success guidance");
        Check(GpuShaderGuidance.AfterVerifiedChange("GTX 750", "GTX 750") == "", "same raw name has no new guidance");
        var notice = GpuShaderGuidance.AfterVerifiedChange("@oem.inf;RTX 4060", "GTX 750");
        Check(notice.Contains("型号生效后") && notice.Contains("完整预热"), "changed verified name explains effective-before-clean ordering");
        var operation = OperationResult.Ok("verified" + notice, requiresReboot: true, suggestShaderMaintenance: true);
        var roundTrip = JsonSerializer.Deserialize<OperationResult>(JsonSerializer.Serialize(operation))!;
        Check(roundTrip.SuggestShaderMaintenance && roundTrip.RequiresReboot && roundTrip.Success, "typed suggestion survives backend payload serialization");
        Check(!JsonSerializer.Deserialize<OperationResult>("{\"Success\":true,\"Message\":\"legacy\"}")!.SuggestShaderMaintenance, "old payload does not invent a reminder");
        var signals = 0;
        Action badSubscriber = () => throw new InvalidOperationException("owned listener fixture");
        session.Changed += badSubscriber;
        session.Changed += () => signals++;
        Check(session.Observe(roundTrip) && session.Revision is not null && signals == 1, "verified change triggers ephemeral guidance despite a failed listener");
        var first = session.Revision!.Value;
        Check(!session.Dismiss(Guid.NewGuid()) && session.Revision == first, "stale close cannot dismiss a new change");
        Check(session.Observe(roundTrip) && session.Revision != first, "another model change replaces in-memory generation");
        Check(!session.Dismiss(first) && session.Revision is not null, "old visible notice cannot close latest guidance");
        var current = session.Revision!.Value;
        Check(session.Dismiss(current) && session.Revision is null && signals == 3, "close dismisses current guidance immediately");
        Check(!session.Dismiss(current), "repeated close is harmless");
        Check(new GpuShaderGuidanceSession().Revision is null, "new application session does not restore reminder");
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => session.Observe(roundTrip))));
        Check(session.Revision is not null, "parallel completions leave a valid current generation");
        Check(session.Dismiss(session.Revision!.Value), "latest generation remains dismissible after parallel completions");

        const string device = @"SYSTEM\CurrentControlSet\Enum\PCI\VEN_10DE&DEV_28E0\4&fixture&0";
        var evidence = new GpuDriverNameSource.Evidence(device, "GTX 750", "driver", "oem.inf", "section", "hardware", "RTX 4060", "621.14", "sha", "@oem.inf;RTX 4060", new(false, "", ""));
        foreach (var fail in new[] { "", "write", "read", "same" })
        {
            var guideCalls = 0;
            var data = fail == "same" ? evidence with { CurrentDescription = evidence.Description } : evidence;
            var engine = new GpuDriverNameRecovery(_ => { if (fail == "read") throw new IOException("owned read failure"); return data; }, _ => { },
                _ => { if (fail == "write") throw new IOException("owned verify failure"); }, _ => { },
                e => { guideCalls++; return GpuShaderGuidance.AfterVerifiedChange(e.CurrentDescription, e.Description); });
            var result = engine.Restore(device, data.Fingerprint);
            Check(result.Success == (fail is "" or "same"), "native-name restoration keeps original success/failure semantics");
            Check(result.SuggestShaderMaintenance == (fail == ""), "native-name guide only after verified actual change");
            Check(guideCalls == (fail is "" or "same" ? 1 : 0), "failed read/write never announces verified model change");
            var mapped = BackendGpuSpoofService.MapDriverNameResult(new BackendResult(
                result.Success ? BackendResultStatuses.Ok : BackendResultStatuses.Failed,
                BackendCommands.GpuDriverNameRestore, result.Message, [], false, JsonSerializer.SerializeToElement(result, BackendJson.SerializerOptions)));
            Check(mapped.SuggestShaderMaintenance == result.SuggestShaderMaintenance, "frontend native-name mapper preserves typed guide flag");
        }

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src/DeltaNFD/MainWindow.xaml.cs"))) root = root.Parent;
        if (root is null) throw new Exception("Cannot locate source wiring.");
        string Source(string path) => File.ReadAllText(Path.Combine(root.FullName, path));
        foreach (var page in new[] { "DashboardPage", "GpuSpoofPage", "ShaderPage", "FramePage" })
        {
            var xaml = XDocument.Parse(Source($"src/DeltaNFD/Views/{page}.xaml"));
            Check(xaml.Descendants().Count(e => e.Name.LocalName == "GpuShaderGuidanceBar") == 1, page + " exposes a single shared transient guide");
        }
        var bar = Source("src/DeltaNFD/Controls/GpuShaderGuidanceBar.xaml.cs");
        Check(bar.Contains("NavigateByTag(\"shader\")") && bar.Contains("DeltaRunning == false") && bar.Contains("!ServiceLocator.GameTarget.IsCustom"), "navigation uses existing target/game gates");
        Check(!bar.Contains("ClearAsync") && !bar.Contains("ClearPsoCache") && !bar.Contains("AppSettingsStore"), "guide cannot clean cache or write settings");
        Check(bar.Contains("Changed -= QueueRefresh") && bar.Contains("if (_attached) Refresh()"), "unload detaches subscriptions and discards stale queued render");
        Check(Source("src/DeltaNFD/Controls/GpuShaderGuidanceBar.xaml").Contains("IsClosable=\"True\""), "guide can be dismissed without completing a maintenance transaction");
        Check(!Source("src/DeltaNFD/Services/GpuShaderGuidanceSession.cs").Contains("AppSettingsStore") && !Source("src/DeltaNFD/Services/GpuShaderGuidanceSession.cs").Contains("File."), "session has no persistence calls");
        Check(!Source("src/DeltaNFD.Shared/Services/AppSettingsStore.cs").Contains("GpuShaderGuidance"), "no reminder fields added to persisted settings");
        var spoof = Source("src/DeltaNFD.Execution/Execution/GpuSpoofService.cs");
        Check(spoof.IndexOf("伪装名称写后复核失败", StringComparison.Ordinal) < spoof.IndexOf("AfterVerifiedChange(current, fakeName)", StringComparison.Ordinal), "spoof guide hook occurs after native write verification");
        Check(spoof.IndexOf("原始型号恢复复核失败", StringComparison.Ordinal) < spoof.IndexOf("AfterVerifiedChange(current, backup.Data)", StringComparison.Ordinal), "normal restore guide hook occurs after verification");
        Check(Source("src/DeltaNFD/Views/GpuSpoofPage.xaml.cs").Contains("型号生效后，请手动到「着色器维护」"), "immediate reboot confirmation explains next steps before application exits");
        Console.WriteLine($"GPU model shader guidance: {count} checks passed; memory-only guidance, simulated results, no GUI/GPU/cache/settings writes.");
    }
}
