using System.Runtime.InteropServices;
using System.Text.Json;
using DeltaNFD.Inspection;
using DeltaNFD.Services.TweakDb;
using Microsoft.Win32;

namespace DeltaNFD.Services;

internal interface IMouseSettingsApi
{
    MouseParameters Read();
    bool Set(MouseParameters value, bool persist);
}

internal interface IMouseRegistryApi
{
    BxBackupEntry Read(string name);
    void Restore(BxBackupEntry entry);
}

/// <summary>One native write path for basic/deep apply and restore; readback failures retain evidence.</summary>
internal sealed class MouseSettingsWriter(IMouseSettingsApi api)
{
    internal sealed class NativeApi : IMouseSettingsApi
    {
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetMouse(uint action, uint param, int[] values, uint flags);
        public MouseParameters Read() => SystemSwitchInspection.ReadMouse();
        public bool Set(MouseParameters value, bool persist) => SetMouse(4, 0,
            [value.Threshold1, value.Threshold2, value.Acceleration], persist ? 3u : 0u);
    }

    internal OperationResult Apply(MouseParameters desired, Action<MouseParameters>? backup = null, bool persist = true,
        Action? rollbackProfile = null)
    {
        MouseParameters original;
        try
        {
            if (!desired.Valid) return OperationResult.Fail("鼠标参数无效，未修改。");
            original = api.Read();
            if (!original.Valid) return OperationResult.Fail("无法确认鼠标原始运行参数，未修改。");
            backup?.Invoke(original); // Must finish saving originals before the first native write.
        }
        catch (Exception ex) { return OperationResult.Fail("鼠标原值读取或备份失败，未修改：" + ex.Message); }
        try
        {
            if (!api.Set(desired, persist) || api.Read() != desired)
                throw new IOException("Windows 鼠标参数写入或回读不一致。");
            return OperationResult.Ok("鼠标参数已立即生效并通过运行状态回读。");
        }
        catch (Exception ex)
        {
            bool restored;
            try { restored = api.Set(original, persist) && api.Read() == original; if (restored) rollbackProfile?.Invoke(); }
            catch { restored = false; }
            return new OperationResult { Success = false, IsPartial = !restored, OutcomeUnknown = !restored,
                Message = ex.Message + (restored ? " 已恢复本次操作前的参数，备份保留。" : " 还原无法确认，备份保留，请核对鼠标设置。") };
        }
    }
}

internal static class MouseSettingsMaintenance
{
    private sealed class NativeRegistry : IMouseRegistryApi
    {
        public BxBackupEntry Read(string name) => BxService.CaptureRegEntry(Registry.CurrentUser, MousePath, name);
        public void Restore(BxBackupEntry entry) => BxService.RestoreRegEntry(entry);
    }
    internal const string MousePath = @"Control Panel\Mouse";
    private const string RuntimeKey = "MouseAcceleration";
    private const string RuntimeValue = "Runtime";
    private const string BxRuntimeId = @"MouseRuntime|HKCU|Control Panel\Mouse";
    private static readonly string[] Names = ["MouseSpeed", "MouseThreshold1", "MouseThreshold2"];

    internal static bool IsMouseBackup(BxBackupEntry entry) => entry.Id == BxRuntimeId || entry.Kind == "Reg"
        && entry.KeyPath.Equals(MousePath, StringComparison.OrdinalIgnoreCase) && Names.Contains(entry.ValueName)
        && entry.Id.Split('|').ElementAtOrDefault(1) is { } hive
        && (hive.Equals("HKCU", StringComparison.OrdinalIgnoreCase) || hive.Equals("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase));

    private static IDisposable Enter()
    {
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AppDataPaths.Root.ToUpperInvariant())));
        var mutex = new Mutex(false, @"Global\DeltaNFD.MouseSettings." + digest);
        try
        {
            bool acquired;
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("鼠标设置正在处理，请稍后重试。");
            return new Lease(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }
    private sealed class Lease(Mutex mutex) : IDisposable
    { public void Dispose() { try { mutex.ReleaseMutex(); } finally { mutex.Dispose(); } } }

    internal static OperationResult ApplyBx(bool disable, IMouseSettingsApi? api = null, IMouseRegistryApi? registry = null)
    {
        registry ??= new NativeRegistry();
        using var lease = Enter();
        List<BxBackupEntry>? before = null;
        return new MouseSettingsWriter(api ?? new MouseSettingsWriter.NativeApi()).Apply(
            disable ? new(0, 0, 0) : new(6, 10, 1), original =>
            {
                if (!BxBackupStore.TryReadAll(out var existing, out var error)) throw new IOException(error);
                before = Names.Select(registry.Read).ToList();
                var mouse = existing.Where(IsMouseBackup).ToList();
                var runtime = mouse.FirstOrDefault(e => e.Id == BxRuntimeId);
                var entries = before.ToList();
                if (runtime is not null) { original = Parse(runtime.Data); RequireTrio(mouse); }
                else if (mouse.Count > 0) original = ReadLegacy(mouse);
                else
                {
                    var cross = DeepRegistryEntries(TweakBackupStore.Default.GetAllStrict());
                    var crossRuntime = TweakBackupStore.Default.GetStrict("State", RuntimeKey, RuntimeValue);
                    if (crossRuntime is not null || cross.Count > 0)
                    {
                        RequireTrio(cross);
                        original = crossRuntime is null ? ReadLegacy(cross) : Parse(crossRuntime.Data);
                        entries = cross.Select(e => new BxBackupEntry { Kind = "Reg", Id = "Reg|HKEY_CURRENT_USER|" + MousePath + "|" + e.ValueName,
                            KeyPath = MousePath, ValueName = e.ValueName, ValueKind = e.ValueKind, Data = e.Data }).ToList();
                    }
                }
                entries.Add(new BxBackupEntry { Id = BxRuntimeId, Kind = "MouseRuntime", Data = JsonSerializer.Serialize(original) });
                BxBackupStore.SaveMany(entries);
            }, rollbackProfile: () => RestoreRegistry(before!, registry));
    }

    internal static OperationResult RestoreBx(IReadOnlyList<BxBackupEntry> entries, IMouseSettingsApi? api = null, IMouseRegistryApi? registry = null)
    {
        registry ??= new NativeRegistry();
        using var lease = Enter();
        var mouse = entries.Where(IsMouseBackup).ToList();
        var runtime = mouse.FirstOrDefault(e => e.Id == BxRuntimeId);
        var original = runtime is null ? ReadLegacy(mouse) : Parse(runtime.Data);
        var result = new MouseSettingsWriter(api ?? new MouseSettingsWriter.NativeApi()).Apply(original, persist: false);
        if (!result.Success) return result;
        try
        {
            foreach (var entry in mouse.Where(e => e.Kind == "Reg"))
            {
                registry.Restore(entry);
                VerifyRegistry(entry, registry);
            }
            return OperationResult.Ok("已恢复鼠标原始运行参数及登记值，立即生效并回读通过。");
        }
        catch (Exception ex) { return new OperationResult { Success = false, IsPartial = true, OutcomeUnknown = true,
            Message = "鼠标运行参数已恢复，登记值恢复未完成，备份保留：" + ex.Message }; }
    }

    internal static OperationResult ApplyDeep(TweakBackupStore store, IMouseSettingsApi? api = null, IMouseRegistryApi? registry = null)
    {
        registry ??= new NativeRegistry();
        using var lease = Enter();
        List<BxBackupEntry>? before = null;
        return new MouseSettingsWriter(api ?? new MouseSettingsWriter.NativeApi()).Apply(new(0, 0, 0), original =>
        {
            var existing = store.GetAllStrict();
            before = Names.Select(registry.Read).ToList();
            var capture = before;
            var old = DeepRegistryEntries(existing);
            var runtime = store.GetStrict("State", RuntimeKey, RuntimeValue);
            if (runtime is not null) { original = Parse(runtime.Data); RequireTrio(old); }
            else if (old.Count > 0) original = ReadLegacy(old);
            else
            {
                if (!BxBackupStore.TryReadAll(out var crossEntries, out var error)) throw new IOException(error);
                var cross = crossEntries.Where(IsMouseBackup).ToList();
                if (cross.Count > 0)
                {
                    RequireTrio(cross);
                    original = cross.FirstOrDefault(e => e.Id == BxRuntimeId) is { } crossRuntime ? Parse(crossRuntime.Data) : ReadLegacy(cross);
                    capture = cross.Where(e => e.Kind == "Reg").ToList();
                }
            }
            store.SaveStrict(new RegistryValueBackup { Hive = "State", KeyPath = RuntimeKey, ValueName = RuntimeValue,
                ValueKind = RegistryValueKind.String, Data = JsonSerializer.Serialize(original), CreatedAt = DateTimeOffset.Now });
            foreach (var name in Names)
            {
                var value = capture.Single(e => e.ValueName == name);
                store.SaveStrict(new RegistryValueBackup { Hive = "HKCU", KeyPath = MousePath, ValueName = name,
                    ValueKind = value.ValueKind == 0 ? RegistryValueKind.None : (RegistryValueKind)value.ValueKind,
                    Data = value.Data ?? "", CreatedAt = DateTimeOffset.Now });
            }
        }, rollbackProfile: () => RestoreRegistry(before!, registry));
    }

    internal static OperationResult RestoreDeep(TweakBackupStore store, IMouseSettingsApi? api = null, IMouseRegistryApi? registry = null)
    {
        registry ??= new NativeRegistry();
        using var lease = Enter();
        var existing = store.GetAllStrict();
        var registryEntries = DeepRegistryEntries(existing);
        var runtime = store.GetStrict("State", RuntimeKey, RuntimeValue);
        if (registryEntries.Count == 0 && runtime is null) return OperationResult.Fail("鼠标加速没有本工具的备份记录。");
        var original = runtime is null ? ReadLegacy(registryEntries) : Parse(runtime.Data);
        var result = new MouseSettingsWriter(api ?? new MouseSettingsWriter.NativeApi()).Apply(original, persist: false);
        if (!result.Success) return result;
        try
        {
            foreach (var entry in registryEntries) { registry.Restore(entry); VerifyRegistry(entry, registry); }
            foreach (var entry in registryEntries) store.RemoveStrict("HKCU", MousePath, entry.ValueName);
            store.RemoveStrict("State", RuntimeKey, RuntimeValue);
            return OperationResult.Ok("已恢复鼠标原设置，立即生效并通过回读。");
        }
        catch (Exception ex) { return new OperationResult { Success = false, IsPartial = true, OutcomeUnknown = true,
            Message = "鼠标恢复未完成，备份保留：" + ex.Message }; }
    }

    private static List<BxBackupEntry> DeepRegistryEntries(IEnumerable<RegistryValueBackup> entries) => entries
        .Where(e => e.Hive == "HKCU" && e.KeyPath.Equals(MousePath, StringComparison.OrdinalIgnoreCase) && Names.Contains(e.ValueName))
        .Select(e => new BxBackupEntry { Kind = "Reg", Id = "Reg|HKCU|" + MousePath + "|" + e.ValueName,
            KeyPath = MousePath, ValueName = e.ValueName, ValueKind = e.ValueKind == RegistryValueKind.None ? 0 : (int)e.ValueKind, Data = e.Data }).ToList();

    internal static MouseParameters ReadLegacy(IReadOnlyList<BxBackupEntry> entries)
    {
        int Value(string name)
        {
            var entry = entries.SingleOrDefault(e => e.Kind == "Reg" && e.ValueName == name);
            if (entry is null || entry.ValueKind != (int)RegistryValueKind.String || !int.TryParse(entry.Data, out var value))
                throw new InvalidDataException("旧鼠标备份不完整或损坏，未猜测默认值，请核对后恢复。");
            return value;
        }
        var result = new MouseParameters(Value("MouseThreshold1"), Value("MouseThreshold2"), Value("MouseSpeed"));
        if (!result.Valid) throw new InvalidDataException("旧鼠标备份参数无效。");
        return result;
    }
    private static MouseParameters Parse(string? data)
    {
        var result = JsonSerializer.Deserialize<MouseParameters>(data ?? "");
        return result is { Valid: true } ? result : throw new InvalidDataException("鼠标原始运行参数备份无效。");
    }
    private static void RequireTrio(IReadOnlyList<BxBackupEntry> entries)
    {
        if (Names.Any(name => entries.Count(e => e.Kind == "Reg" && e.ValueName == name) != 1))
            throw new InvalidDataException("鼠标备份缺少完整登记值，未继续修改，请先核对恢复。");
    }
    private static void RestoreRegistry(IEnumerable<BxBackupEntry> entries, IMouseRegistryApi registry)
    { foreach (var entry in entries) { registry.Restore(entry); VerifyRegistry(entry, registry); } }
    private static void VerifyRegistry(BxBackupEntry entry, IMouseRegistryApi registry)
    {
        var actual = registry.Read(entry.ValueName);
        if (actual.ValueKind != entry.ValueKind || actual.Data != entry.Data && !(entry.ValueKind == 0 && string.IsNullOrEmpty(entry.Data)))
            throw new IOException("鼠标登记值回读不一致：" + entry.ValueName);
    }
}
