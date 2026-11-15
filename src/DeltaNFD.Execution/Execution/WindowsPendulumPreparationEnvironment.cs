using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using System.Text;
using System.Globalization;
using DeltaNFD.Inspection;
using Microsoft.Win32;

namespace DeltaNFD.Services;

internal sealed class WindowsPendulumPreparationEnvironment(string applicationRoot) : IPendulumPreparationEnvironment, IDisposable
{
    private readonly List<FileStream> _locks = [];
    private string PackageDirectory => Path.Combine(Path.GetFullPath(applicationRoot), "Drivers", "PendulumTimer");
    public string Sid => WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("无法确认当前用户。");
    public string BootId => WindowsBootIdentity.Read() ?? "";
    public string? Blocker()
    {
        if (!PendulumGlobalInspection.ExperimentalWindows10 || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
            return "自动准备仅适用于 Windows 10 1809+ x64。";
        if (!ElevationHelper.IsElevated) return ElevationHelper.NotElevatedMessage;
        if (OfflineModeGuard.BlocksNormalAutomation) return "恢复流程中，暂不能准备驱动。";
        try { return DeltaForceLocator.IsGameRunning() ? "请先退出三角洲，再准备驱动和测试签名环境。" : null; }
        catch { return "无法确认游戏已退出，未准备驱动。"; }
    }
    public void VerifyPackage()
    {
        NoLinks(PackageDirectory);
        foreach (var item in new[] { ("DeltaNFDPendulumTimerV1.sys", PendulumPreparationPackage.DriverSha),
            ("DeltaNFD-Pendulum-Test.cer", PendulumPreparationPackage.CertificateSha), ("Setup-Driver.ps1", PendulumPreparationPackage.SetupSha),
            ("driver-package.json", PendulumPreparationPackage.ManifestSha) })
        {
            var path = Path.Combine(PackageDirectory, item.Item1);
            NoLinks(path);
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _locks.Add(file);
            if (file.Length > 1048576 || !Convert.ToHexString(SHA256.HashData(file)).Equals(item.Item2, StringComparison.OrdinalIgnoreCase))
                throw new IOException("随包文件核对失败，未信任或执行：" + item.Item1);
        }
        using var certificate = Certificate();
        if (certificate.HasPrivateKey || !certificate.Thumbprint.Equals(PendulumPreparationPackage.CertificateThumbprint, StringComparison.OrdinalIgnoreCase))
            throw new IOException("随包测试公钥身份不匹配。");
    }
    private static void NoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("准备路径不能经过重解析点。");
    }
    private X509Certificate2 Certificate() => new(File.ReadAllBytes(Path.Combine(PackageDirectory, "DeltaNFD-Pendulum-Test.cer")));
    public bool TestModeActive() => PendulumWin10DriverInspection.ReadTestSigning();
    public bool TrustPresent(bool publisher)
    {
        using var store = new X509Store(publisher ? StoreName.TrustedPublisher : StoreName.Root, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        return store.Certificates.Cast<X509Certificate2>().Any(c => c.GetCertHashString(HashAlgorithmName.SHA256).Equals(PendulumPreparationPackage.CertificateSha, StringComparison.OrdinalIgnoreCase));
    }
    public void AddTrust(bool publisher)
    {
        using var certificate = Certificate();
        using var store = new X509Store(publisher ? StoreName.TrustedPublisher : StoreName.Root, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly);
        store.Add(certificate);
    }
    public PendulumWin10DriverStatus Driver() => PendulumWin10DriverInspection.Read();
    public PendulumPreparationRecord? Read() => PendulumPreparationInspection.Read();
    private static void EnsureDirectory()
    {
        var path = PendulumPreparationInspection.DirectoryPath;
        NoLinks(path);
        if (!Directory.Exists(path))
        {
            var dir = Directory.CreateDirectory(path);
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            acl.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
            foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" })
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"), FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            dir.SetAccessControl(acl);
        }
        PendulumPreparationInspection.VerifyProtectedDirectory();
    }
    public void Save(PendulumPreparationRecord record)
    {
        PendulumPreparationInspection.Validate(record);
        if (record.Sid != Sid) throw new IOException("准备记录用户不匹配。");
        EnsureDirectory();
        var path = PendulumPreparationInspection.RecordPath;
        NoLinks(path);
        var temp = Path.Combine(PendulumPreparationInspection.DirectoryPath, "state-" + Guid.NewGuid().ToString("N") + ".tmp");
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { JsonSerializer.Serialize(stream, record); stream.Flush(true); }
        ProtectCreatedFile(temp);
        File.Move(temp, path, true);
        var check = PendulumPreparationInspection.Read();
        if (check?.Id != record.Id || check.Stage != record.Stage || check.ChildPid != record.ChildPid)
            throw new IOException("自动准备记录落盘回读失败。");
    }
    private static void ProtectCreatedFile(string path)
    {
        NoLinks(path);
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"), FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
        PendulumPreparationInspection.VerifyProtectedFile(path);
    }
    public void CheckPreviousChild(PendulumPreparationRecord record)
    {
        if (record.ChildKind.Length == 0) return;
        if (record.ChildBootId == BootId)
        {
            if (record.ChildPid is not { } pid || record.ChildStartTicks <= 0)
                throw new IOException("上次子进程登记未完成；请完成 Windows 重启后再核对，未重复执行。");
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.StartTime.ToUniversalTime().Ticks == record.ChildStartTicks && !process.HasExited)
                    throw new IOException("上次准备子进程仍在运行，未启动第二份操作。");
            }
            catch (ArgumentException) { } // exact old PID no longer exists
        }
        record.ChildPid = null; record.ChildStartTicks = 0; record.ChildKind = ""; Save(record);
    }
    private string BackupPath(PendulumPreparationRecord record) => Path.Combine(PendulumPreparationInspection.DirectoryPath, "bcd-before-" + Guid.ParseExact(record.Id, "N").ToString("N") + ".bcd");
    public void BackupBoot(PendulumPreparationRecord record)
    {
        var path = BackupPath(record);
        if (File.Exists(path))
        {
            NoLinks(path);
            Run(record, "bcd-backup-check", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "bcdedit.exe"), ["/store", path, "/enum", "all"]);
        }
        else
        {
            Run(record, "bcd-export", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "bcdedit.exe"), ["/export", path]);
            ProtectCreatedFile(path);
        }
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        PendulumPreparationInspection.VerifyProtectedFile(path);
        if (file.Length is < 4096 or > 16777216) throw new IOException("BCD备份大小异常，未修改启动设置。");
        record.BootBackupSha = Convert.ToHexString(SHA256.HashData(file));
    }
    public void EnableTestSigning(PendulumPreparationRecord record)
    {
        var path = BackupPath(record);
        NoLinks(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        PendulumPreparationInspection.VerifyProtectedFile(path);
        if (!Convert.ToHexString(SHA256.HashData(file)).Equals(record.BootBackupSha, StringComparison.OrdinalIgnoreCase))
            throw new IOException("BCD备份核对失败，未修改启动设置。");
        Run(record, "bcd-testsigning", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "bcdedit.exe"), ["/set", "{current}", "testsigning", "on"]);
    }
    public void InstallOrStart(PendulumPreparationRecord record)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + PendulumWin10DriverInspection.ServiceName);
        string action = key is null ? "Install" : "Start";
        if (key?.GetValue("ImagePath") as string == "\"" + PendulumWin10DriverInspection.DriverPath + "\"")
            Setup(record, "RepairPath");
        Setup(record, action);
    }
    private void Setup(PendulumPreparationRecord record, string action)
        => Run(record, "driver-" + action, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "RemoteSigned", "-File", Path.Combine(PackageDirectory, "Setup-Driver.ps1"), "-Action", action]);
    private void Run(PendulumPreparationRecord record, string kind, string exe, string[] arguments)
    {
        if (Blocker() is { } blocked) throw new IOException(blocked);
        record.ChildKind = kind; record.ChildBootId = BootId; record.ChildPid = null; record.ChildStartTicks = 0; Save(record);
        var encoding = CodePagesEncodingProvider.Instance.GetEncoding(CultureInfo.InstalledUICulture.TextInfo.OEMCodePage) ?? Encoding.UTF8;
        using var child = new Process { StartInfo = new ProcessStartInfo(exe)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = encoding, StandardErrorEncoding = encoding, WorkingDirectory = PackageDirectory } };
        foreach (var arg in arguments) child.StartInfo.ArgumentList.Add(arg);
        if (!child.Start()) throw new IOException("准备子进程未启动。");
        record.ChildPid = child.Id; record.ChildStartTicks = child.StartTime.ToUniversalTime().Ticks; Save(record);
        var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        // On timeout keep its exact identity and the journal. Never kill an in-flight system write.
        Task.WhenAll(output, error, child.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
        record.ChildPid = null; record.ChildStartTicks = 0; record.ChildKind = ""; Save(record);
        if (child.ExitCode != 0)
        {
            var detail = (output.Result + "\n" + error.Result).Trim();
            if (detail.Length > 4096) detail = detail[..4096];
            throw new IOException($"{kind} 失败（退出码 {child.ExitCode}）：{detail}");
        }
    }
    public void Dispose() { foreach (var file in _locks) file.Dispose(); }
}
