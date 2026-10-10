using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeltaNFD.Services;

public sealed record GameTarget(bool IsCustom, string ExecutablePath, string ProcessName, string DisplayName, long Generation);
public sealed record GameProcessIdentity(int Id, DateTime StartTimeUtc, string ExecutablePath, long Generation);

/// <summary>One target authority. Custom targets are matched by resolved executable path, never by name alone.</summary>
public sealed class GameTargetService
{
    private static readonly Lazy<GameTargetService> Singleton = new(() => new GameTargetService(AppSettingsStore.DefaultPath));
    public static GameTargetService Default => Singleton.Value;
    private readonly string _settingsPath;
    private readonly object _gate = new();
    private GameTarget _current;
    private int _operations;
    private TaskCompletionSource? _idle;
    private GameProcessIdentity[] _sample = [];
    private DateTime _sampleTime;
    private readonly HashSet<string> _restoreFailures = [];
    public Func<IEnumerable<string>>? RuntimeBlockers { get; set; }
    public event Action<GameTarget>? Changed;
    public GameTarget Current { get { lock (_gate) return _current; } }
    public bool IsCustom => Current.IsCustom;
    public const string DeltaOnlyMessage = "此功能仅适用于三角洲，当前已开启“不玩三角洲辣”模式。";

    internal GameTargetService(string settingsPath)
    {
        _settingsPath = settingsPath;
        _current = Describe(AppSettingsStore.Read(settingsPath), 1);
    }

    private static GameTarget Describe(AppSettings s, long generation) => s.CustomGameModeEnabled
        ? new(true, s.CustomGameExecutablePath, Path.GetFileNameWithoutExtension(s.CustomGameExecutablePath),
            Path.GetFileName(s.CustomGameExecutablePath), generation)
        : new(false, "", DeltaForceLocator.GameProcessName, "三角洲", generation);

    public IDisposable BeginOperation()
    {
        lock (_gate) { _operations++; return new OperationLease(this); }
    }
    private sealed class OperationLease(GameTargetService owner) : IDisposable
    {
        private GameTargetService? _owner = owner;
        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _owner, null);
            if (value is null) return;
            lock (value._gate)
                if (--value._operations == 0) { value._idle?.TrySetResult(); value._idle = null; }
        }
    }

    /// <summary>模式切换前等待已开始的目标进程操作排空；超时由调用方拒绝切换。</summary>
    public async Task<bool> WaitForIdleAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Task? wait;
        lock (_gate)
        {
            if (_operations == 0) return true;
            wait = (_idle ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        try
        {
            await wait.WaitAsync(timeout, cancellationToken);
            return true;
        }
        catch (TimeoutException) { return false; }
        catch (OperationCanceledException) { return false; }
    }
    public void SetRestoreFailure(string owner, bool failed)
    {
        lock (_gate)
        {
            if (failed) _restoreFailures.Add(owner); else _restoreFailures.Remove(owner);
        }
    }
    internal static List<string> SettingBlockers(AppSettings s)
    {
        var result = new List<string>();
        if (s.FrameModeActive) result.Add("帧格模式");
        if (s.DwmRestartOnGameStart) result.Add("游戏启动重启 DWM");
        if (s.GamePriorityEnabled) result.Add("自动优先级");
        if (s.GameAffinityRuleEnabled) result.Add("CPU 亲和性规则");
        if (s.SingleCcdExcludeCpu0Enabled) result.Add("单 CCD 锁核");
        if (s.DualCcdImmediateEnabled || s.DualCcdArmed) result.Add("双 CCD 调度/登记");
        return result;
    }
    public string GetSwitchBlocker()
    {
        lock (_gate)
        {
            var reasons = SettingBlockers(AppSettingsStore.Read(_settingsPath));
            reasons.AddRange(RuntimeBlockers?.Invoke() ?? []);
            reasons.AddRange(_restoreFailures.Select(x => x + "还原未完成"));
            return reasons.Count == 0 ? "" : "请先关闭并成功还原：" + string.Join("、", reasons.Distinct());
        }
    }
    public async Task<OperationResult> ChangeAsync(bool enabled, string? selectedPath = null)
    {
        if (OfflineModeGuard.BlocksNormalAutomation)
            return OperationResult.Fail("脱机模式或恢复流程正在运行，暂不能切换目标进程。");
        string path;
        try
        {
            path = selectedPath ?? AppSettingsStore.Read(_settingsPath).CustomGameExecutablePath;
            if (enabled || selectedPath is not null) path = ValidateExecutable(path);
        }
        catch (Exception ex) { return OperationResult.Fail("目标文件无效：" + ex.Message); }
        GameTarget next;
        while (true)
        {
            Task? wait;
            lock (_gate)
            {
                if (OfflineModeGuard.BlocksNormalAutomation)
                    return OperationResult.Fail("脱机模式或恢复流程正在运行，暂不能切换目标进程。");
                var blocker = GetSwitchBlocker();
                if (blocker.Length > 0) return OperationResult.Fail(blocker);
                wait = _operations == 0 ? null : (_idle ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                if (wait is null)
                {
                    AppSettingsStore.Update(_settingsPath, s =>
                    {
                        if (SettingBlockers(s).Count != 0) throw new InvalidOperationException("优化开关已变化，请先关闭优化。");
                        s.CustomGameModeEnabled = enabled;
                        s.CustomGameExecutablePath = path;
                    });
                    var saved = AppSettingsStore.Read(_settingsPath);
                    if (saved.CustomGameModeEnabled != enabled || saved.CustomGameExecutablePath != path)
                        return OperationResult.Fail("目标设置保存失败，未切换目标。");
                    _current = next = Describe(saved, _current.Generation + 1);
                    _sample = []; _sampleTime = default;
                    break;
                }
            }
            try { await wait.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException) { return OperationResult.Fail("目标操作尚未结束，请稍后重试。"); }
        }
        Log.Info($"游戏目标切换：generation={next.Generation}, custom={enabled}, path={next.ExecutablePath}");
        foreach (Action<GameTarget> handler in Changed?.GetInvocationList() ?? [])
            try { handler(next); } catch (Exception ex) { Log.Error("目标变更订阅处理失败", ex); }
        return OperationResult.Ok("当前目标：" + next.DisplayName);
    }

    public Process[] GetProcesses(bool forceRefresh = false)
    {
        var target = Current;
        if (target.IsCustom && !File.Exists(target.ExecutablePath)) return [];
        if (string.IsNullOrWhiteSpace(target.ProcessName)) return [];
        var result = new List<Process>();
        GameProcessIdentity[] cached;
        bool refresh;
        lock (_gate) { cached = _sample; refresh = forceRefresh || DateTime.UtcNow - _sampleTime >= TimeSpan.FromSeconds(2); }
        if (!refresh)
        {
            foreach (var identity in cached)
            {
                try
                {
                    var p = Process.GetProcessById(identity.Id);
                    if (Matches(p, identity)) result.Add(p); else p.Dispose();
                }
                catch (ArgumentException) { }
            }
            return result.ToArray();
        }
        // EXE symlinks may change the visible process name. Only the final path is authoritative in custom mode.
        foreach (var p in target.IsCustom ? Process.GetProcesses() : Process.GetProcessesByName(target.ProcessName))
            if (Matches(p, target)) result.Add(p); else p.Dispose();
        lock (_gate)
            if (_current.Generation == target.Generation)
            {
                _sample = result.Select(Identify).OfType<GameProcessIdentity>().ToArray();
                _sampleTime = DateTime.UtcNow;
            }
        return result.ToArray();
    }
    public bool IsRunning()
    {
        var processes = GetProcesses();
        var running = processes.Length != 0;
            foreach (var p in processes) p.Dispose();
        return running;
    }
    public bool Matches(Process process) => Matches(process, Current);
    public bool Matches(Process process, GameTarget target)
    {
        try
        {
            if (Current.Generation != target.Generation || process.HasExited) return false;
            if (!target.IsCustom) return process.ProcessName.Equals(target.ProcessName, StringComparison.OrdinalIgnoreCase);
            var started = process.StartTime.ToUniversalTime();
            var path = ReadProcessPath(process.Id);
            return File.Exists(target.ExecutablePath) &&
                CanonicalPath(path).Equals(CanonicalPath(target.ExecutablePath), StringComparison.OrdinalIgnoreCase) &&
                !process.HasExited && process.StartTime.ToUniversalTime() == started;
        }
        catch { return false; }
    }
    public GameProcessIdentity? Identify(Process process)
    {
        var target = Current;
        if (!Matches(process, target)) return null;
        try { return new(process.Id, process.StartTime.ToUniversalTime(), target.IsCustom ? CanonicalPath(ReadProcessPath(process.Id)) : "", target.Generation); }
        catch { return null; }
    }
    public bool Matches(Process process, GameProcessIdentity identity)
    {
        try { return Current.Generation == identity.Generation && Matches(process) && process.Id == identity.Id && process.StartTime.ToUniversalTime() == identity.StartTimeUtc; }
        catch { return false; }
    }
    public bool ProtectFromCleanup(Process process)
    {
        // Be conservative for unreadable same-name instances; never clean a possibly selected target.
        try
        {
            if (process.ProcessName.Equals(Current.ProcessName, StringComparison.OrdinalIgnoreCase)) return true;
            if (!IsCustom) return false;
            var path = CanonicalPath(ReadProcessPath(process.Id));
            return path.Equals(Current.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }
    public string Status
    {
        get
        {
            var target = Current;
            if (target.IsCustom && !File.Exists(target.ExecutablePath)) return "目标文件不存在，请在设置中重新选择 EXE。";
            if (IsRunning()) return target.DisplayName + "运行中";
            if (target.IsCustom)
            {
                var sameName = Process.GetProcessesByName(target.ProcessName);
                try { foreach (var p in sameName) try { _ = ReadProcessPath(p.Id); } catch { return "目标进程路径无法读取，已跳过优化。"; } }
                finally { foreach (var p in sameName) p.Dispose(); }
            }
            return "等待 " + target.DisplayName + " 启动";
        }
    }
    public static string ValidateExecutable(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new ArgumentException("请选择存在的 EXE 文件。");
        var normalized = CanonicalPath(path);
        if (!normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("目标文件必须是 EXE。");
        if (Environment.ProcessPath is { } self && normalized.Equals(CanonicalPath(self), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("不能选择工具自身。");
        var name = Path.GetFileNameWithoutExtension(normalized);
        string[] denied = ["DeltaNFD", "DeltaOptimizer", "system", "lsass", "services", "winlogon", "smss", "csrss", "svchost", "dwm", "explorer", "cmd", "powershell", "pwsh"];
        var windows = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) + Path.DirectorySeparatorChar;
        if (denied.Contains(name, StringComparer.OrdinalIgnoreCase) || normalized.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("不能选择工具自身或系统关键程序。");
        return normalized;
    }
    public static string CanonicalPath(string path)
    {
        using var handle = CreateFileW(Path.GetFullPath(path), 0x80, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw new IOException("无法规范化文件路径。");
        var result = buffer.ToString();
        if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + result[8..];
        return result.StartsWith(@"\\?\", StringComparison.Ordinal) ? result[4..] : result;
    }
    internal static string ReadProcessPath(int id)
    {
        using var handle = OpenProcess(0x1000, false, id);
        if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var buffer = new StringBuilder(32768); var size = buffer.Capacity;
        if (!QueryFullProcessImageNameW(handle, 0, buffer, ref size)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return buffer.ToString();
    }
    public static string CustomProfileName(string path) => "Delta NFD - " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..24];
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle handle, uint flags, StringBuilder path, ref int size);
}
