using System.Runtime.Intrinsics.X86;
using System.Text.Json;

namespace DeltaNFD.Services;

public sealed partial class PowerService
{
    internal Func<bool>? IsIntelForTests { get; set; }
    private static string PcoreDirectory => Path.Combine(AppDataPaths.Root, "intel-pcore-policies");
    private sealed record PcoreValue(string Guid, int Ac, int Dc);
    private sealed record PcoreBackup(int SchemaVersion, string SchemeGuid, List<PcoreValue> Values, List<string> Attempted, bool Completed);
    private static int PcoreTarget(string guid) => guid == PowerSchemes.HeteroPolicySettings[0].Guid ? 0 : 2;
    private static bool IsIntelProcessor()
    {
        if (!X86Base.IsSupported) return false;
        var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);
        return System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(ebx).Concat(BitConverter.GetBytes(edx)).Concat(BitConverter.GetBytes(ecx)).ToArray()) == "GenuineIntel";
    }

    private static void PcoreNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("P 核策略备份路径不允许链接。"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void SavePcoreBackup(string path, PcoreBackup backup)
    {
        PcoreNoLinks(path);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, backup); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static PcoreBackup LoadPcoreBackup(string path)
    {
        PcoreNoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 65536) throw new InvalidDataException("P 核备份过大。");
        var backup = JsonSerializer.Deserialize<PcoreBackup>(stream) ?? throw new InvalidDataException("P 核备份为空。");
        if (backup.SchemaVersion != 1 || !Guid.TryParse(backup.SchemeGuid, out var scheme)
            || Path.GetFileName(path) != scheme.ToString("D") + ".json" || backup.Values is null || backup.Values.Count != 3 || backup.Attempted is null
            || backup.Values.Select(v => v.Guid).Distinct().Count() != 3
            || backup.Values.Any(v => !PowerSchemes.HeteroPolicySettings.Any(s => s.Guid == v.Guid) || v.Ac < 0 || v.Dc < 0 || v.Ac > (PcoreTarget(v.Guid) == 0 ? 4 : 5) || v.Dc > (PcoreTarget(v.Guid) == 0 ? 4 : 5))
            || backup.Attempted.Count > 6 || backup.Completed && backup.Attempted.Count != 6 || backup.Attempted.Distinct().Count() != backup.Attempted.Count
            || backup.Attempted.Any(k => !backup.Values.Any(v => k == v.Guid + ":ac" || k == v.Guid + ":dc")))
            throw new InvalidDataException("P 核备份格式不匹配，原文件保留。");
        return backup;
    }

    private async Task<List<PcoreValue>> ReadPcoreValuesAsync(string scheme)
    {
        var values = new List<PcoreValue>();
        foreach (var (guid, title) in PowerSchemes.HeteroPolicySettings)
        {
            var (code, stdout, _) = await RunPowerCfgAsync($"/qh {scheme} SUB_PROCESSOR {guid}", TimeSpan.FromSeconds(15));
            var hex = HexValueRegex.Matches(stdout);
            if (code != 0 || hex.Count < 2) throw new IOException($"无法读取「{title}」，没有确认原值时不执行快捷预设。");
            var ac = ParseHexToken(hex[^2].Value); var dc = ParseHexToken(hex[^1].Value);
            var maximum = guid == PowerSchemes.HeteroPolicySettings[0].Guid ? 4 : 5;
            if (ac < 0 || dc < 0 || ac > maximum || dc > maximum) throw new IOException($"「{title}」当前值待核对，未执行快捷预设。");
            values.Add(new(guid, ac, dc));
        }
        return values;
    }

    /// <summary>供已标记只读的后端查询使用；直接读取隐藏设置，不改变属性显隐。</summary>
    public async Task<List<HeteroPolicyInfo>> GetHeteroPoliciesReadOnlyAsync()
    {
        if (_heteroNative is not null) return _heteroNative.Read();
        var scheme = await GetActiveSchemeGuidAsync();
        var result = new List<HeteroPolicyInfo>();
        foreach (var (guid, title) in PowerSchemes.HeteroPolicySettings)
        {
            if (!Guid.TryParse(scheme, out _)) { result.Add(new() { SettingGuid = guid, Title = title }); continue; }
            var (code, stdout, _) = await RunPowerCfgAsync($"/qh {scheme} SUB_PROCESSOR {guid}", TimeSpan.FromSeconds(15));
            var hex = HexValueRegex.Matches(stdout);
            result.Add(new HeteroPolicyInfo
            {
                SettingGuid = guid, Title = title, Supported = code == 0 && hex.Count >= 2,
                AcValue = code == 0 && hex.Count >= 2 ? ParseHexToken(hex[^2].Value) : null,
                DcValue = code == 0 && hex.Count >= 2 ? ParseHexToken(hex[^1].Value) : null,
            });
        }
        return result;
    }

    private async Task WritePcoreValueAsync(string scheme, string setting, string source, int value)
    {
        var (code, stdout, stderr) = await RunPowerCfgAsync($"/set{source}valueindex {scheme} SUB_PROCESSOR {setting} {value}", TimeSpan.FromSeconds(15));
        if (code != 0) throw new IOException("写入异类策略失败：" + FirstLine(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr));
    }

    private async Task RefreshPcoreSchemeAsync(string scheme)
    {
        // Never activate an old plan if the player or another application has switched plans.
        var active = await GetActiveSchemeGuidAsync();
        if (!Guid.TryParse(active, out _)) throw new IOException("无法核对当前活动计划，未执行激活。");
        if (active != scheme) return;
        var (code, _, error) = await RunPowerCfgAsync($"/setactive {scheme}", TimeSpan.FromSeconds(15));
        if (code != 0 || await GetActiveSchemeGuidAsync() != scheme) throw new IOException("计划激活未核对通过：" + FirstLine(error));
    }

    private static bool PcorePowerLockConflict() => AppSettingsStore.Read() is { FrameModeActive: true, FramePowerLockEnabled: true }
        || AppSettingsStore.Read().FramePowerRestorePending;

    public async Task<OperationResult> PreferIntelPcoresAsync()
    {
        if (!_isElevated()) return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        if (!(IsIntelForTests?.Invoke() ?? IsIntelProcessor())) return OperationResult.Fail("此快捷功能仅适用于 Intel 处理器，未修改电源计划。");
        if (OfflineModeGuard.BlocksNormalAutomation) return OperationResult.Fail("旧版设置恢复期间不能应用 P 核优先预设。");
        if (PcorePowerLockConflict()) return OperationResult.Fail("请先关闭帧格电源计划锁定并完成待还原操作，再设置 P 核优先。");
        string path = ""; PcoreBackup? backup = null;
        try
        {
            var scheme = await GetActiveSchemeGuidAsync();
            if (!Guid.TryParse(scheme, out var parsed)) return OperationResult.Fail("无法确认当前电源计划，未修改。");
            scheme = parsed.ToString("D"); path = Path.Combine(PcoreDirectory, scheme + ".json");
            PcoreNoLinks(path); Directory.CreateDirectory(PcoreDirectory);
            PcoreNoLinks(path + ".lock");
            using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (File.Exists(path))
            {
                var previous = LoadPcoreBackup(path);
                var actual = await ReadPcoreValuesAsync(scheme);
                if (previous.Completed && actual.All(v => v.Ac == PcoreTarget(v.Guid) && v.Dc == PcoreTarget(v.Guid)) && await GetActiveSchemeGuidAsync() == scheme)
                    return OperationResult.Ok("当前电源计划已为 P 核优先（0／2／2，交流与电池），原值备份保留。");
                return OperationResult.Fail("此计划已有 P 核策略备份或未完成操作，请先点击还原已备份策略；不覆盖原值记录。");
            }
            backup = new(1, scheme, await ReadPcoreValuesAsync(scheme), [], false);
            SavePcoreBackup(path, backup); // Durable original values before the first powercfg write.
            try
            {
                foreach (var value in backup.Values)
                foreach (var source in new[] { "ac", "dc" })
                {
                    if (await GetActiveSchemeGuidAsync() != scheme || PcorePowerLockConflict()) throw new IOException("活动计划或帧格锁定状态发生变化，已停止后续写入。");
                    backup.Attempted.Add(value.Guid + ":" + source); SavePcoreBackup(path, backup);
                    await WritePcoreValueAsync(scheme, value.Guid, source, PcoreTarget(value.Guid));
                }
                if (await GetActiveSchemeGuidAsync() != scheme) throw new IOException("活动计划发生变化。");
                await RefreshPcoreSchemeAsync(scheme);
                var actual = await ReadPcoreValuesAsync(scheme);
                if (actual.Any(v => v.Ac != PcoreTarget(v.Guid) || v.Dc != PcoreTarget(v.Guid)) || await GetActiveSchemeGuidAsync() != scheme)
                    throw new IOException("三项策略回读不一致，不能报告应用成功。");
                SavePcoreBackup(path, backup with { Completed = true });
                return OperationResult.Ok($"当前电源计划 {scheme} 已验证：生效的异类策略 0；线程与短线程策略均为首选性能处理器（2），交流/电池同时生效。原值已备份。");
            }
            catch (Exception ex)
            {
                var restored = await RestorePcoreBackupAsync(path, backup);
                return restored.Success ? OperationResult.Fail("P 核优先未完成：" + ex.Message + " 已还原原值。")
                    : new OperationResult { OutcomeUnknown = true, IsPartial = true, Message = "P 核优先未完成：" + ex.Message + " " + restored.Message };
            }
        }
        catch (Exception ex) { return OperationResult.Fail("P 核优先未执行或未完成，已有备份保留：" + ex.Message); }
    }

    private async Task<OperationResult> RestorePcoreBackupAsync(string path, PcoreBackup backup)
    {
        try
        {
            var actual = await ReadPcoreValuesAsync(backup.SchemeGuid);
            foreach (var original in backup.Values)
            foreach (var source in new[] { "ac", "dc" })
            {
                if (!backup.Attempted.Contains(original.Guid + ":" + source)) continue;
                var live = actual.Single(v => v.Guid == original.Guid);
                var current = source == "ac" ? live.Ac : live.Dc; var before = source == "ac" ? original.Ac : original.Dc;
                if (current != before && current != PcoreTarget(original.Guid))
                    return OperationResult.Fail("策略已被其他操作更改，未覆盖外部值；备份保留，请核对后再还原。");
            }
            foreach (var original in backup.Values)
            foreach (var source in new[] { "ac", "dc" })
                if (backup.Attempted.Contains(original.Guid + ":" + source))
                    await WritePcoreValueAsync(backup.SchemeGuid, original.Guid, source, source == "ac" ? original.Ac : original.Dc);
            await RefreshPcoreSchemeAsync(backup.SchemeGuid);
            var verified = await ReadPcoreValuesAsync(backup.SchemeGuid);
            if (backup.Values.Any(v => verified.Single(x => x.Guid == v.Guid) is var live
                && (backup.Attempted.Contains(v.Guid + ":ac") && live.Ac != v.Ac || backup.Attempted.Contains(v.Guid + ":dc") && live.Dc != v.Dc)))
                return OperationResult.Fail("恢复回读未通过，备份保留。");
            var archive = Path.Combine(PcoreDirectory, "restored"); PcoreNoLinks(archive); Directory.CreateDirectory(archive);
            File.Move(path, Path.Combine(archive, backup.SchemeGuid + "-" + Guid.NewGuid().ToString("N") + ".json"));
            return OperationResult.Ok("已按备份恢复原交流/电池策略并归档记录。");
        }
        catch (Exception ex) { return OperationResult.Fail("P 核策略恢复未完成，备份保留：" + ex.Message); }
    }

    public IReadOnlyList<ReconcileItem> GetIntelPcoreRecoveryItems()
    {
        try { _ = File.GetAttributes(PcoreDirectory); }
        catch (DirectoryNotFoundException) { return []; }
        catch (FileNotFoundException) { return []; }
        PcoreNoLinks(PcoreDirectory);
        var files = Directory.EnumerateFiles(PcoreDirectory, "*.json").Take(257).ToArray();
        if (files.Length > 256) throw new InvalidDataException("P 核策略备份数量异常，保留记录并停止自动枚举。");
        return files.Select(path =>
        {
            try { var backup = LoadPcoreBackup(path); return new ReconcileItem { Id = backup.SchemeGuid, Kind = "intel-pcore", State = backup.Completed ? "原值备份（操作已验证）" : "待核对", HasBackup = true, Detail = "此处展示持久记录，恢复前重新核对计划实际值。" }; }
            catch (Exception ex) { return new ReconcileItem { Id = Path.GetFileName(path), Kind = "intel-pcore", State = "待核对", HasBackup = true, Detail = ex.Message }; }
        }).ToArray();
    }

    public async Task<OperationResult> RestoreIntelPcoresAsync()
    {
        if (!_isElevated()) return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        if (PcorePowerLockConflict()) return OperationResult.Fail("请先关闭帧格电源锁定并完成待还原操作。");
        var count = 0;
        try
        {
            foreach (var item in GetIntelPcoreRecoveryItems())
            {
                var path = Path.Combine(PcoreDirectory, item.Id + ".json");
                if (!Guid.TryParse(item.Id, out _)) return OperationResult.Fail("存在格式异常的策略备份，原文件保留，请核对。");
                PcoreNoLinks(path + ".lock");
                using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var result = await RestorePcoreBackupAsync(path, LoadPcoreBackup(path));
                if (!result.Success) return result;
                count++;
            }
            return OperationResult.Ok(count == 0 ? "没有需要恢复的 P 核策略备份。" : $"已还原 {count} 个计划的已备份策略，不切换当前活动计划。");
        }
        catch (Exception ex) { return OperationResult.Fail("P 核策略恢复未完成，记录保留：" + ex.Message); }
    }
}
