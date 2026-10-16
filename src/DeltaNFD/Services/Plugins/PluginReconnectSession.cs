using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace DeltaNFD.Services.Plugins;

internal sealed class PluginReconnectSession : IDisposable
{
    private readonly Process _process;
    private readonly NamedPipeClientStream _pipe;
    private readonly PluginHandoffEntry _record;
    private readonly Guid _session = Guid.NewGuid();

    private PluginReconnectSession(Process process, NamedPipeClientStream pipe, PluginHandoffEntry record)
    { _process = process; _pipe = pipe; _record = record; }

    public static async Task<PluginReconnectSession> ConnectAsync(PluginHandoffEntry record, CancellationToken cancellation)
    {
        var process = Process.GetProcessById(record.ProcessId);
        var pipe = new NamedPipeClientStream(".", record.ReconnectPipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var session = new PluginReconnectSession(process, pipe, record);
        try
        {
            session.CheckIdentity();
            await pipe.ConnectAsync(10_000, cancellation);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid != (uint)record.ProcessId)
                throw new IOException("重连服务端 PID 与记录不符。");
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var request = Guid.NewGuid();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            await session.WriteAsync(PluginIpcType.Reconnect, request, JsonSerializer.Serialize(new
            { nonce, hostVersion = AppVersion.NumericText, hostPid = Environment.ProcessId, runId = record.RunId.ToString("D") }), deadline.Token);
            var frame = await session.ReadAsync(deadline.Token);
            if (frame.Type != PluginIpcType.Ready || frame.SessionId != session._session || frame.RequestId != request)
                throw new IOException("重连握手会话/请求不符。");
            var ready = PluginIpc.ParseReady(frame.Payload);
            if (ready.Nonce != nonce || ready.PluginId != record.PluginId || ready.PluginVersion != record.PluginVersion || ready.Pid != record.ProcessId)
                throw new IOException("重连 nonce 或插件身份不符。");
            session.CheckIdentity();
            return session;
        }
        catch { session.Dispose(); throw; }
    }

    public async Task<PluginIpcFrame> ExchangeAsync(PluginIpcType type, string payload, TimeSpan timeout, CancellationToken cancellation)
    {
        CheckIdentity();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        var request = Guid.NewGuid();
        await WriteAsync(type, request, payload, deadline.Token);
        while (true)
        {
            var frame = await ReadAsync(deadline.Token);
            if (frame.SessionId != _session || frame.RequestId != request)
                throw new IOException("重连操作会话/请求不符。");
            if (frame.Type == PluginIpcType.Result) return frame;
            if (frame.Type != PluginIpcType.Progress)
                throw new IOException("重连操作响应类型无效。");
        }
    }

    public async Task<bool> WaitForExitAsync(CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try { await _process.WaitForExitAsync(deadline.Token); return true; }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return false; }
    }

    private void CheckIdentity()
    {
        if (_process.HasExited || _process.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) != _record.StartTimeUtcTicks ||
            !GameTargetService.CanonicalPath(GameTargetService.ReadProcessPath(_record.ProcessId)).Equals(
                GameTargetService.CanonicalPath(_record.EntryExecutable), StringComparison.OrdinalIgnoreCase))
            throw new IOException("重连目标 PID、启动时间或路径无法复核，记录保留。");
    }

    private async Task WriteAsync(PluginIpcType type, Guid request, string payload, CancellationToken cancellation) =>
        await _pipe.WriteAsync(PluginIpc.Encode(type, _session, request, payload), cancellation);

    private async Task<PluginIpcFrame> ReadAsync(CancellationToken cancellation)
    {
        var header = new byte[4];
        await _pipe.ReadExactlyAsync(header, cancellation);
        var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > PluginContract.MaxFrameBytes) throw new IOException("重连帧长度非法。");
        var bytes = new byte[4 + length];
        header.CopyTo(bytes, 0);
        await _pipe.ReadExactlyAsync(bytes.AsMemory(4), cancellation);
        PluginIpc.ParseFrame(bytes, out var frame);
        return frame;
    }

    public void Dispose() { _pipe.Dispose(); _process.Dispose(); }

    [DllImport("kernel32.dll")]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
}
