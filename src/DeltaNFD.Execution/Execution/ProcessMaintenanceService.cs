using System.Diagnostics;
using System.Runtime.InteropServices;
using DeltaNFD.Backends;

namespace DeltaNFD.Services;

public sealed class ProcessMaintenanceService
{
    private const string GameProcessName = DeltaForceLocator.GameProcessName;
    /// <summary>系统关键进程白名单：绝不出现在可清理列表，也不做内存整理。</summary>
    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "csrss", "wininit", "winlogon", "smss", "lsass", "services",
        "svchost", "dwm", "explorer", "fontdrvhost", "sihost", "taskhostw", "conhost", "runtimebroker",
        "ctfmon", "audiodg", "wudfhost", "spoolsv", "searchindexer", "securityhealthservice",
        "msmpeng", "nissrv", "memcompression", "wudfcompanionhost", "dashboard",
        "DeltaNFD", "DeltaNFD.exe", "powershell", "pwsh", "cmd", "conhost", "openconsole",
        "applicationframehost", "systemsettings", "shellexperiencehost", "startmenuexperiencehost",
        "searchhost", "textinputhost", "lockapp", "phoneexperiencehost",
        GameProcessName, "sguard64", "sguardsvc64", "sguardmk64", "sguard64e",
        "ace-base", "ace-guimgr", "sguard",
    };

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


    public List<BackgroundProcessDto> Scan(CancellationToken cancellationToken = default)
    {
        var result = new List<BackgroundProcessDto>();
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (IsProtected(process)) continue;
                    _ = process.Handle;
                    var path = GameTargetService.CanonicalPath(GameTargetService.ReadProcessPath(process.Id));
                    var started = process.StartTime.ToUniversalTime().Ticks;
                    var mb = process.WorkingSet64 / 1024.0 / 1024.0;
                    if (mb < 10 || process.HasExited) continue;
                    var category = CategoryMap.FirstOrDefault(c => process.ProcessName.Contains(c.Key, StringComparison.OrdinalIgnoreCase)).Category ?? "其他";
                    result.Add(new(process.Id, started, path, process.ProcessName, mb, category));
                }
                catch { /* Unreadable identities must never become actionable entries. */ }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        return result.OrderByDescending(p => p.MemoryMb).ToList();
    }

    public static bool IdentityMatches(Process process, BackgroundProcessDto expected)
    {
        try
        {
            _ = process.Handle;
            return expected.Pid > 0 && expected.StartTimeUtcTicks > 0
                && Path.IsPathFullyQualified(expected.ExecutablePath)
                && process.Id == expected.Pid
                && process.StartTime.ToUniversalTime().Ticks == expected.StartTimeUtcTicks
                && GameTargetService.CanonicalPath(GameTargetService.ReadProcessPath(process.Id))
                    .Equals(expected.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                && !process.HasExited;
        }
        catch { return false; }
    }

    private static bool IsProtected(Process process)
    {
        var name = process.ProcessName;
        if (process.Id == Environment.ProcessId || ProtectedProcesses.Contains(name)
            || name.StartsWith("DeltaNFD.", StringComparison.OrdinalIgnoreCase)
            || name.Equals("DeltaOptimizer", StringComparison.OrdinalIgnoreCase))
            return true;
        // Reload persisted target for each item; worker singleton state may predate a target switch.
        var target = new GameTargetService(AppSettingsStore.DefaultPath);
        if (target.ProtectFromCleanup(process)) return true;
        var path = GameTargetService.CanonicalPath(GameTargetService.ReadProcessPath(process.Id));
        var appRoot = Path.TrimEndingDirectorySeparator(BackendEnvironment.AppRoot) + Path.DirectorySeparatorChar;
        return path.StartsWith(appRoot, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ProcessMaintenanceReport> ApplyAsync(ProcessMaintenanceArgs args, bool trim,
        CancellationToken cancellationToken, Action<string>? progress = null)
    {
        if (args.Processes is null || args.Processes.Count is < 1 or > 256
            || args.Processes.Any(p => p is null) || args.Processes.Select(p => p.Pid).Distinct().Count() != args.Processes.Count)
            throw new ArgumentException("进程批次无效：应包含 1–256 个不重复的进程身份。");
        var items = new List<ProcessMaintenanceItem>();
        foreach (var expected in args.Processes)
        {
            if (cancellationToken.IsCancellationRequested) break;
            progress?.Invoke((trim ? "正在整理 " : "正在结束 ") + expected.Name + "…");
            try
            {
                using var process = Process.GetProcessById(expected.Pid);
                if (!IdentityMatches(process, expected))
                {
                    items.Add(new(expected.Pid, "failed", "进程身份已变化或无法读取，未操作。", 0));
                    continue;
                }
                if (IsProtected(process))
                {
                    items.Add(new(expected.Pid, "failed", "系统、反作弊、工具或目标游戏进程受保护，未操作。", 0));
                    continue;
                }
                if (!ElevationHelper.IsElevated)
                {
                    items.Add(new(expected.Pid, "failed", ElevationHelper.NotElevatedMessage, 0));
                    continue;
                }
                cancellationToken.ThrowIfCancellationRequested();
                var before = process.WorkingSet64;
                if (trim)
                {
                    if (!EmptyWorkingSet(process.Handle))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    try
                    {
                        process.Refresh();
                        items.Add(new(expected.Pid, "ok", "工作集已整理。", Math.Max(0, before - process.WorkingSet64) / 1024.0 / 1024.0));
                    }
                    catch
                    {
                        items.Add(new(expected.Pid, "ok", "工作集整理请求成功；无法读取后续工作集，释放量未知。", 0));
                    }
                }
                else
                {
                    process.Kill(entireProcessTree: false);
                    // Once termination is requested, settle its outcome even if cancellation arrives.
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch (TimeoutException)
                    {
                        items.Add(new(expected.Pid, "unknown", "已请求结束但未确认退出，不计入完成数量。", 0));
                        continue;
                    }
                    items.Add(new(expected.Pid, "ok", "已结束 " + expected.Name + "（原工作集体积为估算，不代表净释放内存）。",
                        before / 1024.0 / 1024.0));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (ArgumentException) { items.Add(new(expected.Pid, "exited", "进程已退出，未执行结束，不计入释放量。", 0)); }
            catch (Exception ex) { items.Add(new(expected.Pid, "failed", "进程操作失败：" + ex.Message, 0)); }
        }
        foreach (var item in items)
            Log.Info($"进程维护：action={(trim ? "trim" : "kill")}, pid={item.Pid}, status={item.Status}, memoryMb={item.MemoryMb:F2}, message={item.Message}");
        return new(items, cancellationToken.IsCancellationRequested);
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(nint process);
}
