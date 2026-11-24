using System.Security.Principal;
using DeltaNFD.Backends;
using DeltaNFD.Services;
using static DeltaNFD.Services.NvContainerMode;
using static DeltaNFD.Services.NvContainerRun;

internal static class TelemetryChecks
{
    internal static void Run(string isolated, string displayImage, Action<bool, string> check)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var directory = Path.Combine(programFiles, @"NVIDIA Corporation\NvTelemetry");
        var exe = Path.Combine(directory, "NvTelemetryContainer.exe");
        var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"NVIDIA\NvTelemetryContainer.log");
        var image = $"""
            "{exe}" -s NvTelemetryContainer -f "{log}" -l 3 -d "{Path.Combine(directory, "plugins")}" -r
            """;
        check(NvTelemetryContainerPolicy.ParseImage(image) == exe, "dedicated signed-image candidate matches known legacy layout");
        check(NvTelemetryContainerPolicy.ParseImage(image.Replace("plugins", "plugin").Replace(" -r", "")) == exe,
            "known singular plugin layout is accepted");
        check(NvTelemetryContainerPolicy.ParseImage(displayImage) is null && NvDisplayContainerService.ParseImage(image) is null,
            "display and telemetry scopes cannot exchange service images");
        foreach (var unsupported in new[]
        {
            image.Replace("NvTelemetryContainer.exe", "nvcontainer.exe"),
            image.Replace("-s NvTelemetryContainer", "-s NvContainerLocalSystem"),
            image.Replace(log, @"C:\Users\Public\test.log"),
            image.Replace(Path.Combine(directory, "plugins"), @"C:\Users\Public"),
            image.Replace(programFiles, @"C:\Users\Public"),
            image + " -extra",
        }) check(NvTelemetryContainerPolicy.ParseImage(unsupported) is null, "foreign service arguments or images are rejected");
        check(NvTelemetryContainerPolicy.NormalizeAccount(@"NT AUTHORITY\NetworkService") == "NetworkService"
            && NvTelemetryContainerPolicy.NormalizeAccount("LocalSystem") == "LocalSystem"
            && NvTelemetryContainerPolicy.NormalizeAccount(@"EXAMPLE\NetworkService") is null, "only supported SCM accounts can be used");
        check(BackendModules.IsReadOnly(BackendModules.System, BackendCommands.SystemNvTelemetryStatus)
            && BackendModules.IsReadOnly(BackendModules.System, BackendCommands.SystemNvTelemetryReconcile)
            && BackendModules.RequiresWriteIntent(BackendModules.System, BackendCommands.SystemNvTelemetryChange)
            && BackendModules.IsMutating(BackendModules.System, BackendCommands.SystemNvTelemetryRecover)
            && !BackendModules.RequiresWriteIntent(BackendModules.System, BackendCommands.SystemNvTelemetryRecover)
            && !BackendModules.IsAllowed(BackendModules.Gpu, BackendCommands.SystemNvTelemetryChange),
            "telemetry commands only run through the system worker with the correct intent boundary");

        using var user = WindowsIdentity.GetCurrent();
        var path = Path.Combine(isolated, "telemetry");
        var store = new NvContainerFileBackup(path, "machine", () => null, user.User!.Value, target: NvContainerTarget.LegacyTelemetry);
        var displayStore = new NvContainerFileBackup(path, "machine", () => null, user.User!.Value);
        var original = new NvContainerState(image, Automatic, true, Running, 71, "NetworkService");
        var fake = new Fake(original);
        OperationResult Commit(NvDisplayContainerChange args, Action<NvContainerState>? restored = null) =>
            NvDisplayContainerService.Commit(fake, store, args, _ => new MemoryStream(), () => true, restored, target: NvContainerTarget.LegacyTelemetry);

        var active = NvDisplayContainerService.Describe(original, null, target: NvContainerTarget.LegacyTelemetry);
        fake.Fail = "stop";
        check(Commit(new(false, active.Fingerprint!)).OutcomeUnknown && fake.State.Mode == Disabled && fake.State.Run == Running,
            "incomplete stop keeps the durable original for independent recovery");
        var backup = store.Read();
        check(backup == new NvContainerBackup(image, Automatic, true, true, "NetworkService")
            && File.Exists(Path.Combine(path, "telemetry-container.json")) && displayStore.Read() is null,
            "backup is scoped to telemetry and records startup, runtime, and account");

        var preview = NvDisplayContainerService.Describe(fake.State, backup, target: NvContainerTarget.LegacyTelemetry);
        var snapshot = NvDisplayContainerService.Snapshot(BackendModules.System, preview, true, true, target: NvContainerTarget.LegacyTelemetry);
        check(snapshot.Items is [{ Id: NvTelemetryContainer.RecoveryScope, HasBackup: true }]
            && snapshot.RecoveryScopes is [{ Id: NvTelemetryContainer.RecoveryScope, Available: true }],
            "recovery snapshot exposes only the telemetry scope");
        fake.State = fake.State with { Account = "LocalSystem" };
        fake.Ops.Clear();
        check(!Commit(new(true, preview.Fingerprint!)).Success && fake.Ops.Count == 0 && store.Read() == backup,
            "changed service account prevents writing or clearing the backup");

        fake.State = fake.State with { Account = "NetworkService", Run = Stopped, Pid = 0 };
        fake.Fail = null;
        var refreshed = NvDisplayContainerService.Describe(fake.State, backup, target: NvContainerTarget.LegacyTelemetry);
        check(refreshed.Fingerprint != preview.Fingerprint && !Commit(new(true, preview.Fingerprint!)).Success && fake.Ops.Count == 0,
            "a stale status confirmation cannot alter the service");
        NvContainerState? verified = null;
        check(Commit(new(true, refreshed.Fingerprint!), state => verified = state).Success && verified == original
            && store.Read() is null && fake.Ops.SequenceEqual(["Automatic", "start"]),
            "restore verifies original configuration and runtime before finalizing the file");
        fake.Ops.Clear();
        check(Commit(new(false, active.Fingerprint!)).Success && fake.State.Mode == Disabled && fake.State.Run == Stopped
            && fake.Ops.SequenceEqual(["Disabled", "stop"]), "normal disable changes only the telemetry SCM handle");
    }
}
