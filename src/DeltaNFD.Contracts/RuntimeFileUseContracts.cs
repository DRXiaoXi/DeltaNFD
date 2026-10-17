namespace DeltaNFD.Services;

public sealed record RuntimeFileUseProcess(int Pid, long StartFileTime, string Name, string ImagePath, bool CanClose, string ProtectionReason);
public enum RuntimeFileUseScope { Vc14, FullRepair }
public sealed class RuntimeFileUseScanArgs
{
    public RuntimeFileUseScope Scope { get; set; }
}
public sealed record RuntimeFileUseScan(bool Success, string Message, IReadOnlyList<RuntimeFileUseProcess> Processes, string SnapshotId = "")
{
    public RuntimeFileUseScope Scope { get; init; }
}
public sealed class RuntimeFileUseCloseArgs
{
    public string SnapshotId { get; set; } = "";
    public List<int> ProcessIds { get; set; } = [];
    public bool Confirmed { get; set; }
    public bool Force { get; set; }
    public bool ForceConfirmed { get; set; }
}
public sealed record RuntimeFileUseAction(int Pid, string Status, string Message);
public sealed record RuntimeFileUseCloseResult(bool Success, string Message, IReadOnlyList<RuntimeFileUseAction> Actions);
