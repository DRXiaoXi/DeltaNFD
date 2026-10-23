using System.Diagnostics;
using DeltaNFD.Native;

namespace DeltaNFD.Services;

/// <summary>
/// NVIDIA 显卡设置执行实现：经 NVAPI DRS 写驱动级设置并管理 nvdrsdb 只读锁定。
/// 合同 DTO、接口与静态设置目录位于 DeltaNFD.Contracts（前端只读展示，后端据同一定义执行）。
/// </summary>
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

    private static string GameExeName => GameTargetService.Default.Current.IsCustom
        ? GameTargetService.Default.Current.ExecutablePath : DeltaForceLocator.GameProcessName + ".exe";

    public IReadOnlyList<NvSettingDef> GetSettingCatalog() => NvSettingsCatalog.Definitions;

    public Task<OperationResult> SetShaderCacheSizeAsync(string scope, uint? value) => Task.Run(() =>
    {
        if (!NvShaderCacheChoices.IsValid(scope, value)) return OperationResult.Fail("缓存范围或大小不合法。");
        if (!ElevationHelper.IsElevated) return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        bool CanWrite() => !DeltaForceLocator.IsGameRunning()
            && !DrsFiles.Any(f => File.Exists(f) && (File.GetAttributes(f) & FileAttributes.ReadOnly) != 0);
        if (!CanWrite()) return OperationResult.Fail("请先退出三角洲，并在 N 卡设置页解除数据库只读锁定。");
        var folder = Path.Combine(AppDataPaths.Root, "logs", "nv-shader-cache"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var (success, message) = NvApiDrs.SetShaderCacheSize(scope, value,
                before => {
                    using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    System.Text.Json.JsonSerializer.Serialize(file, before); file.Flush(true);
                }, CanWrite);
            var report = NvApiDrs.ReadShaderCacheSizes();
            var targets = report.Scopes.Where(s => scope == "game" ? s.Scope.StartsWith("三角洲") : s.Scope.StartsWith("全局")).ToArray();
            var verified = success && targets.Length > 0 && targets.All(s => s.State != "unknown"
                && (value is null ? s.State is "default" or "inherited" || scope == "game" && s.Detail.Contains("继承") : s.Value == value));
            return new OperationResult { Success = verified, OutcomeUnknown = !success && message.Contains("保存未成功") && !report.Complete,
                Message = (verified ? "设置及只读复检通过。" : "未确认设置完整生效，请核对实际状态。") + "\n" + message
                    + "\n" + report.Advice + "\n操作前原值记录：" + path };
        }
        catch (Exception ex) { return OperationResult.Fail("缓存大小设置未完成，请刷新核对：" + ex.Message + "\n原值记录：" + path); }
    });

    // ---------------- 环境与状态 ----------------

    public Task<NvGpuEnvironmentInfo> GetEnvironmentAsync() => DeltaNFD.Inspection.NvEnvironmentInspection.ReadAsync();

    private static NvShaderCacheReport ReadShaderCacheReport()
    {
        try { return NvApiDrs.ReadShaderCacheSizes(); }
        catch (Exception ex)
        {
            Log.Warn("NVIDIA 着色器缓存大小检测失败：" + ex.Message);
            return new() { Scopes = [new("全局与三角洲", "unknown", null, ex.Message)] };
        }
    }

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
        using var targetOperation = GameTargetService.Default.BeginOperation();
        if (GameTargetService.Default.IsCustom && !File.Exists(GameExeName)) return null;
        try
        {
            return NvApiDrs.GetApplicationSettings(GameExeName, [.. NvSettingsCatalog.Definitions.Select(c => c.Id)]);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    });

    public Task<OperationResult> ApplyGameSettingsAsync(IReadOnlyList<(uint Id, uint? Value)> changes) => Task.Run(() =>
    {
        using var targetOperation = GameTargetService.Default.BeginOperation();
        if (GameTargetService.Default.IsCustom && !File.Exists(GameExeName)) return OperationResult.Fail("目标 EXE 不存在，请重新选择。");
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
            GameExeName, GameTargetService.Default.Current.DisplayName, expanded);
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
