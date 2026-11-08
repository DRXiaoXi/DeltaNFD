using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DeltaNFD.Services;

internal sealed record GameVersionObservation(string GameRoot, string LauncherVersion, string LauncherBuild, string ResourceVersion)
{
    [JsonIgnore]
    public string DisplayVersion => string.Join("；", new[]
    {
        LauncherVersion.Length == 0 ? "" : $"启动器 {LauncherVersion}（构建 {LauncherBuild}）",
        ResourceVersion.Length == 0 ? "" : $"资源 {ResourceVersion}",
    }.Where(s => s.Length > 0));
    internal bool LosesSource(GameVersionObservation previous) =>
        previous.LauncherVersion.Length > 0 && LauncherVersion.Length == 0;
    internal bool ChangedFrom(GameVersionObservation previous) =>
        previous.LauncherVersion.Length > 0 && LauncherVersion.Length > 0 &&
            (LauncherVersion != previous.LauncherVersion || LauncherBuild != previous.LauncherBuild);
}

internal sealed class GameVersionEntry
{
    [JsonRequired]
    public GameVersionObservation Current { get; set; } = new("", "", "", "");
    [JsonRequired]
    public string PreviousVersion { get; set; } = "";
    [JsonRequired]
    public bool MaintenancePending { get; set; }
    [JsonRequired]
    public string ChangeToken { get; set; } = "";
    [JsonRequired]
    public DateTimeOffset RecordedUtc { get; set; }
}

internal sealed class GameVersionState
{
    [JsonRequired]
    public int SchemaVersion { get; set; } = 1;
    // 旧记录缺少此字段。只迁移一次，避免以后真实更新/回退又被当成旧资源提醒。
    public int UpdateDetectionPolicy { get; set; }
    [JsonRequired]
    public List<GameVersionEntry> Entries { get; set; } = [];
}

internal sealed record GameVersionCheckResult(IReadOnlyList<GameVersionEntry> Entries, IReadOnlyList<string> Errors);

/// <summary>只读游戏固定版本文件；仅向工具自己的目录保存基线与待维护提醒，不清理缓存或 ACE。</summary>
internal sealed class GameVersionTracker(string statePath)
{
    private readonly object _gate = new();
    private const int MetadataLimit = 64 * 1024;
    private const int StateLimit = 1024 * 1024;
    private static readonly Regex VersionPattern = new(@"\A[0-9]+(?:\.[0-9]+){1,5}\z", RegexOptions.CultureInvariant);

    internal GameVersionCheckResult Refresh(IEnumerable<string> roots)
    {
        lock (_gate)
        {
            var state = Load();
            var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var errors = new List<string>();
            var changed = UpgradeUpdatePolicy(state);
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var payload = ResolvePayload(root);
                    if (!active.Add(payload)) continue;
                    var current = ReadObservation(payload);
                    var entry = state.Entries.SingleOrDefault(e => SameRoot(e.Current.GameRoot, payload));
                    if (entry is null)
                    {
                        state.Entries.Add(new() { Current = current, RecordedUtc = DateTimeOffset.UtcNow });
                        changed = true; // 首次见到此安装只记录基线，不假报版本更新。
                    }
                    else if (current.LosesSource(entry.Current))
                    {
                        errors.Add($"{payload}：先前的版本来源暂不可读，保留原记录和提醒。");
                    }
                    else if (current != entry.Current)
                    {
                        var launcherChanged = current.ChangedFrom(entry.Current);
                        if (launcherChanged)
                        {
                            if (!entry.MaintenancePending) entry.PreviousVersion = entry.Current.DisplayVersion;
                            entry.MaintenancePending = true;
                        }
                        if (entry.MaintenancePending && launcherChanged) entry.ChangeToken = Guid.NewGuid().ToString("N");
                        entry.Current = current;
                        entry.RecordedUtc = DateTimeOffset.UtcNow;
                        changed = true;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException)
                {
                    errors.Add($"{root}：版本未确认（{ex.Message}），未覆盖原记录。");
                }
            }
            if (changed) Save(state);
            return new(state.Entries.Where(e => active.Contains(e.Current.GameRoot)).ToArray(), errors);
        }
    }

    // 用户自行确认已完成维护；不把导航、点击或部分清理当作维护验证成功。
    internal int Acknowledge(IReadOnlyList<(string GameRoot, string ChangeToken)> expected)
    {
        lock (_gate)
        {
            var state = Load();
            var count = 0;
            foreach (var entry in state.Entries.Where(e => e.MaintenancePending))
            {
                if (!expected.Any(x => SameRoot(x.GameRoot, entry.Current.GameRoot) && x.ChangeToken == entry.ChangeToken)) continue;
                // 再读游戏文件，防止用户查看指引期间发生下一次更新而误清掉新提醒。
                try
                {
                    var observed = ReadObservation(entry.Current.GameRoot);
                    if (observed.LosesSource(entry.Current) || observed.ChangedFrom(entry.Current)) continue;
                }
                catch { continue; }
                entry.MaintenancePending = false;
                count++;
            }
            if (count > 0) Save(state);
            return count;
        }
    }

    private static bool UpgradeUpdatePolicy(GameVersionState state)
    {
        if (state.UpdateDetectionPolicy == 1) return false;
        foreach (var entry in state.Entries.Where(e => e.MaintenancePending))
        {
            // 仅撤销旧记录中可明确识别的资源字段变化，不冒称维护已完成。
            var old = Regex.Match(entry.PreviousVersion,
                @"\A启动器 (?<version>[0-9]+(?:\.[0-9]+){1,5})（构建 (?<build>[0-9]+)）(?:；资源 [0-9]+(?:\.[0-9]+){1,5})?\z",
                RegexOptions.CultureInvariant);
            var resourceOnly = old.Success
                ? old.Groups["version"].Value == entry.Current.LauncherVersion && old.Groups["build"].Value == entry.Current.LauncherBuild
                : Regex.IsMatch(entry.PreviousVersion, @"\A资源 [0-9]+(?:\.[0-9]+){1,5}\z", RegexOptions.CultureInvariant);
            if (!resourceOnly) continue;
            entry.MaintenancePending = false;
            entry.PreviousVersion = "";
            entry.ChangeToken = "";
        }
        state.UpdateDetectionPolicy = 1;
        return true;
    }

    internal static string ResolvePayload(string root)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        foreach (var candidate in new[] { full, Path.Combine(full, "DeltaForce") })
            if (Directory.Exists(Path.Combine(candidate, "Content", "Paks")) ||
                File.Exists(Path.Combine(candidate, "Binaries", "Win64", DeltaForceLocator.GameProcessName + ".exe")))
                return candidate;
        throw new IOException("未找到三角洲安装内容。");
    }

    internal static GameVersionObservation ReadObservation(string payload)
    {
        payload = ResolvePayload(payload);
        var install = Path.GetFileName(payload).Equals("DeltaForce", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(payload)! : payload;
        var rail = Path.Combine(install, "rail_files", "rail_game_build_info.dat");
        // 直接布局也允许版本文件与 Binaries/Content 同级。
        if (!File.Exists(rail)) rail = Path.Combine(payload, "rail_files", "rail_game_build_info.dat");
        var resource = Path.Combine(payload, "Saved", "Config", "WindowsClient", "GameResVersion.ini");
        var launcherVersion = "";
        var launcherBuild = "";
        var resourceVersion = "";
        if (File.Exists(rail))
        {
            using var json = JsonDocument.Parse(ReadStableText(rail));
            var versionFields = json.RootElement.EnumerateObject().Where(p => p.Name is "version_name" or "repo_build_id").ToArray();
            if (versionFields.Length != 2 || versionFields.Select(p => p.Name).Distinct().Count() != 2)
                throw new IOException("启动器版本字段缺失或重复。");
            launcherVersion = json.RootElement.GetProperty("version_name").GetString() ?? "";
            launcherBuild = json.RootElement.GetProperty("repo_build_id").GetString() ?? "";
            ValidateVersion(launcherVersion);
            if (launcherBuild.Length is < 1 or > 32 || !launcherBuild.All(char.IsAsciiDigit))
                throw new IOException("启动器构建号无效。");
        }
        try
        {
            if (File.Exists(resource)) resourceVersion = ReadResourceVersion(resource);
        }
        catch (Exception ex) when (launcherVersion.Length > 0 && (ex is IOException or UnauthorizedAccessException))
        {
            // Saved 中的资源配置可在启动/预热时改写；它不是安装更新的独立证据。
            // 启动器版本有效时不让这个可变字段阻塞更新判定。
        }
        if (launcherVersion.Length == 0 && resourceVersion.Length == 0)
            throw new IOException("尚无可识别的启动器/资源版本；不会使用旧 PSOCache 版本作为当前游戏版本。");
        return new(payload, launcherVersion, launcherBuild, resourceVersion);
    }

    private static string ReadResourceVersion(string resource)
    {
        var resourceVersion = "";
        var inSection = false;
        var found = false;
        foreach (var line in ReadStableText(resource).Split('\n'))
        {
            var text = line.Trim();
            if (text.StartsWith('[')) { inSection = text.Equals("[/Script/GameCore.GameResVersion]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inSection || text.StartsWith(';') || text.StartsWith('#')) continue;
            var pair = text.Split('=', 2);
            if (pair.Length != 2 || !pair[0].Trim().Equals("GameResVersionStr", StringComparison.OrdinalIgnoreCase)) continue;
            if (found) throw new IOException("资源版本字段重复。");
            resourceVersion = pair[1].Trim();
            ValidateVersion(resourceVersion);
            found = true;
        }
        if (!found) throw new IOException("资源版本字段缺失。");
        return resourceVersion;
    }

    private static void ValidateVersion(string value)
    {
        if (value.Length > 64 || !VersionPattern.IsMatch(value)) throw new IOException("版本格式无效。");
    }

    private static string ReadStableText(string path)
    {
        var before = new FileInfo(path);
        var length = before.Length;
        var stamp = before.LastWriteTimeUtc;
        if (length is < 1 or > MetadataLimit) throw new IOException("版本文件大小异常。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[MetadataLimit + 1];
        var count = reader.ReadBlock(buffer, 0, buffer.Length);
        var after = new FileInfo(path);
        if (count > MetadataLimit || !after.Exists || length != after.Length || stamp != after.LastWriteTimeUtc)
            throw new IOException("版本文件正在变化，稍后重试。");
        return new string(buffer, 0, count);
    }

    private GameVersionState Load()
    {
        if (!File.Exists(statePath)) return new();
        var info = new FileInfo(statePath);
        if (info.Length is < 1 or > StateLimit) throw new IOException("版本记录大小异常，保留原文件。");
        var state = JsonSerializer.Deserialize<GameVersionState>(File.ReadAllBytes(statePath))
            ?? throw new IOException("版本记录为空。");
        if (state.SchemaVersion != 1 || state.UpdateDetectionPolicy is < 0 or > 1 || state.Entries is null || state.Entries.Count > 256 ||
            state.Entries.Any(e => e is null || e.Current is null || string.IsNullOrWhiteSpace(e.Current.GameRoot) ||
                e.Current.LauncherVersion is null || e.Current.LauncherBuild is null || e.Current.ResourceVersion is null ||
                e.PreviousVersion is null || e.ChangeToken is null || e.MaintenancePending && !Guid.TryParseExact(e.ChangeToken, "N", out _)) ||
            state.Entries.Select(e => e.Current.GameRoot).Distinct(StringComparer.OrdinalIgnoreCase).Count() != state.Entries.Count)
            throw new IOException("版本记录结构无效，保留原文件。");
        foreach (var entry in state.Entries)
        {
            if (!Path.IsPathFullyQualified(entry.Current.GameRoot) || entry.PreviousVersion.Length > 1024)
                throw new IOException("版本记录路径或说明无效，保留原文件。");
            if (entry.Current.LauncherVersion.Length > 0)
            {
                ValidateVersion(entry.Current.LauncherVersion);
                if (entry.Current.LauncherBuild.Length is < 1 or > 32 || !entry.Current.LauncherBuild.All(char.IsAsciiDigit))
                    throw new IOException("已保存的构建号无效，保留原文件。");
            }
            if (entry.Current.ResourceVersion.Length > 0) ValidateVersion(entry.Current.ResourceVersion);
            if (entry.Current.LauncherVersion.Length == 0 && entry.Current.ResourceVersion.Length == 0)
                throw new IOException("已保存的版本为空，保留原文件。");
        }
        return state;
    }

    private void Save(GameVersionState state)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state);
        if (bytes.Length > StateLimit || state.Entries.Count > 256) throw new IOException("版本记录已达到上限，未覆盖原记录。");
        var full = Path.GetFullPath(statePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, full, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool SameRoot(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
}
