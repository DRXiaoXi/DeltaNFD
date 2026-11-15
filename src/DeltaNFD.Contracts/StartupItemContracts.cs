namespace DeltaNFD.Services;

public enum StartupItemScope { UserRun, MachineRun, MachineRun32, UserFolder, CommonFolder }
public enum StartupItemAction { Disable, Restore, KeepCurrent }
public sealed record StartupItemChangeArgs(string Id, string Fingerprint, StartupItemAction Action);
public sealed record StartupItemInfo(string Id, string Name, string Source, string Command, string State,
    string Detail, string Fingerprint, bool HasBackup, bool CanDisable, bool CanRestore, bool CanKeepCurrent);
public sealed record StartupItemReport(IReadOnlyList<StartupItemInfo> Items, IReadOnlyList<string> Errors)
{
    public bool Complete => Errors.Count == 0;
    public int BackupCount => Items.Count(i => i.HasBackup);
}
