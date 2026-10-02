using DeltaNFD.Services;

internal static class OfflineTaskOwnershipChecks
{
    public static void Run()
    {
        var current = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Delta NFD", "DeltaNFD.exe"));
        var legacy = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DeltaOptimizer", "DeltaOptimizer.exe"));
        var currentXml = Xml(current, enabled: true);
        Require(OfflineTaskOwnership.IsOwnedTaskXml(currentXml, current, [legacy], out var path, out var enabled, out _),
            "exact current executable action should be owned");
        Require(path.Equals(current, StringComparison.OrdinalIgnoreCase) && enabled, "enabled state and command path should parse");

        Require(OfflineTaskOwnership.IsOwnedTaskXml(Xml(legacy, enabled: false), current, [legacy], out _, out enabled, out _),
            "known legacy executable should be owned");
        Require(!enabled, "disabled task state should be preserved");
        Require(!OfflineTaskOwnership.IsOwnedTaskXml(Xml(Path.Combine(Path.GetTempPath(), "other.exe"), true), current, [legacy], out _, out _, out _),
            "foreign executable action should not be owned");
        Require(!OfflineTaskOwnership.IsOwnedTaskXml(Xml(current, true, arguments: "--custom"), current, [legacy], out _, out _, out _),
            "tasks with extra arguments should not be deleted as ours");
        Require(!OfflineTaskOwnership.IsOwnedTaskXml(Xml(current, true, secondAction: true), current, [legacy], out _, out _, out _),
            "multiple task actions should not be owned");

        Console.WriteLine("脱机计划任务归属、动作数量、参数与启用状态检查通过。");
    }

    private static string Xml(string command, bool enabled, string arguments = "", bool secondAction = false)
    {
        var safeCommand = System.Security.SecurityElement.Escape(command);
        var safeArguments = System.Security.SecurityElement.Escape(arguments);
        var extra = secondAction ? "<Exec><Command>C:\\Windows\\System32\\calc.exe</Command></Exec>" : "";
        return $"<Task><Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers><Actions><Exec><Command>{safeCommand}</Command><Arguments>{safeArguments}</Arguments></Exec>{extra}</Actions><Settings><Enabled>{enabled.ToString().ToLowerInvariant()}</Enabled></Settings></Task>";
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
