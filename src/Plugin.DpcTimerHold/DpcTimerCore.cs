using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Plugin.DpcTimerHold;

/// <summary>
/// 「DPC 计时器常驻（Win10 研究）」插件本体逻辑（纯 BCL，可在宿主冒烟测试中直接链接验证）。
///
/// 设计边界（与包内 README 一致）：
///   * 插件不包含、不提取、不直接加载任何驱动；只启动/关闭玩家自备的原版 DPC 1.4.0 EXE。
///   * 原版 DPC 在对话框初始化（WM_INITDIALOG）时自行创建并按需启动 dpclat_driver 内核服务，
///     调用 ExSetTimerResolution(10000, TRUE) 持有 1.0000ms 的系统计时器精度请求，退出时释放并删除服务。
///   * 本插件只做三件事：校验玩家文件身份、启动/关闭该进程、只读核验服务状态与系统分辨率读数。
///   * 不修改注册表/BCD/证书/启动配置，不结束非本插件启动的进程，不删除归属不明的服务。
/// </summary>
internal static class DpcRules
{
    public const string PluginId = "io.github.drxiaoxi.dpc-timer-hold";
    public const string PluginVersion = "1.0.0";

    /// <summary>原版 DPC Latency Checker 1.4.0 EXE 的精确字节数与 SHA256（只读身份核对，不分发文件）。</summary>
    public const int ExeLength = 306928;
    public const string ExeSha256 = "00a7c7b88877bf59dbaa70de734fcd3f287f1eb92caff4571a4c8e67a5dc0aca";

    /// <summary>原版 DPC 安装的内核服务名与驱动文件名（来自静态逆向的固定字符串）。</summary>
    public const string ServiceName = "dpclat_driver";
    public const string DriverFileName = "dpclat.sys";

    /// <summary>DPC GUI 在 WM_INITDIALOG 中固定提交 1000 微秒（=10000×100ns=1.0000ms）。</summary>
    public const uint RequestedMicroseconds = 1000;
    public const uint ExpectedCurrent100ns = 10000;

    public const string BackupPrefix = "dpctimer-";
    public const string JournalFileName = "hold-state.json";
    public const int MinWin10Build = 17763;
    public const int MaxWin10BuildExclusive = 22000;
    public const int HoldPollTimeoutMs = 15000;
    public const int GracefulCloseTimeoutMs = 10000;
    public const int ServiceCleanupTimeoutMs = 10000;
    public const int PollIntervalMs = 250;

    public static bool IsSupportedOs(int major, int build, bool is64) =>
        is64 && major == 10 && build >= MinWin10Build && build < MaxWin10BuildExclusive;

    public static string NewBackupId() => BackupPrefix + Guid.NewGuid().ToString("N");

    public static string Format100ns(uint value) =>
        (value / 10000.0).ToString("0.0000", CultureInfo.InvariantCulture) + "ms";

    public static string FormatMicroseconds(uint microseconds) =>
        (microseconds / 1000.0).ToString("0.0000", CultureInfo.InvariantCulture) + "ms";

    public static string Relation(uint current100ns, uint target100ns) =>
        current100ns == target100ns ? "等于目标" : current100ns < target100ns ? "比目标更细" : "比目标更粗";

    public static string ShortSha(string sha) => sha.Length <= 16 ? sha : sha[..16] + "…";

    /// <summary>规范化服务映像路径：去掉 Win32 对象前缀并统一分隔符；不做宽松的文件名匹配。</summary>
    public static string NormalizeImagePath(string path)
    {
        var text = (path ?? "").Trim();
        if (text.StartsWith(@"\??\", StringComparison.Ordinal)) text = text[4..];
        if (text.StartsWith(@"\\?\", StringComparison.Ordinal)) text = text[4..];
        return text.Replace('/', '\\');
    }
}

internal sealed record ProbeItem(string Id, string Status, string Message);

internal sealed record HoldOutcome(string Status, string Code, string Message,
    IReadOnlyList<ProbeItem> Items, bool PendingRestore, IReadOnlyList<string> BackupIds)
{
    public static HoldOutcome Simple(string status, string code, string message,
        bool pendingRestore = false, string? backupId = null) =>
        new(status, code, message, [], pendingRestore,
            backupId is null ? [] : [backupId]);
}

/// <summary>只读原版 DPC 文件解析与身份核验。</summary>
internal static class DpcInput
{
    public const string DefaultFileName = "DPCLatencyChecker_V1.4.exe";

    /// <summary>
    /// 解析玩家提供的路径；为空时回退到插件数据目录 input\ 下的固定文件名。
    /// 只接受本机绝对路径（盘符），拒绝 UNC、引号、NUL、相对路径与不存在的文件。
    /// </summary>
    public static string? Resolve(string? raw, string dataDirectory, out string error)
    {
        error = "";
        var candidate = (raw ?? "").Trim();
        if (candidate.Length > 0)
        {
            if (candidate.Length > 4096) { error = "路径超过 4096 字符上限。"; return null; }
            if (candidate.Contains('\0') || candidate.Contains('"') || candidate.Contains('|'))
            {
                error = "路径包含非法字符（NUL、引号或竖线）。";
                return null;
            }
            if (!Path.IsPathFullyQualified(candidate)) { error = "必须填写完整路径（含盘符）。"; return null; }
            var root = Path.GetPathRoot(candidate) ?? "";
            if (root.StartsWith(@"\\", StringComparison.Ordinal) || root.StartsWith("//", StringComparison.Ordinal))
            {
                error = "不支持网络路径（UNC）；请先把原版文件复制到本机磁盘。";
                return null;
            }
            if (!File.Exists(candidate)) { error = "文件不存在：" + candidate; return null; }
            return Path.GetFullPath(candidate);
        }

        foreach (var name in new[] { DefaultFileName, "dpclat-1.4.0.exe" })
        {
            var fallback = Path.Combine(dataDirectory, "input", name);
            if (File.Exists(fallback)) return Path.GetFullPath(fallback);
        }
        error = "未指定原版 DPC EXE 路径，且插件数据目录 input\\ 下没有 " + DefaultFileName +
                "。请填写路径，或把原版文件放到该目录。";
        return null;
    }

    internal sealed record Identity(bool Ok, long Length, string Sha256, string Error)
    {
        public static Identity Fail(string error) => new(false, 0, "", error);
    }

    /// <summary>纯函数身份核验，供测试直接驱动。</summary>
    public static Identity VerifyIdentity(long length, string sha256) =>
        length == DpcRules.ExeLength && string.Equals(sha256, DpcRules.ExeSha256, StringComparison.OrdinalIgnoreCase)
            ? new Identity(true, length, DpcRules.ExeSha256, "")
            : Identity.Fail($"不是原版 DPC 1.4.0：期望 {DpcRules.ExeLength} 字节、SHA256 {DpcRules.ShortSha(DpcRules.ExeSha256)}；" +
                            $"实际 {length} 字节、SHA256 {(sha256.Length == 0 ? "（未计算）" : DpcRules.ShortSha(sha256))}。");

    public static Identity VerifyFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.SequentialScan);
            var (length, sha) = Hash(stream);
            return VerifyIdentity(length, sha);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Identity.Fail("读取文件失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 以只读共享方式锁定文件并核验身份；返回的流在释放前阻止其他进程改写/删除该文件，
    /// 用于覆盖「核验后、启动前被替换」的窗口。调用方必须在启动完成后释放。
    /// </summary>
    public static FileStream? OpenLocked(string path, out Identity identity)
    {
        identity = Identity.Fail("未打开文件。");
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.SequentialScan);
            var (length, sha) = Hash(stream);
            identity = VerifyIdentity(length, sha);
            if (!identity.Ok) { stream.Dispose(); return null; }
            return stream;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            identity = Identity.Fail("读取文件失败：" + ex.Message);
            return null;
        }
    }

    private static (long Length, string Sha256) Hash(Stream stream)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            hash.AppendData(buffer, 0, read);
        }
        return (total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }
}

/// <summary>常驻记录（只保存在插件数据目录，不写系统目录）。</summary>
internal sealed class HoldJournal
{
    public int SchemaVersion { get; set; } = 1;
    public string Phase { get; set; } = HoldJournalStore.PhaseReleased;
    public string BackupId { get; set; } = "";
    public int Pid { get; set; }
    public long StartTicksUtc { get; set; }
    public string ExePath { get; set; } = "";
    public string ExeSha256 { get; set; } = "";
    public string ServiceImagePath { get; set; } = "";
    public uint RequestedMicroseconds { get; set; }
    public uint Current100nsAtStart { get; set; }
    public string CreatedUtc { get; set; } = "";
    public string UpdatedUtc { get; set; } = "";
    public string Detail { get; set; } = "";
}

internal static class HoldJournalStore
{
    public const string PhaseReleased = "released";
    public const string PhaseHolding = "holding";
    public const string PhaseUnknown = "unknown";

    public static string JournalPath(string dataDirectory) =>
        Path.Combine(dataDirectory, DpcRules.JournalFileName);

    /// <summary>读取记录；不存在返回 null 且无错误；损坏/字段非法返回 null 并给出错误。</summary>
    public static HoldJournal? Load(string dataDirectory, out string error)
    {
        error = "";
        var path = JournalPath(dataDirectory);
        try
        {
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > 1024 * 1024) { error = "常驻记录大小异常。"; return null; }
            var bytes = File.ReadAllBytes(path);
            var journal = JsonSerializer.Deserialize<HoldJournal>(bytes, JsonOptions);
            if (journal is null) { error = "常驻记录无法解析。"; return null; }
            if (journal.SchemaVersion != 1) { error = "常驻记录 schemaVersion 不支持。"; return null; }
            if (journal.Phase is not (PhaseReleased or PhaseHolding or PhaseUnknown))
            {
                error = "常驻记录 phase 非法。";
                return null;
            }
            if (journal.Phase == PhaseHolding &&
                (journal.Pid <= 0 || journal.StartTicksUtc <= 0 || journal.ExePath.Length == 0))
            {
                error = "常驻记录缺少进程身份。";
                return null;
            }
            if (journal.BackupId.Length > 128 || journal.ExePath.Length > 4096 ||
                journal.ServiceImagePath.Length > 4096 || journal.Detail.Length > 4096)
            {
                error = "常驻记录字段超长。";
                return null;
            }
            return journal;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = "读取常驻记录失败：" + ex.Message;
            return null;
        }
    }

    public static bool Save(string dataDirectory, HoldJournal journal, out string error)
    {
        error = "";
        var path = JournalPath(dataDirectory);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(dataDirectory);
            journal.SchemaVersion = 1;
            journal.UpdatedUtc = DateTimeOffset.UtcNow.ToString("O");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       64 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = "写入常驻记录失败：" + ex.Message;
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return false;
        }
    }

    public static void Delete(string dataDirectory)
    {
        try
        {
            var path = JournalPath(dataDirectory);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16,
    };
}

// ---------------------------------------------------------------- 可替换的环境接口（测试注入假实现）

internal sealed record OwnedProcess(int Pid, long StartTicksUtc, string ExePath);

internal enum DpcServiceState : uint
{
    Unknown = 0,
    Stopped = 1,
    StartPending = 2,
    StopPending = 3,
    Running = 4,
    ContinuePending = 5,
    PausePending = 6,
    Paused = 7,
}

internal sealed record ServiceInfo(bool Exists, uint State, uint Type, uint StartType, string ImagePath, string Error)
{
    public DpcServiceState RunState => Exists ? (DpcServiceState)State : DpcServiceState.Unknown;

    public string StateText => !Exists ? "不存在" : RunState switch
    {
        DpcServiceState.Stopped => "STOPPED",
        DpcServiceState.StartPending => "START_PENDING",
        DpcServiceState.StopPending => "STOP_PENDING",
        DpcServiceState.Running => "RUNNING",
        DpcServiceState.ContinuePending => "CONTINUE_PENDING",
        DpcServiceState.PausePending => "PAUSE_PENDING",
        DpcServiceState.Paused => "PAUSED",
        _ => "未知(" + State + ")",
    };
}

internal interface IClock
{
    DateTimeOffset UtcNow { get; }
    void Delay(int milliseconds);
}

internal interface IEnvironmentInfo
{
    int OsMajor { get; }
    int OsBuild { get; }
    bool Is64BitProcess { get; }
    bool IsAdmin { get; }
    string SecureBootState { get; }
}

internal interface IInputApi
{
    string? Resolve(string? raw, string dataDirectory, out string error);
    DpcInput.Identity VerifyFile(string path);
    FileStream? OpenLocked(string path, out DpcInput.Identity identity);
}

internal sealed class RealInputApi : IInputApi
{
    public string? Resolve(string? raw, string dataDirectory, out string error) =>
        DpcInput.Resolve(raw, dataDirectory, out error);
    public DpcInput.Identity VerifyFile(string path) => DpcInput.VerifyFile(path);
    public FileStream? OpenLocked(string path, out DpcInput.Identity identity) =>
        DpcInput.OpenLocked(path, out identity);
}

internal interface IProcessApi
{
    OwnedProcess Launch(string exePath);
    bool IsAlive(OwnedProcess owned);
    bool CloseGracefully(OwnedProcess owned, int timeoutMs);
}

internal interface IServiceApi
{
    ServiceInfo Query(string name);
    bool StopAndDelete(ServiceInfo expected, out string detail);
}

internal interface ITimerApi
{
    uint? TryCurrent100ns(out string error);
}

// ---------------------------------------------------------------- 控制器

internal sealed class DpcHoldController(IEnvironmentInfo environment, IProcessApi process,
    IServiceApi service, ITimerApi timer, IClock clock, IInputApi input)
{
    private readonly IEnvironmentInfo _env = environment;
    private readonly IProcessApi _process = process;
    private readonly IServiceApi _service = service;
    private readonly ITimerApi _timer = timer;
    private readonly IClock _clock = clock;
    private readonly IInputApi _input = input;

    // ------------------------------------------------------------ 只读检查

    public HoldOutcome Probe(string? inputPath, string dataDirectory)
    {
        var items = new List<ProbeItem>();
        CollectEnvironment(items);
        CollectInput(items, inputPath, dataDirectory);
        var loaded = HoldJournalStore.Load(dataDirectory, out var journalError);
        CollectJournal(items, loaded, journalError);
        var svc = _service.Query(DpcRules.ServiceName);
        CollectService(items, svc);
        CollectTimer(items);
        return Summarize(items, "环境与状态检查完成", "ENVIRONMENT_BLOCKED");
    }

    public HoldOutcome DescribeStatus(string dataDirectory)
    {
        var items = new List<ProbeItem>();
        var loaded = HoldJournalStore.Load(dataDirectory, out var journalError);
        CollectJournal(items, loaded, journalError);
        CollectService(items, _service.Query(DpcRules.ServiceName));
        CollectTimer(items);
        return Summarize(items, "状态读取完成", "STATUS_UNKNOWN");
    }

    // ------------------------------------------------------------ 启动常驻

    public HoldOutcome Start(string? inputPath, bool riskAccepted, string dataDirectory,
        CancellationToken cancellationToken, Action<int, string>? progress = null)
    {
        var backupId = DpcRules.NewBackupId();
        var items = new List<ProbeItem>();
        void Step(int percent, string message) => progress?.Invoke(percent, message);

        Step(5, "检查运行环境");
        if (!riskAccepted)
            return Fail(backupId, items, "RISK_NOT_ACCEPTED",
                "需要先勾选风险确认：本插件会启动玩家自备的第三方 DPC，其签名驱动将加载内核并持有系统计时器请求。");
        if (!DpcRules.IsSupportedOs(_env.OsMajor, _env.OsBuild, _env.Is64BitProcess))
            return Fail(backupId, items, "UNSUPPORTED_OS",
                $"仅支持 Windows 10 x64 1809（17763）至 21H2（21999）；当前系统 {_env.OsMajor}.{_env.OsBuild}、x64={_env.Is64BitProcess}。" +
                "Win11 已有宿主内置钟摆路径，本插件不在 Win11 上运行。");
        if (!_env.IsAdmin)
            return Fail(backupId, items, "UNAUTHORIZED",
                "需要管理员权限：宿主本身要求管理员，插件后端应继承该权限；当前令牌不是管理员。");
        items.Add(new ProbeItem("os", "success",
            $"Windows 10 x64 build {_env.OsBuild}；Secure Boot：{_env.SecureBootState}。"));
        items.Add(new ProbeItem("admin", "success", "当前进程具有管理员权限。"));

        Step(15, "校验玩家自备的原版 DPC 文件");
        var resolved = _input.Resolve(inputPath, dataDirectory, out var resolveError);
        if (resolved is null)
            return Fail(backupId, items, "INPUT_MISSING", resolveError);
        var identity = _input.VerifyFile(resolved);
        if (!identity.Ok)
            return Fail(backupId, items, "INPUT_MISMATCH", identity.Error);
        items.Add(new ProbeItem("input", "success",
            $"原版 DPC 身份匹配：{resolved}（{identity.Length} 字节，SHA256 {DpcRules.ShortSha(identity.Sha256)}）。"));

        Step(25, "检查常驻记录与服务状态");
        var loaded = HoldJournalStore.Load(dataDirectory, out var journalError);
        if (journalError.Length > 0)
        {
            items.Add(new ProbeItem("journal", "partial", journalError));
            var existing = _service.Query(DpcRules.ServiceName);
            if (existing.Exists)
                return Fail(backupId, items, "JOURNAL_UNREADABLE",
                    "常驻记录不可读且检测到 dpclat_driver 服务；无法确认归属，拒绝启动。请手动关闭 DPC 并清理记录后重试。",
                    pendingRestore: true);
            HoldJournalStore.Delete(dataDirectory);
        }
        else if (loaded is not null && loaded.Phase == HoldJournalStore.PhaseHolding)
        {
            var owned = new OwnedProcess(loaded.Pid, loaded.StartTicksUtc, loaded.ExePath);
            if (_process.IsAlive(owned))
                return Fail(backupId, items, "ALREADY_HOLDING",
                    "本插件启动的 DPC 仍在常驻。请先在插件管理页点『恢复备份…』释放，再重新启动。",
                    pendingRestore: true);
            var leftover = _service.Query(DpcRules.ServiceName);
            if (leftover.Exists)
                return Fail(backupId, items, "LEFTOVER_SERVICE",
                    $"上次常驻的 DPC 进程已退出，但 dpclat_driver 服务仍存在（{leftover.StateText}）。" +
                    "请先点『恢复备份…』尝试清理；若服务归属不明，请手动关闭 DPC 后处理。",
                    pendingRestore: true);
            HoldJournalStore.Delete(dataDirectory);
        }

        var svc = _service.Query(DpcRules.ServiceName);
        if (svc.Error.Length > 0)
            return Fail(backupId, items, "SERVICE_QUERY_FAILED",
                "无法读取 dpclat_driver 服务状态：" + svc.Error + "；为避免与未知驱动冲突，拒绝启动。");
        if (svc.Exists)
            return Fail(backupId, items, "SERVICE_CONFLICT",
                $"已存在 {DpcRules.ServiceName} 服务（{svc.StateText}，类型 {svc.Type}，启动类型 {svc.StartType}）。" +
                "可能由其他 DPC 实例或上次异常退出遗留；本插件不会改动归属不明的服务，请先关闭 DPC 或手动清理后重试。",
                pendingRestore: svc.RunState != DpcServiceState.Stopped);

        Step(35, "启动玩家自备的原版 DPC");
        FileStream? locked = null;
        OwnedProcess? ownedProcess = null;
        try
        {
            locked = _input.OpenLocked(resolved, out var lockedIdentity);
            if (locked is null)
                return Fail(backupId, items, "INPUT_CHANGED", lockedIdentity.Error);
            ownedProcess = _process.Launch(resolved);
            items.Add(new ProbeItem("process", "success",
                $"已启动 DPC 进程 PID {ownedProcess.Pid}（启动时间 ticks {ownedProcess.StartTicksUtc}）。"));

            Step(55, "等待 DPC 驱动加载并持有内核计时器请求");
            var hold = WaitForHold(ownedProcess, cancellationToken);
            if (!hold.Ok)
            {
                var closed = TryClose(ownedProcess);
                var after = _service.Query(DpcRules.ServiceName);
                var phase = closed && !after.Exists ? HoldJournalStore.PhaseReleased : HoldJournalStore.PhaseUnknown;
                SaveJournal(dataDirectory, new HoldJournal
                {
                    Phase = phase,
                    BackupId = backupId,
                    Pid = ownedProcess.Pid,
                    StartTicksUtc = ownedProcess.StartTicksUtc,
                    ExePath = ownedProcess.ExePath,
                    ExeSha256 = identity.Sha256,
                    ServiceImagePath = after.ImagePath,
                    RequestedMicroseconds = DpcRules.RequestedMicroseconds,
                    Detail = hold.Code + "：" + hold.Message,
                    CreatedUtc = NowText(),
                });
                items.Add(new ProbeItem("hold", "failed", hold.Message));
                return new HoldOutcome("failed", hold.Code,
                    hold.Message + (closed ? " 已尝试正常关闭本次启动的 DPC。" : " 未能确认 DPC 已退出，请查看其窗口。"),
                    items, PendingRestore: !closed || after.Exists, BackupIds: [backupId]);
            }

            Step(85, "写入常驻记录");
            var journal = new HoldJournal
            {
                Phase = HoldJournalStore.PhaseHolding,
                BackupId = backupId,
                Pid = ownedProcess.Pid,
                StartTicksUtc = ownedProcess.StartTicksUtc,
                ExePath = ownedProcess.ExePath,
                ExeSha256 = identity.Sha256,
                ServiceImagePath = hold.Service.ImagePath,
                RequestedMicroseconds = DpcRules.RequestedMicroseconds,
                Current100nsAtStart = hold.Current100ns,
                CreatedUtc = NowText(),
                Detail = "DPC 常驻中；由插件管理页恢复备份释放。",
            };
            if (!SaveJournal(dataDirectory, journal))
            {
                var closed = TryClose(ownedProcess);
                var after = _service.Query(DpcRules.ServiceName);
                items.Add(new ProbeItem("journal", "failed", "常驻记录写入失败，已尝试释放。"));
                return new HoldOutcome("failed", "RESULT_UNKNOWN",
                    "常驻记录写入失败，无法保证重启后仍能释放；已尝试正常关闭本次启动的 DPC。" +
                    (closed ? "" : " DPC 可能仍在运行，请手动关闭。"),
                    items, PendingRestore: !closed || after.Exists, BackupIds: [backupId]);
            }

            items.Add(new ProbeItem("service", "success",
                $"{DpcRules.ServiceName} 已 RUNNING；映像 {hold.Service.ImagePath}。"));
            items.Add(new ProbeItem("resolution", "success",
                $"DPC 请求 {DpcRules.FormatMicroseconds(DpcRules.RequestedMicroseconds)}（{DpcRules.RequestedMicroseconds} 微秒）；" +
                $"系统当前读数 {DpcRules.Format100ns(hold.Current100ns)}（{DpcRules.Relation(hold.Current100ns, DpcRules.ExpectedCurrent100ns)}）。" +
                "该读数不等于跨进程 Sleep、帧时间或游戏收益已验证。"));
            items.Add(new ProbeItem("release", "success",
                "释放：在插件管理页点『恢复备份…』，插件会正常关闭本次启动的 DPC 并核验服务清理；" +
                "也可直接关闭 DPC 窗口后点恢复清除记录。宿主退出不会自动关闭 DPC。"));
            Step(100, "常驻已建立");
            return new HoldOutcome("success", "OK",
                "DPC 已启动并确认持有内核计时器请求；常驻期间请保持 DPC 窗口运行。",
                items, PendingRestore: true, BackupIds: [backupId]);
        }
        catch (OperationCanceledException)
        {
            var closed = ownedProcess is not null && TryClose(ownedProcess);
            var after = _service.Query(DpcRules.ServiceName);
            if (ownedProcess is not null)
                SaveJournal(dataDirectory, new HoldJournal
                {
                    Phase = closed && !after.Exists ? HoldJournalStore.PhaseReleased : HoldJournalStore.PhaseUnknown,
                    BackupId = backupId,
                    Pid = ownedProcess.Pid,
                    StartTicksUtc = ownedProcess.StartTicksUtc,
                    ExePath = ownedProcess.ExePath,
                    ExeSha256 = identity.Sha256,
                    ServiceImagePath = after.ImagePath,
                    RequestedMicroseconds = DpcRules.RequestedMicroseconds,
                    Detail = "启动被取消",
                    CreatedUtc = NowText(),
                });
            items.Add(new ProbeItem("hold", "partial", "启动被取消，已尝试释放本次启动的 DPC。"));
            return new HoldOutcome("cancelled", "CANCELLED",
                "启动被取消；" + (closed ? "已尝试正常关闭本次启动的 DPC。" : "DPC 可能仍在运行，请手动关闭。"),
                items, PendingRestore: !closed || after.Exists, BackupIds: [backupId]);
        }
        catch (Exception ex)
        {
            var closed = ownedProcess is not null && TryClose(ownedProcess);
            var after = _service.Query(DpcRules.ServiceName);
            if (ownedProcess is not null)
                SaveJournal(dataDirectory, new HoldJournal
                {
                    Phase = closed && !after.Exists ? HoldJournalStore.PhaseReleased : HoldJournalStore.PhaseUnknown,
                    BackupId = backupId,
                    Pid = ownedProcess.Pid,
                    StartTicksUtc = ownedProcess.StartTicksUtc,
                    ExePath = ownedProcess.ExePath,
                    ExeSha256 = identity.Sha256,
                    ServiceImagePath = after.ImagePath,
                    RequestedMicroseconds = DpcRules.RequestedMicroseconds,
                    Detail = "启动异常：" + ex.GetType().Name,
                    CreatedUtc = NowText(),
                });
            items.Add(new ProbeItem("hold", "failed", "启动异常：" + ex.GetType().Name + " —— " + ex.Message));
            return new HoldOutcome("failed", "RESULT_UNKNOWN",
                "启动过程异常，结果未知：" + ex.GetType().Name + " —— " + ex.Message +
                (closed ? " 已尝试正常关闭本次启动的 DPC。" : " DPC 可能仍在运行，请手动关闭。"),
                items, PendingRestore: !closed || after.Exists, BackupIds: [backupId]);
        }
        finally
        {
            locked?.Dispose();
        }
    }

    // ------------------------------------------------------------ 释放 / 恢复

    public HoldOutcome Restore(string backupId, string dataDirectory,
        CancellationToken cancellationToken, Action<int, string>? progress = null)
    {
        var items = new List<ProbeItem>();
        void Step(int percent, string message) => progress?.Invoke(percent, message);

        if (string.IsNullOrEmpty(backupId) || !backupId.StartsWith(DpcRules.BackupPrefix, StringComparison.Ordinal) ||
            backupId.Length > 128)
            return new HoldOutcome("failed", "RESTORE_FAILED",
                "不是本插件产生的恢复标识，不能代它释放。", items, false, []);

        var loaded = HoldJournalStore.Load(dataDirectory, out var journalError);
        if (journalError.Length > 0)
        {
            items.Add(new ProbeItem("journal", "failed", journalError));
            var existing = _service.Query(DpcRules.ServiceName);
            if (existing.Exists)
                return new HoldOutcome("failed", "RESTORE_FAILED",
                    "常驻记录损坏且检测到 dpclat_driver 服务，无法确认归属；请手动关闭 DPC 并处理服务。",
                    items, true, [backupId]);
            HoldJournalStore.Delete(dataDirectory);
            return new HoldOutcome("success", "OK",
                "常驻记录损坏但未检测到 dpclat_driver 服务；按已释放处理并清除记录。", items, false, [backupId]);
        }

        if (loaded is null || loaded.Phase == HoldJournalStore.PhaseReleased)
        {
            var existing = _service.Query(DpcRules.ServiceName);
            if (existing.Error.Length > 0)
            {
                items.Add(new ProbeItem("service", "failed", "读取服务状态失败：" + existing.Error));
                return new HoldOutcome("failed", "RESTORE_FAILED",
                    "无法读取 dpclat_driver 服务状态，不能确认已释放；记录保留，请重试或人工检查。",
                    items, true, [backupId]);
            }
            if (existing.Exists)
            {
                items.Add(new ProbeItem("service", "failed",
                    $"无有效常驻记录，但检测到 {DpcRules.ServiceName} 服务（{existing.StateText}）；未改动。"));
                return new HoldOutcome("failed", "RESTORE_FAILED",
                    "没有本插件的常驻记录，却检测到 dpclat_driver 服务；无法确认归属，未做任何改动，请手动处理。",
                    items, true, [backupId]);
            }
            HoldJournalStore.Delete(dataDirectory);
            return new HoldOutcome("success", "OK", "已释放（无需恢复）。", items, false, [backupId]);
        }

        if (loaded.Phase == HoldJournalStore.PhaseHolding && loaded.Pid > 0)
        {
            var owned = new OwnedProcess(loaded.Pid, loaded.StartTicksUtc, loaded.ExePath);
            if (_process.IsAlive(owned))
            {
                Step(20, "正常关闭本插件启动的 DPC");
                if (!_process.CloseGracefully(owned, DpcRules.GracefulCloseTimeoutMs))
                {
                    items.Add(new ProbeItem("process", "failed",
                        $"DPC 进程 PID {owned.Pid} 未在 {DpcRules.GracefulCloseTimeoutMs / 1000} 秒内退出；未强制结束。"));
                    return new HoldOutcome("failed", "RESTORE_FAILED",
                        "DPC 未在期限内正常退出；记录保留。请手动关闭 DPC 窗口后重试恢复。",
                        items, true, [backupId]);
                }
                items.Add(new ProbeItem("process", "success", $"已正常关闭 DPC 进程 PID {owned.Pid}。"));
            }
            else
            {
                items.Add(new ProbeItem("process", "partial", "记录中的 DPC 进程已不在运行。"));
            }
        }

        Step(50, "等待 DPC 自行清理内核服务");
        var deadline = _clock.UtcNow.AddMilliseconds(DpcRules.ServiceCleanupTimeoutMs);
        var svc = _service.Query(DpcRules.ServiceName);
        while (svc.Exists && svc.RunState != DpcServiceState.Stopped && _clock.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _clock.Delay(DpcRules.PollIntervalMs);
            svc = _service.Query(DpcRules.ServiceName);
        }

        if (svc.Error.Length > 0)
        {
            items.Add(new ProbeItem("service", "failed", "读取服务状态失败：" + svc.Error));
            return new HoldOutcome("failed", "RESTORE_FAILED",
                "关闭 DPC 后无法读取服务状态，不能确认服务已清理；记录保留，请重试或人工检查。",
                items, true, [backupId]);
        }

        if (svc.Exists)
        {
            Step(75, "清理归属可核验的遗留服务");
            var recorded = DpcRules.NormalizeImagePath(loaded.ServiceImagePath);
            var actual = DpcRules.NormalizeImagePath(svc.ImagePath);
            if (recorded.Length == 0 || !string.Equals(recorded, actual, StringComparison.OrdinalIgnoreCase) ||
                svc.Type != 1 || svc.StartType != 3)
            {
                items.Add(new ProbeItem("service", "failed",
                    $"服务仍存在（{svc.StateText}，类型 {svc.Type}，启动 {svc.StartType}，映像 {svc.ImagePath}），" +
                    "与常驻记录中的归属身份不一致；未删除。"));
                return new HoldOutcome("failed", "RESTORE_FAILED",
                    "遗留服务归属无法核验，未删除任何服务；记录保留。请手动关闭 DPC 或检查该服务。",
                    items, true, [backupId]);
            }
            if (!_service.StopAndDelete(svc, out var cleanupDetail))
            {
                items.Add(new ProbeItem("service", "failed", cleanupDetail));
                return new HoldOutcome("failed", "RESTORE_FAILED",
                    "清理遗留服务失败：" + cleanupDetail + "；记录保留，可重试。", items, true, [backupId]);
            }
            items.Add(new ProbeItem("service", "success", cleanupDetail));
        }
        else
        {
            items.Add(new ProbeItem("service", "success", $"{DpcRules.ServiceName} 已不存在（DPC 正常清理）。"));
        }

        HoldJournalStore.Delete(dataDirectory);
        var current = _timer.TryCurrent100ns(out var timerError);
        items.Add(new ProbeItem("resolution", current is null ? "partial" : "success",
            current is null
                ? "释放后读取系统分辨率失败：" + timerError
                : $"释放后系统当前读数 {DpcRules.Format100ns(current.Value)}。"));
        Step(100, "释放完成");
        return new HoldOutcome("success", "OK",
            "已释放本插件启动的 DPC 并核验服务清理。", items, false, [backupId]);
    }

    // ------------------------------------------------------------ 内部

    private (bool Ok, string Code, string Message, ServiceInfo Service, uint Current100ns) WaitForHold(
        OwnedProcess owned, CancellationToken cancellationToken)
    {
        var deadline = _clock.UtcNow.AddMilliseconds(DpcRules.HoldPollTimeoutMs);
        var lastState = "未知";
        var lastCurrent = 0u;
        var lastService = new ServiceInfo(false, 0, 0, 0, "", "");
        while (_clock.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_process.IsAlive(owned))
                return (false, "DPC_EXITED",
                    "DPC 进程在建立常驻前退出（可能驱动安装/启动失败或窗口被关闭）；请查看 DPC 窗口提示。",
                    lastService, lastCurrent);
            var svc = _service.Query(DpcRules.ServiceName);
            lastService = svc;
            lastState = svc.StateText;
            if (svc.Exists && svc.RunState == DpcServiceState.Running)
            {
                var current = _timer.TryCurrent100ns(out _);
                if (current is > 0)
                {
                    lastCurrent = current.Value;
                    if (current.Value <= DpcRules.ExpectedCurrent100ns)
                        return (true, "", "", svc, current.Value);
                }
            }
            _clock.Delay(DpcRules.PollIntervalMs);
        }
        return (false, "HOLD_TIMEOUT",
            $"在 {DpcRules.HoldPollTimeoutMs / 1000} 秒内未观察到 {DpcRules.ServiceName} 处于 RUNNING 且系统分辨率 ≤ 1ms" +
            $"（最后服务状态 {lastState}，最后读数 {(lastCurrent == 0 ? "未读到" : DpcRules.Format100ns(lastCurrent))}）。",
            lastService, lastCurrent);
    }

    private bool TryClose(OwnedProcess owned)
    {
        try { return _process.CloseGracefully(owned, DpcRules.GracefulCloseTimeoutMs); }
        catch (Exception) { return false; }
    }

    private bool SaveJournal(string dataDirectory, HoldJournal journal)
    {
        var ok = HoldJournalStore.Save(dataDirectory, journal, out var error);
        if (!ok) Console.Error.WriteLine(error);
        return ok;
    }

    private string NowText() => _clock.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private void CollectEnvironment(List<ProbeItem> items)
    {
        var supported = DpcRules.IsSupportedOs(_env.OsMajor, _env.OsBuild, _env.Is64BitProcess);
        items.Add(new ProbeItem("os", supported ? "success" : "partial",
            $"系统 {_env.OsMajor}.{_env.OsBuild}、x64={_env.Is64BitProcess}；" +
            (supported ? "属于支持的 Windows 10 x64 1809–21H2。" : "不是本插件支持的 Windows 10 x64 1809–21H2（Win11 请用宿主内置钟摆路径）。")));
        items.Add(new ProbeItem("admin", _env.IsAdmin ? "success" : "failed",
            _env.IsAdmin ? "当前进程具有管理员权限。" : "当前进程不是管理员；启动 DPC 驱动需要管理员。"));
        items.Add(new ProbeItem("secureBoot", _env.SecureBootState == "开启" ? "partial" : "success",
            _env.SecureBootState == "开启"
                ? "Secure Boot 已开启：该 2010 年前后签发的旧驱动可能无法在开启 Secure Boot 的 Win10 上加载；本插件不会关闭 Secure Boot。"
                : "Secure Boot：" + _env.SecureBootState + "。"));
    }

    private void CollectInput(List<ProbeItem> items, string? inputPath, string dataDirectory)
    {
        var resolved = _input.Resolve(inputPath, dataDirectory, out var resolveError);
        if (resolved is null)
        {
            items.Add(new ProbeItem("input", "partial", resolveError));
            return;
        }
        var identity = _input.VerifyFile(resolved);
        items.Add(identity.Ok
            ? new ProbeItem("input", "success",
                $"原版 DPC 身份匹配：{resolved}（{identity.Length} 字节，SHA256 {DpcRules.ShortSha(identity.Sha256)}）。")
            : new ProbeItem("input", "failed", identity.Error));
    }

    private void CollectJournal(List<ProbeItem> items, HoldJournal? loaded, string journalError)
    {
        if (journalError.Length > 0)
        {
            items.Add(new ProbeItem("journal", "failed", journalError));
            return;
        }
        if (loaded is null)
        {
            items.Add(new ProbeItem("journal", "success", "没有常驻记录（未由本插件启动过 DPC）。"));
            return;
        }
        if (loaded.Phase == HoldJournalStore.PhaseReleased)
        {
            items.Add(new ProbeItem("journal", "success", "常驻记录为已释放。"));
            return;
        }
        var alive = loaded.Pid > 0 &&
                    _process.IsAlive(new OwnedProcess(loaded.Pid, loaded.StartTicksUtc, loaded.ExePath));
        items.Add(new ProbeItem("journal", "partial",
            $"常驻记录 phase={loaded.Phase}，PID {loaded.Pid}，文件 {loaded.ExePath}，" +
            (alive ? "进程仍在运行。" : "进程已不在运行。") + " 释放请用插件管理页恢复备份。"));
        if (loaded.Phase == HoldJournalStore.PhaseHolding)
            items.Add(new ProbeItem("process", alive ? "success" : "partial",
                alive ? $"本插件启动的 DPC 进程 PID {loaded.Pid} 仍在运行。" : "记录中的 DPC 进程已退出。"));
    }

    private void CollectService(List<ProbeItem> items, ServiceInfo svc)
    {
        if (svc.Error.Length > 0)
        {
            items.Add(new ProbeItem("service", "partial", "读取服务状态失败：" + svc.Error));
            return;
        }
        items.Add(svc.Exists
            ? new ProbeItem("service", "partial",
                $"{DpcRules.ServiceName} 存在：{svc.StateText}，类型 {svc.Type}，启动 {svc.StartType}，映像 {svc.ImagePath}。")
            : new ProbeItem("service", "success", $"未发现 {DpcRules.ServiceName} 服务。"));
    }

    private void CollectTimer(List<ProbeItem> items)
    {
        var current = _timer.TryCurrent100ns(out var error);
        if (current is null)
        {
            items.Add(new ProbeItem("timer", "partial", "读取系统计时器分辨率失败：" + error));
            return;
        }
        items.Add(new ProbeItem("timer", current.Value <= DpcRules.ExpectedCurrent100ns ? "success" : "partial",
            $"系统当前计时器分辨率读数 {DpcRules.Format100ns(current.Value)}（DPC 目标 " +
            $"{DpcRules.Format100ns(DpcRules.ExpectedCurrent100ns)}；{DpcRules.Relation(current.Value, DpcRules.ExpectedCurrent100ns)}）。" +
            "该读数不等于跨进程 Sleep 或游戏收益。"));
    }

    private static HoldOutcome Summarize(List<ProbeItem> items, string doneMessage, string blockedCode)
    {
        var failed = items.Count(i => i.Status == "failed");
        var partial = items.Count(i => i.Status == "partial");
        var status = failed > 0 ? "failed" : partial > 0 ? "partial" : "success";
        var code = failed > 0 ? blockedCode : partial > 0 ? "CHECK_PARTIAL" : "OK";
        var message = failed > 0
            ? $"{doneMessage}：{failed} 项未通过、{partial} 项提示；请按明细处理。"
            : partial > 0
                ? $"{doneMessage}：{partial} 项提示，未发现阻断项。"
                : doneMessage + "：全部通过。";
        return new HoldOutcome(status, code, message, items, false, []);
    }

    private static HoldOutcome Fail(string backupId, List<ProbeItem> items, string code, string message,
        bool pendingRestore = false)
    {
        items.Add(new ProbeItem("result", "failed", message));
        return new HoldOutcome("failed", code, message, items, pendingRestore, [backupId]);
    }
}
