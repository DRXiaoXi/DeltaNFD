using System.Diagnostics;
using DeltaNFD.Native;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.CpuFrame;

/// <summary>
/// CPU/帧格后端执行引擎（T4）：持有唯一的「两秒共享游戏监控」，并在每个 tick 按当前偏好维护
/// 实际生效的优先级 / 硬亲和性 / CPU Sets / 双单 CCD / 计时器 / DWM /
/// 电源守护 / 临时优化。前端不再执行这些写入，只通过状态快照读取「实际生效状态」，
/// 并在需要立即生效时调用本引擎的命令。
///
/// 每次操作前都用 <see cref="GameTargetService"/> 重新复核目标代次、PID、启动时间与可执行文件路径；
/// 代次变化或身份不符时立即跳过该进程，绝不对旧目标/复用 PID 施加优化。
/// 后端只按内置设置与合同参数执行，不接收前端传入的任意注册表路径、脚本或 shell 命令。
/// </summary>
public sealed class CpuFrameEngine : IDisposable
{
    private const string DwmProcessName = "dwm";
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PowerGuardInterval = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _automationGate = new(1, 1);
    private readonly object _observationGate = new();
    internal Func<bool?>? DeltaReaderForTests { get; set; }
    internal Func<DateTimeOffset>? ClockForTests { get; set; }
    internal Func<CancellationToken, Task>? TickForTests { get; set; }
    private readonly SemaphoreSlim _powerGate = new(1, 1);
    private readonly CpuTopologyService _cpu;
    private readonly FrameTweaksService _tweaks;
    private readonly IPowerService _power;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    // ---- 实际生效状态（只由本引擎写入；快照只读暴露） ----
    private bool _monitorRunning;
    private string _lastAction = "";
    private string _lastError = "";
    private long _targetGeneration;
    private string _targetDisplay = "";
    private bool _targetCustom;
    private string _targetExe = "";
    private bool _gameRunning;
    private sealed record DeltaObservation(bool? Running, DateTimeOffset ObservedUtc);
    private DeltaObservation? _deltaObservation;
    private int? _gamePid;
    private long _gameStartTicks;
    private string _gameExe = "";
    private int _gameInstances;
    private bool _priorityEnabled;
    private int _priorityAppliedCount;
    private ulong _affinityMask;
    private bool _timerActive;
    private bool _dwmHandledForSession;
    private bool _tempOptimizationApplied;
    private bool _powerLockActive;
    private string _powerLockedGuid = "";
    private bool _powerLockReady;
    private int _powerGuardCount;

    // 显式由前端命令设定的临时优化/加速响应期望（机箱形态等编排由前端决定）。
    private bool _boostTimer;
    private bool _boostGamePower;
    private string _gamePowerIdentityError = "";
    private readonly GamePowerThrottling _gamePower;
    private bool _tweaksDesired;
    private CpuFrameTempArgs _tweaksArgs = new();

    private readonly Dictionary<int, (GameProcessIdentity Identity, ProcessPriorityClass Original)> _priorityOriginals = [];

    public CpuFrameEngine() : this(new PowerService(), new FrameTweaksService()) { }

    internal CpuFrameEngine(IPowerService power, FrameTweaksService tweaks)
    {
        _power = power; _tweaks = tweaks;
        _gamePower = new GamePowerThrottling(Path.Combine(AppDataPaths.Root, "frame-game-power.json"), DeltaNFD.Execution.Procman.ProcessPropertyGate.Default);
        _cpu = new CpuTopologyService(TweakBackupStore.Default, _power);
    }

    // ---------------- 监控生命周期 ----------------

    public async Task<OperationResult> StartMonitorAsync()
    {
        lock (_gate)
        {
            if (_monitorRunning) return OperationResult.Ok("游戏监控已在运行。");
            _cts = new CancellationTokenSource();
            _monitorRunning = true;
            _dwmHandledForSession = false;
            var token = _cts.Token;
            _loop = Task.Run(() => RunLoopAsync(token));
        }
        SetAction("游戏监控已启动。");
        await Task.CompletedTask;
        return OperationResult.Ok("游戏监控已启动。");
    }

    public async Task<OperationResult> StopMonitorAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
            _monitorRunning = false;
        }
        if (cts is not null)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            if (loop is not null) { try { await loop; } catch (OperationCanceledException) { } }
            cts.Dispose();
        }
        var restored = _gamePower.Sync([]);
        _gamePowerIdentityError = "";
        SetAction("游戏监控已停止。");
        return restored ? OperationResult.Ok("游戏监控已停止，游戏节能原值已恢复。") : OperationResult.Fail(_gamePower.Status);
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await SerializeAutomationAsync(async () =>
            {
                token.ThrowIfCancellationRequested();
                if (TickForTests is { } tick) await tick(token).ConfigureAwait(false);
                else await TickAsync(token).ConfigureAwait(false);
                return true;
            }, cancellationToken: token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                SetError("监控 tick 异常：" + ex.Message);
                Log.Error("CPU/帧格后台监控 tick 异常", ex);
            }
            try { await Task.Delay(TickInterval, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Commands and the maintenance tick must never apply/revert the same policy concurrently.
    // Stop cancels queued ticks before waiting for the current mutation to finish.
    public async Task<T> SerializeAutomationAsync<T>(Func<Task<T>> operation, bool stopMonitor = false,
        CancellationToken cancellationToken = default)
    {
        if (stopMonitor) await StopMonitorAsync().ConfigureAwait(false);
        await _automationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await operation().ConfigureAwait(false); }
        finally { _automationGate.Release(); }
    }

    private async Task TickAsync(CancellationToken token)
    {
        // 目标切换同步：后端与前端共享同一份设置，但目标代次必须在后端进程内递增并重新复核。
        var settings = AppSettingsStore.Read();
        await SyncTargetIfChangedAsync(settings).ConfigureAwait(false);

        // 目标身份复核：只在当前代次内操作目标进程。
        using var targetOperation = GameTargetService.Default.BeginOperation();
        var generation = GameTargetService.Default.Current.Generation;
        var identities = RefreshIdentities(generation);

        _targetGeneration = generation;
        _targetDisplay = GameTargetService.Default.Current.DisplayName;
        _targetCustom = GameTargetService.Default.Current.IsCustom;
        _targetExe = GameTargetService.Default.Current.ExecutablePath;
        _gameInstances = identities.Count;
        var main = identities.FirstOrDefault();
        _gameRunning = main is not null;
        _gamePid = main?.Id;
        _gameStartTicks = main?.StartTimeUtc.Ticks ?? 0;
        _gameExe = main?.ExecutablePath ?? "";

        if (OfflineModeGuard.BlocksNormalAutomation)
        {
            _gamePower.Sync([]);
            if (_monitorRunning) SetAction("脱机模式或恢复流程中：普通帧格自动化已暂停。");
            return;
        }

        var frameActive = settings.FrameModeActive;
        var gameRunning = identities.Count > 0;

        // ① 自动优先级（按设置；关闭时还原原值）
        _priorityEnabled = settings.GamePriorityEnabled;
        if (_priorityEnabled && gameRunning) ApplyPriorityCore(identities);
        else RestorePriorities();

        // ② 硬亲和性规则（CPU 实验室；仅目标游戏进程，持续保持）
        _affinityMask = settings.GameAffinityRuleEnabled ? settings.GameAffinityRuleMask : 0UL;
        if (_affinityMask != 0) CpuTopologyService.ApplyAffinityRuleTick(_affinityMask);
        else CpuTopologyService.RestoreAffinityRule();

        // ③ 双 CCD / 单 CCD 调度持续维持（互斥：先撤销不再需要的，再应用需要的）
        var dualWanted = frameActive && settings.DualCcdArmed && settings.DualCcdFrameEnabled;
        var singleWanted = !dualWanted && settings.SingleCcdExcludeCpu0Enabled;
        if (!dualWanted && CpuTopologyService.DualCcdSchedulingActive)
            await _cpu.RevertDualCcdSchedulingAsync().ConfigureAwait(false);
        if (!singleWanted && CpuTopologyService.SingleCcdExcludeActive)
            await _cpu.RestoreGameFullCoresAsync().ConfigureAwait(false);
        if (dualWanted && !CpuTopologyService.DualCcdSchedulingActive)
            await _cpu.ApplyDualCcdSchedulingAsync(settings.DualCcdGameCcdIndex).ConfigureAwait(false);
        else if (singleWanted && !CpuTopologyService.SingleCcdExcludeActive)
            await _cpu.ExcludeCpu0FromGameAsync().ConfigureAwait(false);
        CpuTopologyService.ApplyCpuSchedulingTick();

        // ④ 加速系统响应模式：高分辨率计时器（随游戏启停换挡）
        UpdateTimer(_boostTimer && gameRunning);
        UpdateGamePower(settings, identities);

        // ⑤ 游戏启动后自动重启 DWM（延迟 5 秒，避免初始化窗口）
        if (!gameRunning) _dwmHandledForSession = false;
        if (frameActive && settings.DwmRestartOnGameStart && gameRunning && !_dwmHandledForSession
            && EmergencyPolicies.BlockReason(DeltaNFD.Backends.BackendCommands.CpuFrameDwmRestart) is null
            && GetGameUptime(identities) >= TimeSpan.FromSeconds(EmergencyPolicies.DwmDelaySeconds))
        {
            var result = RestartDwmCore();
            _dwmHandledForSession = true;
            SetAction(result.Success ? "游戏启动：已自动重启 DWM。" : "游戏启动：自动重启 DWM 失败（可手动重试）。");
        }

        // ⑥ 帧格电源锁定与内容守护
        await _powerGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
        settings = AppSettingsStore.Read();
        frameActive = settings.FrameModeActive && !OfflineModeGuard.BlocksNormalAutomation;
        if (frameActive && settings.FramePowerLockEnabled)
        {
            if (!_powerLockReady || !_powerLockActive)
                await ApplyPowerLockCoreAsync(settings, resume: true).ConfigureAwait(false);
            else if (++_powerGuardCount >= 15)
            {
                _powerGuardCount = 0;
                await GuardPowerLockCoreAsync(settings, token).ConfigureAwait(false);
            }
        }
        else if (_powerLockActive)
        {
            await RevertPowerLockCoreAsync(settings).ConfigureAwait(false);
        }
        }
        finally { _powerGate.Release(); }

        // ⑦ 帧格临时优化（期望由前端命令设定；备份丢失时自动补应用）
        if (_tweaksDesired && !_tempOptimizationApplied)
            await ApplyTweaksCoreAsync(_tweaksArgs).ConfigureAwait(false);
        else if (!_tweaksDesired && _tempOptimizationApplied)
            await RevertTweaksCoreAsync().ConfigureAwait(false);

        // ⑧ 内存清理（实验性；帧格激活 + 游戏运行 + 达到阈值 + 冷却已过）
    }

    /// <summary>
    /// 前端在另一个进程切换目标后，把设置里的目标同步到本进程的 <see cref="GameTargetService"/>：
    /// 代次自增并重新解析进程名/路径，随后的每次操作都按新代次复核。目标未变则不动。
    /// </summary>
    private async Task SyncTargetIfChangedAsync(AppSettings settings)
    {
        try
        {
            var current = GameTargetService.Default.Current;
            var wantCustom = settings.CustomGameModeEnabled;
            var wantPath = wantCustom ? (settings.CustomGameExecutablePath ?? "") : "";
            if (current.IsCustom == wantCustom && SamePath(current.ExecutablePath, wantPath)) return;
            var result = await GameTargetService.Default.ChangeAsync(wantCustom, wantCustom ? wantPath : null).ConfigureAwait(false);
            if (result.Success)
                Log.Info($"CPU/帧格后端：目标已同步为 {(wantCustom ? wantPath : "三角洲")}（代次 {GameTargetService.Default.Current.Generation}）");
            else
            {
                _lastError = "目标同步被拒绝：" + result.Message;
                Log.Warn("CPU/帧格后端：目标同步被拒绝：" + result.Message);
            }
        }
        catch (Exception ex)
        {
            _lastError = "目标同步失败：" + ex.Message;
            Log.Warn("CPU/帧格后端：目标同步失败：" + ex.Message);
        }
    }

    private static bool SamePath(string left, string right)
    {
        if (string.IsNullOrEmpty(left) && string.IsNullOrEmpty(right)) return true;
        try
        {
            return Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private List<GameProcessIdentity> RefreshIdentities(long generation)
    {
        var result = new List<GameProcessIdentity>();
        foreach (var process in GameTargetService.Default.GetProcesses(forceRefresh: true))
        {
            using (process)
            {
                var identity = GameTargetService.Default.Identify(process);
                if (identity is not null && identity.Generation == generation) result.Add(identity);
            }
        }
        return result;
    }

    // ---------------- 优先级 ----------------

    private void ApplyPriorityCore(IReadOnlyList<GameProcessIdentity> identities)
    {
        var applied = 0;
        foreach (var identity in identities)
        {
            try
            {
                using var process = Process.GetProcessById(identity.Id);
                if (!GameTargetService.Default.Matches(process, identity)) continue;
                using var ownership = DeltaNFD.Execution.Procman.ProcessPropertyGate.TryEnterGame(identity.Id, "priority");
                if (ownership is null) continue;
                if (_priorityOriginals.TryGetValue(identity.Id, out var old) && old.Identity.StartTimeUtc != identity.StartTimeUtc)
                    _priorityOriginals.Remove(identity.Id);
                if (process.PriorityClass != ProcessPriorityClass.High)
                {
                    _priorityOriginals.TryAdd(identity.Id, (identity, process.PriorityClass));
                    process.PriorityClass = ProcessPriorityClass.High;
                }
                applied++;
            }
            catch (ArgumentException) { _priorityOriginals.Remove(identity.Id); }
            catch (Exception ex) { SetError("优先级提升失败：" + ex.Message); }
        }
        _priorityAppliedCount = applied;
        GameTargetService.Default.SetRestoreFailure("自动优先级", _priorityOriginals.Count != 0 && !_priorityEnabled);
    }

    public OperationResult ApplyPriorityAsync(bool enabled)
    {
        _priorityEnabled = enabled;
        if (!enabled)
        {
            // 安全门禁：目标已是 High 但没有本工具原值备份时拒绝宣称已还原（与关闭前的行为一致）。
            foreach (var identity in RefreshIdentities(GameTargetService.Default.Current.Generation))
            {
                try
                {
                    using var process = Process.GetProcessById(identity.Id);
                    if (process.PriorityClass != ProcessPriorityClass.High) continue;
                    lock (_gate)
                    {
                        if (!_priorityOriginals.TryGetValue(identity.Id, out var saved) || saved.Identity.StartTimeUtc != identity.StartTimeUtc)
                            return OperationResult.Fail($"目标 PID {identity.Id} 已是 High，但没有本工具原优先级备份；未关闭自动优先级。");
                    }
                }
                catch (Exception ex)
                {
                    return OperationResult.Fail($"无法核对目标 PID {identity.Id} 的优先级：{ex.Message}");
                }
            }
            RestorePriorities();
            return OperationResult.Ok("自动优先级已关闭，已还原原优先级。");
        }
        var identities = RefreshIdentities(GameTargetService.Default.Current.Generation);
        if (identities.Count == 0) return OperationResult.Ok("自动优先级已就绪：目标游戏启动后由监控自动提升。");
        ApplyPriorityCore(identities);
        SetAction($"已提升 {_priorityAppliedCount} 个目标实例的优先级为 High。");
        return OperationResult.Ok($"已提升 {_priorityAppliedCount} 个目标实例的优先级为 High。");
    }

    private void RestorePriorities()
    {
        foreach (var (id, saved) in _priorityOriginals.ToArray())
        {
            try
            {
                using var process = Process.GetProcessById(id);
                if (process.HasExited || process.StartTime.ToUniversalTime() != saved.Identity.StartTimeUtc)
                {
                    _priorityOriginals.Remove(id);
                    continue;
                }
                if (!GameTargetService.Default.Matches(process, saved.Identity)) continue;
                using var ownership = DeltaNFD.Execution.Procman.ProcessPropertyGate.TryEnterGame(id, "priority", true);
                if (ownership is null) continue;
                if (process.PriorityClass == ProcessPriorityClass.High) process.PriorityClass = saved.Original;
                _priorityOriginals.Remove(id);
            }
            catch (ArgumentException) { _priorityOriginals.Remove(id); }
            catch { }
        }
        _priorityAppliedCount = 0;
        GameTargetService.Default.SetRestoreFailure("自动优先级", _priorityOriginals.Count != 0);
    }

    // ---------------- CPU 调度（亲和性 / CCD / 单 CCD） ----------------

    public async Task<OperationResult> ApplyAffinityAsync(ulong mask)
    {
        _affinityMask = mask;
        if (mask == 0)
        {
            CpuTopologyService.RestoreAffinityRule();
            SetAction("CPU 亲和性规则已解除，目标游戏恢复默认亲和性。");
            return OperationResult.Ok("CPU 亲和性规则已解除。");
        }
        CpuTopologyService.ApplyAffinityRuleTick(mask);
        SetAction($"CPU 亲和性规则已应用（掩码 0x{mask:X}）。");
        await Task.CompletedTask;
        return OperationResult.Ok($"CPU 亲和性规则已应用（掩码 0x{mask:X}）。");
    }

    /// <summary>只读：能否安全地按原值还原亲和性（供脱机切换在写入前复核归属）。</summary>
    public (bool Ok, string Reason) CanRestoreAffinityRule(ulong expectedRuleMask)
        => CpuTopologyService.CanSafelyRestoreAffinityRuleForOffline(expectedRuleMask, out var reason)
            ? (true, "")
            : (false, reason);

    /// <summary>只读：是否存在待还原的亲和性原值记录。</summary>
    public bool HasAffinityRuleRestorePending() => CpuTopologyService.HasAffinityRuleRestorePending;

    public async Task<OperationResult> ApplyDualCcdAsync(int gameCcdIndex)
    {
        var result = await _cpu.ApplyDualCcdSchedulingAsync(gameCcdIndex).ConfigureAwait(false);
        SetResult("双CCD调度", result);
        return result;
    }

    public async Task<OperationResult> RevertDualCcdAsync()
    {
        var result = await _cpu.RevertDualCcdSchedulingAsync().ConfigureAwait(false);
        SetResult("撤销双CCD调度", result);
        return result;
    }

    public async Task<OperationResult> ApplySingleCcdAsync()
    {
        var result = await _cpu.ExcludeCpu0FromGameAsync().ConfigureAwait(false);
        SetResult("单CCD排除CPU0", result);
        return result;
    }

    public async Task<OperationResult> RevertSingleCcdAsync()
    {
        var result = await _cpu.RestoreGameFullCoresAsync().ConfigureAwait(false);
        SetResult("恢复单CCD", result);
        return result;
    }

    public Task<CpuTopology> GetTopologyAsync() => _cpu.GetTopologyAsync();

    public Task<(bool Detected, int RemovedMb, string RawOutput)> DetectMemoryLimitAsync() => _cpu.DetectMemoryLimitAsync();

    public async Task<OperationResult> RemoveMemoryLimitAsync()
    {
        var result = await _cpu.RemoveMemoryLimitAsync().ConfigureAwait(false);
        SetResult("移除内存限制", result);
        return result;
    }

    // ---------------- 高分辨率计时器（加速响应） ----------------

    /// <summary>设定计时器期望，随后随游戏启停维持。</summary>
    public Task<OperationResult> ApplyBoostAsync(bool timer, bool gamePower = false)
    {
        _boostTimer = timer;
        _boostGamePower = gamePower;
        var gameRunning = RefreshIdentities(GameTargetService.Default.Current.Generation).Count > 0;
        UpdateTimer(timer && gameRunning);
        var result = UpdateGamePower(AppSettingsStore.Read(), RefreshIdentities(GameTargetService.Default.Current.Generation));
        SetAction($"加速响应已更新（计时器={(timer ? "开" : "关")}，游戏禁电源限制={(gamePower ? "开" : "关")}）。");
        return Task.FromResult(result ? OperationResult.Ok("加速响应选项已更新。") : OperationResult.Fail(_lastError));
    }

    private bool UpdateGamePower(AppSettings settings, IReadOnlyList<GameProcessIdentity> games)
    {
        var wanted = _boostGamePower && settings.FrameModeActive && settings.FrameResponseBoostEnabled
            && settings.FrameGamePowerThrottlingDisabled && !OfflineModeGuard.BlocksNormalAutomation;
        var identities = new List<DeltaNFD.Execution.Procman.ProcmanProcessIdentity>();
        foreach (var game in wanted ? games : Array.Empty<GameProcessIdentity>())
        {
            var identity = DeltaNFD.Execution.Procman.ProcmanIdentity.TryIdentify(game.Id);
            if (identity is not null && identity.StartTimeUtc == game.StartTimeUtc
                && game.Generation == GameTargetService.Default.Current.Generation
                && (string.IsNullOrEmpty(game.ExecutablePath) || SamePath(identity.ExecutablePath, game.ExecutablePath))) identities.Add(identity);
        }
        _gamePowerIdentityError = wanted && identities.Count != games.Count ? "部分目标身份无法确认，未对这些进程写入。" : "";
        var ok = _gamePower.Sync(identities) && _gamePowerIdentityError.Length == 0;
        if (!ok) SetError(_gamePowerIdentityError.Length > 0 ? _gamePowerIdentityError : _gamePower.Status);
        return ok;
    }

    /// <summary>设定高分辨率计时器期望，并立即按游戏运行状态更新请求。</summary>
    public async Task<OperationResult> ApplyTimerAsync(bool enabled)
    {
        _boostTimer = enabled;
        var identities = RefreshIdentities(GameTargetService.Default.Current.Generation);
        UpdateTimer(enabled && identities.Count > 0);
        await Task.CompletedTask;
        if (!enabled) return OperationResult.Ok("已释放高分辨率计时器请求。");
        return _timerActive
            ? OperationResult.Ok("已请求高分辨率系统计时器（0.5ms）。")
            : OperationResult.Fail("请求高分辨率计时器失败。");
    }

    private void UpdateTimer(bool wanted)
    {        if (wanted && !_timerActive)
        {
            var request = ProcessPerf.RequestTimerResolutionHighDetailed();
            _timerActive = request.Succeeded;
            if (!_timerActive) SetError("请求高分辨率计时器失败。");
        }
        else if (!wanted && _timerActive)
        {
            var release = ProcessPerf.ReleaseTimerResolutionDetailed();
            _timerActive = !release.Succeeded;
        }
    }

    // ---------------- DWM ----------------

    public OperationResult RestartDwmAsync() => RestartDwmCore();

    private static OperationResult RestartDwmCore()
    {
        if (EmergencyPolicies.BlockReason(DeltaNFD.Backends.BackendCommands.CpuFrameDwmRestart) is { } emergencyReason)
            return OperationResult.Fail(emergencyReason);
        try
        {
            var processes = Process.GetProcessesByName(DwmProcessName);
            if (processes.Length == 0) return OperationResult.Fail("未找到 DWM 进程。");
            var killed = 0;
            foreach (var process in processes)
            {
                using (process)
                {
                    try { process.Kill(); killed++; }
                    catch { /* 个别实例失败可接受 */ }
                }
            }
            return killed > 0
                ? OperationResult.Ok("DWM 已结束，系统会立即自动重启它（屏幕会闪一下，属正常现象）。")
                : OperationResult.Fail("结束 DWM 失败，请确认以管理员身份运行。");
        }
        catch (Exception ex) { return OperationResult.Fail($"重启 DWM 失败：{ex.Message}"); }
    }

    private static TimeSpan GetGameUptime(IReadOnlyList<GameProcessIdentity> identities)
    {
        if (identities.Count == 0) return TimeSpan.FromMinutes(1);
        try { return DateTime.UtcNow - identities[0].StartTimeUtc; }
        catch { return TimeSpan.FromMinutes(1); }
    }

    // ---------------- 帧格临时优化 ----------------

    public async Task<OperationResult> ApplyTweaksAsync(CpuFrameTempArgs args)
    {
        _tweaksDesired = true;
        _tweaksArgs = args;
        var result = await ApplyTweaksCoreAsync(args).ConfigureAwait(false);
        return result;
    }

    private async Task<OperationResult> ApplyTweaksCoreAsync(CpuFrameTempArgs args)
    {
        var result = await _tweaks.ApplyAsync(args.ForegroundResponsiveness, args.ForegroundPriority,
            args.NicPowerOff, args.UsbSuspendOff, args.PcieAspmOff, args.LaptopExcluded).ConfigureAwait(false);
        _tempOptimizationApplied = FrameTweaksService.IsForegroundBoostApplied() || FrameTweaksService.IsPowerSaveLatencyApplied();
        SetResult("帧格临时优化", result);
        return result;
    }

    public async Task<OperationResult> RevertTweaksAsync()
    {
        _tweaksDesired = false;
        return await RevertTweaksCoreAsync().ConfigureAwait(false);
    }

    private async Task<OperationResult> RevertTweaksCoreAsync()
    {
        var result = await _tweaks.RevertAsync().ConfigureAwait(false);
        _tempOptimizationApplied = FrameTweaksService.IsForegroundBoostApplied() || FrameTweaksService.IsPowerSaveLatencyApplied();
        SetResult("帧格临时优化还原", result);
        return result;
    }

    public Task<IReadOnlyList<FrameIncompletePowerBackup>> GetIncompletePowerBackupsAsync()
        => _tweaks.GetIncompletePowerBackupsAsync();

    public async Task<OperationResult> PreserveCurrentPowerValuesAsync(IReadOnlyList<FrameIncompletePowerBackup> confirmed)
    {
        await _powerGate.WaitAsync().ConfigureAwait(false);
        try
        {
        if (AppSettingsStore.Read().FrameModeActive || _powerLockActive) return OperationResult.Fail("请先停用帧格及电源守护，再核对保留当前值。");
        var result = await _tweaks.PreserveCurrentPowerValuesAsync(confirmed).ConfigureAwait(false);
        if (result.Success && confirmed.Any(c => c.FileName == "frame_power_scheme_snapshot.json"))
        {
            AppSettingsStore.Update(s => { s.FramePowerRestorePending = false; s.FramePowerPreviousSchemeGuid = "";
                s.FramePowerLockedSchemeGuid = ""; s.FramePowerLockEnabled = false; });
            _powerLockedGuid = ""; _powerLockReady = false;
            GameTargetService.Default.SetRestoreFailure("帧格电源计划", false);
        }
        SetResult("帧格旧电源记录处理", result);
        return result;
        }
        finally { _powerGate.Release(); }
    }

    // ---------------- 帧格电源锁定 ----------------

    public async Task<OperationResult> SetPowerLockAsync(bool enabled, string targetGuid)
    {
        await _powerGate.WaitAsync().ConfigureAwait(false);
        try
        {
        if (!enabled) return await RevertPowerLockCoreAsync(AppSettingsStore.Read()).ConfigureAwait(false);
        AppSettingsStore.Update(s => s.FramePowerLockTargetGuid = targetGuid ?? "");
        return await ApplyPowerLockCoreAsync(AppSettingsStore.Read(), resume: false).ConfigureAwait(false);
        }
        finally { _powerGate.Release(); }
    }

    private async Task<OperationResult> ApplyPowerLockCoreAsync(AppSettings settings, bool resume)
    {
        if (_powerLockReady && _powerLockActive && (string.IsNullOrWhiteSpace(settings.FramePowerLockTargetGuid)
            || settings.FramePowerLockTargetGuid.Equals(_powerLockedGuid, StringComparison.OrdinalIgnoreCase))) return OperationResult.Ok("电源锁定已生效。");
        try
        {
            // Never overwrite a pending original plan with a new attempt. Recover the snapshot's actual plan first.
            if ((!resume && (settings.FramePowerRestorePending || _tweaks.HasPowerSchemeContentSnapshot))
                || (resume && _tweaks.PowerSchemeSnapshotGuid is { } snapshotGuid
                    && !snapshotGuid.Equals(settings.FramePowerLockedSchemeGuid, StringComparison.OrdinalIgnoreCase)))
            {
                var recovered = await RevertPowerLockCoreAsync(settings).ConfigureAwait(false);
                if (!recovered.Success) return OperationResult.Fail("旧电源锁定尚未恢复，未覆盖原记录：" + recovered.Message);
                settings = AppSettingsStore.Read(); resume = false;
            }
            var schemes = await _power.GetSchemesAsync().ConfigureAwait(false);
            var active = schemes.FirstOrDefault(s => s.IsActive);
            if (active is null || string.IsNullOrEmpty(active.Guid))
            {
                SetError("无法读取当前电源计划，电源锁定未应用。");
                return OperationResult.Fail("无法读取当前电源计划。");
            }

            var savedPrevious = settings.FramePowerPreviousSchemeGuid;
            var requested = resume
                ? (!string.IsNullOrWhiteSpace(settings.FramePowerLockedSchemeGuid) ? settings.FramePowerLockedSchemeGuid
                    : !string.IsNullOrWhiteSpace(settings.FramePowerLockTargetGuid) ? settings.FramePowerLockTargetGuid : savedPrevious)
                : settings.FramePowerLockTargetGuid;

            // 卓越性能模板 GUID 不是可直接激活的计划：首次导入后的真实计划是随机副本 GUID。
            // 必须在快照/守护/还原之前归一化为真实 GUID，否则会出现“目标=模板、实际=副本”的错位。
            if (!string.IsNullOrWhiteSpace(requested) &&
                requested.Equals(PowerSchemes.UltimateSchemeTemplateGuid, StringComparison.OrdinalIgnoreCase))
            {
                if (!resume)
                {
                    AppSettingsStore.Update(s =>
                    {
                        s.FramePowerPreviousSchemeGuid = active.Guid;
                        s.FramePowerLockedSchemeGuid = "";
                        s.FramePowerRestorePending = true;
                    });
                }
                var imported = await _power.SetSchemeAsync(PowerSchemes.UltimateSchemeTemplateGuid).ConfigureAwait(false);
                if (!imported.Success)
                {
                    var back = await _power.SetSchemeAsync(active.Guid).ConfigureAwait(false);
                    GameTargetService.Default.SetRestoreFailure("帧格电源计划", true);
                    SetError("卓越性能计划导入失败：" + imported.Message);
                    return OperationResult.Fail("卓越性能计划导入失败：" + imported.Message
                        + (back.Success ? " 已切回原计划。" : " 切回原计划也失败，已保留恢复记录。"));
                }
                var actual = AppSettingsStore.Read().UltimatePowerSchemeGuid;
                if (string.IsNullOrWhiteSpace(actual))
                {
                    SetError("卓越性能计划导入后没有取得真实计划 GUID。");
                    return OperationResult.Fail("卓越性能计划导入后没有取得真实计划 GUID，未继续锁定。");
                }
                schemes = await _power.GetSchemesAsync().ConfigureAwait(false);
                if (schemes.All(s => !s.Guid.Equals(actual, StringComparison.OrdinalIgnoreCase)))
                {
                    SetError("卓越性能计划导入后未出现在计划列表中：" + actual);
                    return OperationResult.Fail("卓越性能计划导入后未出现在计划列表中，未继续锁定。");
                }
                requested = actual;
                AppSettingsStore.Update(s =>
                {
                    s.FramePowerLockTargetGuid = actual;
                    s.FramePowerLockedSchemeGuid = actual;
                });
                settings = AppSettingsStore.Read();
            }

            if (!string.IsNullOrWhiteSpace(requested) && schemes.All(s => !s.Guid.Equals(requested, StringComparison.OrdinalIgnoreCase)))
            {
                if (resume) { SetError("原锁定计划已不存在，未切换。"); return OperationResult.Fail("原锁定计划已不存在。"); }
                // 不再静默改成“当前计划”：用户选择已失效时明确失败，避免“显示成功但计划没有写入”。
                SetError("选择的电源计划已不存在：" + requested);
                return OperationResult.Fail("选择的电源计划已不存在，请刷新电源计划列表后重新选择。");
            }
            if (resume && string.IsNullOrWhiteSpace(savedPrevious)) return OperationResult.Fail("原计划恢复记录缺失。");
            var lockGuid = string.IsNullOrWhiteSpace(requested) ? active.Guid : requested;
            if (!resume)
            {
                AppSettingsStore.Update(s =>
                {
                    s.FramePowerPreviousSchemeGuid = active.Guid;
                    s.FramePowerLockedSchemeGuid = lockGuid;
                    s.FramePowerRestorePending = true;
                });
            }
            var saved = AppSettingsStore.Read();
            if (!saved.FramePowerRestorePending) return OperationResult.Fail("原计划恢复记录保存失败，未切换计划。");
            var set = await _power.SetSchemeAsync(lockGuid).ConfigureAwait(false);
            if (!set.Success) { SetError("电源计划锁定失败：" + set.Message); return set; }

            var snapshot = await _tweaks.SnapshotPowerSchemeContentAsync(lockGuid,
                settings.FramePowerSaveLatencyEnabled && settings.FrameUsbSuspendOffEnabled,
                settings.FramePowerSaveLatencyEnabled && settings.FramePcieAspmOffEnabled).ConfigureAwait(false);
            if (!snapshot.Success)
            {
                GameTargetService.Default.SetRestoreFailure("帧格电源计划", true);
                SetError("电源计划内容快照失败：" + snapshot.Message);
                return OperationResult.Fail("电源计划内容快照失败，守护未启动：" + snapshot.Message);
            }
            _powerLockedGuid = lockGuid;
            _powerLockReady = true;
            _powerLockActive = true;
            _powerGuardCount = 0;
            SetAction($"电源计划已锁定为 {lockGuid}，内容已快照。");
            return OperationResult.Ok($"电源计划已锁定为 {lockGuid}。");
        }
        catch (Exception ex)
        {
            SetError("电源锁定异常：" + ex.Message);
            return OperationResult.Fail("电源锁定失败：" + ex.Message);
        }
    }

    private async Task<OperationResult> RevertPowerLockCoreAsync(AppSettings settings)
    {
        // Stop guarding immediately, including when exact recovery fails and requires user review.
        _powerLockActive = false; _powerLockReady = false;
        if (!_tweaks.HasPowerSchemeContentSnapshot && !settings.FramePowerRestorePending)
        {
            _powerLockActive = false;
            _powerLockReady = false;
            return OperationResult.Ok("");
        }
        try
        {
            var previous = settings.FramePowerPreviousSchemeGuid;
            var expected = _tweaks.PowerSchemeSnapshotGuid
                ?? (!string.IsNullOrWhiteSpace(_powerLockedGuid) ? _powerLockedGuid
                    : !string.IsNullOrWhiteSpace(settings.FramePowerLockedSchemeGuid) ? settings.FramePowerLockedSchemeGuid : previous);
            var content = _tweaks.HasPowerSchemeContentSnapshot
                ? await _tweaks.VerifyAndRestorePowerSchemeContentAsync(expected).ConfigureAwait(false)
                : OperationResult.Ok("");
            if (string.IsNullOrWhiteSpace(previous))
            {
                GameTargetService.Default.SetRestoreFailure("帧格电源计划", true);
                return OperationResult.Fail("电源计划还原未完成：原计划记录缺失，已保留快照。" + content.Message);
            }
            var restore = await _power.SetSchemeAsync(previous).ConfigureAwait(false);
            var cleanup = restore.Success && content.Success ? _tweaks.DiscardPowerSchemeContentSnapshot() : OperationResult.Ok("");
            var complete = restore.Success && content.Success && cleanup.Success;
            if (complete)
            {
                _powerLockedGuid = "";
                _powerLockActive = false;
                _powerLockReady = false;
                AppSettingsStore.Update(s =>
                {
                    s.FramePowerPreviousSchemeGuid = "";
                    s.FramePowerLockedSchemeGuid = "";
                    s.FramePowerRestorePending = false;
                });
                var cleared = AppSettingsStore.Read();
                if (cleared.FramePowerRestorePending) complete = false;
            }
            GameTargetService.Default.SetRestoreFailure("帧格电源计划", !complete);
            if (complete) SetAction("电源计划已还原，锁定解除。");
            return complete
                ? OperationResult.Ok("电源计划已还原为原计划，锁定解除。" + content.Message)
                : OperationResult.Fail($"电源计划还原未完成，已保留恢复记录：{restore.Message} {content.Message} {cleanup.Message}");
        }
        catch (Exception ex)
        {
            GameTargetService.Default.SetRestoreFailure("帧格电源计划", true);
            SetError("电源还原异常：" + ex.Message);
            return OperationResult.Fail("电源计划还原失败：" + ex.Message);
        }
    }

    private async Task GuardPowerLockCoreAsync(AppSettings settings, CancellationToken token)
    {
        try
        {
            var expected = _powerLockedGuid;
            if (string.IsNullOrWhiteSpace(expected)) return;
            var schemes = await _power.GetSchemesAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (schemes.All(s => !s.Guid.Equals(expected, StringComparison.OrdinalIgnoreCase)))
            {
                // 锁定的计划被外部删除：不能盲目再切，保留快照与恢复记录等待人工核对。
                SetError("锁定的电源计划已不存在，电源守护已停止：" + expected);
                return;
            }
            var active = schemes.FirstOrDefault(s => s.IsActive);
            if (active is not null && !active.Guid.Equals(expected, StringComparison.OrdinalIgnoreCase))
                await _power.SetSchemeAsync(expected).ConfigureAwait(false);
            if (_tweaks.HasPowerSchemeContentSnapshot)
                await _tweaks.VerifyAndRestorePowerSchemeContentAsync(expected, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { SetError("电源锁定守护失败：" + ex.Message); }
    }

    // Compatibility tombstone: old clients/settings must never trigger a global purge.
    public (bool Success, string Message) CleanMemoryNow() =>
        (false, "旧帧格内存清理已下线；请使用进程管理中的后台内存回收。");

    // ---------------- 全量还原（供退出/恢复） ----------------

    public async Task<OperationResult> RestoreAllAsync(Action<string>? progress = null)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        void Report(string message)
        {
            Log.Info($"帧格退出还原：{message}；累计 {elapsed.Elapsed.TotalSeconds:F1} 秒");
            try { progress?.Invoke(message); }
            catch (Exception ex) { Log.Warn("帧格还原进度显示失败：" + ex.Message); }
        }
        Report("正在停止帧格监控…");
        await StopMonitorAsync().ConfigureAwait(false);
        var messages = new List<string>();
        var failures = new List<string>();
        Report("正在还原进程优先级与亲和性…");
        RestorePriorities();
        if (_priorityOriginals.Count != 0) failures.Add("自动优先级仍有待还原实例");
        else messages.Add("自动优先级已还原");
        _affinityMask = 0;
        CpuTopologyService.RestoreAffinityRule();
        if (CpuTopologyService.HasAffinityRuleRestorePending) failures.Add("CPU 亲和性规则仍待还原");
        else messages.Add("CPU 亲和性规则已还原");
        if (CpuTopologyService.DualCcdSchedulingActive)
        {
            Report("正在还原双 CCD 调度及电源策略，可能需要一些时间…");
            var dual = await _cpu.RevertDualCcdSchedulingAsync().ConfigureAwait(false);
            messages.Add("双CCD：" + dual.Message);
            if (!dual.Success || CpuTopologyService.DualCcdSchedulingActive) failures.Add("双 CCD 还原未完成");
        }
        if (CpuTopologyService.SingleCcdExcludeActive)
        {
            Report("正在还原单 CCD 调度及电源策略…");
            var single = await _cpu.RestoreGameFullCoresAsync().ConfigureAwait(false);
            messages.Add("单CCD：" + single.Message);
            if (!single.Success || CpuTopologyService.SingleCcdExcludeActive) failures.Add("单 CCD 还原未完成");
        }
        Report("正在释放计时器并还原游戏节能状态…");
        _boostTimer = false;
        _boostGamePower = false;
        if (!_gamePower.Sync([])) failures.Add(_gamePower.Status);
        UpdateTimer(false);
        messages.Add("高分辨率计时器请求已释放");
        _tweaksDesired = false;
        Report("正在还原帧格临时优化…");
        var tweaks = await RevertTweaksCoreAsync().ConfigureAwait(false);
        messages.Add("临时优化：" + tweaks.Message);
        if (!tweaks.Success) failures.Add("临时优化：" + tweaks.Message);
        Report("正在还原电源计划…");
        var power = await SetPowerLockAsync(false, "").ConfigureAwait(false);
        messages.Add("电源锁定：" + power.Message);
        if (!power.Success) failures.Add("电源锁定：" + power.Message);
        Report(failures.Count > 0 ? "帧格还原未完成，保留恢复记录。" : "帧格执行状态已还原。");
        if (failures.Count > 0) return OperationResult.Fail("帧格还原未完成：" + string.Join("；", failures));
        return OperationResult.Ok("帧格执行状态已还原：" + string.Join("；", messages));
    }

    // ---------------- 只读核对 / 按备份恢复（T1 模式） ----------------

    public ReconcileSnapshot Reconcile()
    {
        var items = new List<ReconcileItem>
        {
            new() { Id = "monitor", Kind = "cpuframe", State = _monitorRunning ? "运行中" : "已停止", Detail = _lastAction },
            new() { Id = "priority", Kind = "cpuframe", State = _priorityEnabled ? $"已生效（High，{_priorityAppliedCount} 个实例）" : "未生效", HasBackup = _priorityOriginals.Count > 0 },
            new() { Id = "affinity", Kind = "cpuframe", State = _affinityMask != 0 ? $"已生效（0x{_affinityMask:X}）" : "未生效", HasBackup = CpuTopologyService.HasAffinityRuleRestorePending },
            new() { Id = "ccd", Kind = "cpuframe", State = CpuTopologyService.DualCcdSchedulingActive ? "双CCD已生效" : CpuTopologyService.SingleCcdExcludeActive ? "单CCD已生效" : "未生效" },
            new() { Id = "game-power", Kind = "cpuframe", State = _gamePower.Status, HasBackup = _gamePower.PendingCount > 0 },
            new() { Id = "timer", Kind = "cpuframe", State = _timerActive ? "高分辨率计时器已请求" : "未请求" },
            new() { Id = "tweaks", Kind = "cpuframe", State = _tempOptimizationApplied ? "临时优化已应用" : "未应用", HasBackup = FrameTweaksService.IsForegroundBoostApplied() || FrameTweaksService.IsPowerSaveLatencyApplied() },
            new() { Id = "power", Kind = "cpuframe", State = _powerLockActive ? $"电源锁定中（{_powerLockedGuid}）" : "未锁定", HasBackup = _tweaks.HasPowerSchemeContentSnapshot || AppSettingsStore.Read().FramePowerRestorePending },
        };
        return IncludeCompleteRecovery(new ReconcileSnapshot
        {
            Module = "cpuframe",
            Summary = $"监控{(_monitorRunning ? "运行中" : "已停止")}；目标代次 {_targetGeneration}；游戏{(_gameRunning ? "运行中" : "未运行")}；"
                + $"优先级{(_priorityEnabled ? "开" : "关")}；亲和性{(_affinityMask != 0 ? "开" : "关")}；"
                + $"CCD{(CpuTopologyService.DualCcdSchedulingActive ? "双" : CpuTopologyService.SingleCcdExcludeActive ? "单" : "无")}。",
            Complete = true,
            Items = items,
            RecoveryScopes =
            [
                new RecoveryScope { Id = "priority", Description = "还原自动优先级", Available = _priorityOriginals.Count > 0, Reason = _priorityOriginals.Count > 0 ? "" : "没有待还原的优先级记录。", AffectedCount = _priorityOriginals.Count },
                new RecoveryScope { Id = "affinity", Description = "解除并还原 CPU 亲和性规则", Available = _affinityMask != 0 || CpuTopologyService.HasAffinityRuleRestorePending, Reason = _affinityMask != 0 ? "" : "没有待还原的亲和性规则。", AffectedCount = _affinityMask != 0 ? 1 : 0 },
                new RecoveryScope { Id = "ccd", Description = "撤销双/单 CCD 调度", Available = CpuTopologyService.DualCcdSchedulingActive || CpuTopologyService.SingleCcdExcludeActive, Reason = CpuTopologyService.DualCcdSchedulingActive || CpuTopologyService.SingleCcdExcludeActive ? "" : "没有生效中的 CCD 调度。", AffectedCount = 1 },
                new RecoveryScope { Id = "game-power", Description = "还原游戏执行速度节能", Available = _gamePower.PendingCount > 0, AffectedCount = _gamePower.PendingCount },
                new RecoveryScope { Id = "timer", Description = "释放高分辨率计时器", Available = _timerActive, Reason = _timerActive ? "" : "计时器未请求。", AffectedCount = _timerActive ? 1 : 0 },
                new RecoveryScope { Id = "tweaks", Description = "还原帧格临时优化", Available = FrameTweaksService.IsForegroundBoostApplied() || FrameTweaksService.IsPowerSaveLatencyApplied(), Reason = _tempOptimizationApplied ? "" : "没有可还原的临时优化备份。", AffectedCount = _tempOptimizationApplied ? 1 : 0 },
                new RecoveryScope { Id = "power", Description = "按备份还原帧格电源锁定", Available = _tweaks.HasPowerSchemeContentSnapshot || AppSettingsStore.Read().FramePowerRestorePending, Reason = _tweaks.HasPowerSchemeContentSnapshot || AppSettingsStore.Read().FramePowerRestorePending ? "" : "没有待还原的电源锁定记录。", AffectedCount = 1 },
            ],
        });
    }

    private static ReconcileSnapshot IncludeCompleteRecovery(ReconcileSnapshot snapshot) => new()
    {
        Module = snapshot.Module, Summary = snapshot.Summary, Complete = snapshot.Complete, Items = snapshot.Items,
        RecoveryScopes = snapshot.RecoveryScopes.Prepend(new RecoveryScope
        {
            Id = "all", Description = "完整恢复 CPU/帧格状态", Available = snapshot.RecoveryScopes.Any(s => s.Available),
            Reason = snapshot.RecoveryScopes.Any(s => s.Available) ? "" : "没有可按备份恢复的 CPU/帧格状态。",
            AffectedCount = snapshot.RecoveryScopes.Where(s => s.Available).Sum(s => s.AffectedCount),
        }).ToArray(),
    };

    public async Task<RecoverOutcome> RecoverAsync(string scope)
    {
        var normalized = (scope ?? "").Trim().ToLowerInvariant();
        string message;
        var ok = true;
        switch (normalized)
        {
            case "all":
            {
                var result = await RestoreAllAsync().ConfigureAwait(false);
                ok = result.Success;
                message = result.Message;
                break;
            }
            case "priority":
                RestorePriorities();
                message = "自动优先级已还原。";
                break;
            case "affinity":
                _affinityMask = 0;
                CpuTopologyService.RestoreAffinityRule();
                message = "CPU 亲和性规则已解除并还原。";
                break;
            case "ccd":
            {
                var dual = await RevertDualCcdAsync().ConfigureAwait(false);
                var single = await RevertSingleCcdAsync().ConfigureAwait(false);
                ok = dual.Success || single.Success;
                message = "CCD：" + dual.Message + "；" + single.Message;
                break;
            }
            case "game-power":
                _boostGamePower = false;
                ok = _gamePower.Sync([]);
                message = _gamePower.Status;
                break;
            case "timer":
                _boostTimer = false;
                UpdateTimer(false);
                message = "高分辨率计时器已释放。";
                break;
            case "tweaks":
            {
                var result = await RevertTweaksAsync().ConfigureAwait(false);
                ok = result.Success;
                message = "临时优化：" + result.Message;
                break;
            }
            case "power":
            {
                var result = await SetPowerLockAsync(false, "").ConfigureAwait(false);
                ok = result.Success;
                message = "电源锁定：" + result.Message;
                break;
            }
            default:
                return new RecoverOutcome { Scope = normalized, Success = false, Verified = false, Message = $"未知恢复范围“{scope}”，已拒绝执行。" };
        }

        var snapshot = Reconcile();
        var remaining = snapshot.RecoveryScopes.FirstOrDefault(s => s.Id == normalized);
        var verified = ok && snapshot.Complete && (normalized == "all"
            ? !snapshot.RecoveryScopes.Any(s => s.Available) : remaining is not null && !remaining.Available);
        var text = message + (verified ? " 复核通过：该范围已无可恢复项。" : " 复核未通过，保留原始证据与阻塞。");
        return new RecoverOutcome { Scope = normalized, Success = ok, Verified = verified, Message = text, VerifiedSnapshot = snapshot };
    }

    // ---------------- 状态快照 ----------------

    public CpuFrameStatusSnapshot Snapshot()
    {
        // The existing status heartbeat runs while a long command/tick is busy. Query here so a
        // power-policy operation cannot stale the independent Delta observation for minutes.
        if (_monitorRunning) lock (_observationGate)
        {
            var now = ClockForTests?.Invoke() ?? DateTimeOffset.UtcNow;
            var previous = Volatile.Read(ref _deltaObservation);
            if (previous is null || now - previous.ObservedUtc >= TickInterval || previous.ObservedUtc > now)
            {
                bool? running;
                try { running = DeltaReaderForTests is { } read ? read() : DeltaForceLocator.IsGameRunning(); }
                catch { running = null; }
                Volatile.Write(ref _deltaObservation, new(running, now));
            }
        }
        var version = typeof(CpuFrameEngine).Assembly.GetName().Version ?? new Version(0, 0, 0);
        var delta = Volatile.Read(ref _deltaObservation);
        return new CpuFrameStatusSnapshot
        {
            ModuleVersion = $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}",
            MonitorRunning = _monitorRunning,
            TargetGeneration = _targetGeneration,
            TargetDisplayName = _targetDisplay,
            IsCustomTarget = _targetCustom,
            TargetExecutablePath = _targetExe,
            GameRunning = _gameRunning,
            DeltaForceRunning = _monitorRunning ? delta?.Running : null,
            DeltaForceObservedUtc = _monitorRunning ? delta?.ObservedUtc : null,
            GamePid = _gamePid,
            GameStartTimeUtcTicks = _gameStartTicks,
            GameExecutablePath = _gameExe,
            GameInstanceCount = _gameInstances,
            PriorityEnabled = _priorityEnabled,
            PriorityApplied = _priorityAppliedCount > 0 ? "High" : "",
            PriorityAppliedCount = _priorityAppliedCount,
            AffinityRuleEnabled = _affinityMask != 0,
            AffinityRuleMask = _affinityMask,
            DualCcdSchedulingActive = CpuTopologyService.DualCcdSchedulingActive,
            SingleCcdExcludeActive = CpuTopologyService.SingleCcdExcludeActive,
            TimerActive = _timerActive,
            GamePowerAppliedCount = _gamePower.AppliedCount,
            GamePowerRestorePending = _gamePower.PendingCount,
            GamePowerHasError = _gamePower.HasError || _gamePowerIdentityError.Length > 0,
            GamePowerStatus = _gamePowerIdentityError.Length > 0 ? _gamePowerIdentityError : _monitorRunning && _boostGamePower && _gamePower.PendingCount == 0 && !_gameRunning ? "已配置 · 等待游戏启动" : _gamePower.Status,
            DwmRestartedThisSession = _dwmHandledForSession,
            TempOptimizationApplied = _tempOptimizationApplied,
            PowerLockActive = _powerLockActive,
            PowerLockedSchemeGuid = _powerLockedGuid,
            MemoryCleanStatus = "旧帧格内存清理已下线。",
            LastAction = _lastAction,
            LastError = _lastError,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
    }

    private void SetAction(string text) { _lastAction = text; }
    private void SetError(string text) { _lastError = text; }
    private void SetResult(string what, OperationResult result)
    {
        if (result.Success) SetAction($"{what}：{result.Message}");
        else SetError($"{what}失败:{result.Message}");
    }

    public void Dispose()
    {
        try { StopMonitorAsync().GetAwaiter().GetResult(); _gamePower.Sync([]); } catch { }
    }
}
