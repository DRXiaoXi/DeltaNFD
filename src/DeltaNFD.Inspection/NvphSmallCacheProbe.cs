using System.Buffers.Binary;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace DeltaNFD.Inspection;

/// <summary>只接受已知v13包裹、完整记录链与全部已解压NVuc标签；不凭小文件大小推断归属。</summary>
internal static class NvphSmallCacheProbe
{
    private const int MaxFileBytes = 4 * 1024 * 1024;
    private const int MaxRecords = 4096;
    private const int MaxFrameBytes = 128 * 1024;
    private const int MaxDecodedBytes = 8 * 1024 * 1024;

    /// <summary>大容器前8条的有界载荷检查：正向B证据优先于平均大小；不是全量载荷验证。</summary>
    internal static bool ContainsLargeBytecode(FileStream file, int header, int prefix, long used, int records)
    {
        if (file.Length < MaxFileBytes || header != 1488 || prefix != 32) return false;
        try
        {
            using var decompressor = new Decompressor();
            decompressor.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, 17);
            var envelope = new byte[144]; long offset = header;
            for (var i = 0; i < Math.Min(8, records); i++)
            {
                if (offset + envelope.Length > used) return false;
                file.Position = offset; file.ReadExactly(envelope);
                var length = BinaryPrimitives.ReadInt32LittleEndian(envelope.AsSpan(8));
                ulong Field(int p) => BinaryPrimitives.ReadUInt64LittleEndian(envelope.AsSpan(prefix + p));
                var aux = Field(80); var compressed = Field(88); var decoded = Field(96); var kind = Field(104);
                if (Field(0) != 0x000d1234 || Field(8) != 64 || Field(56) != 2 || Field(64) != 0x5678 || Field(72) != 48
                    || aux is not (1 or 8 or 16) || compressed is < 4 or > MaxFrameBytes || decoded is < 32 or > MaxFrameBytes
                    || kind is not (1 or 2) || length != 112L + (long)aux + (long)compressed || offset + prefix + length > used) return false;
                file.Position = offset + prefix + 112 + (long)aux;
                var input = new byte[(int)compressed]; file.ReadExactly(input);
                byte[] output;
                if (kind == 1)
                {
                    if (compressed != decoded) return false;
                    output = input;
                }
                else
                {
                    if (!SingleFrame(input) || Decompressor.GetDecompressedSize(input) != decoded) return false;
                    output = new byte[(int)decoded];
                    if (decompressor.Unwrap(input, output) != output.Length) return false;
                }
                var tag = BinaryPrimitives.ReadUInt32LittleEndian(output);
                if (tag is 0x6375564e or 0xdec04342 or 0x00525453 or 0x43425844 or 0x464c457f) return true;
                offset += prefix + length;
            }
        }
        catch { }
        return false;
    }

    internal static NvphLayoutProbe.NvphLayer Read(FileStream file, int header, int prefix, long used, int records)
    {
        const NvphLayoutProbe.NvphLayer unknown = NvphLayoutProbe.NvphLayer.Unknown;
        if (file.Length >= MaxFileBytes || used >= MaxFileBytes || header != 1488 || prefix != 32 || records is < 1 or > MaxRecords) return unknown;
        try
        {
            using var decompressor = new Decompressor();
            decompressor.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, 17);
            var envelope = new byte[32 + 112];
            long offset = header; var decodedBytes = 0;
            for (var index = 0; index < records; index++)
            {
                if (offset + envelope.Length > used) return unknown;
                file.Position = offset; file.ReadExactly(envelope);
                var length = BinaryPrimitives.ReadInt32LittleEndian(envelope.AsSpan(8));
                var payload = envelope.AsSpan(prefix);
                ulong Field(int position) => BinaryPrimitives.ReadUInt64LittleEndian(envelope.AsSpan(prefix + position));
                // 固定包裹中的长度必须铺满本记录，未知版本/压缩方式不猜测。
                var aux = Field(80); var compressed = Field(88); var decoded = Field(96);
                if (BinaryPrimitives.ReadUInt64LittleEndian(payload) != 0x000d1234 || Field(8) != 64
                    || Field(56) != 2 || Field(64) != 0x5678 || Field(72) != 48 || Field(104) != 2
                    || aux is not (1 or 8 or 16) || compressed is < 4 or > MaxFrameBytes || decoded is < 32 or > MaxFrameBytes
                    || length != 112L + (long)aux + (long)compressed || offset + prefix + length > used
                    || decodedBytes + (long)decoded > MaxDecodedBytes) return unknown;
                file.Position = offset + prefix + 112 + (long)aux;
                var input = new byte[(int)compressed]; file.ReadExactly(input);
                if (!SingleFrame(input)) return unknown;
                // 用声明的固定目标容量解压，帧的解压上界也必须匹配，拒绝额外串接帧与扩张输入。
                if (Decompressor.GetDecompressedSize(input) != decoded) return unknown;
                var output = new byte[(int)decoded];
                if (decompressor.Unwrap(input, output) != output.Length) return unknown;
                decodedBytes += output.Length;
                if (!output.AsSpan(0, 4).SequenceEqual("NVuc"u8))
                {
                    var tag = BinaryPrimitives.ReadUInt32LittleEndian(output);
                    return tag is 0xdec04342 or 0x00525453 or 0x43425844 or 0x464c457f
                        ? NvphLayoutProbe.NvphLayer.Bytecode : unknown;
                }
                offset += prefix + length;
            }
            return offset == used ? NvphLayoutProbe.NvphLayer.NvucAuxiliary : unknown;
        }
        catch { return unknown; }
    }

    // Zstandard frame header/block lengths, per upstream zstd_compression_format.md.
    // 明确排除串接帧，避免只看合并输出的首个NVuc标签而漏掉随后BC/STR。
    private static bool SingleFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 6 || BinaryPrimitives.ReadUInt32LittleEndian(frame) != 0xfd2fb528) return false;
        var descriptor = frame[4];
        if ((descriptor & 0x08) != 0) return false;
        var singleSegment = (descriptor & 0x20) != 0;
        var dictionaryBytes = (descriptor & 3) switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 };
        var contentBytes = (descriptor >> 6) switch { 0 => singleSegment ? 1 : 0, 1 => 2, 2 => 4, _ => 8 };
        var position = 5 + (singleSegment ? 0 : 1) + dictionaryBytes + contentBytes;
        while (position + 3 <= frame.Length)
        {
            var block = frame[position] | frame[position + 1] << 8 | frame[position + 2] << 16;
            var type = block >> 1 & 3; var size = block >> 3;
            if (type == 3 || size > MaxFrameBytes) return false;
            position += 3 + (type == 1 ? 1 : size);
            if (position > frame.Length) return false;
            if ((block & 1) != 0) return position + ((descriptor & 4) != 0 ? 4 : 0) == frame.Length;
        }
        return false;
    }
}
