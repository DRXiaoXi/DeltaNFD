using DeltaNFD.Services;

internal static class OfflineTaskOwnershipChecks
{
    public static async Task RunAsync()
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
        Require(OfflineTaskOwnership.IsOwnedTaskXml(Xml(current, true, StartupPresentation.LogonArgument), current, [legacy], out _, out _, out _),
            "exact logon marker remains owned for disable, uninstall and offline recovery");
        foreach (var argument in new[] { "--autostart --custom", "--autostart=1", "--AUTOSTART", "--prepare-deployment", "--autostart & calc.exe" })
            Require(!OfflineTaskOwnership.IsOwnedTaskXml(Xml(current, true, argument), current, [legacy], out _, out _, out _),
                "unrecognized or appended arguments must stay unowned");
        Require(!OfflineTaskOwnership.IsOwnedTaskXml(Xml(current, true).Replace("</Arguments>", "</Arguments><Arguments>--autostart</Arguments>"), current, [legacy], out _, out _, out _),
            "duplicate Arguments cannot smuggle an action past inspection");
        await CheckMarkedTaskLifecycleAsync();

        Console.WriteLine("脱机计划任务归属、动作数量、参数与启用状态检查通过。");
    }

    private static async Task CheckMarkedTaskLifecycleAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-silent-task-" + Guid.NewGuid().ToString("N"), "中文 with spaces");
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "DeltaNFD.exe");
        File.WriteAllText(executable, "inert task fixture; never launched");
        string? xml = null;
        var calls = new List<string[]>();
        var refuseChange = false;
        var omitMarker = false;
        OfflineTaskOwnership.ProcessRunnerForTests = (args, _) =>
        {
            calls.Add(args);
            if (args[0] == "/Query") return Task.FromResult(xml is null ? (1, "", "The system cannot find the file specified") : (0, xml, ""));
            if (args[0] == "/Delete") { xml = null; return Task.FromResult((0, "", "")); }
            if (args[0] == "/Change" && refuseChange) return Task.FromResult((5, "", "synthetic access denied"));
            var trIndex = Array.IndexOf(args, "/TR");
            if (trIndex >= 0)
            {
                Require(args[trIndex + 1] == $"\"{executable}\" --autostart", "quoted Unicode executable and single marker");
                if (args[0] == "/Create") xml = Xml(executable, true, omitMarker ? "" : StartupPresentation.LogonArgument);
                else if (!omitMarker)
                {
                    var doc = System.Xml.Linq.XDocument.Parse(xml!);
                    doc.Descendants("Arguments").Single().Value = StartupPresentation.LogonArgument;
                    xml = doc.ToString();
                }
            }
            else if (args[0] == "/Change")
            {
                var doc = System.Xml.Linq.XDocument.Parse(xml!);
                doc.Descendants("Settings").Single().Element("Enabled")!.Value = args.Contains("/ENABLE") ? "true" : "false";
                xml = doc.ToString();
            }
            return Task.FromResult((0, "", ""));
        };
        try
        {
            Require((await OfflineTaskOwnership.EnsureOwnedTaskAsync("fixture", executable)).Success, "fresh logon task is marked and verified");
            var writes = calls.Count(c => c[0] != "/Query");
            Require((await OfflineTaskOwnership.EnsureOwnedTaskAsync("fixture", executable)).Success
                && calls.Count(c => c[0] != "/Query") == writes, "matching marked task is idempotent");
            xml = Xml(executable, false).Replace("<Settings>", "<Settings><Hidden>true</Hidden>");
            Require((await OfflineTaskOwnership.EnsureOwnedTaskAsync("fixture", executable, false)).Success
                && xml!.Contains("<Hidden>true</Hidden>") && xml.Contains("<Enabled>false</Enabled>"), "old task migration preserves settings and disabled state");
            Require((await OfflineTaskOwnership.RemoveOwnedTaskAsync("fixture", executable)).Success && xml is null,
                "marked task can be removed without stranding autostart");
            xml = Xml(executable, true, "--autostart --unknown"); calls.Clear();
            Require(!(await OfflineTaskOwnership.EnsureOwnedTaskAsync("fixture", executable)).Success
                && !(await OfflineTaskOwnership.RemoveOwnedTaskAsync("fixture", executable)).Success
                && calls.All(c => c[0] == "/Query"), "unknown same-name task is never changed/deleted");
            xml = Xml(executable, true); refuseChange = true;
            Require(!(await OfflineTaskOwnership.EnsureOwnedTaskAsync("fixture", executable)).Success && !xml!.Contains("--autostart"), "migration failure is reported");
            refuseChange = false; omitMarker = true;
            Require(!(await OfflineTaskOwnership.EnsureOwnedTaskAsync("fixture", executable)).Success, "success exit without verified action is rejected");
            xml = null;
            Require(!(await OfflineTaskOwnership.EnsureOwnedTaskAsync("fixture", executable)).Success, "fresh task missing marker after creation is rejected");
        }
        finally
        {
            OfflineTaskOwnership.ProcessRunnerForTests = null;
            File.Delete(executable);
            Directory.Delete(root);
            Directory.Delete(Path.GetDirectoryName(root)!);
        }
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
