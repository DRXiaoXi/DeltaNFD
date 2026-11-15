namespace DeltaNFD.Services;

/// <summary>一次操作的安装目录选择；不改变玩家登记目录或默认游戏。</summary>
public sealed class GameInstallationSelection
{
    public string InventoryId { get; init; } = "";
    public string[] Roots { get; init; } = [];
    public bool AllInstallations { get; init; }
    public bool SharedCleanupConfirmed { get; init; }
}

public sealed class GameInstallationArgs
{
    public GameInstallationSelection? Selection { get; init; }
}

public static class GameInstallationProtocol
{
    public const int Version = 1;

    public static bool IsSupported(System.Text.Json.JsonElement args, int backendVersion) =>
        args.ValueKind != System.Text.Json.JsonValueKind.Object ||
        !args.TryGetProperty("selection", out var selection) || selection.ValueKind == System.Text.Json.JsonValueKind.Null ||
        backendVersion == Version;
}
