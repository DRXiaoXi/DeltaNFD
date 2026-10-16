using System.Buffers.Binary;
using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Execution.Procman;
using DeltaNFD.Services;

internal static class ProcmanCpuTopologyChecks
{
    internal static async Task RunIpcAsync()
    {
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-procman-topology-" + id.ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT", root);
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ID", id.ToString("D"));
        await using var backend = await DeltaNFD.Services.Backends.BackendClient.StartAsync(BackendModules.Procman, CancellationToken.None);
        var reply = await backend.InvokeAsync(BackendCommands.ProcmanCpuTopology, new { }, null, CancellationToken.None);
        if (reply.Status != BackendResultStatuses.Ok || reply.Data is not { } data) throw new Exception(reply.Message);
        var topology = BackendJson.Deserialize<ProcmanTopologyResult>(data);
        var native = ProcmanCpuTopologyReader.Read();
        if (topology.Processors.Count != native.Processors.Count || topology.ProcessorName != native.ProcessorName
            || !topology.Processors.Select(c => (c.Group, c.Index, c.Core, c.CoreKind, c.Ccd)).SequenceEqual(
                native.Processors.Select(c => (c.Group, c.Index, c.Core, c.CoreKind, c.Ccd))))
            throw new Exception("IPC topology differs from independent local reader.");
        await backend.DrainAsync(TimeSpan.FromSeconds(15));
        Console.WriteLine($"Procman topology IPC passed: {topology.Processors.Count} logical CPUs; isolated backend data only.");
    }
    internal static void Run()
    {
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
        bool Reject(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
        List<ProcmanGroupMask> groups = [new(0, 0xff), new(1, 0xf)];
        byte[] Join(params byte[][] values) => values.SelectMany(b => b).ToArray();
        var relations = Join(
            Processor(3, (0, 0xff), (1, 0xf)),
            Processor(0, (0, 0x11)), Processor(0, (0, 0x22)), Processor(0, (0, 0x44)), Processor(0, (0, 0x88)),
            Processor(0, (1, 0x5)), Processor(0, (1, 0xa)),
            Cache(32 * 1024 * 1024, (0, 0x55), (1, 0x5)), Cache(96 * 1024 * 1024, (0, 0xaa), (1, 0xa)));
        var dual = ProcmanCpuTopologyReader.Decode(groups, relations, [], "AMD Ryzen 9 7950X3D 16-Core Processor");
        Check(dual.Processors.Count == 12, "multi-group logical processors");
        Check(dual.Processors.Single(c => c.Group == 0 && c.Index == 4).Core == dual.Processors[0].Core, "non-adjacent SMT siblings");
        var presets = ProcmanCpuSelection.Presets(dual);
        var ccd0 = presets.Single(p => p.Label.StartsWith("CCD0"));
        Check(ccd0.Masks.Count == 2 && ccd0.Masks[0].Mask == 0x55 && ccd0.Masks[1].Mask == 5, "CCD masks must follow topology, not half of numbering");
        var primary = presets.Single(p => p.Label == "每核一个线程");
        Check(primary.Masks[0].Mask == 0xf && primary.Masks[1].Mask == 3, "one real thread per core across groups");
        var secondary = presets.Single(p => p.Label == "SMT 其余线程");
        Check(secondary.Masks[0].Mask == 0xf0 && secondary.Masks[1].Mask == 0xc, "SMT siblings must not use odd numbering");
        Check(dual.Processors.All(c => c.CoreKind == ""), "AMD performance class is not Intel P/E");
        foreach (var name in new[] { "AMD Ryzen 9 3950X", "AMD Ryzen 9 7940HS", "AMD Ryzen 7 8700G", "AMD EPYC 9754", "Intel Core i9", "Unknown CPU" })
        {
            var unknown = ProcmanCpuTopologyReader.Decode(groups, relations, [], name);
            Check(unknown.Processors.All(c => !c.Ccd.HasValue), "cannot invent CCD for " + name);
            Check(ProcmanCpuSelection.Presets(unknown).Any(p => p.Label == "L3 分组 0"), "unknown CCD retains actual shared L3");
        }
        foreach (var name in new[] { "AMD Ryzen 9 7940HX with Radeon Graphics", "AMD Ryzen 9 7945HX with Radeon Graphics" })
            Check(ProcmanCpuTopologyReader.Decode(groups, relations, [], name).Processors.All(c => c.Ccd.HasValue), "verified Dragon Range CCD: " + name);
        var zen2Cache = ProcmanCpuTopologyReader.Decode([new(0, 3)], Join(Processor(3, (0, 3)), Processor(0, (0, 3)), Cache(16 * 1024 * 1024, (0, 3))), [], "AMD Ryzen 5 5600G");
        Check(zen2Cache.Processors.All(c => !c.Ccd.HasValue), "APU not CCD");
        var sparse = ProcmanCpuTopologyReader.Decode([new(0, 3)], Group(0x8000000000000005), [], "");
        Check(sparse.Groups[0].Mask == 0x8000000000000005 && sparse.Processors.Select(c => c.Index).SequenceEqual(new[] { 0, 2, 63 }), "native sparse group mask, including bit63");
        var sets = Join(CpuSet(25, 1, 2, 7, 0), CpuSet(22, 0, 4, 7, 1), CpuSet(21, 0, 0, 7, 1), CpuSet(26, 1, 0, 7, 0));
        var fallback = ProcmanCpuTopologyReader.Decode([new(0, 0x11), new(1, 5)], [], sets, "Intel");
        Check(fallback.Processors[0].Core == fallback.Processors[1].Core, "CPU Set core fallback supports unordered records");
        Check(fallback.Processors[0].Core != fallback.Processors[2].Core, "CPU Set CoreIndex is group-relative");
        Check(fallback.Processors.All(c => c.NumaNode is null && c.L3 is null), "group-relative CPU Set indices aren't global NUMA/L3 IDs");
        Check(fallback.Processors.All(c => c.CoreKind == ""), "EfficiencyClass alone cannot confirm P/E");
        fallback.Processors[0].CoreKind = fallback.Processors[1].CoreKind = "P";
        fallback.Processors[2].CoreKind = fallback.Processors[3].CoreKind = "E";
        Check(ProcmanCpuSelection.Presets(fallback).Single(p => p.Label == "性能核 (P)").Masks.Single().Mask == 0x11, "P preset preserves group mask");
        Check(ProcmanCpuSelection.Presets(fallback).Single(p => p.Label == "能效核 (E)").Masks.Single().Group == 1, "E preset across group boundaries");
        var noData = ProcmanCpuTopologyReader.Decode(groups, [], [], "");
        Check(noData.Processors.Count == 12 && ProcmanCpuSelection.Presets(noData).Count == 0, "unknown topology retains logical CPU without fake presets");
        var partial = ProcmanCpuTopologyReader.Decode([new(0, 7)], Processor(0, (0, 3)), [], "");
        Check(!ProcmanCpuSelection.Presets(partial).Any(p => p.Label.StartsWith("每核")), "partial topology cannot silently omit CPUs from every-core selection");
        var future = new byte[12]; U32(future, 0, 12); U32(future, 4, 999);
        Check(ProcmanCpuTopologyReader.Decode([new(0, 1)], [], Join(future, CpuSet(50, 0, 0, 0, 0)), "").Processors[0].CpuSetId == 50, "skip future CPU Set record by size");
        foreach (var bad in new[] { new byte[3], new byte[8], CpuSet(1, 0, 0, 0, 0)[..20] })
            Check(Reject(() => ProcmanCpuTopologyReader.Decode(groups, [], bad, "")), "reject truncated or zero-sized records");
        Check(Reject(() => ProcmanCpuTopologyReader.Decode(groups, Join(Processor(0, (0, 3)), Processor(0, (0, 1))), [], "")), "reject overlapping core records");
        Check(Reject(() => ProcmanCpuTopologyReader.Decode(groups, [], Join(CpuSet(1, 0, 0, 0, 0), CpuSet(1, 0, 0, 0, 0)), "")), "reject duplicate CPU Sets");
        var roundTrip = JsonSerializer.Deserialize<ProcmanTopologyResult>(JsonSerializer.Serialize(dual))!;
        Check(roundTrip.Processors.Count == dual.Processors.Count && roundTrip.Processors[0].Ccd == 0, "IPC JSON topology round trip");
        var native = ProcmanCpuTopologyReader.Read();
        Check(native.Groups.Count > 0 && native.Processors.Count > 0, "real local read-only probe");
        Check(native.Processors.Select(c => (c.Group, c.Index)).Distinct().Count() == native.Processors.Count, "real topology has unique coordinates");
        Check(native.Processors.All(c => native.Groups.Any(g => g.Group == c.Group && (g.Mask & (1UL << c.Index)) != 0)), "real CPUs belong to returned groups");
        Check(native.Groups.All(g => ProcmanTopology.SystemGroupMasks().Any(m => m.Group == g.Group && m.Mask == g.Mask)), "selection and backend validation share actual native group masks");
        Console.WriteLine($"Procman independent topology: {count} checks passed.");
        Console.WriteLine($"Native: {native.ProcessorName}; {native.Processors.Count} CPUs; {native.Processors.Select(c => c.Core).Distinct().Count()} cores; P={native.Processors.Count(c => c.CoreKind == "P")}, E={native.Processors.Count(c => c.CoreKind == "E")}; CCD={native.Processors.Select(c => c.Ccd).Where(c => c.HasValue).Distinct().Count()}");
        Console.WriteLine(native.DetectionNote);
        foreach (var preset in ProcmanCpuSelection.Presets(native))
            Console.WriteLine(preset.Label + ": " + string.Join(";", preset.Masks.Select(g => $"g{g.Group}:0x{g.Mask:X}")));
    }
    private static void U32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o, 4), v);
    private static void U16(byte[] b, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o, 2), v);
    private static void Masks(byte[] b, int offset, (ushort Group, ulong Mask)[] masks)
    {
        for (var i = 0; i < masks.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(offset + i * 16, 8), masks[i].Mask);
            U16(b, offset + i * 16 + 8, masks[i].Group);
        }
    }
    private static byte[] Processor(uint type, params (ushort Group, ulong Mask)[] masks)
    {
        var b = new byte[32 + 16 * masks.Length]; U32(b, 0, type); U32(b, 4, (uint)b.Length);
        U16(b, 30, (ushort)masks.Length); Masks(b, 32, masks); return b;
    }
    private static byte[] Cache(uint size, params (ushort Group, ulong Mask)[] masks)
    {
        var b = new byte[40 + 16 * masks.Length]; U32(b, 0, 2); U32(b, 4, (uint)b.Length); b[8] = 3;
        U32(b, 12, size); U16(b, 38, (ushort)masks.Length); Masks(b, 40, masks); return b;
    }
    private static byte[] CpuSet(uint id, ushort group, byte index, byte core, byte efficiency)
    {
        var b = new byte[32]; U32(b, 0, 32); U32(b, 8, id); U16(b, 12, group);
        b[14] = index; b[15] = core; b[18] = efficiency; return b;
    }
    private static byte[] Group(ulong mask)
    {
        var b = new byte[80]; U32(b, 0, 4); U32(b, 4, 80); U16(b, 10, 1);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(72, 8), mask); return b;
    }
}
