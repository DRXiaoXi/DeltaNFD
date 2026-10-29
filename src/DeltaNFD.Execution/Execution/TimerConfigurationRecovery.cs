using System.Diagnostics;
using System.Text.Json;
using DeltaNFD.Inspection;

namespace DeltaNFD.Services;

internal interface ITimerRecoveryEnvironment
{
    string? Blocker();
    TimerRecoveryStatus Read();
    void Save(TimerRecoveryRecord record);
    int DeleteValue(string name);
    int EnableDevice(string id);
}

internal static class TimerRecoveryEngine
{
    internal static OperationResult Run(ITimerRecoveryEnvironment env, TimerRecoveryArgs args, CancellationToken token)
    {
        TimerRecoveryRecord? journal = null;
        OperationResult Stop(string reason, bool unknown = false) => new() { Success=false, IsPartial=journal?.Changed==true,
            OutcomeUnknown=unknown,RequiresReboot=journal?.Changed==true,Message=reason+" 原始状态和恢复记录保留，请在此页重新检测核对。" };
        try
        {
            if (env.Blocker() is { } blocked) return OperationResult.Fail(blocked);
            token.ThrowIfCancellationRequested();
            var before=env.Read();
            if (!before.BcdReadable) return OperationResult.Fail("无法读取当前启动配置，未执行恢复。");
            if (string.IsNullOrWhiteSpace(args.Fingerprint) || args.Fingerprint!=before.Fingerprint)
                return OperationResult.Fail("配置已变化，请刷新并重新确认恢复范围。");
            if (args.RestoreDisabledDevices && !before.DevicesReadable) return OperationResult.Fail("无法确认设备状态，未执行恢复。");
            var devices=args.RestoreDisabledDevices ? before.Devices.Where(d=>d.Disabled).ToArray() : [];
            if (devices.Length>16 || devices.Any(d=>!TimerConfigurationInspection.IsHpetDevice(d.InstanceId)))
                return OperationResult.Fail("设备范围不符合固定 HPET 规则，未执行恢复。");
            journal=new() { Before=before, Changed=before.PendingRestart };
            env.Save(journal); // Durable before any boot or device write; failure aborts.
            foreach (var name in new[] { "disabledynamictick", "useplatformtick" })
            {
                token.ThrowIfCancellationRequested();
                if (env.Blocker() is { } guard) return Stop(guard);
                var original=name=="disabledynamictick" ? before.DynamicTick : before.PlatformTick;
                var current=env.Read();
                var value=name=="disabledynamictick" ? current.DynamicTick : current.PlatformTick;
                if (!current.BcdReadable || value!=original) return Stop("启动配置发生外部变化，停止后续步骤。");
                if (value.Length==0) { journal.Steps.Add(name+" 已不存在，无需删除");env.Save(journal);continue; }
                // Fixed argument list, independent steps. No shell/&&, no arbitrary BCD key.
                journal.Changed=true;env.Save(journal);
                var code=env.DeleteValue(name);
                var after=env.Read();
                var remaining=name=="disabledynamictick" ? after.DynamicTick : after.PlatformTick;
                if (code<0) return Stop("启动项恢复执行超时，结果尚未确认。",true);
                if (!after.BcdReadable || remaining.Length!=0) return Stop("启动项 "+name+" 尚未确认恢复（退出码 "+code+"）。",code<0);
                journal.Steps.Add(name+" 已独立复检为不存在；退出码 "+code);env.Save(journal);
            }
            foreach (var device in devices)
            {
                token.ThrowIfCancellationRequested();
                if (env.Blocker() is { } guard) return Stop(guard);
                var current=env.Read();
                var target=current.Devices.SingleOrDefault(d=>d.InstanceId.Equals(device.InstanceId,StringComparison.OrdinalIgnoreCase));
                if (!current.DevicesReadable || target is null) return Stop("设备身份或状态发生变化，未继续操作。");
                if (!target.Disabled) { journal.Steps.Add(device.InstanceId+" 已不再禁用");env.Save(journal);continue; }
                journal.Changed=true;env.Save(journal);
                var code=env.EnableDevice(device.InstanceId);
                var after=env.Read();
                if (code<0) return Stop("设备恢复执行超时，结果尚未确认。",true);
                if (!after.DevicesReadable || !after.Devices.Any(d=>d.InstanceId.Equals(device.InstanceId,StringComparison.OrdinalIgnoreCase) && d.ProblemCode==0))
                    return Stop("HPET 设备尚未确认恢复（退出码 "+code+"）；若要求重启，请重启后重新核对。",code<0);
                journal.Steps.Add(device.InstanceId+" 已启用并复检");env.Save(journal);
            }
            var final=env.Read();
            if (!final.BcdReadable || final.DynamicTick.Length!=0 || final.PlatformTick.Length!=0)
                return Stop("最终启动配置复检未通过。");
            journal.Completed=true;env.Save(journal);
            var reboot=journal.Changed || before.PendingRestart;
            return new() { Success=true,RequiresReboot=reboot,Message=(reboot ? "恢复已完成，必须手动重启电脑后再检测；当前启动的计时器行为不会立即改变。" : "启动配置已核对为 Windows 默认。")
                +(final.Devices.Any(d=>d.Disabled) ? " 仍有未选择恢复的已禁用 HPET 设备，请核对设备选项。" : "")
                +" 未修改运行库、useplatformclock 或其他启动配置。原始状态已归档。" };
        }
        catch (OperationCanceledException) { return Stop("已取消后续恢复。"); }
        catch (Exception ex) { return Stop("恢复未完成："+ex.Message,journal?.Changed==true); }
    }
}

public static class TimerConfigurationRecovery
{
    private static readonly SemaphoreSlim Gate=new(1,1);
    public static async Task<OperationResult> RunAsync(TimerRecoveryArgs args,CancellationToken token)
    {
        if (!await Gate.WaitAsync(0,token)) return OperationResult.Fail("另一个计时器恢复正在执行，请等待结束后重新检测。");
        try { return await Task.Run(()=>TimerRecoveryEngine.Run(new WindowsTimerRecoveryEnvironment(),args,token)); }
        finally { Gate.Release(); }
    }
}

internal sealed class WindowsTimerRecoveryEnvironment : ITimerRecoveryEnvironment
{
    public string? Blocker()
    {
        if (!ElevationHelper.IsElevated) return ElevationHelper.NotElevatedMessage;
        try { return DeltaForceLocator.IsGameRunning() ? "请完全退出三角洲后再恢复计时器。" : null; }
        catch { return "无法确认三角洲运行状态，未执行计时器恢复。"; }
    }
    public TimerRecoveryStatus Read()=>TimerConfigurationInspection.Read();
    public int DeleteValue(string name)
    {
        if (name is not ("disabledynamictick" or "useplatformtick")) throw new InvalidOperationException("启动项不在恢复范围内");
        return Run("bcdedit.exe",["/deletevalue","{current}",name]);
    }
    public int EnableDevice(string id)
    {
        if (!TimerConfigurationInspection.IsHpetDevice(id)) throw new InvalidOperationException("设备不在恢复范围内");
        return Run("pnputil.exe",["/enable-device",id]);
    }
    private static int Run(string file,string[] args)
    {
        var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,file)) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var child=Process.Start(start) ?? throw new IOException("无法启动恢复命令");
        var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(20000)) { try { child.Kill(); } catch { } return -1; }
        Task.WaitAll([stdout,stderr],TimeSpan.FromSeconds(3));
        Log.Info("计时器恢复："+file+" "+string.Join(" ",args)+"；退出码="+child.ExitCode);
        return child.ExitCode;
    }
    public void Save(TimerRecoveryRecord record)
    {
        var directory=Path.Combine(AppDataPaths.Root,"timer-recovery");
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0) throw new IOException("恢复目录为链接，已停止");
        var bytes=JsonSerializer.SerializeToUtf8Bytes(record,new JsonSerializerOptions { WriteIndented=true });
        Atomic(Path.Combine(directory,record.Id+".json"),bytes);
        Atomic(TimerConfigurationInspection.RecordPath,bytes);
    }
    private static void Atomic(string path,byte[] bytes)
    {
        if (File.Exists(path) && (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0) throw new IOException("恢复记录为链接，已停止");
        var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        using (var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { file.Write(bytes);file.Flush(true); }
        File.Move(temp,path,true);
    }
}
