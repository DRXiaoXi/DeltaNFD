namespace DeltaNFD.Services;

public static class OfficialHelperPaths
{
    public static string ExecutablePath
    {
        get
        {
            var shared = Path.Combine(AppContext.BaseDirectory, "Backends", "DeltaNFD.OfflineHelper.exe");
            return File.Exists(shared) ? shared
                : Path.Combine(AppContext.BaseDirectory, "OfflineHelper", "DeltaNFD.OfflineHelper.exe");
        }
    }
}
