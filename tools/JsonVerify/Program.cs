using System.Text.Json;
using System.Text.Json.Serialization;

// 与 BxCatalog.JOpts 相同的解析选项验证全部 TweakDb 数据库文件
var opts = new JsonSerializerOptions
{
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
};

var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
    "..", "..", "..", "..", "..", "src", "DeltaNFD", "Assets", "TweakDb"));
Console.WriteLine("TweakDb dir: " + dir);

var bad = 0;
foreach (var file in Directory.GetFiles(dir, "*.json"))
{
    var name = Path.GetFileName(file);
    if (name is "meta.json" or "special.json")
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            Console.WriteLine($"OK   {name} (raw document, {doc.RootElement.EnumerateObject().Count()} keys)");
        }
        catch (Exception ex)
        {
            bad++;
            Console.WriteLine($"FAIL {name}: {ex.Message}");
        }
        continue;
    }

    try
    {
        var items = JsonSerializer.Deserialize<List<BxItem>>(File.ReadAllText(file), opts);
        Console.WriteLine($"OK   {name}: {items?.Count ?? 0} items");
    }
    catch (Exception ex)
    {
        bad++;
        Console.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

Console.WriteLine(bad == 0 ? "ALL DATABASE FILES PARSED OK" : $"{bad} FILE(S) FAILED");
return bad == 0 ? 0 : 1;

public sealed class BxValueEntry
{
    public List<string> ValueTypes { get; set; } = new();

    [JsonConverter(typeof(LooseStringJsonConverter))]
    public string? Value { get; set; }
}

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
        if (value is null) writer.WriteNullValue(); else writer.WriteStringValue(value);
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
    public List<BxTweak> Tweaks { get; set; } = new();
}
