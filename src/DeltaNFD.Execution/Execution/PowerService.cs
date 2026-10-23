using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DeltaNFD.Services;

/// <summary>电源管理的真实实现（powercfg 命令行）。</summary>
public sealed partial class PowerService : IPowerService
{
    private readonly Func<string, TimeSpan, Task<(int Code, string StdOut, string StdErr)>> _runPowerCfg;
    private readonly Func<bool> _isElevated;

    public PowerService() : this(RunPowerCfgCoreAsync, () => ElevationHelper.IsElevated)
    { _heteroNative = new HeteroPowerPolicy(new HeteroPowerPolicy.NativeApi()); }

    internal PowerService(Func<string, TimeSpan, Task<(int Code, string StdOut, string StdErr)>> runPowerCfg,
        Func<bool> isElevated)
    {
        _runPowerCfg = runPowerCfg;
        _isElevated = isElevated;
    }

    static PowerService()
    {
        // .NET Core 下 GBK 等非 UTF 编码需要注册 CodePagesEncodingProvider
        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }
    /// <summary>Windows 内置电源计划 GUID。</summary>

    public Task<List<PowerSchemeInfo>> GetSchemesAsync() => Task.Run(async () =>
    {
        var activeGuid = await GetActiveSchemeGuidAsync();
        var (code, stdout, stderr) = await RunPowerCfgAsync("/list", TimeSpan.FromSeconds(20));
        var schemes = new List<PowerSchemeInfo>();
        if (code != 0)
        {
            Log.Warn($"电源计划：/list 失败，退出码={code}，原因={PowerCfgDetail(stdout, stderr)}");
            throw new InvalidOperationException($"读取电源计划失败：{FirstLine(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr)}");
        }

        foreach (var match in SchemeRegex.Matches(stdout).Cast<Match>())
        {
            var guid = match.Groups["guid"].Value.ToLowerInvariant();
            var name = match.Groups["name"].Value.Trim();
            schemes.Add(new PowerSchemeInfo
            {
                Guid = guid,
                Name = name,
                IsActive = guid == activeGuid,
            });
        }
        if (schemes.Count == 0)
        {
            Log.Warn($"电源计划：/list 退出码=0，但未解析到计划；活动 GUID={DisplayGuid(activeGuid)}");
            throw new InvalidOperationException("powercfg 未返回可识别的电源计划列表，已停止导入以避免生成重复计划。");
        }

        // 列表里没有卓越性能时补一个"未导入"条目，供 UI 展示可选
        if (schemes.All(s => s.Name != "卓越性能"))
        {
            schemes.Add(new PowerSchemeInfo
            {
                Guid = PowerSchemes.UltimateSchemeTemplateGuid,
                Name = "卓越性能（未导入，选择时自动导入）",
                IsActive = false,
            });
        }

        return schemes;
    });

    public async Task<OperationResult> SetSchemeAsync(string schemeGuid)
    {
        Log.Info($"电源计划：请求切换，目标 GUID={DisplayGuid(schemeGuid)}");
        if (!ElevationHelper.IsElevated)
        {
            Log.Warn("电源计划：切换被拒绝，程序未以管理员运行");
            return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        }

        try
        {
            var targetGuid = schemeGuid;
            var beforeGuid = await GetActiveSchemeGuidAsync();

            // 卓越性能默认隐藏：动态导入一份并缓存其 GUID
            if (schemeGuid.Equals(PowerSchemes.UltimateSchemeTemplateGuid, StringComparison.OrdinalIgnoreCase))
            {
                var imported = await EnsureUltimateSchemeAsync();
                if (!imported.Success)
                {
                    Log.Warn($"电源计划：卓越性能导入失败，原因={imported.Message}");
                    return imported;
                }

                targetGuid = AppSettingsStore.Read().UltimatePowerSchemeGuid;
                if (string.IsNullOrWhiteSpace(targetGuid))
                {
                    Log.Warn("电源计划：卓越性能导入后没有得到目标 GUID");
                    return OperationResult.Fail("卓越性能计划导入失败（未获得 GUID）。");
                }
            }

            var (code, stdout, stderr) = await RunPowerCfgAsync($"/setactive {targetGuid}", TimeSpan.FromSeconds(20));
            var afterGuid = await GetActiveSchemeGuidAsync();
            Log.Info($"电源计划：/setactive 完成，退出码={code}，之前={DisplayGuid(beforeGuid)}，目标={DisplayGuid(targetGuid)}，之后={DisplayGuid(afterGuid)}");
            if (code != 0)
            {
                var reason = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                Log.Warn($"电源计划：/setactive 失败，原因={PowerCfgDetail(stdout, stderr)}");
                return OperationResult.Fail($"切换电源计划失败：{FirstLine(reason)}");
            }

            // 命令退出码成功不等于切换成功：必须确认活动计划就是目标，否则如实失败。
            // 旧实现只记 Warn 仍返回成功，会造成“显示成功但系统计划没有写入”。
            var validation = ValidateActiveScheme(targetGuid, afterGuid);
            if (!validation.Success) return validation;

            var name = await GetSchemeNameAsync(targetGuid);
            return OperationResult.Ok($"已切换电源计划为「{name}」（立即生效）。");
        }
        catch (Exception ex)
        {
            Log.Error($"电源计划：切换异常，目标 GUID={DisplayGuid(schemeGuid)}", ex);
            return OperationResult.Fail($"切换电源计划失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 切换后的活动计划复核（供 /setactive 成功路径与隔离测试共用）：
    /// 空 GUID 或与目标不一致都不得返回成功。
    /// </summary>
    internal static OperationResult ValidateActiveScheme(string targetGuid, string afterGuid)
    {
        if (string.IsNullOrWhiteSpace(afterGuid))
            return OperationResult.Fail("切换电源计划后无法确认活动计划 GUID，未认定为成功。");
        if (!afterGuid.Equals(targetGuid, StringComparison.OrdinalIgnoreCase))
            return OperationResult.Fail($"切换电源计划后活动计划不一致：目标 {DisplayGuid(targetGuid)}，实际 {DisplayGuid(afterGuid)}。");
        return OperationResult.Ok("");
    }

    public async Task<bool> IsHibernateEnabledAsync()
    {
        var (code, stdout, _) = await RunPowerCfgAsync("/a", TimeSpan.FromSeconds(15));
        if (code != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return false;
        }

        // 按输出结构判定（中英文系统均适用）：第一段「此系统上有以下睡眠状态 / The following sleep
        // states are available」列出的是可用状态；「休眠 / Hibernate」出现在第一段 = 已启用，
        // 出现在第二段（不可用状态）或出现「未启用 / has not been enabled」= 未启用。
        var notAvailableIndex = IndexOfAny(stdout, "此系统上没有以下睡眠状态", "没有以下睡眠状态", "not available");
        var cut = notAvailableIndex >= 0 ? notAvailableIndex : stdout.Length;
        var available = stdout[..cut];
        var unavailable = notAvailableIndex >= 0 ? stdout[notAvailableIndex..] : "";

        if (ContainsAny(unavailable, "休眠未启用", "has not been enabled") ||
            ContainsAny(stdout, "休眠不可用", "Hibernation unavailable"))
        {
            return false;
        }

        return ContainsAny(available, "休眠", "Hibernate", "Hibernation");

        static int IndexOfAny(string text, params string[] needles)
        {
            var best = -1;
            foreach (var needle in needles)
            {
                var i = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
                if (i >= 0 && (best < 0 || i < best))
                {
                    best = i;
                }
            }

            return best;
        }

        static bool ContainsAny(string text, params string[] needles) =>
            needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<OperationResult> SetHibernateAsync(bool enable)
    {
        if (!ElevationHelper.IsElevated)
        {
            return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        }

        var (code, stdout, stderr) = await RunPowerCfgAsync(enable ? "/h on" : "/h off", TimeSpan.FromSeconds(20));
        if (code != 0)
        {
            var reason = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            return OperationResult.Fail($"{(enable ? "开启" : "关闭")}休眠失败：{FirstLine(reason)}");
        }

        return OperationResult.Ok($"已{(enable ? "开启" : "关闭")}休眠。");
    }

    // ---------------- 无省电电源计划（显示名「无省电释放模式」） ----------------

    /// <summary>无省电电源计划显示名。</summary>

    /// <summary>无省电电源计划描述。</summary>

    /// <summary>历史上出现过的旧显示名：.pow 内嵌的 Atlas 名 / 旧版中文名，统一迁移到 PowerSchemes.NoPowerSaveSchemeName。</summary>

    public async Task<OperationResult> ImportNoPowerSaveSchemeAsync()
    {
        Log.Info("无省电电源计划：请求导入或修正旧名称");
        if (!ElevationHelper.IsElevated)
        {
            Log.Warn("无省电电源计划：导入被拒绝，程序未以管理员运行");
            return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        }

        try
        {
            return await ImportNoPowerSaveSchemeCoreAsync();
        }
        catch (Exception ex)
        {
            Log.Error("导入无省电电源计划失败", ex);
            return OperationResult.Fail($"导入无省电电源计划失败：{ex.Message}");
        }
    }

    private async Task<OperationResult> ImportNoPowerSaveSchemeCoreAsync()
    {
        // 已有缓存的 GUID 且计划仍存在，或按名字能认出旧导入：直接收养，不重复导入（避免刷出多个同名计划）
        var cachedGuid = AppSettingsStore.Read().AtlasPowerSchemeGuid;
        var schemes = await GetSchemesAsync();
        Log.Info($"无省电电源计划：导入前检测，缓存 GUID={DisplayGuid(cachedGuid)}，系统计划数={schemes.Count(s => s.Guid != PowerSchemes.UltimateSchemeTemplateGuid)}，活动 GUID={DisplayGuid(schemes.FirstOrDefault(s => s.IsActive)?.Guid)}");
        if (!string.IsNullOrWhiteSpace(cachedGuid) &&
            schemes.All(s => !s.Guid.Equals(cachedGuid, StringComparison.OrdinalIgnoreCase)))
        {
            Log.Warn($"无省电电源计划：缓存 GUID 已不存在，清除旧缓存后再按名称/重新导入：{cachedGuid}");
            AppSettingsStore.Update(s => s.AtlasPowerSchemeGuid = "");
            cachedGuid = "";
        }
        var legacySchemes = schemes.Where(s => PowerSchemes.LegacyNoPowerSaveNames.Contains(s.Name)).ToList();
        var renameFailures = 0;
        foreach (var legacyScheme in legacySchemes)
        {
            if (!await RenameNoPowerSaveSchemeAsync(legacyScheme.Guid))
                renameFailures++;
        }

        var existing = schemes.FirstOrDefault(s =>
                !string.IsNullOrWhiteSpace(cachedGuid) &&
                s.Guid.Equals(cachedGuid, StringComparison.OrdinalIgnoreCase))
            ?? schemes.FirstOrDefault(s =>
                s.Name == PowerSchemes.NoPowerSaveSchemeName || PowerSchemes.LegacyNoPowerSaveNames.Contains(s.Name));
        if (existing is not null)
        {
            AppSettingsStore.Update(s => s.AtlasPowerSchemeGuid = existing.Guid);
            Log.Info($"无省电电源计划：识别为已存在，GUID={existing.Guid}，名称={existing.Name}，旧名改写失败数={renameFailures}，未重复导入");
            return OperationResult.Ok(renameFailures == 0
                ? $"无省电电源计划已存在（{existing.Guid}），无需重复导入。"
                : $"无省电电源计划已存在（{existing.Guid}），但有 {renameFailures} 份旧名称计划改名失败，可再次点击修正。");
        }

        var powPath = Path.Combine(BackendEnvironment.AssetsRoot, "NoPowerSave-Scheme.pow");
        if (!File.Exists(powPath))
        {
            Log.Warn("无省电电源计划：内置 NoPowerSave-Scheme.pow 不存在，未执行导入");
            return OperationResult.Fail("未找到内置电源计划文件（Assets\\NoPowerSave-Scheme.pow），请重新部署程序。");
        }

        var newGuid = Guid.NewGuid().ToString("D");
        Log.Info($"无省电电源计划：开始 /import，指定 GUID={newGuid}");
        var (code, stdout, stderr) = await RunPowerCfgAsync(
            $"/import \"{powPath}\" {newGuid}", TimeSpan.FromSeconds(30));
        Log.Info($"无省电电源计划：/import 退出码={code}，GUID={newGuid}" +
            (code == 0 ? "" : $"，原因={PowerCfgDetail(stdout, stderr)}"));

        var (verifyCode, verifyOut, verifyErr) = await RunPowerCfgAsync($"/query {newGuid}", TimeSpan.FromSeconds(20));
        Log.Info($"无省电电源计划：/query 复核退出码={verifyCode}，GUID={newGuid}" +
            (verifyCode == 0 ? "" : $"，原因={PowerCfgDetail(verifyOut, verifyErr)}"));
        if (verifyCode != 0)
        {
            var reason = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            Log.Warn($"无省电电源计划：导入未能确认，导入退出码={code}，复核退出码={verifyCode}，GUID={newGuid}");
            return OperationResult.Fail(code == 0
                ? $"导入命令已完成，但无法查询计划 {newGuid}；请先在电源选项中核对，不要重复点击导入。"
                : $"导入无省电电源计划失败：{FirstLine(reason)}");
        }

        // 只有 /query 复核成功（或导入命令失败但计划可查询）才写缓存；
        // 导入命令成功但无法复核时不留下“看似已导入”的过期 GUID。
        if (verifyCode == 0)
            AppSettingsStore.Update(s => s.AtlasPowerSchemeGuid = newGuid);

        // 覆盖 .pow 内嵌的「Atlas Power Scheme」名；改名失败时保留已导入的 GUID。
        var renamed = await RenameNoPowerSaveSchemeAsync(newGuid);
        Log.Info($"无省电电源计划：导入完成，GUID={newGuid}，标准名称改写={(renamed ? "成功" : "失败")}");
        return OperationResult.Ok(renamed
            ? $"「{PowerSchemes.NoPowerSaveSchemeName}」电源计划已导入（GUID {newGuid}）。可在电源选项中查看，或一键切换启用。"
            : $"电源计划已导入（GUID {newGuid}），但显示名暂未改成功——可在实验室点击「修正计划名称」重试。");
    }

    /// <summary>
    /// 旧计划名迁移（幂等，每次刷新执行）：把所有仍叫旧名（Atlas Power Scheme / 无省电极限模式，
    /// 含历史导入失败改名被跳过的）的计划统一改为「无省电释放模式」；缓存 GUID 失效或为空时按名收养。
    /// </summary>
    public async Task<OperationResult> EnsureNoPowerSaveSchemeNameAsync()
    {
        try
        {
            var schemes = await GetSchemesAsync();

            var stale = schemes.Where(x => PowerSchemes.LegacyNoPowerSaveNames.Contains(x.Name)).ToList();
            foreach (var scheme in stale)
            {
                await RenameNoPowerSaveSchemeAsync(scheme.Guid);
            }

            var s = AppSettingsStore.Read();
            var guid = s.AtlasPowerSchemeGuid;
            if (string.IsNullOrWhiteSpace(guid) ||
                schemes.All(x => !x.Guid.Equals(guid, StringComparison.OrdinalIgnoreCase)))
            {
                var adopted = schemes.FirstOrDefault(x => x.Name == PowerSchemes.NoPowerSaveSchemeName) ?? stale.FirstOrDefault();
                if (adopted is not null)
                {
                    AppSettingsStore.Update(u => u.AtlasPowerSchemeGuid = adopted.Guid);
                }
            }

            return OperationResult.Ok("");
        }
        catch (Exception ex)
        {
            Log.Error("无省电电源计划：旧名称迁移异常", ex);
            return OperationResult.Ok("");
        }
    }

    /// <summary>powercfg /changename 改为标准显示名。返回命令是否成功。</summary>
    private async Task<bool> RenameNoPowerSaveSchemeAsync(string guid)
    {
        var (code, stdout, stderr) = await RunPowerCfgAsync(
            $"/changename {guid} \"{PowerSchemes.NoPowerSaveSchemeName}\" \"{PowerSchemes.NoPowerSaveSchemeDescription}\"",
            TimeSpan.FromSeconds(20));
        if (code != 0)
            Log.Warn($"无省电电源计划：/changename 失败，GUID={guid}，退出码={code}，原因={PowerCfgDetail(stdout, stderr)}");
        else
            Log.Info($"无省电电源计划：/changename 成功，GUID={guid}");
        return code == 0;
    }

    // ---------------- 异类调度策略（隐藏属性，先取消隐藏再读写） ----------------

    /// <summary>
    /// 三个异类调度策略设置（SUB_PROCESSOR 下）。定义见 <see cref="PowerSchemes.HeteroPolicySettings"/>。
    /// </summary>

    public Task<List<HeteroPolicyInfo>> GetHeteroPoliciesAsync() => GetHeteroPoliciesReadOnlyAsync();

    public Task<OperationResult> SetHeteroPolicyAsync(string settingGuid, int value) =>
        SetHeteroPolicyValuesAsync(settingGuid, value, value);

    /// <summary>按交流/直流分别设置一个异类策略值（还原原值时两者可能不同）。</summary>
    public async Task<OperationResult> SetHeteroPolicyValuesAsync(string settingGuid, int acValue, int dcValue)
    {
        if (!_isElevated())
        {
            return OperationResult.Fail(ElevationHelper.NotElevatedMessage);
        }

        var title = PowerSchemes.HeteroPolicySettings.FirstOrDefault(s => s.Guid.Equals(settingGuid, StringComparison.OrdinalIgnoreCase)).Title;
        if (string.IsNullOrEmpty(title))
        {
            return OperationResult.Fail("未知的异类调度策略设置。");
        }
        var maximum = settingGuid.Equals(PowerSchemes.HeteroPolicySettings[0].Guid, StringComparison.OrdinalIgnoreCase) ? 4 : 5;
        if (acValue < 0 || dcValue < 0 || acValue > maximum || dcValue > maximum)
            return OperationResult.Fail("异类调度策略值超出可用范围。");
        if (_heteroNative is not null) return _heteroNative.Set(Guid.Parse(settingGuid), acValue, dcValue);

        // 属性显隐只影响电源选项界面；按 GUID 写入无需改变显隐。
        // 退出恢复三项策略时，避免重复启动 15 次 /attributes 子进程。
        var (acCode, acOut, acErr) = await RunPowerCfgAsync(
            $"/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR {settingGuid} {acValue}",
            TimeSpan.FromSeconds(20));
        if (acCode != 0)
        {
            var reason = string.IsNullOrWhiteSpace(acErr) ? acOut : acErr;
            return OperationResult.Fail($"设置「{title}」交流值失败：{FirstLine(reason)}");
        }

        var (dcCode, dcOut, dcErr) = await RunPowerCfgAsync(
            $"/setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR {settingGuid} {dcValue}",
            TimeSpan.FromSeconds(20));
        if (dcCode != 0)
        {
            var reason = string.IsNullOrWhiteSpace(dcErr) ? dcOut : dcErr;
            return OperationResult.Fail($"设置「{title}」直流值失败：{FirstLine(reason)}");
        }

        var (applyCode, _, applyErr) = await RunPowerCfgAsync("/setactive SCHEME_CURRENT", TimeSpan.FromSeconds(20));
        if (applyCode != 0)
        {
            return OperationResult.Fail($"激活电源计划失败：{FirstLine(applyErr)}");
        }

        return acValue == dcValue
            ? OperationResult.Ok($"「{title}」已设置为 {acValue}（交流/直流同时生效）。")
            : OperationResult.Ok($"「{title}」已设置为 交流 {acValue} / 直流 {dcValue}。");
    }

    // ---------------- 内部实现 ----------------

    private static readonly Regex SchemeRegex = new(
        @"GUID: ?(?<guid>[0-9a-fA-F-]{36})\s+\((?<name>[^)]+)\)",
        RegexOptions.Compiled);

    private static readonly Regex HexValueRegex = new("0x[0-9A-Fa-f]{1,8}", RegexOptions.Compiled);

    private async Task<string> GetActiveSchemeGuidAsync()
    {
        var (code, stdout, stderr) = await RunPowerCfgAsync("/getactivescheme", TimeSpan.FromSeconds(15));
        if (code != 0)
        {
            Log.Warn($"电源计划：/getactivescheme 失败，退出码={code}，原因={PowerCfgDetail(stdout, stderr)}");
            return "";
        }

        var match = SchemeRegex.Match(stdout);
        if (!match.Success)
            Log.Warn($"电源计划：/getactivescheme 退出码=0，但无法解析活动 GUID；输出={PowerCfgDetail(stdout, stderr)}");
        return match.Success ? match.Groups["guid"].Value.ToLowerInvariant() : "";
    }

    private async Task<string> GetSchemeNameAsync(string guid)
    {
        var (code, stdout, _) = await RunPowerCfgAsync("/list", TimeSpan.FromSeconds(15));
        if (code == 0)
        {
            foreach (var match in SchemeRegex.Matches(stdout).Cast<Match>())
            {
                if (match.Groups["guid"].Value.Equals(guid, StringComparison.OrdinalIgnoreCase))
                {
                    return match.Groups["name"].Value.Trim();
                }
            }
        }

        return guid;
    }

    /// <summary>卓越性能显示名（登录项/旧缓存按名字收养用；模板 GUID 本身不可直接激活）。</summary>
    private const string UltimateSchemeName = "卓越性能";

    private async Task<OperationResult> EnsureUltimateSchemeAsync()
    {
        var schemes = await GetSchemesAsync();

        // 已导入过：缓存 GUID 必须仍在计划列表里；过期缓存不能直接信任。
        var cached = AppSettingsStore.Read().UltimatePowerSchemeGuid;
        if (!string.IsNullOrWhiteSpace(cached) &&
            schemes.Any(s => s.Guid.Equals(cached, StringComparison.OrdinalIgnoreCase)))
        {
            return OperationResult.Ok(cached);
        }
        if (!string.IsNullOrWhiteSpace(cached))
        {
            Log.Warn($"电源计划：卓越性能缓存 GUID 已不在计划列表，清除缓存并重新导入：{cached}");
            AppSettingsStore.Update(s => s.UltimatePowerSchemeGuid = "");
        }

        // 缓存丢失但系统已有导入副本（如用户清过设置/换过配置）：按显示名收养，避免重复导入。
        var adopted = schemes.FirstOrDefault(s => s.Name.Equals(UltimateSchemeName, StringComparison.Ordinal));
        if (adopted is not null)
        {
            AppSettingsStore.Update(s => s.UltimatePowerSchemeGuid = adopted.Guid);
            Log.Info($"电源计划：按名称收养已存在的卓越性能计划，GUID={adopted.Guid}，未重复导入");
            return OperationResult.Ok(adopted.Guid);
        }

        var (code, stdout, stderr) = await RunPowerCfgAsync(
            $@"-duplicatescheme {PowerSchemes.UltimateSchemeTemplateGuid}", TimeSpan.FromSeconds(20));
        if (code != 0)
        {
            var reason = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            return OperationResult.Fail($"导入卓越性能计划失败：{FirstLine(reason)}");
        }

        var match = Regex.Match(stdout, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        if (!match.Success)
        {
            return OperationResult.Fail("导入卓越性能计划失败（未从 powercfg 输出解析到新 GUID）。");
        }

        var newGuid = match.Value.ToLowerInvariant();
        AppSettingsStore.Update(s => s.UltimatePowerSchemeGuid = newGuid);
        return OperationResult.Ok(newGuid);
    }

    private Task<(int Code, string StdOut, string StdErr)> RunPowerCfgAsync(string arguments, TimeSpan timeout) =>
        _runPowerCfg(arguments, timeout);

    private static async Task<(int Code, string StdOut, string StdErr)> RunPowerCfgCoreAsync(
        string arguments, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "powercfg.exe"),
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // powercfg 输出为系统 ANSI 码页（zh-CN = GBK），按当前区域码页解码，否则中文名乱码
            StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentUICulture.TextInfo.ANSICodePage),
            StandardErrorEncoding = Encoding.GetEncoding(CultureInfo.CurrentUICulture.TextInfo.ANSICodePage),
        };

        using var process = Process.Start(psi);
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
            try
            {
                process.Kill();
            }
            catch
            {
                // 进程可能已自行退出
            }

            return (-1, "", "执行超时");
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static int ParseHexToken(string token) =>
        int.Parse(token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? token[2..] : token, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static string FirstLine(string text)
    {
        var trimmed = text.Trim();
        var lineBreak = trimmed.IndexOfAny(['\r', '\n']);
        return lineBreak > 0 ? trimmed[..lineBreak] : trimmed;
    }

    private static string DisplayGuid(string? guid) => string.IsNullOrWhiteSpace(guid) ? "(空)" : guid;

    private static string PowerCfgDetail(string stdout, string stderr)
    {
        var line = FirstLine(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
        return line.Length == 0 ? "(无输出)" : line.Length <= 180 ? line : line[..180] + "…";
    }
}
