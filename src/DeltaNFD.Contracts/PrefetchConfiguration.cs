namespace DeltaNFD.Services;

/// <summary>Shared addresses for prefetch inspection, backup, disable and restore.</summary>
public static class PrefetchConfiguration
{
    public const string MemoryManagementPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    public const string PrefetchPath = MemoryManagementPath + @"\PrefetchParameters";
    public const string ApplicationPreLaunchPath = @"Software\Microsoft\Windows\CurrentVersion\ApplicationPreLaunch";

    public static IReadOnlyList<(string Hive, string Path, string Name, string Label)> RegistryValues { get; } =
        Array.AsReadOnly(new[]
        {
            ("HKLM", MemoryManagementPath, "EnableApplicationLaunchPrefetching", "应用启动预读取"),
            ("HKLM", MemoryManagementPath, "EnableOperationAPI", "OperationAPI"),
            ("HKLM", PrefetchPath, "EnablePrefetcher", "内核预读取"),
            ("HKCU", ApplicationPreLaunchPath, "Enabled", "用户预启动"),
        });
}
