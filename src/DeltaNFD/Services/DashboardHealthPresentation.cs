namespace DeltaNFD.ViewModels;

public enum DashboardHealthState
{
    Checking, Normal, Information, Warning, Abnormal, NotApplicable, Failed,
}

public static class DashboardHealthPresentation
{
    public static string Label(DashboardHealthState state) => state switch
    {
        DashboardHealthState.Checking => "检测中",
        DashboardHealthState.Normal => "正常",
        DashboardHealthState.Information => "提示",
        DashboardHealthState.Warning => "需关注",
        DashboardHealthState.Abnormal => "异常",
        DashboardHealthState.NotApplicable => "不适用",
        DashboardHealthState.Failed => "检测失败",
        _ => "状态未知",
    };

    public static string Summary(bool scanning, params DashboardHealthState[] states)
    {
        var abnormal = states.Count(s => s is DashboardHealthState.Abnormal or DashboardHealthState.Failed);
        var warning = states.Count(s => s == DashboardHealthState.Warning);
        if (scanning || states.Contains(DashboardHealthState.Checking))
            return abnormal > 0 ? $"正在检测 · {abnormal} 项异常" : "正在检测组件";
        if (abnormal > 0) return warning > 0 ? $"{abnormal} 项异常 · {warning} 项提醒" : $"{abnormal} 项异常";
        if (warning > 0) return $"{warning} 项提醒";
        if (states.Contains(DashboardHealthState.Information)) return "存在组件提示";
        if (states.Contains(DashboardHealthState.NotApplicable)) return "部分组件不适用";
        return states.Length > 0 && states.All(s => s == DashboardHealthState.Normal) ? "组件检测正常" : "组件状态未确认";
    }
}
