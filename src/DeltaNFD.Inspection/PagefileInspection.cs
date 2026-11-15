using System.Diagnostics;
using Microsoft.Win32;
using DeltaNFD.Services;

namespace DeltaNFD.Inspection;
public sealed class PagefileInspection
{
    private const string MemoryKeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private const string PagingFilesValue = "PagingFiles";
    public bool HasOriginalBackup => TweakBackupStore.Default.Get("HKLM", MemoryKeyPath, PagingFilesValue)is not null;

    public Task<PagefileInfo> GetInfoAsync() => DeltaNFD.Inspection.InspectionScheduler.Run(() =>
    {
        using var key = Registry.LocalMachine.OpenSubKey(MemoryKeyPath);
        var raw = key?.GetValue(PagingFilesValue) as string[];
        var entries = raw ?? Array.Empty<string>();
        var automatic = IsAutomatic(entries);
        var summary = automatic ? "当前：系统托管（自动管理所有驱动器的大小）" : entries.Length == 0 ? "当前：未配置任何页面文件" : "当前：手动管理 — " + string.Join("；", entries.Select(FormatEntry));
        return new PagefileInfo
        {
            AutomaticManaged = automatic,
            HasPagefile = entries.Length > 0,
            RawEntries = entries,
            Summary = summary,
        };
    });
    private static bool IsAutomatic(string[] entries) => entries.Length == 1 && entries[0].StartsWith("?:", StringComparison.OrdinalIgnoreCase);
    private static string FormatEntry(string entry)
    {
        // "C:\pagefile.sys 16384 32768" → "C:\pagefile.sys（16384–32768 MB）"
        var parts = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 && int.TryParse(parts[1], out var min) && int.TryParse(parts[2], out var max))
        {
            return $"{parts[0]}（{min / 1024}–{max / 1024} GB）";
        }

        return entry;
    }
}
