namespace DeltaNFD.Services;

/// <summary>
/// 后端运行环境定位（共享执行库）：解析程序根目录与 Assets 目录。
/// 官方后端部署在 &lt;程序根&gt;\Backends\&lt;模块&gt;\ 下，其后端 EXE 的 BaseDirectory 不是程序根，
/// 需要上溯到含 Assets 的目录；从程序根直接运行（旧路径/测试）时行为不变。
/// </summary>
public static class BackendEnvironment
{
    private static readonly Lazy<string> CachedRoot = new(ResolveAppRoot, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>程序根目录（含 Assets 的目录；找不到时回退为当前 BaseDirectory）。</summary>
    public static string AppRoot => CachedRoot.Value;

    /// <summary>随程序输出的资源目录。</summary>
    public static string AssetsRoot => Path.Combine(AppRoot, "Assets");

    private static string ResolveAppRoot()
    {
        var start = AppContext.BaseDirectory;
        try
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "Assets")))
                {
                    return directory.FullName;
                }
            }
        }
        catch
        {
            // 回退到 BaseDirectory
        }

        return start;
    }
}
