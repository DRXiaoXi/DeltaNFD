using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DeltaNFD.Services;

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

    /// <summary>前台加速响应的调度参数改写是否已经生效（有本功能的备份记录即视为已改写）。</summary>
    public static bool IsForegroundBoostApplied() => HasBackup(b =>
        b.KeyPath == MultimediaProfilePath && b.ValueName == "SystemResponsiveness" ||
        b.KeyPath == PriorityControlPath && b.ValueName == "Win32PrioritySeparation");

    /// <summary>降低省电延迟的改写是否已经生效（有本功能的备份记录即视为已改写）。</summary>
    public static bool IsPowerSaveLatencyApplied() => HasBackup(b =>
        b.KeyPath == NetworkClassPath && b.ValueName is "EnablePowerManagement" or "PnPCapabilities" ||
        b.KeyPath == UsbControlPath && b.ValueName == "DisableSelectiveSuspend");

    // ---------------- 应用（帧格激活） ----------------

    /// <summary>
    /// 应用全部已启用的帧格临时优化。对每一项：记录原值备份（已有备份不重复记录，
    /// 保证多次激活还原的始终是最初原值）→ 写入优化值。USB 选择性暂停的电源计划部分
    /// 由 <see cref="ApplyUsbPowerSchemeAsync"/> 处理。
    /// </summary>
    public async Task<OperationResult> ApplyAsync(bool foregroundResponsiveness, bool foregroundPriority,
        bool nicPowerOff, bool usbSuspendOff)
    {
        if (!ElevationHelper.IsElevated)
        {
            return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        }

        var notes = new List<string>();
        var failures = new List<string>();

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
        if (nicPowerOff)
        {
            var (done, failed) = ApplyNicPowerOff();
            if (done > 0) notes.Add($"网卡省电全禁（{done} 项）");
            if (failed.Count > 0) failures.Add("网卡省电：" + string.Join("；", failed));
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

        Log.Info($"帧格临时优化：应用完成 —— 成功 {notes.Count} 项（{string.Join("，", notes)}）；失败 {failures.Count} 项");
        return failures.Count == 0
            ? OperationResult.Ok(notes.Count == 0 ? "没有需要应用的临时优化。" : $"已应用 {notes.Count} 项帧格临时优化。")
            : OperationResult.Fail($"帧格临时优化部分失败：{string.Join("；", failures)}");
    }

    /// <summary>还原全部帧格临时优化（退出帧格时调用；没有备份 = 没改过，静默成功）。</summary>
    public async Task<OperationResult> RevertAsync()
    {
        var entries = ReadBackup();

        // 注册表备份为空也要检查 USB 电源计划备份（注册表部分失败但 powercfg 成功的半生效场景）
        if (entries.Count == 0)
        {
            var onlyUsb = await RevertUsbPowerSchemeAsync();
            return onlyUsb.Success && onlyUsb.Message.Length == 0
                ? OperationResult.Ok("")
                : onlyUsb;
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

        // USB 电源计划部分按备份还原（帧格改的是备份里记录的那个计划；
        // 用户期间手动切换计划导致的差异不属于帧格的改动范围）
        var usbPower = await RevertUsbPowerSchemeAsync();
        if (!usbPower.Success)
        {
            failures.Add(usbPower.Message);
        }

        if (failures.Count == 0)
        {
            File.Delete(BackupPath);
            Log.Info($"帧格临时优化：已还原 {restored} 项注册表值" +
                (usbPower.Success && usbPower.Message.Length > 0 ? " + USB 电源计划" : ""));
            return OperationResult.Ok(restored > 0 ? $"已还原 {restored} 项帧格临时优化。" : "");
        }

        // 有失败项：保留未成功还原的备份，下次退出/自愈重试
        SaveBackup(failedEntries);
        Log.Warn($"帧格临时优化：还原失败 {failures.Count} 项 —— {string.Join("；", failures)}");
        return OperationResult.Fail($"帧格临时优化还原失败：{string.Join("；", failures)}（重启后再次退出帧格会重试）");
    }

    // ---------------- USB 电源计划部分（powercfg，写当前激活计划） ----------------

    private const string PowerSchemesPath = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
    private const string UsbSubgroup = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string UsbSuspendSetting = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";

    /// <summary>USB 电源计划 AC/DC 原值备份（与注册表备份同一文件，kind 字段区分）。</summary>
    private sealed class UsbPowerBackup
    {
        public string Scheme { get; set; } = "";
        public int? Ac { get; set; }
        public int? Dc { get; set; }
    }

    /// <summary>与 PowerService.HexValueRegex 同构：输出末尾两个 0x 值依次为交流/直流当前值（本地化安全）。</summary>
    private static readonly Regex PowerHexValueRegex = new("0x[0-9A-Fa-f]{1,8}", RegexOptions.Compiled);

    private static async Task<OperationResult> ApplyUsbPowerSchemeAsync()
    {
        try
        {
            var scheme = ReadActivePowerScheme();
            if (scheme is null)
            {
                return OperationResult.Fail("无法读取当前电源计划，USB 电源部分未修改。");
            }

            var backupPath = Path.Combine(AppDataPaths.Root, "frame_usb_power.json");
            if (!File.Exists(backupPath))
            {
                var (ac, dc) = await QueryUsbSuspendValuesAsync(scheme);
                var backup = new UsbPowerBackup { Scheme = scheme, Ac = ac, Dc = dc };
                await File.WriteAllTextAsync(backupPath,
                    System.Text.Json.JsonSerializer.Serialize(backup));
            }

            foreach (var mode in new[] { "ac", "dc" })
            {
                var (code, _, _) = await RunPowerCfgAsync(
                    $"/set{mode}valueindex {scheme} {UsbSubgroup} {UsbSuspendSetting} 0", TimeSpan.FromSeconds(20));
                if (code != 0)
                {
                    return OperationResult.Fail($"USB {mode.ToUpperInvariant()} 设置失败（退出码 {code}）。");
                }
            }

            var (activeCode, _, _) = await RunPowerCfgAsync($"/setactive {scheme}", TimeSpan.FromSeconds(20));
            return activeCode == 0
                ? OperationResult.Ok("")
                : OperationResult.Fail($"USB 电源计划刷新失败（退出码 {activeCode}）。");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("USB 电源设置失败：" + ex.Message);
        }
    }

    private static async Task<OperationResult> RevertUsbPowerSchemeAsync()
    {
        try
        {
            var backupPath = Path.Combine(AppDataPaths.Root, "frame_usb_power.json");
            if (!File.Exists(backupPath))
            {
                return OperationResult.Ok(""); // 没改过电源计划
            }

            var backup = System.Text.Json.JsonSerializer.Deserialize<UsbPowerBackup>(
                await File.ReadAllTextAsync(backupPath));
            if (backup is null || string.IsNullOrWhiteSpace(backup.Scheme))
            {
                File.Delete(backupPath);
                return OperationResult.Ok("");
            }

            foreach (var (mode, value) in new[] { ("ac", backup.Ac), ("dc", backup.Dc) })
            {
                if (value is null)
                {
                    // 原值不存在：用 powercfg 默认值 1（系统默认启用选择性暂停）
                    var (clearCode, _, _) = await RunPowerCfgAsync(
                        $"/set{mode}valueindex {backup.Scheme} {UsbSubgroup} {UsbSuspendSetting} 1", TimeSpan.FromSeconds(20));
                    if (clearCode != 0)
                    {
                        return OperationResult.Fail($"USB {mode.ToUpperInvariant()} 还原失败（退出码 {clearCode}）。");
                    }

                    continue;
                }

                var (code, _, _) = await RunPowerCfgAsync(
                    $"/set{mode}valueindex {backup.Scheme} {UsbSubgroup} {UsbSuspendSetting} {value.Value}", TimeSpan.FromSeconds(20));
                if (code != 0)
                {
                    return OperationResult.Fail($"USB {mode.ToUpperInvariant()} 还原失败（退出码 {code}）。");
                }
            }

            var (activeCode, _, _) = await RunPowerCfgAsync($"/setactive {backup.Scheme}", TimeSpan.FromSeconds(20));
            if (activeCode != 0)
            {
                return OperationResult.Fail($"原电源计划激活失败（退出码 {activeCode}）。");
            }

            File.Delete(backupPath);
            return OperationResult.Ok("USB 电源计划已还原。");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("USB 电源计划还原失败：" + ex.Message);
        }
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

    /// <summary>查询当前计划 USB 选择性暂停 AC/DC 值（powercfg /q 输出按 0x 值序列解析，
    /// 不依赖本地化文案；读取失败返回 null 表示原值不存在）。</summary>
    private static async Task<(int? Ac, int? Dc)> QueryUsbSuspendValuesAsync(string scheme)
    {
        var (code, stdout, _) = await RunPowerCfgAsync(
            $"/q {scheme} {UsbSubgroup} {UsbSuspendSetting}", TimeSpan.FromSeconds(20));
        if (code != 0 || stdout is null)
        {
            return (null, null);
        }

        var matches = PowerHexValueRegex.Matches(stdout);
        if (matches.Count < 2)
        {
            return (null, null);
        }

        int? ParseAt(int index) => int.TryParse(
            matches[index].Value,
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

        return (ParseAt(matches.Count - 2), ParseAt(matches.Count - 1));
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
