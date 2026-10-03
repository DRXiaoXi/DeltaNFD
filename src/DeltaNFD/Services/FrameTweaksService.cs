using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DeltaNFD.Services;

public sealed record FrameIncompletePowerBackup(string FileName, string Path, string Fingerprint, string Reason);

/// <summary>帧格临时改写的一项注册表值原值（跨重启持久化，退出帧格时还原）。</summary>
public sealed class FrameRegBackupEntry
{
    /// <summary>HKLM / HKCU。</summary>
    public string Hive { get; set; } = "HKLM";

    /// <summary>键路径（不含 hive 前缀）。</summary>
    public string KeyPath { get; set; } = "";

    /// <summary>值名。</summary>
    public string ValueName { get; set; } = "";

    /// <summary>0 = 原值不存在（还原时删除）；4 = DWord。</summary>
    public int Kind { get; set; }

    /// <summary>原值的十进制文本（DWord）。</summary>
    public string Data { get; set; } = "";
}

/// <summary>
/// 帧格「前台加速响应」「降低省电延迟」两个临时优化功能：
/// 激活帧格时改写调度/省电注册表值，退出帧格时精确还原为开启前的原值。
/// 原值备份独立持久化在 %APPDATA%\Delta NFD\frame_reg_backup.json（帧格跨重启保持激活，
/// 重启后退出帧格仍能还原），不与系统优化库的备份体系混用，避免污染「一键恢复」。
/// </summary>
public sealed class FrameTweaksService
{
    private const string MultimediaProfilePath =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string PriorityControlPath = @"SYSTEM\CurrentControlSet\Control\PriorityControl";
    private const string NetworkClassPath =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
    private const string UsbControlPath = @"SYSTEM\CurrentControlSet\Control\Usb";

    /// <summary>网卡类键下形如 0000/0001 的实例子键（须存在 *IfType 值，即物理/虚拟网卡）。</summary>
    private static readonly Regex NetworkInstanceRegex = new(@"^00\d\d$", RegexOptions.Compiled);

    private static readonly object Gate = new();
    private static readonly string BackupPath = Path.Combine(AppDataPaths.Root, "frame_reg_backup.json");
    private readonly string _powerBackupRoot;
    private readonly Func<string, TimeSpan, Task<(int Code, string StdOut, string StdErr)>> _powerRunner;
    private readonly Func<string?> _activeSchemeReader;

    public FrameTweaksService() : this(AppDataPaths.Root, RunPowerCfgAsync, ReadActivePowerScheme) { }

    internal FrameTweaksService(string backupRoot,
        Func<string, TimeSpan, Task<(int Code, string StdOut, string StdErr)>> powerRunner,
        Func<string?> activeSchemeReader)
    {
        _powerBackupRoot = backupRoot;
        _powerRunner = powerRunner;
        _activeSchemeReader = activeSchemeReader;
    }

    /// <summary>前台加速响应的调度参数改写是否已经生效（有本功能的备份记录即视为已改写）。</summary>
    public static bool IsForegroundBoostApplied() => HasBackup(b =>
        b.KeyPath == MultimediaProfilePath && b.ValueName == "SystemResponsiveness" ||
        b.KeyPath == PriorityControlPath && b.ValueName == "Win32PrioritySeparation");

    /// <summary>
    /// 降低省电延迟的改写是否已经<b>全部</b>生效：注册表部分有备份记录，且 USB 选择性暂停与
    /// PCIe ASPM 两项电源计划改写都有备份文件。任何一项缺失都返回 false（= 需要重新应用）——
    /// 这里必须从严：若只看注册表备份，PCIe 这一项没生效也会被判为「已应用」，
    /// 开机自愈就会静默跳过新机制的补应用。返回 false 只会让应用流程重跑一次（幂等，原值备份不覆盖）。
    /// </summary>
    public static bool IsPowerSaveLatencyApplied() =>
        HasBackup(b =>
            b.KeyPath == NetworkClassPath && b.ValueName is "EnablePowerManagement" or "PnPCapabilities" ||
            b.KeyPath == UsbControlPath && b.ValueName == "DisableSelectiveSuspend")
        && File.Exists(Path.Combine(AppDataPaths.Root, UsbBackupFile))
        && File.Exists(Path.Combine(AppDataPaths.Root, PcieBackupFile));

    // ---------------- 应用（帧格激活） ----------------

    /// <summary>
    /// 应用全部已启用的帧格临时优化。对每一项：记录原值备份（已有备份不重复记录，
    /// 保证多次激活还原的始终是最初原值）→ 写入优化值。USB 选择性暂停与 PCIe ASPM
    /// 的电源计划部分由 <see cref="ApplyPlanSettingAsync"/> 处理。
    /// <paramref name="laptopExcluded"/> = 机箱形态确认为笔记本时，网卡省电全禁与
    /// 关闭 PCIe 省电一律跳过（记为跳过而非失败，帧格激活状态不受影响）。
    /// </summary>
    public async Task<OperationResult> ApplyAsync(bool foregroundResponsiveness, bool foregroundPriority,
        bool nicPowerOff, bool usbSuspendOff, bool pcieAspmOff, bool laptopExcluded)
    {
        if (!ElevationHelper.IsElevated)
        {
            return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        }

        var notes = new List<string>();
        var failures = new List<string>();
        var skips = new List<string>(); // 形态不适用而跳过的项：既不算成功也不算失败

        // ---- 前台加速响应 ----
        if (foregroundResponsiveness)
        {
            var result = WriteOnce("HKLM", MultimediaProfilePath, "SystemResponsiveness", 0x0A, "系统后台资源预留 10%");
            (result.Success ? notes : failures).Add(result.Message);
        }

        if (foregroundPriority)
        {
            var result = WriteOnce("HKLM", PriorityControlPath, "Win32PrioritySeparation", 0x1A, "前台游戏优先调度");
            (result.Success ? notes : failures).Add(result.Message);
        }

        // ---- 降低省电延迟 ----
        // 笔记本不适用：网卡省电全禁与关闭 PCIe 省电在笔记本上收益不稳定、耗电与发热代价明确，
        // 界面已标「笔记本不适用」并置灰，这里做后端同款拦截（只标不拦就是撒谎）。
        // 跳过不算失败，也不进入 notes 的成功计数，避免污染帧格激活状态文案。
        if (laptopExcluded && (nicPowerOff || pcieAspmOff))
        {
            skips.Add("笔记本不适用，已跳过网卡省电全禁 / 关闭 PCIe 省电");
        }
        else
        {
            if (nicPowerOff)
            {
                var (done, failed) = ApplyNicPowerOff();
                if (done > 0) notes.Add($"网卡省电全禁（{done} 项）");
                if (failed.Count > 0) failures.Add("网卡省电：" + string.Join("；", failed));
            }

            if (pcieAspmOff)
            {
                var pcieResult = await ApplyPcieAspmPowerSchemeAsync();
                if (pcieResult.Success)
                {
                    notes.Add("关闭 PCIe 省电（链接状态电源管理 ASPM）");
                }
                else
                {
                    failures.Add(pcieResult.Message);
                }
            }
        }

        if (usbSuspendOff)
        {
            var result = WriteOnce("HKLM", UsbControlPath, "DisableSelectiveSuspend", 1, "USB 选择性暂停关闭（全局）");
            (result.Success ? notes : failures).Add(result.Message);

            var powerResult = await ApplyUsbPowerSchemeAsync();
            if (!powerResult.Success)
            {
                failures.Add(powerResult.Message);
            }
        }

        var skipText = skips.Count == 0 ? "" : " " + string.Join("；", skips) + "。";
        Log.Info($"帧格临时优化：应用完成 —— 成功 {notes.Count} 项（{string.Join("，", notes)}）；" +
            $"跳过 {skips.Count} 项（{string.Join("，", skips)}）；失败 {failures.Count} 项");
        return failures.Count == 0
            ? OperationResult.Ok((notes.Count == 0 ? "没有需要应用的临时优化。" : $"已应用 {notes.Count} 项帧格临时优化。") + skipText)
            : OperationResult.Fail($"帧格临时优化部分失败：{string.Join("；", failures)}" + skipText);
    }

    /// <summary>还原全部帧格临时优化（退出帧格时调用；没有备份 = 没改过，静默成功）。</summary>
    public async Task<OperationResult> RevertAsync()
    {
        var entries = ReadBackup();

        // 注册表备份为空也要检查电源计划备份（注册表部分失败但 powercfg 成功的半生效场景；
        // 笔记本跳过网卡/PCIe 但 USB 部分生效的情况下也只剩电源计划备份）
        if (entries.Count == 0)
        {
            return await RevertPlanSettingsAsync();
        }

        var failures = new List<string>();
        var failedEntries = new List<FrameRegBackupEntry>();
        var restored = 0;
        foreach (var entry in entries)
        {
            try
            {
                using var key = OpenWritable(entry.Hive, entry.KeyPath);
                if (key is null)
                {
                    continue; // 键已不存在（设备移除等），无需还原
                }

                if (entry.Kind == 0)
                {
                    key.DeleteValue(entry.ValueName, throwOnMissingValue: false);
                }
                else if (int.TryParse(entry.Data, out var original))
                {
                    key.SetValue(entry.ValueName, original, RegistryValueKind.DWord);
                }
                else
                {
                    failures.Add($"{entry.ValueName} 备份值无效");
                    failedEntries.Add(entry);
                    continue;
                }

                restored++;
            }
            catch (Exception ex)
            {
                failures.Add($"{entry.ValueName}: {ex.Message}");
                failedEntries.Add(entry);
            }
        }

        // 电源计划部分按备份还原（帧格改的是备份里记录的那个计划；
        // 用户期间手动切换计划导致的差异不属于帧格的改动范围）——USB 选择性暂停 + PCIe ASPM
        var planPower = await RevertPlanSettingsAsync();
        if (!planPower.Success)
        {
            failures.Add(planPower.Message);
        }

        if (failures.Count == 0)
        {
            File.Delete(BackupPath);
            Log.Info($"帧格临时优化：已还原 {restored} 项注册表值" +
                (planPower.Success && planPower.Message.Length > 0 ? " + 电源计划设置" : ""));
            return OperationResult.Ok(restored > 0 ? $"已还原 {restored} 项帧格临时优化。" : planPower.Message);
        }

        // 有失败项：保留未成功还原的备份，下次退出/自愈重试
        SaveBackup(failedEntries);
        Log.Warn($"帧格临时优化：还原失败 {failures.Count} 项 —— {string.Join("；", failures)}");
        return OperationResult.Fail($"帧格临时优化还原失败：{string.Join("；", failures)}（重启后再次退出帧格会重试）");
    }

    // ---------------- USB 选择性暂停 / PCIe ASPM 电源计划部分（powercfg，写当前激活计划） ----------------

    private const string PowerSchemesPath = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
    private const string UsbSubgroup = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string UsbSuspendSetting = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";

    /// <summary>PCIe 子组「PCI Express」。</summary>
    private const string PcieSubgroup = "501a4d13-42af-4429-9fd1-a8218c268e20";

    /// <summary>PCIe 设置「链接状态电源管理」（ASPM，Link State Power Management）。</summary>
    private const string PcieAspmSetting = "ee12f906-d277-404b-b6da-e5fa1a576df5";

    /// <summary>ASPM「关闭」值：0 = 关闭链接状态电源管理。</summary>
    private const int PcieAspmOffValue = 0;

    private const string UsbBackupFile = "frame_usb_power.json";
    private const string PcieBackupFile = "frame_pcie_power.json";

    public async Task<IReadOnlyList<FrameIncompletePowerBackup>> GetIncompletePowerBackupsAsync()
    {
        var result = new List<FrameIncompletePowerBackup>();
        foreach (var name in new[] { UsbBackupFile, PcieBackupFile })
        {
            var path = Path.Combine(_powerBackupRoot, name);
            if (!PowerFileExists(path)) continue;
            var bytes = await ReadPowerBackupBytesAsync(path);
            if (!IncompletePowerBackup(bytes, out var reason)) continue;
            result.Add(new(name, path, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), reason));
        }
        return result;
    }

    private static bool IncompletePowerBackup(byte[] bytes, out string reason)
    {
        try
        {
            var backup = System.Text.Json.JsonSerializer.Deserialize<PowerSettingBackup>(bytes);
            reason = $"计划={backup?.Scheme ?? "(缺失)"}；AC={backup?.Ac?.ToString() ?? "(缺失)"}；DC={backup?.Dc?.ToString() ?? "(缺失)"}";
            return backup is null || !Guid.TryParse(backup.Scheme, out _) || !ValidPowerValue(backup.Ac) || !ValidPowerValue(backup.Dc);
        }
        catch (System.Text.Json.JsonException) { reason = "JSON 损坏，无法取得可靠原值"; return true; }
    }

    private static async Task<byte[]> ReadPowerBackupBytesAsync(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("备份路径是重解析点。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 1024 * 1024) throw new IOException("备份超过 1MiB，未接管。");
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes);
        return bytes;
    }

    /// <summary>Requires explicit consent: retain current values, archive lost originals, never claim restoration.</summary>
    public async Task<OperationResult> PreserveCurrentPowerValuesAsync(IReadOnlyList<FrameIncompletePowerBackup> confirmed)
    {
        try
        {
            if (confirmed.Count is < 1 or > 2 || confirmed.Select(x => x.FileName).Distinct().Count() != confirmed.Count)
                return OperationResult.Fail("确认清单无效，未移动备份。");
            foreach (var item in confirmed)
            {
                if (item.FileName is not (UsbBackupFile or PcieBackupFile)) return OperationResult.Fail("拒绝处理其他备份。");
                var path = Path.Combine(_powerBackupRoot, item.FileName);
                var bytes = await ReadPowerBackupBytesAsync(path);
                if (!IncompletePowerBackup(bytes, out _) || Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) != item.Fingerprint)
                    return OperationResult.Fail("备份已变化，请重新确认；未移动备份。");
            }
            var directory = Path.Combine(_powerBackupRoot, "unresolved-frame-power");
            Directory.CreateDirectory(directory);
            foreach (var ancestor in new[] { _powerBackupRoot, directory })
                if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0) throw new IOException("归档目录含重解析点。");
            foreach (var item in confirmed)
            {
                var path = Path.Combine(_powerBackupRoot, item.FileName);
                var bytes = await ReadPowerBackupBytesAsync(path);
                if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) != item.Fingerprint)
                    throw new IOException("确认后备份已变化，未继续移动。");
                var archived = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + "-" + item.FileName);
                File.Move(path, archived); // No deletion or replacement of evidence, and no powercfg writes.
                var actual = await ReadPowerBackupBytesAsync(archived);
                if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(actual)) != item.Fingerprint)
                    throw new IOException("归档复核失败，证据仍保留：" + archived);
                Log.Warn("帧格旧电源备份已按用户确认归档；当前值保留，未恢复未知原值：" + archived + "；" + item.Reason);
            }
            return OperationResult.Ok("已保留当前电源值并归档旧记录（不是还原）；证据目录：" + directory);
        }
        catch (Exception ex) { return OperationResult.Fail("旧备份处理未完成，证据保留：" + ex.Message); }
    }

    /// <summary>
    /// 一项电源计划设置的 AC/DC 原值备份。每个设置各自一个文件
    /// （frame_usb_power.json / frame_pcie_power.json），不与注册表备份文件混用，
    /// 也不与系统优化库的备份体系混用，避免污染「一键恢复」。
    /// 值用 long：DWORD 索引可到 0xFFFFFFFF（如「永不关闭硬盘」），int 会溢出读错。
    /// </summary>
    private sealed class PowerSettingBackup
    {
        public string Scheme { get; set; } = "";
        public long? Ac { get; set; }
        public long? Dc { get; set; }
    }

    /// <summary>
    /// 由帧格「降低省电延迟」在帧格期间<b>临时</b>改写、退出帧格时还原的电源计划设置。
    /// 电源计划「内容锁定」的快照与差异校验都必须排除这些项：否则 30 秒守护会把帧格的
    /// 临时改写改回原值，而退出帧格时的还原又会和锁定互相覆盖 —— 两套机制互相打架。
    /// 这是唯一的排除清单定义处（快照与守护差异校验都引用它）。
    /// </summary>
    private static readonly (string Subgroup, string Setting)[] FrameManagedPowerSettings =
    [
        (UsbSubgroup, UsbSuspendSetting), // USB 选择性暂停（帧格临时改写为关闭）
        (PcieSubgroup, PcieAspmSetting),  // PCIe 链接状态电源管理 ASPM（帧格临时改写为关闭）
    ];

    /// <summary>该电源计划设置是否由帧格临时改写（⇒ 电源计划内容锁定必须跳过）。</summary>
    private static bool IsFrameManagedPowerSetting(string subgroup, string setting, bool usb, bool pcie) =>
        FrameManagedPowerSettings.Any(s =>
            s.Subgroup.Equals(subgroup, StringComparison.OrdinalIgnoreCase) &&
            s.Setting.Equals(setting, StringComparison.OrdinalIgnoreCase) &&
            ((usb && s.Setting == UsbSuspendSetting) || (pcie && s.Setting == PcieAspmSetting)));

    /// <summary>与 PowerService.HexValueRegex 同构：输出末尾两个 0x 值依次为交流/直流当前值（本地化安全）。</summary>
    private static readonly Regex PowerHexValueRegex = new("0x[0-9A-Fa-f]{1,8}", RegexOptions.Compiled);

    /// <summary>
    /// 把当前激活计划里的一项设置临时改写成目标值：首次改写前备份原 AC/DC 值（已有备份不重复记录，
    /// 保证多次激活还原的始终是最初原值）→ 写 AC/DC → /setactive 让改动生效。
    /// USB 选择性暂停与 PCIe ASPM 共用本流程，各自使用独立的备份文件。
    /// </summary>
    internal async Task<OperationResult> ApplyPlanSettingAsync(
        string subgroup, string setting, string backupFile, string label, int targetValue)
    {
        try
        {
            var scheme = _activeSchemeReader();
            if (scheme is null)
            {
                return OperationResult.Fail($"无法读取当前电源计划，{label}未修改。");
            }

            var backupPath = Path.Combine(_powerBackupRoot, backupFile);
            if (!PowerFileExists(backupPath))
            {
                var (ac, dc) = await QueryPlanSettingValuesAsync(scheme, subgroup, setting);
                if (!ValidPowerValue(ac) || !ValidPowerValue(dc))
                    return OperationResult.Fail($"{label} 原值读取失败，未修改设置（不能用默认值代替原值）。");
                var backup = new PowerSettingBackup { Scheme = scheme, Ac = ac, Dc = dc };
                await SavePowerJsonOnceAsync(backupPath, backup);
            }

            var existing = System.Text.Json.JsonSerializer.Deserialize<PowerSettingBackup>(await File.ReadAllTextAsync(backupPath));
            if (existing is null || !existing.Scheme.Equals(scheme, StringComparison.OrdinalIgnoreCase)
                || !ValidPowerValue(existing.Ac) || !ValidPowerValue(existing.Dc))
                return OperationResult.Fail($"{label} 备份无效或属于另一个计划，请先恢复旧记录；未继续修改。");

            foreach (var mode in new[] { "ac", "dc" })
            {
                var (code, _, _) = await _powerRunner(
                    $"/set{mode}valueindex {scheme} {subgroup} {setting} {targetValue}", TimeSpan.FromSeconds(20));
                if (code != 0)
                {
                    return OperationResult.Fail($"{label} {mode.ToUpperInvariant()} 设置失败（退出码 {code}）。");
                }
            }

            var (activeCode, _, _) = await _powerRunner($"/setactive {scheme}", TimeSpan.FromSeconds(20));
            var applied = await QueryPlanSettingValuesAsync(scheme, subgroup, setting);
            return activeCode == 0 && applied.Ac == targetValue && applied.Dc == targetValue
                ? OperationResult.Ok("")
                : OperationResult.Fail($"{label} 电源计划刷新或写后复检失败（退出码 {activeCode}），已保留原值备份。");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"{label} 设置失败：" + ex.Message);
        }
    }

    /// <summary>关闭 USB 选择性暂停（当前计划的 AC/DC，原值备份到 frame_usb_power.json）。</summary>
    private Task<OperationResult> ApplyUsbPowerSchemeAsync() =>
        ApplyPlanSettingAsync(UsbSubgroup, UsbSuspendSetting, UsbBackupFile, "USB 选择性暂停", 0);

    /// <summary>关闭 PCIe 链接状态电源管理（ASPM，当前计划的 AC/DC，原值备份到 frame_pcie_power.json）。</summary>
    private Task<OperationResult> ApplyPcieAspmPowerSchemeAsync() =>
        ApplyPlanSettingAsync(PcieSubgroup, PcieAspmSetting, PcieBackupFile, "PCIe 链接状态电源管理", PcieAspmOffValue);

    /// <summary>
    /// 按备份还原一项电源计划设置（没有备份 = 没改过，静默成功）。
    /// 原值不完整时保留记录并报错，不猜测用户原来的电源值。
    /// </summary>
    internal async Task<OperationResult> RevertPlanSettingAsync(
        string subgroup, string setting, string backupFile, string label)
    {
        try
        {
            var backupPath = Path.Combine(_powerBackupRoot, backupFile);
            if (!PowerFileExists(backupPath))
            {
                return OperationResult.Ok(""); // 没改过电源计划
            }

            var backup = System.Text.Json.JsonSerializer.Deserialize<PowerSettingBackup>(
                await File.ReadAllTextAsync(backupPath));
            if (backup is null || !Guid.TryParse(backup.Scheme, out _) || !ValidPowerValue(backup.Ac) || !ValidPowerValue(backup.Dc))
            {
                Log.Warn($"帧格电源恢复阻止：文件={backupPath}；计划={backup?.Scheme}；AC={backup?.Ac}；DC={backup?.Dc}；未执行 powercfg 写入。");
                return OperationResult.Fail($"{label} 原值备份不完整，已保留记录，未使用猜测默认值恢复。备份：{backupPath}");
            }

            foreach (var (mode, value) in new[] { ("ac", backup.Ac), ("dc", backup.Dc) })
            {
                var restored = value!.Value;
                var (code, _, _) = await _powerRunner(
                    $"/set{mode}valueindex {backup.Scheme} {subgroup} {setting} {restored}", TimeSpan.FromSeconds(20));
                if (code != 0)
                {
                    return OperationResult.Fail($"{label} {mode.ToUpperInvariant()} 还原失败（退出码 {code}）。");
                }
            }

            // 只有该计划仍处于激活状态时才 /setactive 刷新（该命令会把计划切成活动计划）。
            // 帧格退出顺序是「先解除电源锁定（切回用户原计划）」再「还原本服务改写的省电项」，
            // 若此处无条件 /setactive，会把刚还原的用户原计划又切回被锁定的目标计划。
            // 计划已不再激活时只写回原值即可（用户日后切到该计划时自然生效）。
            var activeScheme = _activeSchemeReader();
            if (activeScheme is null)
                return OperationResult.Fail($"{label} 原值已写回，但当前活动计划无法确认，已保留备份且未切换计划。");
            if (activeScheme.Equals(backup.Scheme, StringComparison.OrdinalIgnoreCase))
            {
                var (activeCode, _, _) = await _powerRunner($"/setactive {backup.Scheme}", TimeSpan.FromSeconds(20));
                if (activeCode != 0)
                {
                    return OperationResult.Fail($"原电源计划激活失败（退出码 {activeCode}）。");
                }
            }
            else
            {
                Log.Info($"帧格临时优化：{label} 原值已写回计划 {backup.Scheme}（当前激活计划为 {activeScheme}，不做切换）");
            }

            var checkedValues = await QueryPlanSettingValuesAsync(backup.Scheme, subgroup, setting);
            if (checkedValues.Ac != backup.Ac || checkedValues.Dc != backup.Dc)
                return OperationResult.Fail($"{label} 还原后复检不符，已保留备份。");
            File.Delete(backupPath);
            return OperationResult.Ok($"{label} 已还原。");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"{label} 还原失败：" + ex.Message);
        }
    }

    /// <summary>还原本服务改写的全部电源计划设置（USB 选择性暂停 + PCIe ASPM）。</summary>
    private async Task<OperationResult> RevertPlanSettingsAsync()
    {
        var messages = new List<string>();
        var failures = new List<string>();

        var usb = await RevertPlanSettingAsync(
            UsbSubgroup, UsbSuspendSetting, UsbBackupFile, "USB 选择性暂停");
        (usb.Success ? messages : failures).Add(usb.Message);

        var pcie = await RevertPlanSettingAsync(
            PcieSubgroup, PcieAspmSetting, PcieBackupFile, "PCIe 链接状态电源管理");
        (pcie.Success ? messages : failures).Add(pcie.Message);

        if (failures.Count > 0)
        {
            var ok = string.Join(" ", messages.Where(m => m.Length > 0));
            return OperationResult.Fail(string.Join("；", failures) + (ok.Length > 0 ? " " + ok : ""));
        }

        return OperationResult.Ok(string.Join(" ", messages.Where(m => m.Length > 0)));
    }

    private static string? ReadActivePowerScheme()
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(PowerSchemesPath);
            var scheme = root?.GetValue("ActivePowerScheme")?.ToString();
            return Guid.TryParse(scheme, out _) ? scheme : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>查询指定计划里一项设置的 AC/DC 值（powercfg /q 输出按 0x 值序列解析，
    /// 不依赖本地化文案；读取失败返回 null 表示原值不存在）。</summary>
    private async Task<(long? Ac, long? Dc)> QueryPlanSettingValuesAsync(
        string scheme, string subgroup, string setting)
    {
        var (code, stdout, _) = await _powerRunner(
            $"/q {scheme} {subgroup} {setting}", TimeSpan.FromSeconds(20));
        if (code != 0 || stdout is null)
        {
            return (null, null);
        }

        var matches = PowerHexValueRegex.Matches(stdout);
        if (matches.Count < 2)
        {
            return (null, null);
        }

        return (ParseHexAt(matches, matches.Count - 2), ParseHexAt(matches, matches.Count - 1));
    }

    /// <summary>MatchCollection 中第 index 个 0x 令牌的数值；解析失败返回 null。</summary>
    private static long? ParseHexAt(MatchCollection matches, int index) =>
        TryParseHexToken(matches[index].Value, out var value) ? value : null;

    /// <summary>
    /// 把 powercfg 输出里的 0x… 十六进制令牌解析为数值。
    /// 注意：<see cref="System.Globalization.NumberStyles.HexNumber"/> 本身不认 "0x" 前缀，
    /// 必须先去前缀再解析（PowerService.ParseHexToken 也是这么做的）——
    /// 否则每个值都会解析失败，被当成「原值不存在」，USB/PCIe 还原会写成默认值、
    /// 电源计划内容快照也会变成 0 项（内容守护静默失效）。
    /// 传 long：DWORD 索引最大 0xFFFFFFFF，int 会溢出。
    /// </summary>
    private static bool TryParseHexToken(string token, out long value) =>
        long.TryParse(
            token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? token[2..] : token,
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out value);

    // ---------------- 电源计划「内容锁定」：快照 / 差异校验 / 自动还原 ----------------

    /// <summary>计划内容快照文件（与帧格其它备份文件同目录；解除锁定时删除）。</summary>
    private string SchemeContentSnapshotPath => Path.Combine(_powerBackupRoot, "frame_power_scheme_snapshot.json");
    public bool HasPowerSchemeContentSnapshot
    {
        get { try { return PowerFileExists(SchemeContentSnapshotPath); } catch { return true; } }
    }

    private static bool PowerFileExists(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                throw new IOException("恢复记录路径不是文件：" + path);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static bool ValidPowerValue(long? value) => value is >= 0 and <= uint.MaxValue;

    private static async Task SavePowerJsonOnceAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, System.Text.Json.JsonSerializer.Serialize(value));
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { /* 保留已存在的首次原值，由调用方再校验。 */ }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool ValidSnapshot(SchemeContentSnapshot? snapshot) => snapshot is not null
        && Guid.TryParse(snapshot.Scheme, out _) && snapshot.Settings is { Count: > 0 }
        && snapshot.Settings.All(s => Guid.TryParse(s.Subgroup, out _) && Guid.TryParse(s.Setting, out _)
            && ValidPowerValue(s.Ac) && ValidPowerValue(s.Dc))
        && snapshot.Settings.Select(s => s.Subgroup + "/" + s.Setting).Distinct(StringComparer.OrdinalIgnoreCase).Count() == snapshot.Settings.Count;

    /// <summary>与 PowerService 同构的 GUID 正则：powercfg /q 输出里的 GUID 不随系统语言变化。</summary>
    private static readonly Regex GuidTokenRegex = new(
        "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    /// <summary>快照里的一项设置（子组 / 设置 GUID + 交流/直流当前值）。
    /// 值用 long：DWORD 索引可到 0xFFFFFFFF，int 解析会溢出并把该项读错。</summary>
    private sealed class SchemeSettingEntry
    {
        public string Subgroup { get; set; } = "";
        public string Setting { get; set; } = "";
        public long? Ac { get; set; }
        public long? Dc { get; set; }
    }

    /// <summary>一个电源计划的设置明细快照。</summary>
    private sealed class SchemeContentSnapshot
    {
        public string Scheme { get; set; } = "";
        public List<SchemeSettingEntry> Settings { get; set; } = new();
    }

    /// <summary>
    /// 快照指定电源计划的全部 AC/DC 设置（内容锁定的基准）。
    /// 仅排除本轮实际启用的临时省电改写；未启用的 USB/PCIe 设置仍受内容保护。
    /// </summary>
    public async Task<OperationResult> SnapshotPowerSchemeContentAsync(string schemeGuid, bool excludeUsb = false, bool excludePcie = false)
    {
        try
        {
            if (!Guid.TryParse(schemeGuid, out _)) return OperationResult.Fail("电源计划 GUID 无效。");
            if (HasPowerSchemeContentSnapshot)
            {
                var existing = System.Text.Json.JsonSerializer.Deserialize<SchemeContentSnapshot>(await File.ReadAllTextAsync(SchemeContentSnapshotPath));
                return ValidSnapshot(existing) && existing!.Scheme.Equals(schemeGuid, StringComparison.OrdinalIgnoreCase)
                    ? OperationResult.Ok("沿用首次锁定的内容快照，未覆盖原始基准。")
                    : OperationResult.Fail("已有内容快照无效或属于其他计划，请先恢复；未覆盖旧记录。");
            }
            var settings = await QuerySchemeSettingsAsync(schemeGuid);
            if (settings is null)
            {
                return OperationResult.Fail("无法读取电源计划的设置明细，计划内容保护未生效。");
            }

            var kept = settings
                .Where(s => !IsFrameManagedPowerSetting(s.Subgroup, s.Setting, excludeUsb, excludePcie))
                .ToList();
            var snapshot = new SchemeContentSnapshot { Scheme = schemeGuid, Settings = kept };
            if (!ValidSnapshot(snapshot)) return OperationResult.Fail("电源计划明细为空或不完整，未创建内容快照。");
            await SavePowerJsonOnceAsync(SchemeContentSnapshotPath, snapshot);
            var saved = System.Text.Json.JsonSerializer.Deserialize<SchemeContentSnapshot>(await File.ReadAllTextAsync(SchemeContentSnapshotPath));
            if (!ValidSnapshot(saved) || !saved!.Scheme.Equals(schemeGuid, StringComparison.OrdinalIgnoreCase))
                return OperationResult.Fail("内容快照保存后校验失败，已保留记录且未覆盖。");
            Log.Info($"帧格·电源锁定：已快照计划 {schemeGuid} 的 {kept.Count} 项设置" +
                $"（排除帧格临时改写项 {settings.Count - kept.Count} 项）");
            return OperationResult.Ok($"已快照 {kept.Count} 项设置");
        }
        catch (Exception ex)
        {
            Log.Warn($"帧格·电源锁定：快照计划内容失败：{ex.Message}");
            return OperationResult.Fail("电源计划内容快照失败：" + ex.Message);
        }
    }

    /// <summary>丢弃计划内容快照（解除锁定时调用）。</summary>
    public OperationResult DiscardPowerSchemeContentSnapshot()
    {
        try
        {
            if (PowerFileExists(SchemeContentSnapshotPath))
            {
                File.Delete(SchemeContentSnapshotPath);
            }
            return OperationResult.Ok("");
        }
        catch (Exception ex)
        {
            Log.Warn($"帧格·电源锁定：删除计划内容快照失败：{ex.Message}");
            return OperationResult.Fail("电源计划恢复记录清理失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 把锁定的电源计划内容与快照比对，逐项自动还原被改动过的设置（内容守护）。
    /// 返回明确成功/失败结果；缺失、无效或读写失败均保留快照，不冒充恢复成功。
    /// </summary>
    public async Task<OperationResult> VerifyAndRestorePowerSchemeContentAsync(string expectedSchemeGuid, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!PowerFileExists(SchemeContentSnapshotPath))
            {
                return OperationResult.Fail("内容快照不存在，无法确认电源计划内容已恢复。");
            }

            var snapshot = System.Text.Json.JsonSerializer.Deserialize<SchemeContentSnapshot>(
                await File.ReadAllTextAsync(SchemeContentSnapshotPath));
            if (!ValidSnapshot(snapshot) || !snapshot!.Scheme.Equals(expectedSchemeGuid, StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult.Fail("内容快照无效或计划不匹配，已保留恢复记录且未修改计划。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var live = await QuerySchemeSettingsAsync(snapshot.Scheme);
            cancellationToken.ThrowIfCancellationRequested();
            if (live is null)
            {
                return OperationResult.Fail("无法完整读取电源计划，已保留恢复快照。");
            }

            var liveMap = live.ToDictionary(
                s => s.Subgroup + "/" + s.Setting, s => s, StringComparer.OrdinalIgnoreCase);
            var restored = new List<string>();
            var failures = new List<string>();

            foreach (var expected in snapshot.Settings)
            {
                if (!liveMap.TryGetValue(expected.Subgroup + "/" + expected.Setting, out var actual))
                {
                    failures.Add($"设置 {expected.Setting} 缺失，无法确认已恢复");
                    continue;
                }

                var acDrifted = actual.Ac != expected.Ac;
                var dcDrifted = actual.Dc != expected.Dc;
                if (!acDrifted && !dcDrifted)
                {
                    continue;
                }

                var entryFailures = 0;
                foreach (var (mode, drifted, value) in new[] { ("ac", acDrifted, expected.Ac), ("dc", dcDrifted, expected.Dc) })
                {
                    if (!drifted || value is null)
                    {
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    var (code, _, _) = await _powerRunner(
                        $"/set{mode}valueindex {snapshot.Scheme} {expected.Subgroup} {expected.Setting} {value.Value}",
                        TimeSpan.FromSeconds(20));
                    if (code != 0)
                    {
                        entryFailures++;
                        failures.Add($"{expected.Setting}/{mode.ToUpperInvariant()} 还原失败（退出码 {code}）");
                    }
                }

                if (entryFailures == 0)
                {
                    restored.Add($"{expected.Subgroup}/{expected.Setting}");
                }
            }

            if (restored.Count > 0)
            {
                // 与帧格临时改写同款收尾：/setactive 让还原立即生效
                cancellationToken.ThrowIfCancellationRequested();
                var (code, _, _) = await _powerRunner($"/setactive {snapshot.Scheme}", TimeSpan.FromSeconds(20));
                if (code != 0) failures.Add($"计划内容刷新失败（退出码 {code}）");
                cancellationToken.ThrowIfCancellationRequested();
                var checkedSettings = await QuerySchemeSettingsAsync(snapshot.Scheme);
                cancellationToken.ThrowIfCancellationRequested();
                if (checkedSettings is null) failures.Add("计划内容还原后复检失败");
                else
                {
                    var checkMap = checkedSettings.ToDictionary(s => s.Subgroup + "/" + s.Setting, StringComparer.OrdinalIgnoreCase);
                    foreach (var expected in snapshot.Settings)
                        if (!checkMap.TryGetValue(expected.Subgroup + "/" + expected.Setting, out var actual)
                            || actual.Ac != expected.Ac || actual.Dc != expected.Dc)
                            failures.Add($"设置 {expected.Setting} 还原后值不符");
                }
                Log.Info($"帧格·电源锁定：计划内容被改动，已自动还原 {restored.Count} 项 —— {string.Join("，", restored)}");
            }

            if (failures.Count > 0)
            {
                Log.Warn($"帧格·电源锁定：计划内容还原部分失败 {failures.Count} 项 —— {string.Join("；", failures)}");
            }

            var message = restored.Count == 0 ? "" : $"电源计划内容守护：检测到 {restored.Count} 项设置被改动，已自动还原。";
            if (failures.Count > 0)
            {
                message += (message.Length > 0 ? " " : "") + $"另有 {failures.Count} 项还原失败：{string.Join("；", failures)}";
            }

            return failures.Count > 0 ? OperationResult.Fail(message) : OperationResult.Ok(message);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error("帧格·电源锁定：计划内容校验异常", ex);
            return OperationResult.Fail("电源计划内容校验失败，已保留快照：" + ex.Message);
        }
    }

    /// <summary>
    /// 结构化读取一个计划的全部设置明细（不解析任何本地化文案）：
    /// 每个含 GUID 的行构成一个「块」（到下一个含 GUID 的行为止）；块内出现 0x 值的行组
    /// 即一项设置，其最后两个 0x 值依次为交流 / 直流当前索引；块内没有 0x 值的 GUID 行是子组。
    /// 输出首个 GUID 行是计划本身，跳过。读取失败返回 null。
    /// </summary>
    private async Task<List<SchemeSettingEntry>?> QuerySchemeSettingsAsync(string schemeGuid)
    {
        var (code, stdout, _) = await _powerRunner($"/q {schemeGuid}", TimeSpan.FromSeconds(30));
        if (code != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            Log.Warn($"帧格·电源锁定：读取计划 {schemeGuid} 的设置明细失败（退出码 {code}）");
            return null;
        }

        var lines = stdout.Split(['\r', '\n'], StringSplitOptions.None);
        var guidLines = new List<(int LineIndex, string Guid)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var match = GuidTokenRegex.Match(lines[i]);
            if (match.Success)
            {
                guidLines.Add((i, match.Value.ToLowerInvariant()));
            }
        }

        if (guidLines.Count < 2)
        {
            Log.Warn($"帧格·电源锁定：计划 {schemeGuid} 的明细输出无法结构化解析（GUID 行 {guidLines.Count} 个）");
            return null;
        }

        var settings = new List<SchemeSettingEntry>();
        string? subgroup = null;
        for (var k = 1; k < guidLines.Count; k++) // 第 0 个 GUID = 计划本身
        {
            var (lineIndex, guid) = guidLines[k];
            var blockEnd = k + 1 < guidLines.Count ? guidLines[k + 1].LineIndex : lines.Length;

            var hexValues = new List<long>();
            for (var i = lineIndex; i < blockEnd; i++)
            {
                foreach (Match hex in PowerHexValueRegex.Matches(lines[i]))
                {
                    // 必须用 long：0xFFFFFFFF（如「永不关闭硬盘」）在 int 下会溢出解析失败，
                    // 进而把「最后两个 0x 值」错位成其它行，酿成误判差异 + 反复写坏用户的设置。
                    if (TryParseHexToken(hex.Value, out var value))
                    {
                        hexValues.Add(value);
                    }
                }
            }

            if (hexValues.Count == 0)
            {
                subgroup = guid; // 只有别名行 = 子组
                continue;
            }

            settings.Add(new SchemeSettingEntry
            {
                Subgroup = subgroup ?? "",
                Setting = guid,
                Ac = hexValues.Count >= 2 ? hexValues[^2] : hexValues[^1],
                Dc = hexValues.Count >= 2 ? hexValues[^1] : null,
            });
        }

        return ValidSnapshot(new SchemeContentSnapshot { Scheme = schemeGuid, Settings = settings }) ? settings : null;
    }

    // ---------------- 网卡省电（REGENUM 风格遍历实例子键） ----------------

    private static (int Done, List<string> Failures) ApplyNicPowerOff()
    {
        var done = 0;
        var failures = new List<string>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(NetworkClassPath);
            if (root is null)
            {
                return (0, ["网卡类注册表键不存在"]);
            }

            foreach (var name in root.GetSubKeyNames())
            {
                if (!NetworkInstanceRegex.IsMatch(name))
                {
                    continue;
                }

                using var inst = root.OpenSubKey(name);
                if (inst?.GetValue("*IfType") is null)
                {
                    continue; // 非网卡实例
                }

                foreach (var valueName in new[] { "EnablePowerManagement", "PnPCapabilities" })
                {
                    var value = valueName == "EnablePowerManagement" ? 0 : 24;
                    var result = WriteOnce("HKLM", NetworkClassPath + "\\" + name, valueName, value, "");
                    if (result.Success)
                    {
                        done++;
                    }
                    else
                    {
                        failures.Add($"{name}\\{valueName}: {result.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            failures.Add(ex.Message);
        }

        return (done, failures);
    }

    // ---------------- 备份仓库（frame_reg_backup.json） ----------------

    private static OperationResult WriteOnce(string hive, string keyPath, string valueName, int optimizedValue, string label)
    {
        try
        {
            using var key = OpenWritable(hive, keyPath);
            if (key is null)
            {
                return OperationResult.Fail($"无法写入 {hive}\\{keyPath}");
            }

            // 备份原值（已有备份不覆盖——多次激活还原的始终是最初原值）
            var current = key.GetValue(valueName);
            lock (Gate)
            {
                var entries = ReadBackup();
                if (entries.All(e => e.KeyPath != keyPath || e.ValueName != valueName || e.Hive != hive))
                {
                    entries.Add(new FrameRegBackupEntry
                    {
                        Hive = hive,
                        KeyPath = keyPath,
                        ValueName = valueName,
                        Kind = current is null ? 0 : 4,
                        Data = current is int i ? i.ToString() : "",
                    });
                    SaveBackup(entries);
                }
            }

            if (current is int existing && existing == optimizedValue)
            {
                return OperationResult.Ok($"{label}已是目标值");
            }

            key.SetValue(valueName, optimizedValue, RegistryValueKind.DWord);
            return OperationResult.Ok(label);
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"{label}写入失败：{ex.Message}");
        }
    }

    private static List<FrameRegBackupEntry> ReadBackup()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(BackupPath))
                {
                    return System.Text.Json.JsonSerializer.Deserialize<List<FrameRegBackupEntry>>(
                        File.ReadAllText(BackupPath)) ?? new();
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"帧格临时优化备份读取失败：{ex.Message}");
            }

            return new();
        }
    }

    private static void SaveBackup(List<FrameRegBackupEntry> entries)
    {
        try
        {
            using var stream = new FileStream(BackupPath, FileMode.Create);
            System.Text.Json.JsonSerializer.Serialize(stream, entries);
        }
        catch (Exception ex)
        {
            Log.Warn($"帧格临时优化备份写入失败：{ex.Message}");
        }
    }

    private static bool HasBackup(Func<FrameRegBackupEntry, bool> predicate) =>
        ReadBackup().Any(predicate);

    private static RegistryKey? OpenWritable(string hive, string keyPath) => hive switch
    {
        "HKCU" => Registry.CurrentUser.CreateSubKey(keyPath, writable: true),
        _ => Registry.LocalMachine.CreateSubKey(keyPath, writable: true),
    };

    // ---------------- 进程调用 ----------------

    private static async Task<(int Code, string StdOut, string StdErr)> RunPowerCfgAsync(
        string arguments, TimeSpan timeout)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "powercfg.exe"),
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = System.Diagnostics.Process.Start(psi);
        if (process is null)
        {
            return (-1, "", "无法启动 powercfg.exe");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            try { process.Kill(); } catch { }
            return (-1, "", "执行超时");
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
