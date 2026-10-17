using DeltaNFD.Backends;

namespace DeltaNFD.Services;

internal interface IAceCoreFileEnvironment
{
    FileAttributes GetAttributes(string path);
    IReadOnlyList<string> EnumerateEntries(string directory);
    void DeleteFile(string path);
}

internal sealed class RealAceCoreFileEnvironment : IAceCoreFileEnvironment
{
    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
    public IReadOnlyList<string> EnumerateEntries(string directory) => Directory.GetFileSystemEntries(directory);
    public void DeleteFile(string path) => File.Delete(path);
}

/// <summary>Fixed-root, file-only cleanup. No service, registry, process termination or delayed-deletion capability.</summary>
internal sealed class AceCoreFileCleanup(IAceCoreFileEnvironment environment, AceCleanupGuard guard)
{
    internal const string Root = @"C:\Program Files\AntiCheatExpert";
    private const int MaximumEntries = 20000;
    private const int MaximumDepth = 32;

    internal static bool IsCoreSysFile(string path) =>
        Path.GetFileName(path).StartsWith("ACE-CORE", StringComparison.OrdinalIgnoreCase)
        && Path.GetExtension(path).Equals(".sys", StringComparison.OrdinalIgnoreCase);

    internal BackendResult Run(IProgress<string>? progress, CancellationToken token)
    {
        var done = new List<BackendResultItem>();
        var verificationPending = false;
        void Check(string stage)
        {
            if (guard.Check(stage, done, token) is { } stopped) throw new Stopped(stopped);
        }
        BackendResult Fail(Exception error)
        {
            var message = $"ACE-CORE SYS 清理未完成，已确认删除 {done.Count} 个文件。\n{error.Message}"
                + "\n未安排重启删除。文件被占用时，请退出游戏和守卫，必要时重启电脑后重新清理。";
            Log.Error(message, error);
            var result = done.Count == 0 ? BackendResult.Failed("ace.core-clean-failed", message)
                : BackendResult.PartialResult("ace.core-clean-partial", message, done.ToArray());
            return result with { PendingRestore = verificationPending };
        }
        try
        {
            Check("core-scan");
            try { ValidateDirectoryChain(Root); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return BackendResult.Ok("ace.core-clean", "未找到 C:\\Program Files\\AntiCheatExpert，无需清理。",
                    OperationResult.Skip("未找到 ACE 安装目录，无需清理。"));
            }
            progress?.Report("正在查找 ACE-CORE SYS 文件…");
            // Resolve every candidate before deleting any file; unreadable or redirected trees cannot become partial scans.
            var files = FindTargets(Check);
            if (files.Count == 0)
                return BackendResult.Ok("ace.core-clean", "未找到 ACE-CORE*.sys 文件，无需清理。",
                    OperationResult.Skip("未找到 ACE-CORE*.sys 文件，无需清理。"));
            foreach (var file in files)
            {
                Check("core-delete");
                ValidateDirectoryChain(Path.GetDirectoryName(file)!);
                var attributes = environment.GetAttributes(file);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || !IsCoreSysFile(file))
                    throw new IOException("目标文件已变为目录或链接，已停止清理：" + file);
                progress?.Report($"仅清理 ACE-CORE SYS [{done.Count + 1}/{files.Count}]：{file}");
                environment.DeleteFile(file);
                verificationPending = true;
                try
                {
                    _ = environment.GetAttributes(file);
                    verificationPending = false;
                    throw new IOException("删除后文件仍存在，未确认成功：" + file);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
                verificationPending = false;
                done.Add(new(file, "ok", "已删除：" + file));
                Log.Info("ACE-CORE SYS 文件已删除：" + file);
            }
            Check("core-postcheck");
            if (FindTargets(Check).Count > 0) throw new IOException("复检仍有 ACE-CORE SYS 文件，可能有其他程序重新生成文件。");
            var message = $"已删除并复检 {done.Count} 个 ACE-CORE SYS 文件。\n请通过游戏启动器执行普通修复，再启动游戏。";
            return BackendResult.Ok("ace.core-clean", message, OperationResult.Ok(message)) with { Items = done.ToArray() };
        }
        catch (Stopped ex) { return ex.Result; }
        catch (Exception ex) { return Fail(ex); }
    }

    private List<string> FindTargets(Action<string> check)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((Root, 0));
        var count = 0;
        while (pending.TryPop(out var directory))
        {
            check("core-scan");
            ValidateDirectoryChain(directory.Path);
            foreach (var entry in environment.EnumerateEntries(directory.Path))
            {
                check("core-scan");
                if (++count > MaximumEntries) throw new IOException("ACE 目录条目超过扫描上限，已停止清理。");
                if (!Path.IsPathFullyQualified(entry)) throw new IOException("枚举返回非绝对路径，已停止清理。");
                var path = Path.GetFullPath(entry);
                if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(Path.GetDirectoryName(path), directory.Path, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("文件路径超出当前 ACE 目录，已停止清理：" + entry);
                var attributes = environment.GetAttributes(path);
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (isDirectory || IsCoreSysFile(path)) throw new IOException("ACE 目录或目标文件包含链接，已停止清理：" + path);
                    continue;
                }
                if (isDirectory)
                {
                    if (directory.Depth >= MaximumDepth) throw new IOException("ACE 目录层级超过扫描上限，已停止清理。");
                    pending.Push((path, directory.Depth + 1));
                }
                else if (IsCoreSysFile(path)) files.Add(path);
            }
        }
        return files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void ValidateDirectoryChain(string directory)
    {
        var chain = new Stack<string>();
        for (string? path = directory; path is not null; path = Path.GetDirectoryName(path)) chain.Push(path);
        while (chain.TryPop(out var path))
        {
            var attributes = environment.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("ACE 路径包含非目录或目录链接，已停止清理：" + path);
        }
    }

    private sealed class Stopped(BackendResult result) : Exception
    {
        internal BackendResult Result { get; } = result;
    }
}
