using System.Runtime.InteropServices;

namespace DeltaNFD.Native;

/// <summary>
/// COM 版文件夹选择对话框（IFileOpenDialog + FOS_PICKFOLDERS）。
/// WinUI 的 FolderPicker 在管理员（提权）进程里会抛 COMException 0x80004005，
/// COM 对话框无此限制，提权/非提权均可用。
/// </summary>
internal static class FolderPickerDialog
{
    private const uint FosPickfolders = 0x20;
    private const uint FosForceFilesystem = 0x40;
    private const uint SigdnFilesyspath = 0x80058000;
    private const int HrCancelled = unchecked((int)0x800704C7);

    /// <summary>弹出文件夹选择框。返回所选目录全路径；用户取消返回 null。</summary>
    public static string? Pick(nint ownerHwnd, string? initialFolder)
    {
        var ownerWasEnabled = ownerHwnd != nint.Zero && IsWindowEnabled(ownerHwnd);
        var dialog = (IFileOpenDialog)new FileOpenDialogCom();
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FosPickfolders | FosForceFilesystem);

            if (!string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder))
            {
                if (SHCreateItemFromParsingName(initialFolder, nint.Zero, typeof(IShellItem).GUID, out var item) == 0)
                {
                    try
                    {
                        dialog.SetFolder(item);
                    }
                    finally
                    {
                        _ = Marshal.ReleaseComObject(item);
                    }
                }
            }

            var hr = dialog.Show(ownerHwnd);

            if (hr == HrCancelled)
            {
                return null; // 用户取消
            }

            if (hr < 0)
            {
                throw Marshal.GetExceptionForHR(hr)!;
            }

            dialog.GetResult(out var result);
            try
            {
                result.GetDisplayName(SigdnFilesyspath, out var path);
                return string.IsNullOrEmpty(path) ? null : path;
            }
            finally
            {
                _ = Marshal.ReleaseComObject(result);
            }
        }
        finally
        {
            _ = Marshal.ReleaseComObject(dialog);
            // WinUI 3 属主窗口偶尔不会被模态对话框重新启用。
            if (ownerWasEnabled && !IsWindowEnabled(ownerHwnd))
            {
                EnableWindow(ownerHwnd, true);
            }
        }
    }

    [ComImport]
    [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialogCom
    {
    }

    [ComImport]
    [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig]
        int Show(nint hwndOwner);

        void SetFileTypes(uint cFileTypes, nint rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(nint pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, uint fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(in Guid guid);
        void ClearClientData();
        void SetFilter(nint pFilter);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint pbc, in Guid bhid, in Guid riid, out nint ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath, nint pbc, in Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(nint hWnd, bool bEnable);
}
