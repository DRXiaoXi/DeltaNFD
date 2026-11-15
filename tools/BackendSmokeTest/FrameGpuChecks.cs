using System.Runtime.InteropServices;
using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Execution.Procman;
using DeltaNFD.Services;

namespace BackendSmokeTest;

internal static class FrameGpuChecks
{
    private static int _checks;
    private static void Check(bool value, string text) { if (!value) throw new Exception(text); _checks++; }
    private static void Reject(Action action, string text)
    { try { action(); } catch (IOException) { _checks++; return; } throw new Exception(text); }
    private sealed class Lease(Action finish) : IDisposable { private Action? _finish = finish; public void Dispose() => Interlocked.Exchange(ref _finish, null)?.Invoke(); }
    private sealed class Nvidia : INvidiaGpuShareApi
    {
        internal int Current = 17, Reads, Leases;
        internal bool Supported = true, Hags = true, FailRead, FailWrite, WrongReadback, LeaseDenied;
        internal string Key = new('A', 64);
        internal List<int> Writes = [];
        internal Action<int>? BeforeWrite;
        internal Func<NvidiaGpuShareInfo, NvidiaGpuShareInfo>? ChangeInfo;
        public NvidiaGpuShareInfo Read()
        {
            Reads++; if (FailRead) throw new IOException("fixture query failure");
            var result = new NvidiaGpuShareInfo { Supported = Supported, HagsEnabled = Hags, HardwareKey = Key, Current = Current,
                Default = 20, Minimum = 10, Maximum = 90, Step = 1, PresetMask = 15 };
            return ChangeInfo?.Invoke(result) ?? result;
        }
        public void Write(NvidiaGpuShareInfo expected, int percent)
        {
            BeforeWrite?.Invoke(percent);
            if (Key != expected.HardwareKey || Current != expected.Current) throw new IOException("fixture hardware/value changed");
            Writes.Add(percent); if (FailWrite) throw new IOException("fixture write denied");
            Current = WrongReadback ? percent + 1 : percent;
        }
        internal IDisposable Acquire() { if (LeaseDenied) throw new IOException("fixture other owner"); Leases++; return new Lease(() => Leases--); }
        public void Dispose() { }
    }
    private sealed class Priority : IFramePriorityApi
    {
        internal readonly Dictionary<string, int> Values = new() { ["priority"] = 32, ["ioPriority"] = 2, ["memoryPriority"] = 5, ["gpuPriority"] = 2 };
        internal List<(string Name, int Value)> Writes = [];
        internal bool FailGpu, FailCpu;
        internal string State = "same";
        public string IdentityState(ProcmanProcessIdentity identity) => State;
        public IFramePriorityHandle Open(ProcmanProcessIdentity identity) => new Handle(this);
        private sealed class Handle(Priority owner) : IFramePriorityHandle
        {
            public int Read(string property) => owner.Values[property];
            public void Write(string property, int value)
            {
                owner.Writes.Add((property, value));
                if (property == "gpuPriority" && owner.FailGpu || property == "priority" && owner.FailCpu) throw new IOException("fixture denied");
                owner.Values[property] = value;
            }
            public void Dispose() { }
        }
    }
    internal static async Task RunAsync()
    {
        _checks = 0;
        var id = Guid.NewGuid(); var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-frame-gpu-" + id.ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        var defaults = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Check(!defaults.FrameGameGpuPriorityEnabled && !defaults.FrameNvidiaGpuShareEnabled, "old settings do not opt into either GPU control");
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new AppSettings { FrameGameGpuPriorityEnabled = true,
            FrameGameGpuPriority = FrameGpuPriority.AboveNormal, FrameNvidiaGpuShareEnabled = true, FrameNvidiaGpuBackgroundPercent = 37 }))!;
        Check(copy.FrameGameGpuPriorityEnabled && copy.FrameGameGpuPriority == FrameGpuPriority.AboveNormal &&
            copy.FrameNvidiaGpuShareEnabled && copy.FrameNvidiaGpuBackgroundPercent == 37 && !copy.FrameGamePriorityEnabled,
            "independent switches and exact custom percent round trip");
        Check(!new FrameGamePriorityConfig { GpuEnabled = true, Gpu = (FrameGpuPriority)5 }.IsValid, "GPU realtime class refused");
        var process = new ProcmanProcessIdentity(41234, DateTime.UtcNow, @"C:\Games\DeltaForceClient-Win64-Shipping.exe");
        var api = new Priority(); var journal = Path.Combine(root, "priority.json");
        var priority = new FrameGamePriority(journal, new ProcessPropertyGate(Path.Combine(root, "owners.json")), api);
        var gpuOnly = new FrameGamePriorityConfig { ProcessEnabled = false, GpuEnabled = true, Gpu = FrameGpuPriority.High };
        Check(priority.Sync([process], gpuOnly, _ => true), "GPU-only frame priority applies");
        Check(api.Writes.SequenceEqual([("gpuPriority", 4)]) && api.Values["priority"] == 32 && api.Values["ioPriority"] == 2,
            "GPU switch does not alter CPU, IO or memory");
        Check(priority.Snapshot.GpuAppliedCount == 1 && priority.Snapshot.GpuBackupCount == 1, "GPU status reports its own readback and backup");
        Check(priority.Sync([process], gpuOnly, _ => true) && api.Writes.Count == 1, "matching GPU class is not rewritten every tick");
        Check(priority.Sync([], new(), _ => false) && api.Values["gpuPriority"] == 2 && priority.PendingCount == 0,
            "GPU original restored when target exits");
        Check(!priority.Sync([process], gpuOnly, _ => true, propertyAllowed: (_, _) => false) && priority.PendingCount == 0,
            "manual/rule exclusion prevents frame GPU takeover");
        Check(priority.Sync([process], gpuOnly with { ProcessEnabled = true }, _ => true), "all priority kinds can be active independently");
        Check(priority.Sync([process], new(), _ => true) && api.Values["gpuPriority"] == 2 && priority.Snapshot.GpuBackupCount == 0,
            "GPU switch-off restores GPU while process CPU priorities remain active");
        Check(priority.Sync([], new(), _ => false), "combined originals are reversible");
        api.FailGpu = true;
        Check(!priority.Sync([process], gpuOnly, _ => true), "GPU write denial reported");
        var attempts = api.Writes.Count;
        Check(!priority.Sync([process], gpuOnly, _ => true) && api.Writes.Count == attempts, "denied GPU write is not hammered");
        api.FailGpu = false;
        Check(priority.Sync([], new(), _ => false), "denied GPU original can be reconciled without replay");
        Check(priority.Sync([process], gpuOnly, _ => true), "explicit restart permits GPU retry");
        api.State = "missing"; attempts = api.Writes.Count;
        Check(priority.Sync([], new(), _ => false) && api.Writes.Count == attempts, "PID exit/reuse discards old GPU ownership without touching new process");

        Check(Marshal.SizeOf<NativeNvidiaGpuShareApi.InfoV1>() == 32 && Marshal.SizeOf<NativeNvidiaGpuShareApi.SetV1>() == 16,
            "NvCpl v1 structs have statically verified exact lengths");
        NativeNvidiaGpuShareApi.Setting getter = (uint scope, ref ulong target, uint feature, IntPtr pointer, uint size) =>
        {
            Check(scope == 1 && target == 0 && feature == 0x346 && size == 32 && Marshal.ReadInt32(pointer) == 1,
                "native read is restricted to the exact NV App v1 query");
            var bytes = new byte[32]; Marshal.Copy(pointer, bytes, 0, 32);
            Check(bytes.Skip(4).All(x => x == 0), "query buffer fully zero initialized");
            Marshal.StructureToPtr(new NativeNvidiaGpuShareApi.InfoV1 { Version = 1, Supported = 1, Current = 17,
                Default = 20, Minimum = 10, Maximum = 90, Step = 1, PresetMask = 15 }, pointer, false); return 0;
        };
        var info = NativeNvidiaGpuShareApi.Query(getter, new string('A', 64));
        Check(info.Current == 17 && info.Allows(17) && info.Allows(10) && info.Allows(20) && info.Allows(50),
            "driver current custom value and NV App presets preserved");
        Check(!info.Allows(-1) && !info.Allows(101), "out of range percentages refused");
        Check(!(info with { Step = 10, PresetMask = 0 }).Allows(17) && (info with { Minimum = 30, PresetMask = 2 }).Allows(10),
            "actual increments and separately advertised presets respected");
        NativeNvidiaGpuShareApi.Setting setter = (uint scope, ref ulong target, uint feature, IntPtr pointer, uint size) =>
        {
            Check(scope == 1 && target == 0 && feature == 0x347 && size == 16 && Marshal.ReadInt32(pointer) == 1 && Marshal.ReadInt32(pointer, 4) == 37,
                "native write is restricted to the exact NV App v1 percent request");
            Check(Marshal.ReadInt32(pointer, 8) == 0 && Marshal.ReadInt32(pointer, 12) == 0, "write outputs start cleared");
            Marshal.WriteByte(pointer, 8, 1); return 0;
        };
        NativeNvidiaGpuShareApi.Apply(setter, 37);
        Reject(() => NativeNvidiaGpuShareApi.Apply((uint a, ref ulong b, uint c, IntPtr d, uint e) => 5, 10), "failed native status refused");
        Reject(() => NativeNvidiaGpuShareApi.Apply((uint a, ref ulong b, uint c, IntPtr d, uint e) => 0, 10), "missing writeSucceeded refused");
        Reject(() => NativeNvidiaGpuShareApi.Query((uint a, ref ulong b, uint c, IntPtr d, uint e) => { Marshal.WriteInt32(d, 2); return 0; }, "fixture"),
            "unknown native layout version refused");

        var nv = new Nvidia(); var path = Path.Combine(root, "nvidia.json");
        using (var control = new FrameNvidiaGpuShare(path, nv, nv.Acquire))
        {
            Check(control.Sync(false, 10, () => false) && nv.Reads == 0 && nv.Leases == 0, "disabled NVIDIA feature never loads driver/library or takes global lease");
            nv.BeforeWrite = _ => { using var json = JsonDocument.Parse(File.ReadAllText(path)); Check(json.RootElement.GetProperty("Value").GetInt32() == 17, "exact non-preset original durable before every write"); };
            Check(control.Sync(true, 10, () => true) && nv.Current == 10 && nv.Leases == 1 && control.Snapshot.Maintaining,
                "NVIDIA requested percentage read back with global ownership");
            Check(control.Sync(true, 10, () => true) && nv.Writes.Count == 1, "NVIDIA current value read without repeated writes");
            Check(control.Sync(false, 10, () => false) && nv.Current == 17 && nv.Leases == 0 && !File.Exists(path), "closing frame restores exact custom original");
        }
        nv = new(); path = Path.Combine(root, "external.json");
        using (var control = new FrameNvidiaGpuShare(path, nv, nv.Acquire))
        {
            Check(control.Sync(true, 10, () => true), "external-change fixture held"); nv.Current = 31;
            Check(!control.Sync(true, 10, () => true) && nv.Current == 31 && nv.Leases == 0 && !control.Snapshot.RestorePending,
                "NVIDIA App external change yields ownership without overwrite");
            attempts = nv.Writes.Count;
            Check(!control.Sync(true, 10, () => true) && nv.Writes.Count == attempts, "external change is not fought on later ticks");
            Check(Directory.GetFiles(root, "external.json.external-*.bak").Length == 1, "external takeover retains original in software backup archive");
        }
        foreach (var failure in new[] { "unsupported", "hags", "lease", "read", "increment", "original" })
        {
            nv = new() { Supported = failure != "unsupported", Hags = failure != "hags", LeaseDenied = failure == "lease", FailRead = failure == "read" };
            if (failure == "increment") nv.ChangeInfo = x => x with { Step = 10, Current = 20, PresetMask = 0 };
            if (failure == "original") nv.ChangeInfo = x => x with { Step = 10, PresetMask = 2 };
            path = Path.Combine(root, failure + ".json");
            using var control = new FrameNvidiaGpuShare(path, nv, nv.Acquire);
            Check(!control.Sync(true, failure == "increment" ? 37 : 10, () => true) && nv.Writes.Count == 0 && !File.Exists(path) && nv.Leases == 0,
                "preflight refusal before any global write: " + failure);
        }
        nv = new(); path = Path.Combine(root, "interrupted.json");
        using (var control = new FrameNvidiaGpuShare(path, nv, nv.Acquire)) Check(control.Sync(true, 10, () => true), "interrupted session fixture saved");
        var recovered = new Nvidia { Current = 10 };
        using (var control = new FrameNvidiaGpuShare(path, recovered, recovered.Acquire))
            Check(control.Snapshot.RestorePending && control.Sync(false, 10, () => false) && recovered.Current == 17,
                "new controller restores after process death from durable original");
        nv = new(); path = Path.Combine(root, "changed-hardware.json");
        using (var control = new FrameNvidiaGpuShare(path, nv, nv.Acquire))
        {
            Check(control.Sync(true, 10, () => true), "hardware-change fixture held"); nv.Key = new string('B', 64);
            Check(!control.Sync(false, 10, () => false) && control.Snapshot.RestorePending && nv.Writes.Count == 1,
                "hardware change keeps recovery evidence and does not write another GPU");
        }
        nv = new(); path = Path.Combine(root, "restore-denied.json");
        using (var control = new FrameNvidiaGpuShare(path, nv, nv.Acquire))
        {
            Check(control.Sync(true, 10, () => true), "restore-denial fixture held"); nv.FailWrite = true;
            Check(!control.Sync(false, 10, () => false) && control.Snapshot.RestorePending && File.Exists(path), "restore failure keeps ownership and evidence");
            nv.FailWrite = false;
            Check(control.Sync(false, 10, () => false) && nv.Current == 17, "explicit later restore can finish without permanent checking state");
        }
        nv = new() { WrongReadback = true }; path = Path.Combine(root, "unconfirmed-write.json");
        using (var control = new FrameNvidiaGpuShare(path, nv, nv.Acquire))
        {
            Check(!control.Sync(true, 10, () => true) && control.Snapshot.RestorePending, "wrong NVIDIA readback retains unconfirmed responsibility");
            Check(!control.Sync(false, 10, () => false) && File.Exists(path) && nv.Leases == 1,
                "unconfirmed native change cannot be discarded as an external takeover");
            nv.WrongReadback = false; nv.Current = 17;
            Check(control.Sync(false, 10, () => false) && nv.Leases == 0,
                "manual restoration to exact original can finish unconfirmed recovery");
        }

        var now = DateTimeOffset.UtcNow;
        var parent = new ProcmanStatusSnapshot { Module = BackendModules.Procman, UpdatedUtc = now, RulesEngineRunning = true,
            FramePriority = new() { UpdatedUtc = now, GpuProtocolVersion = 1, Maintaining = true, GpuAppliedCount = 1, InstanceCount = 1 },
            FrameNvidiaGpuShare = new() { ProtocolVersion = 1, UpdatedUtc = now, Maintaining = true, Message = "fixture current ratio" } };
        Check(FrameGpuPresentation.Process(true, true, parent, now, "").Contains("已回读"), "fresh GPU readback displayed");
        Check(!FrameGpuPresentation.Process(true, true, parent, now.AddMinutes(1), "").Contains("已回读"), "stale GPU parent cannot claim active");
        Check(FrameGpuPresentation.Nvidia(true, true, parent, now, "") == "fixture current ratio", "fresh NVIDIA status displayed");
        Check(!FrameGpuPresentation.Nvidia(true, true, parent, now.AddMinutes(1), "").Contains("fixture current ratio"), "stale NVIDIA parent cannot claim active");
        await Integration(root);
        Console.WriteLine($"Frame GPU checks passed: {_checks}; fake native APIs, owned files and real lifecycle only. No host GPU/driver/timer or ProgramData writes. Fixture: {root}");
    }
    private static async Task Integration(string root)
    {
        var targetPath = @"C:\Other\custom-gpu-game.exe";
        var identity = new ProcmanProcessIdentity(45678, DateTime.UtcNow, targetPath);
        AppSettingsStore.Update(s => { s.FrameModeActive = true; s.FrameGamePriorityEnabled = false; s.FrameGameGpuPriorityEnabled = false;
            s.FrameNvidiaGpuShareEnabled = true; s.FrameNvidiaGpuBackgroundPercent = 10; s.FrameAutomaticMemoryEnabled = false;
            s.CustomGameModeEnabled = true; s.CustomGameExecutablePath = targetPath; });
        var priorities = new Priority(); var nv = new Nvidia(); var running = true;
        using var engine = new ProcmanEngine(Path.Combine(root, "engine-changes.json"), Path.Combine(root, "engine-rules.json"), priorities)
        {
            BackgroundLoopDisabledForTests = false, FramePriorityTargetsForTests = () => running ? [identity] : [],
            FrameTargetForTests = () => new(true, targetPath, "custom", "custom-gpu-game.exe", 1), FrameCanonicalPathForTests = Path.GetFullPath,
            GlobalPrecisionForTests = () => new() { Supported = true, Readable = true, Message = "fixture no clock request" },
        };
        engine.NvidiaGpuApiForTests(nv, Path.Combine(root, "engine-nvidia.json"), nv.Acquire);
        var start = engine.SyncFrameBackground();
        Check(start.Success && engine.Snapshot().RulesEngineRunning && nv.Writes.SequenceEqual([10]),
            "NVIDIA-only frame setting starts real supervision without CPU priorities or memory reclaim: " + start.Message + " | " + JsonSerializer.Serialize(engine.Snapshot().FrameNvidiaGpuShare));
        Check(engine.FramePriorityOnceForTests() && nv.Writes.Count == 1 && priorities.Writes.Count == 0,
            "process priority disabled path does not restore/reapply NVIDIA each tick");
        running = false;
        Check(engine.FramePriorityOnceForTests() && nv.Current == 17 && !engine.Snapshot().FrameNvidiaGpuShare.RestorePending,
            "selected custom game exit restores global ratio");
        running = true;
        AppSettingsStore.Update(s => { s.FrameGameGpuPriorityEnabled = true; s.FrameGameGpuPriority = FrameGpuPriority.High; });
        Check(engine.SyncFrameBackground().Success && priorities.Values["gpuPriority"] == 4 && nv.Current == 10,
            "both independent GPU settings apply to selected custom mode");
        var result = await engine.StopRulesEngineAsync();
        Check(result.Success && priorities.Values["gpuPriority"] == 2 && nv.Current == 17 && engine.Snapshot().HeldChangeCount == 0,
            "stop restores both original GPU settings and clears confirmed obligations");
        AppSettingsStore.Update(s => s.FrameGamePriorityEnabled = true);
        Check(engine.SyncFrameBackground().Success, "combined cleanup-failure fixture active");
        priorities.FailCpu = true;
        Check(!(await engine.StopRulesEngineAsync()).Success && nv.Current == 17 && !engine.Snapshot().FrameNvidiaGpuShare.RestorePending,
            "CPU restore failure does not skip independent NVIDIA restoration");
        priorities.FailCpu = false;
        Check((await engine.StopRulesEngineAsync()).Success, "remaining process restore can be retried after NVIDIA cleanup");
        AppSettingsStore.Update(s => { s.FrameModeActive = false; s.FrameGameGpuPriorityEnabled = false; s.FrameNvidiaGpuShareEnabled = false; });
    }
}
