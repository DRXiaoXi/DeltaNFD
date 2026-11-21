using System.Diagnostics;
using System.Text.Json;
using DeltaNFD.Services.TweakDb;

namespace DeltaNFD.Services;

/// <summary>
/// 优化前安全软件预检 / 优化后恢复（DefenderGuard）——系统维护后端执行侧。
/// 本类只做只读状态查询（Get-MpComputerStatus / SecurityCenter2）与恢复方向/排除项的官方 Cmdlet 调用；
/// 全部命令与关键词字面量在 Assets\TweakDb\DefenderGuard.json（杀软对抗铁律，见 HANDOFF §11.1）。
/// 用户决策弹窗不再属于本类：后端只返回预检报告与收尾结果，前端负责引导。
/// 配置与资产位于程序根目录（后端 BaseDirectory 不是程序根），经 <see cref="BxCatalog.AssetRoot"/> 定位。
/// </summary>
public static class DefenderGuard
{
    private const string ConfigFileName = "DefenderGuard";

    private static readonly object _gate = new();
    private static readonly SemaphoreSlim SessionGate = new(1, 1);
    private static string JournalPath => Path.Combine(AppDataPaths.Root, "defender-session.json");
    private sealed record SessionJournal(List<string> Paths, DefenderPreflightReport? Report);
    // Isolated tests replace only the process boundary; production always uses the configured commands.
    internal static Func<string, (int Code, string? StdOut, string? StdErr)>? PowerShellForTests { get; set; }
    internal static Func<DeltaNFD.Inspection.SecurityFeatureSnapshot>? SecurityFeatureForTests { get; set; }
    public static bool HasPendingSession => File.Exists(JournalPath);

    private static SessionJournal ReadJournal()
    {
        if (!File.Exists(JournalPath)) return new([], null);
        if ((File.GetAttributes(JournalPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("Defender 会话记录含重解析点。");
        var journal = JsonSerializer.Deserialize<SessionJournal>(File.ReadAllText(JournalPath))
            ?? throw new InvalidDataException("Defender 会话恢复记录损坏。");
        var allowed = new[] { TrimDir(BackendEnvironment.AppRoot), AppDataPaths.Root };
        if (journal.Paths is null || journal.Paths.Any(p => !allowed.Contains(p, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("Defender 会话恢复记录包含非本程序目录。");
        return journal;
    }

    private static void WriteJournal(SessionJournal journal)
    {
        Directory.CreateDirectory(AppDataPaths.Root);
        var temporary = JournalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, journal);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, JournalPath, overwrite: true);
    }

    private static HashSet<string> QueryExclusions(GuardConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.ExclusionsQuery)) throw new IOException("缺少 Defender 排除项核对命令。");
        var (code, output, error) = RunPowerShell(cfg.ExclusionsQuery);
        if (code != 0 || string.IsNullOrWhiteSpace(output))
        {
            Log.Warn("Defender 排除项读取失败：" + CommandDiagnostic(code, error));
            throw new IOException($"无法核对 Defender 排除项（PowerShell 退出码 {code}），恢复记录保留。");
        }
        return new HashSet<string>(JsonSerializer.Deserialize<string[]>(output)
            ?? throw new IOException("Defender 排除项查询结果无效。"), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>最近一次预检报告（后端进程内会话状态；PostApply 用它判断用户是否动过防护）。</summary>
    private static DefenderPreflightReport? _lastReport;

    // ---------------- 配置（数据驱动） ----------------

    private sealed class GuardConfig
    {
        public string StatusQuery { get; set; } = "";
        public string AvProductsQuery { get; set; } = "";
        public string EnableRealtimeCmd { get; set; } = "";
        public string StartProtectionServiceCmd { get; set; } = "";
        public string AddExclusionCmd { get; set; } = "";
        public string RemoveExclusionCmd { get; set; } = "";
        public string ExclusionsQuery { get; set; } = "";
        public List<string> AvProductIgnoreNames { get; set; } = new();
        public List<string> InterceptTargets { get; set; } = new();
        public List<string> DefenderOffTargets { get; set; } = new();
    }

    private static GuardConfig? _config;

    private static GuardConfig GetConfig()
    {
        if (_config is not null)
        {
            return _config;
        }

        var cfg = new GuardConfig();
        try
        {
            var path = Path.Combine(BxCatalog.AssetRoot, "Assets", "TweakDb", ConfigFileName + ".json");
            var opts = new JsonSerializerOptions
            {
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                PropertyNameCaseInsensitive = true,
            };
            cfg = JsonSerializer.Deserialize<GuardConfig>(File.ReadAllText(path), opts) ?? cfg;
        }
        catch (Exception ex)
        {
            Log.Error("DefenderGuard：配置加载失败", ex);
        }

        _config = cfg;
        return cfg;
    }

    // ---------------- 预检 ----------------

    /// <summary>
    /// 应用前预检：批次包含会被安全软件拦截的安全防护类条目时，查询本机防护状态并生成预检报告。
    /// 返回 null = 批次无安全类条目，无需关注。
    /// 说明：条目名称与拼接后的操作文本仅用于「关键词匹配以决定是否引导」，不构成执行指令；
    /// 真正写入的条目由后端按自己的数据库解析。
    /// </summary>
    public static async Task<DefenderPreflightReport?> PreflightAsync(IReadOnlyList<DefenderBatchItem> batchItems)
    {
        var cfg = GetConfig();
        if (batchItems.Count == 0 || cfg.InterceptTargets.Count == 0)
        {
            return null;
        }

        var report = new DefenderPreflightReport();
        foreach (var item in batchItems)
        {
            var text = (item.Ops ?? "").ToLowerInvariant();
            if (!cfg.InterceptTargets.Any(t => text.Contains(t)))
            {
                continue;
            }

            report.SecurityItemNames.Add(item.Name);
            if (cfg.DefenderOffTargets.Any(t => text.Contains(t)))
            {
                if (item.TurnOn == false) report.DefenderRestoreTweakInBatch = true;
                else report.DefenderOffTweakInBatch = true;
            }
        }

        if (report.SecurityItemNames.Count == 0)
        {
            return null;
        }

        // 建议排除项：程序目录 + 用户数据目录（优化期间防误杀/防隔离）
        report.ExclusionPaths.Add(TrimDir(BackendEnvironment.AppRoot));
        report.ExclusionPaths.Add(AppDataPaths.Root);

        var status = await QueryStatusAsync();
        if (status is not null)
        {
            report.DefenderPresent = status.Present;
            report.InitialRealTimeProtectionOn = status.RealTimeProtectionOn;
            report.TamperProtectionOn = status.TamperProtectionOn;
        }
        else
        {
            // 状态查不到但 SecurityCenter2 有 Defender 登记：按最保守处理（防护在开）
            report.DefenderPresent = (await QueryThirdPartyAvRawAsync(cfg))
                .Any(n => n.Equals("Windows Defender", StringComparison.OrdinalIgnoreCase));
            report.InitialRealTimeProtectionOn = report.DefenderPresent;
            report.TamperProtectionOn = report.DefenderPresent;
        }

        foreach (var name in await QueryThirdPartyAvAsync(cfg))
        {
            report.ThirdPartyAvNames.Add(name);
        }

        lock (_gate)
        {
            _lastReport = report;
        }

        Log.Info("DefenderGuard：预检 —— " + Describe(report));
        return report;
    }

    public sealed record DefenderStatus(bool Present, bool RealTimeProtectionOn, bool TamperProtectionOn);
    private sealed record ProtectionReading(bool Present, bool RealTimeProtectionOn, bool? TamperProtectionOn);

    /// <summary>只读查询 Defender 状态；cmdlet 不存在 / 查询失败返回 null。</summary>
    public static async Task<DefenderStatus?> QueryStatusAsync()
    {
        var reading = await ReadProtectionAsync();
        return reading is { TamperProtectionOn: bool tamper }
            ? new DefenderStatus(reading.Present, reading.RealTimeProtectionOn, tamper) : null;
    }

    private static async Task<ProtectionReading?> ReadProtectionAsync()
    {
        var cfg = GetConfig();
        if (string.IsNullOrWhiteSpace(cfg.StatusQuery))
        {
            Log.Warn("Defender 防护状态读取失败：缺少状态查询配置。");
            return null;
        }

        var (code, stdout, error) = await Task.Run(() => RunPowerShell(cfg.StatusQuery));
        var reading = code == 0 ? ParseStatus(stdout) : null;
        if (reading is null)
            Log.Warn("Defender 防护状态读取失败：" + CommandDiagnostic(code, error) + "；未取得有效的核心防护状态。");
        else if (reading.TamperProtectionOn is null)
            Log.Info($"Defender 防护状态：实时保护={reading.RealTimeProtectionOn}；篡改防护字段不可用。");
        return reading;
    }

    /// <summary>重新查询防护状态，不回写预检报告（报告里的 Initial* 必须保持预检当时的值）。</summary>
    public static async Task<(bool RealTimeOn, bool TamperOn)> RefreshProtectionStateAsync()
    {
        var status = await QueryStatusAsync();
        return status is null ? throw new IOException("未能复核 Defender 防护状态，本次检查未通过。")
            : (status.RealTimeProtectionOn, status.TamperProtectionOn);
    }

    private static ProtectionReading? ParseStatus(string? stdout)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(stdout))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(stdout.Trim());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            bool? Flag(string name) => root.TryGetProperty(name, out var flag)
                && flag.ValueKind is JsonValueKind.True or JsonValueKind.False ? flag.GetBoolean() : null;
            return Flag("av") is bool av && Flag("rtp") is bool rtp
                ? new ProtectionReading(av, rtp, Flag("tamper")) : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<List<string>> QueryThirdPartyAvRawAsync(GuardConfig cfg)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(cfg.AvProductsQuery))
        {
            return result;
        }

        var (_, stdout, _) = await Task.Run(() => RunPowerShell(cfg.AvProductsQuery));
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return result;
        }

        foreach (var raw in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Length > 0 && !result.Contains(raw))
            {
                result.Add(raw);
            }
        }

        return result;
    }

    /// <summary>第三方杀软清单（按配置排除 Defender 自身）。</summary>
    private static async Task<List<string>> QueryThirdPartyAvAsync(GuardConfig cfg)
    {
        var result = new List<string>();
        foreach (var name in await QueryThirdPartyAvRawAsync(cfg))
        {
            if (cfg.AvProductIgnoreNames.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add(name);
        }

        return result;
    }

    // ---------------- 会话排除项 ----------------

    /// <summary>
    /// 把后端自身解析的建议目录加入 Defender 排除列表（会话内记录；应用结束后由 PostApply 移除）。
    /// 被组策略/杀软拦截时静默失败，仅记录日志。路径来自后端自身环境，不接受前端传入。
    /// </summary>
    public static async Task AddSessionExclusionsAsync()
    {
        await SessionGate.WaitAsync();
        try
        {
        var cfg = GetConfig();
        if (string.IsNullOrWhiteSpace(cfg.AddExclusionCmd))
        {
            throw new IOException("缺少 Defender 排除项命令。");
        }
        var journal = ReadJournal();
        var existing = QueryExclusions(cfg);
        if (journal.Report is null && _lastReport is not null)
        {
            journal = journal with { Report = _lastReport };
            WriteJournal(journal);
        }
        var paths = new[] { TrimDir(BackendEnvironment.AppRoot), AppDataPaths.Root };
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (existing.Contains(path)) continue; // Never claim ownership of a pre-existing exclusion.
            if (!journal.Paths.Contains(path, StringComparer.OrdinalIgnoreCase)) journal.Paths.Add(path);
            journal = journal with { Report = journal.Report ?? _lastReport };
            WriteJournal(journal); // Write ahead of the system change, including unsuccessful/uncertain adds.
            var (code, _, error) = await Task.Run(() => RunPowerShell(string.Format(cfg.AddExclusionCmd, path.Replace("'", "''"))));
            if (code != 0 || !QueryExclusions(cfg).Contains(path))
            {
                Log.Warn("Defender 临时排除项添加未确认：" + CommandDiagnostic(code, error));
                throw new IOException("Defender 排除项添加未确认，已保留恢复记录。");
            }
            Log.Info($"DefenderGuard：已请求添加 Defender 排除项（exit={code}）—— {path}");
        }
        }
        finally { SessionGate.Release(); }
    }

    private static void RemoveSessionExclusions()
    {
        var cfg = GetConfig();
        var journal = ReadJournal();
        foreach (var path in journal.Paths.ToArray())
        {
            if (QueryExclusions(cfg).Contains(path))
            {
                if (string.IsNullOrWhiteSpace(cfg.RemoveExclusionCmd)) throw new IOException("缺少 Defender 清理命令。");
                var (code, _, error) = RunPowerShell(string.Format(cfg.RemoveExclusionCmd, path.Replace("'", "''")));
                if (code != 0) Log.Warn("Defender 临时排除项移除命令异常：" + CommandDiagnostic(code, error));
                if (QueryExclusions(cfg).Contains(path)) throw new IOException("Defender 临时排除项移除未确认，恢复记录保留。");
            }
            journal.Paths.Remove(path);
            WriteJournal(journal);
        }
    }

    // ---------------- 应用后收尾 ----------------

    /// <summary>
    /// 应用完成后的收尾：移除会话排除项；优化前防护在位且现在被关闭时，尽力恢复实时保护。
    /// 返回是否应提醒用户重新开启安全防护。
    /// </summary>
    public static async Task<DefenderPostApplyResult> PostApplyAsync(bool batchNotStarted = false)
    {
        await SessionGate.WaitAsync();
        DefenderPostApplyResult? result = null;
        try
        {
        var journal = ReadJournal();
        DefenderPreflightReport? report;
        lock (_gate)
        {
            report = journal.Report ?? _lastReport;
        }
        if (batchNotStarted && report is not null)
        {
            // No optimization was submitted: never retain a planned disable as a completed change on retry.
            report.DefenderOffTweakInBatch = report.DefenderRestoreTweakInBatch = false;
            WriteJournal(journal with { Report = report });
        }
        else if (report is not null && !HasPendingSession) WriteJournal(new([], report));
        result = new DefenderPostApplyResult();
        Log.Info($"Defender 收尾开始：批次未执行={batchNotStarted}；需核对的临时排除项={journal.Paths.Count}；预检实时保护={report?.InitialRealTimeProtectionOn}；计划恢复原值={report?.DefenderRestoreTweakInBatch}；包含 Defender 优化={report?.DefenderOffTweakInBatch}。");
        try { RemoveSessionExclusions(); }
        catch (Exception ex)
        {
            Log.Warn($"Defender 临时排除项收尾失败：{ex.GetType().Name}：{ex.Message}");
            result.Remind = true; result.CleanupSucceeded = false; result.Message = ex.Message;
            return result;
        }

        if (report is null || !report.DefenderPresent || !report.InitialRealTimeProtectionOn)
        {
            // 优化前防护就不在位（或批次无安全类条目）：无需恢复
            return result;
        }

        // Exclusions have already been independently removed above. A deliberately stopped service can make
        // the Defender cmdlet unavailable; that does not create a new temporary-session cleanup obligation.
        DeltaNFD.Inspection.SecurityFeatureSnapshot? core = null;
        if (report.DefenderOffTweakInBatch || report.DefenderRestoreTweakInBatch)
        {
            core = ReadCore();
            if (ResolveKnownBatchIntent(report, core) is { } known) { result = known; return result; }
        }

        var status = await ReadProtectionAsync();
        if (status?.RealTimeProtectionOn != true)
        {
            core ??= ReadCore();
            var startCommand = GetConfig().StartProtectionServiceCmd;
            if (core.IsStoppedWithEnabledStartup && !string.IsNullOrWhiteSpace(startCommand))
            {
                var (code, _, error) = await Task.Run(() => RunPowerShell(startCommand));
                Log.Info("Defender 恢复核心服务：" + CommandDiagnostic(code, error));
                core = ReadCore();
                status = await ReadProtectionAsync();
            }
        }
        if (status is null)
        {
            core ??= ReadCore();
            Log.Warn($"Defender 收尾只读服务证据：{core.State}；配置禁用={core.ConfiguredDisabled}；{core.Detail}");
            // Only a confirmed, enabled and running service justifies retrying the restore command.
            if (core is not { State: BxState.Off, ConfiguredDisabled: false })
            {
                result.CleanupSucceeded = false; result.Remind = true;
                result.Message = "临时排除项已核对。Defender 防护状态仍无法复核，会话记录保留。"
                    + core.Detail + " 请在 Windows 安全中心查看防护状态后再核对。";
                return result;
            }
        }

        if (status is { RealTimeProtectionOn: true, TamperProtectionOn: true })
        {
            // 防护状态与预检时一致（用户没动过，走了「仍然继续」）：除第三方杀软外不提醒
            result.Remind = report.ThirdPartyAvNames.Count > 0;
            return result;
        }

        if (status?.RealTimeProtectionOn != true)
        {
            var cfg = GetConfig();
            if (!string.IsNullOrWhiteSpace(cfg.EnableRealtimeCmd))
            {
                var (code, _, error) = await Task.Run(() => RunPowerShell(cfg.EnableRealtimeCmd));
                Log.Info("Defender 实时保护恢复命令：" + CommandDiagnostic(code, error));
                status = await ReadProtectionAsync();
                if (status?.RealTimeProtectionOn != true)
                { result.CleanupSucceeded = false; result.Message = $"Defender 实时保护恢复未通过复核（PowerShell 退出码 {code}），会话记录保留。"; }
            }
            else { result.CleanupSucceeded = false; result.Message = "缺少 Defender 实时保护恢复命令。"; }
        }

        if (result.CleanupSucceeded && status is { RealTimeProtectionOn: true, TamperProtectionOn: null })
            result.Message = "实时保护已确认开启。请在 Windows 安全中心核对篡改防护。";
        result.Remind = true;
        return result;
        }
        finally
        {
            try
            {
                if (result?.CleanupSucceeded == true)
                {
                    File.Delete(JournalPath);
                    lock (_gate) _lastReport = null;
                    Log.Info($"Defender 收尾已完成：重启核对={result.RequiresReboot}；{result.Message}");
                }
            }
            finally { SessionGate.Release(); }
        }
    }

    public static async Task<OperationResult> RestoreSessionAsync(bool batchNotStarted = false)
    {
        var result = await PostApplyAsync(batchNotStarted);
        return result.CleanupSucceeded ? OperationResult.Ok("Defender 临时排除项已复核清理。") : OperationResult.Fail(result.Message);
    }

    internal static DefenderPostApplyResult? ResolveKnownBatchIntent(DefenderPreflightReport report, DeltaNFD.Inspection.SecurityFeatureSnapshot core)
    {
        if (report.DefenderOffTweakInBatch && !report.DefenderRestoreTweakInBatch && core.ConfiguredDisabled && core.State == BxState.On)
            return new() { Remind = true, DefenderLeftOff = true,
                Message = "Defender 核心服务已按本批意图禁用并确认停止；临时排除项已复核清理，可单独恢复 Defender 条目。" };
        if (report.DefenderOffTweakInBatch && !report.DefenderRestoreTweakInBatch && core.ConfiguredDisabled && core.State == BxState.Mixed)
            return new() { Remind = true, RequiresReboot = true,
                Message = "Defender 已回读到本次选择的启动配置，运行状态仍待重启后核对。临时排除项已复核清理。" };
        if (report.DefenderRestoreTweakInBatch && !report.DefenderOffTweakInBatch && !core.ConfiguredDisabled
            && core.State is BxState.Off or BxState.Mixed)
            return new() { Remind = true, RequiresReboot = true,
                Message = "Defender 核心服务当前未配置禁用；实际防护仍须手动重启后核对。临时排除项已复核清理，本提示不代替条目原值恢复结果。" };
        return null;
    }

    private static string Describe(DefenderPreflightReport report) =>
        $"Defender={(report.DefenderPresent ? "在" : "无")}，实时保护={(report.InitialRealTimeProtectionOn ? "开" : "关")}，篡改防护={(report.TamperProtectionOn ? "开" : "关")}，" +
        "第三方杀软：" + (report.ThirdPartyAvNames.Count > 0 ? string.Join("、", report.ThirdPartyAvNames) : "无") +
        $"，命中条目 {report.SecurityItemNames.Count} 项（{string.Join("、", report.SecurityItemNames.Take(5))}{(report.SecurityItemNames.Count > 5 ? " …" : "")}）";

    private static string TrimDir(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static DeltaNFD.Inspection.SecurityFeatureSnapshot ReadCore() =>
        SecurityFeatureForTests?.Invoke() ?? DeltaNFD.Inspection.SecurityFeatureInspection.Read("DefenderName");

    private static string CommandDiagnostic(int code, string? error)
    {
        var output = string.Join(" ", (error ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return $"PowerShell 退出码 {code}" + (output.Length == 0 ? "" : "；错误输出：" + output[..Math.Min(output.Length, 768)]);
    }

    // ---------------- 进程调用 ----------------

    /// <summary>运行只读/恢复方向 PowerShell 命令（与 BxService.RunPowerShell 同构，但加 SilentlyContinue 前缀会掩盖命令失败，这里不加）。</summary>
    private static (int Code, string? StdOut, string? StdErr) RunPowerShell(string command)
    {
        if (PowerShellForTests is { } runner) return runner(command);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " + command,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return (-1, null, null);
            }

            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            try { p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)).GetAwaiter().GetResult(); }
            catch (TimeoutException)
            {
                // A timeout is not a completed mutation. Keep this handler and its journal alive until
                // the child exits, so cleanup cannot race a late Add/Remove command.
                Log.Warn("Defender 命令超过等待窗口，仍等待子进程结束；恢复记录保留，未强杀。");
                p.WaitForExit();
            }
            return (p.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            return (-1, null, $"{ex.GetType().Name}：{ex.Message}");
        }
    }
}
