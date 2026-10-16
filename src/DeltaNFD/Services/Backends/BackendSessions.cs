namespace DeltaNFD.Services.Backends;

/// <summary>
/// 活动官方后端会话登记表：供生命周期（退出/更新/卸载/目标切换）统一排空使用。
/// 排空失败时调用方必须阻止交接，不得当作已完成。
/// </summary>
internal static class BackendSessions
{
    internal static TimeSpan StopTimeoutFor(string module) => module == DeltaNFD.Backends.BackendModules.CpuFrame
        ? DeltaNFD.Backends.BackendModules.TimeoutFor(module, DeltaNFD.Backends.BackendCommands.CpuFrameRestore) + TimeSpan.FromSeconds(30)
        : module == DeltaNFD.Backends.BackendModules.System ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30);
    private static readonly object Gate = new();
    private static readonly List<BackendClient> Active = [];

    public static void Register(BackendClient client)
    {
        lock (Gate) Active.Add(client);
    }

    public static void Unregister(BackendClient client)
    {
        lock (Gate) Active.Remove(client);
    }

    public static bool AnyActive
    {
        get { lock (Gate) return Active.Any(c => c.IsUsable || c.RequiresAttention || !c.ExitConfirmed); }
    }

    public static IReadOnlyList<BackendClient> Snapshot()
    {
        lock (Gate) return Active.ToArray();
    }

    /// <summary>排空全部后端；返回未能确认退出的模块列表（空 = 全部排空）。</summary>
    public static async Task<IReadOnlyList<string>> DrainAllAsync(TimeSpan? perBackendTimeout)
    {
        BackendClient[] clients;
        lock (Gate) clients = Active.ToArray();

        // 优化：并行排空所有后端，而非顺序处理，显著减少总等待时间
        var drainTasks = clients.Select(async client =>
        {
            try
            {
                var timeout = perBackendTimeout ?? StopTimeoutFor(client.ModuleId);
                await client.QuiesceAsync(timeout);
                // Disk persistence can fail. An in-memory unknown outcome still blocks handover
                // after the worker exits; process exit alone must never erase that obligation.
                if (client.RequiresAttention)
                {
                    return client.ModuleId;
                }
                return null;
            }
            catch (Exception ex)
            {
                Log.Warn($"官方后端排空失败：module={client.ModuleId}；需核对={client.RequiresAttention}；已确认退出={client.ExitConfirmed}；原因={ex.Message}");
                return client.ModuleId;
            }
        });

        var results = await Task.WhenAll(drainTasks);
        return results.Where(m => m is not null).ToArray()!;
    }
}
