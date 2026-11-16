using System.Runtime.InteropServices;
using DeltaNFD.Inspection;

namespace DeltaNFD.Services;

internal interface IClockEnvironment
{
    string? Blocker(bool restoring);
    ClockEnvironmentSystem Read();
    void WriteSource(string entryId, string key, string value);
    void SetDevice(string id, string identity, bool disabled);
}

internal sealed class ClockEnvironmentEngine(ClockEnvironmentStore store, IClockEnvironment environment)
{
    public ClockEnvironmentStatus Read() => ClockEnvironmentInspection.Evaluate(environment.Read(), store.Read(), store.Sid);
    public OperationResult Change(ClockEnvironmentArgs args, CancellationToken token = default)
    {
        var submitted = false; ClockEnvironmentBackup? journal = null;
        try
        {
            if (!Enum.IsDefined(args.Action) || !args.Confirmed || !ClockEnvironmentPolicy.ValidHash(args.Fingerprint))
                return OperationResult.Fail("计时源操作或确认参数无效。");
            using var lease = store.Lease(writing: true);
            var restoring = ClockEnvironmentPolicy.IsRestore(args.Action); var source = ClockEnvironmentPolicy.IsSource(args.Action);
            var kind = source ? "source" : "hpet";
            void Guard() { token.ThrowIfCancellationRequested(); if (environment.Blocker(restoring) is { } error) throw new IOException(error); }
            Guard(); var selected = Read();
            if (selected.Fingerprint != args.Fingerprint) return OperationResult.Fail("计时源、设备或备份已变化，请刷新后重新确认。");
            var allowed = args.Action switch
            {
                ClockEnvironmentAction.RestoreDefaultSource => selected.CanDefaultSource,
                ClockEnvironmentAction.DisableHpet => selected.CanDisableHpet,
                ClockEnvironmentAction.RestoreSource => selected.CanRestoreSource,
                ClockEnvironmentAction.RestoreHpet => selected.CanRestoreHpet,
                _ => false,
            };
            if (!allowed || selected.System is not { BootId.Length: > 0 } current) return OperationResult.Fail("当前平台、原始备份或设备状态不允许执行此操作。请刷新核对。");
            var previous = store.Read().SingleOrDefault(r => r.Kind == kind);
            if (restoring)
            {
                journal = previous ?? throw new IOException("没有匹配的原始备份。");
                journal = journal with { Phase = "Restoring", BootId = current.BootId, UpdatedUtc = DateTimeOffset.UtcNow,
                    Steps = journal.Steps.Select(s => s with { Verified = false }).ToArray() };
            }
            else
            {
                if (previous is not null) store.PreservePrevious(previous);
                var bcd = source ? current.Bcd ?? throw new IOException("当前启动项不可读，未修改计时源。") : null;
                var steps = new List<ClockEnvironmentStep>();
                if (bcd is not null) steps.AddRange([
                    new("useplatformclock", bcd.EntryId, bcd.Clock, ""),
                    new("useplatformtick", bcd.EntryId, bcd.Tick, ""),
                ]);
                if (!source) steps.AddRange(current.Devices
                    .Select(d => new ClockEnvironmentStep(d.InstanceId, d.Identity, d.Disabled ? "disabled" : "enabled", "disabled")));
                journal = new()
                {
                    Schema = source ? 1 : 4, Kind = kind, Machine = store.Machine, Sid = store.Sid,
                    EntryId = bcd?.EntryId ?? "", Inheritance = bcd?.Inheritance ?? "", BootId = current.BootId, Steps = steps,
                };
            }
            // Saving an unfinished journal first closes clock admission even if the process stops before the first device/BCD call.
            store.Save(journal);
            // A linked rollback enables owned devices before restoring any boot override.
            var order = Enumerable.Range(0, journal.Steps.Count);
            if (restoring && journal.Kind == "hpet" && journal.HasSource) order = order.Reverse();
            void CheckDependencies(ClockEnvironmentSystem state, bool stepSource)
            {
                if (!ClockEnvironmentInspection.RecordMatches(state, journal, allowOriginal: true))
                    throw new IOException("设备、驱动或计时源存在外部变更，未覆盖。");
                if (restoring && stepSource && journal.Kind == "hpet"
                    && journal.Steps.Where(s => !ClockEnvironmentPolicy.IsBcdKey(s.Key))
                        .Any(s => ClockEnvironmentInspection.Current(state, journal, s) != s.Original))
                    throw new IOException("HPET 设备尚未全部恢复，未还原计时源覆盖。");
            }
            foreach (var i in order)
            {
                Guard(); var step = journal.Steps[i]; var fresh = environment.Read();
                if (fresh.Machine != store.Machine || fresh.BootId != current.BootId) throw new IOException("本机或启动身份发生变化。");
                var stepSource = ClockEnvironmentPolicy.IsBcdKey(step.Key);
                CheckDependencies(fresh, stepSource);
                var value = ClockEnvironmentInspection.Current(fresh, journal, step);
                var desired = restoring ? step.Original : step.Applied;
                if (value is null || (value != step.Original && value != step.Applied)) throw new IOException("设备、驱动或计时源存在外部变更，未覆盖。");
                if (value != desired)
                {
                    var steps = journal.Steps.ToArray(); steps[i] = step with { Attempted = true, Verified = false };
                    journal = journal with { Phase = restoring ? "Restoring" : "Writing", UpdatedUtc = DateTimeOffset.UtcNow, Steps = steps };
                    store.Save(journal);
                    using (store.Hold(journal))
                    {
                        Guard(); fresh = environment.Read();
                        CheckDependencies(fresh, stepSource);
                        if (ClockEnvironmentInspection.Current(fresh, journal, step) != value) throw new IOException("提交前状态已经变化，未执行。");
                        if (!restoring && !stepSource && fresh.Devices.Single(d => d.InstanceId.Equals(step.Key, StringComparison.OrdinalIgnoreCase)).CanDisable == false)
                            throw new IOException("设备当前不允许禁用，未强制执行。");
                        submitted = true;
                        if (stepSource) environment.WriteSource(journal.EntryId, step.Key, desired);
                        else environment.SetDevice(step.Key, step.Identity, desired == "disabled");
                    }
                }
                Guard(); var after = environment.Read();
                if (ClockEnvironmentInspection.Current(after, journal, step) != desired) throw new IOException("写后回读未确认，原始记录保留。");
                var verifiedSteps = journal.Steps.ToArray(); verifiedSteps[i] = verifiedSteps[i] with { Verified = true };
                journal = journal with { Steps = verifiedSteps, UpdatedUtc = DateTimeOffset.UtcNow }; store.Save(journal);
            }
            Guard(); var end = environment.Read();
            var done = journal with { Phase = restoring ? "Restored" : "Applied", UpdatedUtc = DateTimeOffset.UtcNow };
            if (!ClockEnvironmentInspection.RecordMatches(end, done, false)) throw new IOException("完整结果与预期不一致，记录保留。");
            store.Save(done);
            return OperationResult.Ok((restoring ? "已按原始备份恢复" : source ? "已移除两项平台计时源覆盖，恢复 Windows 自动选择" : "已确认所列 HPET 设备已禁用，未修改 BCD 启动计时源")
                + "并完成回读。动态节拍未修改。请手动重启电脑，再重新扫描或验证钟摆精度。", requiresReboot: done.Attempted);
        }
        catch (Exception ex)
        {
            return new() { Success = false, IsPartial = submitted, OutcomeUnknown = submitted,
                RequiresReboot = submitted || journal?.Attempted == true,
                Message = "计时源操作未完整完成：" + ex.Message + " 原始状态及逐项记录保留，请刷新核对；不会自动重放。" };
        }
    }
    public OperationResult RestoreAll(CancellationToken token)
    {
        var messages = new List<string>(); var ok = true; var reboot = false; var partial = false;
        foreach (var action in new[] { ClockEnvironmentAction.RestoreHpet, ClockEnvironmentAction.RestoreSource })
        {
            try
            {
                var status = Read();
                if (!(ClockEnvironmentPolicy.IsSource(action) ? status.SourceBackup : status.HpetBackup)) continue;
                var result = Change(new(action, status.Fingerprint, true), token); messages.Add(result.Message);
                ok &= result.Success; reboot |= result.RequiresReboot; partial |= result.IsPartial || result.OutcomeUnknown;
            }
            catch (Exception ex) { ok = false; messages.Add(ex.Message); }
        }
        return new() { Success = ok, RequiresReboot = reboot, IsPartial = partial, OutcomeUnknown = !ok && partial,
            Message = messages.Count == 0 ? "没有待恢复的计时源或 HPET 原始备份。" : string.Join("\n", messages) };
    }
}

public static class ClockEnvironmentService
{
    private static ClockEnvironmentEngine Engine => new(ClockEnvironmentStore.Default, new WindowsClockEnvironment());
    public static OperationResult Change(ClockEnvironmentArgs args, CancellationToken token) => Engine.Change(args, token);
    public static OperationResult RestoreAll(CancellationToken token) => Engine.RestoreAll(token);
}

internal sealed class WindowsClockEnvironment : IClockEnvironment
{
    public string? Blocker(bool restoring)
    {
        if (!restoring && !ReleaseFeaturePolicy.PendulumEnabled) return "当前通道未开放钟摆计划。";
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) || !Environment.Is64BitProcess) return "此功能仅支持 Windows 10 1809 起的 x64 系统。";
        if (!ElevationHelper.IsElevated) return ElevationHelper.NotElevatedMessage;
        if (!restoring && OfflineModeGuard.BlocksNormalAutomation) return "恢复流程中，暂不能修改计时源或设备状态。";
        try
        {
            if (AppSettingsStore.ReadRequired().FrameModeActive) return "请先关闭帧格，再操作计时源或 HPET。";
            if (GameTargetService.Default.IsRunning() || DeltaForceLocator.IsGameRunning()) return "请先退出目标游戏与三角洲。";
        }
        catch { return "无法确认帧格或游戏状态，未执行计时源操作。"; }
        return null;
    }
    public ClockEnvironmentSystem Read() => ClockEnvironmentInspection.ReadSystem();
    public void WriteSource(string entryId, string key, string value)
        => ClockEnvironmentInspection.QueryBcd(SourceArguments(entryId, key, value));
    internal static string[] SourceArguments(string entryId, string key, string value)
    {
        if (!ClockEnvironmentPolicy.EntryId(entryId) || key is not ("useplatformclock" or "useplatformtick") || !ClockEnvironmentPolicy.BcdValue(value))
            throw new IOException("启动计时源写入不在固定范围内。");
        return value.Length == 0 ? ["/deletevalue", entryId, key] : ["/set", entryId, key, value == "no" ? "false" : "true"];
    }
    public void SetDevice(string id, string identity, bool disabled)
    {
        if (!ClockEnvironmentPolicy.ValidDevice(id) || !ClockEnvironmentPolicy.ValidHash(identity)) throw new IOException("HPET 设备参数无效。");
        var fresh = ClockEnvironmentInspection.ReadDevice(id);
        if (fresh.Identity != identity || disabled && !fresh.CanDisable || !disabled && !fresh.Disabled) throw new IOException("HPET 设备身份或状态已变化。");
        if (ClockEnvironmentInspection.CM_Locate_DevNodeW(out var node, id, 0) != 0) throw new IOException("HPET 设备不再可用。");
        var result = disabled ? CM_Disable_DevNode(node, 8) : CM_Enable_DevNode(node, 0);
        if (result != 0) throw new IOException("设备操作返回 0x" + result.ToString("X") + "，未强制操作。");
    }
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Disable_DevNode(uint node, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Enable_DevNode(uint node, uint flags);
}
