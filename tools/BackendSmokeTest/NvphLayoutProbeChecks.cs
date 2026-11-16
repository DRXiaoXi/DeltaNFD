using System.Buffers.Binary;
using DeltaNFD.Inspection;
using DeltaNFD.Services;
using DeltaNFD.ViewModels;

/// <summary>仅写自有临时 NVPH，直接检查真实解析器与诊断汇总。</summary>
internal static class NvphFixtureFiles
{
    internal static void WriteNvuc(string path, int records = 85, uint lastTag = 0x6375564e, bool concatenate = false, int decodedBytes = 528, bool compressible = false)
    {
        using var compressor = new ZstdSharp.Compressor(1);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        file.SetLength(1024 * 1024);
        const int header = 1488;
        file.Position = header;
        for (var index = 0; index < records; index++)
        {
            var decoded = new byte[decodedBytes]; if (!compressible) new Random(index + 21).NextBytes(decoded);
            BinaryPrimitives.WriteUInt32LittleEndian(decoded, index == records - 1 ? lastTag : 0x6375564e);
            var compressed = compressor.Wrap(decoded).ToArray();
            if (concatenate && index == records - 1)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(decoded, 0xdec04342);
                compressed = compressed.Concat(compressor.Wrap(decoded).ToArray()).ToArray();
            }
            var envelope = new byte[32 + 112 + 1];
            "phwr"u8.CopyTo(envelope);
            BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(8), 112 + 1 + compressed.Length);
            void Field(int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(envelope.AsSpan(32 + offset), value);
            Field(0, 0x000d1234); Field(8, 64); Field(56, 2); Field(64, 0x5678); Field(72, 48);
            Field(80, 1); Field(88, (ulong)compressed.Length); Field(96, (ulong)(concatenate && index == records - 1 ? 2 * decodedBytes : decodedBytes)); Field(104, 2);
            file.Write(envelope); file.Write(compressed);
        }
        var used = file.Position;
        file.Position = 0;
        var head = new byte[header]; "nvph"u8.CopyTo(head);
        BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(8), (ulong)used); file.Write(head);
    }
    internal static void Write(string path, long size, int records = 400, int payload = 15800, int prefix = 32)
    {
        const int header = 1488;
        var used = header + (long)records * (prefix + payload);
        if (payload < 4 || used > size || prefix is not (16 or 24 or 32)) throw new ArgumentException("Invalid owned NVPH fixture.");
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.SetLength(size);
        var head = new byte[header];
        "nvph"u8.CopyTo(head);
        BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(8), (ulong)used);
        fs.Write(head);
        var record = new byte[prefix + payload];
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(8), payload);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(prefix), 0x000d1234);
        for (var i = 0; i < records; i++) fs.Write(record);
    }

    internal static void Put32(string path, long offset, uint value)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write);
        fs.Position = offset;
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); fs.Write(bytes);
    }

    internal static void Raw(string path, long size)
    { using var fs = File.Create(path); fs.SetLength(size); }
}

internal static class NvphLayoutProbeChecks
{
    internal static void Run()
    {
        const long mb = 1024L * 1024;
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-nvph-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var appdata = Path.Combine(root, "owned-appdata"); Directory.CreateDirectory(appdata);
        var id = Guid.NewGuid(); File.WriteAllText(Path.Combine(appdata, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(appdata, id);
        var cache = Path.Combine(root, "PSO", "GameVer_Test", "SM6", "DXCache"); Directory.CreateDirectory(cache);
        var a = Path.Combine(cache, "a.nvph"); var b = Path.Combine(cache, "b.nvph"); var extra = Path.Combine(cache, "extra.nvph");
        var count = 0;
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); count++; }
        ShaderDiagnosis Diagnose(string driver = "621.14") => new ShaderInspection(null,
            () => new(ShaderGpuDetectionState.Nvidia, driver)).DiagnoseAt(Path.Combine(root, "PSO"));
        void Unknown(ShaderDiagnosis diag, string why)
        {
            Check(diag.Level == ShaderDiagLevel.Normal && diag.CacheLayout == ShaderCacheLayout.Unspecified, why + ": capacity-valid files stay green without ASD inference");
            Check(!diag.Summary.Contains("ASD已激活") && !diag.Summary.Contains("B 层已退出"), why + ": cannot claim activation/exit");
        }
        var status = new ShaderStatus { IsNvidia = true, DriverVersion = "621.14", DriverState = ShaderDriverState.Ok,
            DriverAdvice = "fixture", IsGameRunning = false };
        var good = new NvShaderCacheReport { Scopes = [new("global", "default", null, "fixture"), new("delta", "default", null, "fixture")] };

        foreach (var prefix in new[] { 16, 24, 32 })
        foreach (var (records, payload, expected) in new[]
        {
            (400, 15800, NvphLayoutProbe.NvphLayer.Pso), (800, 5400, NvphLayoutProbe.NvphLayer.Bytecode),
            (250, 1024, NvphLayoutProbe.NvphLayer.Unknown),
        })
        {
            NvphFixtureFiles.Write(a, 256 * mb, records, payload, prefix);
            var initial = NvphLayoutProbe.Read(a);
            Check(initial.Ok && initial.Records == records && initial.Layer == expected, "valid prefix and classification retained");
            Check(initial.AverageFootprint == prefix + payload, "footprint calculation retained");
            foreach (var offset in new[] { 16L, 20L, 64L, 128L }) NvphFixtureFiles.Put32(a, offset, 0x00061234);
            Check(NvphLayoutProbe.Read(a) == initial, "earlier invalid marker candidates do not mask true chain");
        }
        foreach (var records in new[] { 199, 200, 200000, 200001 })
        {
            NvphFixtureFiles.Write(a, 256 * mb, records, 4);
            var probe = NvphLayoutProbe.Read(a);
            Check(probe.Ok == (records <= 200000), "structural validity is independent of 200-record classification threshold; upper bound retained");
            if (records == 199) Check(probe.Records == 199 && probe.Layer == NvphLayoutProbe.NvphLayer.Unknown, "short valid chain retains count without inventing a layer");
        }
        foreach (var payload in new[] { 12255, 12256 })
        {
            NvphFixtureFiles.Write(a, 256 * mb, 400, payload);
            Check(NvphLayoutProbe.Read(a).Layer == (payload == 12256 ? NvphLayoutProbe.NvphLayer.Pso : NvphLayoutProbe.NvphLayer.Bytecode), "12KiB heuristic deliberately unchanged");
        }
        NvphFixtureFiles.Raw(a, 4095); Check(!NvphLayoutProbe.Read(a).Ok, "short file rejected");
        NvphFixtureFiles.Raw(a, 256 * mb); Check(!NvphLayoutProbe.Read(a).Ok, "zero-filled preallocation is not valid structure");
        NvphFixtureFiles.Write(a, 256 * mb); NvphFixtureFiles.Put32(a, 0, 0);
        Check(!NvphLayoutProbe.Read(a).Ok, "wrong container magic rejected");
        NvphFixtureFiles.Write(a, 256 * mb); NvphFixtureFiles.Put32(a, 8, (uint)(256 * mb + 1));
        Check(!NvphLayoutProbe.Read(a).Ok, "used region beyond file rejected");
        NvphFixtureFiles.Write(a, 256 * mb); NvphFixtureFiles.Put32(a, 1488 + 8, 0);
        Check(!NvphLayoutProbe.Read(a).Ok, "zero first payload cannot be skipped in favor of a later chain");

        // 同一版本标记同时满足16/32前缀，不能任选一个边界。
        NvphFixtureFiles.Write(a, 256 * mb, 400, 16000);
        for (var i = 0; i < 400; i++) NvphFixtureFiles.Put32(a, 1488L + i * 16032L + 24, (uint)(i == 399 ? 16000 : 16016));
        Check(!NvphLayoutProbe.Read(a).Ok, "ambiguous prefix interpretations stay unreadable");

        foreach (var driver in new[] { "621.00", "621.14", "621.99", "622.00" })
        {
            NvphFixtureFiles.Write(a, 256 * mb); NvphFixtureFiles.Write(b, 256 * mb);
            var active = Diagnose(driver);
            Check(active.Level == ShaderDiagLevel.Normal && active.CacheLayout == ShaderCacheLayout.Unspecified, "all-A contents do not imply automatic ASD activation");
            NvphFixtureFiles.Write(b, 256 * mb, 800, 5400);
            var legacy = Diagnose(driver);
            Check(legacy.Level == ShaderDiagLevel.Normal && legacy.CacheLayout == ShaderCacheLayout.Unspecified, "B-layer contents do not change capacity compliance");
            using (var locked = new FileStream(b, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Unknown(Diagnose(driver), "locked B");
                Check(!NvphLayoutProbe.Read(b).Ok, "exclusive lock genuinely prevents probing");
            }
            Check(Diagnose(driver).Level == ShaderDiagLevel.Normal, "released B remains capacity-valid");
            foreach (var offset in new[] { 16L, 20L, 64L }) NvphFixtureFiles.Put32(b, offset, 0x00061234);
            Check(Diagnose(driver).Level == ShaderDiagLevel.Normal, "decoy header markers are not inspected by automatic health checks");
            NvphFixtureFiles.Write(b, 256 * mb, 250, 1024);
            Unknown(Diagnose(driver), "A plus Unknown");
            NvphFixtureFiles.Raw(b, mb); Unknown(Diagnose(driver), "A plus unparsed small cache");
            NvphFixtureFiles.Write(a, 256 * mb, 250, 1024); Unknown(Diagnose(driver), "no A, only Unknown plus unparsed small");
            NvphFixtureFiles.Write(a, 256 * mb); NvphFixtureFiles.Write(b, 256 * mb); NvphFixtureFiles.Raw(extra, 1);
            Unknown(Diagnose(driver), "nonzero intermediate permitted but cannot confirm ASD");
            NvphFixtureFiles.Write(b, 256 * mb, 800, 5400);
            Check(Diagnose(driver).Level == ShaderDiagLevel.Normal && Diagnose(driver).CacheLayout == ShaderCacheLayout.Unspecified, "known B does not change the capacity rule");
            File.Delete(extra);
            using (var locked = new FileStream(a, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(Diagnose(driver).Level == ShaderDiagLevel.Normal, "header read locks are irrelevant to readable size metadata");
        }
        NvphFixtureFiles.Write(a, 256 * mb); NvphFixtureFiles.Write(b, 256 * mb, 800, 5400);
        Check(DashboardShaderHealth.Evaluate(status, Diagnose(), good, true).State == DashboardHealthState.Normal, "capacity-valid B pair stays green");
        NvphFixtureFiles.Raw(b, mb);
        var unknownHealth = DashboardShaderHealth.Evaluate(status, Diagnose(), good, true);
        Check(unknownHealth.State == DashboardHealthState.Normal && !unknownHealth.Text.Contains("ASD"), "unknown headers do not add automatic ASD warnings");
        foreach (var records in new[] { 7, 85, 199, 200, 400 })
        {
            NvphFixtureFiles.Write(a, 256 * mb); NvphFixtureFiles.WriteNvuc(b, records);
            var probe = NvphLayoutProbe.Read(b); var diagnosis = Diagnose();
            Check(probe.Ok && probe.Records == records && probe.Layer == NvphLayoutProbe.NvphLayer.NvucAuxiliary, "bounded pure NVuc content has positive auxiliary classification, including 85 records");
            Check(diagnosis.Level == ShaderDiagLevel.Normal && diagnosis.CacheLayout == ShaderCacheLayout.Unspecified
                && !diagnosis.Summary.Contains("ASD已激活") && !diagnosis.IsLayoutUncertain, "A plus decoded pure NVuc small cache confirms ASD layout");
            Check(!diagnosis.Details.Any(d => d.Contains("全部记录已解压确认")), "ordinary diagnosis does not parse auxiliary contents");
        }
        foreach (var tag in new uint[] { 0xdec04342, 0x00525453, 0x43425844, 0x464c457f })
        {
            NvphFixtureFiles.WriteNvuc(b, lastTag: tag);
            Check(NvphLayoutProbe.Read(b).Layer == NvphLayoutProbe.NvphLayer.Bytecode, "small mixed bytecode is positive B evidence, not a benign auxiliary");
            Check(Diagnose().CacheLayout == ShaderCacheLayout.Unspecified, "mixed small cache does not produce an ASD claim");
        }
        NvphFixtureFiles.WriteNvuc(b, lastTag: 0x12345678); Unknown(Diagnose(), "unknown decoded tag");
        NvphFixtureFiles.WriteNvuc(b, concatenate: true); Unknown(Diagnose(), "concatenated NVuc plus hidden BC frame");
        NvphFixtureFiles.WriteNvuc(b); NvphFixtureFiles.Put32(b, 1488 + 32 + 96, 128 * 1024 + 1);
        Unknown(Diagnose(), "oversized declared output rejected before allocation");
        NvphFixtureFiles.WriteNvuc(b); NvphFixtureFiles.Put32(b, 1488 + 32 + 88, 1);
        Unknown(Diagnose(), "compressed size does not match record envelope");
        NvphFixtureFiles.WriteNvuc(b); NvphFixtureFiles.Put32(b, 1488 + 32 + 112 + 1, 0);
        Unknown(Diagnose(), "corrupt frame cannot masquerade as auxiliary");
        NvphFixtureFiles.WriteNvuc(b); NvphFixtureFiles.Put32(b, 1488 + 32, 0x000e1234);
        Unknown(Diagnose(), "unsupported first record cannot be skipped to classify a later suffix as auxiliary");
        NvphFixtureFiles.WriteNvuc(b);
        using (var file = new FileStream(b, FileMode.Open, FileAccess.Write)) file.SetLength(4 * mb);
        Check(NvphLayoutProbe.Read(b).Layer == NvphLayoutProbe.NvphLayer.Bytecode && Diagnose().CacheLayout == ShaderCacheLayout.Unspecified,
            "expanded NVuc preallocation is B evidence rather than a small auxiliary or A-like capacity hint");
        NvphFixtureFiles.WriteNvuc(b, records: 4097);
        Unknown(Diagnose(), "auxiliary record budget cannot be exceeded");
        NvphFixtureFiles.WriteNvuc(b, records: 2100, decodedBytes: 4096, compressible: true);
        Unknown(Diagnose(), "aggregate decoded budget prevents highly compressed amplification");
        NvphFixtureFiles.WriteNvuc(a);
        Check(Diagnose().Level == ShaderDiagLevel.Abnormal, "auxiliary data never replaces the required large primary cache");
        NvphFixtureFiles.Write(a, 256 * mb); NvphFixtureFiles.WriteNvuc(b);
        var auxiliaryActive = Diagnose();
        var uncertainDiagnosis = new ShaderDiagnosis { Level = ShaderDiagLevel.Warning, Summary = "owned unknown", Details = [], Advice = "owned", IsLayoutUncertain = true };
        var unknownWire = System.Text.Json.JsonSerializer.Deserialize<ShaderDiagnosis>(System.Text.Json.JsonSerializer.Serialize(uncertainDiagnosis))!;
        Check(unknownWire.IsLayoutUncertain && !DashboardShaderHealth.Evaluate(status, unknownWire, good, true).ShowAction, "pure layout uncertainty survives IPC without a cleanup action");
        Check(!System.Text.Json.JsonSerializer.Deserialize<ShaderDiagnosis>("{\"Level\":5,\"Summary\":\"old\",\"Details\":[],\"Advice\":\"old\"}")!.IsLayoutUncertain, "old payload cannot invent benign-layout status");
        var custom = new NvShaderCacheReport { Scopes = [new("global", "custom", 0, "fixture"), new("delta", "default", null, "fixture")] };
        Check(DashboardShaderHealth.Evaluate(status, auxiliaryActive, custom, true) is { State: DashboardHealthState.Abnormal, ShowAction: true }, "auxiliary-positive ASD cannot hide unrelated cache setting errors");
        NvphFixtureFiles.Raw(extra, 0);
        Check(Diagnose().Level == ShaderDiagLevel.Normal, "extra zero intermediate does not invalidate an existing valid pair");
        Directory.CreateDirectory(Path.Combine(root, "PSO", "GameVer_Old"));
        Check(Diagnose().Level == ShaderDiagLevel.Abnormal, "multi-version abnormality retains priority");
        Console.WriteLine($"NVPH probe robustness: {count} checks passed; owned files and real read-only parser/diagnosis only, no GPU/real cache/system writes. Evidence: {root}");
    }
}
