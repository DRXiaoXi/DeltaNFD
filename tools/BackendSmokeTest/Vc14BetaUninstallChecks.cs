using System.Text.RegularExpressions;
using DeltaNFD.Services;

internal static class Vc14BetaUninstallChecks
{
    internal static void Run()
    {
        var count = 0;
        void Check(bool value, string detail) { if (!value) throw new Exception(detail); count++; }
        void Reject(Action action, string detail)
        {
            var rejected = false;
            try { action(); } catch (IOException) { rejected = true; }
            Check(rejected, detail);
        }
        foreach (var arch in new[] { "x64", "x86" })
        {
            Check(Vc14BetaUninstall.InScope($"Microsoft Visual C++ 2022 {arch} Minimum Runtime - 14.42.34433", "14.42.34433.0", arch), "baseline excluded");
            Check(Vc14BetaUninstall.InScope($"Microsoft Visual C++ v14 Redistributable ({arch})", "14.51.36247", arch), "v14 excluded");
            foreach (var (year, version) in new[] { ("2005", "8.0.61001"), ("2008", "9.0.30729"), ("2010", "10.0.40219"), ("2012", "11.0.61030"), ("2013", "12.0.40664") })
                Check(!Vc14BetaUninstall.InScope($"Microsoft Visual C++ {year} Redistributable ({arch})", version, arch), "older branch entered uninstall");
        }
        foreach (var name in new[] { "Microsoft Visual C++ SDK", "Microsoft Visual C++ Debug Runtime", "Visual C++ Library CRT Desktop Appx Package", "Microsoft DirectX" })
            Check(!Vc14BetaUninstall.InScope(name, "14.42.34433", "x64"), "non-release branch entered uninstall: " + name);
        Check(!Vc14BetaUninstall.InScope("Microsoft Visual C++ 2005 Redistributable (x64)", "14.42.34433", "x64"), "contradictory old-year label entered uninstall");
        Check(!Vc14BetaUninstall.InScope("Microsoft Visual C++ v14 Redistributable (x64)", "12.0.40664", "x64"), "v14 label overrode real non-14 version");
        Check(!Vc14BetaUninstall.InScope("Microsoft Visual C++ v14 Redistributable (ARM64)", "14.51.0", "arm64"), "ARM64 entered uninstall");
        Check(Vc14BetaUninstall.RelevantForResidue("Microsoft Visual C++ 2026 Redistributable (x64)", ""), "versionless historical 2026 residue missed");
        Check(!Vc14BetaUninstall.RelevantForResidue("Microsoft Visual C++ v14 Redistributable (x64)", "12.0.40664"), "non-14 residue included");
        Check(Vc14BetaUninstall.PreferredCommand("quiet-command", "interactive-command") == "quiet-command", "quiet command preference changed");
        Check(Vc14BetaUninstall.PreferredCommand(" ", "interactive-command") == "interactive-command", "empty quiet command prevented fallback");
        var command = "\"C:\\Program Files\\Vendor\\Uninstall.exe\" /quiet /norestart";
        var shell = Vc14BetaUninstall.Shell(command);
        Check(Path.IsPathFullyQualified(shell.FileName) && Path.GetFileName(shell.FileName) == "cmd.exe"
            && shell.Arguments == "/d /s /c \"" + command + "\"" && !shell.UseShellExecute, "Beta shell command or quoting changed");
        Reject(() => Vc14BetaUninstall.Shell(command + " & remove-other-branch.exe"), "compound command allowed");
        Reject(() => Vc14BetaUninstall.Shell(command + "\nremove-other-branch.exe"), "multiline command allowed");
        Check(Vc14BetaUninstall.IsUnsupportedScript("cmd.exe /c Installer.cmd /quiet"), "AIO shell accepted");
        Check(Vc14BetaUninstall.IsUnsupportedScript("rundll32.exe advpack.dll,LaunchINFSection other.inf,Uninstall"), "unknown INF accepted");
        var id = "{11111111-1111-1111-1111-111111111111}";
        var entry = new Vc14Entry(id, "Microsoft Visual C++ 2022 X64 Minimum Runtime - 14.42.34433", "14.42.34433", "x64", "Minimum", "{22222222-2222-2222-2222-222222222222}");
        Check(Vc14BetaUninstall.MsiTarget("MsiExec.exe /I" + id, code => code.Equals(id, StringComparison.OrdinalIgnoreCase) ? entry : throw new Exception("wrong product code")) == entry,
            "registered MSI command target not resolved");
        Reject(() => Vc14BetaUninstall.MsiTarget("MsiExec.exe /X" + id, _ => entry with { Version = "12.0.40664" }), "MSI command targeting older CRT accepted");
        Reject(() => Vc14BetaUninstall.MsiTarget("MsiExec.exe /Xunrelated.msi", _ => entry), "guessed MSI product code accepted");
        Check(Vc14BetaUninstall.MsiResult(new(1605)).Success && !Vc14BetaUninstall.MsiResult(new(1605, true)).Success,
            "MSI 1605 handling discarded unknown result");
        Check(!Vc14BetaUninstall.MsiResult(new(1603)).Success && Vc14BetaUninstall.MsiResult(new(3010)).Reboot, "MSI failure/reboot ignored");
        foreach (var arch in new[] { "x64", "x86" })
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Vc14BetaUninstall", "vcred14_" + arch + ".inf");
            var text = File.ReadAllText(fixture);
            string Code(string field) => Regex.Match(text, "(?m)^" + field + "\\s*=\\s*\"([^\"]+)\"").Groups[1].Value;
            var minimum = Code("GuidMin"); var additional = Code("GuidAdd");
            Vc14Entry? Read(string code) => new(code, $"Microsoft Visual C++ 2026 {arch} {(code.Equals(minimum, StringComparison.OrdinalIgnoreCase) ? "Minimum" : "Additional")} Runtime - 14.51.36247",
                "14.51.36247.0", arch, code.Equals(minimum, StringComparison.OrdinalIgnoreCase) ? "Minimum" : "Additional", id);
            Vc14BetaUninstall.ValidateInf(text, arch, Read);
            Check(true, "fixed INF must validate");
            Vc14BetaUninstall.ValidateInf(text, arch, _ => null);
            Check(true, "already-removed MSI should not prevent old INF cleanup");
            Reject(() => Vc14BetaUninstall.ValidateInf(text, arch, code => Read(code)! with { Version = "12.0.40664" }), "INF targeting old CRT allowed");
            Reject(() => Vc14BetaUninstall.ValidateInf(text, arch, code => Read(code)! with { Architecture = "arm64" }), "INF targeting other architecture allowed");
            Reject(() => Vc14BetaUninstall.ValidateInf(text, arch, code => Read(code)! with { Role = "other" }), "INF target role mismatch allowed");
            Reject(() => Vc14BetaUninstall.ValidateInf(text.Replace("14.51.36247.0", "12.0.40664.0"), arch, Read), "INF non-14 AppVer accepted");
            Reject(() => Vc14BetaUninstall.ValidateInf(text + "\n[Extra]\ncmd.exe /c remove-all.cmd\n", arch, Read), "modified INF template accepted");
            Reject(() => Vc14BetaUninstall.ValidateInf(text.Replace(additional, minimum), arch, Read), "duplicate INF product codes accepted");
            Check(new Vc14RemovalPlanItem("vcredist14_" + arch, arch, "Microsoft Visual C++ v14", "14.51.0", "inf", "command", "method", "detail").CanRemove,
                "INF plan missing from removal flow");
        }
        var item = new Vc14RemovalPlanItem(id, "x64", entry.Name, entry.Version, "bundle", "cache", "method", "detail", Command: command, Hive: "LocalMachine", View: "Registry64");
        Check(Vc14Reinstall.Fingerprint([item]) != Vc14Reinstall.Fingerprint([item with { Command = "changed" }]), "preview fingerprint ignores command changes");
        Check(Vc14Reinstall.Fingerprint([item]) != Vc14Reinstall.Fingerprint([item with { Hive = "CurrentUser" }]), "preview fingerprint ignores registry location");
        // 实际经过 cmd 的引号往返验证；findstr 仅处理本测试提供的标准输入，不启动任何安装器。
        foreach (var payload in new[] { "argument with spaces", "argument & value", "(argument)" })
        {
            var probe = Vc14BetaUninstall.Shell("\"" + Path.Combine(Environment.SystemDirectory, "findstr.exe") + "\" /L /X /C:\"" + payload + "\"");
            probe.RedirectStandardInput = true;
            probe.RedirectStandardOutput = true;
            probe.RedirectStandardError = true;
            using var process = System.Diagnostics.Process.Start(probe) ?? throw new Exception("shell probe did not start");
            process.StandardInput.WriteLine(payload);
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            Check(process.ExitCode == 0 && output.GetAwaiter().GetResult().TrimEnd('\r', '\n') == payload,
                "cmd quoting roundtrip failed: " + error.GetAwaiter().GetResult());
        }
        Console.WriteLine($"Beta0.9-style 14.x uninstall: {count} checks passed; scopes, simulated MSI identities and harmless findstr probes; no installers executed.");
    }
}
