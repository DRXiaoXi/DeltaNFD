using System.Diagnostics;
using DeltaNFD.Native;

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
}

/// <summary>一个驱动设置的候选取值。</summary>
public sealed class NvSettingOption
{
    public required uint Value { get; init; }

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
    /// <summary>DRS 设置 ID（本机驱动实测验证）。</summary>
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

/// <summary>
/// NVIDIA 显卡设置服务：通过 NVAPI DRS（驱动配置仓库）针对三角洲进程写驱动级设置
/// （DLSS 超分模型 / 渲染比例 / 帧生成模型 / Smooth Motion / 抗锯齿透明度 / 显存回退），
/// 并支持把驱动配置数据库 nvdrsdb0/1.bin 锁定为只读（防止驱动更新或 NVIDIA App 重置优化）。
/// </summary>
public interface INvProfileService
{
    /// <summary>读取环境信息（N 卡 / NVAPI / 数据库文件锁定状态），纯只读。</summary>
    Task<NvGpuEnvironmentInfo> GetEnvironmentAsync();

    /// <summary>本页管理的设置目录（含中文说明与候选取值）。</summary>
    IReadOnlyList<NvSettingDef> GetSettingCatalog();

    /// <summary>读取三角洲程序配置中各设置的当前覆盖状态。
    /// 返回：settingId → (是否有覆盖, 覆盖值)。</summary>
    Task<Dictionary<uint, (bool HasOverride, uint Value)>?> GetGameSettingValuesAsync();

    /// <summary>应用一组设置（value=null 表示恢复默认）。写前检查数据库只读锁定。</summary>
    Task<OperationResult> ApplyGameSettingsAsync(IReadOnlyList<(uint Id, uint? Value)> changes);

    /// <summary>把 nvdrsdb0/1.bin 设为只读（true）或解除只读（false）。</summary>
    Task<OperationResult> SetDrsFilesReadOnlyAsync(bool readOnly);
}

public sealed class NvProfileService : INvProfileService
{
    /// <summary>驱动配置数据库文件（ProgramData 公共位置）。</summary>
    private static readonly string[] DrsFiles =
    [
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NVIDIA Corporation", "Drs", "nvdrsdb0.bin"),
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NVIDIA Corporation", "Drs", "nvdrsdb1.bin"),
    ];

    private static string GameExeName => DeltaForceLocator.GameProcessName + ".exe";

    // ---------------- 设置目录（ID 与取值均在本机驱动 596.36 实测 + NVIDIA Profile Inspector 源码核对） ----------------

    private static readonly NvSettingDef[] Catalog =
    [
        new()
        {
            Id = 0x10E41DF3,
            Title = "DLSS 超分模型（SR Preset）",
            Description = "指定三角洲使用的 DLSS 超分辨率模型版本。常用：F=旧版 CNN 模型；J/K=Transformer 新模型（画质更稳）；最新=始终跟随驱动内最新模型。设为非默认时会自动同时开启「DLSS-SR 覆盖」总开关。",
            CompanionEnableId = 0x10E41E01,
            Options =
            [
                new NvSettingOption { Value = 0, Label = "默认（跟随游戏与驱动）" },
                new NvSettingOption { Value = 1, Label = "A" },
                new NvSettingOption { Value = 2, Label = "B" },
                new NvSettingOption { Value = 3, Label = "C" },
                new NvSettingOption { Value = 4, Label = "D" },
                new NvSettingOption { Value = 5, Label = "E" },
                new NvSettingOption { Value = 6, Label = "F（旧版 CNN）" },
                new NvSettingOption { Value = 7, Label = "G" },
                new NvSettingOption { Value = 8, Label = "H" },
                new NvSettingOption { Value = 9, Label = "I" },
                new NvSettingOption { Value = 10, Label = "J（Transformer）" },
                new NvSettingOption { Value = 11, Label = "K（新版 Transformer）" },
                new NvSettingOption { Value = 12, Label = "L" },
                new NvSettingOption { Value = 13, Label = "M" },
                new NvSettingOption { Value = 14, Label = "N" },
                new NvSettingOption { Value = 15, Label = "O" },
                new NvSettingOption { Value = 0x00FFFFFF, Label = "最新（跟随驱动）" },
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
                new NvSettingOption { Value = 0, Label = "默认（游戏内档位决定）" },
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
            Description = "指定 DLSS 帧生成（FG）的模型版本：A=初版帧生成；B=改进版帧生成。设为非默认时会自动同时开启「DLSS-FG 覆盖」总开关。游戏内需开启帧生成才生效。",
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
                new NvSettingOption { Value = 0, Label = "默认（跟随游戏）" },
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
                new NvSettingOption { Value = 0, Label = "关闭（默认）" },
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
                new NvSettingOption { Value = 0, Label = "驱动默认" },
                new NvSettingOption { Value = 1, Label = "偏好不回退（防掉帧）" },
                new NvSettingOption { Value = 2, Label = "偏好回退（防报错）" },
            ],
        },
    ];

    private static NvSettingOption[] BuildPresetOptions()
    {
        // 帧生成模型只提供 默认/A/B（HANDOFF §25）：C 及更高档与 Default/最新 哨兵值不开放选择。
        // 已装更高档的机器读回时按"已覆盖→自定义值"显示，不崩溃不误写。
        return
        [
            new NvSettingOption { Value = 0, Label = "默认（跟随游戏与驱动）" },
            new NvSettingOption { Value = 1, Label = "A（初版帧生成）" },
            new NvSettingOption { Value = 2, Label = "B（改进版帧生成）" },
        ];
    }

    public IReadOnlyList<NvSettingDef> GetSettingCatalog() => Catalog;

    // ---------------- 环境与状态 ----------------

    public Task<NvGpuEnvironmentInfo> GetEnvironmentAsync() => Task.Run(() =>
    {
        var (isNvidia, gpuName) = ReadNvidiaGpu();
        return new NvGpuEnvironmentInfo
        {
            IsNvidia = isNvidia,
            GpuName = gpuName,
            NvApiAvailable = NvApiDrs.IsAvailable,
            DrsFilesExist = DrsFiles.Any(File.Exists),
            DrsFilesLocked = DrsFiles.Any(f => File.Exists(f) && (File.GetAttributes(f) & FileAttributes.ReadOnly) != 0),
        };
    });

    private static (bool IsNvidia, string GpuName) ReadNvidiaGpu()
    {
        // 注册表显示设备类直读（与着色器服务的驱动检测同源，无 ServiceLocator 依赖）
        try
        {
            using var displayClass = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (displayClass is null)
            {
                return (false, "");
            }

            foreach (var subName in displayClass.GetSubKeyNames())
            {
                if (subName.Length != 4 || !subName.StartsWith("00", StringComparison.Ordinal))
                {
                    continue;
                }

                using var key = displayClass.OpenSubKey(subName);
                if (key?.GetValue("ProviderName") is not string provider ||
                    !provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return (true, key.GetValue("DriverDesc") as string ?? "NVIDIA 显卡");
            }
        }
        catch
        {
            // 读取失败按无 N 卡处理
        }

        return (false, "");
    }

    // ---------------- 读取 / 应用 ----------------

    public Task<Dictionary<uint, (bool HasOverride, uint Value)>?> GetGameSettingValuesAsync() => Task.Run(() =>
    {
        try
        {
            return NvApiDrs.GetApplicationSettings(GameExeName, [.. Catalog.Select(c => c.Id)]);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    });

    public Task<OperationResult> ApplyGameSettingsAsync(IReadOnlyList<(uint Id, uint? Value)> changes) => Task.Run(() =>
    {
        if (!ElevationHelper.IsElevated)
        {
            return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        }

        Log.Info("NV设置：应用 " + string.Join(", ", changes.Select(c => $"0x{c.Id:X8}={(c.Value is null ? "默认" : $"0x{c.Value:X}")}")));

        var env = GetEnvironmentAsync().GetAwaiter().GetResult();
        if (!env.IsNvidia || !env.NvApiAvailable)
        {
            Log.Warn($"NV设置：环境不可用（N卡={env.IsNvidia}，NVAPI={env.NvApiAvailable}）");
            return OperationResult.Fail("未检测到可用的 NVIDIA NVAPI，无法写入驱动设置。");
        }

        if (env.DrsFilesLocked)
        {
            Log.Warn("NV设置：数据库只读锁定中，拒绝写入");
            return OperationResult.Fail("驱动配置数据库（nvdrsdb）处于只读锁定状态——请先在上方解除只读锁定，再应用设置。");
        }

        // 展开联动总开关：SR preset / SR 比例 任一非默认 → 开启 SR Enable；两者都默认 → 删除 SR Enable。FG 同理。
        var expanded = ExpandCompanionChanges(changes);

        var (ok, message) = NvApiDrs.ApplyApplicationSettings(
            GameExeName, "三角洲行动", expanded);
        if (ok)
        {
            Log.Info("NV设置：应用成功 —— " + message);
        }
        else
        {
            Log.Warn("NV设置：应用失败 —— " + message);
        }

        return ok
            ? OperationResult.Ok(message + "立即对之后启动的游戏会话生效。")
            : OperationResult.Fail(message);
    });

    private List<(uint Id, uint? Value)> ExpandCompanionChanges(IReadOnlyList<(uint Id, uint? Value)> changes)
    {
        var result = new List<(uint, uint?)>(changes);

        void HandleCompanion(uint enableId, uint[] dependents)
        {
            // 本次（或既有）依赖项是否为非默认：以本次提交的最终意图为准
            bool AnyNonDefault()
            {
                foreach (var id in dependents)
                {
                    var change = changes.FirstOrDefault(c => c.Id == id);
                    if (change.Id == id)
                    {
                        if (change.Value is > 0)
                        {
                            return true;
                        }

                        continue;
                    }

                    // 本次未改：读现有覆盖状态判断
                    var current = NvApiDrs.GetApplicationSettings(GameExeName, [id]);
                    if (current is not null && current.TryGetValue(id, out var state) && state.HasOverride && state.Value != 0)
                    {
                        return true;
                    }
                }

                return false;
            }

            if (!changes.Any(c => dependents.Contains(c.Id)))
            {
                return; // 依赖项都没动，不动总开关
            }

            result.Add((enableId, AnyNonDefault() ? 1u : null));
        }

        HandleCompanion(0x10E41E01, [0x10E41DF3, 0x10E41DF5]); // DLSS-SR Enable
        HandleCompanion(0x10E41E03, [0x10E41DF1]);             // DLSS-FG Enable
        return result;
    }

    // ---------------- nvdrsdb 只读锁定 ----------------

    public Task<OperationResult> SetDrsFilesReadOnlyAsync(bool readOnly) => Task.Run(() =>
    {
        if (!ElevationHelper.IsElevated)
        {
            return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        }

        Log.Info($"NV设置：数据库只读锁定 → {(readOnly ? "锁定" : "解锁")}");

        var changed = 0;
        var missing = 0;
        var problems = new List<string>();
        foreach (var file in DrsFiles)
        {
            try
            {
                if (!File.Exists(file))
                {
                    missing++;
                    continue;
                }

                var attrs = File.GetAttributes(file);
                var target = readOnly
                    ? attrs | FileAttributes.ReadOnly
                    : attrs & ~FileAttributes.ReadOnly;
                File.SetAttributes(file, target);
                changed++;
            }
            catch (Exception ex)
            {
                problems.Add(Path.GetFileName(file) + ": " + ex.Message);
            }
        }

        if (problems.Count > 0)
        {
            return OperationResult.Fail($"锁定失败：{string.Join("；", problems)}");
        }

        if (changed == 0)
        {
            return OperationResult.Fail("未找到驱动配置数据库文件（nvdrsdb0/1.bin）——驱动可能尚未生成配置。");
        }

        return readOnly
            ? OperationResult.Ok($"已把 {changed} 个驱动配置数据库文件锁定为只读{(missing > 0 ? $"（{missing} 个文件不存在已跳过）" : "")}。" +
                                 "驱动更新 / NVIDIA App / 其他工具都无法再改写驱动配置，你的优化设置不会被重置；需要修改时先回来解锁。")
            : OperationResult.Ok($"已解除 {changed} 个数据库文件的只读锁定，驱动可以正常保存配置了。");
    });
}
