namespace DeltaNFD.Services;

/// <summary>
/// 后端操作的统一结果：失败时通过 <see cref="Message"/> 说明原因，不向 UI 层抛异常。
/// </summary>
public sealed class OperationResult
{
    public bool Success { get; init; }

    public string Message { get; init; } = "";

    /// <summary>需要注销登录才能生效（显卡型号伪装）。</summary>
    public bool RequiresLogoff { get; init; }

    /// <summary>需要重启才能生效（内核隔离 / VBS / 内存压缩 / 分页合并 / 预读取）。</summary>
    public bool RequiresReboot { get; init; }

    /// <summary>
    /// 台式机重载显卡后需要使用者在 1 分钟内确认显示器亮屏；
    /// 超时未确认由 UI 层触发自动重启以恢复显示。
    /// </summary>
    public bool RequiresDisplayConfirm { get; init; }

    public static OperationResult Ok(string message, bool requiresLogoff = false, bool requiresReboot = false, bool requiresDisplayConfirm = false)
        => new()
        {
            Success = true,
            Message = message,
            RequiresLogoff = requiresLogoff,
            RequiresReboot = requiresReboot,
            RequiresDisplayConfirm = requiresDisplayConfirm
        };

    public static OperationResult Fail(string message)
        => new() { Success = false, Message = message };
}
