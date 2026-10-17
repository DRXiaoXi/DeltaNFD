using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.RegularExpressions;
using DeltaNFD.Services;

namespace DeltaNFD.Execution.Procman;

/// <summary>
/// 进程管理专用读者：Windows 核/缓存关系 + CPU Sets + Intel 逐核 CPUID。
/// 与 CPU 实验室没有服务、模型或识别算法依赖。所有亲和性探测仅作用于专用线程。
/// </summary>
public static class ProcmanCpuTopologyReader
{
    private const uint RelationAll = 0xffff;
    private const int MaxBuffer = 16 * 1024 * 1024;
    [StructLayout(LayoutKind.Sequential)]
    private struct GroupAffinity
    {
        public ulong Mask;
        public ushort Group, Reserved0, Reserved1, Reserved2;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorNumber { public ushort Group; public byte Number, Reserved; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(uint relationship, nint buffer, ref uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemCpuSetInformation(nint buffer, uint length, out uint returnedLength, nint process, uint flags);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentThread();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadGroupAffinity(nint thread, ref GroupAffinity affinity, out GroupAffinity previous);
    [DllImport("kernel32.dll")] private static extern void GetCurrentProcessorNumberEx(out ProcessorNumber number);

    private delegate bool BufferQuery(nint buffer, ref uint length);
    private static byte[] Query(BufferQuery query)
    {
        uint length = 0;
        query(0, ref length);
        for (var attempt = 0; attempt < 3 && length is > 0 and <= MaxBuffer; attempt++)
        {
            var capacity = length;
            var buffer = Marshal.AllocHGlobal((int)capacity);
            try
            {
                if (query(buffer, ref length))
                {
                    if (length > capacity) throw new InvalidDataException("拓扑返回长度超过缓冲区。");
                    var bytes = new byte[length];
                    Marshal.Copy(buffer, bytes, 0, bytes.Length);
                    return bytes;
                }
                if (Marshal.GetLastWin32Error() != 122) break;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return [];
    }

    public static ProcmanTopologyResult Read()
    {
        var groups = ProcmanTopology.SystemGroupMasks().Select(g => new ProcmanGroupMask(g.Group, g.Mask)).ToList();
        var notes = new List<string>();
        byte[] relations = [], cpuSets = [];
        try { relations = Query((nint p, ref uint n) => GetLogicalProcessorInformationEx(RelationAll, p, ref n)); }
        catch (Exception ex) { notes.Add("核关系读取失败：" + ex.GetType().Name); }
        try { cpuSets = Query((nint p, ref uint n) => GetSystemCpuSetInformation(p, n, out n, 0, 0)); }
        catch (Exception ex) { notes.Add("CPU 集合读取失败：" + ex.GetType().Name); }
        var name = ReadBrand();
        ProcmanTopologyResult result;
        try { result = Decode(groups, relations, cpuSets, name); }
        catch (InvalidDataException)
        {
            // 丢弃整个损坏数据源，避免部分记录造成错误的单线程/CCD选择。
            notes.Add("拓扑记录不完整，仅提供逻辑 CPU 编号。");
            result = Decode(groups, [], [], name);
        }
        DetectIntelCoreKinds(result.Processors);
        if (relations.Length == 0) notes.Add("Windows 核/缓存关系不可用。");
        if (cpuSets.Length == 0) notes.Add("CPU 集合信息不可用。");
        notes.Add(result.Processors.Any(p => p.CoreKind.Length > 0)
            ? "P/E 类型由逐核 CPUID 确认。" : "P/E 类型未确认，不按编号或性能等级猜测。");
        notes.Add(result.Processors.Any(p => p.Ccd.HasValue)
            ? "CCD 为已知 Ryzen 芯粒型号的共享 L3 分组推定；编号是本工具本机编号。"
            : "CCD 未确认；可按已识别的 L3 或物理 Die 分组选择。");
        return new ProcmanTopologyResult
        {
            Groups = result.Groups, Processors = result.Processors, ProcessorName = name,
            DetectionNote = string.Join(" ", notes)
        };
    }

    internal static IReadOnlyList<ProcmanGroupMaskValue> ReadGroupMasks()
    {
        try
        {
            var bytes = Query((nint p, ref uint n) => GetLogicalProcessorInformationEx(4, p, ref n));
            return Decode([], bytes, [], "").Groups.Select(g => new ProcmanGroupMaskValue(g.Group, g.Mask)).ToList();
        }
        catch { return []; }
    }

    // 独立的纯解析入口，供乱序、多处理器组和损坏记录测试。
    internal static ProcmanTopologyResult Decode(List<ProcmanGroupMask> groups, byte[] relations, byte[] cpuSets, string name)
    {
        foreach (var (offset, size) in Records(relations, sizeFirst: false))
        {
            var span = relations.AsSpan(offset, size);
            if (U32(span, 0) != 4) continue;
            Need(span, 32);
            var count = U16(span, 10);
            if (count == 0 || count > 4096) throw new InvalidDataException("处理器组数量无效。");
            Need(span, 32 + count * 48);
            groups = Enumerable.Range(0, count).Select(g => new ProcmanGroupMask((ushort)g,
                BinaryPrimitives.ReadUInt64LittleEndian(relations.AsSpan(offset + 32 + g * 48 + 40, 8))))
                .Where(g => g.Mask != 0).ToList();
        }
        var cpus = groups.OrderBy(g => g.Group).SelectMany(g => Enumerable.Range(0, 64)
            .Where(i => (g.Mask & (1UL << i)) != 0).Select(i => new ProcmanLogicalCpu { Group = g.Group, Index = i })).ToList();
        var cores = 0; var packages = 0; var dies = 0; var caches = 0;
        foreach (var (offset, size) in Records(relations, sizeFirst: false))
        {
            var span = relations.AsSpan(offset, size);
            var type = U32(span, 0);
            if (type is 0 or 3 or 5)
            {
                Need(span, 32);
                var members = Members(cpus, span, 32, U16(span, 30));
                var id = type == 0 ? cores++ : type == 3 ? packages++ : dies++;
                for (var thread = 0; thread < members.Count; thread++)
                {
                    var cpu = members[thread];
                    if (type == 0)
                    {
                        if (cpu.Core.HasValue) throw new InvalidDataException("物理核记录重叠。");
                        cpu.Core = id; cpu.Thread = thread; cpu.ThreadCount = members.Count; cpu.EfficiencyClass = span[9];
                    }
                    else if (type == 3) cpu.Package = id;
                    else cpu.Die = id;
                }
            }
            else if (type == 2)
            {
                Need(span, 40);
                if (span[8] != 3 || U32(span, 16) != 0) continue; // unified L3 only
                var count = U16(span, 38);
                // Windows 10 旧布局的 GroupCount 是保留的零，仍有一个 GroupMask。
                var members = Members(cpus, span, 40, count == 0 ? 1 : count);
                foreach (var cpu in members)
                {
                    if (cpu.L3.HasValue) throw new InvalidDataException("L3 记录重叠。");
                    cpu.L3 = caches; cpu.L3Bytes = U32(span, 12);
                }
                caches++;
            }
            else if (type is 1 or 6)
            {
                Need(span, 32);
                var count = type == 1 ? 1 : U16(span, 30);
                foreach (var cpu in Members(cpus, span, 32, count)) cpu.NumaNode = checked((int)U32(span, 8));
            }
        }
        var setCoreKeys = new Dictionary<(ushort, byte), List<ProcmanLogicalCpu>>();
        foreach (var (offset, size) in Records(cpuSets, sizeFirst: true))
        {
            var span = cpuSets.AsSpan(offset, size);
            if (U32(span, 4) != 0) continue;
            Need(span, 32);
            var setGroup = U16(span, 12); var setIndex = span[14];
            var cpu = cpus.SingleOrDefault(c => c.Group == setGroup && c.Index == setIndex);
            if (cpu is null) continue;
            if (cpu.CpuSetId.HasValue) throw new InvalidDataException("CPU 集合记录重复。");
            cpu.CpuSetId = U32(span, 8); cpu.EfficiencyClass = span[18];
            var key = (cpu.Group, span[15]);
            if (!setCoreKeys.TryGetValue(key, out var members)) setCoreKeys[key] = members = [];
            members.Add(cpu);
        }
        foreach (var members in setCoreKeys.Values)
        {
            var known = members.Where(c => c.Core.HasValue).Select(c => c.Core!.Value).Distinct().ToList();
            if (known.Count > 1) throw new InvalidDataException("CPU 集合与物理核关系冲突。");
            var id = known.Count == 1 ? known[0] : cores++;
            foreach (var cpu in members) cpu.Core ??= id;
        }
        foreach (var core in cpus.Where(c => c.Core.HasValue).GroupBy(c => c.Core))
        {
            var siblings = core.OrderBy(c => c.Group).ThenBy(c => c.Index).ToList();
            for (var t = 0; t < siblings.Count; t++)
            { siblings[t].Thread = t; siblings[t].ThreadCount = siblings.Count; }
        }
        InferKnownRyzenCcd(cpus, name);
        return new ProcmanTopologyResult { Groups = groups, Processors = cpus, ProcessorName = name };
    }

    private static IEnumerable<(int Offset, int Size)> Records(byte[] bytes, bool sizeFirst)
    {
        for (var offset = 0; offset < bytes.Length;)
        {
            if (bytes.Length - offset < 8) throw new InvalidDataException("拓扑头部截断。");
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + (sizeFirst ? 0 : 4)));
            if (length < 8 || length > bytes.Length - offset) throw new InvalidDataException("拓扑记录长度无效。");
            yield return (offset, (int)length);
            offset += (int)length;
        }
    }
    private static List<ProcmanLogicalCpu> Members(List<ProcmanLogicalCpu> cpus, ReadOnlySpan<byte> span, int offset, int count)
    {
        if (count <= 0 || count > 4096) throw new InvalidDataException("组数无效。");
        Need(span, offset + count * 16);
        var keys = new HashSet<(ushort, int)>();
        for (var g = 0; g < count; g++)
        {
            var mask = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset + g * 16, 8));
            var group = U16(span, offset + g * 16 + 8);
            for (var i = 0; i < 64; i++) if ((mask & (1UL << i)) != 0) keys.Add((group, i));
        }
        return cpus.Where(c => keys.Contains((c.Group, c.Index))).OrderBy(c => c.Group).ThenBy(c => c.Index).ToList();
    }
    private static ushort U16(ReadOnlySpan<byte> span, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2));
    private static uint U32(ReadOnlySpan<byte> span, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
    private static void Need(ReadOnlySpan<byte> span, int size)
    { if (span.Length < size) throw new InvalidDataException("拓扑记录截断。"); }

    private static void InferKnownRyzenCcd(List<ProcmanLogicalCpu> cpus, string name)
    {
        // 仅覆盖已知 Zen 3/4/5 Ryzen 桌面系列和核对过的 Dragon Range 型号。
        // Zen 2 的 CCX、Zen 4c、APU 和未知新品
        // 不能把共享 L3 直接称为 CCD，因此保留真实 L3/Die 标签。
        var knownDesktop = Regex.IsMatch(name, @"^AMD Ryzen [579] (?:5[6-9]\d{2}|7[6-9]\d{2}|9[6-9]\d{2})(?:X3D|XT|X)?(?:\s|$)");
        var knownDragonRange = Regex.IsMatch(name, @"^AMD Ryzen 9 (?:7940HX|7945HX)(?:\s|$)");
        if ((!knownDesktop && !knownDragonRange)
            || cpus.Count == 0 || cpus.Any(c => !c.Core.HasValue || !c.Package.HasValue || !c.L3.HasValue)) return;
        var domains = cpus.GroupBy(c => c.L3).OrderBy(g => g.Key).ToList();
        if (domains.Count is < 1 or > 2 || domains.Any(g => g.Select(c => c.Package).Distinct().Count() != 1
            || g.Select(c => c.Core).Distinct().Count() > 8
            || g.Any(c => c.L3Bytes is not (32 * 1024 * 1024) and not (96 * 1024 * 1024)))) return;
        for (var id = 0; id < domains.Count; id++) foreach (var cpu in domains[id]) cpu.Ccd = id;
    }
    private static string ReadBrand()
    {
        if (!X86Base.IsSupported || (uint)X86Base.CpuId(unchecked((int)0x80000000), 0).Eax < 0x80000004) return "";
        var bytes = new byte[48];
        for (var i = 0; i < 3; i++)
        {
            var r = X86Base.CpuId(unchecked((int)0x80000002) + i, 0);
            var values = new[] { r.Eax, r.Ebx, r.Ecx, r.Edx };
            for (var v = 0; v < 4; v++) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 16 + v * 4), values[v]);
        }
        return Encoding.ASCII.GetString(bytes).Trim('\0', ' ');
    }
    private static void DetectIntelCoreKinds(List<ProcmanLogicalCpu> cpus)
    {
        if (!X86Base.IsSupported) return;
        var vendor = X86Base.CpuId(0, 0);
        if (vendor.Ebx != 0x756e6547 || vendor.Edx != 0x49656e69 || vendor.Ecx != 0x6c65746e || vendor.Eax < 0x1a) return;
        if ((X86Base.CpuId(7, 0).Edx & (1 << 15)) == 0) return;
        // 专用 OS 线程，CPUID 与绑定之间没有 await/线程池切换；不动目标进程。
        var probe = new Thread(() =>
        {
            foreach (var cpu in cpus)
            {
                var affinity = new GroupAffinity { Group = cpu.Group, Mask = 1UL << cpu.Index };
                if (!SetThreadGroupAffinity(GetCurrentThread(), ref affinity, out var previous)) continue;
                try
                {
                    GetCurrentProcessorNumberEx(out var current);
                    if (current.Group != cpu.Group || current.Number != cpu.Index) continue;
                    cpu.CoreKind = ((uint)X86Base.CpuId(0x1a, 0).Eax >> 24) switch { 0x40 => "P", 0x20 => "E", _ => "" };
                }
                finally { SetThreadGroupAffinity(GetCurrentThread(), ref previous, out _); }
            }
        }) { IsBackground = true, Name = "Procman topology probe" };
        probe.Start(); probe.Join();
    }
}
