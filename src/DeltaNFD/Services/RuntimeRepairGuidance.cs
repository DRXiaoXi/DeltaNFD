namespace DeltaNFD.Services;

internal static class RuntimeRepairGuidance
{
    internal const string ManualSteps = "可自行在 Windows“已安装的应用”中卸载 Microsoft Visual C++ v14 / 2015–2022（14.x）的 x64、x86 项，再回正常模式安装工具自带的 14.44 x64、x86 微软原包。2005–2013 不属于此次手动卸载范围。\n"
        + "操作前请关闭游戏、其他应用和工具的运行库防护；如有安装事务未结束、要求重启或临时 Installer 设置待恢复，请先按原错误提示处理，再手动操作。手动卸载仍失败时，请保留日志与错误信息。\n"
        + "如不希望工具继续提醒，可关闭上方“运行库检测”；这只停止检测和红黄提醒，不会修复或改变系统运行库，也不会关闭运行库防护。";

    internal static string ForResult(OperationResult result, bool cancelled)
    {
        if (result.IsSkipped || cancelled || result.Success && !result.IsPartial && !result.OutcomeUnknown) return "";
        var timing = result.OutcomeUnknown
            ? "本次操作结果尚未确认，请先等待安装事务结束并核对旧操作；确认没有仍在运行的安装事务后，再考虑以下手动方式。\n"
            : result.RequiresReboot ? "本次操作要求重启，请按原提示重启并核对结果，再考虑以下手动方式。\n" : "";
        return "工具未能解决本次运行库问题，无法继续自动修复。\n" + timing + ManualSteps;
    }

    internal static string DialogText(string original, string guidance) => guidance.Length == 0 ? original : original + "\n\n" + guidance;

    internal static string OriginalText(OperationResult result) => string.IsNullOrWhiteSpace(result.DiagnosticsDirectory)
        || result.Message.Contains(result.DiagnosticsDirectory, StringComparison.OrdinalIgnoreCase)
        ? result.Message : result.Message + "\n诊断目录：" + result.DiagnosticsDirectory;
}
