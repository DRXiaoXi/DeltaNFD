using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace DeltaNFD.Services {
    internal sealed class NvContainerScm : INvContainerScm, IDisposable {
        internal const string ServiceName = "NVDisplay.ContainerLocalSystem";
        private const uint ReadAccess = 0x0001 | 0x0004; // QUERY_CONFIG | QUERY_STATUS
        private const uint EnumerateDependents = 0x0008;
        private const uint ChangeConfig = 0x0002, Start = 0x0010, Stop = 0x0020;
        private const int InsufficientBuffer = 122, MoreData = 234, AlreadyRunning = 1056, NotActive = 1062;
        private readonly ServiceHandle manager;
        private readonly ServiceHandle service;
        private readonly NvContainerTarget target;

        internal NvContainerScm(bool writable, NvContainerTarget target = NvContainerTarget.Display) {
            this.target = target;
            manager = OpenSCManagerW(null, null, 0x0001); // SC_MANAGER_CONNECT
            if (manager.IsInvalid) {
                var error = LastError("连接 Windows 服务管理器");
                manager.Dispose(); throw error;
            }
            try {
                var name = target switch
                {
                    NvContainerTarget.Display => ServiceName,
                    NvContainerTarget.LegacyTelemetry => NvTelemetryContainer.ServiceName,
                    NvContainerTarget.LocalSystem => NvLocalSystemContainer.ServiceName,
                    _ => throw new ArgumentOutOfRangeException(nameof(target)),
                };
                service = OpenServiceW(manager, name,
                    ReadAccess | (target != NvContainerTarget.Display ? EnumerateDependents : 0u) | (writable ? ChangeConfig | Start | Stop : 0u));
                if (service.IsInvalid) {
                    var error = LastError("打开目标服务");
                    service.Dispose(); throw error;
                }
            } catch { manager.Dispose(); throw; }
        }
        public void Dispose() { service.Dispose(); manager.Dispose(); }

        public NvContainerState Read() {
            uint bytes;
            bool first = QueryServiceConfigW(service, IntPtr.Zero, 0, out bytes);
            if (first || Marshal.GetLastWin32Error() != InsufficientBuffer || bytes < Marshal.SizeOf(typeof(Config)) || bytes > 8192)
                throw LastError("读取服务配置长度");
            IntPtr mem = Marshal.AllocHGlobal((int)bytes);
            string? image;
            string? account = null;
            NvContainerMode mode;
            try {
                if (!QueryServiceConfigW(service, mem, bytes, out bytes)) throw LastError("读取服务配置");
                var cfg = Marshal.PtrToStructure<Config>(mem);
                image = Marshal.PtrToStringUni(cfg.Image);
                if (cfg.Type != 0x10)
                    throw new InvalidOperationException("服务类型不是独立 Win32 服务（0x" + cfg.Type.ToString("X") + "）。");
                var configuredAccount = Marshal.PtrToStringUni(cfg.Account);
                if (target == NvContainerTarget.LegacyTelemetry) {
                    account = NvTelemetryContainerPolicy.NormalizeAccount(configuredAccount)
                        ?? throw new InvalidOperationException("遥测服务登录账号不是受支持的系统账号。");
                    if (NvTelemetryContainerPolicy.ParseImage(image) is null)
                        throw new InvalidOperationException("遥测服务映像路径或启动参数不是受支持的专用服务配置。");
                } else {
                    if (!NvDisplayContainerService.IsLocalSystem(configuredAccount))
                        throw new InvalidOperationException("服务登录账号不是 LocalSystem。");
                    if (target == NvContainerTarget.Display)
                    {
                        if (NvDisplayContainerService.ParseImage(image, out var imageReason) is null)
                            throw new InvalidOperationException(imageReason);
                    }
                    else
                    {
                        account = "LocalSystem";
                        if (NvLocalSystemContainerPolicy.ParseImage(image) is null)
                            throw new InvalidOperationException("共用容器的映像路径或启动参数不受支持。");
                    }
                }
                if (cfg.Dependencies != IntPtr.Zero && Marshal.ReadInt16(cfg.Dependencies) != 0)
                    throw new InvalidOperationException("目标服务声明了依赖项；为避免停启其他服务，不提供切换。");
                mode = (NvContainerMode)cfg.Mode;
                if (mode != NvContainerMode.Automatic && mode != NvContainerMode.Manual && mode != NvContainerMode.Disabled)
                    throw new InvalidOperationException("服务的启动类型不受支持。");
            } finally { Marshal.FreeHGlobal(mem); }
            if (target != NvContainerTarget.Display && !EnumDependentServicesW(service, 3, IntPtr.Zero, 0, out _, out _)) {
                var error = Marshal.GetLastWin32Error();
                if (error == MoreData)
                    throw new InvalidOperationException("有其他服务依赖此容器，当前不可切换。");
                throw Error("核对依赖该容器的其他服务", error);
            }
            int delayed;
            if (!QueryServiceConfig2W(service, 3, out delayed, sizeof(int), out bytes)) throw LastError("读取延迟自动启动标记");
            StatusEx status = ReadStatus();
            return new NvContainerState(image!, mode, delayed != 0, (NvContainerRun)status.State, status.Pid, account);
        }
        public void SetMode(NvContainerMode mode) {
            const uint keep = 0xffffffff;
            if (!ChangeServiceConfigW(service, keep, (uint)mode, keep, null, null, IntPtr.Zero, null, null, null, null))
                throw LastError("修改服务启动类型");
        }
        public void RequestStop() {
            Status status;
            if (!ControlService(service, 1, out status)) {
                int code = Marshal.GetLastWin32Error();
                if (code != NotActive) throw Error("正常停止服务", code);
            }
        }
        public void RequestStart() {
            if (!StartServiceW(service, 0, IntPtr.Zero)) {
                int code = Marshal.GetLastWin32Error();
                if (code != AlreadyRunning) throw Error("重新启动服务", code);
            }
        }
        public void WaitFor(NvContainerRun expected, NvContainerRun pending) {
            Stopwatch watch = Stopwatch.StartNew();
            do {
                var status = ReadStatus();
                if ((NvContainerRun)status.State == expected) return;
                if ((NvContainerRun)status.State != pending)
                    throw new InvalidOperationException("服务未到达目标状态。状态码：" + status.State + "；服务退出码：" + status.Win32Exit);
                Thread.Sleep(200);
            } while (watch.ElapsedMilliseconds < 25000);
            throw new TimeoutException("等待服务切换超时，请稍后再打开工具核对。");
        }
        private StatusEx ReadStatus() {
            StatusEx status;
            uint bytes;
            if (!QueryServiceStatusEx(service, 0, out status, (uint)Marshal.SizeOf(typeof(StatusEx)), out bytes)) throw LastError("读取服务状态");
            return status;
        }
        private static Win32Exception LastError(string step) { return Error(step, Marshal.GetLastWin32Error()); }
        internal static Win32Exception Error(string step, int code) {
            return new Win32Exception(code, step + "失败（Windows 错误 " + code + "：" + new Win32Exception(code).Message + "）。");
        }

        [StructLayout(LayoutKind.Sequential)] private struct Config {
            internal uint Type, Mode, Error;
            internal IntPtr Image, Group;
            internal uint Tag;
            internal IntPtr Dependencies, Account, DisplayName;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Status {
            internal uint Type, State, Controls, Win32Exit, ServiceExit, Checkpoint, WaitHint;
        }
        [StructLayout(LayoutKind.Sequential)] private struct StatusEx {
            internal uint Type, State, Controls, Win32Exit, ServiceExit, Checkpoint, WaitHint, Pid, Flags;
        }
        private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid {
            private ServiceHandle() : base(true) { }
            protected override bool ReleaseHandle() { return CloseServiceHandle(handle); }
        }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern ServiceHandle OpenSCManagerW(string? machine, string? database, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern ServiceHandle OpenServiceW(ServiceHandle scm, string name, uint access);
        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern bool QueryServiceConfigW(ServiceHandle svc, IntPtr cfg, uint size, out uint needed);
        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern bool QueryServiceConfig2W(ServiceHandle svc, uint level, out int info, uint size, out uint needed);
        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern bool EnumDependentServicesW(ServiceHandle svc, uint state, IntPtr results, uint size, out uint needed, out uint count);
        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern bool QueryServiceStatusEx(ServiceHandle svc, uint level, out StatusEx status, uint size, out uint needed);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern bool ChangeServiceConfigW(ServiceHandle svc, uint type, uint mode, uint error, string? binary,
            string? group, IntPtr tag, string? deps, string? user, string? password, string? display);
        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern bool ControlService(ServiceHandle svc, uint code, out Status status);
        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern bool StartServiceW(ServiceHandle svc, uint argc, IntPtr argv);
        [DllImport("advapi32.dll", ExactSpelling = true)]
        private static extern bool CloseServiceHandle(IntPtr handle);
    }
}


