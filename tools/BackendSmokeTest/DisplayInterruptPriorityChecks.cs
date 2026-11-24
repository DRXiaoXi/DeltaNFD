using DeltaNFD.Backends;
using DeltaNFD.Inspection;
using DeltaNFD.Services;

namespace BackendSmokeTest;

internal static class DisplayInterruptPriorityChecks
{
    private sealed class Environment(DisplayInterruptAdapter[] adapters) : IDisplayInterruptEnvironment
    {
        public DisplayInterruptAdapter[] Adapters { get; } = adapters;
        public string? Block { get; set; }
        public bool FailBeforeWrite { get; set; }
        public bool FailAfterWrite { get; set; }
        public int Writes { get; private set; }
        public Action? BeforeWrite { get; set; }
        public string? Blocker(bool restoring) => Block;
        public IReadOnlyList<DisplayInterruptAdapter> Read() => Adapters.ToArray();
        public void Write(DisplayInterruptAdapter expected, uint? priority, bool restoring, Action beforeMutation)
        {
            BeforeWrite?.Invoke();
            if (FailBeforeWrite) throw new IOException("simulated pre-write failure");
            beforeMutation();
            var index = Array.IndexOf(Adapters, expected);
            Adapters[index] = expected with { Priority = priority };
            Writes++;
            if (FailAfterWrite) throw new IOException("simulated interruption after write");
        }
    }

    public static void Run(string root)
    {
        var fixtures = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var intel = Adapter(@"PCI\VEN_8086&DEV_1234\a&b", 0);
        var nvidia = Adapter(@"PCI\VEN_10DE&DEV_2860\c&d", null);
        var environment = new Environment([intel, nvidia]);
        var store = new DisplayInterruptPriorityStore(fixtures, "fixture-machine", protect: false);
        var alpha = new DisplayInterruptPriorityEngine(store, environment, featureEnabled: true);
        var beta = new DisplayInterruptPriorityEngine(store, environment, featureEnabled: false);
        var count = 0;

        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            count++;
        }
        DisplayInterruptPriorityArgs Args(DisplayInterruptPriorityEngine engine, DisplayInterruptAdapter target, bool restore = false)
        {
            var row = engine.List().Items.Single(i => i.Adapter.InstanceId == target.InstanceId);
            return new(target.InstanceId, row.Fingerprint, restore, true);
        }

        Check(BackendModules.IsReadOnly(BackendModules.System, BackendCommands.SystemDisplayInterruptList), "List is read-only");
        Check(BackendModules.RequiresWriteIntent(BackendModules.System, BackendCommands.SystemDisplayInterruptChange), "Change requires a write intent");
        Check(BackendModules.RetainsStaticBackup(BackendModules.System, BackendCommands.SystemDisplayInterruptChange), "Verified backup persists");
        Check(!beta.Change(Args(beta, nvidia), default).Success && !Directory.Exists(fixtures), "Beta refuses apply before backup");
        Check(!alpha.Change(Args(alpha, nvidia) with { Confirmed = false }, default).Success, "Unconfirmed request refused");
        Check(!alpha.Change(Args(alpha, nvidia) with { InstanceId = @"PCI\..\other" }, default).Success, "Arbitrary device path refused");
        environment.Block = "game running";
        Check(!alpha.Change(Args(alpha, nvidia), default).Success && environment.Writes == 0, "Game gate blocks write");
        environment.Block = null;

        var nvidiaApply = Args(alpha, nvidia);
        environment.BeforeWrite = () => Check(store.ReadAll().Single().Original is null, "Absent original recorded before write");
        var applied = alpha.Change(nvidiaApply, default);
        environment.BeforeWrite = null;
        Check(applied is { Success: true, RequiresReboot: true }, "High write requires reboot");
        Check(environment.Adapters[0] == intel && environment.Adapters[1].Priority == 3 && environment.Writes == 1,
            "Only the confirmed adapter changes");
        Check(!alpha.Change(nvidiaApply, default).Success && environment.Writes == 1, "Old confirmation cannot be reused");
        var betaRestored = beta.Change(Args(beta, nvidia, restore: true), default);
        Check(betaRestored is { Success: true, RequiresReboot: true } && environment.Adapters[1].Priority is null,
            "Beta can restore a missing original");
        Check(store.ReadAll().Count == 0 && Directory.EnumerateFiles(Path.Combine(fixtures, "archive")).Count() == 1,
            "Restored original remains archived");

        environment.FailAfterWrite = true;
        Check(alpha.Change(Args(alpha, intel), default).OutcomeUnknown && store.ReadAll().Single().Original == 0,
            "Interrupted write retains an explicit zero backup");
        environment.FailAfterWrite = false;
        environment.Adapters[0] = intel with { Priority = 2 };
        Check(!alpha.Change(Args(alpha, intel, restore: true), default).Success && environment.Adapters[0].Priority == 2,
            "External value is not overwritten by restore");
        environment.Adapters[0] = intel with { Priority = 3, Identity = DisplayInterruptPriorityPolicy.Hash("other-driver") };
        Check(!alpha.Change(Args(alpha, intel, restore: true), default).Success && store.ReadAll().Count == 1,
            "Changed driver identity keeps the backup");
        environment.Adapters[0] = intel with { Priority = 3 };
        Check(alpha.RestoreAll(default).Success && environment.Adapters[0].Priority == 0 && store.ReadAll().Count == 0,
            "Recovery restores an explicit zero");

        environment.FailBeforeWrite = true;
        var failed = alpha.Change(Args(alpha, nvidia), default);
        Check(!failed.Success && !failed.OutcomeUnknown && environment.Adapters[1].Priority is null, "Pre-write failure is known");
        environment.FailBeforeWrite = false;
        var writes = environment.Writes;
        Check(alpha.Change(Args(alpha, nvidia, restore: true), default).Success && environment.Writes == writes,
            "Unchanged original after a pre-write failure can be verified without writing");

        var present = DisplayInterruptInspection.Read();
        var anyNvidia = present.Any(a => a.InstanceId.StartsWith(@"PCI\VEN_10DE&", StringComparison.OrdinalIgnoreCase));
        Check(DisplayInterruptInspection.HasPresentNvidia() == anyNvidia, "Read-only navigation and inventory agree on present NVIDIA adapters");
        Console.WriteLine($"Display interrupt priority: {count} checks passed. Local read-only inventory: {present.Count} PCI display adapters.");
        foreach (var adapter in present) Console.WriteLine($"{adapter.Name}: readable={adapter.Readable}, priority={adapter.Priority}, detail={adapter.Detail}");
    }

    private static DisplayInterruptAdapter Adapter(string id, uint? priority) =>
        new(id, "Fixture", "1", "driver", DisplayInterruptPriorityPolicy.Hash(id), true, true, priority);
}
