using System.Diagnostics;
using System.Text.Json;

namespace DeltaNFD.Services;

/// <summary>Defender 预检引导的用户决策。</summary>
public enum DefenderGuardDecision
{
    /// <summary>防护已关闭（重新检测通过或本机无 Defender），继续应用。</summary>
    Proceed,

    /// <summary>用户选择跳过引导继续应用（相关条目可能被拦截而失败）。</summary>
    ProceedAnyway,

    /// <summary>用户取消应用。</summary>
    Cancel,
}

/// <summary>优化批次的安全软件预检报告。</summary>
public sealed class DefenderPreflightReport
{
    /// <summary>批次中命中安全防护关键词的条目名。</summary>
    public List<string> SecurityItemNames { get; } = new();

    /// <summary>本机是否存在 Windows Defender。</summary>
    public bool DefenderPresent { get; internal set; }

    /// <summary>预检时实时保护是否开启（查询不到时按开启保守处理）。</summary>
    public bool InitialRealTimeProtectionOn { get; internal set; }

    /// <summary>预检时篡改防护是否开启。</summary>
    public bool TamperProtectionOn { get; internal set; }

    /// <summary>检测到的第三方杀软名（SecurityCenter2，排除 Defender 自身）。</summary>
    public List<string> ThirdPartyAvNames { get; } = new();

    /// <summary>批次是否包含「关闭 Defender 本体」的条目（应用成功后不做实时保护自动恢复）。</summary>
    public bool DefenderOffTweakInBatch { get; internal set; }

    /// <summary>建议加入排除项的目录（程序目录 + %APPDATA%\Delta NFD）。</summary>
    public List<string> ExclusionPaths { get; } = new();

    /// <summary>是否需要弹引导（批次含安全类条目，且 Defender 防护在位或存在第三方杀软）。</summary>
    public bool NeedsGuidance =>
        (DefenderPresent && (InitialRealTimeProtectionOn || TamperProtectionOn)) || ThirdPartyAvNames.Count > 0;
}

/// <summary>应用完成后的安全收尾结果。</summary>
public sealed class DefenderPostApplyResult
{
    /// <summary>是否需要提醒用户重新开启安全防护。</summary>
    public bool Remind { get; internal set; }

    /// <summary>Defender 将按批次条目的选择保持关闭（提醒文案需相应说明）。</summary>
    public bool DefenderLeftOff { get; internal set; }
}

/// <summary>
/// 优化前安全软件预检 / 优化后恢复（DefenderGuard）。
/// 背景：扩展优化库的安全类条目（Windows Defender / 安全中心等）会被 Defender 的篡改防护拦截——
/// 篡改防护不允许任何程序（包括管理员）停止 Defender 服务或修改其策略，这是系统设计而非误报，
/// 代码无法也不应绕过；正确做法是应用前引导用户在安全中心手动临时关闭防护，应用完成后再提醒恢复。
/// 本类只做只读状态查询（Get-MpComputerStatus / SecurityCenter2）与恢复方向/排除项的官方 Cmdlet 调用；
/// 全部命令与关键词字面量在 Assets\TweakDb\DefenderGuard.json（杀软对抗铁律，见 HANDOFF §11.1）。
/// </summary>
public static class DefenderGuard
{
    private const string ConfigFileName = "DefenderGuard";

    private static readonly object _gate = new();
    private static readonly List<string> _sessionExclusions = new();

    /// <summary>页面注入的预检引导弹窗（返回用户决策）；未注入时按「跳过引导继续」处理。</summary>
    public static Func<DefenderPreflightReport, Task<DefenderGuardDecision>>? PreflightHook { get; set; }

    /// <summary>页面注入的应用完成「重新开启防护」提醒弹窗。</summary>
    public static Func<DefenderPostApplyResult, Task>? RestoreRemindHook { get; set; }

    // ---------------- 配置（数据驱动） ----------------

    private sealed class GuardConfig
    {
        public string StatusQuery { get; set; } = "";
        public string AvProductsQuery { get; set; } = "";
        public string EnableRealtimeCmd { get; set; } = "";
        public string AddExclusionCmd { get; set; } = "";
        public string RemoveExclusionCmd { get; set; } = "";
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
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "TweakDb", ConfigFileName + ".json");
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
    /// 应用前预检：批次包含会被安全软件拦截的安全防护类条目时，查询本机防护状态并生成预检报告，
    /// 再经 <see cref="PreflightHook"/> 引导用户。返回 null = 批次无安全类条目，无需关注。
    /// </summary>
    public static async Task<DefenderPreflightReport?> PreflightAsync(IReadOnlyList<(string Name, string Ops)> batchItems)
    {
        var cfg = GetConfig();
        if (batchItems.Count == 0 || cfg.InterceptTargets.Count == 0)
        {
            return null;
        }

        var report = new DefenderPreflightReport();
        foreach (var (name, ops) in batchItems)
        {
            var text = ops.ToLowerInvariant();
            if (!cfg.InterceptTargets.Any(t => text.Contains(t)))
            {
                continue;
            }

            report.SecurityItemNames.Add(name);
            if (cfg.DefenderOffTargets.Any(t => text.Contains(t)))
            {
                report.DefenderOffTweakInBatch = true;
            }
        }

        if (report.SecurityItemNames.Count == 0)
        {
            return null;
        }

        // 建议排除项：程序目录 + 用户数据目录（优化期间防误杀/防隔离）
        report.ExclusionPaths.Add(TrimDir(AppContext.BaseDirectory));
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

        Log.Info("DefenderGuard：预检 —— " + Describe(report));
        return report;
    }

    public sealed record DefenderStatus(bool Present, bool RealTimeProtectionOn, bool TamperProtectionOn);

    /// <summary>只读查询 Defender 状态；cmdlet 不存在 / 查询失败返回 null。</summary>
    public static async Task<DefenderStatus?> QueryStatusAsync()
    {
        var cfg = GetConfig();
        if (string.IsNullOrWhiteSpace(cfg.StatusQuery))
        {
            return null;
        }

        var (_, stdout, _) = await Task.Run(() => RunPowerShell(cfg.StatusQuery));
        return ParseStatus(stdout);
    }

    /// <summary>
    /// 重新查询防护状态（不回写报告——报告里的 Initial* 必须保持预检当时的值，供 PostApplyAsync 判断用户是否动过防护）。
    /// 返回 (实时保护仍开?, 篡改防护仍开?)；两项都关闭才算「引导通过」——
    /// 篡改防护开着时实时保护关了也照样拦截服务停止与策略写入。查询失败返回 (false, false)（不再阻塞引导）。
    /// </summary>
    public static async Task<(bool RealTimeOn, bool TamperOn)> RefreshProtectionStateAsync(DefenderPreflightReport report)
    {
        var status = await QueryStatusAsync();
        if (status is null)
        {
            return (false, false);
        }

        return (status.RealTimeProtectionOn, status.TamperProtectionOn);
    }

    private static DefenderStatus? ParseStatus(string? stdout)
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

            bool Flag(string name) => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True;
            var queried = root.TryGetProperty("av", out var avEl)
                && avEl.ValueKind is JsonValueKind.True or JsonValueKind.False;
            if (!queried)
            {
                return null;
            }

            return new DefenderStatus(Flag("av"), Flag("rtp"), Flag("tamper"));
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
    /// 把建议目录加入 Defender 排除列表（会话内记录；应用结束后由 <see cref="PostApplyAsync"/> 移除）。
    /// 被组策略/杀软拦截时静默失败，仅记录日志。
    /// </summary>
    public static async Task AddSessionExclusionsAsync(DefenderPreflightReport report)
    {
        var cfg = GetConfig();
        if (string.IsNullOrWhiteSpace(cfg.AddExclusionCmd))
        {
            return;
        }

        var pending = new List<string>();
        lock (_gate)
        {
            foreach (var path in report.ExclusionPaths)
            {
                if (!_sessionExclusions.Contains(path))
                {
                    pending.Add(path);
                }
            }
        }

        foreach (var path in pending)
        {
            var (code, _, _) = await Task.Run(() => RunPowerShell(string.Format(cfg.AddExclusionCmd, path)));
            lock (_gate)
            {
                if (!_sessionExclusions.Contains(path))
                {
                    _sessionExclusions.Add(path);
                }
            }

            Log.Info($"DefenderGuard：已请求添加 Defender 排除项（exit={code}）—— {path}");
        }
    }

    private static void RemoveSessionExclusions()
    {
        var cfg = GetConfig();
        lock (_gate)
        {
            if (_sessionExclusions.Count == 0 || string.IsNullOrWhiteSpace(cfg.RemoveExclusionCmd))
            {
                return;
            }

            foreach (var path in _sessionExclusions)
            {
                var (code, _, _) = RunPowerShell(string.Format(cfg.RemoveExclusionCmd, path));
                Log.Info($"DefenderGuard：已移除 Defender 排除项（exit={code}）—— {path}");
            }

            _sessionExclusions.Clear();
        }
    }

    // ---------------- 应用后收尾 ----------------

    /// <summary>
    /// 应用完成后的收尾：移除会话排除项；优化前防护在位且现在被关闭时，尽力恢复实时保护。
    /// 返回是否应提醒用户重新开启安全防护。
    /// </summary>
    public static async Task<DefenderPostApplyResult> PostApplyAsync(DefenderPreflightReport? report)
    {
        var result = new DefenderPostApplyResult();
        RemoveSessionExclusions();

        if (report is null || !report.DefenderPresent || !report.InitialRealTimeProtectionOn)
        {
            // 优化前防护就不在位（或批次无安全类条目）：无需恢复
            return result;
        }

        var status = await QueryStatusAsync();
        var rtpNow = status?.RealTimeProtectionOn ?? report.InitialRealTimeProtectionOn;
        var tamperNow = status?.TamperProtectionOn ?? report.TamperProtectionOn;

        if (rtpNow && tamperNow)
        {
            // 防护状态与预检时一致（用户没动过，走了「仍然继续」）：除第三方杀软外不提醒
            result.Remind = report.ThirdPartyAvNames.Count > 0;
            return result;
        }

        if (!rtpNow)
        {
            if (report.DefenderOffTweakInBatch)
            {
                // 批次本身包含关闭 Defender 的条目：尊重条目意图，不做自动恢复，只提醒
                result.Remind = true;
                result.DefenderLeftOff = true;
                Log.Info("DefenderGuard：批次包含关闭 Defender 的条目，跳过实时保护自动恢复");
                return result;
            }

            var cfg = GetConfig();
            if (!string.IsNullOrWhiteSpace(cfg.EnableRealtimeCmd))
            {
                var (code, _, _) = await Task.Run(() => RunPowerShell(cfg.EnableRealtimeCmd));
                Log.Info($"DefenderGuard：已尝试恢复实时保护（exit={code}）");
            }

            result.Remind = true;
            return result;
        }

        // 实时保护仍在（只关了篡改防护）：提醒用户一并恢复
        result.Remind = true;
        return result;
    }

    private static string Describe(DefenderPreflightReport report) =>
        $"Defender={(report.DefenderPresent ? "在" : "无")}，实时保护={(report.InitialRealTimeProtectionOn ? "开" : "关")}，篡改防护={(report.TamperProtectionOn ? "开" : "关")}，" +
        "第三方杀软：" + (report.ThirdPartyAvNames.Count > 0 ? string.Join("、", report.ThirdPartyAvNames) : "无") +
        $"，命中条目 {report.SecurityItemNames.Count} 项（{string.Join("、", report.SecurityItemNames.Take(5))}{(report.SecurityItemNames.Count > 5 ? " …" : "")}）";

    private static string TrimDir(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    // ---------------- 进程调用 ----------------

    /// <summary>运行只读/恢复方向 PowerShell 命令（与 BxService.RunPowerShell 同构，但加 SilentlyContinue 前缀会掩盖命令失败，这里不加）。</summary>
    private static (int Code, string? StdOut, string? StdErr) RunPowerShell(string command)
    {
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
            p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)).GetAwaiter().GetResult();
            return (p.ExitCode, stdoutTask.GetAwaiter().GetResult(), null);
        }
        catch
        {
            return (-1, null, null);
        }
    }
}
