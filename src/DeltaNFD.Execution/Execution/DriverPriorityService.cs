using Microsoft.Win32;
using System.Security.Principal;
using DeltaNFD.Inspection;

namespace DeltaNFD.Services;

public interface IDriverPriorityEnvironment
{
    string? Blocker(bool restoring);
    DriverPriorityDiscovery Discover();
    DriverPriorityDevice? Read(string instanceId);
    void Write(DriverPriorityDevice expected, int slot, InterruptDword value, bool restoring);
}

public sealed class DriverPriorityEngine(DriverPriorityStore store, IDriverPriorityEnvironment environment)
{
    private static bool Owned(DriverPriorityBackup b, int slot) => b.Original.Get(slot) != b.Applied.Get(slot) || b.Original.Get(slot) != b.Before.Get(slot);
    private static bool Known(DriverPriorityBackup b, int slot, InterruptDword current) => current == b.Original.Get(slot) || current == b.Applied.Get(slot) || current == b.Before.Get(slot);
    private static bool SameDevice(DriverPriorityDevice d, DriverPriorityBackup b) => d.InstanceId.Equals(b.InstanceId, StringComparison.OrdinalIgnoreCase) && d.Identity == b.Identity;
    private void Gate(bool restoring, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); if (environment.Blocker(restoring) is { } message) throw new InvalidOperationException(message); }
    public DriverPriorityReport List(bool backupsOnly = false) => store.Exclusive<DriverPriorityReport>(() =>
    {
        IReadOnlyList<DriverPriorityBackup> backups;
        try { backups = store.ReadAll(); }
        catch (Exception ex) { return new([], false, 1, ["原始备份未能读取，未把它当作没有备份：" + ex.Message]); }
        if (backupsOnly && backups.Count == 0) return new([], true, 0, []);
        var scan = environment.Discover(); var errors = scan.Errors.ToList();
        if (scan.Devices.Select(d => d.InstanceId.ToUpperInvariant()).Distinct().Count() != scan.Devices.Count)
            return new([], false, backups.Count, ["PCI列表存在重复实例，未提供修改入口。"]);
        var devices = scan.Devices.ToList();
        foreach (var backup in backups.Where(b => !devices.Any(d => d.InstanceId.Equals(b.InstanceId, StringComparison.OrdinalIgnoreCase))))
            devices.Add(new(backup.InstanceId, "未连接或未读取到的设备", "", "", "", "", null, null, false, null, [], "设备缺失，原始备份保留。"));
        var rows = devices.Select(d =>
        {
            var b = backups.SingleOrDefault(b => b.InstanceId.Equals(d.InstanceId, StringComparison.OrdinalIgnoreCase));
            var matches = b is null || SameDevice(d, b);
            var known = d.Settings is not null && (b is null || Enumerable.Range(0, 3).All(i => !Owned(b, i) || Known(b, i, d.Settings.Get(i))));
            var readable = d.Settings is not null && DriverPriorityPolicy.ValidId(d.InstanceId) && DriverPriorityPolicy.ValidHash(d.Identity);
            var edit = readable && d.Started && d.DriverKey.Length > 0 && d.DriverVersion.Length > 0 && matches && known;
            var detail = b is null ? "" : !matches ? "设备或驱动身份已变化，未自动套用旧备份。"
                : !known ? "本工具管理的值已被外部改动，原始记录保留。" : "原始值已备份，可按备份恢复。";
            return new DriverPriorityItem(d, DriverPriorityPolicy.SnapshotFingerprint(d, b?.Digest ?? ""), b is not null, edit && errors.Count == 0,
                b is not null && readable && matches && errors.Count == 0, detail);
        }).ToArray();
        return new(backupsOnly ? rows.Where(r => r.HasBackup).ToArray() : rows, errors.Count == 0, backups.Count, errors);
    });
    public OperationResult Change(DriverPriorityArgs args, CancellationToken token = default)
    {
        var submitted = false;
        try
        {
            if (!DriverPriorityPolicy.ValidId(args.InstanceId) || !DriverPriorityPolicy.ValidHash(args.Fingerprint)
                || !Enum.IsDefined(args.Action) || !args.Confirmed) return OperationResult.Fail("设备、操作或确认参数无效。");
            if (args.Action != DriverPriorityAction.Apply && (args.MsiEnabled is not null || args.SetMessageLimit || args.MessageLimit is not null || args.Priority is not null))
                return OperationResult.Fail("恢复或归档不接受新的设置值。");
            return store.Exclusive(() =>
            {
                Gate(args.Action != DriverPriorityAction.Apply, token);
                var report = List(); if (!report.Complete) throw new IOException(string.Join("；", report.Errors));
                var row = report.Items.SingleOrDefault(r => r.Device.InstanceId.Equals(args.InstanceId, StringComparison.OrdinalIgnoreCase));
                if (row is null || row.Fingerprint != args.Fingerprint) throw new IOException("设备、设置或备份已变化，请刷新后再操作。");
                var record = store.ReadAll().SingleOrDefault(b => b.InstanceId.Equals(args.InstanceId, StringComparison.OrdinalIgnoreCase));
                if (args.Action == DriverPriorityAction.KeepCurrent)
                {
                    if (record is null) return OperationResult.Fail("没有可归档的原始记录。");
                    Gate(true, token); store.Archive(record);
                    return OperationResult.Ok("当前中断设置未修改；原始记录已移入软件备份归档目录。");
                }
                if (args.Action == DriverPriorityAction.Restore)
                {
                    if (record is null || !row.CanRestore) return OperationResult.Fail("设备或驱动身份未匹配，未覆盖当前值；备份保留。");
                    var failures = new List<string>();
                    for (var i = 0; i < 3; i++)
                    {
                        if (!Owned(record, i)) continue;
                        try
                        {
                            var original = record.Original.Get(i);
                            using (store.Hold(record))
                            {
                                Gate(true, token);
                                var current = ReadSame(record.InstanceId, record.Identity);
                                if (!Known(record, i, current.Settings!.Get(i))) throw new IOException("当前值存在外部变更，未覆盖。");
                                if (current.Settings.Get(i) != original)
                                {
                                    Gate(true, token); submitted = true;
                                    environment.Write(current, i, original, true); VerifyWrite(current, i, original);
                                }
                            }
                            record = record with { Before = record.Before.Set(i, original), Applied = record.Applied.Set(i, original), WrittenUtc = DateTimeOffset.UtcNow };
                            store.Save(record);
                        }
                        catch (Exception ex) { failures.Add($"第{i + 1}项恢复未完成：{ex.Message}"); }
                    }
                    if (failures.Count > 0) return new OperationResult { Success = false, IsPartial = submitted, OutcomeUnknown = submitted,
                        Message = string.Join("；", failures) + " 原始记录保留，可重试或人工核对。" };
                    store.Archive(record);
                    return OperationResult.Ok("已按原始备份恢复中断设置；请手动重启电脑后生效。", requiresReboot: true);
                }
                if (!row.CanApply) return OperationResult.Fail("当前设备仅供查看或存在恢复冲突，未修改。");
                var before = row.Device.Settings!; var desired = DriverPriorityPolicy.Desired(row.Device, args);
                if (before == desired) return OperationResult.Ok("设置已与所选值一致，未重复写入。");
                var originalSettings = before;
                if (record is not null)
                    for (var i = 0; i < 3; i++) if (Owned(record, i)) originalSettings = originalSettings.Set(i, record.Original.Get(i));
                var prepared = new DriverPriorityBackup(1, store.Machine, row.Device.InstanceId, row.Device.Identity,
                    originalSettings, before, desired, DateTimeOffset.UtcNow);
                Gate(false, token); var fresh = ReadSame(row.Device.InstanceId, row.Device.Identity);
                if (fresh.Settings != before) throw new IOException("写前设置已变化，未修改。");
                store.Save(prepared); // durable before the first registry write
                var expected = before;
                for (var i = 0; i < 3; i++)
                {
                    if (expected.Get(i) == desired.Get(i)) continue;
                    using (store.Hold(prepared))
                    {
                        Gate(false, token);
                        fresh = ReadSame(row.Device.InstanceId, row.Device.Identity);
                        if (fresh.Settings != expected) throw new IOException("提交前设置已被外部改变，已停止后续写入。");
                        submitted = true; environment.Write(fresh, i, desired.Get(i), false); VerifyWrite(fresh, i, desired.Get(i));
                    }
                    expected = expected.Set(i, desired.Get(i));
                }
                if (ReadSame(row.Device.InstanceId, row.Device.Identity).Settings != desired) throw new IOException("完整写后核对不符。");
                if (!prepared.Pending) store.Archive(prepared);
                return OperationResult.Ok("中断设置已写入并回读，原值已备份；请手动重启电脑后生效。", requiresReboot: true);
            });
        }
        catch (Exception ex) { return new OperationResult { Success = false, IsPartial = submitted, OutcomeUnknown = submitted,
            Message = (submitted ? "中断设置操作未完整确认，原始备份保留：" : "未完成中断设置修改：") + ex.Message }; }
    }
    private DriverPriorityDevice ReadSame(string id, string identity)
    {
        var current = environment.Read(id) ?? throw new IOException("设备已断开。");
        if (!current.InstanceId.Equals(id, StringComparison.OrdinalIgnoreCase) || current.Identity != identity || current.Settings is null)
            throw new IOException("设备或驱动身份、设置读取已变化。");
        return current;
    }
    private void VerifyWrite(DriverPriorityDevice before, int slot, InterruptDword value)
    { if (ReadSame(before.InstanceId, before.Identity).Settings != before.Settings!.Set(slot, value)) throw new IOException("写后回读不符，未继续处理。"); }
    public OperationResult RestoreAll(CancellationToken token = default)
    {
        try { return store.Exclusive(() => RestoreAllCore(token)); }
        catch (Exception ex) { return OperationResult.Fail("未取得完整恢复名额，备份保留：" + ex.Message); }
    }
    private OperationResult RestoreAllCore(CancellationToken token)
    {
        var unknown = false; var reboot = false;
        try
        {
            var messages = new List<string>();
            foreach (var b in store.ReadAll())
            {
                Gate(true, token);
                var row = List().Items.SingleOrDefault(r => r.Device.InstanceId.Equals(b.InstanceId, StringComparison.OrdinalIgnoreCase));
                if (row is null) { messages.Add(b.InstanceId + " 未能核对，备份保留。"); continue; }
                var result = Change(new(b.InstanceId, row.Fingerprint, DriverPriorityAction.Restore, true), token);
                if (!result.Success) messages.Add(b.InstanceId + "：" + result.Message);
                unknown |= result.OutcomeUnknown; reboot |= result.RequiresReboot;
            }
            return new OperationResult { Success = messages.Count == 0, OutcomeUnknown = unknown, IsPartial = messages.Count > 0,
                RequiresReboot = reboot, Message = messages.Count == 0 ? "已恢复本工具保存的全部设备中断设置；如有改动，请手动重启。" : string.Join("；", messages) };
        }
        catch (Exception ex) { return new OperationResult { Success = false, OutcomeUnknown = unknown, IsPartial = true,
            RequiresReboot = reboot, Message = "驱动设置恢复未完成，保留原始备份：" + ex.Message }; }
    }
}

public static class DriverPriorityService
{
    private static DriverPriorityEngine Create()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        var machine = key?.GetValue("MachineGuid") as string;
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(machine) || string.IsNullOrWhiteSpace(sid)) throw new IOException("未能绑定本机与当前用户，未读取或修改备份。");
        return new(new(Path.Combine(AppDataPaths.Root, "DriverPriority"), DriverPriorityPolicy.Hash(machine + "|" + sid)), new NativeEnvironment());
    }
    public static DriverPriorityReport List() => Create().List();
    public static DriverPriorityReport ReadBackups()
    {
        var path = Path.Combine(AppDataPaths.Root, "DriverPriority"); DriverPriorityStore.RejectLinks(path);
        try { if ((File.GetAttributes(path) & FileAttributes.Directory) == 0) throw new IOException("驱动备份根不是目录。"); }
        catch (FileNotFoundException) { return new([], true, 0, []); }
        catch (DirectoryNotFoundException) { return new([], true, 0, []); }
        return Create().List(backupsOnly: true);
    }
    public static OperationResult Change(DriverPriorityArgs args, CancellationToken token)
    {
        if (args.Action == DriverPriorityAction.Apply && !ReleaseFeaturePolicy.DriverPriorityEnabled)
            return OperationResult.Fail("当前版本未开放驱动优先级；原有备份仍可恢复。");
        using var target = GameTargetService.Default.BeginOperation(); return Create().Change(args, token);
    }
    public static OperationResult RestoreAll(CancellationToken token = default)
    { using var target = GameTargetService.Default.BeginOperation(); return Create().RestoreAll(token); }
    private sealed class NativeEnvironment : IDriverPriorityEnvironment
    {
        public string? Blocker(bool restoring)
        {
            if (!ElevationHelper.IsElevated) return ElevationHelper.NotElevatedMessage;
            if (!restoring && OfflineModeGuard.BlocksNormalAutomation) return "恢复门禁尚未解除，不能修改设备中断设置。";
            return GameTargetService.Default.IsRunning() || DeltaForceLocator.IsGameRunning() ? "请先退出游戏再更改设备中断设置。" : null;
        }
        public DriverPriorityDiscovery Discover() => DriverPriorityInspection.Discover();
        public DriverPriorityDevice? Read(string instanceId) => DriverPriorityInspection.Read(instanceId);
        public void Write(DriverPriorityDevice expected, int slot, InterruptDword value, bool restoring)
        {
            if (Blocker(restoring) is { } blocker) throw new IOException(blocker);
            var fresh = Read(expected.InstanceId);
            if (fresh is null || fresh.Identity != expected.Identity || fresh.Settings != expected.Settings || !fresh.Started && !restoring)
                throw new IOException("设备身份或配置已变化。");
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var device = root.OpenSubKey(DriverPriorityInspection.EnumRoot + expected.InstanceId) ?? throw new IOException("设备登记已消失。");
            if (!string.Equals(device.GetValue("Driver") as string, expected.DriverKey, StringComparison.Ordinal)
                || DriverPriorityInspection.ReadSettings(device) != expected.Settings) throw new IOException("同句柄写前核验不符。");
            using var management = device.OpenSubKey(@"Device Parameters\Interrupt Management", writable: true) ?? throw new IOException("中断管理登记不存在或没有写权限。");
            var relative = slot == 2 ? "Affinity Policy" : "MessageSignaledInterruptProperties";
            var name = slot switch { 0 => "MSISupported", 1 => "MessageNumberLimit", 2 => "DevicePriority", _ => throw new ArgumentOutOfRangeException(nameof(slot)) };
            using var key = value.Exists ? management.CreateSubKey(relative, writable: true) : management.OpenSubKey(relative, writable: true);
            if (value.Exists) key!.SetValue(name, unchecked((int)value.Value), RegistryValueKind.DWord);
            else key?.DeleteValue(name, throwOnMissingValue: false);
            key?.Flush();
        }
    }
}
