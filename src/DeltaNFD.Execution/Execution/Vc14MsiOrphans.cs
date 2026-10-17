using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace DeltaNFD.Services;

internal sealed record Vc14MsiAbsence(bool ConfirmedAbsent, string Detail);
internal sealed record Vc14MsiRegistration(string Hive, string View, string Key, string Name, string Version,
    string Publisher, object? WindowsInstaller, string Uninstall, string QuietUninstall, bool SimpleShape, string ValuesFingerprint);

/// <summary>Only stale ARP leaves. Never delete Installer Products/Components/UserData or invoke msiexec.</summary>
internal static class Vc14MsiOrphans
{
    private static readonly Regex NamePattern = new(
        @"^Microsoft Visual C\+\+ (?:2015|2017|2019|2022|2026|v14) (X64|X86) (Minimum|Additional) Runtime - (14\.\d+\.\d+(?:\.\d+)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal static bool IsCandidate(string name) => NamePattern.IsMatch(name);

    internal static Vc14ResidueItem? Describe(Vc14MsiRegistration r, Vc14Scan scan, Func<string, Vc14MsiAbsence> absence)
    {
        var match = NamePattern.Match(r.Name);
        if (!match.Success) return null;
        Vc14ResidueItem Item(string kind, string detail) => new(r.Hive, r.View, r.Key, r.Name, r.Version, kind, detail)
            { RegistrationFingerprint = r.ValuesFingerprint };
        if (scan.Issues.Count != 0) return Item("keep", "检测未完成，不能证明 MSI 显示登记已失效。");
        if (!Guid.TryParseExact(r.Key, "B", out var code) || code == Guid.Empty
            || r.Hive is not ("LocalMachine" or "CurrentUser") || r.View is not ("Registry32" or "Registry64")
            || r.WindowsInstaller is not int marker || marker != 1
            || !r.Publisher.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
            || !Vc14Catalog.SameVersion(match.Groups[3].Value, r.Version)
            || !r.SimpleShape || !Regex.IsMatch(r.ValuesFingerprint, "^[0-9A-Fa-f]{64}$"))
            return Item("keep", "MSI 显示登记的产品码、微软发布者、版本或键值形状未确认，保留。");
        if (scan.Products.Any(p => p.Code.Equals(r.Key, StringComparison.OrdinalIgnoreCase)))
            return Item("keep", "MSI 产品仍安装，保留显示登记。");
        if (!CommandMatches(r.Uninstall, code) || r.QuietUninstall.Length > 0 && !CommandMatches(r.QuietUninstall, code))
            return Item("keep", "MSI 命令与登记产品码不一致或包含未知参数，保留。");
        Vc14MsiAbsence proof;
        try { proof = absence(code.ToString("B")); }
        catch (Exception ex) { return Item("keep", "MSI 安装上下文无法核对，保留：" + ex.Message); }
        return proof.ConfirmedAbsent
            ? Item("msi-orphan", "Windows Installer 在本机、所有用户已安装及当前用户通告状态中均未发现此产品；将备份后仅删除该 Uninstall 显示登记，不执行卸载、不修改 Installer 组件数据库或 DLL。")
            : Item("keep", "MSI 未安装状态未确认，保留：" + proof.Detail);
    }

    internal static bool CommandMatches(string command, Guid product)
    {
        var (executable, args) = Vc14Catalog.SplitUninstallCommand(command);
        if (!executable.Equals("MsiExec.exe", StringComparison.OrdinalIgnoreCase)
            && !executable.Equals(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), StringComparison.OrdinalIgnoreCase)) return false;
        var match = Regex.Match(args,
            @"^/(?:x|i)\s*(\{[0-9A-F-]{36}\})(?:\s+/(?:qn|quiet|norestart))*\s*$", RegexOptions.IgnoreCase);
        return match.Success && Guid.TryParse(match.Groups[1].Value, out var code) && code == product;
    }

    internal static Vc14MsiAbsence EvaluateAbsence(int productState, IReadOnlyList<uint> contextResults) =>
        productState == -1 && contextResults.Count == 3 && contextResults.All(r => r is 259 or 1605)
            ? new(true, "Machine / all-user installed / current-user advertised contexts absent")
            : new(false, "产品状态=" + productState + "；安装上下文查询=" + string.Join(",", contextResults));

    internal static void CleanLeaf(Vc14ResidueItem approved, Func<Vc14ResidueItem?> observe, Action backup,
        Func<bool> delete, Func<bool> blocked, Action deleting)
    {
        if (approved.Kind != "msi-orphan" || blocked() || observe() != approved)
            throw new IOException("清理前显示登记或安装上下文已变化，未删除。");
        backup(); // An exception or an unverified backup must prevent deletion.
        if (blocked() || observe() != approved)
            throw new IOException("备份后显示登记或安装上下文已变化，未删除。");
        deleting();
        if (!delete() || observe() is not null)
            throw new IOException("删除后显示登记仍存在或状态未确认，请核对保留的备份。");
    }

    internal static Vc14MsiAbsence ReadAbsence(string code)
    {
        if (!Guid.TryParseExact(code, "B", out var parsed) || parsed == Guid.Empty) return new(false, "产品码无效。");
        // Everyone enumeration omits advertised-only unmanaged products in other accounts.
        // ARP leaves are only HKLM/current HKCU; query current-user advertised contexts separately.
        uint Query(string? sid, uint context) => MsiEnumProductsExW(code, sid, context, 0, new StringBuilder(39), out _, IntPtr.Zero, IntPtr.Zero);
        return EvaluateAbsence(MsiQueryProductStateW(code), [Query(null, 4), Query("S-1-1-0", 3), Query(null, 3)]);
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern int MsiQueryProductStateW(string product);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiEnumProductsExW(string product, string? sid,
        uint context, uint index, StringBuilder installedProduct, out uint installedContext, IntPtr installedSid, IntPtr sidLength);
}
