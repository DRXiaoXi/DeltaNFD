namespace DeltaNFD.Services;

/// <summary>
/// 按需创建的共享服务定位器：页面 ViewModel 从这里取服务。
/// </summary>
public static class ServiceLocator
{
    private static readonly Lazy<ISystemOptimizer> SystemOptimizerLazy = new(() => new RealSystemOptimizer());
    private static readonly Lazy<IGpuOptimizer> GpuOptimizerLazy = new(() => new MockGpuOptimizer());
    private static readonly Lazy<IGpuSpoofService> GpuSpoofLazy = new(() => new GpuSpoofService());
    private static readonly Lazy<ISystemTweakService> SystemTweaksLazy = new(() => new SystemTweakService());
    private static readonly Lazy<IFrameService> FrameLazy = new(() => new FrameService());
    private static readonly Lazy<IGameProcessService> GameProcessLazy = new(() => new GameProcessService());
    private static readonly Lazy<IPowerService> PowerLazy = new(() => new PowerService());
    private static readonly Lazy<ICleanupService> CleanupLazy = new(() => new CleanupService());
    private static readonly Lazy<IAdvancedTweakService> AdvancedTweaksLazy = new(() => new AdvancedTweakService());
    private static readonly Lazy<IRuntimeGuardService> GuardLazy = new(() => new RuntimeGuardService());
    private static readonly Lazy<IAceService> AceLazy = new(() => new AceService());
    private static readonly Lazy<IShaderService> ShaderLazy = new(() => new ShaderService());
    private static readonly Lazy<IAppControlService> AppControlLazy = new(() => new AppControlService());
    private static readonly Lazy<ICpuTopologyService> CpuLazy = new(() => new CpuTopologyService());
    private static readonly Lazy<TweakDb.IBxService> BxLazy = new(() => new TweakDb.BxService());
    private static readonly Lazy<INvProfileService> NvProfileLazy = new(() => new NvProfileService());
    private static readonly Lazy<IPagefileService> PagefileLazy = new(() => new PagefileService());
    private static readonly Lazy<GameProcessMonitor> GameMonitorLazy = new(() => new GameProcessMonitor());
    private static readonly Lazy<IUpdateService> UpdateLazy = new(() => new UpdateService());

    internal static GameProcessMonitor GameMonitor => GameMonitorLazy.Value;

    /// <summary>系统优化器（真实实现：进程/内存/电源/游戏提权）。</summary>
    public static ISystemOptimizer SystemOptimizer => SystemOptimizerLazy.Value;

    public static IGpuOptimizer GpuOptimizer => GpuOptimizerLazy.Value;

    /// <summary>显卡型号伪装（真实后端，写注册表 DeviceDesc，需管理员）。</summary>
    public static IGpuSpoofService GpuSpoof => GpuSpoofLazy.Value;

    /// <summary>系统精简优化：Hyper-V/VBS / 内存压缩 / 分页合并 / 预读取 / 游戏微调（真实后端，需管理员）。</summary>
    public static ISystemTweakService SystemTweaks => SystemTweaksLazy.Value;

    /// <summary>帧格：游戏运行期间的自动化帧数保障（一键帧格模式 / 三角洲启动时重启 DWM）。</summary>
    public static IFrameService Frame => FrameLazy.Value;

    /// <summary>游戏进程优化：自动提权 / 真实进程扫描与清理 / 内存整理。</summary>
    public static IGameProcessService GameProcess => GameProcessLazy.Value;

    /// <summary>电源管理：电源计划切换 / 休眠开关。</summary>
    public static IPowerService Power => PowerLazy.Value;

    /// <summary>系统垃圾清理。</summary>
    public static ICleanupService Cleanup => CleanupLazy.Value;

    /// <summary>高级（高危）优化：遥测 / 安全 / 更新 分组开关。</summary>
    public static IAdvancedTweakService AdvancedTweaks => AdvancedTweaksLazy.Value;

    /// <summary>运行库保护：阻止三角洲静默安装 VC++ 14.x 运行库。</summary>
    public static IRuntimeGuardService Guard => GuardLazy.Value;

    /// <summary>ACE 相关：反作弊组件清理。</summary>
    public static IAceService Ace => AceLazy.Value;

    /// <summary>着色器维护：N 卡驱动体检 + PSOCache 旧着色器清理。</summary>
    public static IShaderService Shader => ShaderLazy.Value;

    /// <summary>应用级控制：开机自启、关闭到托盘。</summary>
    public static IAppControlService AppControl => AppControlLazy.Value;

    /// <summary>CPU 拓扑检测与核心锁定（实验室）。</summary>
    public static ICpuTopologyService Cpu => CpuLazy.Value;

    /// <summary>扩展优化库引擎（系统优化子栏目：注册表/服务/任务 + 全局备份恢复）。</summary>
    public static TweakDb.IBxService Bx => BxLazy.Value;

    /// <summary>NVIDIA 显卡设置（NVAPI DRS 针对三角洲的驱动级设置 + nvdrsdb 只读锁定）。</summary>
    public static INvProfileService NvProfile => NvProfileLazy.Value;

    /// <summary>虚拟内存（页面文件）设置：状态读取 / 固定大小设置 / 恢复系统托管。</summary>
    public static IPagefileService Pagefile => PagefileLazy.Value;

    /// <summary>自动更新：检查 update.json / 下载并校验安装包 / 交给 Inno Setup 静默安装。</summary>
    public static IUpdateService Update => UpdateLazy.Value;
}
