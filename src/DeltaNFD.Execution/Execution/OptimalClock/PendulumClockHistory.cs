using System.Text.Json;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.OptimalClock;

/// <summary>Bounded diagnostic history, fed by the existing two-second supervisor. It never writes a process policy.</summary>
internal sealed class PendulumClockHistory
{
    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;
    private readonly List<PendulumClockNotice> _items = [];
    private readonly object _gate = new();
    private int _active = -1;
    private DateTimeOffset _lastSave;
    private bool _loadFailed;
    public string Error { get; private set; } = "";
    internal PendulumClockHistory(string path, Func<DateTimeOffset>? clock = null)
    {
        _path = path; _clock = clock ?? (() => DateTimeOffset.UtcNow);
        try
        {
            if (!File.Exists(path)) return;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 256 * 1024)
                throw new IOException("诊断记录类型或大小异常。");
            var loaded = JsonSerializer.Deserialize<List<PendulumClockNotice>>(File.ReadAllText(path))
                ?? throw new IOException("诊断记录为空。");
            if (loaded.Count > 32 || loaded.Any(n => n is null || n.FirstSeenUtc == default || n.Message.Length > 2048))
                throw new IOException("诊断记录内容异常。");
            _items.AddRange(loaded);
        }
        catch (Exception ex) { _loadFailed = true; Error = "钟摆计划旧诊断记录待核对，原文件保留：" + ex.Message; }
    }
    public List<PendulumClockNotice> Snapshot { get { lock (_gate) return _items.AsEnumerable().Reverse().ToList(); } }
    internal void Observe(uint requested, uint actual)
    {
        lock (_gate) ObserveCore(requested, actual);
    }
    private void ObserveCore(uint requested, uint actual)
    {
        var now = _clock();
        var kind = actual == 0 ? "Unreadable" : (ulong)actual + 10 < requested ? "FinerRequest"
            : actual > (ulong)requested + OptimalClockController.ResolutionTolerance100ns ? "LockLost" : "";
        if (kind.Length == 0)
        {
            if (_active >= 0)
            {
                _items[_active] = _items[_active] with { LastSeenUtc = now, RecoveredUtc = now };
                _active = -1; Save();
            }
            return;
        }
        var message = kind switch
        {
            "FinerRequest" => "系统精度被更细的请求覆盖，可能有其他应用申请了更高精度；来源应用尚未确认。请核对进程管理页的定时器忽略列表。",
            "LockLost" => "系统当前精度比锁定目标更粗，锁定未生效；正在按既有监督机制重新核对。",
            _ => "系统精度当前不可读，无法确认锁定生效。",
        };
        if (_active >= 0 && _items[_active].Kind == kind && _items[_active].Requested100ns == requested)
        {
            _items[_active] = _items[_active] with { LastSeenUtc = now, Observed100ns = actual };
            if (now - _lastSave >= TimeSpan.FromMinutes(1)) Save();
            return;
        }
        _items.Add(new() { FirstSeenUtc = now, LastSeenUtc = now, Kind = kind,
            Requested100ns = requested, Observed100ns = actual, Message = message });
        if (_items.Count > 32) _items.RemoveAt(0);
        _active = _items.Count - 1;
        Save();
    }
    private void Save()
    {
        if (_loadFailed) return;
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0
                || File.Exists(_path) && (File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("诊断记录含链接。");
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, _items); file.Flush(true); }
            File.Move(temp, _path, true); _lastSave = _clock(); Error = "";
        }
        catch (Exception ex) { Error = "钟摆计划诊断记录保存未完成：" + ex.Message; }
    }
}
