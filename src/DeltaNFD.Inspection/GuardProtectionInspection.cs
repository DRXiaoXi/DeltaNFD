using Microsoft.Win32;
using System.Security.AccessControl;
using System.Security.Principal;
using DeltaNFD.Services;

namespace DeltaNFD.Inspection;
internal sealed class GuardProtectionInspection
{
    internal const string IfeoRoot = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    internal const string OwnerName = "DeltaNFD_RuntimeGuard_Owner";
    internal const string Debugger = @"%windir%\System32\taskkill.exe";
    internal const string OwnerPrefix = "DeltaNFD.RuntimeGuard/1/";
    internal static readonly string[] Names = ["vc_redist.x64.exe", "vc_redist.x86.exe", "vc_redist.arm64.exe", "vcredist_x64.exe", "vcredist_x86.exe", "vcredist_ia64.exe", "vcredist.exe", "DXSETUP.exe", "dxwebsetup.exe", "UE4PrereqSetup_x64.exe", "UE4PrereqSetup_x86.exe"];
    private const int FileExecute = 0x20;
    private readonly IGuardRegistry _registry;
    private readonly IGuardFiles _files;
    private readonly IGuardStateStore _store;
    internal static string[] Scope(RuntimeGuardMode mode) => mode switch
    {
        RuntimeGuardMode.BasicV14 => Names.Take(3).ToArray(),
        RuntimeGuardMode.Full => Names,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
    private static RuntimeGuardMode EffectiveMode(GuardProtectionState state) => state.Mode ?? (state.Ifeo.Count > 0 || state.Acls.Count > 0 ? RuntimeGuardMode.Full : RuntimeGuardMode.BasicV14);
    internal static bool IsGuardDebugger(string? debugger)
    {
        if (string.IsNullOrWhiteSpace(debugger))
            return false;
        var text = debugger.Trim();
        string image;
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            image = close > 0 ? text[1..close] : text[1..];
        }
        else
        {
            var space = text.IndexOf(' ');
            image = space > 0 ? text[..space] : text;
        }

        string file;
        try
        {
            file = Path.GetFileName(Environment.ExpandEnvironmentVariables(image).Trim());
        }
        catch (ArgumentException)
        {
            return false;
        }

        return file.Equals("taskkill.exe", StringComparison.OrdinalIgnoreCase) || file.Equals("taskkill", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGuard(GuardRegistryValue value) => value.Kind is RegistryValueKind.String or RegistryValueKind.ExpandString && IsGuardDebugger(value.Debugger);
    private static bool HasDebugger(GuardRegistryValue value) => !string.IsNullOrWhiteSpace(value.Debugger);
    private static bool IsOurs(GuardRegistryValue value) => IsGuard(value) && !value.ForeignOwner;
    private static bool IsToolOwned(GuardRegistryValue value) => IsGuard(value) && value.ForeignOwner;
    private static string Fingerprint(string name, GuardRegistryValue value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { Name = name.ToLowerInvariant(), Value = value }))));
    private (GuardProtectionState State, string? Issue) LoadState()
    {
        try
        {
            return (_store.Load(), null);
        }
        catch (Exception ex)
        {
            Log.Warn("运行库防护状态文件无法读取，按空记录继续：" + ex.Message);
            return (new GuardProtectionState(), ex.Message);
        }
    }

    internal GuardProtectionStatus Probe(string? currentFile)
    {
        var(state, issue) = LoadState();
        var mode = EffectiveMode(state);
        var scope = Scope(mode);
        int blocked = 0, managed = 0, external = 0, scopeManaged = 0, scopeBlocked = 0;
        var markerResidue = false;
        var toolOwned = new List<RuntimeGuardLegacyEntry>();
        foreach (var name in Names)
        {
            var value = _registry.Read(name);
            if (!HasDebugger(value))
            {
                markerResidue |= value.Owner is not null;
                continue;
            }

            blocked++;
            var inScope = scope.Contains(name);
            if (inScope)
                scopeBlocked++;
            if (IsOurs(value))
            {
                managed++;
                if (inScope)
                    scopeManaged++;
            }
            else
                external++;
            if (IsToolOwned(value))
                toolOwned.Add(new(name, Fingerprint(name, value)));
        }

        var notes = new List<string>();
        if (issue is not null)
            notes.Add("防护状态文件无法读取：" + issue);
        var denied = false;
        foreach (var path in AclPaths(state, currentFile))
        {
            try
            {
                denied |= HasExplicitExecuteDeny(_files.Read(path).Dacl);
            }
            catch (Exception ex)when (IsMissing(ex))
            {
            }
            catch (Exception ex)
            {
                notes.Add("UE4 前置包权限无法读取：" + ex.Message);
            }
        }

        return new(blocked, managed, external, denied, state.Acls.Count > 0 || state.LegacyAclRepairs.Any(r => !r.Completed))
        {
            Mode = mode,
            ScopeManaged = scopeManaged,
            ScopeBlocked = scopeBlocked,
            LegacyIfeo = toolOwned,
            PendingIfeo = markerResidue || state.LegacyIfeoRepairs.Any(r => !r.Completed),
            Issue = string.Join("；", notes),
        };
    }

    private static IEnumerable<string> AclPaths(GuardProtectionState state, string? currentFile)
    {
        var paths = state.Acls.Select(a => a.Path).Concat(state.LegacyAclRepairs.Where(r => !r.Completed).Select(r => r.Path)).ToList();
        if (!string.IsNullOrWhiteSpace(currentFile))
            paths.Add(currentFile);
        return paths.Where(IsUe4Prereq).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsUe4Prereq(string path)
    {
        var file = Path.GetFileName(path);
        return file.StartsWith("UE4PrereqSetup_", StringComparison.OrdinalIgnoreCase) && file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMissing(Exception ex) => ex is FileNotFoundException or DirectoryNotFoundException || ex is System.ComponentModel.Win32Exception { NativeErrorCode: 2 or 3 };
    internal bool CanRepairLegacyAcl(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !IsUe4Prereq(path))
            return false;
        try
        {
            return HasExplicitExecuteDeny(_files.Read(path).Dacl);
        }
        catch
        {
            return false;
        }
    }

    internal static bool HasExplicitExecuteDeny(string sddl) => ExecuteDenies(sddl).Any(a => (a.AceFlags & AceFlags.Inherited) == 0);
    private static IEnumerable<CommonAce> ExecuteDenies(string sddl)
    {
        var dacl = new RawSecurityDescriptor(sddl).DiscretionaryAcl;
        return dacl is null ? [] : dacl.OfType<CommonAce>().Where(a => a.AceQualifier == AceQualifier.AccessDenied && a.SecurityIdentifier.IsWellKnown(WellKnownSidType.WorldSid) && (a.AccessMask & FileExecute) != 0).ToList();
    }

    internal GuardProtectionInspection()
    {
        _registry = new GuardReadRegistry();
        _files = new GuardReadFile();
        _store = new GuardReadStore();
    }
}
