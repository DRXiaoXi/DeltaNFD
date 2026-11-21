using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeltaNFD.Execution.Procman;

namespace DeltaNFD.Services;

internal enum NvContainerMode : uint { Automatic = 2, Manual = 3, Disabled = 4 }
internal enum NvContainerRun : uint { Stopped = 1, StartPending = 2, StopPending = 3, Running = 4 }
internal sealed record NvContainerState(string Image, NvContainerMode Mode, bool Delayed, NvContainerRun Run, uint Pid)
{
    internal bool Stable => Run is NvContainerRun.Running or NvContainerRun.Stopped;
}
internal sealed record NvContainerBackup(string Image, NvContainerMode Mode, bool Delayed, bool WasRunning);

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
    void PrepareRestore(NvContainerBackup backup);
    void Clear(NvContainerBackup backup);
}

public static class NvDisplayContainerService
{
    private const string GateName = @"Global\DeltaNFD.NVIDIA-Display-Container-Switch";
    internal static readonly string DriverStore = Path.Combine(Environment.SystemDirectory, @"DriverStore\FileRepository");
    // Only the executable portion can use the Windows root alias. Never expand arbitrary variables.
    private static readonly Regex Executable = new(
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
    private static IDisposable PinTrusted(string raw)
    {
        var image = ParseImage(raw) ?? throw new InvalidOperationException("映像路径或启动参数不是支持的 NVIDIA DriverStore 格式。");
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

    public static ReconcileSnapshot Reconcile()
    {
        var (status, stateVerified) = Inspect();
        Log.Info($"NVIDIA 显示容器只读核对：状态已确认={stateVerified}；可切换={status.Fingerprint is not null}；{status.Detail}");
        return new ReconcileSnapshot
        {
            Module = DeltaNFD.Backends.BackendModules.Gpu,
            Summary = status.Detail,
            Complete = stateVerified,
            Items = [new ReconcileItem { Id = "nv-display-container", Kind = "service", State = status.Detail }],
        };
    }

    public static (NvDisplayContainerStatus Status, bool StateVerified) Inspect()
    {
        var stage = "读取服务配置与运行状态";
        try
        {
            using var svc = new NvContainerScm(false);
            var state = svc.Read();
            stage = "核验 NVIDIA 服务映像";
            using var pin = PinTrusted(state.Image);
            return InspectBackup(state, new NvContainerRegistryBackup());
        }
        catch (Exception ex)
        {
            Log.Warn($"NVIDIA 显示容器核对失败：阶段={stage}；{ex.GetType().Name}：{ex.Message}");
            return (new($"无法核验显示容器（{stage}）：{ex.Message}"), false);
        }
    }

    // Acknowledging a stable, authenticated service state has no service or backup write authority.
    internal static (NvDisplayContainerStatus Status, bool StateVerified) InspectBackup(
        NvContainerState state, INvContainerBackupStore store)
    {
        try { return (Describe(state, store.Read()), state.Stable); }
        catch (Exception ex)
        {
            Log.Warn($"NVIDIA 显示容器核对备份失败：{ex.GetType().Name}：{ex.Message}");
            return (new($"{DescribeRuntime(state)}。原设置备份无法核验：{ex.Message} 当前无法执行切换。"), state.Stable);
        }
    }

    public static OperationResult Change(NvDisplayContainerChange args)
    {
        if (!ElevationHelper.IsElevated) return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        try
        {
            using var target = GameTargetService.Default.BeginOperation();
            using var gate = new Mutex(false, GateName);
            bool held;
            try { held = gate.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
            if (!held) return OperationResult.Fail("另一显示容器操作尚未结束，请关闭旧开关或等待完成。");
            try
            {
                using var svc = new NvContainerScm(true);
                return Commit(svc, new NvContainerRegistryBackup(), args, PinTrusted, GameExited);
            }
            finally { gate.ReleaseMutex(); }
        }
        catch (Exception ex)
        {
            Log.Warn($"NVIDIA 显示容器未进入切换：{ex.GetType().Name}：{ex.Message}");
            return OperationResult.Fail("无法打开显示容器操作，未改动服务：" + ex.Message);
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

    internal static NvDisplayContainerStatus Describe(NvContainerState now, NvContainerBackup? backup)
    {
        var text = DescribeRuntime(now);
        if (!now.Stable) return new(text + "。请稍后刷新。");
        if (backup is null && now.Mode == NvContainerMode.Disabled) return new(text + "。没有恢复备份，不猜测原设置。");
        if (backup is not null && !Matches(now, backup)) return new(text + "。服务配置与备份不匹配（如更换驱动），不会覆盖当前配置。");
        return new(text + (backup is null ? "。" : "。已找到原设置备份，可恢复。"),
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { now, backup }))), backup is not null);
    }

    private static bool Matches(NvContainerState now, NvContainerBackup backup) =>
        now.Image == backup.Image && now.Delayed == backup.Delayed &&
        (now.Mode == NvContainerMode.Disabled || now.Mode == backup.Mode);

    internal static OperationResult Commit(INvContainerScm svc, INvContainerBackupStore store, NvDisplayContainerChange args,
        Func<string, IDisposable> pinTrusted, Func<bool> gameExited)
    {
        var serviceWriteAttempted = false;
        var stage = "读取服务与恢复备份";
        try
        {
            var original = svc.Read();
            var saved = store.Read();
            var expected = Describe(original, saved);
            Log.Info($"NVIDIA 显示容器操作：方向={(args.Restore ? "恢复" : "停用")}；{expected.Detail}");
            if (expected.Fingerprint is null || args.Fingerprint != expected.Fingerprint || args.Restore != expected.Restore)
                return OperationResult.Fail("状态与确认时不一致，未改动服务。请刷新。" + expected.Detail);
            stage = "核验映像及写入前状态";
            using var pin = pinTrusted(original.Image);
            void CheckGame()
            {
                if (!gameExited()) throw new InvalidOperationException("请先退出游戏，再操作显示容器服务。");
            }
            void Recheck()
            {
                CheckGame();
                if (svc.Read() != original) throw new InvalidOperationException("服务状态在写入前变化，请刷新。");
            }
            Recheck();
            if (saved is null)
            {
                saved = new(original.Image, original.Mode, original.Delayed, original.Run == NvContainerRun.Running);
                stage = "保存并回读原设置备份";
                store.Save(saved);
            }
            else {
                stage = "复核原设置备份及权限";
                store.PrepareRestore(saved);
            }
            stage = "备份后复核服务和游戏状态";
            Recheck();
            var mode = args.Restore ? saved.Mode : NvContainerMode.Disabled;
            NvContainerState ReadBack()
            {
                var actual = svc.Read();
                if (!Matches(actual, saved) || actual.Mode != mode)
                    throw new IOException($"服务配置回读与预期不符：映像一致={actual.Image == saved.Image}，启动方式={actual.Mode}（预期 {mode}），延迟标记={actual.Delayed}（原值 {saved.Delayed}），运行状态={(uint)actual.Run}。");
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
                Log.Info($"NVIDIA 显示容器停用已回读：{DescribeRuntime(disabled)}，原设置备份已保存。");
                return OperationResult.Ok("显示容器已禁用并停止。要打开 NVIDIA 控制面板或结束测试，可恢复原设置。");
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
            stage = "清理已恢复的备份";
            store.Clear(saved);
            Log.Info($"NVIDIA 显示容器恢复已回读：{DescribeRuntime(restored)}。");
            return OperationResult.Ok("已恢复原启动方式。当前服务" + (restored.Run == NvContainerRun.Running ? "运行中。" : "已停止。"));
        }
        catch (Exception ex)
        {
            var detail = $"{stage}失败：{ex.Message}";
            Log.Warn($"NVIDIA 显示容器操作失败：阶段={stage}；服务写入待核对={serviceWriteAttempted}；{ex.GetType().Name}：{ex.Message}");
            if (serviceWriteAttempted)
                return new OperationResult { OutcomeUnknown = true, Message = detail + " 请只读核对当前服务状态及原设置备份。" };
            return OperationResult.Fail(stage == "清理已恢复的备份"
                ? "服务恢复已通过回读，备份收尾失败。请刷新核对。" + detail
                : "未改动服务。" + detail);
        }
    }
}



