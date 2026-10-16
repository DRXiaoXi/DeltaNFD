using System.Security.Principal;

namespace DeltaNFD.Services;

/// <summary>当前进程是否以管理员身份运行。</summary>
public static class ElevationHelper
{
    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public const string NotElevatedMessage =
        "当前未以管理员身份运行，无法修改系统设置。请右键程序选择「以管理员身份运行」。";
}
