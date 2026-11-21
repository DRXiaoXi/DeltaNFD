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
    void Clear();
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

    public static NvDisplayContainerStatus Status()
    {
        try
        {
            using var svc = new NvContainerScm(false);
            var state = svc.Read();
            using var pin = PinTrusted(state.Image);
            var backup = new NvContainerRegistryBackup().Read();
            return Describe(state, backup);
        }
        catch (Exception ex) { return new("无法核验显示容器：" + ex.Message); }
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
        catch (Exception ex) { return OperationResult.Fail("无法打开显示容器操作，未改动服务：" + ex.Message); }
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

    internal static NvDisplayContainerStatus Describe(NvContainerState now, NvContainerBackup? backup)
    {
        var mode = now.Mode switch { NvContainerMode.Automatic => now.Delayed ? "自动（延迟启动）" : "自动", NvContainerMode.Manual => "手动", _ => "禁用" };
        var text = $"当前：{mode} · " + (now.Run == NvContainerRun.Running ? "运行中" : now.Run == NvContainerRun.Stopped ? "已停止" : "状态切换中或不受支持");
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
        var attempted = false;
        try
        {
            var original = svc.Read();
            var saved = store.Read();
            var expected = Describe(original, saved);
            if (expected.Fingerprint is null || args.Fingerprint != expected.Fingerprint || args.Restore != expected.Restore)
                return OperationResult.Fail("状态与确认时不一致，未改动服务。请刷新。" + expected.Detail);
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
                attempted = true; // An interrupted backup write also needs review.
                store.Save(saved);
                Recheck();
            }
            var mode = args.Restore ? saved.Mode : NvContainerMode.Disabled;
            NvContainerState ReadBack()
            {
                var actual = svc.Read();
                if (!Matches(actual, saved) || actual.Mode != mode)
                    throw new IOException("服务身份或启动方式与预期不一致，请只读核对。");
                return actual;
            }
            if (original.Mode != mode)
            {
                attempted = true;
                svc.SetMode(mode);
            }
            var current = ReadBack();
            if (!args.Restore)
            {
                if (current.Run == NvContainerRun.Running) { CheckGame(); attempted = true; svc.RequestStop(); }
                svc.WaitFor(NvContainerRun.Stopped, NvContainerRun.StopPending);
                if (ReadBack().Run != NvContainerRun.Stopped) throw new IOException("未确认服务停止。");
                return OperationResult.Ok("显示容器已禁用并停止。要打开 NVIDIA 控制面板或结束测试，可恢复原设置。");
            }
            if (saved.WasRunning)
            {
                if (current.Run == NvContainerRun.Stopped) { CheckGame(); attempted = true; svc.RequestStart(); }
                svc.WaitFor(NvContainerRun.Running, NvContainerRun.StartPending);
            }
            var restored = ReadBack();
            if (!restored.Stable || saved.WasRunning && restored.Run != NvContainerRun.Running) throw new IOException("恢复状态尚未确认。");
            attempted = true;
            store.Clear();
            return OperationResult.Ok("已恢复原启动方式。当前服务" + (restored.Run == NvContainerRun.Running ? "运行中。" : "已停止。"));
        }
        catch (Exception ex)
        {
            return attempted ? new OperationResult { OutcomeUnknown = true, Message = "结果未确认，请只读核对后再恢复；不要删除备份。" + ex.Message }
                : OperationResult.Fail("未改动服务。" + ex.Message);
        }
    }
}



