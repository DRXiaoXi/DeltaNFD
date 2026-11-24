using System.Security.Principal;
using DeltaNFD.Backends;
using DeltaNFD.Services;

internal static class LocalSystemChecks
{
    internal static void Run(string isolated, string displayImage, Action<bool, string> check)
    {
        var program = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            @"NVIDIA Corporation\NvContainer");
        var exe = Path.Combine(program, "nvcontainer.exe");
        var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"NVIDIA Corporation\NVIDIA App\NvContainer\NvContainerLocalSystem.log");
        var plugin = Path.Combine(program, @"plugins\LocalSystem");
        var image = $"\"{exe}\" -s NvContainerLocalSystem -a -f \"{log}\" -l 3 -d \"{plugin}\" -r -p 30000  -ert";
        check(NvLocalSystemContainerPolicy.ParseImage(image) == exe, "621.24 shared service image recognized");
        check(NvLocalSystemContainerPolicy.ParseImage(image.Replace("  -ert", "")) == exe, "optional -ert flag");
        check(NvDisplayContainerService.ParseImage(image) is null && NvLocalSystemContainerPolicy.ParseImage(displayImage) is null
            && NvTelemetryContainerPolicy.ParseImage(image) is null, "strictly separate from display and dedicated telemetry");
        foreach (var invalid in new[]
        {
            image.Replace("NvContainerLocalSystem -a", "NvTelemetryContainer -a"),
            image.Replace(exe, @"C:\Users\Public\nvcontainer.exe"),
            image.Replace(log, log + ".bak"),
            image.Replace(plugin, plugin + "-foreign"),
            image + " -s OtherService",
        })
            check(NvLocalSystemContainerPolicy.ParseImage(invalid) is null, "reject unsupported shared-container command");

        using var user = WindowsIdentity.GetCurrent();
        var folder = Path.Combine(isolated, "shared-container");
        var backupStore = new NvContainerFileBackup(folder, "machine", () => null, user.User!.Value, NvContainerTarget.LocalSystem);
        var original = new NvContainerState(image, NvContainerMode.Automatic, false, NvContainerRun.Running, 71, "LocalSystem");
        var svc = new Fake(original);
        OperationResult Apply(bool restore, string? fingerprint, bool gameExited = true) =>
            NvDisplayContainerService.Commit(svc, backupStore, new(restore, fingerprint ?? ""), _ => new MemoryStream(),
                () => gameExited, target: NvContainerTarget.LocalSystem);
        var ready = NvDisplayContainerService.Describe(original, null, NvContainerTarget.LocalSystem);
        check(ready is { Fingerprint: not null, Restore: false }, "shared service ready to disable");
        check(!Apply(false, ready.Fingerprint, gameExited: false).Success && svc.Ops.Count == 0 && backupStore.Read() is null,
            "running game must prevent backup and service writes");
        check(Apply(false, ready.Fingerprint).Success && svc.State is { Mode: NvContainerMode.Disabled, Run: NvContainerRun.Stopped }
            && svc.Ops.SequenceEqual(["Disabled", "stop"]), "disable/stop only the selected SCM service");
        var saved = backupStore.Read();
        check(saved is { Account: "LocalSystem", WasRunning: true, Mode: NvContainerMode.Automatic }
            && File.Exists(Path.Combine(folder, "local-system-container.json")), "shared container has its own original-state file");
        check(!File.Exists(Path.Combine(folder, "telemetry-container.json")) && !File.Exists(Path.Combine(folder, "display-container.json")),
            "no mutation to legacy telemetry/display backup paths");
        var stopped = NvDisplayContainerService.Describe(svc.State, saved, NvContainerTarget.LocalSystem);
        var review = NvDisplayContainerService.Snapshot(BackendModules.System, stopped, true, true, NvContainerTarget.LocalSystem);
        check(review is { Complete: true, Items: [{ Id: NvLocalSystemContainer.RecoveryScope, HasBackup: true }],
              RecoveryScopes: [{ Id: NvLocalSystemContainer.RecoveryScope, Available: true }] },
            "recovery advertises only the shared service’s scope");
        check(!Apply(true, ready.Fingerprint).Success && svc.Ops.Count == 2, "stale pre-disable fingerprint cannot restore");
        check(Apply(true, stopped.Fingerprint).Success && svc.State.Mode == original.Mode && svc.State.Run == original.Run
            && backupStore.Read() is null, "restore starts the original running service and archives the backup");
        check(BackendModules.RequiresWriteIntent(BackendModules.System, BackendCommands.SystemNvLocalSystemChange)
            && BackendModules.RetainsStaticBackup(BackendModules.System, BackendCommands.SystemNvLocalSystemChange)
            && !BackendModules.IsAllowed(BackendModules.Gpu, BackendCommands.SystemNvLocalSystemChange)
            && BackendModules.IsReadOnly(BackendModules.System, BackendCommands.SystemNvLocalSystemReconcile)
            && !BackendModules.RequiresWriteIntent(BackendModules.System, BackendCommands.SystemNvLocalSystemRecover),
            "shared service uses separate system IPC with intent and a recover path");
    }
}
