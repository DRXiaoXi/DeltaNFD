using System.Diagnostics;
using System.Text.Json;

namespace DeltaNFD.Services.AlphaRestore;

internal sealed class RuntimeRepairProcessRunner(string root, Func<bool>? busyProbe = null)
{
    private sealed record Activity(string Id, string Stage, int Pid, long StartTicks, string Executable,
        int OwnerPid, long OwnerStartTicks, DateTimeOffset CreatedUtc);
    private string RecordPath => Path.Combine(root, "runtime-repair-active.json");

    internal string? CheckPending(bool installerBusy) => ReadPending(installerBusy, archiveEnded: true, out _);

    internal string? InspectPending(bool installerBusy, out bool endedUnknown)
        => ReadPending(installerBusy, archiveEnded: false, out endedUnknown);

    private string? ReadPending(bool installerBusy, bool archiveEnded, out bool endedUnknown)
    {
        endedUnknown = false;
        if (Directory.Exists(RecordPath)) return "安装记录路径被目录占用，状态无法确认。";
        if (!File.Exists(RecordPath)) return null;
        try
        {
            var record = JsonSerializer.Deserialize<Activity>(File.ReadAllText(RecordPath))
                ?? throw new InvalidDataException("安装记录为空");
            if (!Guid.TryParse(record.Id, out _) || record.OwnerPid <= 0 || record.OwnerStartTicks <= 0
                || !Path.IsPathFullyQualified(record.Executable) || record.Stage is not ("prepared" or "running")) throw new InvalidDataException("安装身份记录无效");
            if (record.Stage == "prepared")
                return "上次安装器启动结果未确认；启动前记录已保留，禁止重复执行。";
            if (record.Pid <= 0 || record.StartTicks <= 0) return "安装器身份缺失，禁止重复执行。";
            using var process = TryFind(record.Pid);
            if (process is not null && !process.HasExited && process.StartTime.ToUniversalTime().Ticks == record.StartTicks)
                return $"上次安装器仍运行（PID {record.Pid}），禁止重复修复。";
            if (installerBusy) return "旧安装器进程已退出，但 Windows Installer 事务仍忙，禁止重复修复。";
            endedUnknown = true;
            if (!archiveEnded) return null;
            // Confirmation of exit is not confirmation of a successful operation. Archive as unknown.
            var archive = Path.Combine(root, "runtime-repair-history");
            Directory.CreateDirectory(archive);
            File.Move(RecordPath, Path.Combine(archive, record.Id + "-ended-unknown.json"));
            Log.Warn("旧安装器已确认退出，未确认结果的证据已归档；下一次修复必须重新检测，不代表已恢复。");
            return null;
        }
        catch (Exception ex) { return "上次安装记录无法确认，禁止重复执行：" + ex.Message; }
    }

    private static Process? TryFind(int pid)
    { try { return Process.GetProcessById(pid); } catch (ArgumentException) { return null; } }

    private void Save(Activity record, bool create)
    {
        Directory.CreateDirectory(root);
        if (create)
        {
            using var file = new FileStream(RecordPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(file, record); file.Flush(true);
        }
        else
        {
            var temporary = RecordPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temporary, JsonSerializer.Serialize(record)); File.Move(temporary, RecordPath, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    internal async Task<RuntimeStepResult> RunAsync(ProcessStartInfo info, string label, TimeSpan timeout,
        IProgress<string>? progress, CancellationToken token, Action<Process>? onStarted = null, Func<string>? waitStatus = null)
    {
        token.ThrowIfCancellationRequested();
        
        // 启动前再次检查 Windows Installer 互斥锁
        if ((busyProbe ?? RuntimeRepairPreflight.InstallerBusy)())
        {
            Log.Warn($"启动 {label} 前检测到 Windows Installer 互斥锁被占用，尝试等待 10 秒。");
            if (!RuntimeRepairPreflight.WaitForInstallerIdle(TimeSpan.FromSeconds(10)))
            {
                return new(1618, "Windows Installer 正在处理其他事务，未启动此步骤。", OutcomeUnknown: false);
            }
            Log.Info("Windows Installer 互斥锁已释放，继续启动。");
        }
        
        using var owner = Process.GetCurrentProcess();
        var record = new Activity(Guid.NewGuid().ToString("D"), "prepared", 0, 0, info.FileName,
            owner.Id, owner.StartTime.ToUniversalTime().Ticks, DateTimeOffset.UtcNow);
        Save(record, create: true);
        Process? process = null;
        var launched = false;
        try
        {
            token.ThrowIfCancellationRequested();
            process = Process.Start(info);
            if (process is null) { File.Delete(RecordPath); return new(null, "安装器未启动"); }
            launched = true;
            record = record with { Stage = "running", Pid = process.Id, StartTicks = process.StartTime.ToUniversalTime().Ticks };
            Save(record, create: false);
            onStarted?.Invoke(process);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout);
            var watch = Stopwatch.StartNew();
            var exit = process.WaitForExitAsync();
            while (!exit.IsCompleted)
            {
                var beat = Task.Delay(TimeSpan.FromSeconds(5), deadline.Token);
                await Task.WhenAny(exit, beat).WaitAsync(deadline.Token);
                if (exit.IsCompleted) break;
                progress?.Report(waitStatus is null
                    ? $"{label} · PID {process.Id} · 已等待 {watch.Elapsed:mm\\:ss}，单步上限 {timeout.TotalMinutes:0.0} 分钟"
                    : waitStatus() + $"（PID {process.Id}，单步上限 {timeout.TotalMinutes:0.#} 分钟）");
            }
            await exit;
            var code = process.ExitCode;
            
            // 退出后等待最多 3 秒确保 Installer 互斥锁释放
            var busyCheckStart = Stopwatch.StartNew();
            while ((busyProbe ?? RuntimeRepairPreflight.InstallerBusy)() && busyCheckStart.Elapsed < TimeSpan.FromSeconds(3))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), token);
            }
            
            var busy = (busyProbe ?? RuntimeRepairPreflight.InstallerBusy)();
            if (busy && code is not (1618 or 1605))
            {
                Log.Warn($"安装器退出但 Installer 事务仍忙：PID={process.Id}；退出码={code}；等待时间={busyCheckStart.Elapsed}");
                return new(code, "启动器已退出，但 Installer 事务仍忙；未启动后续步骤。", OutcomeUnknown: true);
            }
            File.Delete(RecordPath);
            Log.Info($"受控安装器结束：PID={process.Id}；阶段={label}；退出码={code}；耗时={watch.Elapsed}");
            return new(code);
        }
        catch (OperationCanceledException) when (launched)
        {
            Log.Warn($"受控安装器等待取消/超时：阶段={label}；PID={record.Pid}；没有强杀安装事务，证据保留。");
            return new(null, "已停止等待，但安装事务结束未确认。未强杀安装器；禁止立即重试，等待其结束后核对。", OutcomeUnknown: true);
        }
        catch (Exception ex)
        {
            if (!launched) { File.Delete(RecordPath); return new(null, "启动失败：" + ex.Message); }
            Log.Error("安装器启动后结果未确认，证据保留", ex);
            return new(null, "安装器状态未确认：" + ex.Message, OutcomeUnknown: true);
        }
        finally { process?.Dispose(); }
    }
}
