using DeltaNFD.Execution.CpuFrame;
using DeltaNFD.Services;

internal static class FrameCloseChecks
{
    private static int _count;
    private static void Check(bool ok, string why) { if (!ok) throw new IOException(why); _count++; }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static async Task RunAsync()
    {
        _count = 0;
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-frame-close-" + id.ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        try
        {
            var api = new FakeApi();
            var power = new PowerService(api, () => true);
            var queried = await power.GetHeteroPoliciesAsync();
            Check(queried.Count == 3 && api.Writes.Count == 0 && api.Activations.Count == 0, "native query never writes or unhides");
            Check(queried.All(p => p.AcValue == 4 && p.DcValue == 0), "native query preserves separate AC/DC values");
            var setting = PowerSchemes.HeteroPolicySettings[0].Guid;
            var result = await power.SetHeteroPolicyValuesAsync(setting, 0, 2);
            Check(result.Success && api.Read(api.Scheme, Guid.Parse(setting)) == (0, 2), "native write and readback complete");
            Check(api.Writes.Count == 2 && api.Writes.All(w => w.Scheme == api.Scheme) && api.Activations.Count == 1,
                "writes are fixed to one scheme and activated once");
            api.Writes.Clear(); api.Activations.Clear();
            Check((await power.SetHeteroPolicyValuesAsync(setting, 0, 2)).Success && api.Writes.Count == 0 && api.Activations.Count == 0,
                "already matching policies cause no writes or activation");
            Check(!(await power.SetHeteroPolicyValuesAsync(setting, 99, 2)).Success && api.Writes.Count == 0,
                "unsupported policy value never writes");
            Check(!(await new PowerService(api, () => false).SetHeteroPolicyValuesAsync(setting, 4, 0)).Success
                && api.Writes.Count == 0, "non-admin cannot write through native path");

            api = new FakeApi { FailRead = true }; power = new(api, () => true);
            Check(!(await power.SetHeteroPolicyValuesAsync(setting, 0, 2)).Success && api.Writes.Count == 0,
                "unknown original values block all writes");
            api = new FakeApi { SwitchOnReadOnce = true }; power = new(api, () => true);
            Check(!(await power.SetHeteroPolicyValuesAsync(setting, 4, 0)).Success && api.Writes.Count == 0,
                "a plan change cannot be reported as an already-matching no-op");
            api = new FakeApi { FailDcOnce = true }; power = new(api, () => true);
            result = await power.SetHeteroPolicyValuesAsync(setting, 0, 2);
            Check(!result.Success && !result.OutcomeUnknown && api.Read(api.Scheme, Guid.Parse(setting)) == (4, 0),
                "second write failure precisely restores AC/DC");
            api = new FakeApi { IgnoreDcOnce = true }; power = new(api, () => true);
            result = await power.SetHeteroPolicyValuesAsync(setting, 0, 2);
            Check(!result.Success && !result.OutcomeUnknown && api.Read(api.Scheme, Guid.Parse(setting)) == (4, 0),
                "readback mismatch rolls back instead of claiming success");
            api = new FakeApi { SwitchAfterWrite = true }; power = new(api, () => true);
            var original = api.Scheme;
            result = await power.SetHeteroPolicyValuesAsync(setting, 0, 2);
            Check(!result.Success && api.Scheme != original && api.Activations.Count == 0,
                "external plan switch never activates the old plan");
            Check(api.Writes.All(w => w.Scheme == original) && api.Read(original, Guid.Parse(setting)) == (4, 0),
                "rollback only restores the originally selected plan");
            api = new FakeApi { ConflictOnActivate = true }; power = new(api, () => true);
            result = await power.SetHeteroPolicyValuesAsync(setting, 0, 2);
            Check(!result.Success && result.OutcomeUnknown && result.IsPartial && api.Read(api.Scheme, Guid.Parse(setting)).Dc == 3,
                "external conflicting value remains untouched and result stays unconfirmed");

            // Hold a real engine tick while a user requests stop. No production tick/system writes run.
            api = new FakeApi(); power = new(api, () => true);
            var tweaks = new FrameTweaksService(root, (_, _) => throw new IOException("fixture forbids powercfg"), () => api.Scheme.ToString("D"));
            using var engine = new CpuFrameEngine(power, tweaks);
            var entered = Signal(); var release = Signal();
            var clock = DateTimeOffset.UtcNow;
            var tickCount = 0; var deltaReads = 0;
            engine.ClockForTests = () => clock;
            engine.DeltaReaderForTests = () => { deltaReads++; return false; };
            engine.TickForTests = async _ => { Interlocked.Increment(ref tickCount); entered.TrySetResult(); await release.Task; };
            await engine.StartMonitorAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var first = engine.Snapshot();
            Check(first.DeltaForceRunning == false && first.DeltaForceObservedUtc == clock,
                "busy tick does not block the independent Delta observation");
            engine.Snapshot();
            Check(deltaReads == 1, "repeated snapshots within the heartbeat do not duplicate process queries");
            clock = clock.AddSeconds(12);
            var next = engine.Snapshot();
            Check(next.DeltaForceObservedUtc == clock && next.DeltaForceRunning == false && deltaReads == 2,
                "status heartbeat stays fresh beyond the former ten-second stale limit");
            clock = clock.AddSeconds(2); engine.DeltaReaderForTests = () => throw new IOException("fixture read denied");
            Check(engine.Snapshot().DeltaForceRunning is null, "actual observation failure remains unknown");
            clock = clock.AddSeconds(2); engine.DeltaReaderForTests = () => false;
            Check(engine.Snapshot().DeltaForceRunning == false, "same heartbeat recovers without a second monitor");
            var restored = false;
            var stop = engine.SerializeAutomationAsync(() => { restored = true; return Task.FromResult(true); }, stopMonitor: true);
            Check(!restored && !stop.IsCompleted, "stop waits for the submitted tick instead of racing its policy writes");
            release.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            Check(restored && tickCount == 1 && !engine.Snapshot().MonitorRunning,
                "stop cancels future ticks and performs the restore after the held tick drains");
            Check(engine.Snapshot().DeltaForceRunning is null, "stopped monitor cannot unlock pages from old observations");
            var commandEntered = Signal(); var commandRelease = Signal();
            var command = engine.SerializeAutomationAsync(async () => { commandEntered.SetResult(); await commandRelease.Task; return true; });
            await commandEntered.Task;
            var secondStarted = false;
            var second = engine.SerializeAutomationAsync(() => { secondStarted = true; return Task.FromResult(true); });
            Check(!secondStarted, "two user mutations share the same serialization as maintenance");
            commandRelease.SetResult(); await Task.WhenAll(command, second).WaitAsync(TimeSpan.FromSeconds(5));
            Check(secondStarted && api.Writes.Count == 0, "queued command drains without any fixture system write");

            Console.WriteLine($"Frame close checks: {_count} passed; real engine scheduling with held fake ticks, simulated native power API only. No host policies/process attributes changed.");
        }
        finally
        {
            var prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("fixture escaped temp");
            Directory.Delete(root, true);
        }
    }

    internal static void ReadNative()
    {
        var time = System.Diagnostics.Stopwatch.StartNew();
        var values = new HeteroPowerPolicy(new HeteroPowerPolicy.NativeApi()).Read();
        Console.WriteLine($"Read-only native hetero query: {values.Count} settings, {time.Elapsed.TotalMilliseconds:F1} ms; no write or activation invoked.");
    }

    private sealed class FakeApi : HeteroPowerPolicy.IApi
    {
        public Guid Scheme = Guid.NewGuid();
        private readonly Dictionary<(Guid Scheme, Guid Setting), (int Ac, int Dc)> _values = [];
        public readonly List<(Guid Scheme, bool Ac, int Value)> Writes = [];
        public readonly List<Guid> Activations = [];
        public bool FailRead, FailDcOnce, IgnoreDcOnce, SwitchAfterWrite, ConflictOnActivate, SwitchOnReadOnce;
        public Guid Active() => Scheme;
        public (int Ac, int Dc) Read(Guid scheme, Guid setting)
        {
            if (FailRead) throw new IOException("fixture read failure");
            var value = _values.GetValueOrDefault((scheme, setting), (4, 0));
            if (SwitchOnReadOnce) { SwitchOnReadOnce = false; Scheme = Guid.NewGuid(); }
            return value;
        }
        public void Write(Guid scheme, Guid setting, bool ac, int value)
        {
            Writes.Add((scheme, ac, value));
            if (!ac && FailDcOnce) { FailDcOnce = false; throw new IOException("fixture DC failure"); }
            if (!ac && IgnoreDcOnce) { IgnoreDcOnce = false; return; }
            var old = Read(scheme, setting);
            _values[(scheme, setting)] = ac ? (value, old.Dc) : (old.Ac, value);
            if (SwitchAfterWrite) { SwitchAfterWrite = false; Scheme = Guid.NewGuid(); }
        }
        public void Activate(Guid scheme)
        {
            Activations.Add(scheme);
            if (ConflictOnActivate)
            {
                ConflictOnActivate = false;
                var setting = Guid.Parse(PowerSchemes.HeteroPolicySettings[0].Guid);
                var before = Read(scheme, setting);
                _values[(scheme, setting)] = (before.Ac, 3);
            }
        }
    }
}
