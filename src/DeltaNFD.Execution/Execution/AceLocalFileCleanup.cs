namespace DeltaNFD.Services;

public sealed record AceLocalDeleteResult(int DeletedFiles, double FreedMb, IReadOnlyList<string> Problems);

/// <summary>单安装 ACE 文件清理；不提权、不结束进程、不登记重启删除。</summary>
internal static class AceLocalFileCleanup
{
    internal static AceLocalDeleteResult Delete(string root, Action checkpoint)
    {
        var deleted = 0;
        long bytes = 0;
        var problems = new List<string>();
        void CheckPath(string path)
        {
            checkpoint();
            for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("目录包含链接/重解析点，已停止：" + current);
        }
        void Visit(string directory)
        {
            CheckPath(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                CheckPath(entry);
                if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0) Visit(entry);
                else
                {
                    try
                    {
                        var length = new FileInfo(entry).Length;
                        CheckPath(entry);
                        File.Delete(entry);
                        deleted++;
                        bytes += length;
                    }
                    catch (UnauthorizedAccessException ex) { problems.Add(entry + "：" + ex.Message); }
                    catch (IOException ex) { problems.Add(entry + "：" + ex.Message); }
                }
            }
            CheckPath(directory);
            Directory.Delete(directory, recursive: false);
        }
        try { Visit(root); }
        catch (Exception ex) { problems.Add(root + "：" + ex.Message); }
        return new(deleted, bytes / 1024d / 1024d, problems);
    }
}
