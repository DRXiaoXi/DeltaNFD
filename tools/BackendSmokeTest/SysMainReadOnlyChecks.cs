using System.Diagnostics;
using System.Text.RegularExpressions;
using DeltaNFD.Inspection;
using Microsoft.Win32;

internal static class SysMainReadOnlyChecks
{
    internal static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        var evidence = SecurityFeatureInspection.ReadService("SysMain");
        Check(evidence.Error != "服务名不在固定只读范围。", "actual fixed-name reader admits SysMain");
        using var process = new Process { StartInfo = new(Path.Combine(Environment.SystemDirectory, "sc.exe"), "queryex SysMain")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var text = await stdout; await stderr;
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SysMain");
        Check(evidence.Start == key?.GetValue("Start") as int?, "actual reader matches separate registry read");
        if (process.ExitCode == 1060)
            Check(evidence.Exists == false && evidence.RuntimeState is null, "missing service remains missing");
        else
        {
            Check(process.ExitCode == 0, "independent sc query succeeds");
            var match = Regex.Match(text, @"STATE\s*:\s*(\d+)");
            Check(match.Success, "independent query includes numeric service state");
            Check(evidence.Exists == true && evidence.RuntimeState == uint.Parse(match.Groups[1].Value), "actual reader agrees with independent service query");
        }
        foreach (var denied in new[] { "Spooler", "SysMain\\other", "SysMain\"", "", "__arbitrary__" })
        {
            var rejected = SecurityFeatureInspection.ReadService(denied);
            Check(rejected.Exists is null && rejected.Start is null && rejected.RuntimeState is null
                && rejected.Error == "服务名不在固定只读范围。", "unrelated/malformed service remains denied");
        }
        Check(!SecurityFeatureInspection.Handles("SysMain"), "SysMain read permission does not make it a security-disable feature");
        Console.WriteLine($"SysMain native read-only: {checks} checks passed; Exists={evidence.Exists}, Start={evidence.Start}, State={evidence.RuntimeState}; host Windows only, no writes.");
    }
}
