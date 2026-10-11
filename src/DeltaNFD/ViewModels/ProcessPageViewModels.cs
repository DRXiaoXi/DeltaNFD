using Microsoft.UI.Xaml;

namespace DeltaNFD.ViewModels;

internal static class ProcessPriorityDisplay
{
    internal static string Memory(string value) => value switch
    {
        "1" => "很低", "2" => "低", "3" => "中等", "4" => "低于正常", "5" => "正常（最高可用）", _ => value,
    };
}

/// <summary>进程列表行（只读快照 + 身份三元组，供选中后详情加载复核）。</summary>
public sealed class ProcessRowVm
{
    public required string Name { get; init; }
    public required int Pid { get; init; }
    public required long StartTimeUtcTicks { get; init; }
    public required string ExecutablePath { get; init; }
    public double? WorkingSetMb { get; init; }
    public double? CpuPercent { get; init; }
    public int OwnedPropertyCount { get; init; }
    public bool Protected { get; init; }

    public string PidText => $"PID {Pid}" + (OwnedPropertyCount > 0 ? $" · 已调整 {OwnedPropertyCount} 项" : "");
    public string CpuText => CpuPercent is { } cpu ? $"{cpu:0.#}%" : "—";
    public string WorkingSetText => WorkingSetMb is { } mb ? $"{mb:0.#} MB" : "—";
    public string MemoryText => (CpuPercent is { } cpu ? $"CPU {cpu:0.#}% · " : "CPU — · ") + (WorkingSetMb is { } mb ? $"{mb:0.#} MB" : "—");
    public string HeldText => OwnedPropertyCount > 0 ? $"持有 {OwnedPropertyCount}" : "";
    public Visibility ProtectedVisibility => Protected ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>能力条目（详情面板）。</summary>
public sealed class CapabilityVm
{
    public required string Title { get; init; }
    public required string Support { get; init; }
    public required string CurrentValue { get; init; }
    public required string Owner { get; init; }
    public required string Error { get; init; }

    public string ChipText => Support switch
    {
        "supported" => Owner == "system" ? "可修改" : "由 " + Owner + " 控制",
        "readOnly" => "只读",
        "unsupported" => "暂不支持",
        _ => Support,
    };
    public Visibility ChipVisibility => Support == "supported" && Owner == "system"
        ? Visibility.Collapsed : Visibility.Visible;
    public string ValueText => "当前实际值：" + (Title == "内存优先级" ? ProcessPriorityDisplay.Memory(CurrentValue) : CurrentValue);
    public string ErrorText => Error;
    public Visibility ErrorVisibility => Error.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>持有记录条目。</summary>
public sealed class HeldChangeVm
{
    public required string Capability { get; init; }
    public required string OriginalValue { get; init; }
    public required string AppliedValue { get; init; }
    public required string Owner { get; init; }
    public required DateTimeOffset AppliedUtc { get; init; }

    public string DisplayText =>
        $"{Capability}：{(Capability == "内存优先级" ? ProcessPriorityDisplay.Memory(OriginalValue) : OriginalValue)} → {(Capability == "内存优先级" ? ProcessPriorityDisplay.Memory(AppliedValue) : AppliedValue)}（{Owner}，{AppliedUtc.LocalDateTime:HH:mm:ss}）";
}
