namespace DeltaNFD.Services;

internal static class FrameStatusPresentation
{
    internal static bool PendulumPrepared(ProcmanStatusSnapshot? snapshot, DateTimeOffset now, string error) =>
        error.Length == 0 && snapshot is not null && snapshot.Module == DeltaNFD.Backends.BackendModules.Procman
        && snapshot.UpdatedUtc != default && snapshot.UpdatedUtc <= now.AddSeconds(2)
        && now - snapshot.UpdatedUtc <= TimeSpan.FromSeconds(10) && snapshot.OptimalClock?.Prepared == true;

    internal static string PendulumDescription(bool win10, string targetName = "三角洲") => (win10
        ? "开启后随帧格保持 DPC 在后台运行，持有已保存的精度，并在核验通过后忽略三角洲的精度请求。"
        : "开启后随帧格持有已保存的精度，并在核验通过后忽略三角洲的精度请求。").Replace("三角洲", targetName, StringComparison.Ordinal);

    internal static string Pendulum(bool active, ProcmanStatusSnapshot? snapshot, DateTimeOffset now, string error,
        string targetName = "三角洲", bool enabled = true)
    {
        if (!enabled)
        {
            if (snapshot?.FrameTimerIgnore.RestorePending == true || snapshot?.OptimalClock is { HasError: true, Holding: true })
                return "钟摆联动已停用，恢复仍待核对：" + (snapshot.FrameTimerIgnore.RestorePending
                    ? snapshot.FrameTimerIgnore.Message : snapshot.OptimalClock!.Message);
            if (snapshot?.FrameTimerIgnore.BackupCount > 0 || snapshot?.OptimalClock?.Holding == true)
                return "正在等待恢复游戏精度请求并释放钟摆，尚未确认完成";
            if (active && !PendulumPrepared(snapshot, now, error)) return "钟摆联动已停用，后台状态待核对";
            return "钟摆联动未启用";
        }
        if (!PendulumPrepared(snapshot, now, error)) return "";
        var clock = snapshot!.OptimalClock!;
        var target = $"{clock.SavedResolution100ns / 10000.0:0.0000} ms";
        if (clock.UsesGameDefault && !clock.HasError && !clock.Holding && snapshot.FrameTimerIgnore.BackupCount == 0)
            return "扫描最佳值 0.5000 ms · 沿用三角洲自身的计时器请求";
        if (!active) return $"已准备 · 对应精度 {target} · 等待开启帧格";
        if (clock.HasError || !clock.Holding) return "钟摆计划尚未生效：" + clock.Message;
        if (!snapshot.RulesEngineRunning || clock.UpdatedUtc == default || clock.UpdatedUtc > now.AddSeconds(2)
            || now - clock.UpdatedUtc > TimeSpan.FromSeconds(10)) return "等待后端核对钟摆持有状态，尚未确认生效";
        var verified = clock.ExperimentalWindows10 ? clock.DpcProcessVerified : clock.VerifiedGlobal;
        if (!verified || clock.Current100ns == 0) return "等待核对对应精度，尚未确认生效";
        var policy = snapshot.FrameTimerIgnore;
        var prefix = clock.ExperimentalWindows10 ? "DPC 后台运行；" : "";
        var policyStatus = TimerIgnore(true, true, snapshot, now, error, targetName);
        if (TimerSnapshotFresh(policy, now) && policy.PostApplyReachabilityConfirmed && policy.AfterPolicyClock100ns is > 0 and var after
            && policy.Maintaining && !policy.HasError && !policy.RestorePending
            && policy.InstanceCount > 0 && policy.AppliedCount == policy.InstanceCount)
        {
            var delta = (long)after - clock.SavedResolution100ns;
            var diagnostic = delta < -50 ? "仍有更细读数，未达到目标，来源未确认"
                : delta > 50 ? "读数比目标粗，未达到目标"
                : policy.BeforePolicyClock100ns is > 0 and var before && before + 50L < clock.SavedResolution100ns
                    ? $"忽略前 {before / 10000.0:0.0000} ms，复核读数已接近目标"
                    : "复核读数与目标相符";
            return prefix + $"已忽略{targetName}的精度请求（{policy.AppliedCount}/{policy.InstanceCount}）；"
                + $"目标 {target}，忽略后系统读数 {after / 10000.0:0.0000} ms：{diagnostic}。";
        }
        return prefix + $"钟摆目标 {target}，持有监测读数 {clock.Current100ns / 10000.0:0.0000} ms；" + policyStatus;
    }

    internal static string Priority(bool selected, bool active, ProcmanStatusSnapshot? snapshot, DateTimeOffset now, string error, string targetName = "三角洲")
        => PriorityCore(selected, active, snapshot, now, error, targetName);
    private static string PriorityCore(bool selected, bool active, ProcmanStatusSnapshot? snapshot, DateTimeOffset now, string error, string targetName)
    {
        var state = snapshot?.FramePriority;
        var pending = state is { RestorePending: true } && (state.GpuProtocolVersion == 0 || state.BackupCount > state.GpuBackupCount)
            || state is not null && state.BackupCount > state.GpuBackupCount && (!selected || !active);
        if (error.Length > 0) return pending ? $"{targetName}优先级恢复仍待核对；后端连接状态未知" : "优先级后端状态待确认，未确认锁定生效";
        if (!pending && (!selected || !active)) return selected ? "已选择 · 等待开启一键帧格模式" : "未启用";
        if (snapshot is null || snapshot.Module != DeltaNFD.Backends.BackendModules.Procman || snapshot.UpdatedUtc == default
            || snapshot.UpdatedUtc > now.AddSeconds(2) || now - snapshot.UpdatedUtc > TimeSpan.FromSeconds(10)
            || state is null || state.UpdatedUtc == default
            || state.UpdatedUtc > now.AddSeconds(2) || now - state.UpdatedUtc > TimeSpan.FromSeconds(10))
            return pending ? $"{targetName}优先级恢复仍待核对；等待后端更新" : "等待后端核对 CPU、IO 和内存优先级，未确认生效";
        if (pending && !state.RestorePending) return $"正在等待后端还原{targetName}优先级，尚未确认完成";
        if (!snapshot!.RulesEngineRunning && state.Maintaining) return $"监控已停止，{targetName}优先级恢复仍待核对";
        return state.Message;
    }
    internal static string TimerIgnore(bool selected, bool active, ProcmanStatusSnapshot? snapshot, DateTimeOffset now, string error, string targetName = "三角洲")
        => TimerIgnoreCore(selected, active, snapshot, now, error, targetName);
    private static string TimerIgnoreCore(bool selected, bool active, ProcmanStatusSnapshot? snapshot, DateTimeOffset now, string error, string targetName)
    {
        var state = snapshot?.FrameTimerIgnore;
        var pending = state is { RestorePending: true } || state is { BackupCount: > 0 } && (!selected || !active);
        var fresh = snapshot is not null && snapshot.Module == DeltaNFD.Backends.BackendModules.Procman
            && snapshot.UpdatedUtc != default && snapshot.UpdatedUtc <= now.AddSeconds(2)
            && now - snapshot.UpdatedUtc <= TimeSpan.FromSeconds(10)
            && state is not null && TimerSnapshotFresh(state, now);
        var reading = Reading(fresh && error.Length == 0 ? state!.SystemCurrentMilliseconds : null);
        string WithReading(string message) => message + reading;
        if (error.Length > 0)
            return WithReading(pending ? $"{targetName}定时器请求忽略状态恢复仍待核对；后端连接状态未知" : "进程管理后端状态待确认，未确认该功能生效");
        if (!pending && fresh && state is { Supported: false })
            return WithReading(state.ExperimentalWindows10
                ? !selected ? "未启用（Windows 10）" : state.PolicyAccepted == false ? state.Message : "Windows 10 等待策略能力核验，未确认生效"
                : selected ? "本系统未通过进程策略支持检查" : "未启用（本系统不支持该功能）");
        if (!pending && (!selected || !active)) return WithReading(selected ? "已选择 · 等待开启一键帧格模式" : "未启用");
        if (!fresh)
            return WithReading(pending ? $"{targetName}定时器请求忽略状态恢复仍待核对；等待后端更新" : $"等待后端核对{targetName}定时器请求忽略状态，未确认生效");
        if (pending && !state!.RestorePending) return WithReading($"正在等待后端恢复{targetName}定时器请求忽略状态，尚未确认完成");
        if (!snapshot!.RulesEngineRunning && state!.Maintaining) return WithReading($"监控已停止，{targetName}定时器请求忽略状态恢复仍待核对");
        if (state!.InstanceCount > 0 && !state.RestorePending)
        {
            if (state.IgnoreApplied && !state.HasError)
                return WithReading(state.PostApplyReachabilityConfirmed
                    ? $"已忽略{targetName}的精度请求（已复测时钟可达性）"
                    : $"已回读{targetName}的忽略策略，仍需复核忽略后的时钟读数");
            if (state.LegacyGlobalReachable == false)
                return WithReading($"已跳过忽略位：本系统按进程计时，忽略会让{targetName}回落到 15.625ms；{targetName}保留自身精度");
            if (state.LegacyGlobalReachable is null)
                return WithReading("系统时钟可达性未确认，未施加忽略位");
        }
        return WithReading(state!.Message);
    }

    private static bool TimerSnapshotFresh(FrameTimerIgnoreSnapshot state, DateTimeOffset now) => state.UpdatedUtc != default
        && state.UpdatedUtc <= now.AddSeconds(2) && now - state.UpdatedUtc <= TimeSpan.FromSeconds(10);

    /// <summary>只读展示：当前系统定时器分辨率实际读数（不改写、不申请、不释放）。</summary>
    private static string Reading(double? milliseconds) => milliseconds is { } value && double.IsFinite(value) && value > 0
        ? $"（当前系统定时器读数 {value:0.###} ms）" : "（当前系统定时器读数不可用）";

    internal static string Dwm(bool selected, bool active, CpuFrameStatusSnapshot? snapshot, DateTimeOffset now, string error)
    {
        if (!selected) return "未启用";
        if (!active) return "已选择 · 开启一键帧格模式后持续监测";
        if (!GameObservationState.Fresh(snapshot, now) || !snapshot!.MonitorRunning || error.Length > 0)
            return "DWM 后端状态待确认，未确认守护生效";
        var dwm = snapshot.Dwm;
        if (dwm.UpdatedUtc == default || dwm.UpdatedUtc > now.AddSeconds(2) || now - dwm.UpdatedUtc > TimeSpan.FromSeconds(10))
            return "DWM 采样状态待确认，等待后端更新";
        return dwm.Message + (dwm.CpuCorePercent is { } cpu ? $" · CPU {cpu:F1}%（单核口径）" : "")
            + (dwm.WorkingSetMiB is { } memory ? $" · 工作集 {memory:F0} MiB" : "");
    }

    internal static string Execution(CpuFrameStatusSnapshot? snapshot, DateTimeOffset now, string error) =>
        GameObservationState.Fresh(snapshot, now) && snapshot!.MonitorRunning ? "帧格监控运行中"
        : error.Length > 0 ? "帧格已配置 · 自动执行未启动：" + (error.Length > 160 ? error[..160] + "…" : error)
        : "帧格已配置 · 自动执行尚未确认";
    internal static string Game(bool? running, string target) => running switch
    {
        true => $" · {target}运行中", false => $" · 等待 {target} 启动", _ => " · 游戏运行状态待确认",
    };
    internal static string Power(bool enabled, string target, CpuFrameStatusSnapshot? snapshot, DateTimeOffset now) =>
        !enabled ? "" : GameObservationState.Fresh(snapshot, now) && snapshot!.PowerLockActive
            ? string.IsNullOrWhiteSpace(target) ? " · 电源计划锁定已生效" : " · 电源计划锁定已生效（指定计划）"
            : " · 电源锁定已配置，尚未确认生效";
}
