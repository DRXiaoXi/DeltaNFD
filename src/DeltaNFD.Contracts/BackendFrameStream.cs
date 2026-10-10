namespace DeltaNFD.Backends;

/// <summary>在流上读写长度前缀帧的共享助手（宿主与后端共用，流式处理半帧/多帧）。</summary>
public static class BackendFrameStream
{
    /// <summary>读取一帧；对端正常关闭返回 null；协议违规抛 <see cref="BackendContractException"/>。</summary>
    public static async Task<BackendFrame?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[4];
        var read = await ReadAtMostAsync(stream, lengthBytes, cancellationToken);
        if (read == 0) return null;
        if (read != 4)
            throw new BackendContractException("帧长度前缀不完整，连接已损坏。");

        var length = (uint)(lengthBytes[0] | (lengthBytes[1] << 8) | (lengthBytes[2] << 16) | (lengthBytes[3] << 24));
        if (length is 0 or > BackendProtocol.MaxFrameBytes)
            throw new BackendContractException($"帧长度非法：{length}（允许 1..{BackendProtocol.MaxFrameBytes}）。");

        var payload = new byte[length];
        if (await ReadAtMostAsync(stream, payload, cancellationToken) != payload.Length)
            throw new BackendContractException("帧内容不完整，连接已损坏。");

        var sequence = new System.Buffers.ReadOnlySequence<byte>(payload);
        if (!BackendProtocol.TryParseFrame(sequence, out var frame))
            throw new BackendContractException("帧解析失败。");
        return frame;
    }

    public static async Task WriteFrameAsync(Stream stream, byte[] frame, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<int> ReadAtMostAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken);
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}
