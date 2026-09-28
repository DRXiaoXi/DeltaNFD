using System.Text.Json;

namespace DeltaNFD.Services;

/// <summary>
/// 全部功能开关的共享持久化仓库（%APPDATA%\Delta NFD\settings.json）。
/// 各服务都通过这里读写整份设置，避免各自为政互相覆盖字段。
/// </summary>
public sealed class AppSettings
{
    internal AppSettings Copy() => (AppSettings)MemberwiseClone();
    // ---- 帧格 ----
    public bool DwmRestartOnGameStart { get; set; }
    public bool GpuSpoofEnabled { get; set; }
    public string GpuSpoofApplyMode { get; set; } = "Reboot";
    public string GpuSpoofRegistryPath { get; set; } = "";
    public string GpuSpoofFakeName { get; set; } = "";
    public bool FrameModeActive { get; set; }

    // ---- 显卡伪装：临时重启生效的登录还原登记 ----
    /// <summary>true = 重启后登录时需要还原原显卡型号。</summary>
    public bool TempSpoofRestorePending { get; set; }

    /// <summary>临时显卡伪装的注册表路径（登录还原用）。</summary>
    public string TempSpoofRestorePath { get; set; } = "";

    // ---- 帧格：电源计划锁定 ----
    /// <summary>帧格功能：开启一键帧格模式时锁定电源计划（游戏期间防止被修改），退出时解除。</summary>
    public bool FramePowerLockEnabled { get; set; }

    /// <summary>帧格电源锁定要锁定的目标计划 GUID；空 = 锁定开启时的当前计划（不切换）。</summary>
    public string FramePowerLockTargetGuid { get; set; } = "";

    /// <summary>帧格开启电源锁定时记住的用户原电源计划 GUID（退出时还原）。</summary>
    public string FramePowerPreviousSchemeGuid { get; set; } = "";

    // ---- 帧格：加速系统响应模式（不改电源计划，运行时行为零残留） ----
    /// <summary>总开关：帧格页「加速系统响应模式」。</summary>
    public bool FrameResponseBoostEnabled { get; set; }

    /// <summary>子开关①：核心常驻（PDH 停放状态轮询 + 亲和性轮转，防核心停放），默认开。</summary>
    public bool FrameResponseBoostCoreParkingEnabled { get; set; } = true;

    /// <summary>子开关②：游戏进程禁用电源限制（EcoQoS off），默认开。</summary>
    public bool FrameResponseBoostEcoQosEnabled { get; set; } = true;

    /// <summary>子开关③：帧格+游戏运行期间系统定时器 0.5ms，默认开。</summary>
    public bool FrameResponseBoostTimerEnabled { get; set; } = true;

    // ---- 帧格：内存清理（实验性；清空待备内存列表） ----
    /// <summary>帧格功能：内存清理（standby list 清空），帧格激活时自动触发 + 手动执行；实验性功能默认关。</summary>
    public bool FrameMemoryCleanEnabled { get; set; }

    /// <summary>游戏内自动清理开关：三角洲运行期间内存占用达到阈值时自动清空，默认开。</summary>
    public bool FrameMemoryCleanGameAutoEnabled { get; set; } = true;

    /// <summary>游戏内自动清理阈值：系统内存占用百分比（30–95），达到即触发清理，默认 80。</summary>
    public int FrameMemoryCleanThresholdPercent { get; set; } = 80;

    // ---- 旧版帧格显卡伪装迁移标记（保留以清理升级前的用户配置） ----
    /// <summary>旧版是否保存过临时帧格伪装配置；新版本启动时会清除并必要时还原原型号。</summary>
    public bool GpuSpoofFrameConfigured { get; set; }

    // ---- 帧格：双CCD 调度（实验室页登记为帧格生效后的帧格侧开关） ----
    /// <summary>帧格功能：双CCD 调度（帧格生效登记存在时）是否随帧格模式自动应用，默认开。</summary>
    public bool DualCcdFrameEnabled { get; set; } = true;

    // ---- 双CCD 专属调度（实验室页；帧格生效模式随帧格开关自动启停） ----
    public int DualCcdGameCcdIndex { get; set; }
    public int DualCcdApplyModeIndex { get; set; }

    /// <summary>双CCD「立即生效」是否已启用；应用重启后按所选 CCD 重新应用。</summary>
    public bool DualCcdImmediateEnabled { get; set; }

    /// <summary>单CCD 排除 CPU0 规则是否已启用；应用重启后重新登记并持续应用。</summary>
    public bool SingleCcdExcludeCpu0Enabled { get; set; }

    /// <summary>双CCD调度已登记为「帧格生效」：帧格模式开启时自动应用、退出时自动撤销。</summary>
    public bool DualCcdArmed { get; set; }

    // ---- 游戏进程 ----
    public bool GamePriorityEnabled { get; set; }

    // ---- 常规设置 ----
    public bool CloseToTrayEnabled { get; set; }

    /// <summary>用户选择“下次不再询问”后，关闭窗口时不再提示开启后台驻留。</summary>
    public bool SuppressBackgroundRunPrompt { get; set; }

    // ---- 外观：自定义背景 ----
    /// <summary>背景图片模式总开关：false = 不使用任何背景图（Mica 云母背景），且界面锁定深色主题。</summary>
    public bool BackgroundImageEnabled { get; set; } = true;

    /// <summary>自定义背景图的完整路径；空 = 使用内置 Assets\background.*。</summary>
    public string BackgroundPath { get; set; } = "";

    /// <summary>背景图上的暗色遮罩不透明度（0~1，默认 70%）。</summary>
    public double BackgroundDimOpacity { get; set; } = 0.7;

    // ---- 外观：主题 ----
    /// <summary>应用明暗主题索引：0 浅色 / 1 深色 / 2 跟随系统。</summary>
    public int AppThemeIndex { get; set; } = 1;

    /// <summary>工具主题色（#RRGGBB，空 = 默认天空蓝 #5EB0FF）。</summary>
    public string AccentColor { get; set; } = "#5EB0FF";

    // ---- 三角洲游戏目录 ----
    /// <summary>手动指定的三角洲安装根目录；空 = 自动识别（WeGame 注册表 + 常见路径扫描）。</summary>
    public string GameRootOverride { get; set; } = "";

    // ---- 运行库保护 ----
    public bool RuntimeGuardEnabled { get; set; }

    // ---- 一键优化（真实执行的步骤开关） ----
    public bool OptimizeCleanProcesses { get; set; } = true;
    public bool OptimizeTrimMemory { get; set; } = true;
    public bool OptimizeUltimatePower { get; set; }

    // ---- 电源 ----
    /// <summary>动态导入的卓越性能计划 GUID（切换时缓存）。</summary>
    public string UltimatePowerSchemeGuid { get; set; } = "";

    // ---- 实验室：无省电电源计划 ----
    /// <summary>已导入的无省电电源计划 GUID（导入时缓存）。历史键名，保持兼容不改。</summary>
    public string AtlasPowerSchemeGuid { get; set; } = "";

    // ---- 实验室：处理器场景 ----
    /// <summary>实验室场景选择：0=自动检测（本机）1=Intel 2=AMD 单CCD 3=AMD 双CCD。
    /// 读取端只做钳制（非法回退 0），禁止再做旧格式映射（每次启动重映射曾导致选择逐次降级）。</summary>
    public int LabScenarioIndex { get; set; }

    // ---- 实验室：CPU 亲和性规则（Process Lasso 风格，仅三角洲进程） ----
    /// <summary>启用 CPU 亲和性规则：三角洲运行期间持续保持硬锁核到规则掩码，默认关。</summary>
    public bool GameAffinityRuleEnabled { get; set; }

    /// <summary>CPU 亲和性规则掩码（位 n = CPU n 允许运行）；0 = 未设置（视为全核）。</summary>
    public ulong GameAffinityRuleMask { get; set; }

    /// <summary>已展示过公告的版本标识（与 MainWindow.CurrentAnnouncementVersion 比对，不同则再次弹公告）。</summary>
    public string AnnouncementVersionSeen { get; set; } = "";

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                var settings = JsonSerializer.Deserialize<AppSettings>(stream) ?? new AppSettings();
                settings.BackgroundPath = AppDataPaths.RemapLegacyPath(settings.BackgroundPath);
                return settings;
            }
        }
        catch
        {
            // 损坏时返回默认
        }

        return new AppSettings();
    }

    public static bool Save(string path, AppSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tempPath = path + ".tmp";
            using (var stream = File.Create(tempPath))
            {
                JsonSerializer.Serialize(stream, settings);
            }

            File.Move(tempPath, path, overwrite: true);
            return true;
        }
        catch
        {
            // 保存失败不影响功能运行
            return false;
        }
    }
}

/// <summary>带锁的共享设置访问点（服务直接引用 <see cref="AppSettingsStore.Shared"/>）。</summary>
public static class AppSettingsStore
{
    public static readonly string DefaultPath = Path.Combine(AppDataPaths.Root, "settings.json");

    private static readonly object Gate = new();
    private static readonly Dictionary<string, (AppSettings Settings, (DateTime LastWriteUtc, long Length, bool Exists) Stamp)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static (DateTime LastWriteUtc, long Length, bool Exists) GetStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length, true) : (default, 0, false);
        }
        catch
        {
            return (default, 0, false);
        }
    }

    private static AppSettings ReadCore(string path)
    {
        var stamp = GetStamp(path);
        if (!Cache.TryGetValue(path, out var cached) || cached.Stamp != stamp)
        {
            cached = (AppSettings.Load(path), GetStamp(path));
            Cache[path] = cached;
        }

        return cached.Settings.Copy();
    }

    /// <summary>线程安全地读取一份设置快照。</summary>
    public static AppSettings Read() => Read(DefaultPath);

    public static AppSettings Read(string path)
    {
        lock (Gate)
        {
            return ReadCore(Path.GetFullPath(path));
        }
    }

    /// <summary>线程安全地读出 → 修改 → 原子写回。</summary>
    public static void Update(Action<AppSettings> mutate) => Update(DefaultPath, mutate);

    public static void Update(string path, Action<AppSettings> mutate)
    {
        lock (Gate)
        {
            path = Path.GetFullPath(path);
            var settings = ReadCore(path);
            mutate(settings);
            var saved = AppSettings.Save(path, settings);
            if (saved)
            {
                Cache[path] = (settings.Copy(), GetStamp(path));
            }
            else
            {
                Cache.Remove(path);
            }
        }
    }
}
