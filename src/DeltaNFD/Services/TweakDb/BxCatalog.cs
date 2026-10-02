using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeltaNFD.Services.TweakDb;

// ---------------- JSON 数据库模型（优化数据库） ----------------

public sealed class BxValueEntry
{
    public List<string> ValueTypes { get; set; } = new();

    /// <summary>数据库里的值绝大多数是字符串，但少量条目直接写数字（如 "Value": 1），
    /// 用宽松转换器统一读成字符串，避免整个文件反序列化失败。</summary>
    [JsonConverter(typeof(LooseStringJsonConverter))]
    public string? Value { get; set; }
}

/// <summary>把字符串 / 数字 / 布尔统一读成字符串（数字保持原始文本，不转科学计数）。</summary>
public sealed class LooseStringJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var l)
                ? l.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => null,
            _ => "",
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }
}

public sealed class BxTweak
{
    public string TweakType { get; set; } = "REG";
    public string Path { get; set; } = "";
    public string? Key { get; set; }
    public string? ValueFormat { get; set; }
    public List<BxValueEntry> Values { get; set; } = new();
}

public sealed class BxItem
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Type { get; set; } = "DOUBLE";
    public string? Risk { get; set; }
    public List<string> NoSupport { get; set; } = new();
    public List<BxTweak> Tweaks { get; set; } = new();
    [JsonIgnore] public string? ValidationError { get; set; }
}

/// <summary>多档位条目的一个档位选项。</summary>
public sealed class BxGearOption
{
    /// <summary>界面显示文本（如「最优（0）」）。</summary>
    public required string Label { get; init; }

    /// <summary>该档位要写入的值（"Null" = 删除值）。</summary>
    public required string Value { get; init; }
}

/// <summary>多档位条目信息：全部档位 + 当前命中的档位下标（-1 = 无匹配）。</summary>
public sealed class BxGearInfo
{
    public required List<BxGearOption> Options { get; init; }

    public required int CurrentIndex { get; init; }
}

public sealed class BxServiceEntry
{
    public string ServiceName { get; set; } = "";
    public bool IsBlocked { get; set; }
    public bool IsBlocked11 { get; set; }
    public int DefaultStartMode { get; set; }

    /// <summary>禁用该服务会破坏的功能说明（源自外部服务数据库的警告清单，如 "Pin code login"）。</summary>
    public List<string> WillBrake { get; set; } = new();
}

/// <summary>优化子栏目定义。标题内置（短语），副标题从外部 meta.json 读取（键 "sec.{Id}" 的 d 字段）。</summary>
public sealed class BxSection
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Subtitle => BxCatalog.Describe("sec." + Id).Item2;
    /// <summary>对应 JSON 数据库文件名（不含扩展名）；服务组栏目为 null（内置定义）。</summary>
    public string? JsonFile { get; init; }
}

/// <summary>服务组定义（一组可批量禁用的系统服务）。名称与说明从外部 meta.json 读取（键 "svc.{Id}"）。</summary>
public sealed record BxServiceGroup(string Id, string[] Services, bool UseBlockedDb = false)
{
    public string Name => BxCatalog.Describe("svc." + Id).Item1;
    public string Desc => BxCatalog.Describe("svc." + Id).Item2;
}

/// <summary>
/// 优化目录：从随程序输出的 JSON 数据库加载条目；
/// 中文名称与说明统一放在外部数据文件 Assets\TweakDb\meta.json（键 -> { n: 名称, d: 说明 }）。
/// </summary>
public static class BxCatalog
{
    public static readonly BxSection[] Sections =
    [
        new() { Id = "basic", Title = "基本设置", JsonFile = "BasicSettingsTweaks" },
        new() { Id = "security", Title = "安全性", JsonFile = "Security" },
        new() { Id = "custom", Title = "自定义", JsonFile = "Customization" },
        new() { Id = "services", Title = "服务组", JsonFile = null },
        new() { Id = "debloat", Title = "精简", JsonFile = "Debloat" },
        new() { Id = "privacy", Title = "隐私", JsonFile = "WindowsTelemetry" },
        new() { Id = "tweaks", Title = "调整", JsonFile = "RegistryTweaks" },
        new() { Id = "tasks", Title = "任务", JsonFile = "Tasks" },
        new() { Id = "tsx", Title = "启用TSX", JsonFile = "Placebo" },
        new() { Id = "pending", Title = "UP推荐", JsonFile = "UpRecommended" },
    ];

    private static readonly Lazy<Dictionary<string, List<BxItem>>> _db = new(LoadAll);

    /// <summary>sectionId -> 条目列表。</summary>
    public static Dictionary<string, List<BxItem>> Database => _db.Value;

    internal static string? GetUnsupportedReason(BxItem item, int build, bool? systemDriveIsHdd)
    {
        if (item.NoSupport.Contains(build >= 22000 ? "W11" : "W10", StringComparer.OrdinalIgnoreCase))
            return build >= 22000 ? "不适用于 Windows 11" : "不适用于 Windows 10";
        if (item.NoSupport.Contains("22H2+", StringComparer.OrdinalIgnoreCase) &&
            (build >= 22621 || build is >= 19045 and < 22000))
            return "不适用于 Windows 22H2 及更新版本";
        if (item.NoSupport.Contains("HDD", StringComparer.OrdinalIgnoreCase))
        {
            if (systemDriveIsHdd == true) return "机械系统盘不适用";
            if (systemDriveIsHdd is null) return "无法确认系统盘介质，暂不可操作";
        }
        return null;
    }

    /// <summary>主页与栏目共用的最终适用性判定，避免两处 UI 对同一数据库条目显示不同结果。</summary>
    internal static string? GetUnavailableReason(BxItem item, int build, bool? systemDriveIsHdd)
    {
        if (item.Name.Equals("HPETName", StringComparison.OrdinalIgnoreCase) &&
            GetCpuRestrictionReason(item, ReadCpuVendor()) is { } cpuReason)
            return cpuReason;
        if (item.ValidationError is not null)
            return "数据校验失败：" + item.ValidationError;

        var platformReason = GetUnsupportedReason(item, build, systemDriveIsHdd);
        if (platformReason is not null)
            return platformReason;

        if (item.Tweaks.All(t => t.TweakType is "CHECK" or "EMBEDDEDTWEAK" or "EMBEDDED_FILE" or "BAT"))
            return "该条目由原工具内部脚本实现，本工具暂不支持";

        return null;
    }

    internal static string ReadCpuVendor()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("VendorIdentifier") as string ?? "";
        }
        catch { return ""; }
    }

    internal static string? GetCpuRestrictionReason(BxItem item, string vendor) =>
        item.Name.Equals("HPETName", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(vendor.Trim(), "GenuineIntel", StringComparison.OrdinalIgnoreCase)
            ? "Intel 平台不开放 HPET 优化；已有设置可通过一键恢复还原" : null;

    internal static List<BxItem> SelectCompatibleItems(IEnumerable<BxItem> items, int build) => items
        .Where(i => !string.IsNullOrWhiteSpace(i.Name))
        .GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
        .Select(g => g.FirstOrDefault(i => GetUnsupportedReason(i, build, false) is null) ?? g.First())
        .ToList();

    internal static string? ValidateItem(BxItem item)
    {
        foreach (var op in item.Tweaks)
        {
            if (op.TweakType is not ("REG" or "REG_DWORD" or "REG_SZ" or "REG_EXPAND_SZ" or "REG_MULTI_SZ" or "REG_BINARY"))
                continue;
            if (string.IsNullOrWhiteSpace(op.Path) || op.Key is null)
                return "注册表路径或值名缺失";
            if (op.ValueFormat is not null && !op.ValueFormat.Equals("HEX", StringComparison.OrdinalIgnoreCase))
                return $"不支持的数值格式：{op.ValueFormat}";
            foreach (var entry in op.Values)
            {
                var value = entry.Value;
                if (value is null) return "注册表值缺失";
                if (value.Equals("Null", StringComparison.OrdinalIgnoreCase)) continue;
                if (op.TweakType == "REG_BINARY")
                {
                    if (!value.All(Uri.IsHexDigit)) return $"二进制值不是十六进制：{value}";
                }
                else if (op.TweakType is "REG" or "REG_DWORD")
                {
                    var style = op.ValueFormat?.Equals("HEX", StringComparison.OrdinalIgnoreCase) == true
                        ? System.Globalization.NumberStyles.HexNumber
                        : System.Globalization.NumberStyles.Integer;
                    if (!int.TryParse(value, style, System.Globalization.CultureInfo.InvariantCulture, out _))
                        return $"DWORD 值不可解析：{value}";
                }
            }
        }
        return null;
    }

    internal static bool IsHagsItem(BxItem item) => item.Tweaks.Any(t =>
        t.Key?.Equals("HwSchMode", StringComparison.OrdinalIgnoreCase) == true &&
        t.Path.EndsWith(@"\Control\GraphicsDrivers", StringComparison.OrdinalIgnoreCase));

    /// <summary>658 个 Windows 服务数据库（服务组栏目使用）。</summary>
    public static List<BxServiceEntry> AllServices => _allServices.Value;

    /// <summary>服务的禁用影响警告（WillBrake 清单，已翻译为中文；未收录词条回退英文）。</summary>
    public static List<string> GetServiceWillBrake(string serviceName)
    {
        foreach (var entry in AllServices)
        {
            if (entry.ServiceName.Equals(serviceName, StringComparison.OrdinalIgnoreCase))
            {
                return entry.WillBrake.Select(TranslateWarning).ToList();
            }
        }

        return Array.Empty<string>().ToList();
    }

    /// <summary>把单条警告翻译为中文（查 ServiceWarningsZh.json，未命中回退原文）。</summary>
    public static string TranslateWarning(string warning)
    {
        var map = WillBrakeTranslations;
        return map.TryGetValue(warning.Trim().ToLowerInvariant(), out var zh) ? zh : warning;
    }

    private static readonly Lazy<Dictionary<string, string>> _willBrakeTranslations = new(LoadWillBrakeTranslations);

    private static Dictionary<string, string> WillBrakeTranslations => _willBrakeTranslations.Value;

    private static Dictionary<string, string> LoadWillBrakeTranslations()
    {
        var result = new Dictionary<string, string>();
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "TweakDb", "ServiceWarningsZh.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                result[prop.Name] = prop.Value.GetString() ?? prop.Name;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("ServiceWarningsZh.json 加载失败：" + ex.Message);
        }

        return result;
    }

    private static readonly Lazy<List<BxServiceEntry>> _allServices = new(() =>
    {
        try
        {
            var json = ReadAsset("allServices");
            return JsonSerializer.Deserialize<List<BxServiceEntry>>(json, JOpts) ?? new();
        }
        catch
        {
            return new();
        }
    });

    internal static readonly JsonSerializerOptions JOpts = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip, // 数据库带 // 注释
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    private static Dictionary<string, List<BxItem>> LoadAll()
    {
        var result = new Dictionary<string, List<BxItem>>();
        foreach (var section in Sections)
        {
            if (section.JsonFile is null)
            {
                continue;
            }

            try
            {
                var json = ReadAsset(section.JsonFile);
                var items = JsonSerializer.Deserialize<List<BxItem>>(json, JOpts) ?? new();
                // 先挑选匹配当前 Windows 版本的定义，再按名称去重。
                var build = Environment.OSVersion.Version.Build;
                result[section.Id] = SelectCompatibleItems(items, build);

                // 合并补充操作（special.json：为特殊条目提供本工具自己的等价操作定义）
                foreach (var item in result[section.Id])
                {
                    item.Tweaks.AddRange(GetSpecialOps(item.Name));
                    item.ValidationError = ValidateItem(item);
                }
            }
            catch
            {
                result[section.Id] = new();
            }
        }

        return result;
    }

    private static string ReadAsset(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "TweakDb", name + ".json");
        return File.ReadAllText(path);
    }

    private static readonly Lazy<Dictionary<string, List<BxTweak>>> _specials = new(LoadSpecials);

    /// <summary>读取补充操作定义（special.json：条目名 -> 操作列表）。</summary>
    public static List<BxTweak> GetSpecialOps(string name) =>
        _specials.Value.TryGetValue(name, out var list) ? list : new();

    private static Dictionary<string, List<BxTweak>> LoadSpecials()
    {
        try
        {
            var json = ReadAsset("special");
            var parsed = JsonSerializer.Deserialize<Dictionary<string, BxItem>>(json, JOpts);
            return parsed?.ToDictionary(
                p => p.Key,
                p => p.Value.Tweaks,
                StringComparer.OrdinalIgnoreCase) ?? new();
        }
        catch
        {
            return new();
        }
    }

    // ---------------- 中文名称与说明（外部 meta.json） ----------------

    /// <summary>条目显示名（中文名, 中文说明）。未收录的条目回退为原始 Id。</summary>
    public static (string Name, string Desc) Describe(string id)
    {
        var meta = LoadMeta();
        return meta.TryGetValue(id, out var m) ? (m.N, m.D) : (id, "优化数据库条目（暂无中文说明）。");
    }

    /// <summary>
    /// 状态描述模式：enable=开/关描述为“已启用/未启用”，remove=“已移除/未移除”，disable（默认）=“已禁用/未禁用”。
    /// 优先条目自身的 "m" 字段，其次栏目级 "sec.{Id}" 的 "m"，最后回退 disable。
    /// </summary>
    public static string ModeOf(string id, string sectionId)
    {
        var meta = LoadMeta();
        if (meta.TryGetValue(id, out var item) && !string.IsNullOrEmpty(item.M))
        {
            return item.M;
        }

        if (meta.TryGetValue("sec." + sectionId, out var sec) && !string.IsNullOrEmpty(sec.M))
        {
            return sec.M;
        }

        return "disable";
    }

    private sealed record MetaEntry(string N, string D, string M, string G);

    /// <summary>该条目是否为多档位条目（meta.json "g" 字段 == "multi"）。</summary>
    public static bool IsGearItem(string id) =>
        LoadMeta().TryGetValue(id, out var m) && m.G == "multi";

    private static Dictionary<string, MetaEntry>? _metaCache;

    private static Dictionary<string, MetaEntry> LoadMeta()
    {
        if (_metaCache is not null)
        {
            return _metaCache;
        }

        var result = new Dictionary<string, MetaEntry>();
        try
        {
            // 中文说明放在外部数据文件（Assets\TweakDb\meta.json），便于维护且不参与编译
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "TweakDb", "meta.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var m = prop.Value.TryGetProperty("m", out var modeEl) ? modeEl.GetString() ?? "" : "";
                var g = prop.Value.TryGetProperty("g", out var gearEl) ? gearEl.GetString() ?? "" : "";
                result[prop.Name] = new MetaEntry(
                    prop.Value.GetProperty("n").GetString() ?? prop.Name,
                    prop.Value.GetProperty("d").GetString() ?? "",
                    m,
                    g);
            }
        }
        catch
        {
            // 缺失时回退为原始 Id
        }

        _metaCache = result;
        return result;
    }

    // ---------------- 服务组（14 组，组内服务清单内置；名称与说明在 meta.json，键 "svc.{Id}"） ----------------

    public static readonly BxServiceGroup[] ServiceGroups =
    [
        // 「无用服务」组已移除（HANDOFF §25）：外部数据库曾混入引导关键项，风险收益不成比例
        new("svc-print", ["Spooler", "PrintScanBroker", "PrintNotify", "Fax"]),
        new("svc-remote", ["RemoteRegistry", "TermService", "UmRdpService", "SessionEnv", "RemoteAccess"]),
        new("svc-hyperv", ["vmcompute", "HvHost", "vmickvpexchange", "vmicguestinterface", "vmicheartbeat", "vmicrdv", "vmicshutdown", "vmictimesync", "vmicvmsession", "vmicvss"]),
        new("svc-hdd", ["SysMain"]),
        new("svc-index", ["WSearch"]),
        new("svc-datausage", ["DusmSvc"]),
        new("svc-autorun", ["ShellHWDetection"]),
        new("svc-driverupd", ["DsmSvc"]),
        new("svc-clipboard", ["cbdhsvc*"]),
        new("svc-store", ["InstallService", "ClipSVC"]),
        new("svc-msa", ["wlidsvc", "TokenBroker"]),
        new("svc-xbox", ["XblAuthManager", "XblGameSave", "XboxGipSvc", "XboxNetApiSvc"]),
        new("svc-defrag", ["defragsvc"]),
    ];
}
