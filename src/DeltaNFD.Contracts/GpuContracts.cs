namespace DeltaNFD.Services;

/// <summary>一枚显示适配器（HKLM\SYSTEM\CurrentControlSet\Enum\PCI 下 ClassGUID 为显示类的实例键）。</summary>
public sealed class DisplayAdapterInfo
{
    /// <summary>注册表键路径（HKLM 相对路径），如 SYSTEM\CurrentControlSet\Enum\PCI\VEN_10DE&amp;DEV_2504\4&amp;38a6&amp;0&amp;0008。</summary>
    public required string RegistryPath { get; init; }

    /// <summary>DeviceDesc 原始值，可能形如 "@oem12.inf,%nvidia_dev.2504%;NVIDIA GeForce RTX 4060"。</summary>
    public required string DeviceDesc { get; init; }

    /// <summary>解析后的可见名称（供 UI 展示，如 "NVIDIA GeForce RTX 4060"）。</summary>
    public required string DisplayName { get; init; }

    /// <summary>当前是否已处于伪装状态（DeviceDesc 命中常见伪装型号，或存在本工具的备份）。</summary>
    public bool IsMasked { get; init; }

    /// <summary>是否存在可恢复的原始值备份（由本工具记录）。</summary>
    public bool HasBackup { get; init; }
}

/// <summary>一枚已登记伪装目标的失效信息（驱动变化且名称不再等于期望值）。</summary>
public sealed record GpuSpoofWatchEntry(string RegistryPath, string ExpectedName, string DriverStamp);

public sealed record GpuSpoofInvalidation(GpuSpoofWatchEntry Entry, string CurrentDriverStamp, string CurrentName);

/// <summary>Reconstruction from the installed driver, not a recovered historical backup.</summary>
public sealed record GpuDriverNamePreview(string RegistryPath, string CurrentDescription, string DriverName,
    string InfName, string HardwareId, string Fingerprint);

/// <summary>显卡伪装预置型号（静态展示数据，前端与后端共用，不含执行）。</summary>
public static class GpuSpoofPresets
{
    public static readonly IReadOnlyList<string> Names =
    [
        "NVIDIA GeForce GTX 1050 Ti",
        "AMD Radeon RX 540",
        "NVIDIA GeForce GTX 750",
        "NVIDIA GeForce RTX 2050 Laptop GPU",
    ];
}

/// <summary>
/// 显卡型号伪装服务：改写显示适配器注册表 DeviceDesc（设备管理器、游戏读取到的名字），
/// 可随时恢复原始型号。修改后需注销或重启生效。
/// 注意：只改描述字符串，不改 PCI 硬件 ID / 驱动，显卡实际性能不受影响。
/// （自 C3 起，执行实现仅在显卡后端；前端经 IPC 调用。）
/// </summary>
public interface IGpuSpoofService
{
    /// <summary>预置伪装型号（与常见伪装脚本一致）。</summary>
    IReadOnlyList<string> PresetNames { get; }

    /// <summary>枚举本机所有显示适配器（纯读注册表，无需管理员）。</summary>
    Task<List<DisplayAdapterInfo>> GetAdaptersAsync();

    /// <summary>
    /// 把指定显卡伪装为任意型号字符串（fakeName 可用 <see cref="PresetNames"/> 中的预置项，也可自定义）。
    /// 写入前自动备份原始 DeviceDesc。重启电脑后生效。
    /// </summary>
    Task<OperationResult> SpoofAsync(DisplayAdapterInfo adapter, string fakeName);

    /// <summary>恢复指定显卡的原始 DeviceDesc（需此前伪装过、有备份）。重启电脑后生效。</summary>
    Task<OperationResult> RestoreAsync(DisplayAdapterInfo adapter);

    /// <summary>按注册表路径直接伪装（供帧格流程免选显卡调用）。重启电脑后生效。</summary>
    Task<OperationResult> SpoofByPathAsync(string registryPath, string fakeName);

    /// <summary>按注册表路径恢复原始型号（供帧格流程调用）。重启电脑后生效。</summary>
    Task<OperationResult> RestoreByPathAsync(string registryPath);

    Task<GpuDriverNamePreview> PreviewDriverNameAsync(string registryPath) =>
        Task.FromException<GpuDriverNamePreview>(new NotSupportedException("当前后端不支持按驱动重建型号。"));
    Task<OperationResult> RestoreDriverNameAsync(string registryPath, string fingerprint) =>
        Task.FromResult(OperationResult.Fail("当前后端不支持按驱动重建型号。"));

    /// <summary>检测已登记伪装是否因驱动更新而失效（纯只读）。</summary>
    Task<IReadOnlyList<GpuSpoofInvalidation>> CheckInvalidationAsync();
}
