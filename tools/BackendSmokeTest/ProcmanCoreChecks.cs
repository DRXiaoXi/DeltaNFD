internal static class ProcmanCoreChecks
{
    public static void Run() => ProcmanRepairChecks.RunAsync().GetAwaiter().GetResult();
    public static void RunChild() => Thread.Sleep(TimeSpan.FromMinutes(2));
}
