using Microsoft.Win32;
using System.Security.Principal;
using System.Security.AccessControl;
using DeltaNFD.Services;

namespace DeltaNFD.Inspection;

/// <summary>固定Run键和当前/公共启动目录快捷方式，只读且不解析或运行命令。</summary>
public static class StartupItemInspection
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string Run32Key = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    public static string CurrentSid => WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("无法核对当前用户身份。");
    public static StartupItemStore Store() => new(Path.Combine(AppDataPaths.Root, "StartupItems"), CurrentSid);
    public static string Source(StartupItemScope scope) => scope switch
    {
        StartupItemScope.UserRun => "当前用户 · Run", StartupItemScope.MachineRun => "所有用户 · Run",
        StartupItemScope.MachineRun32 => "所有用户 · 32位Run", StartupItemScope.UserFolder => "当前用户 · 启动文件夹",
        _ => "所有用户 · 启动文件夹",
    };
    public static string Folder(StartupItemScope scope)
    {
        var path = scope switch
        {
            StartupItemScope.UserFolder => Environment.GetFolderPath(Environment.SpecialFolder.Startup, Environment.SpecialFolderOption.DoNotVerify),
            StartupItemScope.CommonFolder => Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup, Environment.SpecialFolderOption.DoNotVerify),
            _ => throw new ArgumentException("不是启动目录范围。"),
        };
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("启动目录不是可核对的本地绝对路径。");
        StartupItemStore.RejectLinks(path); return path;
    }
    public static RegistryKey? OpenRun(StartupItemScope scope, bool writable = false)
    {
        if (scope is not (StartupItemScope.UserRun or StartupItemScope.MachineRun or StartupItemScope.MachineRun32)) throw new ArgumentException("不是Run范围。");
        var hive = scope == StartupItemScope.UserRun ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        return root.OpenSubKey(scope == StartupItemScope.MachineRun32 ? Run32Key : RunKey, writable);
    }
    public static string ShortcutPath(StartupItemScope scope, string name)
    {
        if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name) || name.IndexOfAny(['\\', '/', ':']) >= 0
            || !new[] { ".lnk", ".url" }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
            throw new IOException("启动快捷方式名称无效。");
        var path = Path.Combine(Folder(scope), name); StartupItemStore.RejectLinks(path); return path;
    }
    public static StartupItemOriginal? ReadTarget(StartupItemScope scope, string name)
    {
        if (scope is StartupItemScope.UserFolder or StartupItemScope.CommonFolder)
            return ReadShortcutFile(ShortcutPath(scope, name), scope, name);
        using var key = OpenRun(scope);
        if (key is null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
        var kind = key.GetValueKind(name);
        if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString)) throw new IOException("非字符串启动登记，保留只读。");
        var data = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? throw new IOException("启动登记无法读取。");
        var result = new StartupItemOriginal(scope, name, (int)kind, data); StartupItemStore.Validate(result); return result;
    }
    internal static StartupItemOriginal? ReadShortcutFile(string path, StartupItemScope scope, string name)
    {
        StartupItemStore.RejectLinks(path);
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        if ((attributes & FileAttributes.Directory) != 0) throw new IOException("启动项路径不是普通快捷方式文件。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadShortcutStream(stream, path, scope, name);
    }
    internal static StartupItemOriginal ReadShortcutStream(FileStream stream, string path, StartupItemScope scope, string name)
    {
        var handle = stream.SafeFileHandle;
        var attributes = File.GetAttributes(handle);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) throw new IOException("启动项不是普通快捷方式文件。");
        if (stream.Length > 1024 * 1024) throw new IOException("快捷方式超过1MiB，保留只读。");
        var created = File.GetCreationTimeUtc(handle); var written = File.GetLastWriteTimeUtc(handle); var length = stream.Length;
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        string? zone = null;
        try
        {
            using var zoneStream = new FileStream(path + ":Zone.Identifier", FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (zoneStream.Length > 65536) throw new IOException("快捷方式来源标记过大，保留只读。");
            var zoneBytes = new byte[(int)zoneStream.Length]; zoneStream.ReadExactly(zoneBytes); zone = Convert.ToBase64String(zoneBytes);
        }
        catch (FileNotFoundException) { }
        var security = stream.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        if (created != File.GetCreationTimeUtc(handle) || written != File.GetLastWriteTimeUtc(handle) || length != stream.Length)
            throw new IOException("读取期间启动快捷方式发生变化。");
        var result = new StartupItemOriginal(scope, name, 0, Convert.ToBase64String(bytes), created.Ticks,
            written.Ticks, (int)attributes, zone, security);
        StartupItemStore.Validate(result); return result;
    }
    public static (List<StartupItemOriginal> Items, List<string> Errors) Discover()
    {
        var items = new List<StartupItemOriginal>(); var errors = new List<string>();
        foreach (var scope in Enum.GetValues<StartupItemScope>())
        {
            try
            {
                string[] names;
                if (scope is StartupItemScope.UserFolder or StartupItemScope.CommonFolder)
                {
                    var folder = Folder(scope);
                    names = Directory.Exists(folder) ? Directory.GetFiles(folder, "*", SearchOption.TopDirectoryOnly)
                        .Where(p => new[] { ".lnk", ".url" }.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)).Select(Path.GetFileName).Cast<string>().ToArray() : [];
                }
                else { using var key = OpenRun(scope); names = key?.GetValueNames() ?? []; }
                if (names.Length > 1024 || items.Count + names.Length > 1024) throw new IOException("启动项超过1024项，请在Windows设置中核对。");
                foreach (var name in names)
                    try { if (ReadTarget(scope, name) is { } item) items.Add(item); }
                    catch (Exception ex) { errors.Add(Source(scope) + " / " + name + "：" + ex.Message); }
            }
            catch (Exception ex) { errors.Add(Source(scope) + "：" + ex.Message); }
        }
        return (items, errors);
    }
    public static string Fingerprint(string id, StartupItemOriginal? current, StartupItemBackup? backup) =>
        StartupItemOriginal.Hash(id + "|" + (current?.Fingerprint ?? "absent") + "|" + (backup?.Fingerprint ?? "none"));
    public static StartupItemInfo Present(string id, StartupItemOriginal? current, StartupItemBackup? backup)
    {
        var original = current ?? backup?.Original ?? throw new IOException("启动项不存在。");
        var conflict = current is not null && backup is not null && !current.SameContent(backup.Original);
        var state = backup is null ? "登记存在" : current is null ? "本工具已停用" : conflict ? "登记已变化，需核对" : "原登记存在，备份待收尾";
        var command = original.Scope is StartupItemScope.UserFolder or StartupItemScope.CommonFolder ? "启动快捷方式：" + original.Name : original.Data;
        return new(id, original.Name.Length == 0 ? "（默认值）" : original.Name, Source(original.Scope), command, state,
            conflict ? "当前登记与原始备份不同，已阻止恢复；可核对后保留当前并归档备份。"
            : backup is null ? "仅表示登记存在；Windows设置或应用自身可能另行禁用。" : "原始备份保存在软件数据目录。",
            Fingerprint(id, current, backup), backup is not null, backup is null && current is not null,
            backup is not null && !conflict, conflict);
    }
    public static StartupItemReport Read()
    {
        var (items, errors) = Discover();
        IReadOnlyList<StartupItemBackup> backups;
        try { backups = Store().ReadAll(); }
        catch (Exception ex) { errors.Add("原始备份读取失败：" + ex.Message); return new([], errors); }
        var ids = items.Select(i => i.Id).Concat(backups.Select(b => b.Original.Id)).Distinct(StringComparer.Ordinal).ToArray();
        var result = new List<StartupItemInfo>();
        foreach (var id in ids)
        {
            if (result.Count >= 512) { errors.Add("项目较多，当前最多显示512项，请在Windows启动设置继续核对。"); break; }
            var backup = backups.SingleOrDefault(b => b.Original.Id == id);
            var original = items.SingleOrDefault(i => i.Id == id) ?? backup?.Original;
            try
            {
                var row = Present(id, ReadTarget(original!.Scope, original.Name), backup);
                if (result.Sum(i => i.Command.Length) + row.Command.Length > 128 * 1024) { errors.Add("启动命令总量过大，当前列表仅显示已读取部分。"); break; }
                result.Add(row);
            }
            catch (Exception ex) { errors.Add(Source(original!.Scope) + " / " + original.Name + "：" + ex.Message); }
        }
        return new(result.OrderByDescending(i => i.HasBackup).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToArray(), LimitErrors(errors));
    }
    public static StartupItemReport ReadBackups()
    {
        var items = new List<StartupItemInfo>(); var errors = new List<string>();
        try
        {
            foreach (var backup in Store().ReadAll())
                try
                {
                    var row = Present(backup.Original.Id, ReadTarget(backup.Original.Scope, backup.Original.Name), backup);
                    if (items.Count >= 512 || items.Sum(i => i.Command.Length) + row.Command.Length > 128 * 1024)
                        throw new IOException("备份项目或命令总量较多，当前核对未覆盖全部记录。");
                    items.Add(row);
                }
                catch (Exception ex) { errors.Add(backup.Original.Name + "：" + ex.Message); }
        }
        catch (Exception ex) { errors.Add("自启动备份读取失败：" + ex.Message); }
        return new(items, LimitErrors(errors));
    }
    private static string[] LimitErrors(List<string> errors) => errors.Take(64).Select(e => e.Length > 512 ? e[..512] + "…" : e).ToArray();
}
