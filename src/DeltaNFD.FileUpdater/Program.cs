using System.Diagnostics;
using System.Text.Json;
using DeltaNFD.Updates;
using Microsoft.Win32;

if (args is ["--describe"])
{
    Console.WriteLine("{\"moduleId\":\"file-updater\",\"protocol\":1}");
    return 0;
}
if (args.Length != 4 || args[0] is not ("--apply" or "--recover")) return 2;
var session = Path.GetFullPath(args[1]);
var root = Path.GetFullPath(args[3]).TrimEnd(Path.DirectorySeparatorChar);
Mutex? gate = null;
bool acquired = false, exited = false;
string? failureMessage = null;
try
{
    ProtectedSessions.Validate(session);
    var worker = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
    if (!worker.Equals(Path.Combine(session, "worker"), StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("更新器必须从本次受保护会话运行。");
    using var registration = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{7A3E9C4D-52B8-4E1F-9A6C-D0F1B2E3A4C5}_is1");
    if (registration?.GetValue("InstallLocation") is not string location
        || !Path.GetFullPath(location).TrimEnd(Path.DirectorySeparatorChar).Equals(root, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("目标不是本工具登记的安装目录。");
    var manifest = FileUpdateRules.Read(File.ReadAllBytes(Path.Combine(session, "manifest.json")), args[2]);
    var payload = Path.Combine(session, "payload");
    if (args[0] == "--apply") FileTransaction.ValidatePayload(root, payload, manifest);
    gate = new Mutex(false, @"Local\DeltaNFD_7A3E9C4D_Deployment");
    try { acquired = gate.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
    if (!acquired) throw new IOException("其他安装或更新正在运行。");
    var activePath = Path.Combine(ProtectedSessions.BaseDirectory,
        "active-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..24] + ".json");
    if (File.Exists(activePath))
    {
        var oldSession = JsonSerializer.Deserialize<string>(File.ReadAllBytes(activePath))!;
        ProtectedSessions.Validate(oldSession);
        var journalPath = Path.Combine(oldSession, "transaction", "journal.json");
        if (File.Exists(journalPath))
        {
            var old = JsonSerializer.Deserialize<FileJournal>(File.ReadAllBytes(journalPath), FileUpdateRules.Json)!;
            if (old.State is not ("Committed" or "RolledBack") && oldSession != session)
                throw new IOException("上次更新事务尚未完成，请先使用保留的独立更新器 --recover 恢复：" + oldSession);
        }
    }
    FileUpdateRules.AtomicJson(activePath, session);
    WriteStatus("Ready", "文件已核验，正在请求安全退出。");
    if (args[0] == "--apply")
    {
        var approval = Path.Combine(session, "apply-approved.txt");
        var approvalWait = Stopwatch.StartNew();
        while (!File.Exists(approval) && approvalWait.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(100);
        if (!File.Exists(approval) || !File.ReadAllText(approval).Equals(args[2], StringComparison.OrdinalIgnoreCase))
            throw new IOException("主程序未确认更新器就绪，本次未退出或替换文件。");
    }
    var control = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
    { UseShellExecute = false, CreateNoWindow = true };
    foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
        Path.Combine(worker, "DeploymentControl.ps1"), "-InstallRoot", root, "-Mode", "install", "-ReportPath", Path.Combine(session, "handover.txt") }) control.ArgumentList.Add(argument);
    using (var process = Process.Start(control) ?? throw new IOException("安全退出协调器未启动。"))
    {
        if (!process.WaitForExit(620000)) throw new IOException("安全退出仍未完成，未强杀进程或替换文件。");
        if (process.ExitCode is not (0 or 10)) throw new IOException("安全交接未完成：" + File.ReadAllText(Path.Combine(session, "handover.txt")));
    }
    exited = true;
    var transaction = Path.Combine(session, "transaction");
    if (args[0] == "--recover")
    {
        FileTransaction.Rollback(transaction, root);
        var recovered = JsonSerializer.Deserialize<FileJournal>(File.ReadAllBytes(Path.Combine(transaction, "journal.json")), FileUpdateRules.Json)!;
        WriteStatus(recovered.State, recovered.State == "Committed" ? "该事务已提交，无需恢复。" : "上次文件替换已恢复。");
    }
    else
    {
        WriteStatus("Applying", "已安全退出，正在备份并替换文件。");
        FileTransaction.Apply(root, payload, manifest, transaction, () =>
        {
            WriteStatus("Verifying", "正在校验新版启动。");
            var nonce = Guid.NewGuid().ToString("N");
            FileUpdateRules.AtomicJson(Path.Combine(session, "health-request.json"), new { nonce, root, version = manifest.Version });
            var probe = new ProcessStartInfo(Path.Combine(root, "DeltaNFD.exe")) { UseShellExecute = false, WorkingDirectory = root };
            foreach (var arg in new[] { "--verify-file-update", session, nonce }) probe.ArgumentList.Add(arg);
            using var child = Process.Start(probe) ?? throw new IOException("新版启动验证未启动。");
            if (!child.WaitForExit(45000))
            {
                // This exact child is a stateless probe, created before any settings/frame/backend initialization.
                // Never terminate the running user application, maintenance workers or installers.
                if (!child.HasExited) child.Kill();
                if (!child.WaitForExit(10000)) throw new IOException("只读验证进程尚未退出，保留恢复证据。");
                throw new IOException("新版只读启动验证超时，将恢复旧文件。");
            }
            var ack = Path.Combine(session, "health-ok.txt");
            if (child.ExitCode != 0 || !File.Exists(ack) || File.ReadAllText(ack) != nonce)
                throw new IOException("新版启动失败，将恢复原文件。");
        });
        var warning = "";
        try
        {
            using var writable = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{7A3E9C4D-52B8-4E1F-9A6C-D0F1B2E3A4C5}_is1", writable: true)
                ?? throw new IOException("卸载登记不可写。");
            writable.SetValue("DisplayVersion", manifest.Version, RegistryValueKind.String);
            writable.SetValue("EstimatedSize", checked((int)((manifest.Files.Sum(f => f.SizeBytes) + 1023) / 1024)), RegistryValueKind.DWord);
            var batchId = Guid.NewGuid().ToString("D");
            writable.SetValue("UpdateChannelInstallationVersion", manifest.DisplayVersion, RegistryValueKind.String);
            if (manifest.DisplayVersion.StartsWith("Alpha", StringComparison.OrdinalIgnoreCase))
                writable.SetValue("UpdateChannelAlphaInstallationId", batchId, RegistryValueKind.String);
            writable.SetValue("UpdateChannelInstallationId", batchId, RegistryValueKind.String);
        }
        catch (Exception ex) { warning = "；文件已更新，但系统版本登记未更新：" + ex.Message; }
        WriteStatus("Committed", "文件更新及启动校验完成" + warning);
    }
    return 0;
}
catch (Exception ex)
{
    failureMessage = ex.Message;
    try { ProtectedSessions.Validate(session); WriteStatus("Failed", ex.ToString()); } catch { }
    Console.Error.WriteLine(ex);
    return 1;
}
finally
{
    if (acquired) gate!.ReleaseMutex();
    gate?.Dispose();
    if (exited)
    {
        // Never start an incomplete directory after rollback failed.
        var path = Path.Combine(session, "transaction", "journal.json");
        try
        {
            var journal = File.Exists(path) ? JsonSerializer.Deserialize<FileJournal>(File.ReadAllBytes(path), FileUpdateRules.Json) : null;
            if (!File.Exists(path) || journal?.State is "Committed" or "RolledBack")
                Process.Start(new ProcessStartInfo(Path.Combine(root, "DeltaNFD.exe")) { UseShellExecute = false, WorkingDirectory = root });
        }
        catch { }
        if (failureMessage is not null)
            WorkerNotice.Show("增量更新未完成。已保留更新记录和备份，请核对更新结果；必要时使用完整安装包。\n\n" + failureMessage + "\n\n记录目录：" + session);
    }
}

void WriteStatus(string state, string detail) => FileUpdateRules.AtomicJson(Path.Combine(session, "status.json"),
    new { state, detail, utc = DateTimeOffset.UtcNow });

internal static class WorkerNotice
{
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(nint window, string text, string caption, uint flags);
    internal static void Show(string text) => MessageBoxW(0, text, "三角帧不掉洲 · 文件更新", 0x10);
}
