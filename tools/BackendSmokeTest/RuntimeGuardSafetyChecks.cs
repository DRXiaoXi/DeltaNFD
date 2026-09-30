using System.Text.Json;
using DeltaNFD.Services;
using Microsoft.Win32;

internal static class RuntimeGuardSafetyChecks
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD_RuntimeGuardChecks_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "backups.json");
            var store = new TweakBackupStore(file);
            var entry = new RegistryValueBackup
            {
                Hive = "HKLM", KeyPath = @"SOFTWARE\DeltaNFD\Test", ValueName = "Debugger",
                ValueKind = RegistryValueKind.None, CreatedAt = DateTimeOffset.UtcNow,
            };
            store.SaveStrict(entry);
            if (store.GetStrict("HKLM", entry.KeyPath, entry.ValueName) is null)
                throw new Exception("Strict backup read lost a saved entry");

            var expandable = new RegistryValueBackup
            {
                Hive = "HKLM", KeyPath = @"SOFTWARE\DeltaNFD\ExpandStringTest", ValueName = "Debugger",
                ValueKind = RegistryValueKind.ExpandString, Data = @"%windir%\System32\example.exe",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            store.SaveStrict(expandable);
            var restored = store.GetStrict("HKLM", expandable.KeyPath, expandable.ValueName);
            if (restored?.ValueKind != RegistryValueKind.ExpandString || restored.Data != expandable.Data)
                throw new Exception("ExpandString backup kind or content changed");

            File.WriteAllText(file, "{invalid json");
            var before = File.ReadAllText(file);
            try
            {
                store.SaveStrict(new RegistryValueBackup
                {
                    Hive = "HKLM", KeyPath = @"SOFTWARE\DeltaNFD\Other", ValueName = "Debugger",
                    ValueKind = RegistryValueKind.None,
                });
                throw new Exception("Save accepted a corrupt backup file");
            }
            catch (JsonException) { }
            if (File.ReadAllText(file) != before)
                throw new Exception("Save overwrote a corrupt backup file");

            var directoryStore = new TweakBackupStore(root);
            try
            {
                directoryStore.GetStrict("HKLM", entry.KeyPath, entry.ValueName);
                throw new Exception("Strict read accepted a directory as a backup file");
            }
            catch (IOException) { }

            var status = await new RuntimeGuardService(store).GetStatusAsync();
            Console.WriteLine($"运行库防护只读检查通过：IFEO {status.IfeoCount}/{status.IfeoTotal}；备份损坏时拒绝覆盖");
        }
        finally
        {
            var fullRoot = Path.GetFullPath(root);
            var tempPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (fullRoot.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(fullRoot, recursive: true);
        }
    }
}
