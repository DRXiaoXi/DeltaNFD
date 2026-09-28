using DeltaNFD.Services;

// 一次性备份工具：枚举显卡 → 把当前 DeviceDesc 写入恢复备份库 + 纯文本副本。
// 只做备份，绝不修改注册表。

Console.WriteLine("=== 显卡原始型号备份 ===");

var backupDir = AppDataPaths.Root;

var spoof = new GpuSpoofService();
var adapters = await spoof.GetAdaptersAsync();
if (adapters.Count == 0)
{
    Console.WriteLine("未检测到显示适配器，没有需要备份的内容。");
    return;
}

var store = TweakBackupStore.Default;
var lines = new List<string>
{
    $"Delta NFD 显卡原始型号备份  {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
    $"恢复用备份库：{Path.Combine(backupDir, "backups.json")}",
    "",
};

var newCount = 0;
foreach (var adapter in adapters)
{
    Console.WriteLine($"显卡：{adapter.DisplayName}");
    Console.WriteLine($"    键：HKLM\\{adapter.RegistryPath}");

    var existing = store.Get(adapter.RegistryPath, "DeviceDesc");
    if (existing is not null)
    {
        Console.WriteLine("    已有更早的备份，跳过（保留最初的原始值）。");
        lines.Add($"[已有备份，跳过] {adapter.RegistryPath}");
        lines.Add($"  DeviceDesc = {existing.Data}");
        lines.Add("");
        continue;
    }

    if (adapter.IsMasked)
    {
        Console.WriteLine("    警告：当前值疑似已是伪装名，仍按当前值记录！");
    }

    store.Save(new RegistryValueBackup
    {
        Id = TweakBackupStore.MakeId(adapter.RegistryPath, "DeviceDesc"),
        KeyPath = adapter.RegistryPath,
        ValueName = "DeviceDesc",
        ValueKind = Microsoft.Win32.RegistryValueKind.String,
        Data = adapter.DeviceDesc,
        CreatedAt = DateTimeOffset.Now,
    });

    Console.WriteLine($"    已备份 DeviceDesc = {adapter.DeviceDesc}");
    lines.Add(adapter.RegistryPath);
    lines.Add($"  DeviceDesc = {adapter.DeviceDesc}");
    lines.Add("");
    newCount++;
}

var textPath = Path.Combine(backupDir, $"gpu-original-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
Directory.CreateDirectory(backupDir);
File.WriteAllLines(textPath, lines);

Console.WriteLine();
Console.WriteLine($"本次新备份 {newCount} 个显卡，共 {adapters.Count} 个显示适配器。");
Console.WriteLine($"恢复用备份库：{Path.Combine(backupDir, "backups.json")}");
Console.WriteLine($"纯文本副本：{textPath}");
Console.WriteLine("（未修改注册表，可放心测试伪装；测试后在「显卡伪装」页点「恢复原始型号」即可还原）");
