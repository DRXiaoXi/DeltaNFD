using System.ComponentModel;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace DeltaNFD.Execution.Procman;

internal static class TimerRequestTrace
{
    private static readonly Guid Provider = new("331c3b3a-2005-44c2-ac5e-77220c37d6b4"); // Microsoft-Windows-Kernel-Power
    private const ulong TimerResolutionKeyword = 0x4000;
    private const int MaxEvents = 8192;

    internal sealed record Observation(int Pid, uint Period100ns, string Name, long ObservedUtcTicks, bool Ignored);

    [StructLayout(LayoutKind.Sequential)]
    private struct WnodeHeader
    {
        public uint BufferSize, ProviderId;
        public ulong HistoricalContext;
        public long Timestamp;
        public Guid Guid;
        public uint ClientContext, Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TraceProperties
    {
        public WnodeHeader Wnode;
        public uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize, LogFileMode, FlushTimer, EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten, LogBuffersLost, RealTimeBuffersLost;
        public nint LoggerThreadId;
        public uint LogFileNameOffset, LoggerNameOffset;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint StartTraceW(out ulong handle, string session, nint properties);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint EnableTraceEx2(ulong handle, in Guid provider, uint controlCode, byte level,
        ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, nint enableParameters);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint ControlTraceW(ulong handle, string? session, nint properties, uint controlCode);

    internal static (IReadOnlyList<Observation> Processes, uint KernelPeriod) Capture(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var name = $"DeltaNFD-TimerRequests-{Guid.NewGuid():N}";
        var directory = Directory.CreateTempSubdirectory("DeltaNFD-TimerRequests-").FullName;
        var file = Path.Combine(directory, "requests.etl");
        try
        {
            var size = Marshal.SizeOf<TraceProperties>();
            var nameBytes = System.Text.Encoding.Unicode.GetBytes(name + '\0');
            var fileBytes = System.Text.Encoding.Unicode.GetBytes(file + '\0');
            var totalSize = size + nameBytes.Length + fileBytes.Length;
            var buffer = Marshal.AllocHGlobal(totalSize);
            uint captureStatus;
            try
            {
                Marshal.Copy(new byte[totalSize], 0, buffer, totalSize);
                Marshal.StructureToPtr(new TraceProperties
                {
                    Wnode = new() { BufferSize = (uint)totalSize, Flags = 0x20000, ClientContext = 1, Guid = Guid.NewGuid() },
                    LogFileMode = 1, // EVENT_TRACE_FILE_MODE_SEQUENTIAL
                    MaximumFileSize = 8,
                    LoggerNameOffset = (uint)size,
                    LogFileNameOffset = (uint)(size + nameBytes.Length),
                }, buffer, false);
                Marshal.Copy(nameBytes, 0, buffer + size, nameBytes.Length);
                Marshal.Copy(fileBytes, 0, buffer + size + nameBytes.Length, fileBytes.Length);
                Check(StartTraceW(out var handle, name, buffer), "启动 Windows 定时器请求跟踪");

                try
                {
                    Check(EnableTraceEx2(handle, in Provider, 1, 5, TimerResolutionKeyword, 0, 3000, 0), "启用 Windows 定时器事件");
                    token.ThrowIfCancellationRequested();
                    captureStatus = EnableTraceEx2(handle, in Provider, 2, 5, TimerResolutionKeyword, 0, 3000, 0);
                    if (token.WaitHandle.WaitOne(300)) token.ThrowIfCancellationRequested();
                }
                finally { Check(ControlTraceW(handle, null, buffer, 1), "结束 Windows 定时器请求跟踪"); }
                var stats = Marshal.PtrToStructure<TraceProperties>(buffer);
                if (stats.EventsLost != 0 || stats.LogBuffersLost != 0) throw new IOException("Windows 定时器事件有丢失，请重试。");
            }
            finally { Marshal.FreeHGlobal(buffer); }

            using var reader = new EventLogReader(new EventLogQuery(file, PathType.FilePath));
            var observations = new List<Observation>();
            uint? kernel = null;
            for (var index = 0; ; index++)
            {
                token.ThrowIfCancellationRequested();
                using var record = reader.ReadEvent(TimeSpan.FromSeconds(2));
                if (record is null) break;
                if (index >= MaxEvents) throw new IOException("定时器事件数量超出当前检测上限。");
                if (record.ProviderId != Provider || record.Id is not (96 or 97)) continue;
                var properties = XElement.Parse(record.ToXml()).Descendants()
                    .Where(node => node.Name.LocalName == "Data" && node.Attribute("Name") is not null)
                    .ToDictionary(node => node.Attribute("Name")!.Value, node => node.Value, StringComparer.Ordinal);
                if (record.Id == 96)
                {
                    if (!TryNumber(properties.GetValueOrDefault("KernelRequestedPeriod"), out var period)
                        || !TryNumber(properties.GetValueOrDefault("KernelRequestCount"), out var requests))
                        throw new IOException("Windows 定时器状态事件格式无法识别。");
                    kernel = requests == 0 ? 0u : period;
                    continue;
                }

                if (!TryNumber(properties.GetValueOrDefault("RequestedPeriod"), out var requested)
                    || !TryNumber(properties.GetValueOrDefault("Pid"), out var pid)
                    || pid > int.MaxValue || requested == 0 || record.TimeCreated is not { } timestamp)
                    throw new IOException("Windows 定时器请求事件格式无法识别。");
                var ignored = properties.GetValueOrDefault("RequestIgnored");
                observations.Add(new((int)pid, requested, properties.GetValueOrDefault("AppName", "").TrimEnd('\0'),
                    timestamp.ToUniversalTime().Ticks, ignored is "1" or "0x1" || bool.TryParse(ignored, out var disabled) && disabled));
            }

            if (kernel is null) throw new IOException($"Windows 未返回定时器请求快照（状态码 {captureStatus}）。");
            return (observations, kernel.Value);
        }
        finally
        {
            try { File.Delete(file); Directory.Delete(directory); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool TryNumber(string? value, out uint number) =>
        value?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true
            ? uint.TryParse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out number)
            : uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);

    private static void Check(uint status, string operation)
    {
        if (status != 0) throw new Win32Exception((int)status, $"{operation}失败（Win32 错误 {status}）。");
    }
}

