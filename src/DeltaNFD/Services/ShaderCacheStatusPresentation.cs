namespace DeltaNFD.Services;

/// <summary>只读缓存范围状态；不把缺项、重复项或未知状态显示为默认。</summary>
internal static class ShaderCacheStatusPresentation
{
    internal static bool Complete(NvShaderCacheReport? report) => new[] { "全局设置", "三角洲程序设置" }
        .All(scope => report?.Scopes.Count(row => row.Scope == scope) == 1
            && report.Scopes.Single(row => row.Scope == scope).State is "default" or "inherited" or "custom");

    internal static string Scope(NvShaderCacheReport? report, string scope)
    {
        var rows = report?.Scopes.Where(s => s.Scope == scope).ToArray() ?? [];
        if (rows.Length != 1) return "状态未确认，重新检测后再核对。";
        var row = rows[0];
        var state = row.State switch
        {
            "default" => "驱动默认",
            "inherited" => "继承全局设置",
            "custom" => "非默认设置",
            _ => "状态未确认",
        };
        return string.IsNullOrWhiteSpace(row.Detail) ? state : state + " · " + row.Detail;
    }
}
