using System.Buffers.Binary;
using DeltaNFD.Services;

internal static class CcdModelChecks
{
    private static int _checks;
    private const int Mb = 1024 * 1024;

    internal static void Run()
    {
        _checks = 0;
        var expected = new (string Name, int Cores)[]
        {
            ("Ryzen 9 3900",12), ("Ryzen 9 3900X",12), ("Ryzen 9 3900XT",12), ("Ryzen 9 3950X",16),
            ("Ryzen 9 5900",12), ("Ryzen 9 5900X",12), ("Ryzen 9 5900XT",16), ("Ryzen 9 5950X",16),
            ("Ryzen 9 7900",12), ("Ryzen 9 7900X",12), ("Ryzen 9 7900X3D",12),
            ("Ryzen 9 7950X",16), ("Ryzen 9 7950X3D",16),
            ("Ryzen 9 9900X",12), ("Ryzen 9 9900X3D",12), ("Ryzen 9 9950X",16),
            ("Ryzen 9 9950X3D",16), ("Ryzen 9 9950X3D2",16),
            ("Ryzen 7 7840HX",12), ("Ryzen 9 7845HX",12), ("Ryzen 9 7940HX",16),
            ("Ryzen 9 7945HX",16), ("Ryzen 9 7945HX3D",16), ("Ryzen 9 8940HX",16), ("Ryzen 9 8945HX",16),
            ("Ryzen 9 9850HX",12), ("Ryzen 9 9955HX",16), ("Ryzen 9 9955HX3D",16),
            ("Ryzen AI Max 390",12), ("Ryzen AI Max+ 395",16),
        };
        foreach (var (name, count) in expected)
        {
            var model = AmdCcdModelCatalog.Find($"AMD {name} {count}-Core Processor");
            Check(model is { CcdCount: 2 } && model.PhysicalCores == count, name + " catalog");
            var cores = Cores(count);
            var domains = new[] { Domain(cores.Take(count / 2)), Domain(cores.Skip(count / 2)) };
            Expect(name + " actual Die", cores, domains, [], model, 2, CcdDetectionSource.ProcessorDie);
            var l3 = domains.Select(d => d with { CacheSizeBytes = 32 * Mb }).ToArray();
            Expect(name + " L3", cores, [], l3, model, model!.L3IsCcd ? 2 : 0,
                model.L3IsCcd ? CcdDetectionSource.L3Fallback : CcdDetectionSource.Unknown);
        }
        foreach (var name in new string?[]
        {
            null, "", "AMD Ryzen 9 7900XT", "AMD Ryzen 9 7900X3D2", "AMD Ryzen 9 7900XFAKE",
            "AMD Ryzen 9 79000X", "AMD Ryzen 7 7900X", "Intel Ryzen 9 7900X", "NVIDIA RTX 7900X",
            "AMD Ryzen 9 7940HS", "AMD Ryzen 9 8945HS", "AMD Ryzen 7 7840HS", "AMD Ryzen 7 7840U",
            "AMD Ryzen 7 7745HX", "AMD Ryzen 7 9800X3D", "AMD Ryzen 7 8700G", "AMD Ryzen 9 6900HX",
            "AMD Ryzen AI 9 HX 370", "AMD Ryzen AI 9 HX 470", "AMD Ryzen AI Max 385",
            "AMD Ryzen Threadripper 7960X", "AMD EPYC 9754", new string('a', 257),
        }) Check(AmdCcdModelCatalog.Find(name) is null, "unknown SKU: " + name);
        Check(AmdCcdModelCatalog.Find("  amd   ryzen(TM) 9 7900x 12-core processor ")?.Name == "Ryzen 9 7900X", "normalized brand");
        Check(AmdCcdModelCatalog.Find("AMD Ryzen™ AI Max+ 395 w/ Radeon Graphics")?.PhysicalCores == 16, "Max+ brand");

        var ryzen = AmdCcdModelCatalog.Find("AMD Ryzen 9 7900X")!;
        var paired = Cores(12);
        var legacy = new[] { Record(0xFFF, 0), Record(0xFFF000, 0) };
        var l3Legacy = legacy.Select(r => Read(r)).ToArray();
        Expect("Win10 legacy 7900X 12C/24T", paired, [], l3Legacy, ryzen, 2, CcdDetectionSource.L3Fallback);
        Expect("package-wide Die must not hide two L3s", paired, [new(0, 0xFFFFFF)], l3Legacy, ryzen, 2, CcdDetectionSource.L3Fallback);
        Expect("SMT off 7900X", Cores(12, false), [], [new(0, 0x3F, 32 * Mb), new(0, 0xFC0, 32 * Mb)], ryzen, 2, CcdDetectionSource.L3Fallback);
        var sparse = Cores(12, interleaved: true);
        var even = Domain(sparse.Where((_, i) => i % 2 == 0)) with { CacheSizeBytes = 32 * Mb };
        var odd = Domain(sparse.Where((_, i) => i % 2 != 0)) with { CacheSizeBytes = 32 * Mb };
        var mapped = Expect("interleaved non-contiguous CCDs", sparse, [], [even, odd], ryzen, 2, CcdDetectionSource.L3Fallback);
        Check(mapped[0].Mask == even.Mask && mapped[1].Mask == odd.Mask, "actual masks retained, never split CPU numbering");
        Expect("missing all topology", paired, [], [], ryzen, 0, CcdDetectionSource.Unknown);
        Expect("unknown 16-core cannot be split", Cores(16), [], [], null, 0, CcdDetectionSource.Unknown);
        Expect("known 16-core cannot be split", Cores(16), [], [], AmdCcdModelCatalog.Find("AMD Ryzen 9 8940HX"), 0, CcdDetectionSource.Unknown);
        Expect("model/physical count conflict", Cores(16), [], [new(0, 0xFFFF, 32 * Mb), new(0, 0xFFFF0000, 32 * Mb)], ryzen, 0, CcdDetectionSource.Unknown);
        Expect("overlapping L3", paired, [], [new(0, 0xFFFF, 32 * Mb), new(0, 0xFFFF00, 32 * Mb)], ryzen, 0, CcdDetectionSource.Unknown);
        Expect("incomplete L3", paired, [], [l3Legacy[0]], ryzen, 0, CcdDetectionSource.Unknown);
        Expect("extra CPU in L3", paired, [], [l3Legacy[0], new(0, 0x1FFF000, 32 * Mb)], ryzen, 0, CcdDetectionSource.Unknown);
        Expect("SMT split across CCDs", paired, [], [new(0, 0x555555, 32 * Mb), new(0, 0xAAAAAA, 32 * Mb)], ryzen, 0, CcdDetectionSource.Unknown);
        Expect("duplicate core record", paired.Concat([paired[0]]).ToList(), [], l3Legacy, null, 0, CcdDetectionSource.Unknown);
        Expect("consistent duplicate cache", paired, [], [..l3Legacy, l3Legacy[0]], ryzen, 2, CcdDetectionSource.L3Fallback);
        Expect("conflicting duplicate cache", paired, [], [..l3Legacy, l3Legacy[0] with { CacheSizeBytes = 96 * Mb }], ryzen, 0, CcdDetectionSource.Unknown);
        Expect("zero L3", paired, [], [..l3Legacy, new(0, 0, 32 * Mb)], ryzen, 0, CcdDetectionSource.Unknown);
        Expect("Zen2 four CCX are not four CCDs", paired, [],
            [new(0, 0x3F, 16 * Mb), new(0, 0xFC0, 16 * Mb), new(0, 0x3F000, 16 * Mb), new(0, 0xFC0000, 16 * Mb)],
            AmdCcdModelCatalog.Find("AMD Ryzen 9 3900X"), 0, CcdDetectionSource.Unknown);
        Expect("unknown Zen2 L3 cannot imply dual CCD", Cores(8), [], [new(0, 0xFF, 16 * Mb), new(0, 0xFF00, 16 * Mb)], null, 0, CcdDetectionSource.Unknown);
        var one = Expect("BIOS only one CCD active", Cores(6), [], [new(0, 0xFFF, 32 * Mb)], ryzen, 1, CcdDetectionSource.L3Fallback);
        var text = CpuTopologyService.BuildCcdAdvice(true, one, CcdDetectionSource.L3Fallback, ryzen);
        Check(text.Contains("双 CCD 设计") && text.Contains("仅确认 1 个可用 CCD") && text.Contains("调度暂不可用"), "design and enabled topology kept separate");
        text = CpuTopologyService.BuildCcdAdvice(true, [], CcdDetectionSource.Unknown, ryzen);
        Check(text.Contains("双 CCD 设计") && text.Contains("尚未确认"), "known model without masks explained");
        Check(CpuTopologyService.BuildCcdAdvice(false, [], CcdDetectionSource.Unknown, null).Contains("不适用"), "non-AMD advice");
        var stack = Expect("asymmetric X3D cache", paired, [], [new(0, 0xFFF, 96 * Mb), l3Legacy[1]],
            AmdCcdModelCatalog.Find("AMD Ryzen 9 7900X3D"), 2, CcdDetectionSource.L3Fallback);
        Check(stack[0].L3CacheMb == 96 && stack[1].L3CacheMb == 32, "X3D actual cache preserved");
        Check(CpuTopologyService.BuildCcdAdvice(true, stack, CcdDetectionSource.L3Fallback, ryzen, 2).Contains("此布局暂不可用"), "multi-group scheduling limitation explained");
        var dualGroups = paired.Select((c, i) => new CpuCoreInfo { Group = i < 6 ? 0 : 1,
            Mask = c.Mask, EfficiencyClass = 0, Smt = c.Smt, LogicalProcessors = c.LogicalProcessors }).ToList();
        Expect("two actual processor groups", dualGroups, [new(0, 0xFFF), new(1, 0xFFF000)], [], ryzen, 2, CcdDetectionSource.ProcessorDie);
        var x3d2 = Expect("both CCDs with V-Cache", Cores(16), [], [new(0, 0xFFFF, 96 * Mb), new(0, 0xFFFF0000, 96 * Mb)],
            AmdCcdModelCatalog.Find("AMD Ryzen 9 9950X3D2"), 2, CcdDetectionSource.L3Fallback);
        Check(x3d2.All(c => c.L3CacheMb == 96), "double V-Cache sizes retained");

        // 新旧 Windows 缓存记录使用同一套真实读取函数，无原生写入。
        Check(Read(Record(0xFFFF, 1)).Mask == 0xFFFF, "modern cache GroupCount=1");
        foreach (var mutate in new Action<byte[]>[]
        {
            r => BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(4), 55),
            r => BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(0), 0),
            r => r[8] = 2,
            r => BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(16), 1),
            r => BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(38), 2),
            r => BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(40), 0),
            r => BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(12), 0),
            r => BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(12), -1),
        })
        {
            var bad = Record(0xFFFF, 1); mutate(bad);
            Check(!CpuCacheRelationshipReader.TryReadL3(bad, out _), "invalid cache record rejected");
        }
        for (var length = 0; length < 56; length++)
            Check(!CpuCacheRelationshipReader.TryReadL3(Record(0xFFFF, 0).AsSpan(0, length), out _), "truncated cache " + length);
        var larger = new byte[72]; Record(0xFFFF, 1).CopyTo(larger, 0); BinaryPrimitives.WriteInt32LittleEndian(larger.AsSpan(4), 72);
        Check(!CpuCacheRelationshipReader.TryReadL3(larger, out _), "unmodeled multi-group cache record rejected");
        Console.WriteLine($"CCD model and topology checks passed: {_checks}. No process affinity or system configuration was changed.");
    }

    private static List<CcdInfo> Expect(string name, List<CpuCoreInfo> cores, CpuDomain[] dies,
        CpuDomain[] l3, AmdCcdModel? model, int count, CcdDetectionSource source)
    {
        var result = CcdTopologyDetector.Detect(cores, dies, l3, model);
        Check(result.Ccds.Count == count && result.Source == source, name);
        return result.Ccds;
    }

    private static CpuDomain Domain(IEnumerable<CpuCoreInfo> cores) => new(0, cores.Aggregate(0UL, (m, c) => m | c.Mask));
    private static List<CpuCoreInfo> Cores(int count, bool smt = true, bool interleaved = false)
        => Enumerable.Range(0, count).Select(i =>
        {
            var mask = !smt ? 1UL << i : interleaved ? (1UL << i) | (1UL << (32 + i)) : 3UL << (2 * i);
            return new CpuCoreInfo { Group = 0, Mask = mask, EfficiencyClass = 0, Smt = smt,
                LogicalProcessors = Enumerable.Range(0, 64).Where(b => (mask & (1UL << b)) != 0).ToList() };
        }).ToList();
    private static CpuDomain Read(byte[] record)
    {
        Check(CpuCacheRelationshipReader.TryReadL3(record, out var domain), "cache read");
        return domain;
    }
    private static byte[] Record(ulong mask, ushort groupCount)
    {
        var bytes = new byte[56];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 2);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length);
        bytes[8] = 3;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), 32 * Mb);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(38), groupCount);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(40), mask);
        return bytes;
    }
    private static void Check(bool ok, string description)
    {
        if (!ok) throw new InvalidOperationException("CCD regression: " + description);
        _checks++;
    }
}
