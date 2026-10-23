using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DeltaNFD.Services;

public sealed partial class PowerService
{
    private readonly HeteroPowerPolicy? _heteroNative;
    internal PowerService(HeteroPowerPolicy.IApi api, Func<bool> elevated) : this(RunPowerCfgCoreAsync, elevated)
    { _heteroNative = new(api); }
}

/// <summary>Fixed GUID queries and writes, without spawning powercfg or changing attribute visibility.</summary>
internal sealed class HeteroPowerPolicy(HeteroPowerPolicy.IApi api)
{
    internal interface IApi
    {
        Guid Active();
        (int Ac, int Dc) Read(Guid scheme, Guid setting);
        void Write(Guid scheme, Guid setting, bool ac, int value);
        void Activate(Guid scheme);
    }

    internal List<HeteroPolicyInfo> Read()
    {
        var scheme = api.Active();
        var result = new List<HeteroPolicyInfo>();
        foreach (var (guid, title) in PowerSchemes.HeteroPolicySettings)
        {
            var values = api.Read(scheme, Guid.Parse(guid));
            result.Add(new() { SettingGuid = guid, Title = title, Supported = true, AcValue = values.Ac, DcValue = values.Dc });
        }
        if (api.Active() != scheme) throw new IOException("读取期间电源计划发生变化，请刷新后重试。");
        return result;
    }

    internal OperationResult Set(Guid setting, int ac, int dc)
    {
        Guid scheme;
        (int Ac, int Dc) before;
        try { scheme = api.Active(); before = api.Read(scheme, setting); }
        catch (Exception ex) { return OperationResult.Fail("无法确认异类策略原值，未写入：" + ex.Message); }
        var attemptedAc = false;
        var attemptedDc = false;
        try
        {
            if (api.Active() != scheme) throw new IOException("活动计划已变化，未写入。");
            if (before.Ac == ac && before.Dc == dc) return OperationResult.Ok("异类策略已是目标交流/电池值。");
            if (before.Ac != ac) { attemptedAc = true; api.Write(scheme, setting, true, ac); }
            if (before.Dc != dc) { attemptedDc = true; api.Write(scheme, setting, false, dc); }
            if (api.Active() != scheme) throw new IOException("活动计划已变化，未切回旧计划。");
            api.Activate(scheme);
            if (api.Read(scheme, setting) != (ac, dc) || api.Active() != scheme)
                throw new IOException("异类策略写后核对未通过。");
            return OperationResult.Ok($"异类策略已核对：交流 {ac} / 电池 {dc}。");
        }
        catch (Exception ex)
        {
            if (!attemptedAc && !attemptedDc) return OperationResult.Fail(ex.Message);
            try
            {
                var live = api.Read(scheme, setting);
                if (attemptedAc && live.Ac != before.Ac && live.Ac != ac
                    || attemptedDc && live.Dc != before.Dc && live.Dc != dc)
                    throw new IOException("检测到外部修改，未覆盖外部值。");
                if (attemptedAc) api.Write(scheme, setting, true, before.Ac);
                if (attemptedDc) api.Write(scheme, setting, false, before.Dc);
                if (api.Active() == scheme) api.Activate(scheme);
                var restored = api.Read(scheme, setting);
                if (attemptedAc && restored.Ac != before.Ac || attemptedDc && restored.Dc != before.Dc)
                    throw new IOException("恢复回读未通过。");
                return OperationResult.Fail("异类策略未完成，已还原本次尝试值：" + ex.Message);
            }
            catch (Exception rollback)
            {
                return new OperationResult { OutcomeUnknown = true, IsPartial = true,
                    Message = "异类策略未完成，恢复需核对：" + ex.Message + "；" + rollback.Message };
            }
        }
    }

    internal sealed class NativeApi : IApi
    {
        private static readonly Guid Processor = new("54533251-82be-4824-96c1-47b60b740d00");
        private static void Check(uint code) { if (code != 0) throw new Win32Exception(unchecked((int)code)); }
        public Guid Active()
        {
            Check(PowerGetActiveScheme(IntPtr.Zero, out var pointer));
            if (pointer == IntPtr.Zero) throw new IOException("活动电源计划为空。");
            try { return Marshal.PtrToStructure<Guid>(pointer); }
            finally { LocalFree(pointer); }
        }
        public (int Ac, int Dc) Read(Guid scheme, Guid setting)
        {
            var subgroup = Processor;
            Check(PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out var ac));
            Check(PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out var dc));
            return (checked((int)ac), checked((int)dc));
        }
        public void Write(Guid scheme, Guid setting, bool ac, int value)
        {
            var subgroup = Processor;
            Check(ac ? PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, checked((uint)value))
                : PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, checked((uint)value)));
        }
        public void Activate(Guid scheme) => Check(PowerSetActiveScheme(IntPtr.Zero, ref scheme));
        [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr key, out IntPtr scheme);
        [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr key, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr key, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr key, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(IntPtr key, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr key, ref Guid scheme);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    }
}
