using DeltaNFD.Services.Plugins;

namespace DeltaNFD.Services;

/// <summary>
/// 插件管理服务（规范第 2、3、5 节的管理层）：列表、授权（绑定 ID+版本+包哈希）、
/// 启停（第 6 项持续运行未实现前仅状态标记）、配置存储（按插件 ID，宿主保存）与卸载。
/// 本层不启动任何插件后端进程；授权前必须由 UI 展示 PluginTrust 确认清单。
/// </summary>
public sealed class PluginManagerService
{
    private static readonly Lazy<PluginManagerService> DefaultService =
        new(() => new PluginManagerService(DefaultPluginsRoot), LazyThreadSafetyMode.ExecutionAndPublication);
    public static PluginManagerService Default => DefaultService.Value;

    public static string DefaultPluginsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppDataPaths.DirectoryName, "Plugins");

    private readonly string _pluginsRoot;
    public string DataRoot { get; }
    public Func<string, bool>? RuntimeIsBusy { get; set; }
    public PluginPackageIndex Index { get; }
    public PluginPackageImporter Importer { get; }
    public PluginBackupStore Backups { get; }
    public PluginHandoffStore Handoffs { get; }
    public PluginAuthorizationStore Authorizations { get; }
    public PluginRunStore Runs { get; }

    public PluginManagerService(string pluginsRoot)
    {
        if (!Path.IsPathFullyQualified(pluginsRoot)) throw new ArgumentException("插件根必须是绝对路径。", nameof(pluginsRoot));
        _pluginsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pluginsRoot));
        DataRoot = Path.Combine(Path.GetDirectoryName(_pluginsRoot)!, "PluginData");
        Index = new PluginPackageIndex(Path.Combine(pluginsRoot, "index.json"));
        Importer = new PluginPackageImporter(Index);
        Backups = new PluginBackupStore(Path.Combine(DataRoot, "backups", "backups.json"));
        Handoffs = new PluginHandoffStore(Path.Combine(DataRoot, "offline-handoff.json"));
        Runs = new PluginRunStore(Path.Combine(DataRoot, "managed-runs.json"));
        Authorizations = _pluginsRoot.Equals(Path.GetFullPath(DefaultPluginsRoot), StringComparison.OrdinalIgnoreCase)
            ? PluginAuthorizationStore.ForCurrentUser() : new PluginAuthorizationStore(Path.Combine(DataRoot, "authorizations.json"));
        Importer.ReplacementBlocker = MutationBlocker;
    }

    public string PluginsRoot => _pluginsRoot;

    public bool TryList(out IReadOnlyList<PluginIndexEntry> entries, out string error) => Index.TryRead(out entries, out error);

    /// <summary>
    /// 授权插件（首次启用/替换后重新确认）。mustConfirmations 为 UI 已逐项展示并确认的
    /// PluginTrust.AuthorizationChecklist 文本；条目数或内容与当前清单不符时拒绝，
    /// 防止"没看清单就授权"。授权绑定当前索引条目（ID+版本+哈希）。
    /// </summary>
public bool TryAuthorize(string pluginId, IReadOnlyList<string> mustConfirmations, out PluginIndexEntry? authorized, out string error) =>
        TraceChange("authorize", pluginId, () => { var ok = TryAuthorizeCore(pluginId, mustConfirmations, out var entry, out var failure); return (ok, entry, failure); }, out authorized, out error);

    private bool TryAuthorizeCore(string pluginId, IReadOnlyList<string> mustConfirmations, out PluginIndexEntry? authorized, out string error)
    {
        authorized = null;
        if (!Index.TryGet(pluginId, out var entry, out error)) return false;
        if (entry is null) { error = $"插件“{pluginId}”未导入。"; return false; }
        if (entry.State == PluginPackageState.PendingRestore)
        { error = "该插件存在待恢复/未知运行记录，先完成恢复处理再授权。"; return false; }

        var checklist = PluginTrust.AuthorizationChecklist(entry, entry.PackageSha256, signed: false);
        if (mustConfirmations is null || mustConfirmations.Count != checklist.Count)
        { error = "授权确认不完整：必须逐项确认全部授权提示。"; return false; }
        for (var i = 0; i < checklist.Count; i++)
            if (!string.Equals(mustConfirmations[i], checklist[i], StringComparison.Ordinal))
            { error = "授权确认内容与当前插件信息不符，请重新查看清单后确认。"; return false; }

        var next = entry with { State = PluginPackageState.Authorized, AuthorizedUtc = DateTimeOffset.UtcNow };
        try
        {
            using var package = PluginPackageIntegrity.VerifyAndLock(entry);
            if (!Authorizations.Authorize(entry, package.FileTableDigest, offline: false, out error)) return false;
        }
        catch (Exception ex) { error = "插件文件校验失败，未授权：" + ex.Message; return false; }
        if (!Index.TryUpsert(next, out error)) return false;
        authorized = next;
        return true;
    }

    /// <summary>
    /// 启停授权插件（第 4 项后端启动未实现前，只做授权状态标记：
    /// 启用 = Authorized；停用 = ImportedDisabled 且清除授权时间）。
    /// </summary>
public bool TrySetEnabled(string pluginId, bool enabled, out PluginIndexEntry? updated, out string error) =>
        TraceChange(enabled ? "enable" : "revoke", pluginId, () => { var ok = TrySetEnabledCore(pluginId, enabled, out var entry, out var failure); return (ok, entry, failure); }, out updated, out error);

    private bool TrySetEnabledCore(string pluginId, bool enabled, out PluginIndexEntry? updated, out string error)
    {
        updated = null;
        if (!Index.TryGet(pluginId, out var entry, out error)) return false;
        if (entry is null) { error = $"插件“{pluginId}”未导入。"; return false; }
        if (entry.State == PluginPackageState.PendingRestore)
        { error = "该插件存在待恢复/未知运行记录，先完成恢复处理。"; return false; }
        if (!enabled)
        {
            var blocker = MutationBlocker(pluginId);
            if (blocker.Length > 0) { error = blocker; return false; }
            // 停用即撤回授权；再次启用必须重新走确认（规范第 5 节）。
            if (!Authorizations.Revoke(pluginId, out error)) return false;
            var off = entry with { State = PluginPackageState.ImportedDisabled, AuthorizedUtc = null, OfflineAuthorizedUtc = null };
            if (!Index.TryUpsert(off, out error)) return false;
            updated = off;
            return true;
        }
        if (entry.State == PluginPackageState.Authorized) { updated = entry; return true; }
        error = "启用插件前必须先完成授权确认。";
        return false;
    }

    /// <summary>读取插件配置（宿主按插件 ID 保存，PluginData 下独立文件）。</summary>
    public bool TryReadConfig(string pluginId, out string json, out string error)
    {
        json = "";
        if (!TryValidateId(pluginId, out error)) return false;
        var path = ConfigPath(pluginId);
        try
        {
            if (!File.Exists(path)) { json = "{}"; return true; }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > PluginContract.MaxJsonBytes) { error = "插件配置过大。"; return false; }
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            json = reader.ReadToEnd();
            return true;
        }
        catch (Exception ex) { error = "读取插件配置失败：" + ex.Message; return false; }
    }

    /// <summary>保存插件配置；运行及修改配置不隐式执行操作（规范第 4 节），此处只写文件。</summary>
    public bool TryWriteConfig(string pluginId, string json, out string error)
    {
        if (!TryValidateId(pluginId, out error)) return false;
        var bytes = System.Text.Encoding.UTF8.GetBytes(json ?? "");
        if (bytes.Length > PluginContract.MaxJsonBytes) { error = "插件配置超过 1MiB 上限。"; return false; }
        try
        {
            var path = ConfigPath(pluginId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + $".{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, json ?? "");
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) { error = "保存插件配置失败：" + ex.Message; return false; }
    }

    /// <summary>
    /// 卸载：只允许未在运行且无待恢复记录的插件；删除安装目录与配置并移除索引。
    /// 第 4 项后端启动实现前不存在宿主启动的插件进程；此处仍校验索引状态以防外部改动。
    /// </summary>
public bool TryUninstall(string pluginId, out string error) =>
        TraceChange("uninstall", pluginId, () => { var ok = TryUninstallCore(pluginId, out var failure); return (ok, null, failure); }, out _, out error);

    private bool TryUninstallCore(string pluginId, out string error)
    {
        if (!TryValidateId(pluginId, out error)) return false;
        if (!Index.TryGet(pluginId, out var entry, out error)) return false;
        if (entry is null) { error = $"插件“{pluginId}”未导入。"; return false; }
        if (entry.State == PluginPackageState.PendingRestore)
        { error = "该插件存在待恢复/未知运行记录，卸载被阻止：先完成恢复或人工处理。"; return false; }
        var blocker = MutationBlocker(pluginId);
        if (blocker.Length > 0) { error = blocker; return false; }

        // 先移除索引（此后任何入口都看不到该插件），再删目录；目录删除失败保留索引前状态回滚。
        if (!Authorizations.Revoke(pluginId, out error)) return false;
        if (!Index.TryRemove(pluginId, out error)) return false;
        try
        {
            if (Directory.Exists(entry.InstallDirectory))
                Directory.Delete(entry.InstallDirectory, recursive: true);
            var config = ConfigPath(pluginId);
            if (File.Exists(config)) File.Delete(config);
            return true;
        }
        catch (Exception ex)
        {
            // 目录删除失败：恢复索引条目，宁留残留也不留"索引已删但文件还在"的不一致。
            Index.TryUpsert(entry, out _);
            error = "删除插件文件失败（已恢复索引，请重试或人工删除）：" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 脱机自主运行的逐插件授权（规范第 8 节）：绑定当前 ID+版本+包哈希；
    /// 要求条目已授权、manifest 声明 offlineAutonomous、且总开关已开（由调用方核对）。
    /// 授权说明须原样传 PluginTrust.OfflineAuthorizationNotice 生成文本，防止未读确认。
    /// </summary>
public bool TryAuthorizeOffline(string pluginId, string mustAcknowledgeNotice, out PluginIndexEntry? updated, out string error) =>
        TraceChange("authorize-offline", pluginId, () => { var ok = TryAuthorizeOfflineCore(pluginId, mustAcknowledgeNotice, out var entry, out var failure); return (ok, entry, failure); }, out updated, out error);

    private bool TryAuthorizeOfflineCore(string pluginId, string mustAcknowledgeNotice, out PluginIndexEntry? updated, out string error)
    {
        updated = null;
        if (!Index.TryGet(pluginId, out var entry, out error)) return false;
        if (entry is null) { error = $"插件“{pluginId}”未导入。"; return false; }
        if (entry.State == PluginPackageState.PendingRestore)
        { error = "该插件存在待恢复/未知运行记录，先完成恢复处理。"; return false; }
        if (entry.State != PluginPackageState.Authorized)
        { error = "先完成普通授权并启用插件，才能授权脱机自主运行。"; return false; }
        var manifestPath = Path.Combine(entry.InstallDirectory, "manifest.json");
        PluginManifest? manifest = null;
        try { manifest = File.Exists(manifestPath) ? PluginManifestParser.Parse(File.ReadAllBytes(manifestPath)) : null; }
        catch (PluginContractException) { manifest = null; }
        if (manifest is null) { error = "读取插件 manifest 失败。"; return false; }
        if (!manifest.Capabilities.OfflineAutonomous)
        { error = $"插件“{entry.Name}”未声明 offlineAutonomous，禁止移交。"; return false; }
        if (!string.Equals(mustAcknowledgeNotice, PluginTrust.OfflineAuthorizationNotice(manifest), StringComparison.Ordinal))
        { error = "脱机授权确认内容不匹配，请重新查看说明后确认。"; return false; }

        var next = entry with { OfflineAuthorizedUtc = DateTimeOffset.UtcNow };
        try
        {
            using var package = PluginPackageIntegrity.VerifyAndLock(entry);
            if (!Authorizations.Matches(entry, package.FileTableDigest, requireOffline: false, out error) ||
                !Authorizations.Authorize(entry, package.FileTableDigest, offline: true, out error)) return false;
        }
        catch (Exception ex) { error = "脱机授权校验失败：" + ex.Message; return false; }
        if (!Index.TryUpsert(next, out error)) return false;
        updated = next;
        return true;
    }

    /// <summary>撤销某插件的脱机授权（不影响普通授权）。</summary>
public bool TryRevokeOffline(string pluginId, out string error) =>
        TraceChange("revoke-offline", pluginId, () => { var ok = TryRevokeOfflineCore(pluginId, out var failure); return (ok, null, failure); }, out _, out error);

    private bool TryRevokeOfflineCore(string pluginId, out string error)
    {
        if (!Index.TryGet(pluginId, out var entry, out error)) return false;
        if (entry is null) { error = $"插件“{pluginId}”未导入。"; return false; }
        if (entry.OfflineAuthorizedUtc is null) return true;
        try
        {
            using var package = PluginPackageIntegrity.VerifyAndLock(entry);
            if (!Authorizations.Matches(entry, package.FileTableDigest, false, out error) ||
                !Authorizations.Authorize(entry, package.FileTableDigest, false, out error)) return false;
        }
        catch (Exception ex) { error = "撤销脱机授权失败：" + ex.Message; return false; }
        return Index.TryUpsert(entry with { OfflineAuthorizedUtc = null }, out error);
    }

    /// <summary>汇总：所有满足移交条件的插件（总开+已授权+声明 offlineAutonomous+单独授权）。</summary>
    public List<PluginIndexEntry> ListOfflineHandoffCandidates(bool allowPluginsInOfflineMode, out string error)
    {
        if (!Index.TryRead(out var entries, out error)) return [];
        if (!allowPluginsInOfflineMode) return [];
        return entries.Where(e => e.State == PluginPackageState.Authorized && e.OfflineAuthorizedUtc is not null)
            .ToList();
    }

    /// <summary>
    /// 冲突检测（规范第 7 节）：对声明资源与已启用功能/插件重叠的变更请求拒绝并提示。
    /// builtinOwners：宿主内置功能当前占用的资源（帧格锁核=cpu.affinity 等，由调用方按实际启用状态提供）。
    /// 返回空串表示无冲突；否则为面向用户的提示。
    /// </summary>
    public string FindResourceConflict(string pluginId, IEnumerable<string> resourceIds,
        IReadOnlyDictionary<string, string> builtinOwners)
    {
        if (!Index.TryRead(out var entries, out _)) return "";
        var wanted = resourceIds.Where(r => PluginContract.OperationResourceIds.Contains(r)).ToList();
        if (wanted.Count == 0) return "";
        var wantedSet = new HashSet<string>(wanted, StringComparer.Ordinal);

        foreach (var (resource, owner) in builtinOwners)
            if (wantedSet.Contains(resource))
                return $"资源“{resource}”正被内置功能（{owner}）使用；请先停用并还原该功能，再运行此插件操作。";

        foreach (var other in entries)
        {
            if (other.Id == pluginId || other.State != PluginPackageState.Authorized) continue;
            // 读取其他已授权插件 manifest 的修改操作资源声明。
            var manifestPath = Path.Combine(other.InstallDirectory, "manifest.json");
            try
            {
                if (!File.Exists(manifestPath)) continue;
                var manifest = PluginManifestParser.Parse(File.ReadAllBytes(manifestPath));
                var overlapping = manifest.Operations
                    .Where(o => o.Mutating)
                    .SelectMany(o => o.ResourceIds)
                    .Where(wantedSet.Contains)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (overlapping.Count > 0)
                    return $"资源“{string.Join("、", overlapping)}”已由插件“{other.Name}”（{other.Id}）声明；不可同时使用，请先停用并恢复其一。";
            }
            catch (PluginContractException) { /* 坏 manifest 的插件不在冲突判定范围（它本身跑不起来） */ }
        }
        return "";
    }

    /// <summary>读取插件已保存的声明式 UI 定义（ui.json）供宿主渲染。</summary>
    public bool TryReadUi(string pluginId, out string uiJson, out string error)
    {
        uiJson = "";
        if (!Index.TryGet(pluginId, out var entry, out error) || entry is null)
        { error = $"插件“{pluginId}”未导入。"; return false; }
        var path = Path.Combine(entry.InstallDirectory, "ui.json");
        try
        {
            if (!File.Exists(path)) { error = "插件包内缺少 ui.json。"; return false; }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > PluginContract.MaxJsonBytes) { error = "ui.json 过大。"; return false; }
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            uiJson = reader.ReadToEnd();
            return true;
        }
        catch (Exception ex) { error = "读取 ui.json 失败：" + ex.Message; return false; }
    }

    private string ConfigPath(string pluginId) => Path.Combine(DataRoot, pluginId, "config.json");

    private bool TraceChange(string phase, string pluginId, Func<(bool Success, PluginIndexEntry? Entry, string Error)> change,
        out PluginIndexEntry? updated, out string error)
    {
        using var diagnostics = PluginDiagnostics.UseDirectory(Path.Combine(DataRoot, "diagnostics"));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var trace = Guid.NewGuid().ToString("D");
        PluginDiagnostics.Record(phase, "", pluginId: pluginId, requestId: trace);
        try
        {
            var result = change();
            updated = result.Entry; error = result.Error;
            PluginDiagnostics.Record(phase, result.Success ? "ok" : "blocked", detail: "管理请求结束；原始参数和错误文本不写入日志",
                pluginId: pluginId, pluginVersion: updated?.Version ?? "", requestId: trace, elapsedMs: watch.ElapsedMilliseconds);
            return result.Success;
        }
        catch (Exception ex)
        {
            PluginDiagnostics.Record(phase, "failed", pluginId: pluginId, requestId: trace,
                elapsedMs: watch.ElapsedMilliseconds, exceptionType: ex.GetType().Name);
            throw;
        }
    }

    private string MutationBlocker(string id)
    {
        if (!Runs.TryList(out var runs, out var runError)) return runError;
        if (runs.Any(r => r.PluginId == id)) return "插件存在未确认的运行记录，请先处理失联后端。";
        if (RuntimeIsBusy?.Invoke(id) == true) return "插件仍有运行或握手，请先停止并确认退出。";
        if (!Backups.TryListPending(id, out var pending, out var error)) return error;
        if (pending.Count > 0) return "插件存在待恢复/结果未知的记录，请先恢复。";
        if (!Handoffs.TryList(out var handed, out error)) return error;
        if (handed.Any(e => e.PluginId == id)) return "插件有自主运行或未知移交记录，请先重连处理。";
        return "";
    }

    public bool TryConfirmRestored(string id, out string error)
    {
        if (!Index.TryGet(id, out var entry, out error) || entry is null) return false;
        if (entry.State != PluginPackageState.PendingRestore) return true;
        if (!Backups.TryList(out var all, out error)) return false;
        var own = all.Where(b => b.PluginId == id).ToList();
        if (own.Count == 0 || own.Any(b => b.Status != PluginBackupStatus.Restored || b.IsInvocationIntent))
        { error = "没有完整的已恢复证据，不能清除未知状态。"; return false; }
        return Index.TryUpsert(entry with { State = entry.AuthorizedUtc.HasValue ? PluginPackageState.Authorized : PluginPackageState.ImportedDisabled }, out error);
    }

    private static bool TryValidateId(string pluginId, out string error)
    {
        // 仅阻挡路径注入（ID 已由索引校验，这里防直接调用时被塞路径分隔符）。
        if (string.IsNullOrEmpty(pluginId) || pluginId.Contains('/') || pluginId.Contains('\\') ||
            pluginId.Contains("..") || pluginId.Any(c => c is ':' or '<' or '>' or '"' or '|' or '?' or '*'))
        { error = "插件 ID 非法。"; return false; }
        error = "";
        return true;
    }
}
