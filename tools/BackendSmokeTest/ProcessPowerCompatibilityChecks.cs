using System.ComponentModel;
using System.Runtime.InteropServices;
using DeltaNFD.Native;
using DeltaNFD.Execution.OptimalClock;
using Microsoft.Win32.SafeHandles;

internal static class ProcessPowerCompatibilityChecks
{
    private sealed class Api : IProcessPowerThrottlingApi
    {
        internal int ReadError, WriteError, ReadStatus, WriteStatus, Reads, Writes, NativeReads, NativeWrites;
        internal uint Length = 12, Version = 1;
        internal bool IgnoreWrite;
        internal ProcessPowerState State = new() { Version = 1, ControlMask = 9, StateMask = 9 };
        internal nint Handle;
        internal bool Fresh = true;
        public int ReadWin32(SafeProcessHandle process, ref ProcessPowerState state)
        {
            Reads++; Handle = process.DangerousGetHandle();
            if (ReadError == 0) state = State;
            else state = new() { Version = 999, ControlMask = 999, StateMask = 999 };
            return ReadError;
        }
        public int WriteWin32(SafeProcessHandle process, ref ProcessPowerState state)
        {
            Writes++; Handle = process.DangerousGetHandle();
            if (WriteError == 0 && !IgnoreWrite) State = state;
            if (WriteError != 0) state = default;
            return WriteError;
        }
        public int ReadNative(SafeProcessHandle process, ref ProcessPowerState state, out uint length)
        {
            NativeReads++; Fresh &= state.Version == 1 && state.ControlMask == 0 && state.StateMask == 0;
            if (process.DangerousGetHandle() != Handle) throw new Exception("changed handle");
            state = State; state.Version = Version; length = Length; return ReadStatus;
        }
        public int WriteNative(SafeProcessHandle process, ref ProcessPowerState state)
        {
            NativeWrites++; Fresh &= state.Version == 1;
            if (process.DangerousGetHandle() != Handle) throw new Exception("changed handle");
            if (WriteStatus == 0 && !IgnoreWrite) State = state;
            return WriteStatus;
        }
        public int Win32Error(int status) => 50;
    }

    private sealed class Policy(Api api, SafeProcessHandle process) : IOptimalClockTimerPolicyApi
    {
        public OptimalClockPowerState Read()
        { var s = ProcessPowerThrottling.Read(process, api); return new(s.ControlMask, s.StateMask); }
        public void Write(OptimalClockPowerState state) => ProcessPowerThrottling.Write(process, state.ControlMask, state.StateMask, api);
    }

    internal static void Run()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        string Reject(Action action)
        {
            try { action(); } catch (Exception ex) when (ex is Win32Exception or IOException or ArgumentException)
            { count++; return ex.Message; }
            throw new Exception("unexpected acceptance");
        }
        using var process = new SafeProcessHandle(new nint(-1), false);
        var normal = new Api();
        var original = ProcessPowerThrottling.Read(process, normal);
        ProcessPowerThrottling.Write(process, 13, 9, normal);
        Check(original.ControlMask == 9 && normal.State.ControlMask == 13 && normal.State.StateMask == 9, "normal masks");
        Check(normal.NativeReads == 0 && normal.NativeWrites == 0, "unnecessary native call");
        foreach (var error in new[] { 1, 50, 120 })
        {
            var fallback = new Api { ReadError = error, WriteError = error };
            Check(ProcessPowerThrottling.Read(process, fallback).ControlMask == 9, "fallback read");
            ProcessPowerThrottling.Write(process, 13, 9, fallback);
            Check(fallback.NativeReads == 1 && fallback.NativeWrites == 1 && fallback.Fresh, "fallback reset/attempt");
            Check(fallback.State.ControlMask == 13 && fallback.State.StateMask == 9, "fallback masks");
            Check(OptimalClockTimerPolicy.TryHonorRequests(out _, new Policy(fallback, process)), "holder compatibility");
            Check(fallback.State.ControlMask == 13 && fallback.State.StateMask == 9, "unrelated bits preserved");
        }
        foreach (var error in new[] { 5, 87, 6, 31 })
        {
            var denied = new Api { ReadError = error, WriteError = error };
            Check(Reject(() => ProcessPowerThrottling.Read(process, denied)).Contains($"Win32 {error}"), "original read code lost");
            Check(Reject(() => ProcessPowerThrottling.Write(process, 4, 0, denied)).Contains($"Win32 {error}"), "original write code lost");
            Check(denied.NativeReads == 0 && denied.NativeWrites == 0, "denied/invalid parameter bypassed");
        }
        var unavailable = new Api { ReadError = 1, WriteError = 1, ReadStatus = unchecked((int)0xC00000BB), WriteStatus = unchecked((int)0xC00000BB) };
        Check(Reject(() => ProcessPowerThrottling.Read(process, unavailable)).Contains("Win32 1；兼容接口 NTSTATUS 0xC00000BB"), "dual read evidence");
        Check(Reject(() => ProcessPowerThrottling.Write(process, 4, 0, unavailable)).Contains("0xC00000BB"), "dual write evidence");
        foreach (var length in new uint[] { 0, 8, 16 })
            Reject(() => ProcessPowerThrottling.Read(process, new Api { ReadError = 1, Length = length }));
        Reject(() => ProcessPowerThrottling.Read(process, new Api { ReadError = 1, Version = 2 }));
        var invalid = new Api();
        Reject(() => ProcessPowerThrottling.Write(process, 0, 4, invalid));
        Check(invalid.Writes == 0 && invalid.NativeWrites == 0, "invalid masks sent");
        var ignored = new Api { ReadError = 1, WriteError = 1, IgnoreWrite = true };
        Check(!OptimalClockTimerPolicy.TryHonorRequests(out var detail, new Policy(ignored, process)) && detail.Contains("回读不符"), "false holder success");
        var failed = new Api { ReadError = 1, WriteError = 1, WriteStatus = unchecked((int)0xC00000BB) };
        Check(!OptimalClockTimerPolicy.TryHonorRequests(out detail, new Policy(failed, process)) && detail.Contains("0xC00000BB"), "holder failure evidence");
        Console.WriteLine($"Process power compatibility: {count} checks passed; fake interfaces, no process policies changed.");
    }

    private sealed class NativeRead : IProcessPowerThrottlingApi
    {
        public int ReadWin32(SafeProcessHandle process, ref ProcessPowerState state) => 1;
        public int WriteWin32(SafeProcessHandle process, ref ProcessPowerState state) => throw new Exception("read-only check");
        public int ReadNative(SafeProcessHandle process, ref ProcessPowerState state, out uint length) => NtQueryInformationProcess(process, 77, ref state, 12, out length);
        public int WriteNative(SafeProcessHandle process, ref ProcessPowerState state) => throw new Exception("read-only check");
        public int Win32Error(int status) => unchecked((int)RtlNtStatusToDosError(status));
    }
    internal static void ReadOnly()
    {
        using var process = new SafeProcessHandle(new nint(-1), false);
        var normal = ProcessPowerThrottling.Read(process);
        var fallback = ProcessPowerThrottling.Read(process, new NativeRead());
        if (normal.Version != fallback.Version || normal.ControlMask != fallback.ControlMask || normal.StateMask != fallback.StateMask)
            throw new Exception("native and Win32 queries disagree");
        using var gameHandle = OpenProcess(0x1200, false, Environment.ProcessId);
        if (gameHandle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limited = ProcessPowerThrottling.Read(gameHandle, new NativeRead());
        if (limited.ControlMask != normal.ControlMask || limited.StateMask != normal.StateMask)
            throw new Exception("fixed game-policy handle query disagrees");
        Console.WriteLine($"Read-only process policy: Win32/native/fixed 0x1200 handle identical; Version={normal.Version}, Control={normal.ControlMask}, State={normal.StateMask}; no writes.");
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(SafeProcessHandle process, int kind, ref ProcessPowerState state, uint size, out uint length);
    [DllImport("ntdll.dll")] private static extern uint RtlNtStatusToDosError(int status);
}
