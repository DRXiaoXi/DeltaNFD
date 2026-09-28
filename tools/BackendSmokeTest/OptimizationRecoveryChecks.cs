using System.Text.Json;
using DeltaNFD.Services;
using DeltaNFD.Services.TweakDb;
using Microsoft.Win32;

internal static class OptimizationRecoveryChecks
{
    public static void Run(bool includeRegistry = true)
    {
        var path = Path.Combine(Path.GetTempPath(), $"delta-backup-check-{Guid.NewGuid():N}.json");
        try
        {
            var store = new TweakBackupStore(path);
            var original = new RegistryValueBackup
            {
                Hive = "HKLM", KeyPath = @"Software\DeltaNFDTest", ValueName = "Multi",
                ValueKind = RegistryValueKind.MultiString,
                Data = "first;second;part",
                StringData = ["first;second", "part"],
            };
            store.Save(original);
            store.Save(new RegistryValueBackup
            {
                Hive = original.Hive, KeyPath = original.KeyPath, ValueName = original.ValueName,
                ValueKind = RegistryValueKind.MultiString, StringData = ["overwritten"],
            });
            var saved = store.Get(original.Hive, original.KeyPath, original.ValueName)!;
            Check(saved.StringData is ["first;second", "part"], "首次备份与数组往返");
            Check(store.GetAll().Count == 1, "备份条目数");
            Check(store.Remove(original.Hive, original.KeyPath, original.ValueName), "移除已恢复备份");
            Check(store.GetAll().Count == 0, "备份清除");

            var typed = JsonSerializer.Deserialize<BxBackupEntry>(JsonSerializer.Serialize(new BxBackupEntry
            {
                ValueKind = (int)RegistryValueKind.MultiString,
                Data = "a;b;c", StringData = ["a;b", "c"],
            }))!;
            Check(BxService.DecodeValue(typed) is string[] { Length: 2 } values && values[0] == "a;b", "扩展库无损多字符串");
            Check(BxService.DecodeValue(new BxBackupEntry
            {
                ValueKind = (int)RegistryValueKind.MultiString, Data = "old;backup",
            }) is string[] { Length: 2 }, "旧格式兼容");
            Expect<FormatException>(() => BxService.DecodeValue(new BxBackupEntry
            {
                ValueKind = (int)RegistryValueKind.MultiString, Data = "System.String[]",
            }), "旧版损坏备份不得写入类型名");
            Check(PagefileService.DecodeOriginalEntries(new RegistryValueBackup
            {
                ValueKind = RegistryValueKind.MultiString,
                StringData = [@"C:\pagefile.sys 16384 32768", @"D:\pagefile.sys 0 0"],
            }).Length == 2, "虚拟内存多盘原配置");
            Check(PagefileService.DecodeOriginalEntries(new RegistryValueBackup
            {
                ValueKind = RegistryValueKind.MultiString,
                Data = @"C:\pagefile.sys 16384 32768;D:\pagefile.sys 0 0",
            }).Length == 2, "虚拟内存旧备份兼容");

            if (includeRegistry) CheckTemporaryRegistryRoundTrip();

            Check(BxService.RunCmd("exit /b 7", TimeSpan.FromSeconds(5)) == 7, "CMD 非零退出码");
            Check(BxService.RunCmd("exit /b 0", TimeSpan.FromSeconds(5)) == 0, "CMD 成功退出码");
            Check(BxService.RunCmd("ping -n 6 127.0.0.1 >nul", TimeSpan.FromMilliseconds(100)) == -1, "CMD 超时退出码");
            Console.WriteLine(includeRegistry
                ? "系统优化恢复检查通过（临时 JSON、临时 HKCU 键、无副作用 CMD）。"
                : "系统优化恢复检查通过（临时 JSON、无副作用 CMD；HKCU 测试未运行）。");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void CheckTemporaryRegistryRoundTrip()
    {
        var sub = @"Software\DeltaNFD\RecoveryChecks\" + Guid.NewGuid().ToString("N");
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(sub))
            {
                key.SetValue("Dword", 42, RegistryValueKind.DWord);
                key.SetValue("Qword", 1234567890123L, RegistryValueKind.QWord);
                key.SetValue("Binary", new byte[] { 0, 127, 255 }, RegistryValueKind.Binary);
                key.SetValue("Multi", new[] { "one;two", "three" }, RegistryValueKind.MultiString);
                key.SetValue("Expand", "%TEMP%\\sample", RegistryValueKind.ExpandString);
            }

            var names = new[] { "Dword", "Qword", "Binary", "Multi", "Expand", "Missing" };
            var saved = names.Select(n => BxService.CaptureRegEntry(Registry.CurrentUser, sub, n)).ToList();
            using (var key = Registry.CurrentUser.OpenSubKey(sub, writable: true)!)
            {
                foreach (var name in names) key.SetValue(name, "changed", RegistryValueKind.String);
            }
            foreach (var entry in saved) BxService.RestoreRegEntry(entry);
            var corrupt = saved.First(e => e.ValueName == "Multi");
            corrupt.StringData = null;
            corrupt.Data = "System.String[]";
            Expect<FormatException>(() => BxService.RestoreRegEntry(corrupt), "损坏备份安全拒绝写回");
            using (var key = Registry.CurrentUser.OpenSubKey(sub)!)
            {
                Check((int)key.GetValue("Dword")! == 42, "DWORD 写回");
                Check((long)key.GetValue("Qword")! == 1234567890123L, "QWORD 写回");
                Check(key.GetValue("Binary") is byte[] { Length: 3 } bytes && bytes[2] == 255, "BINARY 写回");
                Check(key.GetValue("Multi") is string[] { Length: 2 } values && values[0] == "one;two", "MULTI_SZ 写回");
                Check((string)key.GetValue("Expand", null, RegistryValueOptions.DoNotExpandEnvironmentNames)! == "%TEMP%\\sample", "EXPAND_SZ 写回");
                Check(key.GetValue("Missing") is null, "原本不存在的值被删除");
            }
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"检查失败：{name}");
    }

    private static void Expect<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"检查失败：{name}");
    }
}
