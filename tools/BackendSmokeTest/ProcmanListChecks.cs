using System.Collections.ObjectModel;
using DeltaNFD.Services;
using DeltaNFD.Execution.Procman;

internal static class ProcmanListChecks
{
    private sealed class Row(string key, int value) { public string Key = key; public int Value = value; }
    internal static void Run()
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; Console.WriteLine("PASS: " + name); }
        var a = new Row("pid:1:start:1:path:A", 1); var b = new Row("pid:2:start:2:path:B", 2);
        var rows = new ObservableCollection<Row> { a, b }; var changes = 0;
        rows.CollectionChanged += (_, _) => changes++;
        void Apply(params Row[] incoming) => StableList.Apply(rows, incoming, r => r.Key, (old, snapshot) => old.Value = snapshot.Value);
        Apply(new(a.Key, 10), new(b.Key, 20));
        Check(changes == 0 && ReferenceEquals(rows[0], a) && ReferenceEquals(rows[1], b) && a.Value == 10, "数值刷新保留行对象且不发出任何集合重建通知");
        Apply(new(a.Key, 10), new(b.Key, 20));
        Check(changes == 0, "相同快照不重复触发布局集合通知");
        var c = new Row("pid:3:start:3:path:C", 30);
        Apply(new(a.Key, 11), c, new(b.Key, 21));
        Check(changes == 1 && ReferenceEquals(rows[2], b) && b.Value == 21, "仅新增进程插入，存活行及选中引用保持稳定");
        Apply(new(b.Key, 22), new(c.Key, 31));
        Check(rows.Count == 2 && ReferenceEquals(rows[0], b) && !rows.Contains(a), "退出进程移除、真实顺序变化移动，存活身份不被替换");
        var reused = new Row("pid:2:start:99:path:B", 40);
        Apply(reused, new(c.Key, 32));
        Check(!rows.Contains(b) && ReferenceEquals(rows[0], reused), "PID 复用不能继承旧身份行及选中引用");
        var before = rows.ToArray(); var invalid = false;
        try { Apply(new("duplicate", 1), new("duplicate", 2)); } catch (ArgumentException) { invalid = true; }
        Check(invalid && rows.SequenceEqual(before), "重复身份快照拒绝且原列表保持不变");
        Apply(); Check(rows.Count == 0, "真实空快照移除退出项");

        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-list-checks-" + id.ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D"));
        AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        try
        {
            using var engine = new ProcmanEngine(Path.Combine(root, "changes.json"));
            var general = engine.List(new() { Scope = "general", SortOrder = "name", Limit = 500 });
            Check(general.Processes.All(p => !p.Name.Equals(DeltaForceLocator.GameProcessName, StringComparison.OrdinalIgnoreCase)), "普通列表排除三角洲主进程");
            var delta = engine.List(new() { Scope = "delta", SortOrder = "name", Limit = 500 });
            Check(delta.Processes.All(p => p.Name.Equals(DeltaForceLocator.GameProcessName, StringComparison.OrdinalIgnoreCase)), "独立三角洲列表仅返回其主进程身份");
            Check(general.Processes.SequenceEqual(general.Processes.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Pid)), "列表默认展示使用稳定名称/PID 排序");
            var rejected = false;
            try { engine.List(new() { Scope = "unknown" }); } catch (ArgumentException) { rejected = true; }
            Check(rejected && !File.Exists(Path.Combine(root, "changes.json")), "无效范围拒绝，列表查询不创建修改日志");
            Console.WriteLine($"Procman list checks: {count} passed. Only read-only process enumeration; isolated storage.");
        }
        finally
        {
            var target = Path.GetFullPath(root);
            if (target.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(target).StartsWith("DeltaNFD-list-checks-", StringComparison.Ordinal)) Directory.Delete(target, true);
        }
    }
}
