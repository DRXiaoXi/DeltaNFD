using System.Text.Json;
using Microsoft.Win32;

namespace DeltaNFD.Services;

internal sealed record MemoryServiceOriginal(int Start, bool Running, int? Delayed);
internal interface IMemorySupportIo
{
    string? Blocker();
    MemoryServiceOriginal ReadSysMain();
    Task<int> ConfigureSysMainAsync(int start, bool delayed);
    Task<int> SetSysMainRunningAsync(bool running);
    void RestoreDelayed(int? value);
    Task<bool?> ReadMmAsync(string property);
    Task<int> EnableMmAsync(string property);
}
internal sealed class MemorySupportControl(TweakBackupStore store, IMemorySupportIo io)
{
    private bool _writeAttempted;
    private static OperationResult Partial(string text) => new() { Success = false,
        Message = text + "；原始备份保留，请核对后再继续。", OutcomeUnknown = true, RequiresReboot = true };
    private void Check() { if (io.Blocker() is { } message) throw new InvalidOperationException(message); }
    private static MemoryServiceOriginal ParseOriginal(RegistryValueBackup entry)
    {
        if (entry.ValueKind != RegistryValueKind.String) throw new InvalidDataException("SysMain 备份类型无效。");
        using var doc = JsonDocument.Parse(entry.Data);
        foreach (var key in new[] { "Start", "Running", "Delayed" })
            if (!doc.RootElement.TryGetProperty(key, out _)) throw new InvalidDataException("SysMain 备份不完整。");
        var original = JsonSerializer.Deserialize<MemoryServiceOriginal>(entry.Data);
        if (original is null || original.Start is not (2 or 3 or 4) || original.Delayed is not (null or 0 or 1))
            throw new InvalidDataException("SysMain 备份值无效。");
        return original;
    }
    internal async Task<OperationResult> EnableAsync(LowMemoryAction action)
    {
        _writeAttempted = false;
        try { return await EnableCoreAsync(action); }
        catch (Exception ex) { return _writeAttempted ? Partial(ex.Message) : OperationResult.Fail("未修改：" + ex.Message); }
    }
    private async Task<OperationResult> EnableCoreAsync(LowMemoryAction action)
    {
        Check();
        if (action == LowMemoryAction.SysMain)
        {
            var original = io.ReadSysMain();
            if (original.Start is not (2 or 3 or 4) || original.Delayed is not (null or 0 or 1))
                return OperationResult.Fail("SysMain 原始状态无法完整确认。");
            store.SaveStrict(new() { Hive = "State", KeyPath = "LowMemory", ValueName = "SysMain",
                ValueKind = RegistryValueKind.String, Data = JsonSerializer.Serialize(original), CreatedAt = DateTimeOffset.Now });
            try { _ = ParseOriginal(store.GetStrict("State", "LowMemory", "SysMain") ?? throw new InvalidDataException()); }
            catch { return OperationResult.Fail("已有 SysMain 备份无法核验，未修改。"); }
            try
            {
                Check();
                _writeAttempted = true;
                if (await io.ConfigureSysMainAsync(2, original.Delayed == 1) != 0) return Partial("SysMain 启动配置未完成");
                Check();
                if (await io.SetSysMainRunningAsync(true) != 0) return Partial("SysMain 启动未完成");
                var actual = io.ReadSysMain();
                if (actual.Start != 2 || !actual.Running) return Partial("SysMain 复检未通过");
                return OperationResult.Ok("SysMain 已设为自动启动并确认运行。", requiresReboot: true);
            }
            catch (Exception ex) { return _writeAttempted ? Partial(ex.Message) : OperationResult.Fail("未修改：" + ex.Message); }
        }
        var property = action switch { LowMemoryAction.MemoryCompression => "MemoryCompression", LowMemoryAction.PageCombining => "PageCombining", _ => "" };
        if (property.Length == 0) return OperationResult.Fail("该操作不在内存启用范围内。");
        var before = await io.ReadMmAsync(property);
        if (before is null) return OperationResult.Fail("无法读取 " + property + " 原状态，未修改。");
        if (before == true) return OperationResult.Ok("该项已开启。");
        store.SaveStrict(new() { Hive = "State", KeyPath = "MMAgent", ValueName = property,
            ValueKind = RegistryValueKind.String, Data = before.Value ? "1" : "0", CreatedAt = DateTimeOffset.Now });
        if (store.GetStrict("State", "MMAgent", property) is not { ValueKind: RegistryValueKind.String, Data: "0" or "1" })
            return OperationResult.Fail("已有内存功能备份无法核验，未修改。");
        try
        {
            Check();
            _writeAttempted = true;
            if (await io.EnableMmAsync(property) != 0 || await io.ReadMmAsync(property) != true)
                return Partial(property + " 启用或复检未完成");
            return OperationResult.Ok(property + " 已启用并复检。", requiresReboot: true);
        }
        catch (Exception ex) { return _writeAttempted ? Partial(ex.Message) : OperationResult.Fail("未修改：" + ex.Message); }
    }
    internal async Task<OperationResult> RestoreSysMainAsync()
    {
        Check();
        var entry = store.Get("State", "LowMemory", "SysMain");
        if (entry is null) return OperationResult.Fail("无 SysMain 原始状态备份。");
        MemoryServiceOriginal? original;
        try
        {
            original = ParseOriginal(entry);
        }
        catch { return OperationResult.Fail("SysMain 备份无法核验，原记录保留。"); }
        if (original is null || original.Start is not (2 or 3 or 4) || original.Delayed is not (null or 0 or 1))
            return OperationResult.Fail("SysMain 备份值无效，原记录保留。");
        try
        {
            Check();
            if (await io.SetSysMainRunningAsync(original.Running) != 0) return Partial("SysMain 原运行状态未恢复");
            Check();
            if (await io.ConfigureSysMainAsync(original.Start, original.Delayed == 1) != 0) return Partial("SysMain 原启动方式未恢复");
            Check(); io.RestoreDelayed(original.Delayed);
            if (io.ReadSysMain() != original) return Partial("SysMain 原配置复检未通过");
            if (store.GetStrict("State", "LowMemory", "SysMain")?.Data != entry.Data) return Partial("SysMain 原备份已变化");
            store.RemoveStrict("State", "LowMemory", "SysMain");
            return OperationResult.Ok("已按原始备份恢复 SysMain，重启后复查。", requiresReboot: true);
        }
        catch (Exception ex) { return Partial(ex.Message); }
    }
}
