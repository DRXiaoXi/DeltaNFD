using System.Buffers.Binary;

namespace DeltaNFD.Services;

/// <summary>CPU 实验室的 x64 CACHE_RELATIONSHIP 解析；进程管理保持独立实现。</summary>
internal static class CpuCacheRelationshipReader
{
    internal static bool TryReadL3(ReadOnlySpan<byte> record, out CpuDomain domain)
    {
        domain = default;
        if (record.Length != 56 || BinaryPrimitives.ReadInt32LittleEndian(record) != 2 ||
            BinaryPrimitives.ReadInt32LittleEndian(record[4..]) != record.Length ||
            record[8] != 3 || BinaryPrimitives.ReadInt32LittleEndian(record[16..]) != 0)
            return false;
        // Windows 10 旧结构此处是 Reserved，值为 0，仍有一个 GroupMask。
        // 新结构 GroupCount=1 的单组记录布局相同；跨组缓存不能拆成多个 CCD。
        var groupCount = BinaryPrimitives.ReadUInt16LittleEndian(record[38..]);
        if (groupCount > 1) return false;
        var size = BinaryPrimitives.ReadInt32LittleEndian(record[12..]);
        var mask = BinaryPrimitives.ReadUInt64LittleEndian(record[40..]);
        if (mask == 0 || size <= 0) return false;
        domain = new CpuDomain(BinaryPrimitives.ReadUInt16LittleEndian(record[48..]), mask, size);
        return true;
    }
}
