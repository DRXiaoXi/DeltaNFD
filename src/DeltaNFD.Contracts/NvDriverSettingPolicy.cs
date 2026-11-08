namespace DeltaNFD.Services;

/// <summary>共享的设置校验与联动计划；不调用驱动、不修改系统。</summary>
public static class NvDriverSettingPolicy
{
    public const uint RebarId = 0x000F00BA; // NVPI: rBAR - Enable; 0=off, 1=on.
    public const uint RebarPolicyId = 0x000BFA21; // Separate v2 policy: 0=off, 1=auto, 2=all settings.
    public const uint RebarMinimumDriver = 61656;
    public const uint RebarRecommendedDriver = 62101; // 项目建议，非 NVIDIA 兼容性保证。
    public const uint AnisoLevelId = 0x101E61A9;
    public const uint AnisoModeId = 0x10D2BB16;
    public const uint MonitorRequestedId = 0x10A879AC;
    public const uint MonitorEffectiveId = 0x10A879CF;
    public static readonly uint[] CompanionIds = [0x10E41E01, 0x10E41E03, AnisoModeId, MonitorEffectiveId];
    /// <summary>已移除的额外采样优化项，仅允许用户恢复默认时删除旧覆盖。</summary>
    public static readonly uint[] RetiredManagedIds = [0x00E73211];
    public static uint[] ReadIds => NvSettingsCatalog.Definitions.Select(d => d.Id).Concat(CompanionIds).ToArray();

    public static string FormatDriver(uint? version) => version is { } v ? $"{v / 100}.{v % 100:00}" : "未知";
    public static bool IsRebarSetting(uint id) => id is RebarId or RebarPolicyId;
    public static bool IsRebarEnable(uint id, uint? value) => id == RebarId && value == 1 || id == RebarPolicyId && value == 2;

    public static string? RebarWarning(uint? driver, IEnumerable<(uint Id, uint? Value)> changes)
    {
        if (!changes.Any(c => IsRebarEnable(c.Id, c.Value)) || driver >= RebarRecommendedDriver) return null;
        var name = changes.Any(c => c.Id == RebarPolicyId && c.Value == 2) ? "Resizable BAR v2" : "Resizable BAR";
        return $"当前 NVIDIA 驱动：{FormatDriver(driver)}。本项目建议 621.01 及以上版本驱动最适合开启 {name}。\n\n"
            + (driver is null ? "无法确认当前版本，开启后的兼容性与效果尚不明确。" : "较旧驱动开启后可能出现兼容性或性能问题。")
            + "建议先更新驱动；若继续，出现异常可将此项恢复为驱动默认。\n\n"
            + "此开关只设置游戏驱动策略，硬件与 BIOS 仍须支持并启用 ReBAR。";
    }

    public static string? Validate(IReadOnlyList<(uint Id, uint? Value)> changes, uint? driver, bool rebarWarningAccepted)
    {
        if (changes.Select(c => c.Id).Distinct().Count() != changes.Count) return "设置 ID 重复，未写入。";
        foreach (var (id, value) in changes)
        {
            var def = NvSettingsCatalog.Definitions.FirstOrDefault(d => d.Id == id);
            if (def is null)
            {
                if (value is null && (CompanionIds.Contains(id) || RetiredManagedIds.Contains(id))) continue; // 全部恢复默认。
                return $"不允许写入设置 0x{id:X8}。";
            }
            if (value is null) continue;
            if (id == RebarPolicyId && !(driver >= RebarMinimumDriver))
                return "Resizable BAR v2 需要确认的 616.56 及以上驱动；当前版本为 " + FormatDriver(driver) + "，仅允许恢复驱动默认。未写入任何设置。";
            if (def.Kind == NvSettingKind.PercentScale
                ? value < def.ScaleMin || value > def.ScaleMax
                : !def.Options.Any(o => o.Value == value)) return $"「{def.Title}」取值不合法。";
        }
        return !rebarWarningAccepted && RebarWarning(driver, changes) is { } warning
            ? "请先在 N 卡设置页确认 ReBAR 驱动版本警告；未写入任何设置。\n" + warning : null;
    }

    public static List<(uint Id, uint? Value)> Expand(IReadOnlyList<(uint Id, uint? Value)> changes,
        IReadOnlyDictionary<uint, (bool HasOverride, uint Value)> current, uint? driver = null)
    {
        var result = changes.ToDictionary(c => c.Id, c => c.Value);
        void Dlss(uint enable, params uint[] dependents)
        {
            if (!dependents.Any(id => result.ContainsKey(id))) return;
            var enabled = dependents.Any(id => result.TryGetValue(id, out var v) ? v > 0
                : current.TryGetValue(id, out var s) && s.HasOverride && s.Value != 0);
            result[enable] = enabled ? 1u : null;
        }
        Dlss(0x10E41E01, 0x10E41DF3, 0x10E41DF5);
        Dlss(0x10E41E03, 0x10E41DF1);
        if (result.TryGetValue(AnisoLevelId, out var level)) result[AnisoModeId] = level is null ? null : 1u;
        if (result.TryGetValue(MonitorRequestedId, out var technology)) result[MonitorEffectiveId] = technology;
        // ReBAR and v2 are independently visible settings. Preserve explicit/current choices;
        // resetting one row deletes only that override, while full reset includes both rows.
        return result.Select(c => (c.Key, c.Value)).ToList();
    }

    /// <summary>联动值不一致时显示未知覆盖，避免把仅一半生效的设置呈现为已开启。</summary>
    public static Dictionary<uint, (bool HasOverride, uint Value)> Project(
        IReadOnlyDictionary<uint, (bool HasOverride, uint Value)> raw)
    {
        var result = NvSettingsCatalog.Definitions.Where(d => raw.ContainsKey(d.Id)).ToDictionary(d => d.Id, d => raw[d.Id]);
        void Pair(uint primary, uint companion, Func<uint, uint> expected)
        {
            if (!raw.TryGetValue(primary, out var p) || !raw.TryGetValue(companion, out var c)) return;
            if ((p.HasOverride || c.HasOverride) && c.Value != expected(p.Value))
                result[primary] = (true, uint.MaxValue);
            else if (c.HasOverride && !p.HasOverride)
                result[primary] = (true, uint.MaxValue);
        }
        Pair(AnisoLevelId, AnisoModeId, _ => 1u);
        Pair(MonitorRequestedId, MonitorEffectiveId, value => value);
        // Keep both ReBAR raw flags, including unknown values, on their own rows.
        return result;
    }
}
