using DeltaNFD.Inspection;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.OptimalClock;

internal sealed record DpcServiceObservation(bool Exists, bool Running, bool Verified, string ImagePath)
{
    public uint State { get; init; }
    public uint Type { get; init; }
    public uint Start { get; init; }
    public string RawImagePath { get; init; } = "";
    public long? DriverBytes { get; init; }
    public string DriverSha256 { get; init; } = "";
    public string Detail { get; init; } = "";
}
internal interface IDpcOwnedProcess : IDisposable
{
    int Pid { get; }
    long StartTicks { get; }
    bool Alive { get; }
    bool CloseNormally(int timeoutMs);
    void BeginWindowMaintenance() { }
}
internal interface IDpcSessionApi
{
    string BootId { get; }
    int ControllerPid { get; }
    long ControllerStartTicks { get; }
    void CheckStartAllowed();
    IDisposable LockInput(string path);
    DpcPendulumJournal? Read();
    void Save(DpcPendulumJournal record);
    void Archive(DpcPendulumJournal record);
    IDpcOwnedProcess Launch(string path);
    IDpcOwnedProcess? Find(int pid, long ticks, string path);
    bool ControllerAlive(int pid, long ticks);
    DpcServiceObservation Service();
    uint Current();
    void Delay(int milliseconds);
}

/// <summary>Only the player's original EXE installs its own driver. No device IOCTL or security-setting writes.</summary>
internal sealed class DpcProcessHolder(IDpcSessionApi api, Func<string> selectedPath,
    Action<string, object?>? diagnostics = null) : IOptimalClockHolderApi
{
    private IDpcOwnedProcess? _owned;
    private IDisposable? _inputLock;
    private DpcPendulumJournal? _record;
    private bool _launchAttempted;
    public bool DpcProcess => true;
    public bool KernelRequest => false;
    public uint Resolution100ns => DpcPendulumSource.Target100ns;
    public uint Granted100ns => 0; // Original EXE doesn't expose its ExSetTimerResolution return value to us.
    public uint Current100ns => api.Current();
    public string Error { get; private set; } = "";
    public bool Holding
    {
        get
        {
            if (_owned is null || _record?.Phase != "running") return false;
            if (!_owned.Alive) return false;
            var service = api.Service();
            return service.Exists && service.Running && service.Verified && service.ImagePath == _record.ServicePath;
        }
    }
    public bool Ensure(uint requested)
    {
        DpcServiceObservation? lastService = null;
        uint? lastCurrent = null;
        try
        {
            if (requested != DpcPendulumSource.Target100ns) throw new IOException("原版 DPC EXE 固定申请 1.0000ms，不支持在此路径设置 0.5ms 或其他值。");
            if (Holding) return true;
            api.CheckStartAllowed();
            if (api.Read() is not null) throw new IOException("存在上次 DPC 常驻记录，请先释放并核对；未重复启动。");
            if (api.Service().Exists) throw new IOException("已存在 dpclat_driver，可能属于外部 DPC 或研究插件。请先关闭对应程序；未接管或删除服务。");
            var path = selectedPath();
            _inputLock = api.LockInput(path);
            if (api.Read() is not null || api.Service().Exists) throw new IOException("取得会话锁后发现其他DPC状态，未启动第二份。");
            var boot = api.BootId;
            if (!Guid.TryParse(boot, out _)) throw new IOException("无法确认本次启动身份，未运行 DPC。");
            _record = new() { BootId = boot, ExePath = path, ControllerPid = api.ControllerPid, ControllerStartTicks = api.ControllerStartTicks };
            api.Save(_record); // Intent is durable before Process.Start; a crash in launch cannot be replayed blindly.
            _launchAttempted = true;
            _owned = api.Launch(path);
            _record.Pid = _owned.Pid; _record.StartTicks = _owned.StartTicks;
            api.Save(_record);
            PendulumDiagnosticLog.Emit(diagnostics, "dpc-process-started", new { _record.Id, _record.Pid, _record.StartTicks, _record.ExePath });
            for (var i = 0; i < 60; i++)
            {
                if (!_owned.Alive) throw new IOException("DPC 提前退出，可能是系统拒绝驱动；请查看原程序提示。未修改系统安全策略。");
                var service = api.Service(); lastService = service;
                var current = api.Current(); lastCurrent = current; // Read even if service identity fails, so the failed predicate is visible.
                if (i % 4 == 0 || i == 59)
                    PendulumDiagnosticLog.Emit(diagnostics, "dpc-process-readiness", new { attempt = i + 1, processAlive = true,
                        service, current100ns = current, target100ns = DpcPendulumSource.Target100ns });
                if (service.Exists && service.Running && service.Verified && DpcReadbackAccepted(current))
                {
                    _record.ServicePath = service.ImagePath; _record.Phase = "running"; api.Save(_record);
                    try { _owned.BeginWindowMaintenance(); }
                    catch (Exception window) { PendulumDiagnosticLog.Emit(diagnostics, "dpc-window-maintenance-error", new { error = window.ToString() }); }
                    Error = ""; return true;
                }
                api.Delay(250);
            }
            throw new IOException("15 秒核验超时：" + DescribeReadiness(lastService, lastCurrent) + "。请查看 DPC 窗口或系统错误。");
        }
        catch (Exception ex)
        {
            var failure = ex.Message;
            PendulumDiagnosticLog.Emit(diagnostics, "dpc-process-start-failed", new { error = ex.ToString(),
                service = lastService, current100ns = lastCurrent, target100ns = DpcPendulumSource.Target100ns });
            if (!_launchAttempted && _record is not null) { try { if (api.Read() is null) _record = null; } catch { } }
            // Only our returned Process object is eligible for this best-effort close.
            if (_owned is not null) { try { Stop(); } catch (Exception close) { failure += "；释放待核对：" + close.Message; } }
            Error = failure; // Stop clears stale status on success; it must not erase this failed start's cause.
            if (_record is null) { _inputLock?.Dispose(); _inputLock = null; }
            return false;
        }
    }
    internal static bool DpcReadbackAccepted(uint current) => current is > 0 and <= DpcPendulumSource.Target100ns;
    internal static string DescribeReadiness(DpcServiceObservation? service, uint? current)
    {
        var state = service is null ? "服务状态未读到"
            : $"服务{(service.Exists ? "存在" : "不存在")}、{(service.Running ? "运行中" : "未运行")}、驱动身份{(service.Verified ? "匹配" : "未通过")}";
        var timer = current is { } value ? $"当前分辨率 {value / 10000.0:0.0000}ms（{value} ×100ns）" : "分辨率未读到";
        return state + "；" + timer + (service?.Detail is { Length: > 0 } detail ? "；" + detail : "");
    }
    public void Stop()
    {
        var record = _record ?? api.Read();
        if (record is null) { DisposeLocal(); return; } // External DPC isn't our cleanup responsibility.
        if (record.BootId != api.BootId)
        {
            if (api.Service().Exists) throw new IOException("重启后仍有 DPC 服务；不删除身份不明的遗留服务，请人工核对。");
            api.Archive(record); _record = null; DisposeLocal(); return;
        }
        if (record.ControllerPid != api.ControllerPid && api.ControllerAlive(record.ControllerPid, record.ControllerStartTicks))
        {
            if (_record is null && _owned is null) { DisposeLocal(); return; }
            throw new IOException("另一钟摆后端仍拥有此 DPC，未代它关闭。");
        }
        if (record.Pid <= 0)
            throw new IOException("上次启动在进程身份落盘前中断，不能猜测进程。请关闭 DPC 并重启 Windows 后再核对；记录已保留。");
        using var recovered = _owned is null ? api.Find(record.Pid, record.StartTicks, record.ExePath) : null;
        var process = _owned ?? recovered;
        if (process is not null && process.Alive && !process.CloseNormally(10000))
            throw new IOException("本工具启动的 DPC 未正常退出；没有强制结束，请手动关闭其窗口后再核对。");
        for (var i = 0; i < 40; i++)
        {
            if (!api.Service().Exists)
            {
                api.Archive(record); _record = null; DisposeLocal(); Error = ""; return;
            }
            api.Delay(250);
        }
        throw new IOException("DPC 退出后的服务清理尚未确认；未直接停止/删除服务，常驻记录已保留。");
    }
    private void DisposeLocal()
    {
        _owned?.Dispose(); _owned = null;
        _inputLock?.Dispose(); _inputLock = null;
        _launchAttempted = false;
    }
}
