namespace DeltaNFD.AgentBridge;

public static class AgentAdmission
{
    public static void CheckTweak(string action, DeltaNFD.Services.SystemTweak? tweak, ulong? totalBytes)
    {
        if (action != "system.tweaks.apply") return;
        if (tweak == DeltaNFD.Services.SystemTweak.SysMainMemorySupport)
            throw new InvalidOperationException("该标识仅用于原备份恢复，请使用低内存引导操作。");
        if (totalBytes is > 0 and <= DeltaNFD.Services.LowMemoryStatus.LimitBytes
            && tweak is DeltaNFD.Services.SystemTweak.MemoryCompression or DeltaNFD.Services.SystemTweak.PageCombining)
            throw new InvalidOperationException("16GB及以下内存不开放关闭内存压缩或分页合并，请使用低内存配置引导。");
    }
    public static void Check(AgentAction action, bool enabled, bool maintaining, bool elevated, bool? deltaRunning)
    {
        if (!enabled || maintaining) throw new InvalidOperationException("MCP 已关闭或主程序正在维护，未接收新操作。");
        if (action.Mutating && action.Name != "app.review" && !elevated)
            throw new InvalidOperationException("请以管理员身份打开主程序后再修改；MCP 不自动提权。");
        if ((action.Module is "system" or "gpu" or "runtime" or "ace" || action.Name == "system.low-memory.status") && deltaRunning != false)
            throw new InvalidOperationException(deltaRunning == true ? "请完全退出三角洲后执行该栏目操作。" : "无法确认三角洲运行状态，操作未执行。");
    }
}
