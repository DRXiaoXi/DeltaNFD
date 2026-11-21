using System.Buffers.Binary;

namespace DeltaNFD.Services.CrashDiagnostics;

internal sealed record KernelDump(string Path, string Format, uint Code, ulong[] Args, DateTimeOffset? CrashTime, string Architecture);

internal static class KernelDumpReader
{
    // Fixed fields of Windows SDK mindumpdef.h DUMP_HEADER32 / DUMP_HEADER64.
    // Never follow pointers into kernel memory or read the dump body.
    internal static KernelDump Read(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] header = new byte[0x2000];
        try
        {
            stream.ReadExactly(header, 0, 8);
            if (!header.AsSpan(0, 4).SequenceEqual("PAGE"u8))
                throw new InvalidDataException("需要 Windows 内核蓝屏转储；不支持应用程序 MDMP 或其他格式。");
            bool wide = header.AsSpan(4, 4).SequenceEqual("DU64"u8);
            if (!wide && !header.AsSpan(4, 4).SequenceEqual("DUMP"u8)) throw new InvalidDataException("不支持该内核转储头格式。");
            stream.ReadExactly(header, 8, (wide ? 0x2000 : 0x1000) - 8);
            token.ThrowIfCancellationRequested();
            uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(at));
            ulong U64(int at) => BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(at));
            if (U32(wide ? 0x30 : 0x20) != (wide ? 0x8664u : 0x14cu)) throw new InvalidDataException("当前仅支持 x64 或 x86 Windows 内核转储。");
            if ((U32(wide ? 0x1050 : 0x8a0) & 0x10) != 0) throw new InvalidDataException("这是实时内核转储，不能据此判为系统蓝屏。");
            uint code = U32(wide ? 0x38 : 0x28);
            if (code is 0 or 0x45474150) throw new InvalidDataException("转储头没有有效的停止代码。");
            ulong[] args = Enumerable.Range(0, 4).Select(i => wide ? U64(0x40 + i * 8) : U32(0x2c + i * 4)).ToArray();
            ulong ticks = U64(wide ? 0xfa8 : 0xfc0);
            DateTimeOffset? time = ticks > 0 && ticks <= (ulong)DateTime.UtcNow.AddDays(1).ToFileTimeUtc()
                ? DateTimeOffset.FromFileTime((long)ticks).ToUniversalTime() : null;
            return new(path, wide ? "PAGE / DU64" : "PAGE / DUMP", code, args, time, wide ? "x64" : "x86");
        }
        catch (EndOfStreamException ex) { throw new InvalidDataException("转储文件头不完整，无法分析。", ex); }
    }
}
