using Microsoft.Win32;
using DeltaNFD.Inspection;

namespace DeltaNFD.Services.TweakDb;

public sealed partial class BxService
{
    private sealed record SecurityRestoreTarget(string Kind, string Path, string Name, BxTweak Operation);
    private static Dictionary<string, SecurityRestoreTarget> SecurityRestoreScope(BxItem item)
    {
        var scope = new Dictionary<string, SecurityRestoreTarget>(StringComparer.OrdinalIgnoreCase);
        if (!SecurityFeatureInspection.Handles(item.Name)) return scope;
        foreach (var op in item.Tweaks)
        {
            if (IsRegType(op.TweakType))
            {
                var (hive, sub) = ParsePath(op.Path);
                scope[$"Reg|{hive.Name}|{sub}|{op.Key}"] = new("Reg", sub, op.Key ?? "", op);
            }
            else if (op.TweakType == "SVC") scope["Service|" + op.Path] = new("Service", op.Path, "", op);
        }
        return scope;
    }
    internal static OperationResult RestoreSecurityEntries(BxItem item, IReadOnlyList<BxBackupEntry> entries,
        Action<BxBackupEntry> restoreAndVerify, Action<List<string>> removeVerified)
    {
        var scope = SecurityRestoreScope(item);
        var selected = entries.Where(e => scope.ContainsKey(e.Id)).ToArray();
        if (selected.Length == 0) return OperationResult.Fail("没有此项的可核验原值备份，未写入默认值，也未修改其他条目。");
        if (selected.GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1))
            return OperationResult.Fail("此项存在重复原值记录，未执行恢复；请先核对记录。");
        var restored = new List<string>(); var errors = new List<string>();
        foreach (var entry in selected)
        {
            var target = scope[entry.Id];
            if (entry.Kind != target.Kind || !string.Equals(entry.KeyPath, target.Path, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(entry.ValueName, target.Name, StringComparison.OrdinalIgnoreCase))
            { errors.Add("本项备份身份不一致，保留：" + entry.Id); continue; }
            try { restoreAndVerify(entry); restored.Add(entry.Id); }
            catch (Exception ex) { errors.Add(entry.Id + "：" + ex.Message); }
        }
        try { removeVerified(restored); }
        catch (Exception ex) { errors.Add("已回读原值，但备份归档未完成：" + ex.Message); }
        return new() { Success = errors.Count == 0, IsPartial = restored.Count > 0 && errors.Count > 0,
            RequiresReboot = restored.Count > 0, Message = $"本项已恢复并回读 {restored.Count}/{selected.Length} 个原值。"
                + (errors.Count > 0 ? "未完成记录保留：" + string.Join("；", errors) : "请手动重启后核对防护；其他条目的备份与设置保持不变。") };
    }
    private static OperationResult RestoreSecurityItem(BxItem item)
    {
        if (!BxBackupStore.TryReadAll(out var entries, out var error))
            return OperationResult.Fail("本项备份不可读，未执行恢复：" + error);
        var scope = SecurityRestoreScope(item);
        return RestoreSecurityEntries(item, entries, entry =>
        {
            var target = scope[entry.Id];
            if (entry.Kind == "Reg")
            {
                var (hive, sub) = ParsePath(target.Operation.Path);
                var current = CaptureRegEntry(hive, sub, target.Name);
                var original = SameSecurityRegistryValue(current, entry);
                var applied = PickAppliedEntry(target.Operation);
                if (!original && (applied is null || CompareReg(ReadReg(target.Operation), applied, target.Operation.ValueFormat) != true))
                    throw new IOException("当前值不属于备份原值或本工具目标值，拒绝覆盖第三方更改。");
                if (!original) RestoreRegEntry(entry);
                if (!SameSecurityRegistryValue(CaptureRegEntry(hive, sub, target.Name), entry))
                    throw new IOException("原值回读失败。");
            }
            else
            {
                if (entry.ValueKind is < 0 or > 4) throw new IOException("备份服务启动值无效。");
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + target.Path, writable: true)
                    ?? throw new IOException("服务键不可用，保留原值备份。");
                if (key.GetValue("Start") is not int current || current != 4 && current != entry.ValueKind)
                    throw new IOException("服务启动配置发生第三方更改，拒绝覆盖。");
                if (current != entry.ValueKind) key.SetValue("Start", entry.ValueKind, RegistryValueKind.DWord);
                if (key.GetValue("Start") is not int after || after != entry.ValueKind) throw new IOException("服务启动原值回读失败。");
            }
        }, BxBackupStore.RemoveIds);
    }
    private static bool SameSecurityRegistryValue(BxBackupEntry left, BxBackupEntry right) => left.ValueKind == right.ValueKind
        && left.Data == right.Data && (left.StringData ?? []).SequenceEqual(right.StringData ?? []);
    private static bool BackupSecurityRegistry(BxTweak op, out string error)
    {
        try
        {
            var (hive, sub) = ParsePath(op.Path);
            BxBackupStore.SaveOnce(CaptureRegEntry(hive, sub, op.Key ?? ""));
            error = ""; return true;
        }
        catch (Exception ex) { error = "原值备份失败，未写入：" + ex.Message; return false; }
    }
    internal static OperationResult SecurityApplyResult(SecurityFeatureSnapshot state, OperationResult operations, int done)
    {
        if (state.State == BxState.On)
            return new() { Success = true, IsPartial = !operations.Success, Message = "核心服务已禁用并确认停止。"
                + (operations.Success ? "" : "附加设置未全部完成，原值备份保留，可单独恢复此项。" + operations.Message) };
        if (state.ConfiguredDisabled && state.State == BxState.Mixed)
            return new() { Success = true, IsPartial = true, RequiresReboot = true,
                Message = state.Detail + " " + operations.Message + " 原值备份保留，可单独恢复此项。" };
        return new() { Success = false, IsPartial = done > 0, OutcomeUnknown = state.State == BxState.Unknown,
            Message = "未确认核心服务已禁用：" + state.Detail + " " + operations.Message + " 原值备份保留。" };
    }
}
