using System.Diagnostics;
using System.Text.Json;
using DeltaNFD.Execution.Procman;
using DeltaNFD.Services;

// Actual procman loop + fake process APIs, and the production EcoQoS RMW/comparison logic.
// No native process-property writes or timer requests.
internal static class FrameAcceptanceFixChecks
{
    private static int _checks;
    private static void Check(bool ok, string why) { if (!ok) throw new IOException(why); _checks++; }
    private static readonly ProcmanProcessIdentity Id = new(12345, DateTime.UtcNow, @"C:\Games\DeltaForceClient-Win64-Shipping.exe");

    internal static async Task RunAsync()
    {
        _checks = 0;
        var token = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-acceptance-fix-" + token.ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), token.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, token);
        try
        {
            await LoopAsync(root);
            EcoQos(root);
            ExitWiring();
            Console.WriteLine($"Frame acceptance fixes: {_checks} passed; real procman loop, fake power APIs, isolated journals; exit aggregation source guard. No native writes or timer requests.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Configure(bool active = true, bool selected = true) => AppSettingsStore.Update(s =>
    {
        s.FrameModeActive = active; s.FrameGameTimerIgnoreEnabled = selected;
        s.FrameAutomaticMemoryEnabled = false; s.FrameGamePriorityEnabled = false;
    });

    private static ProcmanEngine Engine(string root, Api api) => new(Path.Combine(root, "changes.json"), Path.Combine(root, "rules.json"),
        timerIgnoreApi: api, timerProbe: new FrameTimerLegacyProbe(new FakeProbe()))
    { ExperimentalTimerFeaturesForTests = true, FrameTimerIgnoreTargetsForTests = () => Volatile.Read(ref api.TargetPresent) ? [Id] : [],
      GlobalPrecisionForTests = () => new() { Supported = true, Readable = true, Message = "fake setup off" } };

    private static async Task Until(Func<bool> condition, string why)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(7)) await Task.Delay(25);
        Check(condition(), why);
    }

    private static async Task LoopAsync(string root)
    {
        Configure();
        foreach (var late in new[] { false, true })
        {
            var dir = Path.Combine(root, late ? "late" : "present"); Directory.CreateDirectory(dir);
            var api = new Api { TargetPresent = !late };
            using var engine = Engine(dir, api);
            Check(!engine.BackgroundLoopDisabledForTests, "Integration must not bypass the running-loop gate");
            Check(engine.SyncFrameBackground().Success && engine.Snapshot().RulesEngineRunning, "Timer-only option starts real loop");
            if (!ProcmanEngine.FrameTimerIgnoreSupported)
            {
                Check(api.Writes == 0, "Unsupported system never writes");
                continue;
            }
            api.TargetPresent = true;
            await Until(() => engine.Snapshot().FrameTimerIgnore.AppliedCount == 1, "Late game is applied by the existing loop");
            Check(engine.SyncFrameBackground().Success && engine.Snapshot().RulesEngineRunning, "Resync retains timer-only loop");
            Check(api.Writes == 1, "Resync is idempotent and does not undo/reapply");
            // A disabled rule must not leave the still-needed timer loop stopped.
            var rule = new ProcmanRuleEntry { ExecutablePath = Path.Combine(dir, "absent.exe"), Capability = "ecoQos", Value = "true", Enabled = false };
            Check(engine.SaveRule(rule).Success && engine.Snapshot().RulesEngineRunning, "Saving a disabled rule restarts timer maintenance");
            Check((await engine.DeleteRuleAsync(rule.ExecutablePath, rule.Capability)).Success && engine.Snapshot().RulesEngineRunning,
                "Deleting the final rule preserves timer maintenance");
            Configure(selected: false);
            Check(engine.SyncFrameBackground().Success && engine.Snapshot().RulesEngineRunning, "Disabling ignore retains frame clock supervision");
            Check(api.State == new FrameTimerPowerState(0, 0) && engine.Snapshot().FrameTimerIgnore.BackupCount == 0, "Disabling restores originals");
            Configure(active: false, selected: false);
            Check(engine.SyncFrameBackground().Success && !engine.Snapshot().RulesEngineRunning, "Closing frame stops the final supervision loop");
            Configure();
            Check(engine.SyncFrameBackground().Success && engine.Snapshot().RulesEngineRunning, "Re-enabling starts maintenance again");
            api.Deny = true;
            Check(!(await engine.StopRulesEngineAsync()).Success && engine.Snapshot().FrameTimerIgnore.RestorePending,
                "A denied restore fails and retains responsibility");
            Check(File.Exists(Path.Combine(dir, "frame-game-timer-ignore.json")), "Failed restore keeps durable journal");
            api.Deny = false;
            Check((await engine.RestoreAllAsync()).Success && api.State.Control == 0 && engine.Snapshot().FrameTimerIgnore.BackupCount == 0,
                "Restore-all retry restores originals and clears only completed responsibility");
            Configure(active: false);
            Check(engine.SyncFrameBackground().Success && api.State.Control == 0, "Inactive frame never re-applies");
            Configure();
        }
        if (!ProcmanEngine.FrameTimerIgnoreSupported) return;
        var restart = Path.Combine(root, "restart"); Directory.CreateDirectory(restart);
        var journal = Path.Combine(restart, "frame-game-timer-ignore.json");
        File.WriteAllText(journal, JsonSerializer.Serialize(new[] { new FrameGameTimerIgnore.Backup(Id, 1, 1, 5, 5) }));
        var restartedApi = new Api { TargetPresent = true, State = new(5, 5) };
        using (var restarted = Engine(restart, restartedApi))
        {
            Configure(active: false);
            Check(restarted.SyncFrameBackground().Success && restartedApi.State == new FrameTimerPowerState(1, 1),
                "Fresh engine restores an existing journal and preserves EcoQoS");
        }
        Configure(active: false);
    }

    private static void EcoQos(string root)
    {
        // Both application orders and both restoration orders, using the actual bit-4 controller.
        foreach (var timerFirst in new[] { false, true })
        foreach (var timerRestoreFirst in new[] { false, true })
        foreach (uint originalControl in new uint[] { 0, 1, 4, 5 })
        foreach (uint originalState in new uint[] { 0, 1, 4, 5 })
        {
            if ((originalState & ~originalControl) != 0) continue;
            var dir = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            var api = new Api { State = new(originalControl, originalState) };
            var timer = new FrameGameTimerIgnore(Path.Combine(dir, "timer.json"), new ProcessPropertyGate(Path.Combine(dir, "owners.json")), api,
                new FrameTimerLegacyProbe(new FakeProbe()));
            bool Timer() => timer.Sync([Id], _ => true, "wait", true);
            bool StopTimer() => timer.Sync([], _ => false, "restored", true);
            ProcmanCapabilityResult Read() => ProcmanCapabilityResult.Success($"eco:{api.State.Control}:{api.State.State}");
            ProcmanCapabilityResult Write(uint c, uint s) { Check((s & ~c) == 0, "Native write masks remain valid"); api.State = new(c, s); return ProcmanCapabilityResult.Success(""); }
            if (timerFirst) Check(Timer(), "Timer first");
            var ecoOriginal = Read().Value!;
            // Toggle bit 1 so this always exercises the write path.
            var target = (api.State.State & 1) == 0 ? "eco:1:1" : "eco:1:0";
            var applied = ProcmanCapabilities.UpdateEcoQos(target, Read, Write);
            Check(applied.Ok, "Eco applies without disturbing timer");
            if (!timerFirst) Check(Timer(), "Timer second");
            if (timerRestoreFirst) Check(StopTimer(), "Timer restore first");
            var current = Read().Value!;
            Check(ProcmanEngine.MatchesRecordedValue("ecoQos", current, applied.Value), "Timer drift does not create an EcoQoS conflict");
            Check(ProcmanCapabilities.UpdateEcoQos(ecoOriginal, Read, Write).Ok, "Legacy full-mask Eco journal restores only bit 1");
            if (!timerRestoreFirst) Check(StopTimer(), "Timer restore second");
            Check(api.State.Control == originalControl && api.State.State == originalState, "Both responsibilities return to their own originals");
        }
        Check(!ProcmanEngine.MatchesRecordedValue("ecoQos", "eco:5:4", "eco:5:5"), "An external bit-1 change is still a conflict");
        Check(!ProcmanEngine.MatchesRecordedValue("priority", "32", "128"), "Other capabilities retain exact comparison");
        Check(ProcmanCapabilities.EcoQosMatches("eco:4:5", "eco:0:0"), "System-managed execution state is not a restore target");
        foreach (var invalid in new[] { "", "eco:x:1", "eco:1:-1", "eco:1:4294967296", "EcoQoS 启用" })
        {
            var writes = 0;
            var result = ProcmanCapabilities.UpdateEcoQos(invalid, () => ProcmanCapabilityResult.Success("eco:5:5"), (c, s) => { writes++; return ProcmanCapabilityResult.Success(""); });
            Check(!result.Ok && writes == 0 && !ProcmanCapabilities.EcoQosMatches(invalid, invalid), "Malformed legacy masks never authorize writes");
        }
        var state = "eco:5:5";
        Check(!ProcmanCapabilities.UpdateEcoQos("eco:0:0", () => ProcmanCapabilityResult.Failure("denied"), (_, _) => throw new Exception("must not write")).Ok, "Unreadable state fails closed");
        Check(!ProcmanCapabilities.UpdateEcoQos("eco:0:0", () => ProcmanCapabilityResult.Success(state), (_, _) => ProcmanCapabilityResult.Failure("denied")).Ok, "Write failure is not success");
        Check(!ProcmanCapabilities.UpdateEcoQos("eco:0:0", () => ProcmanCapabilityResult.Success(state), (_, _) => ProcmanCapabilityResult.Success("")).Ok, "Wrong readback is not success");
        var reads = 0;
        Check(!ProcmanCapabilities.UpdateEcoQos("eco:0:0", () => ++reads == 1 ? ProcmanCapabilityResult.Success(state) : ProcmanCapabilityResult.Failure("lost"), (_, _) => ProcmanCapabilityResult.Success("")).Ok, "Unreadable readback remains unconfirmed");
        var clear = ProcmanCapabilities.UpdateEcoQos("eco:0:0", () => ProcmanCapabilityResult.Success(state), (c, s) => { state = $"eco:{c}:{s}"; return ProcmanCapabilityResult.Success(""); });
        Check(clear.Ok && state == "eco:4:4", "ClearEcoQos releases only execution-speed control");
        for (uint c = 0; c < 8; c++)
        for (uint s = 0; s < 8; s++)
        foreach (var target in new[] { "eco:0:0", "eco:1:0", "eco:1:1" })
        {
            state = $"eco:{c}:{s}";
            Check(ProcmanCapabilities.UpdateEcoQos(target, () => ProcmanCapabilityResult.Success(state), (nc, ns) =>
            {
                Check((ns & ~nc) == 0 && (nc & ~1u) == (c & ~1u) && (ns & c & ~1u) == (s & c & ~1u),
                    "Only bit 1 changes; other controlled bits survive; no invalid StateMask");
                state = $"eco:{nc}:{ns}"; return ProcmanCapabilityResult.Success("");
            }).Ok, "Every controlled/uncontrolled combination is handled");
        }
    }

    private static void ExitWiring()
    {
        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "DeltaNFD.sln"))) current = current.Parent;
        if (current is null) throw new IOException("Cannot find repository");
        var source = File.ReadAllText(Path.Combine(current.FullName, "src/DeltaNFD/Services/FrameService.cs"));
        var start = source.IndexOf("// 退出帧格：", StringComparison.Ordinal);
        var block = source[start..source.IndexOf("if (!useSpoof) return", start, StringComparison.Ordinal)];
        Check(block.Contains("var gamePowerNote = await cpuFrame.ApplyGamePowerAsync(false);"), "Exit must retain game-power recovery result");
        Check(block.Contains("gamePowerNote.Message") && block.Contains("new[] { gamePowerNote, tweaksNote, powerNote, background }"), "Exit must aggregate every result and diagnostic");
        Check(block.Contains("!result.Success || result.IsPartial || result.OutcomeUnknown"), "Failure/partial/unknown cannot be a successful exit");
        Check(block.Contains("OutcomeUnknown = recoveryResults.Any") && block.Contains("IsPartial = recoveryResults.Any"), "Exit must preserve unknown/partial semantics");
    }

    /// <summary>假探针：固定返回时钟可达，测试门禁之外的既有循环行为。</summary>
    private sealed class FakeProbe : IFrameTimerReachabilityApi
    {
        public double? MeasureRequestlessGranularityMs() => 1.0;
    }

    private sealed class Api : IFrameTimerIgnoreApi
    {
        internal bool TargetPresent;
        internal volatile bool Deny;
        internal FrameTimerPowerState State = new(0, 0);
        private int _writes;
        internal int Writes => Volatile.Read(ref _writes);
        public string IdentityState(ProcmanProcessIdentity id) => "same";
        public IFrameTimerIgnoreHandle Open(ProcmanProcessIdentity id) => new Handle(this);
        private sealed class Handle(Api api) : IFrameTimerIgnoreHandle
        {
            public FrameTimerPowerState Read() => api.State;
            public void Write(FrameTimerPowerState state)
            {
                if (api.Deny) throw new IOException("fake write denied");
                api.State = state; Interlocked.Increment(ref api._writes);
            }
            public void Dispose() { }
        }
    }
}
