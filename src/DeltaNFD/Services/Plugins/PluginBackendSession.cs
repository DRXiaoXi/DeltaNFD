using System.Buffers;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace DeltaNFD.Services.Plugins;

/// <summary>管道帧的一步读取结果。</summary>
internal readonly record struct PluginFrameRead(PluginIpcFrame? Frame, bool Disconnected)
{
    public static PluginFrameRead DisconnectedResult() => new(null, true);
}

/// <summary>供同程序集（Plugins 命名空间）内使用的读取结果包装。</summary>
internal sealed record PluginFrameOutcome(PluginIpcFrame? Frame, bool Disconnected);

/// <summary>
/// 单个插件后端的管道会话（规范第 6 节）：宿主创建随机名管道（ACL 当前用户+SYSTEM，
/// CurrentUserOnly），核对客户端 PID 为本次启动的后端，处理半帧/多帧，超时即 cancel→等待→结果未知。
/// 本类只做 IO 与协议；进程生命周期由 PluginRuntimeService 管理。
/// </summary>
internal sealed class PluginBackendSession : IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly MemoryStream _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public PluginBackendSession()
    {
        PipeName = "DeltaNFD_Plugin_" + Guid.NewGuid().ToString("N");
        _pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    public string PipeName { get; }

    public bool IsConnected => _pipe.IsConnected;

    /// <summary>等待后端连接并核对客户端 PID（规范第 6 节：服务端核对客户端 PID 为本次启动后端）。</summary>
    public async Task<bool> WaitForClientAsync(int expectedPid, TimeSpan timeout, CancellationToken cancellation)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            cts.CancelAfter(timeout);
            await _pipe.WaitForConnectionAsync(cts.Token);
        }
        catch (OperationCanceledException) { return false; }
        catch (IOException) { return false; }
        if (!_pipe.IsConnected) return false;
        return GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var clientPid) && clientPid == (uint)expectedPid;
    }

    public void WriteFrame(PluginIpcType type, Guid sessionId, Guid requestId, string payloadJson)
    {
        try { WriteFrameAsync(type, sessionId, requestId, payloadJson).GetAwaiter().GetResult(); }
        catch (OperationCanceledException ex) { throw new IOException("插件发送超时。", ex); }
    }

    public async Task WriteFrameAsync(PluginIpcType type, Guid sessionId, Guid requestId, string payloadJson)
    {
        var frame = PluginIpc.Encode(type, sessionId, requestId, payloadJson);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _writeGate.WaitAsync(timeout.Token);
        try
        {
            await _pipe.WriteAsync(frame, timeout.Token);
            await _pipe.FlushAsync(timeout.Token);
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>
    /// 读一帧，缓冲半帧与粘包；坏帧（超长/坏 JSON/重复属性）抛 PluginContractException，
    /// 由调用方断开连接；流关闭返回 Disconnected。
    /// </summary>
    public async Task<PluginFrameRead> ReadFrameAsync(TimeSpan timeout, CancellationToken cancellation)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        cts.CancelAfter(timeout);
        try
        {
            var buffer = new byte[81920];
            while (true)
            {
                if (TryDecodeBuffered(out var frame))
                    return new PluginFrameRead(frame, false);
                var read = await _pipe.ReadAsync(buffer, cts.Token);
                if (read == 0) return PluginFrameRead.DisconnectedResult();
                _pending.Write(buffer, 0, read);
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new TimeoutException("等待插件响应超时。"); }
        catch (IOException) { return PluginFrameRead.DisconnectedResult(); }
    }

    private bool TryDecodeBuffered(out PluginIpcFrame? frame)
    {
        frame = null;
        var data = _pending.GetBuffer();
        var length = (int)_pending.Length;
        if (length < 4) return false;
        var payloadLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (payloadLength is 0 or > PluginContract.MaxFrameBytes)
            throw new PluginContractException($"插件帧长度非法：{payloadLength}。");
        if (length < 4 + (int)payloadLength) return false;
        // Copy before compacting: buffered subsequent frames share the same backing array.
        var payload = new ReadOnlySequence<byte>(data.AsSpan(4, (int)payloadLength).ToArray());
        _pending.Position = 0;
        _pending.SetLength(0);
        if (length > 4 + (int)payloadLength)
            _pending.Write(data, 4 + (int)payloadLength, length - 4 - (int)payloadLength);
        if (!PluginIpc.TryParseFrame(payload, out var decoded))
            throw new PluginContractException("插件帧解析失败。");
        frame = decoded;
        return true;
    }

    /// <summary>与 ReadFrameAsync 相同但暴露给程序集内部类型（持续会话 pong 监听用）。</summary>
    internal async Task<PluginFrameOutcome> ReadFrameAsyncRaw(TimeSpan timeout, CancellationToken cancellation)
    {
        var read = await ReadFrameAsync(timeout, cancellation);
        return new PluginFrameOutcome(read.Frame, read.Disconnected);
    }

    public void Dispose()
    {
        try { if (_pipe.IsConnected) _pipe.Disconnect(); } catch { }
        _pipe.Dispose();
        _pending.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
}
