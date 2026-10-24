using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;

internal static class InspectionInvalidation
{
    internal static void AfterCommand(string command, object args)
    {
        if (!BackendModules.IsAllowed(BackendModules.System, command) || !BackendModules.IsMutating(BackendModules.System, command)) return;
        if (command == BackendCommands.SystemBxRestoreAll) { OptimizationScan.Invalidate(); return; }
        var data = System.Text.Json.JsonSerializer.SerializeToElement(args, BackendJson.SerializerOptions);
        if (command is BackendCommands.SystemBxApply or BackendCommands.SystemBxGearApply)
        {
            if (data.TryGetProperty("sectionId", out var section) && data.TryGetProperty("name", out var name))
                OptimizationScan.ForgetItem(section.GetString() ?? "", name.GetString() ?? "");
        }
        if (command == BackendCommands.SystemBxServiceGroupApply) OptimizationScan.ForgetGroups();
        if (command is BackendCommands.SystemTweaksApply or BackendCommands.SystemTweaksRestore)
        {
            // These aliases share native state with the basic/UP entries.
            OptimizationScan.ForgetItem("basic", "setsaccel");
            OptimizationScan.ForgetItem("basic", "HAGS");
            OptimizationScan.ForgetTweaks();
        }
        SystemInspectionCache.Invalidate();
    }
}
