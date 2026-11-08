namespace DeltaNFD.Services.Backends;

/// <summary>PPID 会遗留并被复用；每一条父子关系都必须符合对应进程实例的创建时间。</summary>
internal static class BackendProcessTree
{
    internal sealed record Entry(string Name, int Pid, int ParentPid, long StartTicks);

    internal static (IReadOnlyList<BackendUnconfirmedStore.ChildProcess> Children, bool Ok) Capture(
        int rootPid, long rootStartTicks, IReadOnlyList<Entry> snapshot)
    {
        if (rootPid <= 0 || rootStartTicks <= 0) return ([], false);
        var children = new List<BackendUnconfirmedStore.ChildProcess>();
        var byPid = snapshot.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.ToArray());
        var byParent = snapshot.ToLookup(p => p.ParentPid);
        var frontier = new Queue<(int Pid, long StartTicks)>();
        frontier.Enqueue((rootPid, rootStartTicks));
        var seen = new HashSet<int> { rootPid };
        var complete = true;
        while (frontier.TryDequeue(out var parent))
        {
            // 若 PID 已被新进程复用，其启动后的孩子不属于旧实例。
            long? reusedAt = null;
            if (byPid.TryGetValue(parent.Pid, out var current))
            {
                if (current.Length != 1 || current[0].StartTicks <= 0
                    || current[0].StartTicks / 10 < parent.StartTicks / 10)
                { complete = false; continue; }
                if (current[0].StartTicks / 10 > parent.StartTicks / 10) reusedAt = current[0].StartTicks;
            }
            foreach (var child in byParent[parent.Pid])
            {
                if (child.Pid <= 0) { complete = false; continue; }
                if (child.StartTicks > 0 && (child.StartTicks / 10 < parent.StartTicks / 10
                    || reusedAt is { } limit && child.StartTicks / 10 >= limit / 10)) continue;
                if (!seen.Add(child.Pid)) continue;
                children.Add(new(child.Name, child.Pid, child.StartTicks));
                if (child.StartTicks <= 0) { complete = false; continue; }
                frontier.Enqueue((child.Pid, child.StartTicks));
            }
        }
        return (children, complete);
    }
}
