using System.Diagnostics;
using DeltaNFD.Services;
using Microsoft.Win32;

internal static class BackendBackupConcurrencyChecks
{
    public static void SettingsChild(string path, string prefix)
    {
        for (var i = 0; i < 30; i++) AppSettingsStore.Update(path, s => s.AnnouncementVersionSeen += prefix);
    }
    public static void Child(string path, string prefix)
    {
        var store = new TweakBackupStore(path);
        for (var i = 0; i < 30; i++) store.SaveStrict(new RegistryValueBackup
        { Hive = "HKLM", KeyPath = "Fixture/" + prefix, ValueName = i.ToString(), Data = "first", ValueKind = RegistryValueKind.String });
    }
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-backup-concurrency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "backups.json");
        var children = new List<Process>();
        try
        {
            foreach (var prefix in new[] { "a", "b", "c" })
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("--backup-write-child"); start.ArgumentList.Add(path); start.ArgumentList.Add(prefix);
                children.Add(Process.Start(start)!);
            }
            foreach (var child in children)
            {
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                if (child.ExitCode != 0) throw new Exception("备份测试子进程失败。");
            }
            var store = new TweakBackupStore(path);
            if (store.GetAllStrict().Count != 90) throw new Exception("跨进程保存丢失了恢复记录。");
            Child(path, "a");
            if (store.GetAllStrict().Count != 90) throw new Exception("首次原值记录被重复创建。");
            File.WriteAllText(path, "{corrupt");
            try { Child(path, "new"); throw new Exception("损坏记录被覆盖。"); }
            catch (System.Text.Json.JsonException) { }
            if (File.ReadAllText(path) != "{corrupt") throw new Exception("损坏证据未保留。");
            var settingsPath = Path.Combine(root, "settings.json");
            foreach (var child in children) child.Dispose();
            children.Clear();
            foreach (var prefix in new[] { "a", "b", "c" })
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("--settings-write-child"); start.ArgumentList.Add(settingsPath); start.ArgumentList.Add(prefix);
                children.Add(Process.Start(start)!);
            }
            foreach (var child in children)
            {
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                if (child.ExitCode != 0) throw new Exception("设置测试子进程失败。");
            }
            var updates = AppSettingsStore.Read(settingsPath).AnnouncementVersionSeen;
            if (updates.Length != 90 || "abc".Any(c => updates.Count(x => x == c) != 30)) throw new Exception("跨进程设置更新丢失。");
            Console.WriteLine("Backend backup concurrency passed: 3 processes / 90 records, first-value preservation, corruption refusal. Temporary files only.");
        }
        finally
        {
            foreach (var child in children) { if (!child.HasExited) child.Kill(); child.Dispose(); }
            var full = Path.GetFullPath(root);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new Exception("测试清理路径无效。");
            Directory.Delete(full, true);
        }
    }
}
