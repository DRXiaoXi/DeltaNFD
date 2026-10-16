using System.Collections.ObjectModel;

namespace DeltaNFD.Services;

/// <summary>Preserve surviving row instances; only membership/order changes notify the collection.</summary>
internal static class StableList
{
    internal static void Apply<T, TKey>(ObservableCollection<T> rows, IReadOnlyList<T> snapshots,
        Func<T, TKey> key, Action<T, T> update) where TKey : notnull
    {
        var incoming = snapshots.ToDictionary(key);
        for (var i = rows.Count - 1; i >= 0; i--)
            if (!incoming.ContainsKey(key(rows[i]))) rows.RemoveAt(i);
        var existing = rows.ToDictionary(key);
        for (var i = 0; i < snapshots.Count; i++)
        {
            var snapshot = snapshots[i];
            if (!existing.TryGetValue(key(snapshot), out var row)) { rows.Insert(i, snapshot); continue; }
            update(row, snapshot);
            if (ReferenceEquals(rows[i], row)) continue;
            rows.Move(rows.IndexOf(row), i);
        }
    }
}
