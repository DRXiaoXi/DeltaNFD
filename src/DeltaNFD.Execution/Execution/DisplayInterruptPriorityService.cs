using Microsoft.Win32;
using DeltaNFD.Backends;
using DeltaNFD.Inspection;

namespace DeltaNFD.Services;

internal interface IDisplayInterruptEnvironment
{
    string? Blocker(bool restoring);
    IReadOnlyList<DisplayInterruptAdapter> Read();
    void Write(DisplayInterruptAdapter expected, uint? priority, bool restoring, Action beforeMutation);
}

internal sealed class DisplayInterruptPriorityEngine(DisplayInterruptPriorityStore store, IDisplayInterruptEnvironment environment,
    bool featureEnabled)
{
    public DisplayInterruptPriorityReport List(bool backupsOnly = false)
    {
        try { return store.Exclusive(() => Inspect(backupsOnly)); }
        catch (Exception ex) { return new([], false, 0, "显卡中断状态未能核对。" + ex.Message); }
    }

    private DisplayInterruptPriorityReport Inspect(bool backupsOnly)
    {
        var backups = store.ReadAll();
        if (backupsOnly && backups.Count == 0) return new([], true, 0, "没有待恢复的显卡中断备份。");
        var adapters = environment.Read().ToDictionary(d => d.InstanceId, StringComparer.OrdinalIgnoreCase);
        foreach (var backup in backups)
            adapters.TryAdd(backup.InstanceId, new(backup.InstanceId, "未连接的显卡", "", "", "", false, false, null));
        var items = adapters.Values.Select(adapter =>
        {
            var backup = backups.SingleOrDefault(b => string.Equals(b.InstanceId, adapter.InstanceId, StringComparison.OrdinalIgnoreCase));
            var same = backup is null || backup.Identity == adapter.Identity;
            var known = backup is null || adapter.Priority == DisplayInterruptPriorityPolicy.High || adapter.Priority == backup.Original;
            var readable = adapter.Readable && DisplayInterruptPriorityPolicy.ValidHash(adapter.Identity);
            var message = !readable ? adapter.Detail : !same ? "设备或驱动身份已变化，原值备份保留。"
                : !known ? "当前设置有外部改动，原值备份保留。"
                : backup is not null ? "已有原值备份。" : !adapter.Started ? "请先启用设备。" : "";
            return new DisplayInterruptPriorityItem(adapter, DisplayInterruptPriorityPolicy.Fingerprint(adapter, backup?.Digest), backup is not null,
                featureEnabled && backup is null && readable && adapter.Started && adapter.Priority != DisplayInterruptPriorityPolicy.High,
                backup is not null && readable && same && known, message);
        }).Where(item => !backupsOnly || item.HasBackup).ToArray();
        return new(items, true, backups.Count, items.Length == 0 ? "没有可识别的 PCI 显卡。" : "显卡中断优先级已读取。");
    }

    public OperationResult Change(DisplayInterruptPriorityArgs args, CancellationToken token)
    {
        var attempted = false;
        try
        {
            if (!args.Confirmed || !DisplayInterruptPriorityPolicy.ValidId(args.InstanceId) || !DisplayInterruptPriorityPolicy.ValidHash(args.Fingerprint))
                return OperationResult.Fail("显卡身份、状态或确认信息无效。");
            if (!args.Restore && !featureEnabled) return OperationResult.Fail("当前构建未开放提高显卡中断优先级。");
            return store.Exclusive(() =>
            {
                Gate(args.Restore, token);
                var item = Inspect(false).Items.SingleOrDefault(i => SameId(i.Adapter.InstanceId, args.InstanceId));
                if (item is null || item.Fingerprint != args.Fingerprint) return OperationResult.Fail("设备或设置已变化，请重新检测。");
                if (args.Restore ? !item.CanRestore : !item.CanApply)
                    return OperationResult.Fail("当前显卡状态不允许执行此操作。" + item.Detail);
                var before = item.Adapter;
                var backup = store.ReadAll().SingleOrDefault(b => SameId(b.InstanceId, before.InstanceId));
                if (args.Restore && backup is null || !args.Restore && backup is not null)
                    return OperationResult.Fail("原值备份已变化，请重新检测。");
                var desired = args.Restore ? backup!.Original : DisplayInterruptPriorityPolicy.High;
                if (backup is null)
                {
                    backup = new(1, store.Machine, before.InstanceId, before.Identity, before.Priority, DateTimeOffset.UtcNow);
                    store.Save(backup);
                }

                using (store.Hold(backup))
                {
                    Gate(args.Restore, token);
                    var current = ReadSame(before);
                    if (current.Priority != before.Priority) return OperationResult.Fail("写入前的中断优先级已变化，原值备份保留。");
                    if (current.Priority != desired)
                    {
                        environment.Write(current, desired, args.Restore, () => attempted = true);
                        if (ReadSame(current).Priority != desired) throw new IOException("中断优先级写后回读不符。");
                    }
                }
                if (args.Restore) store.Archive(backup);
                return OperationResult.Ok(args.Restore ? "已核对原始中断优先级，请手动重启电脑。"
                    : "已保存并回读 High。请手动重启电脑，让 Windows 按新配置重新分配中断。", requiresReboot: true);
            });
        }
        catch (Exception ex)
        {
            return new OperationResult { Success = false, IsPartial = attempted, OutcomeUnknown = attempted,
                Message = "显卡中断设置未确认完成。" + ex.Message };
        }
    }

    public OperationResult RestoreAll(CancellationToken token) => store.Exclusive(() =>
    {
        var report = Inspect(true);
        var results = report.Items.Select(item => Change(new(item.Adapter.InstanceId, item.Fingerprint, true, true), token)).ToArray();
        return new OperationResult { Success = results.All(r => r.Success), OutcomeUnknown = results.Any(r => r.OutcomeUnknown),
            RequiresReboot = results.Any(r => r.RequiresReboot), IsPartial = results.Any(r => !r.Success),
            Message = results.Length == 0 ? report.Message : string.Join("\n", results.Select(r => r.Message)) };
    });

    private void Gate(bool restoring, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (environment.Blocker(restoring) is { } reason) throw new InvalidOperationException(reason);
    }

    private DisplayInterruptAdapter ReadSame(DisplayInterruptAdapter expected)
    {
        var actual = environment.Read().SingleOrDefault(d => SameId(d.InstanceId, expected.InstanceId));
        if (actual is not { Readable: true } || actual.Identity != expected.Identity || actual.Started != expected.Started)
            throw new IOException("设备或驱动身份已变化，原值备份保留。");
        return actual;
    }

    private static bool SameId(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

public static class DisplayInterruptPriorityService
{
    private static DisplayInterruptPriorityEngine Create() => new(DisplayInterruptPriorityStore.Default, new NativeEnvironment(),
        ReleaseFeaturePolicy.DisplayInterruptPriorityEnabled);

    public static DisplayInterruptPriorityReport List() => Create().List();
    public static OperationResult Change(DisplayInterruptPriorityArgs args, CancellationToken token)
    {
        using var target = GameTargetService.Default.BeginOperation();
        return Create().Change(args, token);
    }

    public static ReconcileSnapshot Reconcile()
    {
        var report = Create().List(backupsOnly: true);
        var complete = report.Complete && report.Items.All(item => item.Adapter.Readable);
        return new ReconcileSnapshot
        {
            Module = BackendModules.System, Summary = report.Message, Complete = complete,
            Items = report.Items.Select(item => new ReconcileItem { Id = item.Adapter.InstanceId, Kind = DisplayInterruptPriorityPolicy.RecoveryScope,
                HasBackup = true, State = item.CanRestore ? "可恢复" : "待核对", Detail = item.Detail }).ToArray(),
            RecoveryScopes = [new RecoveryScope { Id = DisplayInterruptPriorityPolicy.RecoveryScope,
                Description = "按备份恢复本功能改过的显卡中断优先级，随后手动重启", Available = complete && report.Items.Any(i => i.CanRestore),
                AffectedCount = report.BackupCount }],
        };
    }

    public static OperationResult RestoreAll(CancellationToken token)
    {
        using var target = GameTargetService.Default.BeginOperation();
        try { return Create().RestoreAll(token); }
        catch (Exception ex) { return OperationResult.Fail("显卡中断备份未能恢复。" + ex.Message); }
    }

    private sealed class NativeEnvironment : IDisplayInterruptEnvironment
    {
        public string? Blocker(bool restoring)
        {
            if (!ElevationHelper.IsElevated) return ElevationHelper.NotElevatedMessage;
            if (!restoring && OfflineModeGuard.BlocksNormalAutomation) return "请先完成已有的系统恢复核对。";
            return GameTargetService.Default.IsRunning() || DeltaForceLocator.IsGameRunning() ? "请退出游戏后再更改显卡中断优先级。" : null;
        }

        public IReadOnlyList<DisplayInterruptAdapter> Read() => DisplayInterruptInspection.Read();

        public void Write(DisplayInterruptAdapter expected, uint? priority, bool restoring, Action beforeMutation)
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var device = DisplayInterruptInspection.OpenDevice(root, expected.InstanceId);
            using var parameters = DisplayInterruptInspection.OpenParameters(device, writable: true);
            var actual = DisplayInterruptInspection.Read(root, device, expected.InstanceId, expected.Started, parameters);
            if (!actual.Readable || actual.Identity != expected.Identity || actual.Priority != expected.Priority)
                throw new IOException("同句柄写前核对失败。");
            if (Blocker(restoring) is { } blocker) throw new IOException(blocker);
            using var existing = parameters.OpenSubKey(DisplayInterruptInspection.PolicyPath, writable: true);
            beforeMutation();
            using var created = existing is null && priority.HasValue
                ? parameters.CreateSubKey(DisplayInterruptInspection.PolicyPath, writable: true) : null;
            var key = existing ?? created;
            if (priority is { } value)
                (key ?? throw new IOException("无法打开显卡中断优先级登记。")).SetValue(
                    DisplayInterruptInspection.ValueName, unchecked((int)value), RegistryValueKind.DWord);
            else key?.DeleteValue(DisplayInterruptInspection.ValueName, throwOnMissingValue: false);
            key?.Flush();
        }
    }
}
