using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace DeltaNFD.Services.Plugins;

/// <summary>导入结果；失败时 Error 面向用户，不包含内部路径细节。</summary>
public sealed record PluginImportResult(bool Succeeded, PluginIndexEntry? Entry, bool ReplacedExisting, string Error)
{
    public static PluginImportResult Fail(string error) => new(false, null, false, error);
}

/// <summary>
/// `.dnfdplugin`（非加密 ZIP）无执行导入器（规范第 2 节）。
/// 只解析、验证、解包：不运行入口、依赖、安装器或脚本。
/// 流式实施实际展开限额（压缩 ≤100MiB、解压总量 ≤300MiB、≤2048 项、单文件 ≤100MiB），
/// 不只信 ZIP 元数据。先暂存、验证，再同卷目录改名及原子索引提交；失败清理本轮暂存，
/// 不破坏旧版。同 ID 不同哈希属于替换，替换前须由调用方停用旧版（此处校验索引状态）。
/// </summary>
public sealed class PluginPackageImporter
{
    public Func<string, string>? ReplacementBlocker { get; set; }
    public const long MaxCompressedBytes = 100L * 1024 * 1024;
    public const long MaxUncompressedBytes = 300L * 1024 * 1024;
    public const int MaxEntryCount = 2048;
    public const long MaxSingleFileBytes = 100L * 1024 * 1024;

    private const string ManifestEntry = "manifest.json";
    private const string UiEntry = "ui.json";
    private const string ReadmeEntry = "README.md";
    private const string LicenseEntry = "LICENSE.txt";

    private readonly PluginPackageIndex _index;

    public PluginPackageImporter(PluginPackageIndex index)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
    }

    /// <summary>
    /// 从 .dnfdplugin 文件导入。root 为插件根（%LOCALAPPDATA%\Delta NFD\Plugins），
    /// dataRoot 为插件数据根（...\PluginData）。全程在暂存目录进行，成功才提交。
    /// </summary>
    public PluginImportResult Import(string packagePath, string pluginsRoot)
    {
        var indexRoot = Path.GetDirectoryName(_index.PathName)!;
        using var diagnostics = PluginDiagnostics.UseDirectory(Path.Combine(Path.GetDirectoryName(indexRoot) ?? indexRoot, "PluginData", "diagnostics"));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var trace = Guid.NewGuid().ToString("D");
        PluginDiagnostics.Record("import", "", requestId: trace);
        var result = ImportCore(packagePath, pluginsRoot);
        PluginDiagnostics.Record("import", result.Succeeded ? "ok" : "blocked", detail: "导入结束；不记录包路径、清单文本或程序内容",
            pluginId: result.Entry?.Id ?? "", pluginVersion: result.Entry?.Version ?? "", requestId: trace,
            elapsedMs: watch.ElapsedMilliseconds, errorCode: result.Succeeded ? "OK" : "INVALID_PACKAGE");
        return result;
    }

    private PluginImportResult ImportCore(string packagePath, string pluginsRoot)
    {
        if (!Path.IsPathFullyQualified(packagePath)) return PluginImportResult.Fail("插件包路径必须是绝对路径。");
        if (!Path.IsPathFullyQualified(pluginsRoot)) return PluginImportResult.Fail("插件安装根路径必须是绝对路径。");
        try
        {
            if ((File.GetAttributes(packagePath) & FileAttributes.ReparsePoint) != 0)
                return PluginImportResult.Fail("插件包不允许是重解析点。");
        }
        catch (Exception ex)
        {
            return PluginImportResult.Fail("插件包不可读：" + ex.Message);
        }
        if (!Directory.Exists(pluginsRoot)) return PluginImportResult.Fail("插件安装根不存在。");

        var stagingRoot = Path.Combine(pluginsRoot, $".staging-{Guid.NewGuid():N}");
        try
        {
            PluginIndexEntry entry;
            bool replaced;
            try
            {
                Directory.CreateDirectory(stagingRoot);
                // 父目录不能含重解析点（规范：检查暂存和目标所有父目录）。
                for (var directory = new DirectoryInfo(stagingRoot); directory is not null; directory = directory.Parent)
                    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                        return PluginImportResult.Fail("插件安装路径包含重解析点，拒绝导入。");

                var (manifest, ui, files, packageHash, packageSize) = ExtractToStaging(packagePath, stagingRoot);
                if (!_index.TryGet(manifest.Id, out var existing, out var indexError))
                    return PluginImportResult.Fail(indexError);
                replaced = existing is not null;
                if (existing is { } old)
                {
                    var blocker = ReplacementBlocker?.Invoke(manifest.Id);
                    if (!string.IsNullOrEmpty(blocker)) return PluginImportResult.Fail(blocker);
                    if (old.State == PluginPackageState.PendingRestore)
                        return PluginImportResult.Fail(
                            $"插件“{manifest.Id}”存在待恢复/未知运行记录，替换被阻止。请先完成恢复或人工处理后再导入。");
                    if (old.PackageSha256 == packageHash && old.Version == manifest.Version)
                        return PluginImportResult.Fail(
                            $"插件“{manifest.Id}” v{manifest.Version} 已导入（相同包哈希），无需重复导入。");
                    // 相同 ID/版本但不同哈希也属于替换，不能沿用旧授权（新条目默认 ImportedDisabled）。
                }

                var installDirectory = Path.Combine(pluginsRoot, manifest.Id, manifest.Version);
                var entryExecutable = Path.Combine(installDirectory, manifest.Backend.Entry.Replace('/', Path.DirectorySeparatorChar));
                if (!PluginPath.IsWithinRoot(pluginsRoot, installDirectory) ||
                    !PluginPath.IsWithinRoot(pluginsRoot, entryExecutable))
                    return PluginImportResult.Fail("安装路径越界，拒绝导入。");
                if (!File.Exists(Path.Combine(stagingRoot, manifest.Backend.Entry.Replace('/', Path.DirectorySeparatorChar))))
                    return PluginImportResult.Fail($"清单入口 {manifest.Backend.Entry} 不在包内。");

                entry = new PluginIndexEntry
                {
                    Id = manifest.Id,
                    Version = manifest.Version,
                    PackageSha256 = packageHash,
                    PackageSizeBytes = packageSize,
                    InstallDirectory = installDirectory,
                    EntryExecutable = entryExecutable,
                    Name = manifest.Name,
                    Author = manifest.Author,
                    Permissions = manifest.Permissions
                        .Select(p => new PluginIndexPermission(p.Id, p.Purpose)).ToList(),
                    State = PluginPackageState.ImportedDisabled,
                    Files = files,
                    ImportedUtc = DateTimeOffset.UtcNow,
                };
                Commit(stagingRoot, installDirectory, entry);
            }
            finally
            {
                try { if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true); }
                catch { /* 暂存清理失败不影响结果；下次导入不会复用该随机目录 */ }
            }
            return new PluginImportResult(true, entry, replaced, "");
        }
        catch (PluginContractException ex)
        {
            return PluginImportResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            return PluginImportResult.Fail("导入插件包失败：" + ex.Message);
        }
    }

    /// <summary>流式解包到暂存目录：边解边执行限额与逐条目校验，产出解析后的 manifest/ui、文件校验表与包哈希。</summary>
    private (PluginManifest Manifest, PluginUi Ui, IReadOnlyList<PluginIndexFile> Files, string PackageSha256, long PackageSize)
        ExtractToStaging(string packagePath, string stagingRoot)
    {
        long packageSize;
        string packageHash;
        using (var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            packageSize = stream.Length;
            if (packageSize == 0) throw new PluginContractException("插件包为空。");
            if (packageSize > MaxCompressedBytes)
                throw new PluginContractException($"插件包超过 {MaxCompressedBytes / (1024 * 1024)} MiB 上限。");
            using var sha = SHA256.Create();
            packageHash = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
            stream.Position = 0;

            var unzippedTotal = 0L;
            var fileRecords = new List<PluginIndexFile>();
            byte[]? manifestBytes = null, uiBytes = null;
            string? readmeEntry = null, licenseEntry = null;
            var entryCount = 0;
            var foldedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count > MaxEntryCount)
                throw new PluginContractException($"插件包条目数超过 {MaxEntryCount}。");
            foreach (var archiveEntry in archive.Entries)
            {
                entryCount++;
                if (entryCount > MaxEntryCount)
                    throw new PluginContractException($"插件包条目数超过 {MaxEntryCount}。");
                var name = PluginPath.ValidateZipEntryName(archiveEntry.FullName);
                // ZIP 目录条目（以 / 结尾的空目录）只记录不计文件；显式目录条目不允许属性异常。
                var isDirectoryEntry = archiveEntry.FullName.EndsWith('/');
                if (isDirectoryEntry && archiveEntry.Length != 0)
                    throw new PluginContractException($"ZIP 目录条目不为空：{name}");
                if (!foldedNames.Add(name.ToLowerInvariant()))
                    throw new PluginContractException($"插件包包含折叠后重复的路径：{name}");

                if (archiveEntry.Length > MaxSingleFileBytes)
                    throw new PluginContractException($"文件“{name}”超过 {MaxSingleFileBytes / (1024 * 1024)} MiB 上限。");
                // 解压总量限额按实际展开字节数累计，不信任已校验过的元数据以外的声明。
                var projected = unzippedTotal + archiveEntry.Length;
                if (projected > MaxUncompressedBytes)
                    throw new PluginContractException($"插件包解压总量超过 {MaxUncompressedBytes / (1024 * 1024)} MiB 上限。");

                var destination = Path.Combine(stagingRoot, name.Replace('/', Path.DirectorySeparatorChar));
                if (!PluginPath.IsWithinRoot(stagingRoot, destination))
                    throw new PluginContractException($"条目路径越界：{name}");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                long written;
                string hash;
                using (var entryStream = archiveEntry.Open())
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
                using (var entrySha = SHA256.Create())
                {
                    // CopyTo 前先做实际读取限额：按块复制并计数，超限立即失败。
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = entryStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total += read;
                        if (total > MaxSingleFileBytes)
                            throw new PluginContractException($"文件“{name}”实际展开超过 {MaxSingleFileBytes / (1024 * 1024)} MiB 上限。");
                        if (unzippedTotal + total > MaxUncompressedBytes)
                            throw new PluginContractException($"插件包解压总量超过 {MaxUncompressedBytes / (1024 * 1024)} MiB 上限。");
                        entrySha.TransformBlock(buffer, 0, read, buffer, 0);
                        output.Write(buffer, 0, read);
                    }
                    entrySha.TransformFinalBlock([], 0, 0);
                    hash = Convert.ToHexString(entrySha.Hash!).ToLowerInvariant();
                    written = total;
                }
                // 展开尺寸与 ZIP 元数据不一致（压缩炸弹特征）时拒绝。
                if (written != archiveEntry.Length)
                    throw new PluginContractException($"文件“{name}”实际展开大小与 ZIP 元数据不一致。");
                unzippedTotal += written;

                if (isDirectoryEntry) continue;
                switch (name)
                {
                    case ManifestEntry when manifestBytes is null:
                        manifestBytes = File.ReadAllBytes(destination); break;
                    case UiEntry when uiBytes is null:
                        uiBytes = File.ReadAllBytes(destination); break;
                    case ReadmeEntry when readmeEntry is null:
                        readmeEntry = name; break;
                    case LicenseEntry when licenseEntry is null:
                        licenseEntry = name; break;
                }
                fileRecords.Add(new PluginIndexFile(name, hash, written));
            }

            if (manifestBytes is null) throw new PluginContractException($"插件包缺少 {ManifestEntry}。");
            if (uiBytes is null) throw new PluginContractException($"插件包缺少 {UiEntry}。");
            if (readmeEntry is null) throw new PluginContractException($"插件包缺少 {ReadmeEntry}。");
            if (licenseEntry is null) throw new PluginContractException($"插件包缺少 {LicenseEntry}。");
            // manifest/ui 已在 1MiB 限额内读出（PluginJson 上限），此处解析并交叉校验。
            var manifest = PluginManifestParser.Parse(manifestBytes);
            var ui = PluginUiParser.Parse(uiBytes, manifest);
            _ = ui;
            return (manifest, ui, fileRecords, packageHash, packageSize);
        }
    }

    /// <summary>同卷目录改名 + 原子索引提交；旧版目录在成功提交后由调用方决定删除（保留旧版直到成功提交）。</summary>
    private void Commit(string stagingRoot, string installDirectory, PluginIndexEntry entry)
    {
        var parent = Path.GetDirectoryName(installDirectory)!;
        Directory.CreateDirectory(parent);
        var replacedExistingDirectory = Directory.Exists(installDirectory);
        var backupDirectory = Path.Combine(parent, $".old-{Guid.NewGuid():N}");
        var committed = false;
        try
        {
            if (replacedExistingDirectory)
            {
                // 升级回滚：先把旧目录移走，索引提交失败时移回。
                Directory.Move(installDirectory, backupDirectory);
            }
            Directory.Move(stagingRoot, installDirectory);
            if (!_index.TryUpsert(entry, out var error))
            {
                // 回滚目录改名，不破坏旧版。
                Directory.Delete(installDirectory, recursive: true);
                if (replacedExistingDirectory) Directory.Move(backupDirectory, installDirectory);
                throw new PluginContractException("写入插件索引失败：" + error);
            }
            committed = true;
        }
        catch
        {
            if (!Directory.Exists(installDirectory) && Directory.Exists(backupDirectory))
                Directory.Move(backupDirectory, installDirectory);
            throw;
        }
        finally
        {
            // 索引提交成功后清理旧目录；失败时上面已移回。
            // 版本目录按 id\<version> 布局：升级成功后同 ID 的其他版本目录一并清理（保留最新）。
            try
            {
                if (committed && Directory.Exists(backupDirectory)) Directory.Delete(backupDirectory, recursive: true);
                var idDirectory = Path.GetDirectoryName(installDirectory);
                if (committed && Directory.Exists(idDirectory))
                    foreach (var sibling in Directory.EnumerateDirectories(idDirectory))
                    {
                        if (string.Equals(sibling, installDirectory, StringComparison.OrdinalIgnoreCase)) continue;
                        if (Path.GetFileName(sibling).StartsWith(".old-", StringComparison.Ordinal) ||
                            Path.GetFileName(sibling).StartsWith(".staging-", StringComparison.Ordinal)) continue;
                        Directory.Delete(sibling, recursive: true);
                    }
            }
            catch { /* 旧版残留不阻塞导入结果；.old-/.staging- 前缀便于人工清理 */ }
        }
    }
}
