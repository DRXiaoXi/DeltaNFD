using System.Reflection;
using System.Text;

namespace DeltaNFD.Services;

/// <summary>
/// 全局文件日志（%APPDATA%\Delta NFD\logs\，按每次启动一个文件）。
/// 用途：在其他电脑上出问题时凭日志定位——所有关键系统操作的入口/结果/异常都会落盘。
/// 设计约束：日志本身绝不能把程序写崩——全部 IO 包 try/catch；线程安全（全局锁）。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _currentPath;
    private static readonly bool Enabled = true;

    /// <summary>日志目录（%APPDATA%\Delta NFD\logs）。</summary>
    public static string LogDirectory => Path.Combine(AppDataPaths.Root, "logs");

    /// <summary>当前日志文件完整路径（本会话）。</summary>
    public static string CurrentPath
    {
        get
        {
            EnsureFile();
            return _currentPath!;
        }
    }

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : message + Environment.NewLine + exception);

    /// <summary>会话启动信息：应用版本、系统环境、管理员状态等，写在本会话日志头部。</summary>
    public static void Startup()
    {
        EnsureFile();
        var elevated = "未知";
        try
        {
            elevated = ElevationHelper.IsElevated ? "是" : "否";
        }
        catch
        {
            // 忽略
        }

        var version = "未知";
        try
        {
            version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "未知";
        }
        catch
        {
            // 忽略
        }

        Write("INFO ",
            "=== 三角帧不掉洲 启动 ===" + Environment.NewLine +
            $"  应用版本：{version} x64" + Environment.NewLine +
            $"  系统：{Environment.OSVersion.VersionString}（Win32NT）" + Environment.NewLine +
            $"  .NET：{Environment.Version}" + Environment.NewLine +
            $"  机器：{Environment.MachineName} · 用户：{Environment.UserName} · 管理员：{elevated}" + Environment.NewLine +
            $"  逻辑处理器：{Environment.ProcessorCount} · 系统目录：{Environment.SystemDirectory}");
    }

    private static void EnsureFile()
    {
        if (_currentPath is not null || !Enabled)
        {
            return;
        }

        lock (Gate)
        {
            if (_currentPath is not null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(LogDirectory);
                _currentPath = Path.Combine(LogDirectory,
                    $"app_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
                CleanupOldLogs();
            }
            catch
            {
                _currentPath = null; // 目录创建失败时停用日志
            }
        }
    }

    /// <summary>清理 14 天前的旧日志（启动时执行一次，失败无影响）。</summary>
    private static void CleanupOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-14);
            foreach (var file in Directory.GetFiles(LogDirectory, "app_*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch
        {
            // 清理失败不影响运行
        }
    }

    private static void Write(string level, string message)
    {
        if (!Enabled)
        {
            return;
        }

        EnsureFile();
        var path = _currentPath;
        if (path is null)
        {
            return;
        }

        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
        }
        catch
        {
            // 日志失败绝不能影响主程序
        }
    }

    /// <summary>在资源管理器中打开日志目录（设置页按钮用；目录不存在时先创建）。</summary>
    public static void OpenFolderInExplorer()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = LogDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Error("打开日志文件夹失败", ex);
        }
    }
}
