// NVAPI DRS 只读探针（分步调试版）
using System.Runtime.InteropServices;

internal static class Program
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int VoidFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SessionFn(IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateSessionFn(ref IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumIdsFn(IntPtr ids, ref uint maxCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NameFromIdFn(uint id, IntPtr nameBuffer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumValuesFn(uint settingId, ref uint maxValues, IntPtr valuesStruct);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetBaseFn(IntPtr session, ref IntPtr profile);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetSettingProbeFn(IntPtr session, IntPtr profile, uint settingId, IntPtr setting, ref uint reserved);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetSettingProbeFn(IntPtr session, IntPtr profile, IntPtr setting, uint reserved1, uint reserved2);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DeleteSettingFn(IntPtr session, IntPtr profile, uint settingId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FindAppProbeFn(IntPtr session, [MarshalAs(UnmanagedType.LPWStr)] string appName, ref IntPtr profile, IntPtr application);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FindProfileProbeFn(IntPtr session, [MarshalAs(UnmanagedType.LPWStr)] string profileName, ref IntPtr profile);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateProfileProbeFn(IntPtr session, IntPtr profileInfo, ref IntPtr profile);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateAppProbeFn(IntPtr session, IntPtr profile, IntPtr application);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string fileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procName);

    private static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine($"64bit={Environment.Is64BitProcess}");

        var lib = LoadLibrary("nvapi64.dll");
        Console.WriteLine($"lib=0x{lib:X}");
        if (lib == IntPtr.Zero) return;

        var qiPtr = GetProcAddress(lib, "nvapi_QueryInterface");
        Console.WriteLine($"qiPtr=0x{qiPtr:X}");
        if (qiPtr == IntPtr.Zero) return;

        var qi = Marshal.GetDelegateForFunctionPointer<QueryInterface>(qiPtr);
        Console.WriteLine("qi delegate ok");

        var initPtr = qi(0x0150E828);
        Console.WriteLine($"initPtr=0x{initPtr:X}");
        if (initPtr == IntPtr.Zero) return;

        var init = Marshal.GetDelegateForFunctionPointer<VoidFn>(initPtr);
        var st = init();
        Console.WriteLine($"NvAPI_Initialize={st}");
        if (st != 0) return;

        IntPtr Resolve(uint id, string name)
        {
            var p = qi(id);
            Console.WriteLine($"{name} (0x{id:X8}) -> 0x{p:X}");
            return p;
        }

        var createSession = Marshal.GetDelegateForFunctionPointer<CreateSessionFn>(Resolve(0x0694D52E, "DRS_CreateSession"));
        var load = Marshal.GetDelegateForFunctionPointer<SessionFn>(Resolve(0x375DBD6B, "DRS_LoadSettings"));
        var destroySession = Marshal.GetDelegateForFunctionPointer<SessionFn>(Resolve(0xDAD9CFF8, "DRS_DestroySession"));

        var enumIdsPtr = Resolve(0xE5DE48E5, "DRS_EnumAvailableSettingIds(new)");
        if (enumIdsPtr == IntPtr.Zero) enumIdsPtr = Resolve(0xF020614A, "DRS_EnumAvailableSettingIds(old)");
        var namePtr = Resolve(0x1EB13791, "DRS_GetSettingNameFromId(new)");
        if (namePtr == IntPtr.Zero) namePtr = Resolve(0xD61CBE6E, "DRS_GetSettingNameFromId(old)");
        var enumValuesPtr = Resolve(0x2EC39F90, "DRS_EnumAvailableSettingValues");
        if (enumIdsPtr == IntPtr.Zero || namePtr == IntPtr.Zero || enumValuesPtr == IntPtr.Zero)
        {
            Console.WriteLine("缺少必要接口，退出");
            return;
        }

        var enumIds = Marshal.GetDelegateForFunctionPointer<EnumIdsFn>(enumIdsPtr);
        var nameFromId = Marshal.GetDelegateForFunctionPointer<NameFromIdFn>(namePtr);
        var enumValues = Marshal.GetDelegateForFunctionPointer<EnumValuesFn>(enumValuesPtr);

        var session = IntPtr.Zero;
        st = createSession(ref session);
        Console.WriteLine($"CreateSession={st}, session=0x{session:X}");
        if (st != 0) return;

        try
        {
            st = load(session);
            Console.WriteLine($"LoadSettings={st}");

            // 验证 NVDRS_SETTING 结构体版本：GetBaseProfile + GetSetting（只读）
            var getBasePtr = qi(0xDA8466A0);
            var getSettingPtr = qi(0xEA99498D);
            GetSettingProbeFn? getSetting = null;
            if (getBasePtr != IntPtr.Zero && getSettingPtr != IntPtr.Zero)
            {
                var getBase = Marshal.GetDelegateForFunctionPointer<GetBaseFn>(getBasePtr);
                getSetting = Marshal.GetDelegateForFunctionPointer<GetSettingProbeFn>(getSettingPtr);

                var baseProfile = IntPtr.Zero;
                Console.WriteLine($"GetBaseProfile={getBase(session, ref baseProfile)}, profile=0x{baseProfile:X}");

                var buf = Marshal.AllocHGlobal(12320);
                try
                {
                    foreach (var (id, label) in new[] { (0x10E41DF3u, "SR preset"), (0x10D48A85u, "TrSSAA"), (0x10ECECC9u, "Sysmem") })
                    {
                        unsafe
                        {
                            var p = (byte*)buf;
                            for (var off = 0; off < 12320; off++) p[off] = 0;
                        }

                        Marshal.WriteInt32(buf, 0, 12320 | (1 << 16));
                        var reserved = 0u;
                        var gst = getSetting(session, baseProfile, id, buf, ref reserved);
                        var location = gst == 0 ? Marshal.ReadInt32(buf, 4108) : -1;
                        var dword = gst == 0 ? unchecked((uint)Marshal.ReadInt32(buf, 8220)) : 0;
                        Console.WriteLine($"GetSetting({label})={gst}, location={location}, value=0x{dword:X8}");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }

            // ===== 写-读-删往返测试（真实写入后清理）=====
            var setSettingPtr = qi(0x8A2CF5F5);
            if (setSettingPtr == IntPtr.Zero) setSettingPtr = qi(0x577DD202);
            var delSettingPtr = qi(0xD20D29DF);
            if (delSettingPtr == IntPtr.Zero) delSettingPtr = qi(0xE4A26362);
            var findAppPtr = qi(0xEEE566B2);
            var createProfilePtr = qi(0xCC176068);
            var createAppPtr = qi(0x4347A9DE);
            var savePtr = qi(0xFCBC7E14);
            var findProfilePtr = qi(0x7E4A9A0B);

            if (setSettingPtr != IntPtr.Zero && delSettingPtr != IntPtr.Zero && findAppPtr != IntPtr.Zero &&
                createProfilePtr != IntPtr.Zero && createAppPtr != IntPtr.Zero && savePtr != IntPtr.Zero)
            {
                var setSetting = Marshal.GetDelegateForFunctionPointer<SetSettingProbeFn>(setSettingPtr);
                var delSetting = Marshal.GetDelegateForFunctionPointer<DeleteSettingFn>(delSettingPtr);
                var findApp = Marshal.GetDelegateForFunctionPointer<FindAppProbeFn>(findAppPtr);
                var createProfile = Marshal.GetDelegateForFunctionPointer<CreateProfileProbeFn>(createProfilePtr);
                var createApp = Marshal.GetDelegateForFunctionPointer<CreateAppProbeFn>(createAppPtr);
                var save = Marshal.GetDelegateForFunctionPointer<SessionFn>(savePtr);
                var findProfile = Marshal.GetDelegateForFunctionPointer<FindProfileProbeFn>(findProfilePtr);

                var gameExe = "DeltaForceClient-Win64-Shipping.exe";

                IntPtr FindGameProfile()
                {
                    var handle = IntPtr.Zero;
                    var buf = Marshal.AllocHGlobal(20492);
                    try
                    {
                        unsafe
                        {
                            var p = (byte*)buf;
                            for (var i = 0; i < 20492; i++) p[i] = 0;
                            Marshal.WriteInt32(buf, 0, 20492 | (4 << 16));
                        }

                        var fst = findApp(session, gameExe, ref handle, buf);
                        Console.WriteLine($"FindApplicationByName={fst}, profile=0x{handle:X}");
                        return handle;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buf);
                    }
                }

                var profile = FindGameProfile();
                if (profile == IntPtr.Zero)
                {
                    var byName = IntPtr.Zero;
                    var fpst = findProfile(session, "Delta NFD - 三角洲行动", ref byName);
                    Console.WriteLine($"FindProfileByName={fpst}, profile=0x{byName:X}");
                    profile = byName;
                }

                // 若仍无配置：创建（用规范的 PROFILE 结构）
                if (profile == IntPtr.Zero)
                {
                    var profBuf = Marshal.AllocHGlobal(4116);
                    try
                    {
                        unsafe
                        {
                            var p = (byte*)profBuf;
                            for (var i = 0; i < 4116; i++) p[i] = 0;
                            Marshal.WriteInt32(profBuf, 0, 4116 | (1 << 16));
                            var np = p + 4;
                            foreach (var ch in "Delta NFD - 三角洲行动") { *(char*)np = ch; np += 2; }
                        }

                        var cst = createProfile(session, profBuf, ref profile);
                        Console.WriteLine($"CreateProfile={cst}, profile=0x{profile:X}");
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(profBuf);
                    }

                    if (profile != IntPtr.Zero)
                    {
                        var appBuf = Marshal.AllocHGlobal(20492);
                        try
                        {
                            unsafe
                            {
                                var p = (byte*)appBuf;
                                for (var i = 0; i < 20492; i++) p[i] = 0;
                                Marshal.WriteInt32(appBuf, 0, 20492 | (4 << 16));
                                var np = p + 8;
                                foreach (var ch in gameExe) { *(char*)np = ch; np += 2; }
                                var fp = p + 8 + 4096 + 4;
                                foreach (var ch in "Delta Force") { *(char*)fp = ch; fp += 2; }
                            }

                            var ast = createApp(session, profile, appBuf);
                            Console.WriteLine($"CreateApplication={ast}");
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(appBuf);
                        }
                    }
                }

                if (profile != IntPtr.Zero)
                {
                    // 写入 sysmem fallback = 1
                    var setBuf = Marshal.AllocHGlobal(12320);                    try
                    {
                        unsafe
                        {
                            var p = (byte*)setBuf;
                            for (var i = 0; i < 12320; i++) p[i] = 0;
                            Marshal.WriteInt32(setBuf, 0, 12320 | (1 << 16));
                            Marshal.WriteInt32(setBuf, 4100, unchecked((int)0x10ECECC9)); // settingId
                            Marshal.WriteInt32(setBuf, 4104, 0); // type DWORD
                            Marshal.WriteInt32(setBuf, 4108, 0); // location CURRENT
                            Marshal.WriteInt32(setBuf, 8220, 1); // u32CurrentValue = 1
                        }

                        var sst = setSetting(session, profile, setBuf, 0, 0);
                        Console.WriteLine($"SetSetting(sysmem=1)={sst}");
                        var svst = save(session);
                        Console.WriteLine($"SaveSettings={svst}");
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(setBuf);
                    }

                    // 重新开会话读回
                    destroySession(session);
                    session = IntPtr.Zero;
                    createSession(ref session);
                    load(session);
                    var profile2 = FindGameProfile();
                    if (profile2 != IntPtr.Zero)
                    {
                        var readBuf = Marshal.AllocHGlobal(12320);
                        try
                        {
                            unsafe
                            {
                                var p = (byte*)readBuf;
                                for (var i = 0; i < 12320; i++) p[i] = 0;
                            }

                            Marshal.WriteInt32(readBuf, 0, 12320 | (1 << 16));
                            var reserved = 0u;
                            var gst = getSetting(session, profile2, 0x10ECECC9, readBuf, ref reserved);
                            var loc = gst == 0 ? Marshal.ReadInt32(readBuf, 4108) : -1;
                            var val = gst == 0 ? unchecked((uint)Marshal.ReadInt32(readBuf, 8220)) : 0;
                            Console.WriteLine($"ReadBack(sysmem): status={gst}, location={loc}, value={val}");
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(readBuf);
                        }

                        // 清理：删除覆盖并保存
                        var dst = delSetting(session, profile2, 0x10ECECC9);
                        var svst2 = save(session);
                        Console.WriteLine($"DeleteOverride={dst}, Save={svst2}");
                    }
                }
            }

            uint count = 0;
            st = enumIds(IntPtr.Zero, ref count);
            Console.WriteLine($"EnumIds(count)={st}, count={count}");
            if (st != 0 || count == 0) return;

            var ids = Marshal.AllocHGlobal((int)(count * 4));
            var nameBuf = Marshal.AllocHGlobal(4096);
            var interesting = new List<(uint Id, string Name)>();
            try
            {
                st = enumIds(ids, ref count);
                Console.WriteLine($"EnumIds(fill)={st}, count={count}");

                for (var i = 0; i < count; i++)
                {
                    var id = unchecked((uint)Marshal.ReadInt32(ids, i * 4));
                    if (nameFromId(id, nameBuf) != 0) continue;
                    var name = Marshal.PtrToStringUni(nameBuf) ?? "";
                    if (name.Contains("SMOOTH", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("SYSMEM", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("TRANSPARENCY", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("DLSS", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("FALLBACK", StringComparison.OrdinalIgnoreCase))
                    {
                        interesting.Add((id, name));
                        Console.WriteLine($"HIT  0x{id:X8}  {name}");
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ids);
                Marshal.FreeHGlobal(nameBuf);
            }

            Console.WriteLine();
            Console.WriteLine("=== 关键设置可用取值 ===");

            // NVDRS_SETTING_VALUES: version(0) numSettingValues(4) settingType(8)
            // defaultValue union 4100B(12) settingValues[100] union×4100B(4112..)
            const int maxValues = 100;
            const int unionSize = 4100;
            var valuesOffset = 12 + unionSize;
            var valuesSize = valuesOffset + maxValues * unionSize;

            var targets = new List<(uint Id, string Label)>
            {
                (0x10E41DF3, "DLSS_SR preset"),
                (0x10E41DF5, "DLSS_SR scaling ratio"),
                (0x10E41DF1, "DLSS_FG preset"),
                (0x10D48A85, "AA Transparency Supersampling"),
                (0x10FC2D9C, "AA Transparency Multisampling"),
                (0x10ECECC9, "CUDA Sysmem Fallback"),
                (0x10308298, "DLSSG mode"),
                (0x10AFB768, "DLSS-SR performance mode"),
                (0x10E41E01, "Enable DLSS-SR override"),
            };
            foreach (var (id, name) in interesting)
            {
                if (targets.All(t => t.Id != id))
                {
                    targets.Add((id, name));
                }
            }

            var values = Marshal.AllocHGlobal(valuesSize);
            try
            {
                foreach (var (id, label) in targets)
                {
                    unsafe
                    {
                        var p = (byte*)values;
                        for (var off = 0; off < valuesSize; off++) p[off] = 0;
                    }

                    Marshal.WriteInt32(values, 0, valuesSize | (1 << 16));
                    var max = (uint)maxValues;
                    var est = enumValues(id, ref max, values);
                    if (est != 0)
                    {
                        Console.WriteLine($"{label} (0x{id:X8}): EnumValues={est}");
                        continue;
                    }

                    var num = unchecked((uint)Marshal.ReadInt32(values, 4));
                    var list = new List<string>();
                    for (var i = 0; i < num && i < maxValues; i++)
                    {
                        var dword = unchecked((uint)Marshal.ReadInt32(values, valuesOffset + i * unionSize));
                        list.Add($"0x{dword:X8}({dword})");
                    }

                    Console.WriteLine($"{label} (0x{id:X8}): {num} 值 → {string.Join(", ", list)}");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(values);
            }
        }
        finally
        {
            destroySession(session);
        }

        Console.WriteLine();
        Console.WriteLine("PROBE DONE (只读)");
    }
}
