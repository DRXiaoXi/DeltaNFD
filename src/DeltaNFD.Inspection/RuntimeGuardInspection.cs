using Microsoft.Win32;
using DeltaNFD.Services;

namespace DeltaNFD.Inspection;
public sealed class RuntimeGuardInspection
{
    private readonly GuardProtectionInspection _protection;
    public Task<RuntimeGuardStatus> GetStatusAsync() => InspectionScheduler.Run(ReadStatusCoreAsync);
    private async Task<RuntimeGuardStatus> ReadStatusCoreAsync()
    {
        var redists = await DeltaNFD.Inspection.InspectionScheduler.Run(GetInstalledRedists);
        var paths = await FindUe4PrereqPathsAsync();
        var path = paths.FirstOrDefault() ?? "";
        var state = _protection.Probe(path);
        return new RuntimeGuardStatus
        {
            InstalledRedists = redists,
            IfeoCount = state.Blocked,
            IfeoTotal = GuardProtectionInspection.Scope(state.Mode).Length,
            IfeoApplied = state.ScopeManaged == GuardProtectionInspection.Scope(state.Mode).Length,
            IfeoManagedCount = state.Managed,
            IfeoExternalCount = state.External,
            Mode = state.Mode,
            ScopeManagedCount = state.ScopeManaged,
            ScopeBlockedCount = state.ScopeBlocked,
            LegacyIfeo = state.LegacyIfeo,
            IfeoRestorePending = state.PendingIfeo,
            Ue4PrereqFound = path.Length > 0,
            Ue4PrereqPath = path,
            Ue4PrereqDenied = state.FileDenied,
            Ue4RestorePending = state.PendingAcl,
            CanRepairLegacyUe4Acl = _protection.CanRepairLegacyAcl(path),
            GuardIssue = state.Issue,
        };
    }

    private static List<VcRedistInfo> GetInstalledRedists()
    {
        var result = new List<VcRedistInfo>();
        var roots = (ValueTuple<string, string>[])[("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"), ("HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"), ];
        foreach (var(hive, rootPath)in roots)
        {
            using var root = Registry.LocalMachine.OpenSubKey(rootPath);
            if (root is null)
            {
                continue;
            }

            foreach (var subName in root.GetSubKeyNames())
            {
                using var sub = root.OpenSubKey(subName);
                if (sub?.GetValue("DisplayName")is not string displayName || !displayName.Contains("Visual C++", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // VC++ x64 redist 由 32 位引导器写入 WOW6432Node，不能按 Hive 判断架构，以显示名为准
                var arch = System.Text.RegularExpressions.Regex.IsMatch(displayName, @"\bx64\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "x64" : displayName.Contains("arm64", StringComparison.OrdinalIgnoreCase) ? "arm64" : "x86";
                if (result.Any(r => r.DisplayName == displayName))
                {
                    continue;
                }

                result.Add(new VcRedistInfo { DisplayName = displayName, Version = sub.GetValue("DisplayVersion") as string ?? "未知", Architecture = arch, });
            }
        }

        return result.OrderBy(r => r.Architecture).ThenBy(r => r.DisplayName).ToList();
    }

    private static Task<IReadOnlyList<string>> FindUe4PrereqPathsAsync() => DeltaForceLocator.FindUe4PrereqPathsAsync();
    public RuntimeGuardInspection()
    {
        _protection = new GuardProtectionInspection();
    }
}
