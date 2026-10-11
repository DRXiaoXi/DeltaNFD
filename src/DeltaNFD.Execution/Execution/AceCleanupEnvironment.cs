using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DeltaNFD.Services;

/// <summary>
/// ACE 清理的可注入执行环境：进程、服务、注册表、文件四类操作。
/// 生产用 <see cref="RealAceCleanupEnvironment"/> 直连真实系统；模拟验收用内存夹具注入，
/// 从而在不动真实 ACE 的前提下验证生产清理范围与拒绝规则。
/// </summary>
public interface IAceCleanupEnvironment
{
    bool IsElevated { get; }

    /// <summary>ACE 系统安装目录（%ProgramFiles%\AntiCheatExpert）。</summary>
    string AceInstallDirectory { get; }

    /// <summary>游戏目录下的 ACE 目录集合（由游戏定位器给出）。</summary>
    IReadOnlyList<string> GameAceDirectories();

    bool IsProcessRunning(string name);

    /// <summary>枚举同名进程的 PID（用于逐个复核阻止条件后再结束）。</summary>
    IReadOnlyList<int> FindProcesses(string name);

    /// <summary>强制结束指定进程及其子进程树；返回是否成功发起结束。</summary>
    bool TerminateProcessTree(int pid);

    /// <summary>服务注册表键是否存在。</summary>
    bool ServiceKeyExists(string name);

    /// <summary>执行外部命令（sc.exe 等），返回退出码与输出。</summary>
    Task<(int Code, string StdOut, string StdErr)> RunCommandAsync(string fileName, string arguments, TimeSpan timeout);

    bool RegistryKeyExists(RegistryHive hive, string path);

    void DeleteRegistryTree(RegistryHive hive, string path, bool throwOnMissingSubKey);

    IReadOnlyList<string> EnumerateSubKeyNames(RegistryHive hive, string path);
    /// <summary>受限候选位置且安装路径值精确匹配已知 ACE 目录，才确认为本工具可清理的键。</summary>
    bool HasAceRegistryOwnership(RegistryHive hive, string path);
    Task<OperationResult> BackupRegistryKeyAsync(RegistryHive hive, string path);

    bool DirectoryExists(string path);

    /// <summary>true = 目录/文件是重解析点（符号链接/联接）；清理必须拒绝，避免被改道删除他处。</summary>
    bool IsReparsePoint(string path);

    double GetDirectorySizeMb(string path);

    /// <summary>递归删除目录内容；被占用文件注册为重启后删除。返回 (释放 MB, 待重启删除数)。</summary>
    (double FreedMb, int RebootPending) DeleteDirectoryTree(string path);
}

/// <summary>真实系统实现（生产路径）。</summary>
internal sealed class RealAceCleanupEnvironment : IAceCleanupEnvironment
{
    private const int MoveFileDelayUntilReboot = 0x4;

    public bool IsElevated => ElevationHelper.IsElevated;

    public string AceInstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AntiCheatExpert");

    public IReadOnlyList<string> GameAceDirectories() => DeltaForceLocator.FindGameAceDirectories().ToArray();

    public bool IsProcessRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public IReadOnlyList<int> FindProcesses(string name)
    {
        var processes = Process.GetProcessesByName(name);
        try { return processes.Select(p => p.Id).ToArray(); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public bool TerminateProcessTree(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch { return false; }
    }

    public bool ServiceKeyExists(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
        return key is not null;
    }

    public Task<(int Code, string StdOut, string StdErr)> RunCommandAsync(string fileName, string arguments, TimeSpan timeout) =>
        RunCaptureAsync(fileName, arguments, timeout);

    public bool RegistryKeyExists(RegistryHive hive, string path)
    {
        using var key = Open(hive, path);
        return key is not null;
    }

    public void DeleteRegistryTree(RegistryHive hive, string path, bool throwOnMissingSubKey)
    {
        if (AceRegistryPolicy.IsSharedPath(path) && !HasAceRegistryOwnership(hive, path))
            throw new InvalidOperationException("共享注册表键归属未确认，已拒绝删除。");
        using var key = Open(hive);
        key?.DeleteSubKeyTree(path, throwOnMissingSubKey);
    }

    public IReadOnlyList<string> EnumerateSubKeyNames(RegistryHive hive, string path)
    {
        using var key = Open(hive, path);
        return key?.GetSubKeyNames() ?? [];
    }

    public bool HasAceRegistryOwnership(RegistryHive hive, string path)
    {
        if (!AceRegistryPolicy.IsCandidatePath(path)) return false;
        using var key = Open(hive, path);
        if (key is null) return false;
        var roots = new[] { AceInstallDirectory }.Concat(GameAceDirectories()).ToArray();
        foreach (var name in new[] { "InstallLocation", "InstallPath", "InstallDir", "BasePath" })
            if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value
                && AceRegistryPolicy.MatchesInstallRoot(value, roots)) return true;
        return false;
    }

    public Task<OperationResult> BackupRegistryKeyAsync(RegistryHive hive, string path)
    {
        if (!Environment.Is64BitProcess || !HasAceRegistryOwnership(hive, path))
            return Task.FromResult(OperationResult.Fail("无法确认 64 位注册表视图或 ACE 归属，保留原键。"));
        return AceRegistryBackups.ExportAsync(Path.Combine(AppDataPaths.Root, "ace-registry-backup"), hive, path, RunCommandAsync);
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }

    public double GetDirectorySizeMb(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;
            return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(file => { try { return file.Length; } catch { return 0; } }) / 1024.0 / 1024.0;
        }
        catch { return 0; }
    }

    public (double FreedMb, int RebootPending) DeleteDirectoryTree(string path)
    {
        var rebootPending = 0;
        double freedMb = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var length = new FileInfo(file).Length;
                    File.Delete(file);
                    freedMb += length / 1024.0 / 1024.0;
                }
                catch
                {
                    if (MoveFileEx(file, null, MoveFileDelayUntilReboot)) rebootPending++;
                }
            }

            foreach (var dir in Directory.EnumerateDirectories(path).OrderByDescending(d => d.Length))
            {
                try { Directory.Delete(dir, recursive: true); }
                catch { /* 内含重启后删除的文件，目录保留 */ }
            }

            try { Directory.Delete(path, recursive: true); }
            catch { /* 目录内还有被锁定文件则保留目录，重启后可再清 */ }
        }
        catch { /* 目录访问失败整体跳过 */ }

        return (freedMb, rebootPending);
    }

    private static RegistryKey? Open(RegistryHive hive, string? subKey = null)
    {
        var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        if (subKey is null) return baseKey;
        using (baseKey) return baseKey.OpenSubKey(subKey);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);

    private static async Task<(int Code, string StdOut, string StdErr)> RunCaptureAsync(
        string fileName, string arguments, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null) return (-1, "", $"无法启动 {fileName}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch { }
            return (-1, "", "执行超时");
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
