using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeltaNFD.Services;

/// <summary>跨主程序/短时脱机助手共享的原子状态仓库。</summary>
public sealed class OfflineModeStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly Lazy<OfflineModeStateStore> DefaultStore =
        new(() => new OfflineModeStateStore(Path.Combine(AppDataPaths.Root, "offline-mode.json")));
    public static OfflineModeStateStore Default => DefaultStore.Value;

    private readonly string _path;
    private readonly string _mutexName;

    public OfflineModeStateStore(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("状态文件路径必须是绝对路径。", nameof(path));
        _path = Path.GetFullPath(path);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_path.ToUpperInvariant())))[..20];
        _mutexName = $"Global\\DeltaNFD_OfflineMode_{digest}";
    }

    public string PathName => _path;

    public OfflineModeState Read()
    {
        if (!TryRead(out var state, out var error)) throw new InvalidDataException(error);
        return state;
    }

    public bool TryRead(out OfflineModeState state, out string error)
    {
        var loaded = new OfflineModeState();
        var readError = "";
        var succeeded = WithMutex(() => TryReadUnlocked(out loaded, out readError), out var lockError);
        state = loaded;
        error = succeeded ? readError : string.IsNullOrWhiteSpace(lockError) ? readError : lockError;
        return succeeded;
    }

    public bool TryUpdate(Action<OfflineModeState> update, out string error)
    {
        ArgumentNullException.ThrowIfNull(update);
        var updateError = "";
        var succeeded = WithMutex(() =>
        {
            if (!TryReadUnlocked(out var state, out updateError)) return false;
            try { update(state); }
            catch (Exception ex) { updateError = "更新脱机状态失败：" + ex.Message; return false; }
            state.UpdatedUtc = DateTimeOffset.UtcNow;
            if (!IsValid(state, out updateError)) return false;
            return TryWriteUnlocked(state, out updateError);
        }, out var lockError);
        error = succeeded ? updateError : string.IsNullOrWhiteSpace(lockError) ? updateError : lockError;
        return succeeded;
    }

    private bool TryReadUnlocked(out OfflineModeState state, out string error)
    {
        state = new();
        error = "";
        try
        {
            try
            {
                if ((File.GetAttributes(_path) & FileAttributes.Directory) != 0)
                    throw new InvalidDataException("脱机状态路径不是文件。");
            }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 8 * 1024 * 1024) throw new InvalidDataException("脱机状态文件过大。");
            state = JsonSerializer.Deserialize<OfflineModeState>(stream, JsonOptions) ?? throw new InvalidDataException("脱机状态文件为空。");
            if (!IsValid(state, out error)) { state = new(); return false; }
            return true;
        }
        catch (Exception ex)
        {
            state = new();
            error = "脱机状态文件不可读或损坏，已阻止自动处理：" + ex.Message;
            return false;
        }
    }

    private bool TryWriteUnlocked(OfflineModeState state, out string error)
    {
        error = "";
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory)) { error = "脱机状态文件没有父目录。"; return false; }
        var tempPath = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, state, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, _path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            error = "保存脱机状态失败：" + ex.Message;
            return false;
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private static bool IsValid(OfflineModeState state, out string error)
    {
        error = "";
        if (!Enum.IsDefined(state.Status) || !Enum.IsDefined(state.HelperCompletionStatus))
        { error = "脱机状态枚举无效。"; return false; }
        if (state.SchemaVersion != 1) { error = $"不支持脱机状态版本 {state.SchemaVersion}。"; return false; }
        if (state.OfflineModeEnabled && state.SavedPreferences is null)
        { error = "脱机模式已标记启用，但缺少原配置快照。"; return false; }
        if (state.HelperProcessId.HasValue != state.HelperStartTimeUtcTicks.HasValue)
        { error = "脱机助手 PID 与启动时间记录不完整。"; return false; }
        if (state.HelperProcessId is <= 0 || state.HelperStartTimeUtcTicks is <= 0 ||
            state.HelperStartTimeUtcTicks > DateTime.MaxValue.Ticks)
        { error = "脱机助手身份无效。"; return false; }
        if (state.HelperCompletionStatus is not (OfflineModeStatus.Ready or OfflineModeStatus.Kept or OfflineModeStatus.Failed))
        { error = "脱机助手完成状态无效。"; return false; }
        if (state.CpuSetChanges is null) { error = "脱机 CPU Sets 恢复清单缺失。"; return false; }
        foreach (var item in state.CpuSetChanges)
        {
            if (item is null || !Enum.IsDefined(item.Api) || !Enum.IsDefined(item.Status))
            { error = "CPU Sets 清单条目或枚举无效。"; return false; }
            if (item.ProcessId <= 0 || item.StartTimeUtcTicks <= 0 || item.StartTimeUtcTicks > DateTime.MaxValue.Ticks ||
                !Path.IsPathFullyQualified(item.ExecutablePath))
            { error = "脱机 CPU Sets 清单包含无效进程身份。"; return false; }
            if (item.OriginalCpuSetIds is null || item.OriginalMasks is null ||
                item.AppliedCpuSetIds is null || item.AppliedMasks is null)
            { error = "脱机 CPU Sets 清单缺少原始集合数据。"; return false; }
            if (item.Api == OfflineCpuSetApi.Windows10Ids && item.OriginalMasks.Count != 0 ||
                item.Api == OfflineCpuSetApi.Windows10Ids && item.AppliedMasks.Count != 0 ||
                item.Api == OfflineCpuSetApi.Windows11Masks && item.OriginalCpuSetIds.Count != 0 ||
                item.Api == OfflineCpuSetApi.Windows11Masks && item.AppliedCpuSetIds.Count != 0)
            { error = "脱机 CPU Sets 原值与系统 API 类型不匹配。"; return false; }
            if (item.OriginalCpuSetIds.Distinct().Count() != item.OriginalCpuSetIds.Count ||
                item.AppliedCpuSetIds.Distinct().Count() != item.AppliedCpuSetIds.Count ||
                item.OriginalMasks.Any(m => m.Mask == 0) || item.AppliedMasks.Any(m => m.Mask == 0) ||
                item.OriginalMasks.Select(m => m.Group).Distinct().Count() != item.OriginalMasks.Count ||
                item.AppliedMasks.Select(m => m.Group).Distinct().Count() != item.AppliedMasks.Count)
            { error = "CPU Sets 清单包含重复或无效集合。"; return false; }
            if (item.OriginalAssignmentWasSet != (item.Api == OfflineCpuSetApi.Windows10Ids ? item.OriginalCpuSetIds.Count > 0 : item.OriginalMasks.Count > 0) ||
                item.AppliedAssignmentWasSet != (item.Api == OfflineCpuSetApi.Windows10Ids ? item.AppliedCpuSetIds.Count > 0 : item.AppliedMasks.Count > 0))
            { error = "CPU Sets 是否已设置的标记与集合不一致。"; return false; }
        }
        var duplicate = state.CpuSetChanges.GroupBy(x => (x.ProcessId, x.StartTimeUtcTicks, x.ExecutablePath), StringTupleComparer.Instance)
            .Any(g => g.Count() > 1);
        if (duplicate) { error = "脱机 CPU Sets 清单包含重复进程记录。"; return false; }
        return true;
    }

    private bool WithMutex(Func<bool> action, out string error)
    {
        error = "";
        try
        {
            using var mutex = new Mutex(false, _mutexName);
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) { error = "等待脱机状态锁超时。"; return false; }
                return action();
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
        catch (Exception ex)
        {
            error = "无法锁定脱机状态文件：" + ex.Message;
            return false;
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    private sealed class StringTupleComparer : IEqualityComparer<(int ProcessId, long StartTimeUtcTicks, string ExecutablePath)>
    {
        public static readonly StringTupleComparer Instance = new();
        public bool Equals((int ProcessId, long StartTimeUtcTicks, string ExecutablePath) x,
            (int ProcessId, long StartTimeUtcTicks, string ExecutablePath) y)
            => x.ProcessId == y.ProcessId && x.StartTimeUtcTicks == y.StartTimeUtcTicks &&
               x.ExecutablePath.Equals(y.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((int ProcessId, long StartTimeUtcTicks, string ExecutablePath) value)
            => HashCode.Combine(value.ProcessId, value.StartTimeUtcTicks, StringComparer.OrdinalIgnoreCase.GetHashCode(value.ExecutablePath));
    }
}
