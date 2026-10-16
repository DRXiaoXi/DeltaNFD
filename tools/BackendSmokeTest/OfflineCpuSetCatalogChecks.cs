using System.Runtime.InteropServices;
using DeltaNFD.Native;

internal static class OfflineCpuSetCatalogChecks
{
    public static void Run()
    {
        var entries = Enumerable.Range(0, 8)
            .Select(i => new SystemCpuSetDescriptor((uint)(100 + i), 0, (byte)i, 0))
            .ToArray();

        Require(OfflineCpuSetCatalog.TryMapMaskToIds(entries, 1, 0, 0b0101_0101, out var ids, out _),
            "single-group mask should map");
        Require(ids.SequenceEqual(new uint[] { 100, 102, 104, 106 }), "IDs should map by logical processor index");

        Require(!OfflineCpuSetCatalog.TryMapMaskToIds(entries, 2, 0, 0b11, out _, out _),
            "multi-group topology must be rejected");
        Require(!OfflineCpuSetCatalog.TryMapMaskToIds(entries, 1, 0, 0, out _, out _),
            "empty masks must be rejected");
        Require(!OfflineCpuSetCatalog.TryMapMaskToIds(entries, 1, 0, 0x1_0000_0000UL, out _, out _),
            "a bit with no CPU Set record must be rejected");

        var allocated = entries.ToArray();
        allocated[2] = allocated[2] with { Flags = 0x02 };
        Require(!OfflineCpuSetCatalog.TryMapMaskToIds(allocated, 1, 0, 0b0100, out _, out _),
            "CPU Sets reserved for another process must be rejected");
        allocated[2] = allocated[2] with { Flags = 0x06 };
        Require(OfflineCpuSetCatalog.TryMapMaskToIds(allocated, 1, 0, 0b0100, out _, out _),
            "CPU Sets allocated to the target process remain usable");

        var duplicate = entries.Append(entries[0] with { Id = 999 }).ToArray();
        Require(!OfflineCpuSetCatalog.TryMapMaskToIds(duplicate, 1, 0, 0b1, out _, out _),
            "duplicate logical processor mappings must be rejected");

        CheckVariableLengthParser();
        Console.WriteLine("脱机 CPU Sets 清单与掩码映射检查通过。");
    }

    private static void CheckVariableLengthParser()
    {
        const int entrySize = 32;
        var buffer = Marshal.AllocHGlobal(entrySize * 2);
        try
        {
            for (var i = 0; i < entrySize * 2; i++) Marshal.WriteByte(buffer, i, 0);
            WriteCpuSet(buffer, 0, id: 41, group: 0, logicalIndex: 0, flags: 0);
            WriteCpuSet(buffer, entrySize, id: 42, group: 0, logicalIndex: 1, flags: 0x02);

            Require(OfflineCpuSetCatalog.TryParse(buffer, entrySize * 2, out var parsed, out _),
                "valid variable-length records should parse");
            Require(parsed.Length == 2 && parsed[1].Id == 42 && parsed[1].IsAvailable == false,
                "parser should preserve IDs and allocation flags");

            Marshal.WriteInt32(buffer, entrySize, 0x7FFF);
            Require(!OfflineCpuSetCatalog.TryParse(buffer, entrySize * 2, out _, out _),
                "truncated record sizes should be rejected");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void WriteCpuSet(IntPtr buffer, int offset, uint id, ushort group, byte logicalIndex, byte flags)
    {
        Marshal.WriteInt32(buffer, offset, 32);
        Marshal.WriteInt32(buffer, offset + 4, 0);
        Marshal.WriteInt32(buffer, offset + 8, unchecked((int)id));
        Marshal.WriteInt16(buffer, offset + 12, unchecked((short)group));
        Marshal.WriteByte(buffer, offset + 14, logicalIndex);
        Marshal.WriteByte(buffer, offset + 19, flags);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
