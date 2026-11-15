using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeltaNFD.Inspection;
using DeltaNFD.Services;
using DeltaNFD.ViewModels;

internal static class D3dsAssociationChecks
{
    internal static async Task RunLiveAsync(string layout)
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-d3ds-readonly-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var id = Guid.NewGuid(); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D")); AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ROOT", Path.Combine(Path.GetFullPath(layout), "Backends"));
        Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ROOT", root); Environment.SetEnvironmentVariable("DELTANFD_BACKEND_ISOLATED_ID", id.ToString("D"));
        var client = await DeltaNFD.Services.Backends.BackendClient.StartAsync(DeltaNFD.Backends.BackendModules.System, default);
        try
        {
            var response = await client.InvokeAsync(DeltaNFD.Backends.BackendCommands.SystemShaderDiagnose, new { }, null, default);
            var diagnosis = DeltaNFD.Services.Backends.BackendResults.RequireData<ShaderDiagnosis>(response, "Actual read-only system cache association");
            if (diagnosis.SystemCache is null) throw new Exception("Actual official backend omitted system-cache correlation.");
            Console.WriteLine(JsonSerializer.Serialize(new { diagnosis.Level, diagnosis.CacheLayout, diagnosis.Summary, diagnosis.SystemCache }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            if (diagnosis.Level != ShaderDiagLevel.Normal || diagnosis.CacheLayout != ShaderCacheLayout.Asd) throw new Exception("Original confirmed A+auxiliary case regressed.");
            Console.WriteLine("Actual D3DSCache association: typed official backend readonly response verified; original A+auxiliary remains Normal/Asd; no cache/system/GPU mutation.");
        }
        finally { await client.DisposeAsync(); }
    }
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-d3ds-owned-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var data = Path.Combine(root, "owned-tool-data"); Directory.CreateDirectory(data); var id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(data, "isolated-test.marker"), id.ToString("D")); AppDataPaths.ConfigureIsolatedDataRoot(data, id);
        var exe = Path.Combine(root, "安装 & 游戏", "DeltaForce", "Binaries", "Win64", "DeltaForceClient-Win64-Shipping.exe");
        var count = 0; var serial = 0;
        void Check(bool pass, string why) { if (!pass) throw new Exception(why); count++; }
        string Fixture(string owner, bool payload = true, bool compiler = true, bool driver = true, string? secondOwner = null)
        {
            var cache = Path.Combine(root, "cache-" + serial++); var app = Path.Combine(cache, "app"); Directory.CreateDirectory(app);
            var path = Path.Combine(app, (driver ? D3dsCacheInspection.DriverCache + "_VEN_10DE&DEV_1234" : "F4EB2D6C-ED2B-4BDD-AD9D-F913287E6768") + ".dxcache");
            string Quote(string value) => "'" + value.Replace("'", "''") + "'";
            // 仅此测试夹具创建自己的SQLite数据库；产品只读接口没有写入入口。
            IntPtr db = IntPtr.Zero;
            try
            {
                if (sqlite3_open_v2(Utf8(path), out db, 2 | 4, IntPtr.Zero) != 0) throw new Exception("Owned fixture open failed");
                var sql = "CREATE TABLE app_id(exe_path TEXT); CREATE TABLE keys(keyid INTEGER PRIMARY KEY); CREATE TABLE vals(keyid INTEGER,type INTEGER,value BLOB); INSERT INTO app_id VALUES(" + Quote(owner) + ");";
                if (secondOwner is not null) sql += "INSERT INTO app_id VALUES(" + Quote(secondOwner) + ");";
                if (compiler) sql += "CREATE TABLE compiler_id(family TEXT); INSERT INTO compiler_id VALUES('NVIDIA_OWNED_TEST');";
                if (payload) sql += "INSERT INTO keys VALUES(1); INSERT INTO vals VALUES(1,0,zeroblob(128));";
                if (sqlite3_exec(db, Utf8(sql), IntPtr.Zero, IntPtr.Zero, out var error) != 0)
                { var message = Marshal.PtrToStringUTF8(error); sqlite3_free(error); throw new Exception(message); }
            }
            finally { if (db != IntPtr.Zero) sqlite3_close_v2(db); }
            return cache;
        }
        D3dsCacheEvidence Read(string cache) => D3dsCacheInspection.ReadAt(cache, exe);
        Dictionary<string, string> Hashes(string cache) => Directory.GetFiles(cache, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        var owned = Fixture(exe); var before = Hashes(owned); var positive = Read(owned); var after = Hashes(owned);
        Check(positive.Complete && positive.HasOwnedDriverData && positive.Entries.Count == 1, "exact full executable path and associated driver records are positive ownership evidence");
        Check(before.Count == after.Count && before.All(p => after[p.Key] == p.Value), "read-only immutable SQL does not create WAL/SHM or alter owned database");
        Check(Read(Fixture(exe.ToUpperInvariant())).HasOwnedDriverData, "Windows path ownership comparison is case insensitive");
        var wrongGpu = D3dsCacheInspection.ReadAt(owned, exe, "VEN_10DE&DEV_9999");
        Check(!wrongGpu.HasOwnedDriverData && wrongGpu.Entries.Single().MatchesAdapter == false, "old hardware cache cannot qualify as compatible current evidence");
        Check(D3dsCacheInspection.ReadAt(owned, exe, "VEN_10DE&DEV_1234").HasOwnedDriverData, "matching VEN/DEV retains positive evidence");
        foreach (var other in new[] { exe + ".backup", Path.Combine(root, "different-install", Path.GetFileName(exe)), Path.Combine(Path.GetDirectoryName(exe)!, "UnrealCEFSubProcess.exe") })
            Check(!Read(Fixture(other)).HasOwnedDriverData, "substring, another installation, and CEF helper cannot establish game cache ownership");
        Check(!Read(Fixture(exe, payload: false)).HasOwnedDriverData, "empty schema is not proof of cache use");
        Check(!Read(Fixture(exe, driver: false)).HasOwnedDriverData, "conversion session with payload is not driver-cache evidence");
        Check(!Read(Fixture(exe, compiler: false)).HasOwnedDriverData, "unverified compiler metadata cannot establish driver-data evidence");
        var ambiguous = Read(Fixture(exe, secondOwner: Path.Combine(root, "other.exe")));
        Check(!ambiguous.HasOwnedDriverData && !ambiguous.Complete, "multi-owner database remains unqualified");
        var journal = Fixture(exe); var main = Directory.GetFiles(journal, "*.dxcache", SearchOption.AllDirectories).Single(); File.WriteAllBytes(main + "-wal", [1, 2, 3, 4]);
        var journalBefore = Hashes(journal); var pending = Read(journal);
        Check(!pending.Complete && !pending.HasOwnedDriverData, "nonempty WAL is not silently ignored by immutable mode");
        Check(journalBefore.All(p => Hashes(journal)[p.Key] == p.Value), "journal rejection does not modify sidecars");
        var split = Path.Combine(root, "split", "app"); Directory.CreateDirectory(split);
        File.WriteAllBytes(Path.Combine(split, "test.val"), Encoding.Unicode.GetBytes("\0" + exe + "\0"));
        var splitReport = Read(Path.GetDirectoryName(split)!);
        Check(!splitReport.Complete && !splitReport.HasOwnedDriverData && splitReport.Entries.Count == 1, "index/value path candidate is reported without pretending payload ownership verification");
        var bad = Fixture(exe); File.WriteAllBytes(Directory.GetFiles(bad, "*.dxcache", SearchOption.AllDirectories).Single(), new byte[256]);
        Check(!Read(bad).Complete && !Read(bad).HasOwnedDriverData, "unknown/corrupt file format is not treated as empty or positive");
        var busy = Fixture(exe); var busyPath = Directory.GetFiles(busy, "*.dxcache", SearchOption.AllDirectories).Single();
        using (var writer = new FileStream(busyPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            Check(!Read(busy).Complete && !Read(busy).HasOwnedDriverData, "a concurrently writable source is not asserted immutable");
        Check(Read(Path.Combine(root, "absent")).Complete && !Read(Path.Combine(root, "absent")).HasOwnedDriverData, "absent cache has no invented records");

        ShaderDiagnosis Diagnosis(ShaderDiagLevel level, ShaderCacheLayout layout) => new() { Level = level, CacheLayout = layout, Summary = "owned original", Details = [], Advice = "owned original" };
        var status = new ShaderStatus { IsNvidia = true, DriverVersion = "621.14", DriverState = ShaderDriverState.Ok, DriverAdvice = "owned", IsGameRunning = false };
        var defaults = new NvShaderCacheReport { Scopes = [new("全局设置", "default", null, "owned"), new("三角洲程序设置", "default", null, "owned")] };
        var matched = D3dsCacheInspection.ReadAt(owned, exe, "VEN_10DE&DEV_1234");
        var systemActive = D3dsCacheInspection.Combine(Diagnosis(ShaderDiagLevel.Warning, ShaderCacheLayout.Legacy), matched);
        Check(systemActive.Level == ShaderDiagLevel.Normal && !systemActive.IsLayoutUncertain && systemActive.CacheLayout == ShaderCacheLayout.Asd
            && systemActive.Summary.StartsWith("ASD已激活"), "local B plus verified-owned and adapter-matched system data is active ASD, not a yellow coexistence warning");
        Check(DashboardShaderHealth.Evaluate(status, systemActive, defaults, true).State == DashboardHealthState.Normal, "homepage uses green normal state for matched system evidence despite local B");
        Check(systemActive.Details.Any(d => d.Contains("本地 B 层缓存仍存在")), "local B coexistence is informational detail rather than the status conclusion");
        Check(D3dsCacheInspection.Combine(Diagnosis(ShaderDiagLevel.Warning, ShaderCacheLayout.Legacy), positive).CacheLayout == ShaderCacheLayout.Legacy, "unverified adapter cannot promote a cache-layout conclusion");
        Check(D3dsCacheInspection.Combine(Diagnosis(ShaderDiagLevel.Warning, ShaderCacheLayout.Legacy), wrongGpu).CacheLayout == ShaderCacheLayout.Legacy, "conflicting hardware cannot promote a cache-layout conclusion");
        var red = D3dsCacheInspection.Combine(Diagnosis(ShaderDiagLevel.Abnormal, ShaderCacheLayout.Unspecified), matched);
        Check(red.Level == ShaderDiagLevel.Abnormal && red.Summary == "owned original", "system cache presence cannot hide zero-file or multi-version abnormality");
        var green = D3dsCacheInspection.Combine(Diagnosis(ShaderDiagLevel.Normal, ShaderCacheLayout.Asd), positive);
        Check(green.Level == ShaderDiagLevel.Normal && green.CacheLayout == ShaderCacheLayout.Asd, "confirmed A plus auxiliary ASD layout retains green with correlation details");
        var noData = D3dsCacheInspection.Combine(Diagnosis(ShaderDiagLevel.Warning, ShaderCacheLayout.Legacy), new([], []));
        Check(noData.CacheLayout == ShaderCacheLayout.Legacy, "unrelated or empty system caches cannot override B evidence");
        var wire = JsonSerializer.Deserialize<ShaderDiagnosis>(JsonSerializer.Serialize(systemActive))!;
        Check(wire.SystemCache?.HasOwnedDriverData == true && !wire.IsLayoutUncertain && wire.Level == ShaderDiagLevel.Normal
            && wire.CacheLayout == ShaderCacheLayout.Asd, "active system-evidence result survives DTO serialization without a stale yellow flag");
        var custom = new NvShaderCacheReport { Scopes = [new("全局设置", "custom", 0, "owned"), new("三角洲程序设置", "default", null, "owned")] };
        Check(DashboardShaderHealth.Evaluate(status, systemActive, custom, true) is { State: DashboardHealthState.Abnormal, OpenCacheRecovery: true }, "system-derived active ASD does not suppress independent cache-setting errors");
        Check(DashboardShaderHealth.Evaluate(status, systemActive, new(), true) is { State: DashboardHealthState.Warning, OpenCacheRecovery: true }, "independent NVAPI read failure remains actionable after ASD promotion");
        foreach (var level in new[] { ShaderDiagLevel.NotApplicable, ShaderDiagLevel.Info, ShaderDiagLevel.AbnormalDriverIssue })
            Check(D3dsCacheInspection.Combine(Diagnosis(level, ShaderCacheLayout.Unspecified), matched).Level == level, "system evidence cannot bypass inapplicable, unwarmed or confirmed abnormal states");
        var unknown = new ShaderDiagnosis { Level = ShaderDiagLevel.Warning, CacheLayout = ShaderCacheLayout.Unspecified, IsLayoutUncertain = true, Summary = "owned unknown", Details = [], Advice = "owned unknown" };
        Check(D3dsCacheInspection.Combine(unknown, matched).IsLayoutUncertain, "unparsed local layout is not silently changed by this B-coexistence-only policy");
        Check(JsonSerializer.Deserialize<ShaderDiagnosis>("{\"Level\":2,\"Summary\":\"old\",\"Details\":[],\"Advice\":\"old\"}")!.SystemCache is null, "old payload does not invent D3DSCache evidence");

        const long mb = 1024L * 1024;
        var pso = Path.Combine(root, "PSO"); var dx = Path.Combine(pso, "GameVer_Test", "SM6", "DXCache"); Directory.CreateDirectory(dx);
        NvphFixtureFiles.Write(Path.Combine(dx, "a.nvph"), 256 * mb);
        var expanded = Path.Combine(dx, "b.nvph"); NvphFixtureFiles.WriteNvuc(expanded, records: 400, decodedBytes: 16384);
        foreach (var size in new[] { 256 * mb, 512 * mb })
        {
            using (var file = new FileStream(expanded, FileMode.Open, FileAccess.Write)) file.SetLength(size);
            var layer = NvphLayoutProbe.Read(expanded);
            Check(layer.AverageFootprint >= 12 * 1024 && layer.Layer == NvphLayoutProbe.NvphLayer.Bytecode, "decoded bytecode overrides A-like footprint regardless of 256/512MB preallocation");
            var diagnosis = new ShaderInspection(null, () => new(ShaderGpuDetectionState.Nvidia, "621.14")).DiagnoseAt(pso);
            Check(diagnosis.CacheLayout == ShaderCacheLayout.Legacy && diagnosis.Summary.StartsWith("未激活ASD"), "A plus expanded B retains its local evidence before correlation");
            var combined = D3dsCacheInspection.Combine(diagnosis, matched);
            Check(combined.Level == ShaderDiagLevel.Normal && combined.CacheLayout == ShaderCacheLayout.Asd && !combined.IsLayoutUncertain, "A plus 256/512MB B plus matched system data is active ASD");
            Check(diagnosis.Level == ShaderDiagLevel.Warning && diagnosis.CacheLayout == ShaderCacheLayout.Legacy, "combining evidence does not mutate the original local snapshot");
        }
        Console.WriteLine($"D3DSCache association: {count} checks passed; owned SQLite/NVPH fixtures only; no real cache/system/GPU writes. Evidence: {root}");
    }
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + '\0');
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr arg, out IntPtr error);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_close_v2(IntPtr db);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void sqlite3_free(IntPtr pointer);
}
