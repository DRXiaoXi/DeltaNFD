using System.Diagnostics;
using System.Management;
using System.Text.Json;
using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;

/// <summary>
/// 官方后端操作日志（T1 未知操作恢复闭环）：
/// - 写前执行意图：可能写入系统的命令在发送前先落盘 intent 记录，前端崩溃也能留下证据；
/// - 结果落定：已完成的静态优化保留原值备份但不锁住新操作；partial、临时恢复义务或根本没有结果（超时/断线）继续保留阻塞；
/// - 子进程关联：记录后端启动的子进程（安装器等），核对/解除阻塞必须确认它们也已结束；
/// - 人工核对：原后端确认退出后，玩家可「保留当前状态」（逐字归档并释放旧会话）或「按可信备份恢复」。
/// 超时、断线、退出都不等于还原成功；本类型绝不自动删除记录、自动恢复系统或重放旧请求。
/// </summary>
internal static class BackendUnconfirmedStore
{
    internal static Func<int, (IReadOnlyList<ChildProcess> Children, bool Ok)>? DescendantsForTests { get; set; }
    internal const string PhaseIntent = "intent";
    internal const string PhaseUnconfirmed = "unconfirmed";

    internal sealed record ChildProcess(string Name, int Pid, long StartTicks);

    private sealed record Evidence(
        string Module,
        string Phase = PhaseUnconfirmed,
        string? Command = null,
        string? RequestId = null,
        string? SessionId = null,
        int Pid = 0,
        long StartTicks = 0,
        string Path = "",
        DateTimeOffset CreatedUtc = default,
        string? Reason = null,
        string? ArgsDigest = null,
        IReadOnlyList<ChildProcess>? Children = null,
        bool ChildrenUnknown = false,
        IReadOnlyList<string>? CompletedItems = null);

    internal sealed record Review(string Module, string Fingerprint, string Description, bool CanAcknowledge)
    {
        public string Phase { get; init; } = PhaseUnconfirmed;
        public string Command { get; init; } = "";
        public string RequestId { get; init; } = "";
        public string SessionId { get; init; } = "";
        public string Reason { get; init; } = "";
        public bool ChildrenAlive { get; init; }
        public bool ChildrenUnknown { get; init; }
        public bool CanRestore { get; init; }
        public IReadOnlyList<string> ChildNames { get; init; } = [];
    }

    /// <summary>一次可能写入系统的命令的写前意图。</summary>
    internal sealed record OperationIntent(
        string Module, string Command, Guid RequestId, Guid SessionId, string ArgsDigest,
        int Pid, long StartTicks, string ImagePath);

    private static string RecordPath(string module) => Path.Combine(AppDataPaths.Root, "official-backend-unconfirmed", module + ".json");

    /// <summary>测试用：暴露记录路径，便于构造「路径被目录占用」等写入失败场景。</summary>
    internal static string RecordPathForTests(string module) => RecordPath(module);

    public static IReadOnlyList<string> PendingModules() => BackendModules.All.Keys
        .Where(m => File.Exists(RecordPath(m)) || Directory.Exists(RecordPath(m))).ToArray();

    /// <summary>仅异常/遗留证据用于主动提示；不把正常在途 intent 或成功临时备份当成报错。</summary>
    internal static string? AttentionFingerprint(string module)
    {
        var path = RecordPath(module);
        if (Directory.Exists(path)) return "invalid-directory";
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = ReadEvidence(path);
            var evidence = JsonSerializer.Deserialize<Evidence>(bytes);
            if (evidence is not null && evidence.Phase == PhaseIntent
                && evidence.Pid > 0 && evidence.StartTicks > 0 && IsProcessAlive(evidence.Pid, evidence.StartTicks)) return null;
            if (evidence is not null && evidence.Reason == BackendResultStatuses.Ok
                && evidence.Phase == PhaseUnconfirmed) return null;
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        }
        catch { return "unreadable-evidence"; }
    }

    /// <summary>
    /// 启动新后端前的阻塞检查：原后端进程仍在运行或身份无法确认时拒绝启动（避免出现重复执行者）；
    /// 若原后端已确认退出，则允许启动只读核对/恢复后端——新的写入操作仍会被 <see cref="BeginIntent"/> 拒绝，
    /// 直到玩家完成核对并归档记录。
    /// </summary>
    public static void Check(string module)
    {
        ArchiveCompletedStaticBackup(module);
        var path = RecordPath(module);
        if (Directory.Exists(path)) throw new IOException("后端未知记录路径不是文件。");
        if (!File.Exists(path)) return;
        var evidence = JsonSerializer.Deserialize<Evidence>(File.ReadAllText(path))
                       ?? throw new InvalidDataException("后端未知记录损坏。");
        if (evidence.Pid <= 0 || evidence.StartTicks <= 0 ||
            IsProcessAlive(evidence.Pid, evidence.StartTicks))
            throw new InvalidOperationException("该模块原后端仍在运行或身份无法确认，禁止启动新的执行者；请先人工核对：" + path);
    }

    /// <summary>该模块是否存在待核对记录，且原后端进程仍在运行或身份无法确认。</summary>
    public static bool HasLiveBackend(string module)
    {
        var path = RecordPath(module);
        if (!File.Exists(path)) return false;
        try
        {
            var evidence = JsonSerializer.Deserialize<Evidence>(File.ReadAllText(path));
            return evidence is null || evidence.Pid <= 0 || evidence.StartTicks <= 0 ||
                   IsProcessAlive(evidence.Pid, evidence.StartTicks);
        }
        catch { return true; }
    }

    /// <summary>写前落盘执行意图；已有记录时拒绝覆盖（保护既有证据）。</summary>
    public static void BeginIntent(OperationIntent intent)
    {
        ArchiveCompletedStaticBackup(intent.Module);
        var path = RecordPath(intent.Module);
        if (Directory.Exists(path)) throw new IOException("后端未知记录路径不是文件。");
        if (File.Exists(path))
            throw new InvalidOperationException("该模块存在待核对的记录，禁止执行新的写入操作：" + path);

        var evidence = new Evidence(
            intent.Module, PhaseIntent, intent.Command, intent.RequestId.ToString("D"), intent.SessionId.ToString("D"),
            intent.Pid, intent.StartTicks, intent.ImagePath, DateTimeOffset.UtcNow, null, intent.ArgsDigest);
        WriteNew(path, evidence);
    }

    /// <summary>
    /// 结果落定。仅当前记录确为本次 intent 时才处理（requestId 不匹配一律不动，保护他人证据）。
    /// 已成功静态优化 → 解除执行意图，原值备份不动；partial / 临时恢复义务 → 保留为 unconfirmed。
    /// </summary>
    public static void ResolveIntent(string module, Guid requestId, string status, bool pendingRestore,
        IReadOnlyList<string>? completedItems = null, bool requiresReview = true)
    {
        var path = RecordPath(module);
        var evidence = TryRead(path);
        if (evidence is null) return;
        if (!string.Equals(evidence.Phase, PhaseIntent, StringComparison.Ordinal)) return;
        if (!string.Equals(evidence.RequestId, requestId.ToString("D"), StringComparison.OrdinalIgnoreCase)) return;

        var retainedBackup = status == BackendResultStatuses.Ok && BackendModules.RetainsStaticBackup(module, evidence.Command ?? "");
        // 只有"结果未知/有恢复义务"才保留阻塞。requiresReview=false 的 partial 表示逐项结果全部已知，
        // 必须正常落定，否则一次拒绝/空操作就会锁死整个模块并阻止退出。
        var keep = (!retainedBackup && pendingRestore) || (status is BackendResultStatuses.Partial && requiresReview);
        if (!keep)
        {
            File.Delete(path);
            if (retainedBackup)
                Log.Info($"官方后端静态优化已确认完成：module={module}；command={evidence.Command}；request={requestId}；仅解除执行意图，原值备份保留。");
            else if (status is BackendResultStatuses.Partial)
                Log.Info($"官方后端已知部分完成已落定，不建立未确认记录：module={module}；command={evidence.Command}；request={requestId}；"
                    + (completedItems is { Count: > 0 } ? string.Join(",", completedItems.Take(5)) : "无逐项明细"));
            return;
        }

        var updated = evidence with
        {
            Phase = PhaseUnconfirmed,
            Reason = status,
            CompletedItems = completedItems ?? [],
        };
        WriteReplace(path, updated);
    }

    /// <summary>兼容旧版错误标记为待恢复的已成功静态优化；逐字归档，不改系统或原值备份。</summary>
    internal static bool ArchiveCompletedStaticBackup(string module)
    {
        var path = RecordPath(module);
        if (!File.Exists(path)) return false;
        try
        {
            var bytes = ReadEvidence(path);
            var evidence = JsonSerializer.Deserialize<Evidence>(bytes);
            if (evidence is null || evidence.Module != module || evidence.Phase != PhaseUnconfirmed
                || evidence.Reason != BackendResultStatuses.Ok
                || !BackendModules.RetainsStaticBackup(module, evidence.Command ?? "")
                || !Guid.TryParse(evidence.RequestId, out var request) || request == Guid.Empty
                || !Guid.TryParse(evidence.SessionId, out var session) || session == Guid.Empty
                || evidence.Pid <= 0 || evidence.StartTicks <= 0 || !Path.IsPathFullyQualified(evidence.Path)
                || !Path.GetFileName(evidence.Path).Equals(BackendModules.Require(module).ExecutableName, StringComparison.OrdinalIgnoreCase)
                || evidence.ChildrenUnknown || evidence.Children?.Count > 0
                || evidence.CompletedItems?.Any(i => !i.EndsWith(":ok", StringComparison.Ordinal)
                    && !i.EndsWith(":skipped", StringComparison.Ordinal)) == true)
                return false;
            var directory = Path.Combine(AppDataPaths.Root, "official-backend-reviewed");
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("归档目录含重解析点。");
            var prefix = Path.Combine(directory, module + "-completed-static-backup-" + Guid.NewGuid().ToString("N"));
            // Write the annotation before moving evidence: a write failure must leave the blocker intact.
            File.WriteAllText(prefix + ".outcome.json", JsonSerializer.Serialize(new
            {
                Module = module, Command = evidence.Command, Outcome = "completed-settings-retained",
                Message = "旧记录明确返回成功，静态设置和原值备份保留；不是还原或重放操作。",
            }));
            if (!ReadEvidence(path).SequenceEqual(bytes)) throw new IOException("静态优化记录已变化，保留待核对状态。");
            var archived = prefix + ".json";
            File.Move(path, archived);
            Log.Info($"官方后端：已归档旧版已成功静态优化记录；module={module}；command={evidence.Command}；request={request}；证据={archived}；原值备份未修改。");
            return true;
        }
        catch (Exception ex) { Log.Warn("官方后端静态优化旧记录无法安全归档，继续保留阻塞：" + module + "；" + ex.Message); return false; }
    }

    internal static bool ArchiveConfirmedRestore(string module, Guid requestId, Guid sessionId, BackendResult result)
    {
        var path = RecordPath(module);
        if (!File.Exists(path)) return false;
        var bytes = ReadEvidence(path);
        var e = JsonSerializer.Deserialize<Evidence>(bytes);
        if (module != BackendModules.CpuFrame || e?.Module != module || e.Command != BackendCommands.CpuFrameRestore
            || e.RequestId != requestId.ToString("D") || e.SessionId != sessionId.ToString("D")
            || result.Status != BackendResultStatuses.Ok || result.PendingRestore || result.Data is not { } data
            || !BackendJson.Deserialize<OperationResult>(data).Success) return false;
        if (e.ChildrenUnknown)
        {
            if (!RefreshChildren(module)) return false;
            bytes = ReadEvidence(path);
            e = JsonSerializer.Deserialize<Evidence>(bytes)!;
            if (e.RequestId != requestId.ToString("D") || e.SessionId != sessionId.ToString("D")) return false;
        }
        if (e.ChildrenUnknown || ChildrenAlive(e.Children)) return false;
        Archive(module, bytes, "late-restore-confirmed", new { Outcome = "restored", RequestId = requestId, Result = result });
        return true;
    }

    internal static bool ArchiveDefenderCleanup(Guid sessionId)
    {
        var path = RecordPath(BackendModules.System);
        if (!File.Exists(path)) return false;
        var bytes = ReadEvidence(path);
        var e = JsonSerializer.Deserialize<Evidence>(bytes);
        if (e?.Module != BackendModules.System || e.SessionId != sessionId.ToString("D")
            || e.Command is not (BackendCommands.SystemDefenderExclusionsAdd or BackendCommands.SystemDefenderPostApply)
            || e.ChildrenUnknown || ChildrenAlive(e.Children)) return false;
        Archive(BackendModules.System, bytes, "defender-cleanup-confirmed", new { Outcome = "restored", SessionId = sessionId });
        return true;
    }

    /// <summary>重新查询失败的子进程枚举；保留已知子进程，并以新指纹要求重新核对。</summary>
    internal static bool RefreshChildren(string module)
    {
        var path = RecordPath(module);
        var bytes = ReadEvidence(path);
        var e = JsonSerializer.Deserialize<Evidence>(bytes) ?? throw new InvalidDataException("记录为空。");
        if (e.Module != module || e.Pid <= 0 || e.StartTicks <= 0) return false;
        bool Unavailable()
        {
            if (!e.ChildrenUnknown)
            {
                if (!ReadEvidence(path).SequenceEqual(bytes)) throw new IOException("记录已变化，请重新核对。");
                WriteReplace(path, e with { ChildrenUnknown = true });
            }
            return false;
        }
        var (children, ok) = CaptureDescendants(e.Pid);
        if (!ok || children.Any(c => c.StartTicks <= 0)) return Unavailable();
        var known = new List<ChildProcess>();
        foreach (var child in e.Children ?? [])
        {
            if (child.StartTicks > 0) { known.Add(child); continue; }
            var observed = children.FirstOrDefault(c => c.Pid == child.Pid && c.Name == child.Name && c.StartTicks >= e.StartTicks);
            if (observed is not null) { known.Add(observed); continue; }
            // An incomplete identity must remain blocked while that PID exists. Once it is gone,
            // the original failed-query evidence is archived and the refreshed list can omit it.
            try { using var process = Process.GetProcessById(child.Pid); if (!process.HasExited) return Unavailable(); }
            catch (ArgumentException) { }
            catch { return Unavailable(); }
        }
        var combined = known.Concat(children.Where(c => c.StartTicks >= e.StartTicks))
            .DistinctBy(c => (c.Pid, c.StartTicks)).ToArray();
        // Also re-query known descendants; installers can outlive their original parent.
        foreach (var child in known)
        {
            var (descendants, childOk) = CaptureDescendants(child.Pid);
            if (!childOk || child.StartTicks <= 0 || descendants.Any(c => c.StartTicks <= 0)) return Unavailable();
            combined = combined.Concat(descendants.Where(c => c.StartTicks >= child.StartTicks))
                .DistinctBy(c => (c.Pid, c.StartTicks)).ToArray();
        }
        if (!ReadEvidence(path).SequenceEqual(bytes)) throw new IOException("记录已变化，请重新核对。");
        if (!e.ChildrenUnknown && combined.SequenceEqual(e.Children ?? [])) return true;
        // Preserve the original failed query as audit evidence, without discarding the blocker.
        var directory = Path.Combine(AppDataPaths.Root, "official-backend-reviewed");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, module + "-children-recheck-" + Guid.NewGuid().ToString("N") + ".json"), bytes);
        WriteReplace(path, e with { Children = combined, ChildrenUnknown = false });
        return true;
    }

    /// <summary>把 intent 提升为 unconfirmed（超时/断线/取消未确认）。无进程身份时按传入信息补建。</summary>
    public static void PromoteIntent(string module, Guid requestId, string reason, int pid, long startTicks,
        string imagePath, string? command, IReadOnlyList<ChildProcess>? children, bool childrenUnknown, Guid? sessionId = null)
    {
        var path = RecordPath(module);
        var evidence = TryRead(path);
        if (evidence is not null &&
            string.Equals(evidence.Phase, PhaseIntent, StringComparison.Ordinal) &&
            !string.Equals(evidence.RequestId, requestId.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            // 记录已被其它请求替换：不覆盖，避免丢失证据。
            return;
        }

        evidence ??= new Evidence(module, PhaseIntent, command, requestId.ToString("D"), sessionId?.ToString("D"), pid, startTicks, imagePath, DateTimeOffset.UtcNow);
        var updated = evidence with
        {
            Phase = PhaseUnconfirmed,
            Reason = evidence.Phase == PhaseUnconfirmed ? evidence.Reason ?? reason : reason,
            Children = (evidence.Children ?? []).Concat(children ?? []).DistinctBy(c => (c.Pid, c.StartTicks)).ToArray(),
            ChildrenUnknown = childrenUnknown || evidence.ChildrenUnknown,
        };
        if (evidence.Phase == PhaseUnconfirmed && updated.Reason == evidence.Reason
            && updated.ChildrenUnknown == evidence.ChildrenUnknown
            && (updated.Children ?? []).SequenceEqual(evidence.Children ?? [])) return;
        WriteReplace(path, updated);
    }

    /// <summary>直接写入一条 unconfirmed 记录（供没有先写 intent 的兜底路径与测试使用）。</summary>
    public static void Save(string module, Process process, string? command = null)
    {
        var path = RecordPath(module);
        if (File.Exists(path)) return;
        long ticks = 0; string image = "";
        try { ticks = process.StartTime.ToUniversalTime().Ticks; image = process.MainModule?.FileName ?? ""; } catch { }
        WriteNew(path, new Evidence(module, PhaseUnconfirmed, command, null, null, process.Id, ticks, image, DateTimeOffset.UtcNow,
            "unknown"));
    }

    private static void WriteNew(string path, Evidence evidence)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(evidence));
        try
        {
            // 不覆盖既有记录：并发写入时先落盘者获胜，后到者丢弃临时文件。
            File.Move(temporary, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            File.Delete(temporary);
        }
    }

    private static void WriteReplace(string path, Evidence evidence)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(evidence));
        File.Move(temporary, path, overwrite: true);
    }

    private static Evidence? TryRead(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<Evidence>(File.ReadAllText(path)); }
        catch (JsonException) { throw new InvalidDataException("后端未知记录损坏。"); }
    }

    private static byte[] ReadEvidence(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("记录含重解析点。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > 1024 * 1024) throw new InvalidDataException("记录大小无效。");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); return bytes;
    }

    private static bool ConfirmExited(Evidence evidence)
    {
        if (evidence.Pid <= 0 || evidence.StartTicks <= 0 || !Path.IsPathFullyQualified(evidence.Path)) return false;
        if (IsProcessAlive(evidence.Pid, evidence.StartTicks)) return false;
        return !ChildrenAlive(evidence.Children);
    }

    /// <summary>true = 进程仍在运行或身份无法确认（PID 复用视为原进程已退出，但需要人工判断）。</summary>
    private static bool IsProcessAlive(int pid, long startTicks)
    {
        if (pid <= 0 || startTicks <= 0) return true;
        try
        {
            using var process = Process.GetProcessById(pid);
            // WMI CreationDate 只有微秒精度，Process.StartTime 为 100ns 精度；不能因末位差异误判子进程已退出。
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks / 10 == startTicks / 10;
        }
        catch (ArgumentException) { return false; }
        catch { return true; }
    }

    private static bool ChildrenAlive(IReadOnlyList<ChildProcess>? children) =>
        children is not null && children.Any(c => IsProcessAlive(c.Pid, c.StartTicks));

    private static string DescribeChildren(IReadOnlyList<ChildProcess>? children) =>
        children is null || children.Count == 0 ? "" : string.Join("、", children.Select(c => $"{c.Name}({c.Pid})"));

    public static IReadOnlyList<Review> ReviewAll()
    {
        var result = new List<Review>();
        foreach (var module in PendingModules())
        {
            try
            {
                var bytes = ReadEvidence(RecordPath(module));
                var e = JsonSerializer.Deserialize<Evidence>(bytes) ?? throw new InvalidDataException("记录为空。");
                if (e.Module != module) throw new InvalidDataException("模块身份不符。");
                var exited = ConfirmExited(e);
                var childrenAlive = ChildrenAlive(e.Children);
                var childNames = (e.Children ?? []).Select(c => $"{c.Name}({c.Pid})").ToArray();
                var lines = new List<string>
                {
                    $"模块:{module}",
                    $"阶段:{(e.Phase == PhaseIntent ? "执行意图（结果未知）" : "未确认操作")}",
                    $"命令:{(string.IsNullOrEmpty(e.Command) ? "旧记录未保存" : e.Command)}",
                    $"请求:{(string.IsNullOrEmpty(e.RequestId) ? "旧记录未保存" : e.RequestId)}",
                    $"PID:{e.Pid}",
                    $"程序:{e.Path}",
                    $"时间:{e.CreatedUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss}",
                };
                if (!string.IsNullOrEmpty(e.Reason)) lines.Add("原因:" + DescribeReason(e.Reason));
                if (childNames.Length > 0)
                    lines.Add($"子进程:{string.Join("、", childNames)}（{(childrenAlive ? "仍在运行" : "已退出")}）");
                if (e.ChildrenUnknown) lines.Add("子进程状态:未知（禁止解除阻塞）");
                if (e.CompletedItems is { Count: > 0 }) lines.Add($"已完成:{string.Join("；", e.CompletedItems.Take(5))}");
                lines.Add(exited
                    ? "原后端已确认退出；这不代表修改已还原。"
                    : "进程仍在运行或身份无法确认，禁止解除阻塞。");
                if (e.ChildrenUnknown) lines.Add("无法确认子进程（安装器）状态，继续保留阻塞。");

                result.Add(new Review(module, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
                    string.Join("\n", lines), exited && !e.ChildrenUnknown)
                {
                    Phase = e.Phase == PhaseIntent ? PhaseIntent : PhaseUnconfirmed,
                    Command = e.Command ?? "",
                    RequestId = e.RequestId ?? "",
                    SessionId = e.SessionId ?? "",
                    Reason = e.Reason ?? "",
                    ChildrenAlive = childrenAlive,
                    ChildrenUnknown = e.ChildrenUnknown,
                    // 可按可信备份恢复：仅当原后端与子进程均已退出、记录未被判定为损坏时提供（ACE 等无可信备份的模块由后端返回不可用）。
                    CanRestore = exited && !e.ChildrenUnknown && !childrenAlive,
                    ChildNames = childNames,
                });
            }
            catch (Exception ex) { result.Add(new Review(module, "", "记录无法核对，证据保留：" + ex.Message, false)); }
        }
        return result;
    }

    private static string DescribeReason(string reason) => reason switch
    {
        "timeout" => "执行超时，结果未知",
        "disconnected" => "连接中断，结果未知",
        "cancel-unconfirmed" => "取消未确认",
        "partial" => "部分完成",
        "failed" => "执行失败",
        "unknown" => "上次未记录结果",
        _ => reason,
    };

    /// <summary>玩家确认「保留当前状态」：逐字归档并返回归档路径。不还原系统、不重放请求、不删除原值备份。</summary>
    public static string AcknowledgeCurrentState(Review confirmed)
    {
        _ = BackendModules.Require(confirmed.Module);
        var source = RecordPath(confirmed.Module);
        var bytes = ReadEvidence(source);
        if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) != confirmed.Fingerprint)
            throw new IOException("记录已变化，请重新核对。");
        var e = JsonSerializer.Deserialize<Evidence>(bytes) ?? throw new InvalidDataException("记录为空。");
        if (e.Module != confirmed.Module || !ConfirmExited(e)) throw new IOException("原后端未确认退出，不能解除阻塞。");
        if (e.ChildrenUnknown) throw new IOException("无法确认子进程状态，不能解除阻塞。");
        return Archive(confirmed.Module, bytes, "kept-current-state");
    }

    /// <summary>恢复成功后归档原始记录，并写入复核证据（恢复动作、复核快照、结果）。</summary>
    public static string ArchiveAfterRecovery(Review confirmed, string scope, bool verified, string message, ReconcileSnapshot? snapshot)
    {
        _ = BackendModules.Require(confirmed.Module);
        var source = RecordPath(confirmed.Module);
        var bytes = ReadEvidence(source);
        if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) != confirmed.Fingerprint)
            throw new IOException("记录已变化，请重新核对。");
        if (!verified) throw new IOException("恢复未通过复核，保留阻塞与原始证据。");
        var outcome = new
        {
            Module = confirmed.Module,
            Outcome = confirmed.Module == BackendModules.Runtime && scope == "repair-review" ? "installation-reviewed" : "restored",
            Scope = scope,
            Message = message,
            VerifiedUtc = DateTimeOffset.UtcNow,
            Snapshot = snapshot,
        };
        var evidence = JsonSerializer.Deserialize<Evidence>(bytes) ?? throw new InvalidDataException("记录为空。");
        if (!ConfirmExited(evidence) || evidence.ChildrenUnknown) throw new IOException("原后端或子进程未确认退出，不能归档恢复结果。");
        return Archive(confirmed.Module, bytes, confirmed.Module == BackendModules.Runtime && scope == "repair-review" ? "installation-reviewed" : "restored", outcome);
    }

    private static string Archive(string module, byte[] original, string outcomeName, object? outcome = null)
    {
        var directory = Path.Combine(AppDataPaths.Root, "official-backend-reviewed");
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("归档目录含重解析点。");
        var stamp = Guid.NewGuid().ToString("N");
        var destination = Path.Combine(directory, $"{module}-{outcomeName}-{stamp}.json");
        if (outcome is not null)
            File.WriteAllText(Path.Combine(directory, $"{module}-{outcomeName}-{stamp}.outcome.json"), JsonSerializer.Serialize(outcome));
        if (!ReadEvidence(RecordPath(module)).SequenceEqual(original)) throw new IOException("记录已变化，请重新核对。");
        File.Move(RecordPath(module), destination);
        if (!ReadEvidence(destination).SequenceEqual(original)) throw new IOException("归档证据已变化，不能宣称核对完成。");
        return destination;
    }

    /// <summary>枚举某进程的全部子孙进程（含父进程已退出后仍挂在原 PPID 下的孤立安装器）。失败时 ok=false。</summary>
    public static (IReadOnlyList<ChildProcess> Children, bool Ok) CaptureDescendants(int rootPid)
    {
        if (DescendantsForTests is { } capture) return capture(rootPid);
        var children = new List<ChildProcess>();
        try
        {
            var frontier = new Queue<int>();
            frontier.Enqueue(rootPid);
            var seen = new HashSet<int> { rootPid };
            while (frontier.Count > 0)
            {
                var parent = frontier.Dequeue();
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT ProcessId, Name, ParentProcessId, CreationDate FROM Win32_Process WHERE ParentProcessId={parent}");
                foreach (var item in searcher.Get())
                {
                    using var record = (ManagementBaseObject)item;
                    var pid = Convert.ToInt32(record["ProcessId"]);
                    if (!seen.Add(pid)) continue;
                    var name = Convert.ToString(record["Name"]) ?? "";
                    children.Add(new ChildProcess(name, pid, TryParseCreationTicks(Convert.ToString(record["CreationDate"]))));
                    frontier.Enqueue(pid);
                }
            }
            return (children, children.All(c => c.StartTicks > 0));
        }
        catch
        {
            return (children, false);
        }
    }

    private static long TryParseCreationTicks(string? wmiDate)
    {
        try { return ManagementDateTimeConverter.ToDateTime(wmiDate ?? "").ToUniversalTime().Ticks; }
        catch { return 0; }
    }
}
