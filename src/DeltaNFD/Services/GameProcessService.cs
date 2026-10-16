using System.ComponentModel;
using System.Runtime.CompilerServices;
using DeltaNFD.Models;
using DeltaNFD.Backends;
using DeltaNFD.Services.Backends;

namespace DeltaNFD.Services;

/// <summary>
/// 游戏进程优化服务的真实实现（T4 后：优先级执行归 CPU/帧格后端）：
/// - 三角洲进程出现时由后端自动提升优先级（本类只持久化偏好并下发命令、展示实际状态）
/// - 后台进程扫描 / 结束 / 工作集整理通过系统维护后端执行
/// </summary>
public sealed class GameProcessService : IGameProcessService
{
    /// <summary>三角洲游戏进程名（定义移至 DeltaForceLocator，此处保留原名兼容既有引用）。</summary>
    public const string GameProcessName = DeltaForceLocator.GameProcessName;

    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;
    private bool _gamePriorityEnabled;
    private bool _isGameRunning;
    private string _statusText = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public GameProcessService()
    {
        try { _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(); }
        catch { _dispatcher = null; }

        _gamePriorityEnabled = LoadSetting();
        _statusText = DescribeStatus();
        var monitor = ServiceLocator.GameMonitor;
        monitor.Tick += OnGameMonitorTick;
        OnGameMonitorTick(monitor.IsRunning, monitor.ProcessId);
        // 后端状态：显示实际生效的游戏运行态（前端不再自行枚举目标进程）。
        Backends.BackendCpuFrameService.Instance.StatusUpdated += OnCpuFrameStatus;
    }

    private void OnCpuFrameStatus(CpuFrameStatusSnapshot snapshot)
    {
        void Apply()
        {
            if (snapshot.GameRunning != _isGameRunning)
            {
                IsGameRunning = snapshot.GameRunning;
                StatusText = DescribeStatus();
            }
        }

        if (_dispatcher is null || _dispatcher.HasThreadAccess) Apply();
        else _dispatcher.TryEnqueue(Apply);
    }

    // ---------------- 属性 ----------------

    public bool IsGameRunning
    {
        get => _isGameRunning;
        private set
        {
            if (_isGameRunning == value) return;
            _isGameRunning = value;
            OnPropertyChanged();
        }
    }

    public bool GamePriorityEnabled
    {
        get => _gamePriorityEnabled;
        set
        {
            if (value && OfflineModeGuard.BlocksNormalAutomation)
            {
                Log.Warn("脱机模式或恢复流程中，拒绝开启自动游戏优先级。");
                return;
            }
            if (_gamePriorityEnabled == value) return;

            _gamePriorityEnabled = value;
            SaveSetting(value);
            StatusText = DescribeStatus();
            OnPropertyChanged();
            // 执行在后端：立即下发期望（后端随后按设置持续维持）。
            _ = Backends.BackendCpuFrameService.Instance.ApplyPriorityAsync(value);
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value) return;
            _statusText = value;
            OnPropertyChanged();
        }
    }

    // ---------------- 共享监控：自动提权（执行在后端） ----------------

    private void OnGameMonitorTick(bool running, int? processId)
    {
        using var operation = GameTargetService.Default.BeginOperation();
        var enabled = AppSettingsStore.Read().GamePriorityEnabled;
        if (enabled != _gamePriorityEnabled)
        {
            _gamePriorityEnabled = enabled;
            OnPropertyChanged(nameof(GamePriorityEnabled));
        }

        if (running != _isGameRunning)
        {
            IsGameRunning = running;
            StatusText = DescribeStatus();
        }
    }

    public async Task<OperationResult> ApplyPriorityNowAsync()
    {
        using var operation = GameTargetService.Default.BeginOperation();
        if (OfflineModeGuard.BlocksNormalAutomation) return OperationResult.Fail("脱机模式或恢复流程中，自动优先级已暂停。");
        if (!LoadSetting()) return OperationResult.Fail("自动优先级未开启。");
        var result = await Backends.BackendCpuFrameService.Instance.ApplyPriorityAsync(true);
        StatusText = result.Success ? DescribeStatus() : "目标优先级提升失败，稍后重试。";
        return result;
    }

    public async Task<OperationResult> DisableGamePriorityForOfflineAsync()
    {
        using var operation = GameTargetService.Default.BeginOperation();
        if (!_gamePriorityEnabled && !AppSettingsStore.Read().GamePriorityEnabled)
            return OperationResult.Ok("自动游戏优先级已关闭。");

        // 记录期望停用后请求后端确认还原；后端不可达或存在无备份的 High 目标时如实失败，
        // 离线门禁保持阻塞（不声称优先级已还原）。
        SaveSetting(false);
        var result = await Backends.BackendCpuFrameService.Instance.ApplyPriorityAsync(false);
        if (!result.Success)
            return OperationResult.Fail("自动优先级未能确认关闭：" + result.Message);

        _gamePriorityEnabled = false;
        OnPropertyChanged(nameof(GamePriorityEnabled));
        if (AppSettingsStore.Read().GamePriorityEnabled)
            return OperationResult.Fail("自动优先级设置未能持久化关闭。");
        return OperationResult.Ok("自动游戏优先级已关闭，目标进程原优先级已由后端还原。");
    }

    private string DescribeStatus()
    {
        if (!_gamePriorityEnabled) return "进程优先级提升未启用";
        if (_isGameRunning) return $"{GameTargetService.Default.Current.DisplayName} · 优先级 High（后端维持）";
        return "等待目标游戏启动（后端将自动提升优先级）";
    }

    // Process mutation is confined to the system maintenance worker.
    private readonly BackendCaller _processBackend = new(BackendModules.System);

    public async Task<List<ProcessInfo>> GetScannableProcessesAsync()
    {
        var result = await _processBackend.InvokeAsync(BackendCommands.SystemProcessesScan, new { }, null, default);
        return BackendResults.RequireData<List<BackgroundProcessDto>>(result, "后台进程扫描")
            .Select(p => new ProcessInfo { Name = p.Name, Pid = p.Pid, StartTimeUtcTicks = p.StartTimeUtcTicks,
                ExecutablePath = p.ExecutablePath, MemoryMb = p.MemoryMb, Category = p.Category }).ToList();
    }

    private async Task<ProcessMaintenanceReport> MaintainAsync(string command,
        IEnumerable<ProcessInfo> processes, IProgress<string>? progress)
    {
        using var operation = GameTargetService.Default.BeginOperation();
        var args = new ProcessMaintenanceArgs { Processes = processes.Select(p =>
            new BackgroundProcessDto(p.Pid, p.StartTimeUtcTicks, p.ExecutablePath, p.Name, p.MemoryMb, p.Category)).ToList() };
        if (args.Processes.Count == 0) return new ProcessMaintenanceReport([], false);
        var result = await _processBackend.InvokeAsync(command, args, progress, default);
        if (!BackendResultStatuses.All.Contains(result.Status)
            || result.Data is null)
            throw new InvalidOperationException("进程维护未完成：" + result.Message);
        return BackendJson.Deserialize<ProcessMaintenanceReport>(result.Data.Value);
    }

    public async Task<OperationResult> KillProcessAsync(ProcessInfo process)
    {
        try
        {
            var report = await MaintainAsync(BackendCommands.SystemProcessesKill, [process], null);
            var item = report.Items.SingleOrDefault();
            return item?.Status == "ok" ? OperationResult.Ok(item.Message)
                : OperationResult.Fail(item?.Message ?? "未执行进程结束。");
        }
        catch (Exception ex) { return OperationResult.Fail("结束失败：" + ex.Message); }
    }

    public async Task<(int Killed, double FreedMb)> KillManyAsync(
        IEnumerable<ProcessInfo> processes, IProgress<string>? progress = null)
    {
        var report = await MaintainAsync(BackendCommands.SystemProcessesKill, processes, progress);
        return (report.Completed, report.MemoryMb);
    }

    public async Task<double> TrimWorkingSetAsync(IEnumerable<ProcessInfo> processes, IProgress<string>? progress = null)
        => (await MaintainAsync(BackendCommands.SystemProcessesTrim, processes, progress)).MemoryMb;

    // ---------------- 设置持久化（共享 AppSettingsStore） ----------------

    private static bool LoadSetting() => !OfflineModeGuard.BlocksNormalAutomation && AppSettingsStore.Read().GamePriorityEnabled;

    private static void SaveSetting(bool value)
        => AppSettingsStore.Update(s => s.GamePriorityEnabled = value);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        var handler = PropertyChanged;
        if (handler is null) return;

        var args = new PropertyChangedEventArgs(propertyName ?? "");
        if (_dispatcher is null || _dispatcher.HasThreadAccess) handler.Invoke(this, args);
        else _dispatcher.TryEnqueue(() => handler.Invoke(this, args));
    }
}
