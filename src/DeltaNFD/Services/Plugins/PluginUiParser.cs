using System.Text.Json;

namespace DeltaNFD.Services.Plugins;

public enum PluginControlType { Text, Input, Number, Toggle, Select, Button, Progress, Result }

/// <summary>声明式控件基元；UI 定义不是可执行模板，宿主按纯文本渲染。</summary>
public sealed record PluginControl(
    string Id,
    PluginControlType Type,
    string? Text,
    string? Label,
    string? OperationId,
    bool? DefaultToggle,
    string? DefaultInput,
    double? DefaultNumber,
    string? DefaultSelect,
    double? Min,
    double? Max,
    double? Step,
    IReadOnlyList<(string Value, string Label)>? Options)
{
    public bool IsInput => Type is PluginControlType.Input or PluginControlType.Number or PluginControlType.Toggle or PluginControlType.Select;
}

/// <summary>ui.json 解析结果。</summary>
public sealed record PluginUi(int SchemaVersion, IReadOnlyList<PluginControl> Controls)
{
    public PluginControl? FindControl(string id) => Controls.FirstOrDefault(c => c.Id == id);

    /// <summary>输入控件当前值与默认值合并后的 values 对象（发给后端前由宿主组装）。</summary>
    public IReadOnlyDictionary<string, object?> DefaultValues()
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var control in Controls)
        {
            switch (control.Type)
            {
                case PluginControlType.Input: values[control.Id] = control.DefaultInput ?? ""; break;
                case PluginControlType.Number: values[control.Id] = control.DefaultNumber; break;
                case PluginControlType.Toggle: values[control.Id] = control.DefaultToggle ?? false; break;
                case PluginControlType.Select: values[control.Id] = control.DefaultSelect; break;
            }
        }
        return values;
    }
}

/// <summary>
/// ui.json 严格解析器（规范第 4 节）。只接受固定控件集合、无表达式/动态代码/远程资源；
/// 文本 ≤4096 字符、单表单 ≤100 控件、控件 ID 与操作 ID 包内唯一并须与 manifest 对应。
/// </summary>
public static class PluginUiParser
{
    public static IReadOnlyDictionary<string, object?> ReadConfiguration(PluginUi ui, byte[] bytes)
    {
        using var doc = PluginJson.ParseStrict(bytes, "插件配置");
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new PluginContractException("插件配置必须为对象。");
        var values = new Dictionary<string, object?>(ui.DefaultValues(), StringComparer.Ordinal);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            // Upgrades may remove controls; their saved values are not forwarded to the backend.
            var control = ui.FindControl(property.Name);
            if (control?.IsInput != true) continue;
            values[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number when property.Value.TryGetDouble(out var number) => number,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new PluginContractException("插件配置含非法输入类型：" + property.Name),
            };
        }
        if (!TryValidateValues(ui, values, out var error)) throw new PluginContractException(error);
        return values;
    }
    private static readonly string[] ControlFields =
        ["id", "type", "text", "label", "operationId", "default", "min", "max", "step", "options"];

    public static PluginUi Parse(byte[] utf8, PluginManifest manifest) =>
        Parse(utf8, manifest, origin: "ui.json");

    public static PluginUi Parse(byte[] utf8, PluginManifest manifest, string origin)
    {
        using var document = PluginJson.ParseStrict(utf8, origin);
        return ParseElement(document.RootElement, manifest, origin);
    }

    public static PluginUi ParseElement(JsonElement root, PluginManifest manifest, string origin)
    {
        if (root.ValueKind is not JsonValueKind.Object)
            throw new PluginContractException($"{origin}：根节点必须是对象。");
        PluginManifestParser.EnsureNoExtraProperties(root, ["schemaVersion", "controls"], origin);

        if (!PluginManifestParser.TryGetInt(root, "schemaVersion", out var schema) || schema != PluginContract.SchemaVersion)
            throw new PluginContractException($"{origin}：schemaVersion 必须为 {PluginContract.SchemaVersion}。");
        if (!PluginManifestParser.TryGetArray(root, "controls", out var controls))
            throw new PluginContractException($"{origin}：缺少 controls 数组。");
        if (controls.GetArrayLength() == 0)
            throw new PluginContractException($"{origin}：controls 不能为空。");
        if (controls.GetArrayLength() > PluginContract.MaxUiControls)
            throw new PluginContractException($"{origin}：控件数超过 {PluginContract.MaxUiControls}。");

        var parsed = new List<PluginControl>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in controls.EnumerateArray())
        {
            if (item.ValueKind is not JsonValueKind.Object)
                throw new PluginContractException($"{origin}：控件必须是对象。");
            PluginManifestParser.EnsureNoExtraProperties(item, ControlFields, origin);
            parsed.Add(ParseControl(item, manifest, seenIds, origin));
        }
        return new PluginUi(schema, parsed);
    }

    private static PluginControl ParseControl(JsonElement item, PluginManifest manifest, HashSet<string> seenIds, string origin)
    {
        if (!PluginManifestParser.TryGetString(item, "id", out var id) || !PluginManifestParser.IsShortId(id))
            throw new PluginContractException($"{origin}：控件 id 必须为 1-64 字符、字母开头、仅含字母数字 _ -。");
        if (!seenIds.Add(id))
            throw new PluginContractException($"{origin}：控件 id“{id}”重复。");
        if (!PluginManifestParser.TryGetString(item, "type", out var typeText) || !TryParseControlType(typeText, out var type))
            throw new PluginContractException($"{origin}：控件“{id}”的类型必须是 text/input/number/toggle/select/button/progress/result 之一。");

        string? text = null, label = null, operationId = null, defaultInput = null, defaultSelect = null;
        bool? defaultToggle = null;
        double? min = null, max = null, step = null, defaultNumber = null;
        IReadOnlyList<(string, string)>? options = null;

        if (PluginManifestParser.TryGetString(item, "text", out var textValue))
        {
            if (textValue.Length > PluginContract.MaxUiTextChars)
                throw new PluginContractException($"{origin}：控件“{id}”文本超过 {PluginContract.MaxUiTextChars} 字符。");
            text = textValue;
        }
        if (PluginManifestParser.TryGetString(item, "label", out var labelValue))
        {
            if (labelValue.Length > PluginContract.MaxUiTextChars)
                throw new PluginContractException($"{origin}：控件“{id}”标签超过 {PluginContract.MaxUiTextChars} 字符。");
            label = labelValue;
        }

        switch (type)
        {
            case PluginControlType.Text:
                if (text is null) throw new PluginContractException($"{origin}：text 控件“{id}”必须有 text。");
                break;
            case PluginControlType.Input:
                if (label is null) throw new PluginContractException($"{origin}：input 控件“{id}”必须有 label。");
                if (PluginManifestParser.TryGetString(item, "default", out var inputDefault))
                {
                    if (inputDefault.Length > PluginContract.MaxFormValueChars)
                        throw new PluginContractException($"{origin}：input 控件“{id}”默认值超过 {PluginContract.MaxFormValueChars} 字符。");
                    defaultInput = inputDefault;
                }
                break;
            case PluginControlType.Number:
                if (label is null) throw new PluginContractException($"{origin}：number 控件“{id}”必须有 label。");
                if (!PluginManifestParser.TryGetDouble(item, "min", out var minValue) ||
                    !PluginManifestParser.TryGetDouble(item, "max", out var maxValue) ||
                    !PluginManifestParser.TryGetDouble(item, "step", out var stepValue) ||
                    !PluginManifestParser.TryGetDouble(item, "default", out var defaultValue))
                    throw new PluginContractException($"{origin}：number 控件“{id}”必须声明 min/max/step/default。");
                if (double.IsNaN(minValue) || double.IsInfinity(minValue) || double.IsNaN(maxValue) ||
                    double.IsInfinity(maxValue) || double.IsNaN(stepValue) || double.IsInfinity(stepValue) ||
                    double.IsNaN(defaultValue) || double.IsInfinity(defaultValue))
                    throw new PluginContractException($"{origin}：number 控件“{id}”的数值必须是有限值。");
                if (minValue > maxValue)
                    throw new PluginContractException($"{origin}：number 控件“{id}”的 min 不得大于 max。");
                if (stepValue <= 0)
                    throw new PluginContractException($"{origin}：number 控件“{id}”的 step 必须为正。");
                if (defaultValue < minValue || defaultValue > maxValue)
                    throw new PluginContractException($"{origin}：number 控件“{id}”的 default 必须在 min 与 max 之间。");
                min = minValue; max = maxValue; step = stepValue; defaultNumber = defaultValue;
                break;
            case PluginControlType.Toggle:
                if (label is null) throw new PluginContractException($"{origin}：toggle 控件“{id}”必须有 label。");
                if (PluginManifestParser.TryGetBool(item, "default", out var toggleDefault)) defaultToggle = toggleDefault;
                break;
            case PluginControlType.Select:
                if (label is null) throw new PluginContractException($"{origin}：select 控件“{id}”必须有 label。");
                if (!PluginManifestParser.TryGetArray(item, "options", out var optionsArray) || optionsArray.GetArrayLength() == 0)
                    throw new PluginContractException($"{origin}：select 控件“{id}”必须声明非空 options。");
                var values = new HashSet<string>(StringComparer.Ordinal);
                var parsedOptions = new List<(string, string)>();
                foreach (var option in optionsArray.EnumerateArray())
                {
                    if (option.ValueKind is not JsonValueKind.Object)
                        throw new PluginContractException($"{origin}：select 控件“{id}”的 option 必须是对象。");
                    PluginManifestParser.EnsureNoExtraProperties(option, ["value", "label"], origin);
                    if (!PluginManifestParser.TryGetString(option, "value", out var value) ||
                        !PluginManifestParser.TryGetString(option, "label", out var optionLabel))
                        throw new PluginContractException($"{origin}：select 控件“{id}”的 option 必须包含 value 与 label。");
                    if (!values.Add(value))
                        throw new PluginContractException($"{origin}：select 控件“{id}”的选项 value“{value}”重复。");
                    parsedOptions.Add((value, optionLabel));
                }
                options = parsedOptions;
                if (PluginManifestParser.TryGetString(item, "default", out var selectDefault))
                {
                    if (!parsedOptions.Any(o => o.Item1 == selectDefault))
                        throw new PluginContractException($"{origin}：select 控件“{id}”的 default 必须是声明的选项之一。");
                    defaultSelect = selectDefault;
                }
                break;
            case PluginControlType.Button:
                if (label is null) throw new PluginContractException($"{origin}：button 控件“{id}”必须有 label。");
                if (!PluginManifestParser.TryGetString(item, "operationId", out operationId))
                    throw new PluginContractException($"{origin}：button 控件“{id}”必须绑定 operationId。");
                if (!manifest.HasOperation(operationId))
                    throw new PluginContractException($"{origin}：button 控件“{id}”绑定的操作“{operationId}”未在 manifest 声明。");
                break;
            case PluginControlType.Progress:
            case PluginControlType.Result:
                break;
        }
        if (operationId is not null && parsedHasOperation(manifest, operationId) == false)
            throw new PluginContractException($"{origin}：控件“{id}”引用未知操作。");
        return new PluginControl(id, type, text, label, operationId, defaultToggle, defaultInput, defaultNumber,
            defaultSelect, min, max, step, options);
    }

    private static bool parsedHasOperation(PluginManifest manifest, string operationId) => manifest.HasOperation(operationId);

    private static bool TryParseControlType(string text, out PluginControlType type)
    {
        switch (text)
        {
            case "text": type = PluginControlType.Text; return true;
            case "input": type = PluginControlType.Input; return true;
            case "number": type = PluginControlType.Number; return true;
            case "toggle": type = PluginControlType.Toggle; return true;
            case "select": type = PluginControlType.Select; return true;
            case "button": type = PluginControlType.Button; return true;
            case "progress": type = PluginControlType.Progress; return true;
            case "result": type = PluginControlType.Result; return true;
            default: type = default; return false;
        }
    }

    /// <summary>
    /// 宿主侧输入校验（规范第 4 节：宿主与后端都必须校验输入）。
    /// 输入按控件 ID 组成 values 对象；未声明控件的输入、类型不符、超长、越界均拒绝。
    /// </summary>
    public static bool TryValidateValues(PluginUi ui, IReadOnlyDictionary<string, object?> values, out string error)
    {
        foreach (var (id, value) in values)
        {
            var control = ui.FindControl(id);
            if (control is null) { error = $"未声明的控件值“{id}”。"; return false; }
            if (!control.IsInput) { error = $"控件“{id}”不接受输入值。"; return false; }
            switch (control.Type)
            {
                case PluginControlType.Input:
                    if (value is not string text || text.Length > PluginContract.MaxFormValueChars)
                    { error = $"控件“{id}”必须是 ≤{PluginContract.MaxFormValueChars} 字符的文本。"; return false; }
                    break;
                case PluginControlType.Number:
                    if (value is not double number && value is not int and not long and not float and not decimal)
                    { error = $"控件“{id}”必须是数字。"; return false; }
                    var numeric = value switch { double d => d, int i => i, long l => l, float f => f, decimal m => (double)m, _ => 0 };
                    if (double.IsNaN(numeric) || double.IsInfinity(numeric) ||
                        numeric < control.Min || numeric > control.Max)
                    { error = $"控件“{id}”的值必须在 {control.Min} 与 {control.Max} 之间。"; return false; }
                    break;
                case PluginControlType.Toggle:
                    if (value is not bool) { error = $"控件“{id}”必须是布尔值。"; return false; }
                    break;
                case PluginControlType.Select:
                    if (value is not string selected || control.Options?.Any(o => o.Item1 == selected) != true)
                    { error = $"控件“{id}”的值必须是声明的选项之一。"; return false; }
                    break;
            }
        }
        error = "";
        return true;
    }
}
