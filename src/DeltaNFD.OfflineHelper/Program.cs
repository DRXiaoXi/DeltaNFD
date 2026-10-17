namespace DeltaNFD.OfflineHelper;

// Retained only so historical project references fail safely if built manually.
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.Error.WriteLine("脱机模式已移除；此历史助手不再接受应用请求。");
        return 2;
    }
}
