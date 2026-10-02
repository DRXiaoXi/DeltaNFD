using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DeltaNFD.Models;

namespace DeltaNFD.Services;

/// <summary>
/// 游戏进程优化服务的真实实现。
/// </summary>
public sealed class GameProcessService : IGameProcessService
{
    /// <summary>三角洲游戏进程名（定义移至 DeltaForceLocator，此处保留原名兼容既有引用）。</summary>
    public const string GameProcessName = DeltaForceLocator.GameProcessName;


    /// <summary>系统关键进程白名单：绝不出现在可清理列表，也不做内存整理。
    /// 含三角洲游戏本体与 ACE/SGuard 反作弊进程——清理它们会导致游戏闪退，甚至触发反作弊处罚。</summary>
    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "csrss", "wininit", "winlogon", "smss", "lsass", "services",
        "svchost", "dwm", "explorer", "fontdrvhost", "sihost", "taskhostw", "conhost", "runtimebroker",
        "ctfmon", "audiodg", "wudfhost", "spoolsv", "searchindexer", "securityhealthservice",
        "msmpeng", "nissrv", "memcompression", "registry", "wudfcompanionhost", "dashboard",
        "DeltaNFD", "DeltaNFD.exe", "powershell", "cmd", "conhost", "openconsole",
        "applicationframehost", "systemsettings", "shellexperiencehost", "startmenuexperiencehost",
        "searchhost", "textinputhost", "lockapp", "phoneexperiencehost",
        // 三角洲游戏本体与反作弊组件（绝不能清理）
        GameProcessName, "sguard64", "sguardsvc64", "sguardmk64", "sguard64e",
        "ace-base", "ace-guimgr", "sguard",
    };

    /// <summary>进程名 → 分类标签的启发式映射。</summary>
    private static readonly (string Key, string Category)[] CategoryMap =
    [
        ("chrome", "浏览器"), ("msedge", "浏览器"), ("firefox", "浏览器"), ("opera", "浏览器"),
        ("360se", "浏览器"), ("QQBrowser", "浏览器"), ("sogouexplorer", "浏览器"), ("browser", "浏览器"),
        ("wechat", "通讯"), ("weixin", "通讯"), ("qq", "通讯"), ("dingtalk", "通讯"), ("telegram", "通讯"),
        ("discord", "通讯"), ("feishu", "通讯"), ("lark", "通讯"), ("wxwork", "通讯"),
        ("obs64", "直播"), ("obs32", "直播"), ("xsplit", "直播"), ("bilibili", "直播"),
        ("onenote", "办公"), ("winword", "办公"), ("excel", "办公"), ("powerpnt", "办公"),
        ("outlook", "办公"), ("wps", "办公"), ("notepad", "办公"), ("Typora", "办公"),
        ("onedrive", "云同步"), ("baidunetdisk", "网盘"), ("quarkclouddrive", "网盘"),
        ("nutstore", "网盘"), ("alist", "网盘"),
        ("steam", "游戏平台"), ("epicgameslauncher", "游戏平台"), ("wegame", "游戏平台"),
        ("origin", "游戏平台"), ("eadm", "游戏平台"), ("ubisoft", "游戏平台"),
        ("battle.net", "游戏平台"), ("riotclientservices", "游戏平台"),
        ("cloudmusic", "娱乐"), ("qqmusic", "娱乐"), ("kugou", "娱乐"), ("kuwo", "娱乐"),
        ("PotPlayer", "娱乐"), ("baidunetdisk", "娱乐"),
        ("combase", "其他"),
    ];

    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;
    private bool _gamePriorityEnabled;
    private bool _isGameRunning;
    private readonly object _priorityGate = new();
    private readonly Dictionary<int, (GameProcessIdentity Identity, ProcessPriorityClass Original)> _priorities = [];
    private string _statusText = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public GameProcessService()
    {
        try
        {
            _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        }
        catch
        {
            _dispatcher = null;
        }

        _gamePriorityEnabled = LoadSetting();
        _statusText = DescribeStatus();
        var monitor = ServiceLocator.GameMonitor;
        monitor.Tick += OnGameMonitorTick;
        OnGameMonitorTick(monitor.IsRunning, monitor.ProcessId);
    }

    // ---------------- 属性 ----------------

    public bool IsGameRunning
    {
        get => _isGameRunning;
        private set
        {
            if (_isGameRunning == value)
            {
                return;
            }

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
            if (_gamePriorityEnabled == value)
            {
                return;
            }

            _gamePriorityEnabled = value;
            SaveSetting(value);
            _gamePriorityEnabled = LoadSetting();
            StatusText = DescribeStatus();
            OnPropertyChanged();
            if (_gamePriorityEnabled)
            {
                var monitor = ServiceLocator.GameMonitor;
                OnGameMonitorTick(monitor.IsRunning, monitor.ProcessId);
            }
            else RestorePriorities();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
        }
    }

    // ---------------- 共享监控：自动提权 ----------------

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

        if (!_gamePriorityEnabled) { RestorePriorities(); return; }
        if (running) _ = ApplyPriorityNow();
    }

    public OperationResult ApplyPriorityNow()
    {
        using var operation = GameTargetService.Default.BeginOperation();
        if (OfflineModeGuard.BlocksNormalAutomation) return OperationResult.Fail("脱机模式或恢复流程中，自动优先级已暂停。");
        if (!LoadSetting()) return OperationResult.Fail("自动优先级未开启。");
        var applied = 0;
        var failed = 0;
        lock (_priorityGate)
        foreach (var gameProcess in GameTargetService.Default.GetProcesses())
        {
            using (gameProcess)
            {
                try
                {
                    var identity = GameTargetService.Default.Identify(gameProcess);
                    if (identity is null || !GameTargetService.Default.Matches(gameProcess, identity)) continue;
                    if (_priorities.TryGetValue(identity.Id, out var old) && old.Identity.StartTimeUtc != identity.StartTimeUtc)
                        _priorities.Remove(identity.Id);
                    if (gameProcess.PriorityClass != ProcessPriorityClass.High)
                    {
                        _priorities.TryAdd(identity.Id, (identity, gameProcess.PriorityClass));
                        gameProcess.PriorityClass = ProcessPriorityClass.High;
                    }
                    applied++;
                }
                catch { failed++; }
            }
        }
        lock (_priorityGate) GameTargetService.Default.SetRestoreFailure("自动优先级", _priorities.Count != 0 && !LoadSetting());
        StatusText = applied > 0
            ? $"{GameTargetService.Default.Current.DisplayName} · 优先级 High（{applied} 个实例）"
            : failed > 0 ? "目标优先级提升失败，稍后重试。" : GameTargetService.Default.Status;
        return applied > 0 ? OperationResult.Ok(StatusText) : OperationResult.Fail(StatusText);
    }

    public OperationResult DisableGamePriorityForOffline()
    {
        using var operation = GameTargetService.Default.BeginOperation();
        if (!_gamePriorityEnabled && !AppSettingsStore.Read().GamePriorityEnabled)
            return OperationResult.Ok("自动游戏优先级已关闭。");

        foreach (var process in GameTargetService.Default.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var identity = GameTargetService.Default.Identify(process);
                    if (identity is null) return OperationResult.Fail("目标进程身份无法确认，未关闭自动优先级。");
                    if (process.PriorityClass != ProcessPriorityClass.High) continue;
                    lock (_priorityGate)
                    {
                        if (!_priorities.TryGetValue(identity.Id, out var saved) || saved.Identity.StartTimeUtc != identity.StartTimeUtc)
                            return OperationResult.Fail($"目标 PID {identity.Id} 已是 High，但没有本工具原优先级备份；为避免覆盖第三方状态，未关闭自动优先级。");
                    }
                }
                catch (Exception ex)
                {
                    return OperationResult.Fail($"无法核对目标 PID {process.Id} 的优先级：{ex.Message}");
                }
            }
        }

        if (_gamePriorityEnabled) GamePriorityEnabled = false;
        else
        {
            SaveSetting(false);
            RestorePriorities();
        }
        if (AppSettingsStore.Read().GamePriorityEnabled)
            return OperationResult.Fail("自动优先级设置未能持久化关闭。");
        lock (_priorityGate)
            if (_priorities.Count != 0)
                return OperationResult.Fail("自动优先级已关闭，但仍有原值待恢复记录。");
        return OperationResult.Ok("自动游戏优先级已关闭，目标进程原优先级已还原或无需还原。");
    }

    private void RestorePriorities()
    {
        using var operation = GameTargetService.Default.BeginOperation();
        lock (_priorityGate)
        foreach (var (id, saved) in _priorities.ToArray())
        {
            try
            {
                using var p = Process.GetProcessById(id);
                if (p.HasExited || p.StartTime.ToUniversalTime() != saved.Identity.StartTimeUtc) { _priorities.Remove(id); continue; }
                if (!GameTargetService.Default.Matches(p, saved.Identity)) continue;
                if (p.PriorityClass == ProcessPriorityClass.High) p.PriorityClass = saved.Original;
                _priorities.Remove(id);
            }
            catch (ArgumentException) { _priorities.Remove(id); }
            catch { }
        }
        GameTargetService.Default.SetRestoreFailure("自动优先级", _priorities.Count != 0);
    }

    private string DescribeStatus()
    {
        if (!_gamePriorityEnabled)
        {
            return "进程优先级提升未启用";
        }

        return GameTargetService.Default.Status;
    }

    // ---------------- 进程扫描 / 结束 / 内存整理 ----------------

    public Task<List<ProcessInfo>> GetScannableProcessesAsync() => Task.Run(() =>
    {
        var result = new List<ProcessInfo>();
        var selfPid = Environment.ProcessId;

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == selfPid || ProtectedProcesses.Contains(process.ProcessName) || GameTargetService.Default.ProtectFromCleanup(process))
                    {
                        continue;
                    }

                    // 会话隔离的系统进程等访问不到的跳过
                    double memoryMb;
                    try
                    {
                        process.Refresh();
                        memoryMb = process.WorkingSet64 / 1024.0 / 1024.0;
                    }
                    catch
                    {
                        continue;
                    }

                    if (memoryMb < 10)
                    {
                        continue; // 忽略占用极小的进程，清理无意义
                    }

                    result.Add(new ProcessInfo
                    {
                        Name = process.ProcessName,
                        Pid = process.Id,
                        MemoryMb = Math.Round(memoryMb),
                        Category = Classify(process.ProcessName),
                    });
                }
                catch
                {
                    // 个别进程访问失败直接跳过
                }
            }
        }

        return result.OrderByDescending(p => p.MemoryMb).ToList();
    });

    private static string Classify(string processName) =>
        CategoryMap.FirstOrDefault(m => processName.Contains(m.Key, StringComparison.OrdinalIgnoreCase)).Category
        ?? "其他";

    public async Task<OperationResult> KillProcessAsync(ProcessInfo process)
    {
        try
        {
            using var operation = GameTargetService.Default.BeginOperation();
            var target = Process.GetProcessById(process.Pid);
            if (ProtectedProcesses.Contains(target.ProcessName) || GameTargetService.Default.ProtectFromCleanup(target))
            {
                target.Dispose();
                return OperationResult.Fail("不能清理目标游戏或受保护进程。");
            }
            target.Kill(entireProcessTree: false);
            await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            target.Dispose();

            return OperationResult.Ok($"已结束 {process.Name}（PID {process.Pid}），释放约 {process.MemoryMb:0} MB 内存。");
        }
        catch (ArgumentException)
        {
            return OperationResult.Ok($"{process.Name} 已自行退出。");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"结束 {process.Name} 失败：{ex.Message}");
        }
    }

    public async Task<(int Killed, double FreedMb)> KillManyAsync(
        IEnumerable<ProcessInfo> processes, IProgress<string>? progress = null)
    {
        var killed = 0;
        var freedMb = 0.0;
        foreach (var process in processes)
        {
            var result = await KillProcessAsync(process);
            if (result.Success)
            {
                killed++;
                freedMb += process.MemoryMb;
                progress?.Report($"已结束 {process.Name}（{killed} 个）");
            }
        }

        return (killed, freedMb);
    }

    public Task<double> TrimWorkingSetAsync(IEnumerable<ProcessInfo> processes, IProgress<string>? progress = null)
        => Task.Run(() =>
        {
            using var targetOperation = GameTargetService.Default.BeginOperation();
            var freedMb = 0.0;
            foreach (var info in processes)
            {
                try
                {
                    using var process = Process.GetProcessById(info.Pid);
                    if (ProtectedProcesses.Contains(process.ProcessName) || GameTargetService.Default.ProtectFromCleanup(process)) continue;
                    var before = process.WorkingSet64;
                    if (!EmptyWorkingSet(process.Handle))
                    {
                        continue;
                    }

                    process.Refresh();
                    freedMb += Math.Max(0, before - process.WorkingSet64) / 1024.0 / 1024.0;
                    progress?.Report($"已整理 {info.Name} 的工作集");
                }
                catch
                {
                    // 无法打开的进程跳过
                }
            }

            return freedMb;
        });

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(nint hProcess);

    // ---------------- 设置持久化（共享 AppSettingsStore） ----------------

    private static bool LoadSetting() => !OfflineModeGuard.BlocksNormalAutomation && AppSettingsStore.Read().GamePriorityEnabled;

    private static void SaveSetting(bool value)
        => AppSettingsStore.Update(s => s.GamePriorityEnabled = value);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        var handler = PropertyChanged;
        if (handler is null)
        {
            return;
        }

        var args = new PropertyChangedEventArgs(propertyName ?? "");
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            handler.Invoke(this, args);
        }
        else
        {
            _dispatcher.TryEnqueue(() => handler.Invoke(this, args));
        }
    }
}
