using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeltaNFD.Execution.OptimalClock;

/// <summary>本地有界结构化日志。日志错误不能中断计时器释放或改变准入结果。</summary>
internal sealed class PendulumDiagnosticLog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _prefix;
    private readonly int _maximumBytes;
    private readonly string _os;
    private readonly string _profile;
    private readonly string? _version;
    private readonly string _session = Guid.NewGuid().ToString("N");
    private int _part;
    private long _sequence;
    internal string CurrentPath { get; private set; } = "";
    internal string Error { get; private set; } = "";

    internal PendulumDiagnosticLog(string directory, int maximumBytes = 2 * 1024 * 1024)
    {
        _directory = Path.GetFullPath(directory);
        _maximumBytes = Math.Max(1024, maximumBytes);
        try { _os = DeltaNFD.Inspection.PendulumGlobalInspection.ReadSystemVersion().ToString(); }
        catch { _os = Environment.OSVersion.Version + " (UBR unavailable)"; }
        _profile = DeltaNFD.Inspection.PendulumGlobalInspection.ExperimentalWindows10 ? "Windows10-experimental" : "Windows11";
        _version = typeof(PendulumDiagnosticLog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        _prefix = $"pendulum_{DateTime.UtcNow:yyyyMMddTHHmmssfff}_{Environment.ProcessId}_{_session}";
        Write("session-start", new
        {
            os = Environment.OSVersion.VersionString,
            runtime = Environment.Version.ToString(),
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            version = _version, profile = _profile,
        });
    }

    internal void Write(string name, object? data)
    {
        lock (_gate)
        {
            try
            {
                // 序列化先完成，循环对象等错误诊断数据不破坏已有日志；非有限数值以字符串保留。
                var line = JsonSerializer.Serialize(new
                {
                    schema = 1, utc = DateTimeOffset.UtcNow, session = _session, os = _os, profile = _profile, version = _version,
                    sequence = ++_sequence, pid = Environment.ProcessId, name, data,
                }, JsonOptions) + "\n";
                var bytes = Encoding.UTF8.GetBytes(line);
                if (bytes.Length > 64 * 1024) throw new IOException("单条诊断信息超过 64KiB。");
                Directory.CreateDirectory(_directory);
                for (var parent = new DirectoryInfo(_directory); parent is not null; parent = parent.Parent)
                    if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("日志目录含链接。");
                if (CurrentPath.Length == 0) CurrentPath = PartPath();
                if (File.Exists(CurrentPath) && (File.GetAttributes(CurrentPath) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("日志文件含链接。");
                if (File.Exists(CurrentPath) && new FileInfo(CurrentPath).Length + bytes.Length > _maximumBytes)
                {
                    _part++;
                    CurrentPath = PartPath();
                }
                using (var stream = new FileStream(CurrentPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                { stream.Write(bytes); stream.Flush(true); }
                Error = "";
                Trim();
            }
            catch (Exception ex) { Error = "钟摆计划日志保存失败：" + ex.Message; }
        }
    }

    private string PartPath() => Path.Combine(_directory, $"{_prefix}_{_part:D4}.jsonl");

    private void Trim()
    {
        // 仅管理本模块日志，最多保留 12 份（每份通常不超过 2MiB）。
        foreach (var file in new DirectoryInfo(_directory).EnumerateFiles("pendulum_*.jsonl")
            .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name).Skip(12))
        {
            if (file.FullName == CurrentPath || (file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            try { file.Delete(); } catch { }
        }
    }

    internal static void Emit(Action<string, object?>? sink, string name, object? data)
    {
        try { sink?.Invoke(name, data); } catch { /* 诊断不能影响业务或释放。 */ }
    }
}
