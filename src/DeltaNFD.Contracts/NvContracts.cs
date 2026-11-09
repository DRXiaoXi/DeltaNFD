namespace DeltaNFD.Services;

/// <summary>NVIDIA 显卡设置环境信息。</summary>
public sealed class NvGpuEnvironmentInfo
{
    /// <summary>是否检测到 NVIDIA 显卡。</summary>
    public required bool IsNvidia { get; init; }

    /// <summary>显卡名（如 NVIDIA GeForce RTX 4070 Laptop GPU；非 N 卡为空）。</summary>
    public required string GpuName { get; init; }

    /// <summary>NVAPI DRS 是否可用。</summary>
    public required bool NvApiAvailable { get; init; }

    /// <summary>nvdrsdb0/1.bin 是否存在。</summary>
    public required bool DrsFilesExist { get; init; }

    /// <summary>nvdrsdb0/1.bin 是否处于只读锁定状态（任一为只读即视为锁定）。</summary>
    public required bool DrsFilesLocked { get; init; }

    /// <summary>NVAPI 报告的当前驱动版本，如 62101 = 621.01；旧后端/读取失败为 null。</summary>
    public uint? DriverVersion { get; init; }

    /// <summary>只读核对全局及三角洲着色器缓存大小；旧后端缺失时不得当作默认。</summary>
    public NvShaderCacheReport? ShaderCache { get; init; }
}

public sealed record NvShaderCacheScope(string Scope, string State, uint? Value, string Detail);

public static class NvShaderCacheChoices
{
    public static readonly uint?[] Values = [null, 0, 128, 256, 512, 1024, 4096, 5120, 10240, 102400, uint.MaxValue];
    public static readonly string[] Labels = ["驱动默认（推荐）", "暂时关闭缓存（清理后恢复默认）", "128 MB", "256 MB", "512 MB", "1 GB", "4 GB", "5 GB", "10 GB", "100 GB", "无限制"];
    public static bool IsValid(string scope, uint? value) => scope is "global" or "game" && Values.Contains(value);
}

/// <summary>驱动配置快照。非默认按用户要求作为三角洲抽帧兼容性风险提示。</summary>
public sealed class NvShaderCacheReport
{
    public List<NvShaderCacheScope> Scopes { get; init; } = [];
    public bool HasNonDefault => Scopes.Any(s => s.State == "custom");
    public bool Complete => Scopes.Count >= 2 && Scopes.All(s => s.State is "default" or "inherited" or "custom");
    public string Details => string.Join("\n", Scopes.Select(s => s.Scope + "：" + s.Detail));
    public string Advice => HasNonDefault
        ? "检测到着色器缓存大小不是驱动默认。根据三角洲抽帧反馈，请改回「驱动默认」。\n"
            + Details + "\n打开 NVIDIA 控制面板 → 管理 3D 设置，在「全局设置」及「程序设置 → 三角洲」检查着色器缓存大小，改回「驱动默认」并应用；修改后重启游戏，再点「刷新状态」。程序页没有该选项时，移除第三方工具的该项覆盖以继承全局默认。"
            + (Complete ? "" : "\n部分范围无法读取，请同时检查这些范围，不能认为已恢复默认。")
        : Complete ? "着色器缓存大小：全局及三角洲均未检测到非默认设置。\n" + Details
            : "着色器缓存大小无法完整核对，不能确认全局及三角洲是否为默认。\n" + Details;
}

/// <summary>一个驱动设置的候选取值。</summary>
public sealed class NvSettingOption
{
    /// <summary>null 为删除覆盖；0 也是合法的显式驱动值（例如关闭 ReBAR）。</summary>
    public required uint? Value { get; init; }

    public required string Label { get; init; }
}

/// <summary>设置项的呈现方式：Options=下拉候选；PercentScale=范围内任意百分比值（滑条 + 数值）。</summary>
public enum NvSettingKind
{
    Options,

    /// <summary>百分比缩放（如 DLSS 超分渲染比例 33–100% 任意调节）。</summary>
    PercentScale,
}

/// <summary>NVIDIA 显卡设置页的一个设置项定义。</summary>
public sealed class NvSettingDef
{
    /// <summary>DRS 设置 ID；新增项按 NVAPI / Profile Inspector 原始定义核对。</summary>
    public required uint Id { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    public required NvSettingOption[] Options { get; init; }

    /// <summary>联动启用的总开关设置 ID（如 DLSS 系 preset 覆盖需要对应 Enable 项同时开启）；无联动为 0。</summary>
    public uint CompanionEnableId { get; init; }

    /// <summary>呈现方式（默认下拉候选）。</summary>
    public NvSettingKind Kind { get; init; } = NvSettingKind.Options;

    /// <summary>PercentScale 模式的最小值（含）。</summary>
    public int ScaleMin { get; init; }

    /// <summary>PercentScale 模式的最大值（含）。</summary>
    public int ScaleMax { get; init; } = 100;
}

/// <summary>单个设置的覆盖状态（线格式，避免元组序列化歧义）。</summary>
public sealed class NvSettingValueDto
{
    public bool HasOverride { get; set; }

    public uint Value { get; set; }
}

/// <summary>一条驱动设置变更（Value=null 表示恢复默认）。</summary>
public sealed class NvSettingChangeDto
{
    public uint Id { get; set; }

    public uint? Value { get; set; }
}

/// <summary>
/// NVIDIA 显卡设置服务：通过 NVAPI DRS（驱动配置仓库）针对三角洲进程写驱动级设置，
/// 并支持把驱动配置数据库 nvdrsdb0/1.bin 锁定为只读。
/// （自 C3 起，执行实现仅在显卡后端；前端经 IPC 调用，设置目录为静态展示数据。）
/// </summary>
public interface INvProfileService
{
    /// <summary>用户确认后完整重置三角洲专用配置，包含本页未列出的用户覆盖；原值先备份。</summary>
    Task<OperationResult> ResetDeltaProfileAsync()
        => Task.FromResult(OperationResult.Fail("后端未提供防闪屏性重置功能，请更新工具。"));
    /// <summary>用户确认后仅修改缓存大小：global / game；null = 恢复驱动默认。</summary>
    Task<OperationResult> SetShaderCacheSizeAsync(string scope, uint? value)
        => Task.FromResult(OperationResult.Fail("后端未提供着色器缓存处理功能，请更新工具。"));
    /// <summary>读取环境信息（N 卡 / NVAPI / 数据库文件锁定状态），纯只读。</summary>
    Task<NvGpuEnvironmentInfo> GetEnvironmentAsync();

    /// <summary>本页管理的设置目录（含中文说明与候选取值）。</summary>
    IReadOnlyList<NvSettingDef> GetSettingCatalog();

    /// <summary>读取三角洲程序配置中各设置的当前覆盖状态。
    /// 返回：settingId → (是否有覆盖, 覆盖值)。</summary>
    Task<Dictionary<uint, (bool HasOverride, uint Value)>?> GetGameSettingValuesAsync();

    /// <summary>应用一组设置（value=null 表示恢复默认）。写前检查数据库只读锁定。</summary>
    Task<OperationResult> ApplyGameSettingsAsync(IReadOnlyList<(uint Id, uint? Value)> changes, bool rebarWarningAccepted = false);

    /// <summary>把 nvdrsdb0/1.bin 设为只读（true）或解除只读（false）。</summary>
    Task<OperationResult> SetDrsFilesReadOnlyAsync(bool readOnly);
}

/// <summary>
/// NVIDIA 驱动设置目录。既有项曾在 596.36 只读验证；新增项按 NVAPI / Profile Inspector 定义核对，非游戏实效证明。
/// 放在合同程序集：前端只读展示，后端据同一份定义执行，避免两端漂移。
/// </summary>
public static class NvSettingsCatalog
{
    public static readonly IReadOnlyList<NvSettingDef> Definitions = Build();

    private static NvSettingDef[] Build() =>
    [
        new()
        {
            Id = 0x10E41DF3,
            Title = "DLSS 超分模型（SR Preset）",
            Description = "选择 DLSS 超分模型：F 为末代 CNN；J/K 为第一代 Transformer 初版/改进型；M/L 为第二代 Transformer 的低性能消耗/高清晰度模型。设为非默认时会自动同时开启「DLSS-SR 覆盖」总开关；实际效果取决于显卡、驱动与游戏支持。",
            CompanionEnableId = 0x10E41E01,
            Options =
            [
                new NvSettingOption { Value = null, Label = "默认（跟随游戏与驱动）" },
                new NvSettingOption { Value = 6, Label = "F（末代CNN）" },
                new NvSettingOption { Value = 10, Label = "J（第一代Transformer初版）" },
                new NvSettingOption { Value = 11, Label = "K（第一代Transformer改进型）" },
                new NvSettingOption { Value = 13, Label = "M（第二代Transformer低性能消耗）" },
                new NvSettingOption { Value = 12, Label = "L（第二代Transformer高清晰度）" },
            ],
        },
        new()
        {
            Id = 0x10E41DF5,
            Title = "DLSS 超分渲染比例",
            Description = "强制指定内部渲染分辨率百分比，33–100 之间任意值可调：33=超性能、50=性能、66=平衡、77=质量、100=原生分辨率，也可取中间值（如 60）微调画质与帧数。默认=由游戏内 DLSS 档位决定。设为非默认时会自动同时开启「DLSS-SR 覆盖」总开关。",
            CompanionEnableId = 0x10E41E01,
            Kind = NvSettingKind.PercentScale,
            ScaleMin = 33,
            ScaleMax = 100,
            Options =
            [
                new NvSettingOption { Value = null, Label = "默认（游戏内档位决定）" },
                new NvSettingOption { Value = 33, Label = "33%（超高性能）" },
                new NvSettingOption { Value = 50, Label = "50%（性能）" },
                new NvSettingOption { Value = 66, Label = "66%（平衡）" },
                new NvSettingOption { Value = 77, Label = "77%（质量）" },
                new NvSettingOption { Value = 100, Label = "100%（原生分辨率）" },
            ],
        },
        new()
        {
            Id = 0x10E41DF1,
            Title = "帧生成模型（FG Preset）",
            Description = "选择 DLSS 帧生成模型：A（性能与画质适中）、B（画质最高）、C（帧数最高）。设为非默认时会自动同时开启「DLSS-FG 覆盖」总开关；游戏内需开启帧生成，实际效果取决于显卡、驱动与游戏支持。",
            CompanionEnableId = 0x10E41E03,
            Options = BuildPresetOptions(),
        },
        new()
        {
            Id = 0x10308298,
            Title = "Smooth Motion / 帧生成模式",
            Description = "DLSSG 帧生成工作模式：4=Smooth Motion（驱动自动在兼容场景生成平滑帧）；2=强制开启普通帧生成；1=强制关闭（含 Smooth Motion）；0=默认（游戏内自行控制）。",
            Options =
            [
                new NvSettingOption { Value = null, Label = "默认（跟随游戏）" },
                new NvSettingOption { Value = 1, Label = "强制关闭" },
                new NvSettingOption { Value = 2, Label = "强制开启（普通帧生成）" },
                new NvSettingOption { Value = 3, Label = "自动" },
                new NvSettingOption { Value = 4, Label = "Smooth Motion（动态平滑帧生成）" },
            ],
        },
        new()
        {
            Id = 0x10D48A85,
            Title = "抗锯齿透明度（透明采样）",
            Description = "对游戏内的透明纹理（栅栏、植被边缘等）施加额外抗锯齿采样。默认=关闭。多采样档开销小；稀疏网格超采样（SGSSAA）画质最好但性能开销较大。部分新游戏可能忽略该覆盖。",
            Options =
            [
                new NvSettingOption { Value = null, Label = "关闭（默认）" },
                new NvSettingOption { Value = 0x02, Label = "2x 多采样透明" },
                new NvSettingOption { Value = 0x04, Label = "4x 多采样透明" },
                new NvSettingOption { Value = 0x08, Label = "8x 多采样透明" },
                new NvSettingOption { Value = 0x10, Label = "2x 稀疏网格超采样" },
                new NvSettingOption { Value = 0x20, Label = "4x 稀疏网格超采样" },
                new NvSettingOption { Value = 0x30, Label = "8x 稀疏网格超采样" },
            ],
        },
        new()
        {
            Id = 0x10ECECC9,
            Title = "显存回退（CUDA Sysmem Fallback）",
            Description = "显存不足时驱动是否借用系统内存（共享 GPU 内存）：借用可避免爆显存报错，但会带来剧烈掉帧。「偏好不回退」= 显存耗尽宁可报错也不借用，帧数更稳，适合已用显卡伪装降低画质档的情况。",
            Options =
            [
                new NvSettingOption { Value = null, Label = "驱动默认" },
                new NvSettingOption { Value = 1, Label = "偏好不回退（防掉帧）" },
                new NvSettingOption { Value = 2, Label = "偏好回退（防报错）" },
            ],
        },
        new()
        {
            Id = 0x1057EB71,
            Title = "电源管理模式",
            Description = "控制此游戏的 GPU 性能与功耗策略。「最高性能优先」可能增加功耗和温度；恢复默认后由驱动决定。",
            Options =
            [
                new() { Value = null, Label = "驱动默认（删除覆盖）" },
                new() { Value = 0, Label = "自适应" },
                new() { Value = 1, Label = "最高性能优先" },
                new() { Value = 2, Label = "驱动控制" },
                new() { Value = 5, Label = "最佳功耗" },
            ],
        },
        new()
        {
            Id = NvDriverSettingPolicy.AnisoLevelId,
            Title = "各向异性过滤",
            Description = "覆盖斜视纹理的过滤级别；同时设置过滤模式为用户覆盖。部分现代渲染路径可能忽略驱动覆盖，以游戏内画面为准。",
            Options =
            [
                new() { Value = null, Label = "应用程序控制（删除覆盖）" },
                new() { Value = 1, Label = "关闭" },
                new() { Value = 2, Label = "2x" },
                new() { Value = 4, Label = "4x" },
                new() { Value = 8, Label = "8x" },
                new() { Value = 16, Label = "16x" },
            ],
        },
        new()
        {
            Id = 0x0084CD70,
            Title = "各向异性过滤优化",
            Description = "允许驱动采用较省开销的过滤路径，可能影响纹理质量；效果取决于图形 API 与游戏。",
            Options = BuildOnOffOptions(),
        },
        new()
        {
            Id = NvDriverSettingPolicy.MonitorRequestedId,
            Title = "监视器技术",
            Description = "设置此游戏的显示同步技术，同时写入应用请求与有效策略。G-SYNC / ULMB 需要显示器、连接方式与驱动支持；不会替你开启显示器或全局 G-SYNC。",
            Options =
            [
                new() { Value = null, Label = "驱动默认（删除覆盖）" },
                new() { Value = 0, Label = "G-SYNC / G-SYNC Compatible" },
                new() { Value = 4, Label = "固定刷新率" },
                new() { Value = 3, Label = "ULMB（需显示器支持）" },
            ],
        },
        new()
        {
            Id = NvDriverSettingPolicy.RebarId,
            Title = "Resizable BAR",
            Description = "与 NVPI 的 rBAR - Enable 主项一致，与下方 v2 分别设置；修改或恢复默认只处理本项。本项目建议 621.01 及以上驱动开启，较旧或版本未知时需确认。仅控制游戏驱动配置，需要硬件和 BIOS 已支持并启用；读取为开启不代表已验证硬件状态或帧数提升。",
            Options =
            [
                new() { Value = null, Label = "驱动默认（删除覆盖）" },
                new() { Value = 0, Label = "关闭" },
                new() { Value = 1, Label = "开启" },
            ],
        },
        new()
        {
            Id = NvDriverSettingPolicy.RebarPolicyId,
            Title = "Resizable BAR v2",
            Description = "对应 NVPI 的 rBAR - Enable v2，需 616.56 及以上驱动。开启使用 ReBAR 配置，自动使用驱动建议，关闭禁用 ReBAR 策略；驱动默认删除本项覆盖。与上方主项独立，不自动修改大小限制等其他 ReBAR 参数。硬件和 BIOS 仍需支持并启用；不保证性能提升。",
            Options =
            [
                new() { Value = null, Label = "驱动默认（删除覆盖）" },
                new() { Value = 0, Label = "关闭" },
                new() { Value = 1, Label = "自动（驱动建议）" },
                new() { Value = 2, Label = "开启" },
            ],
        },
    ];

    private static NvSettingOption[] BuildOnOffOptions() =>
    [
        new() { Value = null, Label = "驱动默认（删除覆盖）" },
        new() { Value = 0, Label = "关闭" },
        new() { Value = 1, Label = "开启" },
    ];

    private static NvSettingOption[] BuildPresetOptions() =>
    [
        new NvSettingOption { Value = null, Label = "默认（跟随游戏与驱动）" },
        new NvSettingOption { Value = 1, Label = "A（性能与画质适中）" },
        new NvSettingOption { Value = 2, Label = "B（画质最高）" },
        new NvSettingOption { Value = 3, Label = "C（帧数最高）" },
    ];
}
