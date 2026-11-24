using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DeltaNFD.Backends;
using DeltaNFD.Execution.Procman;

namespace DeltaNFD.Services;

internal enum NvContainerMode : uint { Automatic = 2, Manual = 3, Disabled = 4 }
internal enum NvContainerRun : uint { Stopped = 1, StartPending = 2, StopPending = 3, Running = 4 }
internal sealed record NvContainerState(string Image, NvContainerMode Mode, bool Delayed, NvContainerRun Run, uint Pid,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Account = null)
{
    internal bool Stable => Run is NvContainerRun.Running or NvContainerRun.Stopped;
}
internal sealed record NvContainerBackup(string Image, NvContainerMode Mode,
    [property: JsonRequired] bool Delayed, [property: JsonRequired] bool WasRunning,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Account = null);

internal interface INvContainerScm
{
    NvContainerState Read();
    void SetMode(NvContainerMode mode);
    void RequestStop();
    void RequestStart();
    void WaitFor(NvContainerRun expected, NvContainerRun pending);
}
internal interface INvContainerBackupStore
{
    NvContainerBackup? Read();
    void Save(NvContainerBackup backup);
    void ReplaceConflicting(NvContainerBackup expected, NvContainerBackup replacement);
    void PrepareRestore(NvContainerBackup backup);
    void Clear(NvContainerBackup backup);
}

public static class NvDisplayContainerService
{
    private const string GateName = @"Global\DeltaNFD.NVIDIA-Display-Container-Switch";
    internal static readonly string DriverStore = Path.Combine(Environment.SystemDirectory, @"DriverStore\FileRepository");
    // Only the executable portion can use the Windows root alias. Never expand arbitrary variables.
    internal static readonly Regex Executable = new(
        """\A[ \t]*(?:"(?<exe>[^"\r\n]+\.exe)"|(?<exe>[^" \t\r\n]+\.exe))(?=[ \t]|\z)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Arguments = new(
        """\A(?:[ \t]+-arg[ \t]+(?:"<ExeDir>\\arguments\.txt"|<ExeDir>\\arguments\.txt))?[ \t]*\z""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Relative = new(
        @"\Anv[a-z0-9_.-]+\.inf_amd64_[0-9a-f]+\\Display\.NvContainer\\NVDisplay\.Container\.exe\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static string? ParseImage(string? raw) => ParseImage(raw, out _);

    internal static string? ParseImage(string? raw, out string reason)
    {
        reason = "服务映像路径不是受支持的 NVIDIA DriverStore 路径。";
        if (raw is null || raw.Length is 0 or > 1024) return null;
        var match = Executable.Match(raw);
        if (!match.Success) return null;
        var image = match.Groups["exe"].Value;
        var root = Path.GetDirectoryName(Environment.SystemDirectory)!;
        foreach (var prefix in new[] { @"\SystemRoot\", @"%SystemRoot%\", @"%windir%\" })
            if (image.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { image = Path.Combine(root, image[prefix.Length..]); break; }
        var basePath = DriverStore + "\\";
        if (!image.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) || !Relative.IsMatch(image[basePath.Length..])) return null;
        if (!Arguments.IsMatch(raw[match.Length..]))
        {
            reason = "显示容器的启动参数不是受支持的格式。";
            return null;
        }
        reason = "";
        return image;
    }

    internal static bool IsLocalSystem(string? name) => name is not null &&
        (name.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)
         || name.Equals(@"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase));

    // Reject reparse points at each component and pin the signed image during the write.
    internal static string? ParseContainerImage(string? raw, NvContainerTarget target) => target switch
    {
        NvContainerTarget.Display => ParseImage(raw),
        NvContainerTarget.LegacyTelemetry => NvTelemetryContainerPolicy.ParseImage(raw),
        NvContainerTarget.LocalSystem => NvLocalSystemContainerPolicy.ParseImage(raw),
        _ => null,
    };

    private static IDisposable PinTrusted(string raw, NvContainerTarget target)
    {
        var image = ParseContainerImage(raw, target)
            ?? throw new InvalidOperationException("映像路径或启动参数不是支持的 NVIDIA 服务配置。");
        for (var entry = image; !string.IsNullOrEmpty(entry); entry = Path.GetDirectoryName(entry))
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("服务映像路径含重解析点，未操作。");
        var pin = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (!NativeNvidiaGpuShareApi.TrustedNvidia(image))
                throw new InvalidOperationException("服务映像未通过 NVIDIA Authenticode 签名核验。");
            return pin;
        }
        catch { pin.Dispose(); throw; }
    }

    public static NvDisplayContainerStatus Status() => Inspect().Status;
    public static NvDisplayContainerStatus TelemetryStatus() => Inspect(NvContainerTarget.LegacyTelemetry).Status;
    public static NvDisplayContainerStatus LocalSystemStatus() => Inspect(NvContainerTarget.LocalSystem).Status;

    public static ReconcileSnapshot Reconcile() => Reconcile(BackendModules.Gpu, offerRecovery: false);
    public static ReconcileSnapshot TelemetryReconcile() => Reconcile(BackendModules.System, offerRecovery: true, target: NvContainerTarget.LegacyTelemetry);
    public static ReconcileSnapshot LocalSystemReconcile() => Reconcile(BackendModules.System, offerRecovery: true, target: NvContainerTarget.LocalSystem);

    public static ReconcileSnapshot Reconcile(string module, bool offerRecovery) => Reconcile(module, offerRecovery, target: NvContainerTarget.Display);

    private static string Scope(NvContainerTarget target) => target switch
    {
        NvContainerTarget.LegacyTelemetry => NvTelemetryContainer.RecoveryScope,
        NvContainerTarget.LocalSystem => NvLocalSystemContainer.RecoveryScope,
        _ => NvDisplayContainerChange.RecoveryScope,
    };
    private static string Label(NvContainerTarget target) => target switch
    {
        NvContainerTarget.LegacyTelemetry => "NVIDIA 遥测服务",
        NvContainerTarget.LocalSystem => "NVIDIA LocalSystem Container",
        _ => "NVIDIA 显示容器",
    };

    private static ReconcileSnapshot Reconcile(string module, bool offerRecovery, NvContainerTarget target)
    {
        var (status, stateVerified) = Inspect(target);
        Log.Info($"{Label(target)}只读核对：状态已确认={stateVerified}；可切换={status.Fingerprint is not null}；{status.Detail}");
        return Snapshot(module, status, stateVerified, offerRecovery, target);
    }

    internal static ReconcileSnapshot Snapshot(string module, NvDisplayContainerStatus status, bool stateVerified, bool offerRecovery, NvContainerTarget target = NvContainerTarget.Display)
        => new()
        {
            Module = module,
            Summary = status.Detail,
            Complete = stateVerified,
            Items = [new ReconcileItem { Id = Scope(target), Kind = "service",
                State = status.Detail, HasBackup = status.Restore }],
            RecoveryScopes = stateVerified && offerRecovery && status.Restore && NvDisplayContainerChange.IsFingerprint(status.Fingerprint)
                ? [new RecoveryScope { Id = Scope(target), Description = $"恢复 {Label(target)}原设置并回读核对",
                    Available = true, AffectedCount = 1, Fingerprint = status.Fingerprint }] : [],
        };

    public static (RecoverOutcome Outcome, bool OutcomeUnknown) Recover(NvDisplayContainerRecoveryArgs args) =>
        Recover(args.Module, args.Fingerprint, target: NvContainerTarget.Display);

    public static (RecoverOutcome Outcome, bool OutcomeUnknown) RecoverTelemetry(string fingerprint) =>
        Recover(BackendModules.System, fingerprint, target: NvContainerTarget.LegacyTelemetry);

    public static (RecoverOutcome Outcome, bool OutcomeUnknown) RecoverLocalSystem(string fingerprint) =>
        Recover(BackendModules.System, fingerprint, NvContainerTarget.LocalSystem);

    private static (RecoverOutcome Outcome, bool OutcomeUnknown) Recover(string module, string fingerprint, NvContainerTarget target)
    {
        ReconcileSnapshot? snapshot = null;
        var result = ChangeCore(new(true, fingerprint), state =>
            snapshot = Snapshot(module, Describe(state, null, target), stateVerified: true, offerRecovery: false, target: target), target);
        return (new RecoverOutcome
        {
            Scope = Scope(target),
            Success = result.Success,
            Verified = result.Success && snapshot is not null,
            Message = result.Message,
            VerifiedSnapshot = snapshot,
        }, result.OutcomeUnknown);
    }

    public static (NvDisplayContainerStatus Status, bool StateVerified) Inspect() => Inspect(target: NvContainerTarget.Display);

    private static (NvDisplayContainerStatus Status, bool StateVerified) Inspect(NvContainerTarget target)
    {
        var label = Label(target);
        var stage = "读取服务配置与运行状态";
        try
        {
            using var svc = new NvContainerScm(writable: false, target: target);
            var state = svc.Read();
            stage = "核验 NVIDIA 服务映像";
            using var pin = PinTrusted(state.Image, target);
            stage = "读取本机原设置备份";
            return InspectBackup(state, Backup(target), target);
        }
        catch (Win32Exception ex) when (target != NvContainerTarget.Display && ex.NativeErrorCode == 1060)
        {
            return (new(target == NvContainerTarget.LegacyTelemetry
                ? "未安装独立 NVIDIA 遥测服务（NvTelemetryContainer）。"
                : "未安装 NVIDIA LocalSystem Container（NvContainerLocalSystem）。"), true);
        }
        catch (Exception ex)
        {
            Log.Warn($"{label}核对失败：阶段={stage}；{ex.GetType().Name}：{ex.Message}");
            return (new($"无法核验{label}（{stage}）：{ex.Message}"), false);
        }
    }

    private static NvContainerFileBackup Backup(NvContainerTarget target) => target switch
    {
        NvContainerTarget.LegacyTelemetry => NvContainerFileBackup.Telemetry,
        NvContainerTarget.LocalSystem => NvContainerFileBackup.LocalSystem,
        _ => NvContainerFileBackup.Default,
    };

    // Acknowledging a stable, authenticated service state has no service or backup write authority.
    internal static (NvDisplayContainerStatus Status, bool StateVerified) InspectBackup(
        NvContainerState state, INvContainerBackupStore store, NvContainerTarget target = NvContainerTarget.Display)
    {
        try { return (Describe(state, store.Read(), target), state.Stable); }
        catch (Exception ex)
        {
            Log.Warn($"{Label(target)}核对备份失败：{ex.GetType().Name}：{ex.Message}");
            return (new($"{DescribeRuntime(state)}。原设置备份无法核验：{ex.Message} 当前无法执行切换。"), state.Stable);
        }
    }

    public static OperationResult Change(NvDisplayContainerChange args) => ChangeCore(args, null, target: NvContainerTarget.Display);
    public static OperationResult ChangeTelemetry(NvDisplayContainerChange args) => ChangeCore(args, null, target: NvContainerTarget.LegacyTelemetry);

    public static OperationResult ChangeLocalSystem(NvDisplayContainerChange args) => ChangeCore(args, null, NvContainerTarget.LocalSystem);

    private static OperationResult ChangeCore(NvDisplayContainerChange args, Action<NvContainerState>? recordRestored, NvContainerTarget target)
    {
        if (!ElevationHelper.IsElevated) return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        var label = Label(target);
        try
        {
            using var gameTargetLease = GameTargetService.Default.BeginOperation();
            using var gate = new Mutex(false, target switch
            {
                NvContainerTarget.LegacyTelemetry => @"Global\DeltaNFD.NVIDIA-Telemetry-Container-Switch",
                NvContainerTarget.LocalSystem => @"Global\DeltaNFD.NVIDIA-LocalSystem-Container-Switch",
                _ => GateName,
            });
            bool held;
            try { held = gate.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
            if (!held) return OperationResult.Fail($"另一{label}操作尚未结束，请等待完成。");
            try
            {
                using var svc = new NvContainerScm(writable: true, target: target);
                return Commit(svc, Backup(target), args, image => PinTrusted(image, target), GameExited, recordRestored, target);
            }
            finally { gate.ReleaseMutex(); }
        }
        catch (Exception ex)
        {
            Log.Warn($"{label}未进入切换：{ex.GetType().Name}：{ex.Message}");
            return OperationResult.Fail($"无法打开{label}操作，未改动服务：" + ex.Message);
        }
    }

    private static bool GameExited()
    {
        if (DeltaForceLocator.IsGameRunning()) return false;
        var target = GameTargetService.Default;
        if (!target.Current.IsCustom) return true;
        var byName = Process.GetProcessesByName(target.Current.ProcessName);
        foreach (var process in byName) process.Dispose();
        if (byName.Length > 0) return false;
        var exact = target.GetProcesses(forceRefresh: true);
        foreach (var process in exact) process.Dispose();
        return exact.Length == 0;
    }

    private static string DescribeRuntime(NvContainerState now)
    {
        var mode = now.Mode switch { NvContainerMode.Automatic => now.Delayed ? "自动（延迟启动）" : "自动", NvContainerMode.Manual => "手动", _ => "禁用" };
        return $"当前：{mode} · " + (now.Run == NvContainerRun.Running ? "运行中" : now.Run == NvContainerRun.Stopped ? "已停止" : $"状态切换中或不受支持（SCM {(uint)now.Run}）");
    }

    internal static NvDisplayContainerStatus Describe(NvContainerState now, NvContainerBackup? backup, NvContainerTarget target = NvContainerTarget.Display)
    {
        var text = DescribeRuntime(now);
        if (!now.Stable) return new(text + "。请稍后刷新。");
        if (backup is null && now.Mode == NvContainerMode.Disabled) return new(text + "。没有恢复备份，不猜测原设置。");
        var differences = backup is null ? "" : DescribeDifferences(now, backup, target);
        var conflict = differences.Length > 0;
        if (conflict && (target != NvContainerTarget.Display || now.Mode is not (NvContainerMode.Automatic or NvContainerMode.Manual)))
            return new(text + $"。服务配置与备份不匹配（{differences}），无法安全切换。");
        var detail = conflict ? $"。旧备份与当前配置不匹配（{differences}）。确认停用后将归档旧备份并保存当前配置。"
            : backup is null ? "。" : "。已找到原设置备份，可恢复。";
        return new(text + detail,
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { now, backup }))), backup is not null && !conflict);
    }

    private static string DescribeDifferences(NvContainerState now, NvContainerBackup backup, NvContainerTarget target)
    {
        var differences = new List<string>();
        if (!SameImageCommand(now.Image, backup.Image, target)) differences.Add("映像路径或启动参数");
        if (now.Delayed != backup.Delayed) differences.Add("延迟启动标记");
        if (now.Account != backup.Account) differences.Add("服务账户");
        if (now.Mode != NvContainerMode.Disabled && now.Mode != backup.Mode) differences.Add("启动类型");
        return string.Join("、", differences);
    }

    private static bool SameImageCommand(string current, string original, NvContainerTarget target)
    {
        if (current == original) return true;
        if (target != NvContainerTarget.Display) return false;

        // Windows setup can rewrite the casing of the DriverStore executable path.
        // The quoting, spacing and argument text still have to match exactly.
        var currentPath = Executable.Match(current).Groups["exe"];
        var originalPath = Executable.Match(original).Groups["exe"];
        return currentPath.Success && originalPath.Success
            && current.AsSpan(0, currentPath.Index).SequenceEqual(original.AsSpan(0, originalPath.Index))
            && currentPath.ValueSpan.Equals(originalPath.ValueSpan, StringComparison.OrdinalIgnoreCase)
            && current.AsSpan(currentPath.Index + currentPath.Length)
                .SequenceEqual(original.AsSpan(originalPath.Index + originalPath.Length));
    }

    internal static OperationResult Commit(INvContainerScm svc, INvContainerBackupStore store, NvDisplayContainerChange args,
        Func<string, IDisposable> pinTrusted, Func<bool> gameExited, Action<NvContainerState>? recordRestored = null, NvContainerTarget target = NvContainerTarget.Display)
    {
        var label = Label(target);
        var serviceWriteAttempted = false;
        var stage = "读取服务与恢复备份";
        try
        {
            var original = svc.Read();
            var saved = store.Read();
            var expected = Describe(original, saved, target);
            Log.Info($"{label}操作：方向={(args.Restore ? "恢复" : "停用")}；{expected.Detail}");
            if (expected.Fingerprint is null || args.Fingerprint != expected.Fingerprint || args.Restore != expected.Restore)
                return OperationResult.Fail("状态与确认时不一致，未改动服务。请刷新。" + expected.Detail);
            stage = "核验映像及写入前状态";
            using var pin = pinTrusted(original.Image);
            void CheckGame()
            {
                if (!gameExited()) throw new InvalidOperationException($"请先退出游戏，再操作{label}。");
            }
            void Recheck()
            {
                CheckGame();
                if (svc.Read() != original) throw new InvalidOperationException("服务状态在写入前变化，请刷新。");
            }
            Recheck();
            if (!args.Restore)
            {
                var replacement = new NvContainerBackup(original.Image, original.Mode, original.Delayed,
                    original.Run == NvContainerRun.Running, original.Account);
                if (saved is null)
                {
                    stage = "保存并回读原设置备份";
                    store.Save(replacement);
                }
                else
                {
                    stage = "归档冲突备份并保存当前配置";
                    store.ReplaceConflicting(saved, replacement);
                }
                saved = replacement;
            }
            else
            {
                stage = "复核原设置备份及权限";
                store.PrepareRestore(saved ?? throw new IOException("原设置备份缺失。"));
            }
            stage = "备份后复核服务和游戏状态";
            Recheck();
            if (saved is null) throw new IOException("原设置备份缺失。");
            var mode = args.Restore ? saved.Mode : NvContainerMode.Disabled;
            NvContainerState ReadBack()
            {
                var actual = svc.Read();
                var differences = DescribeDifferences(actual, saved, target);
                if (differences.Length > 0 || actual.Mode != mode)
                    throw new IOException($"服务配置回读与预期不符：差异={differences}，启动方式={actual.Mode}（预期 {mode}），运行状态={(uint)actual.Run}。");
                return actual;
            }
            if (original.Mode != mode)
            {
                stage = $"写入启动方式 {mode}";
                serviceWriteAttempted = true;
                svc.SetMode(mode);
            }
            stage = "回读启动方式";
            var current = ReadBack();
            if (!args.Restore)
            {
                stage = "请求正常停止服务";
                if (current.Run == NvContainerRun.Running) { CheckGame(); serviceWriteAttempted = true; svc.RequestStop(); }
                stage = "等待服务停止并回读";
                svc.WaitFor(NvContainerRun.Stopped, NvContainerRun.StopPending);
                var disabled = ReadBack();
                if (disabled.Run != NvContainerRun.Stopped) throw new IOException($"未确认服务停止，SCM 状态 {(uint)disabled.Run}。");
                Log.Info($"{label}停用已回读：{DescribeRuntime(disabled)}，原设置备份已保存。");
                return OperationResult.Ok(target == NvContainerTarget.Display
                    ? "显示容器已禁用并停止。要打开 NVIDIA 控制面板或结束测试，可恢复原设置。"
                    : $"{label}已禁用并停止，可在本页恢复原设置。");
            }
            if (saved.WasRunning)
            {
                stage = "请求启动原来正在运行的服务";
                if (current.Run == NvContainerRun.Stopped) { CheckGame(); serviceWriteAttempted = true; svc.RequestStart(); }
                stage = "等待服务启动";
                svc.WaitFor(NvContainerRun.Running, NvContainerRun.StartPending);
            }
            stage = "回读恢复结果";
            var restored = ReadBack();
            if (!restored.Stable || saved.WasRunning && restored.Run != NvContainerRun.Running)
                throw new IOException($"恢复后的 SCM 状态尚未确认（{(uint)restored.Run}）。");
            serviceWriteAttempted = false; // The service change has been verified. Backup cleanup has its own error.
            stage = "标记原设置备份已恢复";
            store.Clear(saved);
            recordRestored?.Invoke(restored);
            Log.Info($"{label}恢复已回读：{DescribeRuntime(restored)}。");
            return OperationResult.Ok("已恢复原启动方式。当前服务" + (restored.Run == NvContainerRun.Running ? "运行中。" : "已停止。"));
        }
        catch (Exception ex)
        {
            var detail = $"{stage}失败：{ex.Message}";
            Log.Warn($"{label}操作失败：阶段={stage}；服务写入待核对={serviceWriteAttempted}；{ex.GetType().Name}：{ex.Message}");
            if (serviceWriteAttempted)
                return new OperationResult { OutcomeUnknown = true, Message = detail + " 请只读核对当前服务状态及原设置备份。" };
            return OperationResult.Fail(stage == "标记原设置备份已恢复"
                ? "服务恢复已通过回读，备份收尾失败。请刷新核对。" + detail
                : "未改动服务。" + detail);
        }
    }
}



