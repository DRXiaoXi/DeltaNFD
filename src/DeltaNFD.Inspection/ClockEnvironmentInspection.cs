using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using DeltaNFD.Services;

namespace DeltaNFD.Inspection;

/// <summary>Only queries BCD and present HPET devnodes. No writes, device toggles or automatic repairs.</summary>
public static class ClockEnvironmentInspection
{
    public static Task<ClockEnvironmentStatus> ReadAsync() => InspectionScheduler.Run(() => Read());
    public static ClockEnvironmentStatus Read(bool backupsOnly = false)
    {
        try
        {
            var store = ClockEnvironmentStore.Default;
            var records = store.Read();
            if (backupsOnly && records.Count == 0) return new() { RecordsReadable = true, Message = "没有本功能的原始备份。" };
            return Evaluate(ReadSystem(), records, store.Sid);
        }
        catch (Exception ex) { return new() { Incomplete = true, Message = "计时源或原始记录待核对：" + ex.Message }; }
    }
    public static ClockEnvironmentSystem ReadSystem()
    {
        ClockBcdState? bcd = null; IReadOnlyList<HpetDeviceState> devices = []; var devicesReadable = false; var errors = new List<string>();
        try { bcd = ReadBcd(); } catch (Exception ex) { errors.Add("启动计时源读取失败：" + ex.Message); }
        try { devices = ReadDevices(); devicesReadable = true; } catch (Exception ex) { errors.Add("HPET 设备读取失败：" + ex.Message); }
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return new(ClockEnvironmentStore.MachineId(), key?.GetValue("VendorIdentifier") as string ?? "", PendulumGlobalInspection.CurrentBootId,
            bcd, devices, devicesReadable, string.Join("；", errors));
    }
    internal static ClockEnvironmentStatus Evaluate(ClockEnvironmentSystem system, IReadOnlyList<ClockEnvironmentBackup> records, string sid)
    {
        var source = records.SingleOrDefault(r => r.Kind == "source"); var hpet = records.SingleOrDefault(r => r.Kind == "hpet");
        var incomplete = records.Any(r => !r.Settled);
        var pending = records.Any(r => r.Attempted && (system.BootId.Length == 0 || r.BootId == system.BootId));
        var amd = system.CpuVendor == "AuthenticAMD" && system.BootId.Length > 0;
        var devicesDisabled = system.DevicesReadable && system.Devices.Count > 0 && system.Devices.All(d => d.Disabled);
        bool CanRestore(ClockEnvironmentBackup? r) => r is { Restored: false } && r.Sid == sid
            && r.Machine == system.Machine && RecordMatches(system, r, allowOriginal: true);
        var mismatches = records.Any(r => r.Settled && !r.Restored && !RecordMatches(system, r, allowOriginal: false));
        var revision = records.Count == 0 ? "" : ClockEnvironmentPolicy.Digest(records.Select(r => r.Digest).ToArray());
        var message = incomplete ? "上次操作未完成，请按原始备份恢复或人工核对；不会自动重放操作。"
            : mismatches ? "当前状态与记录不符，钟摆验证已停止准入，请核对原始备份。"
            : pending ? "设置已回读；请手动重启电脑，再重新扫描或验证钟摆精度。"
            : !amd ? "本项应用仅开放 AMD CPU 平台；已有匹配备份仍可恢复。"
            : devicesDisabled ? "HPET 设备已禁用；本方案不修改 BCD 启动计时源，动态节拍保持原样。"
            : !system.DevicesReadable ? system.Error
            : "HPET 设备已读取；禁用操作只修改设备状态，BCD 启动计时源和动态节拍保持原样。";
        if (system.Error.Length > 0 && system.DevicesReadable) message += "\n" + system.Error;
        var result = new ClockEnvironmentStatus
        {
            System = system, RecordsReadable = true, SourceBackup = source is { Restored: false }, HpetBackup = hpet is { Restored: false },
            PendingRestart = pending, Incomplete = incomplete || mismatches, Revision = revision, Message = message,
            CanDefaultSource = amd && !incomplete && !mismatches && !pending && source is not { Restored: false } && hpet is not { Restored: false }
                && system.Bcd is { InheritedOverride: false } b && (b.Clock.Length > 0 || b.Tick.Length > 0),
            CanDisableHpet = amd && !incomplete && !mismatches && !pending && hpet is not { Restored: false } && system.DevicesReadable
                && system.Devices.Count > 0 && system.Devices.All(d => d.Disabled || d.CanDisable) && !devicesDisabled,
            CanRestoreSource = hpet is not { Restored: false } && CanRestore(source), CanRestoreHpet = CanRestore(hpet),
        };
        return result with { Fingerprint = ClockEnvironmentPolicy.Digest(new { system, revision }) };
    }
    internal static bool RecordMatches(ClockEnvironmentSystem system, ClockEnvironmentBackup record, bool allowOriginal)
    {
        if (system.Machine != record.Machine) return false;
        if (record.HasSource && (system.Bcd is null || system.Bcd.EntryId != record.EntryId || system.Bcd.Inheritance != record.Inheritance
            || record.Kind == "hpet" && system.Bcd.InheritedOverride)) return false;
        foreach (var step in record.Steps)
        {
            var current = Current(system, record, step);
            var expected = record.Restored ? step.Original : step.Applied;
            if (current is null || current != expected && !(allowOriginal && current == step.Original)) return false;
        }
        return true;
    }
    internal static string? Current(ClockEnvironmentSystem system, ClockEnvironmentBackup record, ClockEnvironmentStep step)
    {
        if (ClockEnvironmentPolicy.IsBcdKey(step.Key)) return record.HasSource && system.Bcd is { } b
            && b.EntryId == record.EntryId && b.Inheritance == record.Inheritance && (record.Kind != "hpet" || !b.InheritedOverride)
            ? step.Key == "useplatformclock" ? b.Clock : b.Tick : null;
        if (!system.DevicesReadable) return null;
        var device = system.Devices.SingleOrDefault(d => d.InstanceId.Equals(step.Key, StringComparison.OrdinalIgnoreCase));
        return device is null || device.Identity != step.Identity ? null : device.Problem == 22 ? "disabled" : device.Problem == 0 ? "enabled" : null;
    }
    private static readonly object CacheGate = new();
    private static (string Revision, DateTime Utc, ClockEnvironmentStatus Status)? _cache;
    public static PendulumGlobalStatus Decorate(PendulumGlobalStatus global)
    {
        try
        {
            var store = ClockEnvironmentStore.Default; var records = store.Read(); if (records.Count == 0) return global;
            var revision = ClockEnvironmentPolicy.Digest(records.Select(r => r.Digest).ToArray()); ClockEnvironmentStatus status;
            lock (CacheGate)
            {
                if (_cache is { } old && old.Revision == revision && DateTime.UtcNow - old.Utc < TimeSpan.FromSeconds(4)) status = old.Status;
                else { status = Evaluate(ReadSystem(), records, store.Sid); _cache = (revision, DateTime.UtcNow, status); }
            }
            var blocked = status.PendingRestart || status.Incomplete || status.System?.BootId.Length == 0
                || status.System?.Bcd is null && records.Any(r => r.HasSource && !r.Restored);
            return global with { ClockEnvironmentRevision = revision, PendingRecovery = global.PendingRecovery || blocked,
                Message = blocked ? status.Message : global.Message };
        }
        catch (Exception ex) { return global with { PendingRecovery = true, Message = "计时源修改记录待核对：" + ex.Message }; }
    }
    internal sealed record ParsedBcd(string Id, string Clock, string Tick, IReadOnlyList<string> Inherits);
    internal static ParsedBcd ParseBcd(string output)
    {
        if (output.Length > 128 * 1024) throw new IOException("BCD 输出超过上限。");
        const string guid = @"\{[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\}";
        var match = Regex.Match(output, guid); if (!match.Success) throw new IOException("未读取到明确的启动项身份。");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var inherits = new List<string>(); var inheritanceLine = false;
        foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim(); var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            if (parts[0].Equals("inherit", StringComparison.OrdinalIgnoreCase)) inheritanceLine = true;
            else if (!Regex.IsMatch(line, "^" + guid + "$")) inheritanceLine = false;
            if (inheritanceLine) inherits.AddRange(Regex.Matches(line, guid).Select(m => m.Value.ToLowerInvariant()));
            if (parts[0] is not ("useplatformclock" or "useplatformtick")) continue;
            if (parts.Length != 2 || values.ContainsKey(parts[0])) throw new IOException("BCD 覆盖重复或格式异常。");
            var normalized = parts[1].ToLowerInvariant() switch { "yes" or "true" or "1" or "是" => "yes", "no" or "false" or "0" or "否" => "no", _ => throw new IOException("BCD 覆盖值无法识别。") };
            values.Add(parts[0], normalized);
        }
        return new(match.Value.ToLowerInvariant(), values.GetValueOrDefault("useplatformclock", ""), values.GetValueOrDefault("useplatformtick", ""), inherits.Distinct().ToArray());
    }
    internal static ClockBcdState ReadBcd(Func<string, string>? query = null)
    {
        query ??= id => QueryBcd(["/enum", id, "/v"]);
        var current = ParseBcd(query("{current}")); var pending = new Queue<string>(current.Inherits);
        var inherited = new Dictionary<string, ParsedBcd>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryDequeue(out var id))
        {
            if (id == current.Id) throw new IOException("启动项继承存在循环。");
            if (inherited.ContainsKey(id)) continue;
            if (inherited.Count >= 12) throw new IOException("启动项继承超过核对上限。");
            var node = ParseBcd(query(id)); if (node.Id != id) throw new IOException("继承启动项身份不匹配。");
            inherited.Add(id, node); foreach (var parent in node.Inherits) pending.Enqueue(parent);
        }
        return new(current.Id, current.Clock, current.Tick, ClockEnvironmentPolicy.Digest(inherited.OrderBy(p => p.Key).ToArray()),
            inherited.Values.Any(v => v.Clock.Length > 0 || v.Tick.Length > 0));
    }
    internal static string QueryBcd(IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "bcdedit.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("未能启动系统 BCD 工具。");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000)) { try { process.Kill(); } catch { } throw new IOException("BCD 工具执行超时，结果待核对。"); }
        if (!Task.WaitAll([output, error], TimeSpan.FromSeconds(3))) throw new IOException("BCD 输出读取超时。");
        if (process.ExitCode != 0) throw new IOException("BCD 工具退出码 " + process.ExitCode + "：" + error.Result.Trim());
        return output.Result;
    }
    internal static IReadOnlyList<HpetDeviceState> ReadDevices()
    {
        const uint present = 0x100; string[]? ids = null;
        for (var retry = 0; retry < 3; retry++)
        {
            NativeOk(CM_Get_Device_ID_List_SizeW(out var size, null, present));
            if (size is < 1 or > 1048576) throw new IOException("设备清单大小异常。");
            var chars = new char[size]; var code = CM_Get_Device_ID_ListW(null, chars, size, present);
            if (code == 0x1a) continue; NativeOk(code);
            ids = new string(chars).Split('\0', StringSplitOptions.RemoveEmptyEntries); break;
        }
        if (ids is null) throw new IOException("设备列表持续变化，未提供修改入口。");
        var result = ids.Where(ClockEnvironmentPolicy.ValidDevice).Select(ReadDevice).ToArray();
        if (result.Length > 16 || result.Select(d => d.InstanceId.ToUpperInvariant()).Distinct().Count() != result.Length)
            throw new IOException("HPET 设备数量异常或重复。");
        return result;
    }
    internal static HpetDeviceState ReadDevice(string id)
    {
        if (!ClockEnvironmentPolicy.ValidDevice(id)) throw new IOException("设备不在 HPET 固定范围。");
        NativeOk(CM_Locate_DevNodeW(out var node, id, 0));
        var hardware = new byte[8192]; uint length = (uint)hardware.Length;
        NativeOk(CM_Get_DevNode_Registry_PropertyW(node, 2, out var type, hardware, ref length, 0));
        if (type != 7 || length > hardware.Length || length % 2 != 0) throw new IOException("HPET 硬件标识格式异常。");
        var values = Encoding.Unicode.GetString(hardware, 0, (int)length).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (!values.Contains(@"ACPI\PNP0103", StringComparer.OrdinalIgnoreCase)) throw new IOException("HPET 硬件标识未通过交叉核对。");
        NativeOk(CM_Get_DevNode_Status(out var status, out var problem, node, 0));
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + id) ?? throw new IOException("设备身份登记不可读。");
        var identity = ClockEnvironmentPolicy.Digest(new { Id = id.ToUpperInvariant(), Hardware = values.Select(v => v.ToUpperInvariant()).Order().ToArray(),
            Class = key.GetValue("ClassGUID") as string ?? "", Service = key.GetValue("Service") as string ?? "", Driver = key.GetValue("Driver") as string ?? "" });
        return new(id, identity, "高精度事件计时器", status, problem);
    }
    private static void NativeOk(uint code) { if (code != 0) throw new IOException("设备接口返回 0x" + code.ToString("X")); }
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_ID_List_SizeW(out uint size, string? filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_ID_ListW(string? filter, [Out] char[] buffer, uint length, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] internal static extern uint CM_Locate_DevNodeW(out uint node, string id, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_DevNode_Registry_PropertyW(uint node, uint property, out uint type, [Out] byte[] buffer, ref uint length, uint flags);
}
