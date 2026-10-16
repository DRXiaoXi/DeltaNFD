using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace DeltaNFD.Services;

// Read-only evidence. Registration labels and uninstall strings are data, never commands to execute.
internal static class Vc14Diagnostics
{
    internal static string ExplainDllMismatch(string directory, string mismatch)
    {
        try
        {
            var evidence = Directory.EnumerateFiles(directory, "install*.log", SearchOption.TopDirectoryOnly).Take(16)
                .Select(Vc14DependencyPolicy.ReadInstallerLog);
            return ExplainDllMismatch(evidence, mismatch);
        }
        catch { return "MSI 登记已是 14.42，但系统 DLL 实际版本仍未对齐，原包修复后仍不能确认成功。\n" + mismatch; }
    }

    internal static string ExplainDllMismatch(IEnumerable<string> installerLogs, string mismatch)
    {
        var compatibilityBlock = installerLogs.Any(log =>
            log.Contains("APPCOMPAT: (GetFileInstallState) Altered install mode for shared redist file", StringComparison.Ordinal)
            && log.Contains("since the same component with higher versioned keyfile exists", StringComparison.Ordinal));
        return compatibilityBlock
            ? "MSI 登记已是 14.42，但 Windows Installer 的共享运行库兼容规则阻止了较高版本 DLL 被覆盖（APPCOMPAT）。"
                + "原包修复返回成功仍可能保留较新文件，本次不能报告修复成功。重复普通修复或仅进入安全模式不能保证解决；需另行核验系统版本规则及文件归属，不能直接删除系统 DLL。\n" + mismatch
            : "MSI 登记已是 14.42，但系统 DLL 实际版本仍未对齐（更高版本文件可能被其他产品持有）。原包修复后仍未恢复，不能报告修复成功。\n" + mismatch;
    }

    internal static bool Relevant(string name, string version) => Vc14BetaUninstall.RelevantForResidue(name, version);
    internal static string? Save(Vc14Scan scan)
    {
        try
        {
            var registrations = new List<object>(); var errors = new List<string>();
            foreach (var (hive, view) in Vc14Catalog.RegistryViews())
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var root = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    foreach (var keyName in root?.GetSubKeyNames() ?? [])
                    {
                        using var key = root!.OpenSubKey(keyName);
                        string Text(string valueName)
                        {
                            var text = key?.GetValue(valueName, "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
                            return text[..Math.Min(8192, text.Length)];
                        }
                        var name = Text("DisplayName"); var version = Vc14Catalog.RegistrationVersion(key);
                        if (!Relevant(name, version)) continue;
                        registrations.Add(new { Hive = hive.ToString(), RegistryView = view.ToString(), Key = keyName, Name = name, Version = version,
                            Publisher = Text("Publisher"), BundleVersion = Text("BundleVersion"), BundleCachePath = Text("BundleCachePath"),
                            UninstallString = Text("UninstallString"), QuietUninstallString = Text("QuietUninstallString"),
                            WindowsInstaller = key?.GetValue("WindowsInstaller"),
                            MsiDiagnosticStateCode = scan.MsiStateDiagnostics.FirstOrDefault(p => p.ProductCode.Equals(keyName, StringComparison.OrdinalIgnoreCase))?.StateCode,
                            VerifiedProductCode = scan.Products.FirstOrDefault(p => p.Code.Equals(keyName, StringComparison.OrdinalIgnoreCase))?.Code });
                        if (registrations.Count >= 256) { errors.Add("登记证据达到 256 项上限，后续项未收集。"); break; }
                    }
                }
                catch (Exception ex) { errors.Add(hive + " " + view + "：" + ex.Message); }
            }
            var dlls = new List<object>();
            foreach (var arch in new[] { "x64", "x86" })
            {
                var folder = Environment.GetFolderPath(arch == "x64" ? Environment.SpecialFolder.System : Environment.SpecialFolder.SystemX86);
                foreach (var name in arch == "x64" ? new[] { "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" } : new[] { "msvcp140.dll", "vcruntime140.dll" })
                {
                    var path = Path.Combine(folder, name);
                    try { dlls.Add(new { Architecture = arch, Path = path, Exists = File.Exists(path), Version = File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion : null }); }
                    catch (Exception ex) { errors.Add(path + "：" + ex.Message); }
                }
            }
            // One latest snapshot avoids creating a directory on every homepage refresh.
            var report = Path.Combine(Log.LogDirectory, "vc14-detection-latest.json");
            Write(report, new { MsiStateDiagnosticsVersion = 1, MsiDiagnosticCoverage = Vc14MsiStateDiagnostics.Limitations,
                CapturedUtc = DateTimeOffset.UtcNow, ProductVersion = FileVersionInfo.GetVersionInfo(typeof(Vc14Diagnostics).Assembly.Location).ProductVersion,
                Scan = scan, Registrations = registrations, CoreDlls = dlls, EvidenceIssues = errors });
            Log.Info($"14.x 检测：已核验组件 {scan.Products.Count}，登记提醒 {scan.Notices.Count}，待核对 {scan.Issues.Count}，MSI 状态诊断 {scan.MsiStateDiagnostics.Count} 项"
                + (scan.MsiStateDiagnosticsTruncated ? "（诊断达到上限，未收集完整）" : "") + $"；报告：{report}");
            foreach (var issue in scan.Issues) Log.Warn("14.x 组件待核对：" + issue);
            return report;
        }
        catch (Exception ex) { Log.Warn("14.x 检测证据未能保存：" + ex.Message); return null; }
    }
    internal static void Write(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
