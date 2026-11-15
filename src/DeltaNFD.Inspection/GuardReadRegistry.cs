using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DeltaNFD.Services;

namespace DeltaNFD.Inspection;
internal sealed class GuardReadRegistry : IGuardRegistry
{
    private static RegistryKey Base() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
    public GuardRegistryValue Read(string name)
    {
        using var root = Base();
        using var key = root.OpenSubKey(GuardProtectionInspection.IfeoRoot + "\\" + name);
        if (key is null)
            return new(null, RegistryValueKind.None, null, false);
        var names = key.GetValueNames();
        var present = names.Contains("Debugger", StringComparer.OrdinalIgnoreCase);
        var raw = present ? key.GetValue("Debugger", null, RegistryValueOptions.DoNotExpandEnvironmentNames) : null;
        var kind = present ? key.GetValueKind("Debugger") : RegistryValueKind.None;
        return new(raw as string ?? (present ? "<unsupported debugger type>" : null), kind, key.GetValue(GuardProtectionInspection.OwnerName) as string ?? (names.Contains(GuardProtectionInspection.OwnerName, StringComparer.OrdinalIgnoreCase) ? "<invalid owner>" : null), names.Contains("RuntimeGuardHotPatch_Owner", StringComparer.OrdinalIgnoreCase))
        {
            ForeignMarker = names.Contains("RuntimeGuardHotPatch_Owner", StringComparer.OrdinalIgnoreCase) && key.GetValueKind("RuntimeGuardHotPatch_Owner") == RegistryValueKind.String ? key.GetValue("RuntimeGuardHotPatch_Owner") as string : null
        };
    }
}
