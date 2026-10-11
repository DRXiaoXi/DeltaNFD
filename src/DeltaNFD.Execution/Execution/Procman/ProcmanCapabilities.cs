using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeltaNFD.Execution.Procman;

/// <summary>单项能力的读取/写入结果（能力原语层；控制权协调在引擎层）。</summary>
public sealed record ProcmanCapabilityResult(bool Ok, string? Value, string Error)
{
    public static ProcmanCapabilityResult Success(string value) => new(true, value, "");
    public static ProcmanCapabilityResult Failure(string error) => new(false, null, error);
}

/// <summary>
/// 通用进程属性能力原语：每项能力都实现「读取 → 设置 → 回读」三步，回读不一致视为失败。
/// 只使用公开 Win32 API；掩码一律按处理器组传递（ushort 组号 + ulong 掩码），绝不退化为组 0 单掩码。
/// </summary>
public static partial class ProcmanCapabilities
{
    // ---------------- 打开句柄（按能力所需最小权限） ----------------

    // PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_LIMITED_INFORMATION：优先级/亲和性读写。
    private const uint AccessPriority = 0x3700;
    // PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_INFORMATION：EcoQoS / CPU Sets。
    private const uint AccessSetInformation = 0x3700;

    [ThreadStatic] private static SafeProcessHandle? _pinned;
    [ThreadStatic] private static int _pinnedPid;
    internal static IDisposable Pin(ProcmanProcessIdentity identity)
    {
        var handle = OpenProcess(AccessSetInformation, false, identity.Pid);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("无法打开写入句柄：" + Marshal.GetLastWin32Error()); }
        if (!ProcmanIdentity.Matches(handle, identity)) { handle.Dispose(); throw new IOException("写入句柄身份不符，已拒绝修改。"); }
        var prior = _pinned; var priorPid = _pinnedPid;
        _pinned = handle; _pinnedPid = identity.Pid;
        return new PinScope(() => { _pinned = prior; _pinnedPid = priorPid; handle.Dispose(); });
    }
    private sealed class PinScope(Action release) : IDisposable { public void Dispose() => release(); }

    private static SafeProcessHandle? TryOpen(int pid, uint access)
    {
        try
        {
            if (_pinned is not null)
            {
                if (pid != _pinnedPid) throw new InvalidOperationException("固定句柄内禁止切换目标 PID。");
                return new SafeProcessHandle(_pinned.DangerousGetHandle(), false);
            }
            var handle = OpenProcess(access, false, pid);
            if (!handle.IsInvalid) return handle;
            handle.Dispose(); return null;
        }
        catch { return null; }
    }

    // ---------------- CPU 优先级 ----------------

    public static ProcmanCapabilityResult ReadPriority(int pid)
    {
        try
        {
            using var handle = TryOpen(pid, 0x1400);
            var value = handle is null ? 0 : GetPriorityClass(handle);
            return value == 0 ? ProcmanCapabilityResult.Failure("读取优先级失败：" + Marshal.GetLastWin32Error())
                : ProcmanCapabilityResult.Success(((ProcessPriorityClass)value).ToString());
        }
        catch (Exception ex) { return ProcmanCapabilityResult.Failure("读取优先级失败：" + ex.Message); }
    }

    public static ProcmanCapabilityResult SetPriority(int pid, ProcessPriorityClass priority)
    {
        try
        {
            using var handle = TryOpen(pid, AccessPriority);
            if (handle is null || !SetPriorityClass(handle, (uint)priority)) return ProcmanCapabilityResult.Failure("设置优先级失败：" + Marshal.GetLastWin32Error());
            var actual = (ProcessPriorityClass)GetPriorityClass(handle);
            return actual == priority
                ? ProcmanCapabilityResult.Success(actual.ToString())
                : ProcmanCapabilityResult.Failure($"回读不一致：请求 {priority}，实际 {actual}。");
        }
        catch (Exception ex) { return ProcmanCapabilityResult.Failure("设置优先级失败：" + ex.Message); }
    }

    // ---------------- Windows 动态优先级提升（注意 API 反向语义：设置的是 DisableBoost） ----------------

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessPriorityBoost(SafeProcessHandle handle, out bool disableBoost);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessPriorityBoost(SafeProcessHandle handle, [MarshalAs(UnmanagedType.Bool)] bool disableBoost);

    public static ProcmanCapabilityResult ReadPriorityBoost(int pid)
    {
        var handle = TryOpen(pid, AccessPriority);
        if (handle is null) return ProcmanCapabilityResult.Failure("打开进程失败（权限不足或进程已退出）。");
        using (handle)
        {
            if (GetProcessPriorityBoost(handle, out var disableBoost))
                return ProcmanCapabilityResult.Success((!disableBoost).ToString()); // API 反向：展示为「提升是否启用」
            return ProcmanCapabilityResult.Failure("读取动态优先级提升失败：Win32 错误 " + Marshal.GetLastWin32Error());
        }
    }

    public static ProcmanCapabilityResult SetPriorityBoost(int pid, bool enableBoost)
    {
        var handle = TryOpen(pid, AccessPriority);
        if (handle is null) return ProcmanCapabilityResult.Failure("打开进程失败（权限不足或进程已退出）。");
        using (handle)
        {
            if (!SetProcessPriorityBoost(handle, !enableBoost)) // 反向参数：禁用提升 = true
                return ProcmanCapabilityResult.Failure("设置动态优先级提升失败：Win32 错误 " + Marshal.GetLastWin32Error());
        }
        return ReadPriorityBoost(pid) is { Ok: true, Value: var value } && value == enableBoost.ToString()
            ? ProcmanCapabilityResult.Success(value)
            : ProcmanCapabilityResult.Failure("回读不一致，动态优先级提升未确认生效。");
    }

    // ---------------- 硬亲和性（按处理器组） ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct GROUP_AFFINITY
    {
        public nuint Mask;
        public ushort Group;
        public ushort Reserved0;
        public ushort Reserved1;
        public ushort Reserved2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessAffinityMask(SafeProcessHandle handle, nuint mask);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessAffinityMask(SafeProcessHandle handle, out nuint processAffinityMask, out nuint systemAffinityMask);

    // GroupCount 是 PUSHORT（输入容量/输出实际数，双向）；GroupArray 是裸指针。
    // 之前误按「返回值=实际数」声明，导致本机 AV——已用最小探针程序验证 ref ushort + IntPtr 才是正确签名。
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessGroupAffinity(IntPtr process, ref ushort groupCount, IntPtr groupArray);

    /// <summary>读取硬亲和性：进程实际所属的每个处理器组的掩码。</summary>
    public static ProcmanCapabilityResult ReadAffinity(int pid)
    {
        var handle = TryOpen(pid, AccessPriority);
        if (handle is null) return ProcmanCapabilityResult.Failure("打开进程失败（权限不足或进程已退出）。");
        using (handle)
        {
            var groups = new ushort[16];
            var pinned = Marshal.AllocHGlobal(sizeof(ushort) * groups.Length);
            ushort actual;
            try
            {
                ushort count = (ushort)groups.Length;
                var raw = handle.DangerousGetHandle();
                if (!GetProcessGroupAffinity(raw, ref count, pinned)) return ProcmanCapabilityResult.Failure("读取处理器组失败：" + Marshal.GetLastWin32Error());
                actual = count;
                if (actual == 0)
                    return ProcmanCapabilityResult.Failure("读取进程处理器组失败：Win32 错误 " + Marshal.GetLastWin32Error());
                if (actual > groups.Length)
                    return ProcmanCapabilityResult.Failure("进程所属处理器组数超出预读缓冲。");
                var bytes = new byte[sizeof(ushort) * actual];
                Marshal.Copy(pinned, bytes, 0, bytes.Length);
                for (var i = 0; i < actual; i++)
                    groups[i] = BitConverter.ToUInt16(bytes, i * sizeof(ushort));
            }
            finally
            {
                Marshal.FreeHGlobal(pinned);
            }

            if (!GetProcessAffinityMask(handle, out var mask, out _))
                return ProcmanCapabilityResult.Failure("读取亲和性掩码失败：Win32 错误 " + Marshal.GetLastWin32Error());
            if (actual != 1 || mask == 0) return ProcmanCapabilityResult.Failure("多组硬亲和性无法完整回读/还原，请使用多组 CPU 集合。");
            return ProcmanCapabilityResult.Success($"g{groups[0]}:0x{(ulong)mask:X}");
        }
    }

    /// <summary>设置硬亲和性：按组掩码逐组设置（多组进程每组一次调用）。</summary>
    public static ProcmanCapabilityResult SetAffinity(int pid, IReadOnlyList<ProcmanGroupMaskValue> masks)
    {
        if (masks.Count == 0) return ProcmanCapabilityResult.Failure("亲和性掩码不能为空（还原请用解除限制语义）。");
        if (masks.Count != 1) return ProcmanCapabilityResult.Failure("硬亲和性仅开放当前单处理器组；跨组请使用 CPU 集合。");
        var currentGroup = ReadAffinity(pid);
        if (!currentGroup.Ok || !currentGroup.Value!.StartsWith($"g{masks[0].Group}:", StringComparison.Ordinal))
            return ProcmanCapabilityResult.Failure("目标组与当前组不同或无法读取，未修改。");
        if (masks.Any(m => m.Mask == 0)) return ProcmanCapabilityResult.Failure("亲和性掩码不能包含 0。");
        if (masks.Select(m => m.Group).Distinct().Count() != masks.Count) return ProcmanCapabilityResult.Failure("亲和性存在重复的处理器组。");
        var handle = TryOpen(pid, AccessPriority);
        if (handle is null) return ProcmanCapabilityResult.Failure("打开进程失败（权限不足或进程已退出）。");
        using (handle)
        {
            foreach (var mask in masks)
            {
                if (!SetProcessAffinityMask(handle, (nuint)mask.Mask))
                    return ProcmanCapabilityResult.Failure($"设置组 {mask.Group} 亲和性失败：Win32 错误 " + Marshal.GetLastWin32Error());
            }
        }
        // 回读验证（主组掩码核对）
        var verify = TryOpen(pid, AccessPriority);
        if (verify is null) return ProcmanCapabilityResult.Failure("回读失败：进程已退出。");
        using (verify)
        {
            if (!GetProcessAffinityMask(verify, out var actual, out _))
                return ProcmanCapabilityResult.Failure("回读亲和性失败。");
            var primary = masks.OrderBy(m => m.Group).First();
            if ((ulong)actual != primary.Mask)
                return ProcmanCapabilityResult.Failure($"回读不一致：请求 0x{primary.Mask:X}，实际 0x{(ulong)actual:X}。");
            return ProcmanCapabilityResult.Success(string.Join(";", masks.Select(m => $"g{m.Group}:0x{m.Mask:X}")));
        }
    }

    /// <summary>解除硬亲和性限制 = 恢复该进程可见的全部处理器。</summary>
    public static ProcmanCapabilityResult ClearAffinity(int pid)
    {
        var system = ProcmanTopology.SystemGroupMasks();
        if (system.Count == 0) return ProcmanCapabilityResult.Failure("读取系统处理器组失败。");
        var original = ReadAffinity(pid);
        if (!original.Ok) return original;
        var group = system.Where(g => original.Value!.StartsWith($"g{g.Group}:", StringComparison.Ordinal)).ToArray();
        return SetAffinity(pid, group);
    }

    // ---------------- CPU 集合（软亲和性，Windows 11 组掩码形式） ----------------

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessDefaultCpuSetMasks(SafeProcessHandle process, [Out] GROUP_AFFINITY[]? masks, ushort maskCount, out ushort requiredCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDefaultCpuSetMasks(SafeProcessHandle process, [In] GROUP_AFFINITY[]? masks, ushort maskCount);

    /// <summary>读取进程默认 CPU 集合；成功且空字符串 = 未设置默认集合（系统默认）。</summary>
    public static ProcmanCapabilityResult ReadCpuSet(int pid)
    {
        var handle = TryOpen(pid, AccessSetInformation);
        if (handle is null) return ProcmanCapabilityResult.Failure("打开进程失败（权限不足或进程已退出）。");
        using (handle)
        {
            ushort required;
            if (!GetProcessDefaultCpuSetMasks(handle, null, 0, out required) && Marshal.GetLastWin32Error() != 122)
                return ProcmanCapabilityResult.Failure("读取 CPU 集合失败：Win32 错误 " + Marshal.GetLastWin32Error() + "（旧系统可能不支持组掩码形式）。");
            if (required == 0) return ProcmanCapabilityResult.Success("(系统默认)");
            if (required > 64) return ProcmanCapabilityResult.Failure("CPU 集合组数异常（>64）。");
            var buffer = new GROUP_AFFINITY[required];
            if (!GetProcessDefaultCpuSetMasks(handle, buffer, (ushort)buffer.Length, out var written) || written == 0)
                return ProcmanCapabilityResult.Failure("读取 CPU 集合组掩码失败。");
            return ProcmanCapabilityResult.Success(string.Join(";", buffer.Take(written).OrderBy(m => m.Group).Select(m => $"g{m.Group}:0x{(ulong)m.Mask:X}")));
        }
    }

    public static ProcmanCapabilityResult SetCpuSet(int pid, IReadOnlyList<ProcmanGroupMaskValue> masks)
    {
        if (masks.Count > 64) return ProcmanCapabilityResult.Failure("CPU 集合组数异常。");
        if (masks.Any(m => m.Mask == 0)) return ProcmanCapabilityResult.Failure("CPU 集合掩码不能包含 0。");
        if (masks.Select(m => m.Group).Distinct().Count() != masks.Count) return ProcmanCapabilityResult.Failure("CPU 集合存在重复的处理器组。");
        if (masks.Any(m => !ProcmanTopology.SystemGroupMasks().Any(g => g.Group == m.Group && (m.Mask & ~g.Mask) == 0)))
            return ProcmanCapabilityResult.Failure("CPU 集合包含不存在的组/逻辑处理器。");
        var handle = TryOpen(pid, AccessSetInformation);
        if (handle is null) return ProcmanCapabilityResult.Failure("打开进程失败（权限不足或进程已退出）。");
        using (handle)
        {
            var values = masks.Count == 0 ? null : masks.OrderBy(m => m.Group).Select(m => new GROUP_AFFINITY { Mask = (nuint)m.Mask, Group = m.Group }).ToArray();
            if (!SetProcessDefaultCpuSetMasks(handle, values, (ushort)masks.Count))
                return ProcmanCapabilityResult.Failure("设置 CPU 集合失败：Win32 错误 " + Marshal.GetLastWin32Error());
        }
        var verify = ReadCpuSet(pid);
        if (!verify.Ok) return ProcmanCapabilityResult.Failure("回读失败：" + verify.Error);
        var expected = masks.Count == 0 ? "(系统默认)" : string.Join(";", masks.OrderBy(m => m.Group).Select(m => $"g{m.Group}:0x{m.Mask:X}"));
        return verify.Value == expected
            ? verify
            : ProcmanCapabilityResult.Failure($"回读不一致：请求 {expected}，实际 {verify.Value}。");
    }

    // ---------------- EcoQoS（效率模式；掩码清零 = 交还系统控制） ----------------

    private const int ProcessInformationClassPowerThrottling = 4;
    private const uint PowerThrottlingCurrentVersion = 1;
    private const uint PowerThrottlingExecutionSpeed = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(SafeProcessHandle handle, int informationClass, ref PROCESS_POWER_THROTTLING_STATE information, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessInformation(SafeProcessHandle handle, int informationClass, ref PROCESS_POWER_THROTTLING_STATE information, int size);

    /// <summary>读取 EcoQoS 是否被本进程显式固定（ControlMask 含 EXECUTION_SPEED 位）。无法读取固定状态时如实报错。</summary>
    public static ProcmanCapabilityResult ReadEcoQos(int pid)
    {
        var handle = TryOpen(pid, AccessSetInformation);
        if (handle is null) return ProcmanCapabilityResult.Failure("打开进程失败（权限不足或进程已退出）。");
        using (handle)
        {
            var state = new PROCESS_POWER_THROTTLING_STATE { Version = 1 };
            return GetProcessInformation(handle, 4, ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>())
                ? ProcmanCapabilityResult.Success($"eco:{state.ControlMask}:{state.StateMask}")
                : ProcmanCapabilityResult.Failure("读取 EcoQoS 失败：" + Marshal.GetLastWin32Error());
        }
    }

    public static ProcmanCapabilityResult SetEcoQos(int pid, bool enabled)
    {
        var handle = TryOpen(pid, AccessSetInformation);
        if (handle is null) return ProcmanCapabilityResult.Failure("打开进程失败（权限不足或进程已退出）。");
        using (handle)
        {
            var state = new PROCESS_POWER_THROTTLING_STATE { Version = 1 };
            if (!GetProcessInformation(handle, 4, ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>()))
                return ProcmanCapabilityResult.Failure("无法读取原 EcoQoS，未修改。");
            state.ControlMask |= 1;
            state.StateMask = enabled ? state.StateMask | 1 : state.StateMask & ~1u;
            if (!SetProcessInformation(handle, ProcessInformationClassPowerThrottling, ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>()))
                return ProcmanCapabilityResult.Failure("设置效率模式失败：Win32 错误 " + Marshal.GetLastWin32Error());
        }
        var actual = ReadEcoQos(pid);
        return actual.Ok && EcoEnabled(actual.Value!) == enabled && actual.Value!.Split(':')[1] is { } control && (uint.Parse(control) & 1) != 0
            ? actual : ProcmanCapabilityResult.Failure("EcoQoS 回读未确认。");
    }

    public static bool EcoEnabled(string raw) => raw.Split(':') is ["eco", _, var state] && uint.TryParse(state, out var value) && (value & 1) != 0;
    public static ProcmanCapabilityResult RestoreEcoQos(int pid, string raw)
    {
        var parts = raw.Split(':');
        if (parts.Length != 3 || parts[0] != "eco" || !uint.TryParse(parts[1], out var control) || !uint.TryParse(parts[2], out var value))
            return ProcmanCapabilityResult.Failure("原 EcoQoS 状态不可确认，保留恢复记录。");
        using var handle = TryOpen(pid, AccessSetInformation);
        if (handle is null) return ProcmanCapabilityResult.Failure("无法打开 EcoQoS 恢复句柄。");
        var state = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = control, StateMask = value };
        if (!SetProcessInformation(handle, 4, ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>()))
            return ProcmanCapabilityResult.Failure("恢复 EcoQoS 失败：" + Marshal.GetLastWin32Error());
        var actual = ReadEcoQos(pid);
        return actual.Ok && actual.Value == raw ? actual : ProcmanCapabilityResult.Failure("EcoQoS 恢复回读不一致。");
    }

    public static ProcmanCapabilityResult ClearEcoQos(int pid)
    {
        var handle = TryOpen(pid, AccessSetInformation);
        if (handle is null) return ProcmanCapabilityResult.Failure("打开进程失败（权限不足或进程已退出）。");
        using (handle)
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PowerThrottlingCurrentVersion,
                ControlMask = 0, // 掩码清零 = 该项交还系统策略
                StateMask = 0,
            };
            if (!SetProcessInformation(handle, ProcessInformationClassPowerThrottling, ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>()))
                return ProcmanCapabilityResult.Failure("交还效率模式失败：Win32 错误 " + Marshal.GetLastWin32Error());
        }
        return ReadEcoQos(pid);
    }

    // ---------------- I/O 优先级（ntdll 兼容层：NtSetInformationProcess/ProcessIoPriority，Windows 8+ 受支持） ----------------

    private const int ProcessInformationClassIoPriority = 0x21; // ProcessIoPriority（undocumented but widely documented by MS docs for background mode）
    private const int IoPriorityVeryLow = 0;   // Background mode（系统“效率模式”后台 IO）
    private const int IoPriorityLow = 1;
    private const int IoPriorityNormal = 2;    // 默认
    private const int IoPriorityHigh = 3;
    private const int IoPriorityCritical = 4;  // 系统保留：不开放

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle handle, int informationClass, ref int information, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationProcess(SafeProcessHandle handle, int informationClass, ref int information, int length);

    /// <summary>读取进程 I/O 优先级（0-4）。失败返回错误（不猜）。</summary>
    public static ProcmanCapabilityResult ReadIoPriority(int pid)
    {
        var handle = TryOpenIoPriority(pid, forWrite: false);
        if (handle is null) return ProcmanCapabilityResult.Failure("打开进程失败（权限不足或进程已退出）。");
        using (handle)
        {
            var value = 0; var length = 0;
            var status = NtQueryInformationProcess(handle, ProcessInformationClassIoPriority, ref value, sizeof(int), out length);
            if (status != 0)
                return ProcmanCapabilityResult.Failure("读取 I/O 优先级失败：NTSTATUS 0x" + unchecked((uint)status).ToString("X8"));
            return ProcmanCapabilityResult.Success(IoPriorityName(value));
        }
    }

    /// <summary>设置进程 I/O 优先级（只开放 VeryLow/Low/Normal/High；Critical 系统保留）。</summary>
    public static ProcmanCapabilityResult SetIoPriority(int pid, int value)
    {
        if (value is < IoPriorityVeryLow or > IoPriorityHigh)
            return ProcmanCapabilityResult.Failure("I/O 优先级档位不开放（只支持 VeryLow/Low/Normal/High）。");
        var handle = TryOpenIoPriority(pid, forWrite: true);
        if (handle is null) return ProcmanCapabilityResult.Failure("打开进程失败（权限不足：I/O 优先级写入需要完全访问权，目标受保护或未提权时明确失败）。");
        using (handle)
        {
            var status = NtSetInformationProcess(handle, ProcessInformationClassIoPriority, ref value, sizeof(int));
            if (status != 0)
                return ProcmanCapabilityResult.Failure("设置 I/O 优先级失败：NTSTATUS 0x" + unchecked((uint)status).ToString("X8") +
                    (unchecked((uint)status) == 0xC0000022 ? "（STATUS_ACCESS_DENIED：目标进程权限更高，如实呈现）" : ""));
        }
        var verify = ReadIoPriority(pid);
        return verify.Ok && verify.Value == IoPriorityName(value)
            ? verify
            : ProcmanCapabilityResult.Failure($"回读不一致（请求 {IoPriorityName(value)}，实际 {verify.Value ?? "未知"}）。");
    }

    /// <summary>
    /// I/O 优先级的 Native 兼容层：查询只需 QUERY_LIMITED；写入实测需要完全访问权
    /// （0x1000/0x0400/0x2000 组合均返回 STATUS_ACCESS_DENIED——已用探针程序在本机验证）。
    /// 受保护进程/未提权时打开失败，按「访问被拒」如实呈现。
    /// </summary>
    private static SafeProcessHandle? TryOpenIoPriority(int pid, bool forWrite)
    {
        if (_pinned is not null) return TryOpen(pid, AccessSetInformation);
        try
        {
            var handle = OpenProcess(forWrite ? AccessSetInformation : 0x1400u, false, pid);
            return handle.IsInvalid ? null : handle;
        }
        catch { return null; }
    }

    public static int IoPriorityFromName(string name) => name switch
    {
        "VeryLow" => IoPriorityVeryLow, "Low" => IoPriorityLow, "Normal" => IoPriorityNormal, "High" => IoPriorityHigh, _ => -1,
    };

    private static string IoPriorityName(int value) => value switch
    {
        IoPriorityVeryLow => "VeryLow", IoPriorityLow => "Low", IoPriorityNormal => "Normal", IoPriorityHigh => "High",
        IoPriorityCritical => "Critical", _ => "Unknown(" + value + ")",
    };

    // ---------------- 应用 GPU 首选项（注册表 per-user，HKCU\...\GraphicsDrivers\GpuPreference） ----------------

    private const string GpuPreferenceKey = @"SOFTWARE\Microsoft\DirectX\UserGpuPreferences";
    private const string GpuPreferenceValueName = "DirectXUserGlobalSettings";

    /// <summary>
    /// 读取某 EXE 的 GPU 首选项（Windows 默认/省电/高性能）。值格式："GpuPreference=N;"（N: 0=默认 1=省电 2=高性能）。
    /// 同一设置行可能还包含其他图形选项——只解析 GpuPreference 段，其余原样保留。
    /// </summary>
    public static ProcmanCapabilityResult ReadGpuPreference(string executablePath)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(GpuPreferenceKey, writable: false);
            if (key?.GetValue(executablePath) is not string raw)
                return ProcmanCapabilityResult.Success("Windows 默认");
            return ProcmanCapabilityResult.Success(ParseGpuPreference(raw, out _)
                ? GpuPreferenceName(ParseGpuPreferenceValue(raw))
                : "Windows 默认（设置行无法解析，已保留原样）");
        }
        catch (Exception ex) { return ProcmanCapabilityResult.Failure("读取 GPU 首选项失败：" + ex.Message); }
    }

    /// <summary>
    /// 设置某 EXE 的 GPU 首选项：只改写 GpuPreference=N; 段，保留同一设置行中的其他图形选项；
    /// 并把修改前的完整原值（含类型）记录在案（由调用方写修改日志）。
    /// </summary>
    public static (bool Ok, string OriginalRaw, string NewRaw, string Error) SetGpuPreference(string executablePath, int value)
    {
        if (value is < 0 or > 2) return (false, "", "", "GPU 首选项只支持 默认(0)/省电(1)/高性能(2)。");
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(GpuPreferenceKey, writable: true);
            if (key is null) return (false, "", "", "打开注册表项失败。");
            var original = key.GetValue(executablePath, "") as string ?? "";
            var updated = ReplaceGpuPreferenceSegment(original, value);
            if (updated.Length == 0) return (false, original, "", "设置行合成失败，未写入。");
            key.SetValue(executablePath, updated, Microsoft.Win32.RegistryValueKind.String);
            var verify = key.GetValue(executablePath) as string ?? "";
            return verify == updated
                ? (true, original, updated, "")
                : (false, original, verify, "回读不一致，未确认生效。");
        }
        catch (Exception ex) { return (false, "", "", "设置 GPU 首选项失败：" + ex.Message); }
    }

    public static string GpuPreferenceName(int value) => value switch
    {
        1 => "省电", 2 => "高性能", _ => "Windows 默认",
    };

    public static int GpuPreferenceFromName(string name) => name switch
    {
        "省电" => 1, "高性能" => 2, "Windows 默认" or "默认" => 0, _ => -1,
    };

    private static bool ParseGpuPreference(string raw, out int value)
    {
        value = 0;
        foreach (var segment in raw.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (segment.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(segment["GpuPreference=".Length..], out var parsed))
            {
                value = parsed;
                return true;
            }
        return false;
    }

    private static int ParseGpuPreferenceValue(string raw) => ParseGpuPreference(raw, out var value) ? value : 0;

    /// <summary>只替换 GpuPreference=N; 段，其他图形选项（如 VRROptimizeEnable）原样保留。</summary>
    private static string ReplaceGpuPreferenceSegment(string raw, int value)
    {
        var kept = new List<string>();
        var replaced = false;
        foreach (var segment in raw.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase))
            {
                kept.Add("GpuPreference=" + value + ";");
                replaced = true;
            }
            else
            {
                kept.Add(segment + ";");
            }
        }
        if (!replaced) kept.Insert(0, "GpuPreference=" + value + ";");
        return string.Concat(kept);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetPriorityClass(SafeProcessHandle handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetPriorityClass(SafeProcessHandle handle, uint value);
}

/// <summary>按处理器组的掩码（引擎层值类型；合同层对应 ProcmanGroupMask）。</summary>
public sealed record ProcmanGroupMaskValue(ushort Group, ulong Mask);
