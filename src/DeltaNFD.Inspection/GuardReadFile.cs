using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DeltaNFD.Services;

namespace DeltaNFD.Inspection;
internal sealed class GuardReadFile : IGuardFiles
{
    private static SafeFileHandle Open(string path)
    {
        var handle = CreateFileW(path, 0x20080u, 7u, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return handle;
    }

    private static string Identity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return $"{info.Volume:X8}:{info.High:X8}{info.Low:X8}";
    }

    private static string Dacl(SafeFileHandle handle)
    {
        var code = GetSecurityInfo(handle, 1, 4, out _, out _, out _, out _, out var descriptor);
        if (code != 0)
            throw new System.ComponentModel.Win32Exception((int)code);
        try
        {
            if (!ConvertSecurityDescriptorToStringSecurityDescriptorW(descriptor, 1, 4, out var text, out _))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                return Marshal.PtrToStringUni(text) ?? throw new IOException("DACL 读取为空。");
            }
            finally
            {
                LocalFree(text);
            }
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    public GuardFileValue Read(string path)
    {
        using var handle = Open(path);
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity)
            throw new IOException("无法确认 UE4 原文件路径。");
        var canonical = buffer.ToString();
        canonical = canonical.StartsWith(@"\\?\UNC\") ? @"\\" + canonical[8..] : canonical.StartsWith(@"\\?\") ? canonical[4..] : canonical;
        return new(canonical, Identity(handle), Dacl(handle));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfoNative
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, High, Low;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfoNative info);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, int type, uint information, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(IntPtr descriptor, uint revision, uint information, out IntPtr text, out uint length);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
