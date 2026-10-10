namespace DeltaNFD.Backends;

public sealed record BackgroundProcessDto(int Pid, long StartTimeUtcTicks, string ExecutablePath,
    string Name, double MemoryMb, string Category);

public sealed class ProcessMaintenanceArgs
{
    public List<BackgroundProcessDto> Processes { get; set; } = [];
}

public sealed record ProcessMaintenanceItem(int Pid, string Status, string Message, double MemoryMb);

public sealed record ProcessMaintenanceReport(IReadOnlyList<ProcessMaintenanceItem> Items, bool Cancelled)
{
    public int Completed => Items.Count(item => item.Status == "ok");
    public double MemoryMb => Items.Where(item => item.Status == "ok").Sum(item => item.MemoryMb);
}
