using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>
/// 显卡型号伪装的注册表实现（与常见 bat 伪装脚本逻辑一致）：
/// 扫描 HKLM\SYSTEM\CurrentControlSet\Enum\PCI 下 ClassGUID 为显示类
/// （{4d36e968-e325-11ce-bfc1-08002be10318}）的实例键，改写其 DeviceDesc 值。
/// Enum 键默认对管理员只读，写入时如遇权限不足会自动接管所有权。
/// </summary>
public sealed class GpuSpoofService : IGpuSpoofService
{
    public const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";

    private const string EnumPciPath = @"SYSTEM\CurrentControlSet\Enum\PCI";

    /// <summary>伪装命中标记，与 bat 脚本的 findstr 条目一致。</summary>
    private static readonly string[] MaskMarkers = ["1050 Ti", "RX 540", "GTX 750"];

    private readonly TweakBackupStore _backups;

    public GpuSpoofService() : this(TweakBackupStore.Default)
    {
    }

    public GpuSpoofService(TweakBackupStore backups)
    {
        _backups = backups;
    }

    public IReadOnlyList<string> PresetNames { get; } =
    [
        "NVIDIA GeForce GTX 1050 Ti",
        "AMD Radeon RX 540",
        "NVIDIA GeForce GTX 750",
    ];

    public Task<List<DisplayAdapterInfo>> GetAdaptersAsync()
        => Task.Run(GetAdapters);

    public Task<OperationResult> SpoofAsync(DisplayAdapterInfo adapter, string fakeName)
        => SpoofByPathAsync(adapter.RegistryPath, fakeName);

    public Task<OperationResult> RestoreAsync(DisplayAdapterInfo adapter)
        => RestoreByPathAsync(adapter.RegistryPath);

    public Task<OperationResult> SpoofByPathAsync(string registryPath, string fakeName)
        => Task.Run(() => Spoof(registryPath, fakeName));

    public Task<OperationResult> RestoreByPathAsync(string registryPath)
        => Task.Run(() => Restore(registryPath));

    private List<DisplayAdapterInfo> GetAdapters()
    {
        var result = new List<DisplayAdapterInfo>();

        using var pci = Registry.LocalMachine.OpenSubKey(EnumPciPath);
        if (pci is null)
        {
            return result;
        }

        // Enum\PCI 的结构：Enum\PCI\VEN_xxxx&DEV_xxxx&SUBSYS_xxxx&REV_xx\{实例号}\
        foreach (var deviceKeyName in pci.GetSubKeyNames())
        {
            using var deviceKey = pci.OpenSubKey(deviceKeyName);
            if (deviceKey is null)
            {
                continue;
            }

            foreach (var instanceKeyName in deviceKey.GetSubKeyNames())
            {
                using var instanceKey = deviceKey.OpenSubKey(instanceKeyName);
                if (instanceKey is null)
                {
                    continue;
                }

                if (instanceKey.GetValue("ClassGUID") is not string classGuid ||
                    !string.Equals(classGuid, DisplayClassGuid, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var deviceDesc = instanceKey.GetValue("DeviceDesc") as string ?? "";
                var relativePath = $@"{EnumPciPath}\{deviceKeyName}\{instanceKeyName}";
                var hasBackup = _backups.Get(relativePath, "DeviceDesc") is not null;

                // 伪装判定与“是否有备份”无关（仅备份未修改过的适配器也存在备份记录）：
                // Windows 原生 DeviceDesc 恒为 "@oemXX.inf,%id%;名称" 的 INF 引用格式，
                // 本工具伪装时写入纯文本型号 → 纯文本 = 已伪装；标记词仅作兜底。
                var isMasked =
                    (!string.IsNullOrWhiteSpace(deviceDesc) && !deviceDesc.StartsWith('@'))
                    || MaskMarkers.Any(m => deviceDesc.Contains(m, StringComparison.OrdinalIgnoreCase));

                result.Add(new DisplayAdapterInfo
                {
                    RegistryPath = relativePath,
                    DeviceDesc = deviceDesc,
                    DisplayName = ParseVisibleName(deviceDesc),
                    IsMasked = isMasked,
                    HasBackup = hasBackup,
                });
            }
        }

        return result.OrderBy(a => a.RegistryPath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>"@oem12.inf,%nvidia_dev.2504%;NVIDIA GeForce RTX 4060" → "NVIDIA GeForce RTX 4060"。</summary>
    private static string ParseVisibleName(string deviceDesc)
    {
        if (deviceDesc.StartsWith('@'))
        {
            var separatorIndex = deviceDesc.LastIndexOf(';');
            if (separatorIndex >= 0 && separatorIndex + 1 < deviceDesc.Length)
            {
                return deviceDesc[(separatorIndex + 1)..];
            }
        }

        return deviceDesc;
    }

    private OperationResult Spoof(string registryPath, string fakeName)
    {
        Log.Info($"显卡伪装：写入 {registryPath} → 「{fakeName}」");
        if (string.IsNullOrWhiteSpace(fakeName))
        {
            return OperationResult.Fail("伪装型号不能为空。");
        }

        if (!ElevationHelper.IsElevated)
        {
            Log.Warn("显卡伪装失败：未以管理员运行");
            return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        }

        try
        {
            RegistryAclHelper.EnsureWritable(registryPath);
            using var key = Registry.LocalMachine.OpenSubKey(registryPath, writable: true);
            if (key is null)
            {
                Log.Warn($"显卡伪装失败：打开注册表键失败 HKLM\\{registryPath}");
                return OperationResult.Fail($"打开注册表键失败：HKLM\\{registryPath}");
            }

            var current = key.GetValue("DeviceDesc") as string ?? "";
            // 保留第一次伪装前的原值。连续更换伪装型号时不能把上一个伪装名覆盖成“原始值”，
            // 否则临时模式登录还原后会停留在旧伪装名。
            if (_backups.Get(registryPath, "DeviceDesc") is null)
            {
                _backups.Save(new RegistryValueBackup
                {
                    Id = TweakBackupStore.MakeId(registryPath, "DeviceDesc"),
                    KeyPath = registryPath,
                    ValueName = "DeviceDesc",
                    ValueKind = RegistryValueKind.String,
                    Data = current,
                    CreatedAt = DateTimeOffset.Now,
                });
            }

            if (_backups.Get(registryPath, "DeviceDesc") is null)
            {
                Log.Warn($"显卡伪装失败：无法保存原始型号备份 HKLM\\{registryPath}");
                return OperationResult.Fail("无法保存原始显卡型号备份，未写入伪装。请检查本工具的数据目录权限后重试。");
            }

            key.SetValue("DeviceDesc", fakeName, RegistryValueKind.String);
            Log.Info("显卡伪装：写入成功（重启后生效）");
            return OperationResult.Ok(
                $"已伪装为「{fakeName}」。重启电脑后生效（设备管理器与游戏读取到的名称将改变）。",
                requiresReboot: true);
        }
        catch (UnauthorizedAccessException)
        {
            Log.Warn($"显卡伪装失败：无权限写 HKLM\\{registryPath}");
            return OperationResult.Fail($"没有权限写入 HKLM\\{registryPath}，请确认以管理员身份运行。");
        }
        catch (Exception ex)
        {
            Log.Error("显卡伪装失败", ex);
            return OperationResult.Fail($"伪装失败：{ex.Message}");
        }
    }

    private OperationResult Restore(string registryPath)
    {
        Log.Info($"显卡伪装：恢复原始型号 {registryPath}");
        if (!ElevationHelper.IsElevated)
        {
            Log.Warn("显卡恢复失败：未以管理员运行");
            return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        }

        try
        {
            var backup = _backups.Get(registryPath, "DeviceDesc");
            if (backup is null)
            {
                Log.Warn("显卡恢复失败：无备份记录");
                return OperationResult.Fail("没有本工具记录的原始型号备份，无法恢复。（如果是用其他脚本伪装的，请用该脚本恢复）");
            }

            RegistryAclHelper.EnsureWritable(registryPath);
            using var key = Registry.LocalMachine.OpenSubKey(registryPath, writable: true);
            if (key is null)
            {
                Log.Warn($"显卡恢复失败：打开注册表键失败 HKLM\\{registryPath}");
                return OperationResult.Fail($"打开注册表键失败：HKLM\\{registryPath}");
            }

            key.SetValue("DeviceDesc", backup.Data, RegistryValueKind.String);
            _backups.Remove(registryPath, "DeviceDesc");
            Log.Info("显卡伪装：恢复成功");
            return OperationResult.Ok("已恢复原始显卡型号，重启电脑后生效。", requiresReboot: true);
        }
        catch (UnauthorizedAccessException)
        {
            Log.Warn($"显卡恢复失败：无权限写 HKLM\\{registryPath}");
            return OperationResult.Fail($"没有权限写入 HKLM\\{registryPath}，请确认以管理员身份运行。");
        }
        catch (Exception ex)
        {
            Log.Error("显卡恢复失败", ex);
            return OperationResult.Fail($"恢复失败：{ex.Message}");
        }
    }
}
