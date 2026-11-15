using System.Security.Cryptography;
using System.Text;

namespace DeltaNFD.Services;

/// <summary>
/// 每个异步操作独立的、已验证的目录快照。IPC 两端均重新核对完整清单，
/// 只把定位器限制到已知安装，不把客户端提供的任意路径作为清理根。
/// </summary>
public sealed class GameInstallationScope : IDisposable
{
    private static readonly AsyncLocal<GameInstallationScope?> Slot = new();
    private readonly GameInstallationScope? _previous;
    private readonly string[] _roots;
    private readonly string[] _inventory;
    private readonly GameInstallationSelection _selection;
    private bool _disposed;

    private GameInstallationScope(GameInstallationSelection selection, string[] roots, string[] inventory)
    {
        _previous = Slot.Value;
        _selection = selection;
        _roots = roots;
        _inventory = inventory;
        Slot.Value = this;
    }

    public static GameInstallationSelection? CurrentSelection => Slot.Value is { } scope ? scope.CopySelection() : null;
    internal static IReadOnlyList<string>? CurrentRoots => Slot.Value?._roots;
    internal static IReadOnlyList<string>? CurrentInventory => Slot.Value?._inventory;
    public static bool AllowsSharedCleanup => Slot.Value is null ||
        (Slot.Value._selection.AllInstallations && Slot.Value._selection.SharedCleanupConfirmed);

    // Always bypass the ambient scope when validating: an old subset cannot become the new inventory.
    public static string[] ReadInventory() => NormalizeInventory(DeltaForceLocator.FindAllRoots());

    public static string[] NormalizeInventory(IEnumerable<string> roots) => roots
        .Select(NormalizeRoot).Distinct(StringComparer.OrdinalIgnoreCase)
        .GroupBy(PayloadKey, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.OrderBy(p => p.Length).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).First())
        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();

    private static string NormalizeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\"))
            throw new InvalidOperationException("安装目录必须是已识别的本地绝对路径。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    internal static string PayloadKey(string root)
    {
        root = NormalizeRoot(root);
        var nested = Path.Combine(root, "DeltaForce");
        return File.Exists(Path.Combine(nested, "Binaries", "Win64", DeltaForceLocator.GameProcessName + ".exe")) ||
            Directory.Exists(Path.Combine(nested, "Content", "Paks")) ? nested : root;
    }

    public static string InventoryId(IEnumerable<string> roots) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join("\n", NormalizeInventory(roots).Select(p => p.ToUpperInvariant())))));

    public static GameInstallationSelection Select(string[] inventory, string? root, bool all, bool sharedConfirmed = false) => new()
    {
        InventoryId = InventoryId(inventory),
        Roots = all ? inventory.ToArray() : root is null ? [] : [root],
        AllInstallations = all,
        SharedCleanupConfirmed = sharedConfirmed,
    };

    public static GameInstallationScope Enter(GameInstallationSelection? selection, bool requireChoice = false,
        IEnumerable<string>? inventory = null)
    {
        var known = inventory is null ? ReadInventory() : NormalizeInventory(inventory);
        if (selection is null)
        {
            if (requireChoice && known.Length > 1)
                throw new InvalidOperationException("识别到多个三角洲安装目录，请先选择一个目录或全部目录，再执行操作。");
            selection = Select(known, null, all: true, sharedConfirmed: true);
        }
        if (selection.Roots is null || selection.Roots.Length > 64 ||
            !string.Equals(selection.InventoryId, InventoryId(known), StringComparison.Ordinal))
            throw new InvalidOperationException("安装目录清单已变化，请重新选择操作目录。");
        var requested = selection.Roots.Select(NormalizeRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (requested.Length != selection.Roots.Length || requested.Any(p => !known.Contains(p, StringComparer.OrdinalIgnoreCase)) ||
            (selection.AllInstallations ? requested.Length != known.Length : requested.Length != 1) ||
            (!selection.AllInstallations && selection.SharedCleanupConfirmed))
            throw new InvalidOperationException("操作目录选择无效，未执行操作。请重新选择一个目录或全部目录。");
        var copy = new GameInstallationSelection { InventoryId = selection.InventoryId, Roots = requested,
            AllInstallations = selection.AllInstallations, SharedCleanupConfirmed = selection.SharedCleanupConfirmed };
        return new(copy, requested, known);
    }

    public static async Task<T> RunAsync<T>(GameInstallationSelection? selection, Func<Task<T>> action, bool requireChoice = false)
    {
        using var scope = Enter(selection, requireChoice);
        return await action();
    }

    private GameInstallationSelection CopySelection() => new() { InventoryId = _selection.InventoryId,
        Roots = _roots.ToArray(), AllInstallations = _selection.AllInstallations,
        SharedCleanupConfirmed = _selection.SharedCleanupConfirmed };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Slot.Value = _previous;
    }
}
