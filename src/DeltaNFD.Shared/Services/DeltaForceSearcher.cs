using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DeltaNFD.Services;

/// <summary>
/// 三角洲安装目录的全盘/指定范围递归搜索。
/// 只读枚举，命中即通过 <see cref="DeltaForceLocator.TryGetValidGameRoot"/> 校验并规范化；
/// 跳过系统目录、回收站、重解析点与无权限子树，支持取消与进度上报。
/// </summary>
internal static class DeltaForceSearcher
{
    /// <summary>在所有固定盘符递归搜索三角洲安装目录。</summary>
    public static async IAsyncEnumerable<string> SearchAllFixedDrivesAsync(
        IProgress<string>? status = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            cancellationToken.ThrowIfCancellationRequested();
            status?.Report("正在搜索 " + drive.Name);
            await foreach (var hit in SearchDirectoryAsync(drive.Name, status, cancellationToken).ConfigureAwait(false))
            {
                if (found.Add(hit)) yield return hit;
            }
        }
    }

    /// <summary>在指定目录子树内递归搜索三角洲安装目录。</summary>
    public static async IAsyncEnumerable<string> SearchDirectoryAsync(
        string root,
        IProgress<string>? status = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) yield break;

        var stack = new Stack<string>();
        stack.Push(Path.GetFullPath(root.Trim().Trim('"')));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            status?.Report(dir);

            if (ShouldSkip(dir)) continue;

            // 先判命中：命中即上报，不再深入该目录（安装目录内部无需再搜）。
            if (DeltaForceLocator.TryGetValidGameRoot(dir, out var normalized))
            {
                yield return normalized;
                continue;
            }

            string[] subDirs;
            try { subDirs = Directory.GetDirectories(dir); }
            catch { continue; }

            foreach (var sub in subDirs)
            {
                stack.Push(sub);
            }

            // 让出一次，避免长时间占用线程池
            await Task.Yield();
        }
    }

    private static bool ShouldSkip(string path)
    {
        try
        {
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name)) name = path;

            // 跳过系统目录 / 回收站 / 隐藏系统卷信息
            if (name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) &&
                    path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 跳过重解析点（符号链接/挂载点可能指向已扫过的位置）
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReparsePoint) != 0) return true;
        }
        catch { return true; }
        return false;
    }
}
