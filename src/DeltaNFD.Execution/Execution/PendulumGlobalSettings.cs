using System.Text.Json;
using Microsoft.Win32;
using DeltaNFD.Inspection;

namespace DeltaNFD.Services;

internal interface IPendulumGlobalEnvironment
{
    string? Blocker();
    PendulumGlobalStatus Read();
    PendulumRegistryValue ReadValue();
    PendulumGlobalRecord? ReadRecord();
    void Backup(PendulumRegistryValue original);
    void Save(PendulumGlobalRecord record);
    void Write(bool enabled);
}

internal static class PendulumGlobalSettingsEngine
{
    internal static OperationResult Configure(IPendulumGlobalEnvironment env, PendulumGlobalArgs args)
    {
        var wrote = false;
        try
        {
            if (env.Blocker() is { } blocked) return OperationResult.Fail(blocked);
            var status = env.Read();
            if (status.ExperimentalWindows10)
                return OperationResult.Fail("Windows 10 实验路径通过实测确认能力，不提供 Win11 全局精度注册表设置。");
            if (!status.Supported || !status.Readable) return OperationResult.Fail(status.Message);
            if (args.Fingerprint.Length == 0 || args.Fingerprint != status.Fingerprint)
                return OperationResult.Fail("全局精度配置已变化，请刷新后重新确认。");
            var original = env.ReadValue();
            var old = env.ReadRecord();
            var record = new PendulumGlobalRecord { Id = old?.Id ?? Guid.NewGuid().ToString("N"),
                CreatedUtc = old?.CreatedUtc ?? DateTimeOffset.UtcNow, Original = old?.Original ?? original,
                WrittenBootId = status.BootId, TargetEnabled = args.Enabled };
            env.Backup(record.Original);
            env.Save(record); // Must be durable before the fixed registry write.
            if (env.Blocker() is { } guard) return OperationResult.Fail(guard);
            if (env.ReadValue() != original) return OperationResult.Fail("配置发生外部变化，未写入；操作记录保留。");
            if (!original.Exists || (original.Value == 1) != args.Enabled)
            {
                wrote = true;
                env.Write(args.Enabled);
            }
            var after = env.ReadValue();
            if (!after.Exists || after.Value != (args.Enabled ? 1 : 0))
                return new() { OutcomeUnknown = true, IsPartial = wrote, RequiresReboot = wrote, Message = "全局精度配置回读未确认，记录保留，请重新核对。" };
            record.Completed = true;
            env.Save(record);
            return OperationResult.Ok(args.Enabled ? "全局生效精度已开启并登记；请手动重启电脑后使用钟摆计划。"
                : "全局生效精度已关闭；钟摆计划已停止准入，请手动重启电脑使系统设置生效。", requiresReboot: true);
        }
        catch (Exception ex) { return new() { IsPartial = wrote, OutcomeUnknown = wrote, RequiresReboot = wrote,
            Message = "全局精度设置未完成：" + ex.Message + "；已有备份与记录继续保留。" }; }
    }
}

public static class PendulumGlobalSettings
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static async Task<OperationResult> ConfigureAsync(PendulumGlobalArgs args, CancellationToken token)
    {
        if (args.Enabled && !ReleaseFeaturePolicy.PendulumEnabled)
            return OperationResult.Fail("当前公测版本未开放钟摆计划。");
        if (!await Gate.WaitAsync(0, token)) return OperationResult.Fail("另一个全局精度设置正在执行。");
        try { return await Task.Run(() => PendulumGlobalSettingsEngine.Configure(new WindowsPendulumGlobalEnvironment(), args), token); }
        finally { Gate.Release(); }
    }
}

internal sealed class WindowsPendulumGlobalEnvironment : IPendulumGlobalEnvironment
{
    public string? Blocker()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return "此设置仅用于 Windows 11；Windows 10 请使用实验兼容检测。";
        if (!ElevationHelper.IsElevated) return ElevationHelper.NotElevatedMessage;
        if (OfflineModeGuard.BlocksNormalAutomation) return "恢复流程中，暂不能修改全局精度设置。";
        try { return DeltaForceLocator.IsGameRunning() ? "请退出三角洲后再修改全局精度设置。" : null; }
        catch { return "无法确认游戏状态，未修改全局精度设置。"; }
    }
    public PendulumGlobalStatus Read() => PendulumGlobalInspection.Read();
    public PendulumRegistryValue ReadValue() => PendulumGlobalInspection.ReadValue();
    public PendulumGlobalRecord? ReadRecord() => PendulumGlobalInspection.ReadRecord();
    public void Backup(PendulumRegistryValue original) => TweakBackupStore.Default.SaveStrict(new()
    {
        KeyPath = PendulumGlobalInspection.KeyPath, ValueName = PendulumGlobalInspection.ValueName,
        ValueKind = original.Exists ? RegistryValueKind.DWord : RegistryValueKind.None,
        Data = original.Exists ? original.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "",
        CreatedAt = DateTimeOffset.UtcNow,
    });
    public void Write(bool enabled)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(PendulumGlobalInspection.KeyPath, writable: true)
            ?? throw new IOException("无法写入固定的全局精度配置键。");
        key.SetValue(PendulumGlobalInspection.ValueName, enabled ? 1 : 0, RegistryValueKind.DWord);
        key.Flush();
    }
    public void Save(PendulumGlobalRecord record)
    {
        Directory.CreateDirectory(AppDataPaths.Root);
        var path = PendulumGlobalInspection.RecordPath;
        if ((File.GetAttributes(AppDataPaths.Root) & FileAttributes.ReparsePoint) != 0
            || File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("全局精度记录目录或文件含链接。");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, record); stream.Flush(true); }
        File.Move(temp, path, true);
    }
}
