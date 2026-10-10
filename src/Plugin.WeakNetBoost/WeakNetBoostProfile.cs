using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// 「弱网加速」插件的本体逻辑：加速配置的生成，以及可恢复的原子改写。
//
// 本文件刻意做成**纯 BCL、无环境依赖**的纯函数式实现（目标路径与数据目录都由调用方传入）：
//  - 插件后端 Program.cs 传入真实的 %APPDATA% 路径与宿主下发的插件数据目录；
//  - tools/BackendSmokeTest 链接编译同一份源码，用临时目录驱动真实逻辑，并把生成的配置
//    交给宿主真实的 UpdateService 解析器校验（见 PluginWeakNetBoostChecks）。
// 任何一方改动这里的常量或结构，两边的检查都会同时失效，避免测试与实现各写一份而漂移。
//
// 不引用宿主任何程序集；不读写宿主安装目录。

namespace Plugin.WeakNetBoost;

public static class WeakNetBoostProfile
{
    public const string PluginId = "io.github.drxiaoxi.weaknet-boost";

    /// <summary>主加速前缀（gh-proxy）。</summary>
    public const string AcceleratorPrefix = "https://gh-proxy.com/";

    /// <summary>备用加速前缀：主节点不可达时宿主会退到这一条。</summary>
    public const string AcceleratorPrefixAlternate = "https://gh-proxy.org/";

    public const string RawManifestUrl = "https://raw.githubusercontent.com/DRXiaoXi/DeltaNFD/main/update.json";
    public const string ReleasesApiUrl = "https://api.github.com/repos/DRXiaoXi/DeltaNFD/releases?per_page=100";
    public const string ReleasePageUrl = "https://github.com/DRXiaoXi/DeltaNFD/releases";

    /// <summary>用户级更新源所在目录名（与宿主 AppDataPaths.DirectoryName 一致）。</summary>
    public const string AppDataFolderName = "Delta NFD";

    /// <summary>用户级更新源文件名（与宿主 UpdateService 的用户配置文件名一致）。</summary>
    public const string UpdateSourceFileName = "update-source.json";

    public const string BackupIdPrefix = "update-source-";

    internal const string BackupFileName = "update-source.backup.bin";
    internal const string AbsentMarkerFileName = "original-absent.marker";
    internal const string StateFileName = "weaknet-boost.state.json";

    /// <summary>加速配置必须保留的 GitHub 直连主机：加速节点整体不可用时仍能退回直连。</summary>
    public static readonly IReadOnlyList<string> DirectHosts =
    [
        "github.com", "api.github.com", "raw.githubusercontent.com", "githubusercontent.com",
        "objects.githubusercontent.com", "release-assets.githubusercontent.com",
    ];

    /// <summary>加速配置声明的允许主机（加速节点 + 直连兜底）。</summary>
    public static IReadOnlyList<string> AllowedHosts =>
    [
        "gh-proxy.com", "gh-proxy.org", .. DirectHosts,
    ];

    /// <summary>
    /// 加速清单读取顺序。宿主按顺序尝试、遇到“取不到”才继续下一条，因此：
    /// 优先任何可用加速节点，最后才退到直连（弱网环境下直连往往超时但不会立刻失败）。
    /// </summary>
    public static IReadOnlyList<string> ManifestUrls =>
    [
        AcceleratorPrefix + RawManifestUrl,           // 加速静态清单：镜像条目排在首位时下载也走加速
        AcceleratorPrefix + ReleasesApiUrl,           // 加速实时 API：静态清单缺失时仍能发现新版本
        AcceleratorPrefixAlternate + RawManifestUrl,   // 备用加速节点
        ReleasesApiUrl,                                // 直连兜底：加速节点整体不可用
    ];

    /// <summary>解析用户级更新源路径。<paramref name="appDataRoot"/> 即 %APPDATA%。</summary>
    public static string ConfigPathFor(string appDataRoot) =>
        Path.Combine(appDataRoot, AppDataFolderName, UpdateSourceFileName);

    public static string BackupPathFor(string dataDirectory) => Path.Combine(dataDirectory, BackupFileName);
    public static string AbsentMarkerPathFor(string dataDirectory) => Path.Combine(dataDirectory, AbsentMarkerFileName);
    public static string StatePathFor(string dataDirectory) => Path.Combine(dataDirectory, StateFileName);

    /// <summary>是否留下了可还原的证据：原件备份，或“开启前该文件不存在”标记。</summary>
    public static bool HasEvidence(string dataDirectory) =>
        File.Exists(BackupPathFor(dataDirectory)) || File.Exists(AbsentMarkerPathFor(dataDirectory));

    /// <summary>内容是否由本插件写入（识别脚本注释里的插件标识）。</summary>
    public static bool IsWrittenByPlugin(byte[]? content) =>
        content is not null && Utf8(content).Contains(PluginId, StringComparison.Ordinal);

    public static bool IsAcceleratedUrl(string url) =>
        url.StartsWith(AcceleratorPrefix, StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith(AcceleratorPrefixAlternate, StringComparison.OrdinalIgnoreCase);

    /// <summary>把加速 URL 还原成“gh-proxy → 内层主机”的可读形式，便于界面显示。</summary>
    public static string DescribeUrlHost(string url)
    {
        if (IsAcceleratedUrl(url))
        {
            var inner = url[(url.IndexOf("//", StringComparison.Ordinal) + 2)..];
            var slash = inner.IndexOf('/');
            var innerUrl = slash >= 0 ? inner[(slash + 1)..] : inner;
            return "gh-proxy → " + (Uri.TryCreate(innerUrl, UriKind.Absolute, out var parsed) ? parsed.Host : innerUrl);
        }
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
    }

    /// <summary>
    /// 生成加速更新源配置。带 // 注释（宿主的配置读取显式 Skip 注释）并写入插件标识，
    /// 用于判断“这个文件是本插件写的”。
    /// </summary>
    public static string ComposeAcceleratedConfig()
    {
        var builder = new StringBuilder();
        builder.Append("// 本文件由「弱网加速」拓展插件（").Append(PluginId).Append("）改写。\r\n");
        builder.Append("// 取消插件页的「启用弱网加速」或在插件管理页点「恢复」，即可还原为改写前的原样。\r\n");
        builder.Append("// 改写前原件已整份备份到：\r\n");
        builder.Append("//   %LOCALAPPDATA%\\Delta NFD\\PluginData\\").Append(PluginId).Append("\\\r\n");
        builder.Append("{\r\n");
        builder.Append("  \"schemaVersion\": 1,\r\n");
        builder.Append("  \"manifestUrls\": [\r\n");
        builder.Append(string.Join(",\r\n", ManifestUrls.Select(u => "    \"" + u + "\""))).Append("\r\n");
        builder.Append("  ],\r\n");
        builder.Append("  \"allowedHosts\": [\r\n");
        builder.Append(string.Join(",\r\n", AllowedHosts.Select(h => "    \"" + h + "\""))).Append("\r\n");
        builder.Append("  ],\r\n");
        builder.Append("  \"autoCheckThrottleHours\": 6,\r\n");
        builder.Append("  \"checkTimeoutSeconds\": 20,\r\n");
        builder.Append("  \"downloadTimeoutMinutes\": 60,\r\n");
        builder.Append("  \"releasePageUrl\": \"").Append(ReleasePageUrl).Append("\"\r\n");
        builder.Append("}\r\n");
        return builder.ToString();
    }

    /// <summary>同一插件内唯一（含历史记录）的恢复标识。</summary>
    public static string NewBackupId() =>
        BackupIdPrefix + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" + Guid.NewGuid().ToString("N")[..8];

    public enum ChangeKind
    {
        /// <summary>本次写入了加速配置。</summary>
        Applied,
        /// <summary>已处于加速状态，未重复修改。</summary>
        AlreadyApplied,
        /// <summary>本次按备份还原完成。</summary>
        Restored,
        /// <summary>本就没有本插件的改动，无需还原。</summary>
        AlreadyRestored,
        /// <summary>目标已被第三方改动，拒绝覆盖。</summary>
        Conflict,
        /// <summary>找不到原值证据，不猜原值。</summary>
        MissingEvidence,
        /// <summary>目标路径不安全（重解析点等）。</summary>
        UnsafeTarget,
        /// <summary>读写失败，结果未知。</summary>
        Failed,
    }

    public sealed record ChangeResult(ChangeKind Kind, string Message, string BackupId)
    {
        public bool Ok => Kind is ChangeKind.Applied or ChangeKind.AlreadyApplied
            or ChangeKind.Restored or ChangeKind.AlreadyRestored;
    }

    /// <summary>
    /// 开启弱网加速：先留证据（原件备份或“原值不存在”标记），再原子改写目标配置。
    /// 证据在改动之前落盘，因此中途崩溃最多留下未被使用的备份，不会破坏旧备份。
    /// </summary>
    public static ChangeResult Apply(string configPath, string dataDirectory, string backupId)
    {
        if (HasReparsePointInPath(configPath))
            return new(ChangeKind.UnsafeTarget,
                "更新源配置路径或其父目录是重解析点（符号链接/目录联接），拒绝改写以避免写到别处：" + configPath,
                backupId);

        var state = LoadState(dataDirectory) ?? new PluginState();
        var current = ReadOrNull(configPath, out var readError);
        if (readError is not null)
            return new(ChangeKind.Failed, "无法读取更新源配置：" + readError, backupId);

        if (state.Applied || (IsWrittenByPlugin(current) && HasEvidence(dataDirectory)))
            return new(ChangeKind.AlreadyApplied, "弱网加速已处于开启状态，本次未重复修改。", backupId);

        try
        {
            Directory.CreateDirectory(dataDirectory);
            if (current is not null)
            {
                File.WriteAllBytes(BackupPathFor(dataDirectory), current);
                DeleteIfExists(AbsentMarkerPathFor(dataDirectory));
                state.HadOriginal = true;
                state.OriginalSha256 = Sha256(current);
            }
            else
            {
                // 原值不存在：用标记文件把“原本没有这个文件”与“备份丢失”区分开，还原时才敢删除。
                DeleteIfExists(BackupPathFor(dataDirectory));
                File.WriteAllText(AbsentMarkerPathFor(dataDirectory), PluginId, new UTF8Encoding(false));
                state.HadOriginal = false;
                state.OriginalSha256 = "";
            }

            var bytes = Encoding.UTF8.GetBytes(ComposeAcceleratedConfig());
            WriteAtomic(configPath, bytes);

            state.Applied = true;
            state.LastWrittenSha256 = Sha256(bytes);
            state.LastBackupId = backupId;
            state.ConfigPath = configPath;
            state.LastChangeUtc = DateTimeOffset.UtcNow.ToString("O");
            state.LastAction = "enable";
            SaveState(dataDirectory, state);

            var scope = current is null
                ? "改写前该文件不存在，还原时会删除它。"
                : "原文件已整份备份，关闭开关可还原。";
            return new(ChangeKind.Applied,
                "已开启弱网加速：更新检查与安装包下载改走 gh-proxy 加速节点。" + scope, backupId);
        }
        catch (Exception ex)
        {
            return new(ChangeKind.Failed, "写入加速配置失败，结果未知：" + ex.Message, backupId);
        }
    }

    /// <summary>
    /// 关闭弱网加速 / 响应宿主 restore：幂等还原；第三方已改动时报告冲突而不覆盖。
    /// <paramref name="backupId"/> 仅在用户通过插件开关关闭时传入，用于记录本次改动的恢复标识。
    /// </summary>
    public static ChangeResult Restore(string configPath, string dataDirectory, string? backupId = null)
    {
        var state = LoadState(dataDirectory) ?? new PluginState();
        var current = ReadOrNull(configPath, out var readError);
        if (readError is not null)
            return new(ChangeKind.Failed, "无法读取更新源配置：" + readError, state.LastBackupId);

        var markedByUs = IsWrittenByPlugin(current);
        if (!state.Applied && !(markedByUs && HasEvidence(dataDirectory)))
            return new(ChangeKind.AlreadyRestored, "当前不是本插件写入的加速配置，未做修改。", state.LastBackupId);

        if (current is not null && state.LastWrittenSha256.Length > 0 && Sha256(current) != state.LastWrittenSha256)
            return new(ChangeKind.Conflict,
                "更新源配置已被其他程序修改，拒绝覆盖；请人工确认，或用插件数据目录里的备份手动覆盖回去。",
                state.LastBackupId);

        if (HasReparsePointInPath(configPath))
            return new(ChangeKind.UnsafeTarget,
                "更新源配置路径或其父目录是重解析点，拒绝写入：" + configPath, state.LastBackupId);

        var backupExists = File.Exists(BackupPathFor(dataDirectory));
        var absentRecorded = File.Exists(AbsentMarkerPathFor(dataDirectory));

        try
        {
            if (backupExists)
            {
                var original = File.ReadAllBytes(BackupPathFor(dataDirectory));
                if (state.OriginalSha256.Length > 0 && Sha256(original) != state.OriginalSha256)
                    return new(ChangeKind.Failed, "备份文件哈希与记录不符，拒绝还原，证据保留。", state.LastBackupId);
                WriteAtomic(configPath, original);
            }
            else if (absentRecorded)
            {
                DeleteIfExists(configPath);
            }
            else
            {
                // 不猜原值：保留证据与待恢复标记，交给人工处理。
                return new(ChangeKind.MissingEvidence,
                    "找不到原值备份，无法确认还原成什么；记录保留，请人工处理（插件数据目录：" + dataDirectory + "）。",
                    state.LastBackupId);
            }

            state.Applied = false;
            state.LastWrittenSha256 = "";
            state.LastChangeUtc = DateTimeOffset.UtcNow.ToString("O");
            state.LastAction = "restore";
            if (!string.IsNullOrEmpty(backupId)) state.LastBackupId = backupId;
            SaveState(dataDirectory, state);
            return new(ChangeKind.Restored, "已关闭弱网加速，更新源已按其开启前原样还原。", state.LastBackupId);
        }
        catch (Exception ex)
        {
            return new(ChangeKind.Failed, "还原更新源配置失败，结果未知：" + ex.Message, state.LastBackupId);
        }
    }

    /// <summary>进度/状态描述用的当前状态简要文本。</summary>
    public static string DescribeState(string configPath, string dataDirectory)
    {
        var state = LoadState(dataDirectory) ?? new PluginState();
        var applied = state.Applied ||
            (IsWrittenByPlugin(ReadOrNull(configPath, out _)) && HasEvidence(dataDirectory));
        if (!applied) return "弱网加速状态：未开启；无待处理的后端改动。";
        var backupNote = File.Exists(BackupPathFor(dataDirectory)) ? "原文件备份存在" : "开启前该文件不存在";
        return $"弱网加速状态：已开启；{backupNote}；最近 backupId {state.LastBackupId}。";
    }

    public static bool IsApplied(string configPath, string dataDirectory)
    {
        var state = LoadState(dataDirectory) ?? new PluginState();
        return state.Applied || (IsWrittenByPlugin(ReadOrNull(configPath, out _)) && HasEvidence(dataDirectory));
    }

    // ---------------------------------------------------------------- 状态与文件辅助

    public sealed class PluginState
    {
        public int SchemaVersion { get; set; } = 1;
        public bool Applied { get; set; }
        public bool HadOriginal { get; set; }
        public string OriginalSha256 { get; set; } = "";
        public string LastWrittenSha256 { get; set; } = "";
        public string LastBackupId { get; set; } = "";
        public string ConfigPath { get; set; } = "";
        public string LastChangeUtc { get; set; } = "";
        public string LastAction { get; set; } = "";
    }

    public static PluginState? LoadState(string dataDirectory)
    {
        try
        {
            var path = StatePathFor(dataDirectory);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<PluginState>(File.ReadAllText(path, Encoding.UTF8));
        }
        catch
        {
            // 状态损坏按“未开启”处理：还原始终以证据文件（备份/标记）为准。
            return null;
        }
    }

    public static void SaveState(string dataDirectory, PluginState state)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(StatePathFor(dataDirectory), JsonSerializer.Serialize(state), new UTF8Encoding(false));
        }
        catch
        {
            // 状态只是辅助证据：写不进去不影响备份/还原，由宿主侧记录继续兜底。
        }
    }

    /// <summary>原子写入：同目录临时文件 + File.Move(overwrite)，避免宿主读到半截配置。</summary>
    public static void WriteAtomic(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory)) throw new InvalidOperationException("目标路径没有父目录：" + path);
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            DeleteIfExists(temp);
        }
    }

    public static void DeleteIfExists(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 清理失败不掩盖主流程结论 */ }
    }

    /// <summary>目标文件或任一已存在的父目录是重解析点时为 true（避免写穿符号链接/目录联接）。</summary>
    public static bool HasReparsePointInPath(string path)
    {
        try
        {
            for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                if (!File.Exists(current) && !Directory.Exists(current)) continue;
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            }
        }
        catch { return true; } // 无法确认时按不安全处理
        return false;
    }

    private static byte[]? ReadOrNull(string path, out string? error)
    {
        error = null;
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static string Utf8(byte[] bytes) => new UTF8Encoding(false, false).GetString(bytes);
}
