using System.Text.Json;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.Procman;

internal interface INvidiaGpuShareApi : IDisposable
{
    NvidiaGpuShareInfo Read();
    void Write(NvidiaGpuShareInfo expected, int percent);
}

/// <summary>One global NVIDIA setting; persist the exact original, then write and read back. External changes win.</summary>
internal sealed class FrameNvidiaGpuShare : IDisposable
{
    private sealed record Original(int Version, string HardwareKey, int Value, int Applied, bool Confirmed = false);
    private readonly string _path;
    private readonly INvidiaGpuShareApi _api;
    private readonly Func<IDisposable> _acquire;
    private readonly object _gate = new();
    private IDisposable? _lease;
    private Original? _known;
    private bool _blocked;
    private FrameNvidiaGpuShareSnapshot _snapshot = new() { ProtocolVersion = 1 };
    internal FrameNvidiaGpuShareSnapshot Snapshot => Volatile.Read(ref _snapshot);
    internal FrameNvidiaGpuShare(string path, INvidiaGpuShareApi? api = null, Func<IDisposable>? acquire = null)
    {
        _path = path; _api = api ?? new NativeNvidiaGpuShareApi(); _acquire = acquire ?? NvidiaGpuShareLease.Acquire;
        if (File.Exists(path)) _snapshot = new() { ProtocolVersion = 1, RestorePending = true, Message = "存在 NVIDIA 比例原值记录，等待核对恢复" };
    }
    private static void RejectLinks(string path)
    {
        for (var p = Path.GetFullPath(path); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("NVIDIA 比例记录目录包含重解析点。");
    }
    private Original? Load()
    {
        RejectLinks(_path);
        if (!File.Exists(_path)) return _known is null ? null : throw new IOException("NVIDIA 比例原值记录已丢失，未把当前值当作原值。");
        if (new FileInfo(_path).Length > 4096) throw new IOException("NVIDIA 比例记录长度异常。");
        var record = JsonSerializer.Deserialize<Original>(File.ReadAllText(_path));
        if (record is null || record.Version != 1 || record.HardwareKey.Length != 64 || !record.HardwareKey.All(Uri.IsHexDigit) ||
            record.Value is < 0 or > 100 || record.Applied is < 0 or > 100) throw new IOException("NVIDIA 比例恢复记录无效。");
        return _known = record;
    }
    private void Save(Original record)
    {
        RejectLinks(_path); Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, record); file.Flush(true); }
            File.Move(temporary, _path, true); _known = record;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private void Finish(bool archive = false)
    {
        RejectLinks(_path);
        if (archive) File.Move(_path, _path + ".external-" + Guid.NewGuid().ToString("N") + ".bak");
        else File.Delete(_path);
        _known = null; _lease?.Dispose(); _lease = null;
    }
    internal bool Sync(bool active, int percent, Func<bool> allowed, bool resetBlock = false)
    {
        lock (_gate)
        {
            NvidiaGpuShareInfo? info = null;
            try
            {
                if (resetBlock) _blocked = false;
                var record = Load();
                if (!active && record is null) { Publish(false, false, false, Snapshot.Info, "未持有 NVIDIA 前后台比例改动"); return true; }
                if (_blocked && record is null) { Publish(false, true, false, Snapshot.Info, "本次帧格已暂停 NVIDIA 比例接管，请关闭后重新启用此功能再重试"); return false; }
                _lease ??= _acquire();
                info = _api.Read();
                if (!info.Supported || info.HardwareKey.Length != 64) throw new IOException("驱动未提供可核验的 NVIDIA 前后台比例；请核对显卡、驱动与 HAGS 支持。");
                if (record is not null)
                {
                    if (record.HardwareKey != info.HardwareKey) throw new IOException("NVIDIA 显卡身份已变化，保留旧记录，未写入另一张显卡。");
                    if (info.Current != record.Applied && info.Current != record.Value)
                    {
                        if (!record.Confirmed) throw new IOException($"前次写入尚未确认，当前 {info.Current}% 与目标 {record.Applied}%、原值 {record.Value}% 均不符。请在 NVIDIA App 核对并恢复原值后重试；保留恢复记录，未覆盖当前设置。");
                        Finish(archive: true); _blocked = true;
                        Publish(false, true, false, info, "检测到 NVIDIA App 或其他程序修改比例，已退出接管并归档原值；未覆盖外部设置"); return false;
                    }
                    if (!active || percent != record.Applied)
                    {
                        if (info.Current != record.Value) { _api.Write(info, record.Value); info = _api.Read(); }
                        if (info.Current != record.Value || info.HardwareKey != record.HardwareKey) throw new IOException("NVIDIA 比例原值恢复回读不一致。");
                        Finish(); record = null;
                        if (!active) { Publish(false, false, false, info, "NVIDIA 前后台比例已恢复原值"); return true; }
                        _lease ??= _acquire(); info = _api.Read();
                    }
                    else if (info.Current == record.Value && record.Value != record.Applied)
                    {
                        Finish(archive: true); _blocked = true;
                        Publish(false, true, false, info, "外部已恢复原比例，本次帧格不再重复写入"); return false;
                    }
                }
                if (!info.HagsEnabled) throw new IOException("HAGS 尚未确认实际生效；请在系统优化中核对并按需重启，本功能不会自动修改 HAGS。");
                if (!info.Allows(percent)) throw new IOException($"驱动不支持后台 {percent}%：范围 {info.Minimum}～{info.Maximum}%，步长 {info.Step}%；预设也须经驱动确认。");
                if (!allowed()) throw new IOException("帧格或目标游戏状态已变化，未申请 NVIDIA 比例。");
                if (record is null)
                {
                    if (!info.Allows(info.Current)) throw new IOException("当前 NVIDIA 原比例不在可核验的恢复范围内，未修改。");
                    record = new(1, info.HardwareKey, info.Current, percent); Save(record);
                    if (!allowed()) throw new IOException("提交前帧格状态已变化，原值记录保留供恢复。");
                    if (info.Current != percent) _api.Write(info, percent);
                    info = _api.Read();
                }
                if (info.HardwareKey != record.HardwareKey || info.Current != percent) throw new IOException("NVIDIA 前后台比例回读不一致。");
                if (!record.Confirmed) Save(record with { Confirmed = true });
                Publish(true, false, false, info, $"NVIDIA 比例已回读：前台 {100 - info.Current}% / 后台 {info.Current}%（全局设置）"); return true;
            }
            catch (Exception ex)
            {
                _blocked = true; var pending = _known is not null || File.Exists(_path);
                if (!pending) { _lease?.Dispose(); _lease = null; }
                Publish(false, true, pending, info, "NVIDIA 比例未完成：" + ex.Message); return false;
            }
        }
    }
    private void Publish(bool maintaining, bool error, bool pending, NvidiaGpuShareInfo? info, string message) =>
        Volatile.Write(ref _snapshot, new() { ProtocolVersion = 1, Maintaining = maintaining, HasError = error,
            RestorePending = pending, Info = info, Message = message, UpdatedUtc = DateTimeOffset.UtcNow });
    public void Dispose() { _lease?.Dispose(); _lease = null; _api.Dispose(); }
}
