namespace DeltaNFD.Services;

public sealed class GameRecordingItem
{
    public string InstallationRoot { get; init; } = "";
    public string ComponentId { get; init; } = "";
    public string Title { get; init; } = "";
    public string FilePath { get; init; } = "";
    public string State { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public bool CanApply { get; init; }
    public bool CanRestore { get; init; }
    public bool CanArchiveRecord { get; init; }
    public bool HasRecord { get; init; }
    public bool IsManaged { get; init; }
    public bool NeedsReview { get; init; }
}

public sealed class GameRecordingSnapshot
{
    public List<GameRecordingItem> Items { get; init; } = [];
    public string Message { get; init; } = "";
    public bool Complete { get; init; } = true;
    public int UnmatchedRecords { get; init; }
    public int RecordCount { get; init; }
    public bool RecoveryReadFailed { get; init; }
}

public sealed class GameRecordingArgs
{
    public string InstallationRoot { get; init; } = "";
    public string ComponentId { get; init; } = "";
    public string ExpectedFingerprint { get; init; } = "";
    public bool Restore { get; init; }
    public bool ArchiveRecord { get; init; }
}

public interface IGameRecordingService
{
    Task<GameRecordingSnapshot> GetStatusAsync();
    Task<OperationResult> ChangeAsync(GameRecordingArgs args);
}
