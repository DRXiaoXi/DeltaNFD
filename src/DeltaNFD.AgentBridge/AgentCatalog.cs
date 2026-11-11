using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeltaNFD.Backends;

namespace DeltaNFD.AgentBridge;

public sealed record AgentAction(string Name, string Module, string Description, string? ArgumentType,
    bool Mutating, JsonElement InputSchema);

/// <summary>Explicit reviewed public actions, never a wildcard export of the backend command table.</summary>
public static class AgentCatalog
{
    private sealed record Entry(string Name, string Module, string Description, string? ArgumentType);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static IReadOnlyList<AgentAction> Actions { get; } = Load();

    private static IReadOnlyList<AgentAction> Load()
    {
        using var stream = typeof(AgentCatalog).Assembly.GetManifestResourceStream("DeltaNFD.AgentBridge.actions.json")!;
        var entries = JsonSerializer.Deserialize<List<Entry>>(stream, Json)!;
        return entries.Select(e =>
        {
            if (e.Module != "app" && !BackendModules.IsAllowed(e.Module, e.Name))
                throw new InvalidOperationException("MCP 目录与后端合同不一致：" + e.Name);
            var schema = e.ArgumentType is null ? EmptySchema() : Schema(ArgumentType(e.ArgumentType), 0);
            var mutating = e.Module == "app" ? e.Name is not ("app.status" or "gpu.settings.catalog" or "system.optimizations.catalog" or "system.low-memory.status" or "frame.status")
                : BackendModules.IsMutating(e.Module, e.Name);
            if (mutating) schema["required"] = new JsonArray(((JsonObject)schema["properties"]!).Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray());
            return new AgentAction(e.Name, e.Module, e.Description, e.ArgumentType,
                mutating, JsonSerializer.SerializeToElement(schema));
        }).ToArray();
    }

    public static AgentAction Require(string name) => Actions.FirstOrDefault(a => a.Name == name)
        ?? throw new ArgumentException("未开放的操作：" + name);

    private static Type ArgumentType(string name) => typeof(BackendResult).Assembly.GetTypes().Concat(typeof(AgentCatalog).Assembly.GetTypes())
        .Single(t => t.Name == name);

    public static JsonElement Validate(string action, JsonElement arguments)
    {
        var spec = Require(action);
        if (arguments.ValueKind != JsonValueKind.Object || arguments.GetRawText().Length > 65536)
            throw new ArgumentException("arguments 必须是至多 64KiB 的 JSON 对象。");
        ValidateShape(spec.InputSchema, arguments, "arguments");
        if (spec.ArgumentType is not null)
        {
            try
            {
                _ = JsonSerializer.Deserialize(arguments.GetRawText(), ArgumentType(spec.ArgumentType), BackendJson.SerializerOptions)
                    ?? throw new ArgumentException("参数不可为空。");
            }
            catch (JsonException ex) { throw new ArgumentException("参数取值超出合同允许范围。", ex); }
        }
        return arguments.Clone();
    }

    private static JsonObject EmptySchema() => new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false };
    private static JsonObject Schema(Type original, int depth)
    {
        if (depth > 12) throw new InvalidOperationException("MCP 参数类型递归过深。");
        var nullable = Nullable.GetUnderlyingType(original);
        var type = nullable ?? original;
        JsonObject result;
        if (type.IsEnum) result = new() { ["type"] = "string", ["enum"] = new JsonArray(Enum.GetNames(type).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()) };
        else if (type == typeof(string) || type == typeof(Guid) || type == typeof(DateTimeOffset)) result = new() { ["type"] = "string", ["maxLength"] = 8192 };
        else if (type == typeof(bool)) result = new() { ["type"] = "boolean" };
        else if (type.IsPrimitive || type == typeof(decimal)) result = new() { ["type"] = type == typeof(float) || type == typeof(double) || type == typeof(decimal) ? "number" : "integer" };
        else if (type.IsArray || (type.IsGenericType && type.GetGenericArguments().Length == 1 && typeof(System.Collections.IEnumerable).IsAssignableFrom(type)))
            result = new() { ["type"] = "array", ["maxItems"] = 2000, ["items"] = Schema(type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[0], depth + 1) };
        else
        {
            result = EmptySchema();
            var props = (JsonObject)result["properties"]!;
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetMethod is not null
                && !(type == typeof(NvDriverApplyArgs) && p.Name == nameof(NvDriverApplyArgs.RebarWarningAccepted))))
                props[JsonNamingPolicy.CamelCase.ConvertName(p.Name)] = Schema(p.PropertyType, depth + 1);
            // Mutable DTO defaults remain backend-defined; callers may omit fields, never invent them.
        }
        if (nullable is not null) return new() { ["anyOf"] = new JsonArray(result, new JsonObject { ["type"] = "null" }) };
        return result;
    }

    private static void ValidateShape(JsonElement schema, JsonElement value, string path)
    {
        if (schema.TryGetProperty("anyOf", out var choices))
        {
            foreach (var child in choices.EnumerateArray())
            { try { ValidateShape(child, value, path); return; } catch (ArgumentException) { } }
            throw new ArgumentException(path + " 参数类型不符。");
        }
        var kind = schema.GetProperty("type").GetString();
        var valid = kind switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= 2000,
            "string" => value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 8192,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" => value.ValueKind == JsonValueKind.Number && (value.TryGetInt64(out _) || value.TryGetUInt64(out _)),
            "number" => value.ValueKind == JsonValueKind.Number,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => false,
        };
        if (!valid) throw new ArgumentException(path + " 参数类型或长度不符。");
        if (schema.TryGetProperty("enum", out var names) && !names.EnumerateArray().Any(x => x.GetString() == value.GetString()))
            throw new ArgumentException(path + " 枚举值不符，请使用目录中的名称。");
        if (kind == "object")
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var field in required.EnumerateArray())
                    if (!value.TryGetProperty(field.GetString()!, out _)) throw new ArgumentException(path + " 缺少显式参数：" + field.GetString());
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name) || !schema.GetProperty("properties").TryGetProperty(property.Name, out var child))
                    throw new ArgumentException(path + " 包含未知或重复字段：" + property.Name);
                ValidateShape(child, property.Value, path + "." + property.Name);
            }
        }
        if (kind == "array") foreach (var item in value.EnumerateArray()) ValidateShape(schema.GetProperty("items"), item, path + "[]");
    }
}

public sealed class AgentTweakArgs { public DeltaNFD.Services.SystemTweak? Tweak { get; set; } }
public sealed class AgentGroupArgs { public string GroupId { get; set; } = ""; }
