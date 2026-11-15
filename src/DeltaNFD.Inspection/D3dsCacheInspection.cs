using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DeltaNFD.Services;

namespace DeltaNFD.Inspection;

/// <summary>仅关联完整游戏exe路径；不创建缓存会话，不用目录容量或子进程路径证明ASD。</summary>
internal static class D3dsCacheInspection
{
    internal const string DriverCache = "6F75932F-7DFC-4FB0-B4B8-12DE1AC415DA";
    internal static D3dsCacheEvidence Read(string psoPath, string pnpDeviceId = "")
    {
        try
        {
            var game = Directory.GetParent(Path.GetFullPath(psoPath))?.Parent?.FullName ?? "";
            var exe = Path.Combine(game, "Binaries", "Win64", "DeltaForceClient-Win64-Shipping.exe");
            if (!File.Exists(exe)) return new([], ["未能核对当前安装的游戏程序路径，未关联系统缓存。"]);
            var adapter = System.Text.RegularExpressions.Regex.Match(pnpDeviceId, @"VEN_[0-9A-F]{4}&DEV_[0-9A-F]{4}", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Value;
            return ReadAt(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D3DSCache"), exe, adapter);
        }
        catch (Exception ex) { return new([], ["系统缓存关联未完成：" + ex.Message]); }
    }
    internal static string Canonical(string path)
    {
        if (path.IndexOf('\0') >= 0 || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("不是本地完整程序路径。");
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
    internal static D3dsCacheEvidence ReadAt(string root, string executable, string adapter = "")
    {
        var entries = new List<D3dsCacheEntry>(); var errors = new List<string>(); var clock = Stopwatch.StartNew();
        try
        {
            executable = Canonical(executable); StartupItemStore.RejectLinks(root);
            if (!Directory.Exists(root)) return new([], []);
            var files = 0; var dirs = 0;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (++dirs > 128) throw new IOException("系统缓存目录超过128个，当前关联未覆盖全部。");
                StartupItemStore.RejectLinks(directory);
                foreach (var path in Directory.EnumerateFiles(directory))
                {
                    if (entries.Count >= 32) throw new IOException("关联结果超过32项，未覆盖全部。");
                    var extension = Path.GetExtension(path);
                    if (!extension.Equals(".dxcache", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".val", StringComparison.OrdinalIgnoreCase)) continue;
                    if (++files > 256 || clock.ElapsedMilliseconds > 2000) throw new IOException("系统缓存关联达到本次读取预算，未覆盖全部。");
                    try
                    {
                        StartupItemStore.RejectLinks(path);
                        // 持有只读且拒绝写入/替换的句柄；正在写的库保留未知，不切换到可写SQLite连接。
                        using var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        if (guard.Length < 16) throw new IOException("系统缓存头不完整。");
                        if (extension.Equals(".val", StringComparison.OrdinalIgnoreCase))
                        {
                            var head = new byte[(int)Math.Min(65536, guard.Length)]; guard.ReadExactly(head);
                            var text = Encoding.Unicode.GetString(head);
                            if (text.Contains(executable + '\0', StringComparison.OrdinalIgnoreCase))
                                entries.Add(new(Path.GetFileName(path), executable, false, "index/value", "仅匹配完整路径候选，未验证索引记录；不作为ASD已激活的依据。"));
                            errors.Add(Path.GetFileName(path) + "：分离索引格式尚未完整解析。");
                            continue;
                        }
                        var signature = new byte[16]; guard.ReadExactly(signature);
                        if (!signature.AsSpan().SequenceEqual("SQLite format 3\0"u8)) throw new IOException("未识别的系统缓存格式。");
                        EnsureNoJournal(path);
                        using var db = new ReadOnlyCacheDatabase(path, clock);
                        var owners = db.Query("SELECT exe_path FROM app_id LIMIT 3", 3);
                        if (owners.Count > 1 && owners.Any(r => Canonical(r[0]).Equals(executable, StringComparison.OrdinalIgnoreCase)))
                            throw new IOException("同一库存在多个程序归属，未作为三角洲独占证据。");
                        if (owners.Count != 1 || owners[0].Count != 1 || !Canonical(owners[0][0]).Equals(executable, StringComparison.OrdinalIgnoreCase)) continue;
                        var name = Path.GetFileName(path);
                        var driver = name.StartsWith(DriverCache + "_VEN_10DE&", StringComparison.OrdinalIgnoreCase);
                        var tag = "_" + adapter; var tagAt = name.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
                        bool? matchesAdapter = adapter.Length == 0 ? null : tagAt >= 0 && tagAt + tag.Length < name.Length && name[tagAt + tag.Length] is '&' or '.';
                        var tables = db.Query("SELECT name FROM sqlite_master WHERE type='table' AND name IN ('keys','vals','compiler_id')", 4);
                        var hasCompiler = tables.Any(r => r[0] == "compiler_id");
                        var rows = driver && hasCompiler ? db.Query("SELECT length(v.value) FROM vals v JOIN keys k ON k.keyid=v.keyid WHERE v.type=0 AND typeof(v.value)='blob' AND length(v.value)>32 LIMIT 1", 1) : [];
                        var compiler = hasCompiler ? db.Query("SELECT family FROM compiler_id LIMIT 2", 2) : [];
                        EnsureNoJournal(path);
                        entries.Add(new(name, executable, rows.Count == 1, "SQLite", driver
                            ? "完整exe归属已核对；" + (rows.Count == 1 ? "发现关联键的非空驱动缓存记录。" : "未取得有效驱动数据记录。")
                                + " 编译器=" + string.Join(",", compiler.Select(r => r[0]))
                                + (matchesAdapter == false ? "；显卡VEN/DEV不匹配，不用于当前判定。" : matchesAdapter == true ? "；显卡VEN/DEV一致。" : "；当前显卡标识未取得。")
                                + " 编译器标识未映射为当前驱动版本，不证明当前仍在使用。"
                            : "完整exe归属已核对；转换/其他会话库，不作为驱动托管缓存已使用的证据。", matchesAdapter));
                    }
                    catch (Exception ex) { errors.Add(Path.GetFileName(path) + "：" + ex.Message); }
                }
            }
        }
        catch (Exception ex) { errors.Add(ex.Message); }
        return new(entries, errors.Take(32).Select(e => e.Length > 300 ? e[..300] : e).ToArray());
    }
    private static void EnsureNoJournal(string path)
    {
        foreach (var suffix in new[] { "-wal", "-journal" })
        {
            try
            {
                var attributes = File.GetAttributes(path + suffix);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || new FileInfo(path + suffix).Length > 0)
                    throw new IOException("存在未合并日志，未按immutable忽略日志读取旧快照。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    internal static ShaderDiagnosis Combine(ShaderDiagnosis original, D3dsCacheEvidence evidence)
    {
        var details = original.Details.ToList();
        details.Add($"D3DSCache归属交叉核对：同一游戏完整exe路径命中 {evidence.Entries.Count} 个库，其中未发现显卡冲突的驱动数据记录证据 {evidence.Entries.Count(e => e.HasDriverData && e.MatchesAdapter != false)} 个。" );
        details.AddRange(evidence.Entries.Select(e => $"· {e.FileName}：{e.Detail}"));
        if (!evidence.Complete) details.Add("系统缓存关联未覆盖全部：" + string.Join("；", evidence.Errors));
        details.Add("ASD提示是本工具的缓存落盘证据判定；目录容量、转换库或子进程缓存不算有效证据，记录本身不证明实时写入或驱动开关。");
        // 本地B存在不否定系统缓存落盘证据；必须明确匹配显卡，null归属不能用于提升状态。
        var systemActive = original.Level == ShaderDiagLevel.Warning && original.CacheLayout == ShaderCacheLayout.Legacy
            && evidence.Entries.Any(e => e.HasDriverData && e.MatchesAdapter == true);
        if (systemActive) details.Add("本地 B 层缓存仍存在，可能是旧残留或两种路径并存；不因此将 ASD 标黄，也不据此断言 B 已停止写入。");
        return new()
        {
            Level = systemActive ? ShaderDiagLevel.Normal : original.Level, CacheLayout = systemActive ? ShaderCacheLayout.Asd : original.CacheLayout,
            IsLayoutUncertain = systemActive ? false : original.IsLayoutUncertain, SystemCache = evidence,
            Summary = systemActive ? "ASD已激活；已核对三角洲系统缓存归属及显卡标识。" : original.Summary,
            Details = details,
            Advice = systemActive ? "已取得匹配显卡的系统缓存落盘证据，无需仅因本地 B 文件并存而清理。此为缓存证据判定，未直接读取驱动开关或验证实时写入。" : original.Advice,
        };
    }
}

/// <summary>系统SQLite，固定SELECT；mode=ro+READONLY+immutable且源文件由外层只读句柄固定，无日志时才读取。</summary>
internal sealed class ReadOnlyCacheDatabase : IDisposable
{
    private IntPtr _db;
    private readonly Progress _progress;
    internal ReadOnlyCacheDatabase(string path, Stopwatch clock)
    {
        _progress = _ => clock.ElapsedMilliseconds > 2000 ? 1 : 0;
        try
        {
            var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri + "?mode=ro&immutable=1&cache=private";
            if (sqlite3_open_v2(Utf8(uri), out _db, 0x1 | 0x40 | 0x40000, IntPtr.Zero) != 0 || sqlite3_db_readonly(_db, Utf8("main")) != 1)
                throw new IOException("只读系统SQLite打开失败。");
            sqlite3_limit(_db, 0, 16 * 1024 * 1024); sqlite3_limit(_db, 1, 4096); sqlite3_limit(_db, 2, 64);
            sqlite3_progress_handler(_db, 500, _progress, IntPtr.Zero);
            Query("PRAGMA trusted_schema=OFF", 0);
        }
        catch { Dispose(); throw; }
    }
    internal List<List<string>> Query(string sql, int maxRows)
    {
        IntPtr statement = IntPtr.Zero;
        try
        {
            if (sqlite3_prepare_v2(_db, Utf8(sql), -1, out statement, IntPtr.Zero) != 0) throw new IOException("系统缓存表结构不受支持。");
            var rows = new List<List<string>>(); int step;
            while ((step = sqlite3_step(statement)) == 100)
            {
                if (rows.Count >= maxRows) throw new IOException("系统缓存元数据条数超过上限。");
                var row = new List<string>();
                var columns = sqlite3_column_count(statement);
                if (columns > 8) throw new IOException("系统缓存列数异常。");
                for (var i = 0; i < columns; i++)
                {
                    var text = sqlite3_column_text(statement, i); var bytes = sqlite3_column_bytes(statement, i);
                    if (bytes > 32768) throw new IOException("系统缓存元数据过长。");
                    row.Add(text == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(text, bytes) ?? "");
                }
                rows.Add(row);
            }
            if (step != 101) throw new IOException("系统缓存读取中断、损坏或超过本次预算。");
            return rows;
        }
        finally { if (statement != IntPtr.Zero) sqlite3_finalize(statement); }
    }
    public void Dispose() { if (_db != IntPtr.Zero) { sqlite3_close_v2(_db); _db = IntPtr.Zero; } GC.KeepAlive(_progress); }
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + '\0');
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Progress(IntPtr state);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_db_readonly(IntPtr db, byte[] schema);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_column_bytes(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_column_count(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_close_v2(IntPtr db);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_limit(IntPtr db, int id, int value);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void sqlite3_progress_handler(IntPtr db, int operations, Progress callback, IntPtr state);
}
