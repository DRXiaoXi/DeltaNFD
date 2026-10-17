using System.Diagnostics;

namespace DeltaNFD.Services.AlphaRestore;

internal sealed record RuntimeServiceState(string Name, int StartType, int State, string Error = "");
internal sealed record RuntimePreflightSnapshot(IReadOnlyList<RuntimeServiceState> Services, bool InstallerBusy,
    bool RebootPending, string Error = "", string WinSxSWarning = "", bool ServicingRebootPending = false)
{
    internal string? Blocker => Error.Length > 0 ? "环境状态无法确认：" + Error
        : Services.Count != 2 ? "安装服务查询不完整，尚未卸载运行库。"
        : Services.FirstOrDefault(s => s.Error.Length > 0 || s.StartType == 4 || s.StartType is not (2 or 3) || s.State is not (1 or 4)) is { } service
            ? service.Error.Length > 0 ? $"服务 {service.Name} 无法查询：{service.Error}" : service.StartType == 4
                ? $"服务 {service.Name} 已禁用；请先确认并恢复服务启动配置，尚未卸载运行库。"
                : $"服务 {service.Name} 的配置/状态无效，未执行运行库操作。"
        : InstallerBusy ? "Windows Installer 正在处理其他事务（可能返回 1618）；等待其结束，不要强杀所有 msiexec。"
        : ServicingRebootPending ? "Windows 系统组件或更新要求重启，请先保存工作并重启后重新检查，尚未卸载运行库。"
        : null; // RebootPending 不再阻塞修复，改为警告
    
    internal string? Warning => (WinSxSWarning.Length > 0 ? "WinSxS 权限警告：" + WinSxSWarning : null)
        ?? (RebootPending ? "Windows 存在待重启记录（可能来自 Windows Update 或其他软件）。若运行库修复失败，建议重启后重试。" : null);
    
    internal static string GetOSDescription()
    {
        var os = Environment.OSVersion;
        return os.Version.Build >= 22000 ? $"Windows 11 Build {os.Version.Build}" : $"Windows 10 Build {os.Version.Build}";
    }
}

internal sealed record RuntimeRepairStep(string Id, string Label, string Branch, string Kind,
    string RelativeFile, VcRedistEntry? UninstallEntry = null);
internal sealed record RuntimeStepResult(int? ExitCode, string Details = "", bool OutcomeUnknown = false,
    bool Skipped = false, bool PostcheckPassed = true, bool RebootPending = false, bool RetainedNewer = false)
{
    internal bool RequiresReboot => RebootPending || ExitCode is 3010 or 1641;
    internal bool Success => !OutcomeUnknown && PostcheckPassed && (Skipped || ExitCode is 0 or 3010 or 1641);
}

internal static class RuntimeRepairCoordinator
{
    internal static readonly TimeSpan TotalBudget = TimeSpan.FromMinutes(80);
}

internal static class RuntimeRepairErrorClassifier
{
    internal static bool IsVersionConflict(int? code) => code is 1638 or unchecked((int)0x80070666);

    internal static string Describe(int? code, string details)
    {
        if (code is null) return "结果未确认：" + details;
        if (details.Contains("0x80070422", StringComparison.OrdinalIgnoreCase))
            return "相关服务被禁用/无法启动（0x80070422）；检查 msiserver 与 TrustedInstaller，不要重复全量卸载。";
        if (details.Contains("0x80070005", StringComparison.OrdinalIgnoreCase))
            return "访问被拒绝（0x80070005）；需要 MSI/CBS 日志确定对象，不要批量重置 WinSxS 权限。";
        if (details.Contains("0x80073712", StringComparison.OrdinalIgnoreCase) || details.Contains("0x80073701", StringComparison.OrdinalIgnoreCase))
            return "组件存储损坏或程序集缺失；先核对 CBS 日志及 Windows 组件健康，不自动接管 WinSxS 或删除组件目录。";
        if (code is 1620 or 1619) return "安装包无法打开或格式无效；检查完整解包、路径及安装内容校验。";
        if (code == 1601) return "Windows Installer 服务无法访问；检查服务和系统日志，不反复卸载其他分支。";
        if (code == 1618) return "其他安装事务正在进行（1618）；等待其结束后再检查，不强杀所有 msiexec。";
        if (code is 1612 or 1706) return "原安装源/缓存缺失；需找回对应原安装器，不删除 Installer/Package Cache。";
        if (IsVersionConflict(code)) return "版本冲突（1638 / 0x80070666），不代表安装成功；需结合安装记录和分支版本复检。";
        if (details.Contains("1935", StringComparison.OrdinalIgnoreCase)) return "程序集提交失败（1935），需根据底层 HRESULT 和 CBS 日志定位服务、权限或组件存储问题。";
        if (code == 1603) return "安装器返回通用失败 1603，详细原因见 MSI 日志；不能直接归因于权限。";
        return $"退出码 {code}；{details}";
    }
}
