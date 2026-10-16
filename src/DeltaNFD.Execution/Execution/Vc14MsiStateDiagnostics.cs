using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeltaNFD.Services;

// Native SIDs are used only to target an exact read. Reports use anonymous owner labels.
internal sealed record Vc14MsiEnumeration(uint ReturnCode, string ProductCode, uint Context, string UserSid, string Detail = "");
internal interface IVc14MsiDiagnosticPlatform
{
    string? CurrentUserSid { get; }
    Vc14MsiEnumeration Enumerate(string code, string? sid, uint context, uint index);
    Vc14MsiPropertyDiagnostic ReadProperty(string code, string? sid, uint context, string property);
}

internal sealed class Vc14MsiStateException(Vc14MsiStateDiagnostic diagnostic) : IOException(
    "产品安装状态未确认：" + diagnostic.ProductCode + "；" + diagnostic.Summary)
{
    internal Vc14MsiStateDiagnostic Diagnostic { get; } = diagnostic;
}

/// <summary>One scan owns one session. Duplicate discovery does not repeat context enumeration or warning rows.</summary>
internal sealed class Vc14MsiDiagnosticSession(IVc14MsiDiagnosticPlatform? platform = null)
{
    internal const int MaximumProducts = 32;
    internal const int MaximumReportBytes = 128 * 1024;
    private readonly IVc14MsiDiagnosticPlatform _platform = platform ?? new Vc14MsiDiagnosticWindowsPlatform();
    private readonly Dictionary<string, Vc14MsiStateDiagnostic> _diagnostics = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);
    internal bool Truncated { get; private set; }
    internal IReadOnlyList<Vc14MsiStateDiagnostic> Results
    {
        get
        {
            var result = new List<Vc14MsiStateDiagnostic>();
            var bytes = 2;
            foreach (var value in _diagnostics.Values)
            {
                if (_sources[value.ProductCode].Count > 8) Truncated = true;
                var row = value with { Sources = _sources[value.ProductCode].OrderBy(s => s, StringComparer.Ordinal).Take(8).ToArray() };
                var length = JsonSerializer.SerializeToUtf8Bytes(row).Length + (result.Count == 0 ? 0 : 1);
                if (bytes + length > MaximumReportBytes) { Truncated = true; continue; }
                result.Add(row); bytes += length;
            }
            return result;
        }
    }

    internal Vc14MsiStateDiagnostic Observe(string code, int state, string source, bool expand = false)
    {
        code = Vc14MsiStateDiagnostics.NormalizeCode(code);
        if (_diagnostics.TryGetValue(code, out var existing))
        {
            _sources[code].Add(source[..Math.Min(source.Length, 256)]);
            // Do not hide a state transition or reuse a healthy-only observation after identity read failed.
            if (existing.StateCode == state && (!expand || existing.Contexts.Count > 0)) return existing;
        }
        else if (_diagnostics.Count >= MaximumProducts)
        {
            Truncated = true;
            return Vc14MsiStateDiagnostics.Limited(code, state, "本次诊断达到 32 个产品上限，未收集此项上下文。");
        }
        var result = Vc14MsiStateDiagnostics.Capture(code, state, _platform, expand || state != 5);
        var states = (existing?.ObservedStateCodes ?? []).Append(state).Distinct().ToArray();
        if (states.Length > 8) Truncated = true;
        result = result with { ObservedStateCodes = states.Take(8).ToArray() };
        _diagnostics[code] = result;
        if (!_sources.ContainsKey(code)) _sources[code] = new(StringComparer.Ordinal);
        _sources[code].Add(source[..Math.Min(source.Length, 256)]);
        return result;
    }

    internal void AddIssue(ICollection<string>? issues, string prefix, Exception error, string? code)
    {
        if (issues is null) return;
        if (error is Vc14MsiStateException stateError)
        {
            if (_reported.Add(stateError.Diagnostic.ProductCode)) issues.Add(prefix + "：" + error.Message);
            return;
        }
        var detail = code is not null && _diagnostics.TryGetValue(code, out var evidence)
            ? "；" + evidence.Summary : "";
        issues.Add(prefix + "：" + error.Message + detail);
    }
}

internal static class Vc14MsiStateDiagnostics
{
    internal const int MaximumInstances = 8;
    internal const string Limitations = "仅作诊断，不代表 MSI 身份已核验或登记可删除。所有用户枚举不包含其他用户的非托管仅通告产品；空结果不能证明全机不存在。未改变用户身份，也未启用 Installer 服务。";
    internal static readonly (string Name, string? Sid, uint Context)[] Scopes =
    [
        ("计算机级", null, 4),
        ("当前用户托管", null, 1),
        ("当前用户非托管", null, 2),
        ("所有用户托管", "S-1-1-0", 1),
        ("所有用户非托管", "S-1-1-0", 2),
    ];
    internal static readonly string[] Properties = ["State", "ProductName", "VersionString", "LocalPackage"];
    private static readonly Regex SidPattern = new(@"^S-\d{1,3}(?:-\d{1,10}){1,15}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static string NormalizeCode(string code) => Guid.TryParseExact(code, "B", out var id) && id != Guid.Empty
        ? id.ToString("B").ToUpperInvariant() : throw new ArgumentException("MSI 诊断产品码无效。");

    internal static (string Name, string Meaning) State(int code) => code switch
    {
        -7 => ("INSTALLSTATE_NOTUSED", "未使用状态，需核对"),
        -6 => ("INSTALLSTATE_BADCONFIG", "返回配置数据损坏状态，需核对"),
        -5 => ("INSTALLSTATE_INCOMPLETE", "安装未完成状态，需核对"),
        -4 => ("INSTALLSTATE_SOURCEABSENT", "安装源缺失状态，需核对"),
        -3 => ("INSTALLSTATE_MOREDATA", "数据不足状态，需核对"),
        -2 => ("INSTALLSTATE_INVALIDARG", "查询参数无效"),
        -1 => ("INSTALLSTATE_UNKNOWN", "当前查询未发现安装或通告，仍需核对其他上下文"),
        0 => ("INSTALLSTATE_BROKEN", "返回损坏状态，需核对"),
        1 => ("INSTALLSTATE_ADVERTISED", "仅通告，尚未安装"),
        2 => ("INSTALLSTATE_ABSENT", "当前查询为其他用户安装状态"),
        3 => ("INSTALLSTATE_LOCAL", "本地状态，非标准产品查询结果，不能视为完整安装"),
        4 => ("INSTALLSTATE_SOURCE", "源状态，非标准产品查询结果，不能视为完整安装"),
        5 => ("INSTALLSTATE_DEFAULT", "当前用户查询为已安装；仍需核验 MSI 身份"),
        _ => ("INSTALLSTATE_UNRECOGNIZED", "未知返回值，不能认定安装或缺失"),
    };

    internal static string ErrorName(uint code) => code switch
    {
        0 => "ERROR_SUCCESS", 5 => "ERROR_ACCESS_DENIED", 87 => "ERROR_INVALID_PARAMETER",
        234 => "ERROR_MORE_DATA", 259 => "ERROR_NO_MORE_ITEMS", 1601 => "ERROR_INSTALL_SERVICE_FAILURE",
        1605 => "ERROR_UNKNOWN_PRODUCT", 1608 => "ERROR_UNKNOWN_PROPERTY", 1610 => "ERROR_BAD_CONFIGURATION",
        1627 => "ERROR_FUNCTION_FAILED", _ => "WIN32_ERROR_" + code,
    };

    internal static Vc14MsiStateDiagnostic Limited(string code, int state, string detail)
    {
        var meaning = State(state);
        return new(code, state, meaning.Name, meaning.Meaning, DateTimeOffset.UtcNow, [],
            $"MSI 状态={state}/{meaning.Name}（{meaning.Meaning}）。{detail}", Limitations) { ObservedStateCodes = [state] };
    }

    internal static Vc14MsiStateDiagnostic Capture(string code, int state, IVc14MsiDiagnosticPlatform platform, bool expand = true)
    {
        code = NormalizeCode(code);
        if (!expand) return Limited(code, state, "身份读取通过后才可视为已核验组件，本项未扩展上下文。");
        var contexts = new List<Vc14MsiContextDiagnostic>();
        var remainingValueCharacters = 4096;
        var owners = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string? currentSid;
        try { currentSid = platform.CurrentUserSid; } catch { currentSid = null; }
        foreach (var scope in Scopes)
        {
            var instances = new List<Vc14MsiInstanceDiagnostic>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Vc14MsiContextDiagnostic Result(uint? error, bool complete, string detail) =>
                new(scope.Name, scope.Context, error, error is { } value ? ErrorName(value) : "QUERY_EXCEPTION", complete, instances.ToArray(), detail);
            try
            {
                var finished = false;
                for (uint index = 0; index < MaximumInstances; index++)
                {
                    // No await: the complete native enumeration stays on one OS thread.
                    var row = platform.Enumerate(code, scope.Sid, scope.Context, index);
                    if (row.ReturnCode is 259 or 1605)
                    {
                        contexts.Add(Result(row.ReturnCode, true, instances.Count == 0 ? "此范围未枚举到记录；不是全机不存在证明。" : "此范围枚举结束。"));
                        finished = true; break;
                    }
                    if (row.ReturnCode != 0)
                    {
                        contexts.Add(Result(row.ReturnCode, false, row.Detail.Length > 0 ? row.Detail : "查询未完成，不能视为无记录。"));
                        finished = true; break;
                    }
                    if (!Guid.TryParse(row.ProductCode, out var returned) || returned.ToString("B").ToUpperInvariant() != code ||
                        row.Context != scope.Context || row.UserSid.Length > 256 ||
                        row.Context == 4 && row.UserSid.Length != 0 ||
                        row.Context != 4 && (!SidPattern.IsMatch(row.UserSid) ||
                            scope.Sid is null && currentSid is not null && !string.Equals(row.UserSid, currentSid, StringComparison.OrdinalIgnoreCase)) ||
                        !seen.Add(row.Context + ":" + row.UserSid))
                    {
                        contexts.Add(Result(null, false, "返回产品码、上下文或用户标识不一致，或重复返回同一实例；未据此读取属性。"));
                        finished = true; break;
                    }
                    var owner = "计算机级";
                    if (row.Context != 4)
                    {
                        if (scope.Sid is null || string.Equals(row.UserSid, currentSid, StringComparison.OrdinalIgnoreCase)) owner = "当前用户";
                        else
                        {
                            if (!owners.TryGetValue(row.UserSid, out var ordinal)) owners[row.UserSid] = ordinal = owners.Count + 1;
                            owner = (currentSid is null ? "用户（是否当前用户未确认）" : "其他用户") + ordinal;
                        }
                    }
                    var properties = new List<Vc14MsiPropertyDiagnostic>();
                    foreach (var property in Properties)
                    {
                        try
                        {
                            var value = platform.ReadProperty(code, row.Context == 4 ? null : row.UserSid, row.Context, property);
                            var length = Math.Min(value.Value.Length, remainingValueCharacters);
                            properties.Add(value with { Value = value.Value[..length], ValueTruncated = value.ValueTruncated || length < value.Value.Length });
                            remainingValueCharacters -= length;
                        }
                        catch (Exception ex) { properties.Add(new(property, null, "QUERY_EXCEPTION/" + ex.GetType().Name, "")); }
                    }
                    instances.Add(new(row.Context, row.Context == 4 ? "Machine" : row.Context == 1 ? "UserManaged" : "UserUnmanaged", owner, properties));
                }
                if (!finished) contexts.Add(Result(0, false, "达到每范围 8 项上限，结果不完整；未继续枚举。"));
            }
            catch (Exception ex) { contexts.Add(Result(null, false, "上下文查询异常：" + ex.GetType().Name)); }
        }
        var meaning = State(state);
        var summary = $"MSI 状态={state}/{meaning.Name}（{meaning.Meaning}）。上下文：" + string.Join("；", contexts.Select(c =>
            c.Scope + (c.Complete ? "=" + c.Instances.Count + "项" : "查询未完成(" + (c.ReturnCode?.ToString() ?? "无返回码") + "/" + c.Result + ")") +
                (c.Instances.Any(i => i.Properties.Any(p => p.Property == "State" && p.ReturnCode == 0 && p.Value == "5")) ? "（含已安装记录）" : "") +
                (c.Instances.Any(i => i.Properties.Any(p => p.ReturnCode != 0 || p.ValueTruncated)) ? "（部分属性未确认或截断）" : ""))) + "。仅作诊断，不据此放行卸载或清理。";
        return new(code, state, meaning.Name, meaning.Meaning, DateTimeOffset.UtcNow, contexts, summary, Limitations) { ObservedStateCodes = [state] };
    }
}

internal sealed class Vc14MsiDiagnosticWindowsPlatform : IVc14MsiDiagnosticPlatform
{
    internal static Vc14MsiStateDiagnostic ReadNative(string code)
    {
        code = Vc14MsiStateDiagnostics.NormalizeCode(code);
        return Vc14MsiStateDiagnostics.Capture(code, MsiQueryProductStateW(code), new Vc14MsiDiagnosticWindowsPlatform());
    }
    public string? CurrentUserSid { get { using var identity = WindowsIdentity.GetCurrent(); return identity.User?.Value; } }
    public Vc14MsiEnumeration Enumerate(string code, string? sid, uint context, uint index)
    {
        var product = new StringBuilder(39); var owner = new StringBuilder(256); uint length = 256;
        var error = MsiEnumProductsExW(code, sid, context, index, product, out var foundContext, owner, ref length);
        return new(error, product.ToString(), foundContext, owner.ToString(), error == 234 ? "SID 缓冲区不足（需要 " + length + " 字符），未扩大读取范围。" : "");
    }
    public Vc14MsiPropertyDiagnostic ReadProperty(string code, string? sid, uint context, string property)
    {
        var buffer = new StringBuilder(512); uint length = 512;
        var error = MsiGetProductInfoExW(code, sid, context, property, buffer, ref length);
        if (error == 234 && length < 4096)
        {
            buffer = new StringBuilder((int)length + 1); length = (uint)buffer.Capacity;
            error = MsiGetProductInfoExW(code, sid, context, property, buffer, ref length);
        }
        return new(property, error, Vc14MsiStateDiagnostics.ErrorName(error), error == 0 ? buffer.ToString() : "");
    }
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MsiQueryProductStateW(string product);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiEnumProductsExW(string product, string? sid, uint context, uint index, StringBuilder installedProduct,
        out uint installedContext, StringBuilder installedSid, ref uint sidLength);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiGetProductInfoExW(string product, string? sid, uint context, string property, StringBuilder value, ref uint length);
}
