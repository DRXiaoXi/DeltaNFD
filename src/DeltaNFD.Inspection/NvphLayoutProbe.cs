using System.Buffers.Binary;

namespace DeltaNFD.Inspection;

/// <summary>
/// NVPH 容器只读结构探针：区分「A 层（PSO 对象）」与「B 层（编译字节码）」。
/// 读取文件头与记录表；小容器另做有界解压识别 NVuc 辅助缓存，不写入缓存。
/// 依据：实测 A 层单条记录约 15.7–15.9 KB，B 层（NVuc / DXBC / STR 混合载荷）约 5.4–8.0 KB，
/// 两档之间存在明显间隔，因此用「单条平均占用」区分，避免为解压 zstd 引入新依赖。
/// </summary>
internal static class NvphLayoutProbe
{
    /// <summary>A 层单条平均占用下限；实测 A 层 15.7 KB 起，B 层最高约 8.0 KB。</summary>
    private const int PsoLayerMinAverage = 12 * 1024;

    private const int MinRecords = 200;
    private const long MinBytecodeUsedBytes = 4L * 1024 * 1024;
    private const int MaxRecords = 200_000;

    /// <summary>记录前缀长度候选：16 / 24 / 32 字节，由结构自校验选出。</summary>
    private static readonly int[] CandidatePrefixes = [16, 24, 32];

    internal enum NvphLayer
    {
        Unknown = 0,
        Pso = 1,
        Bytecode = 2,
        NvucAuxiliary = 3,
    }

    internal readonly record struct NvphProbeResult(
        bool Ok,
        long UsedBytes,
        int Records,
        int AverageFootprint,
        NvphLayer Layer);

    /// <summary>读取单个 NVPH 的结构特征；结构不合法或不可读时返回 Ok=false。</summary>
    internal static NvphProbeResult Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = fs.Length;
            if (length < 4096)
            {
                return default;
            }

            var head = new byte[16];
            if (fs.Read(head, 0, head.Length) != head.Length)
            {
                return default;
            }

            if (head[0] != (byte)'n' || head[1] != (byte)'v' || head[2] != (byte)'p' || head[3] != (byte)'h')
            {
                return default;
            }

            var used = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(8));
            if (used < 4096 || used > (ulong)length)
            {
                return default;
            }

            var scanLength = (int)Math.Min(8192, (long)used);
            var scan = new byte[scanLength];
            fs.Position = 0;
            var read = 0;
            while (read < scanLength)
            {
                var got = fs.Read(scan, read, scanLength - read);
                if (got <= 0)
                {
                    break;
                }

                read += got;
            }

            if (read < 12)
            {
                return default;
            }

            // 记录版本标记：低 16 位 0x1234，高字节 6..13；位于 头部 + 记录前缀 处。
            var record = new byte[32];
            // 多候选搜索仍保持原三种前缀的总记录读取上限，异常输入不能放大扫描成本。
            var remainingReads = MaxRecords * CandidatePrefixes.Length;
            for (var o = 0; o + 4 <= read; o += 4)
            {
                var v = BinaryPrimitives.ReadUInt32LittleEndian(scan.AsSpan(o));
                if ((v & 0xFFFF) != 0x1234)
                {
                    continue;
                }

                var high = (v >> 16) & 0xFF;
                if (high is < 6 or > 13)
                {
                    continue;
                }

                // 形似标记不等于记录起点。当前候选失败后，继续搜索后面的候选。
                NvphProbeResult? accepted = null;
                var acceptedHeader = 0; var acceptedPrefix = 0;
                foreach (var prefix in CandidatePrefixes)
                {
                    var header = o - prefix;
                    if (header < 0 || header > 4096 || (long)header + prefix > (long)used)
                    {
                        continue;
                    }

                    long offset = header;
                    long sumPayload = 0;
                    var records = 0;
                    var consistent = true;
                    while (offset + prefix <= (long)used && records < MaxRecords)
                    {
                        if (remainingReads-- <= 0) return default;
                        fs.Position = offset;
                        fs.ReadExactly(record.AsSpan(0, prefix));

                        // 不存在 0 长度记录，拒绝错位前缀或越过已用区的载荷。
                        var payload = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(8));
                        if (payload <= 0 || offset + prefix + payload > (long)used)
                        {
                            consistent = false;
                            break;
                        }

                        sumPayload += payload;
                        records++;
                        offset += prefix + payload;
                    }

                    // 触达上限后不能继续从后续记录起点解析后缀来绕过整文件上限。
                    if (records == MaxRecords && offset < (long)used) return default;
                    if (!consistent || records == 0)
                    {
                        continue;
                    }

                    // 自校验：记录前缀 + 载荷 必须正好铺满已用区。
                    if (records * (long)prefix + sumPayload != (long)used - header)
                    {
                        continue;
                    }

                    var average = (int)((sumPayload + records * (long)prefix) / records);
                    var layer = records < MinRecords ? NvphLayer.Unknown : average >= PsoLayerMinAverage
                        ? NvphLayer.Pso
                        : (long)used >= MinBytecodeUsedBytes
                            ? NvphLayer.Bytecode
                            : NvphLayer.Unknown;
                    // 同一标记有多种合法前缀时无法确认记录边界，不任选一种判绿。
                    if (accepted is not null) return default;
                    accepted = new NvphProbeResult(true, (long)used, records, average, layer);
                    acceptedHeader = header; acceptedPrefix = prefix;
                }
                if (accepted is { } result)
                {
                    if (result.Layer == NvphLayer.Unknown)
                        result = result with { Layer = NvphSmallCacheProbe.Read(fs, acceptedHeader, acceptedPrefix, (long)used, result.Records) };
                    if (result.Layer != NvphLayer.Bytecode && result.Layer != NvphLayer.NvucAuxiliary
                        && NvphSmallCacheProbe.ContainsLargeBytecode(fs, acceptedHeader, acceptedPrefix, (long)used, result.Records))
                        result = result with { Layer = NvphLayer.Bytecode };
                    return result;
                }
            }

            return default;
        }
        catch
        {
            return default;
        }
    }
}
