using DeltaNFD.Services;
using DeltaNFD.Services.TweakDb;

if (args.Contains("--freq", StringComparer.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("--freq 已移除：当前电源服务不再提供频率上限写入接口。");
    Environment.ExitCode = 2;
    return;
}

// --toggle <条目名>：对单个条目做 开→查→关→查 的写读冒烟（会真实写系统，仅用于低风险键验证）
if (args.Length == 2 && args[0] == "--toggle")
{
    var target = args[1];
    var item = BxCatalog.Database.Values.SelectMany(v => v).FirstOrDefault(i => i.Name == target);
    if (item is null)
    {
        Console.WriteLine("item not found: " + target);
        return;
    }

    var svc = new BxService();
    Console.WriteLine($"before: {await svc.GetItemStateAsync(item)}");
    Console.WriteLine("apply ON:  " + (await svc.ApplyItemAsync(item, true)).Message);
    Console.WriteLine($"after ON:  {await svc.GetItemStateAsync(item)}");
    Console.WriteLine("apply OFF: " + (await svc.ApplyItemAsync(item, false)).Message);
    Console.WriteLine($"after OFF: {await svc.GetItemStateAsync(item)}");
    return;
}

Console.WriteLine("=== 扩展优化库逐项状态检查（只读） ===");
Console.WriteLine($"管理员：{(ElevationHelper.IsElevated ? "是" : "否")}");
Console.WriteLine();

var bx = new BxService();
int totalUnknown = 0;

foreach (var section in BxCatalog.Sections)
{
    if (section.JsonFile is null)
    {
        // 服务组
        Console.WriteLine($"---- {section.Title} ----");
        foreach (var g in BxCatalog.ServiceGroups)
        {
            var state = await bx.GetServiceGroupStateAsync(g);
            if (state == BxState.Unknown)
            {
                totalUnknown++;
            }

            Console.WriteLine($"  [{State(state)}] {g.Name}");
        }

        Console.WriteLine();
        continue;
    }

    if (!BxCatalog.Database.TryGetValue(section.Id, out var items) || items.Count == 0)
    {
        Console.WriteLine($"---- {section.Title} ---- 数据库为空或加载失败！");
        totalUnknown++;
        continue;
    }

    Console.WriteLine($"---- {section.Title}（{items.Count} 项）----");
    var states = await bx.GetItemStatesAsync(items);
    foreach (var item in items)
    {
        var state = states[item];
        var (name, _) = BxCatalog.Describe(item.Name);
        if (state == BxState.Unknown)
        {
            totalUnknown++;
            var opTypes = string.Join(",", item.Tweaks.Select(t => t.TweakType).Distinct());
            Console.WriteLine($"  [未知] {item.Name} ({name}) ops={opTypes}");
        }
        else
        {
            Console.WriteLine($"  [{State(state)}] {item.Name} ({name})");
        }
    }

    Console.WriteLine();
}

Console.WriteLine($"==== 状态未知条目总数：{totalUnknown} ====");

static string State(BxState s) => s switch
{
    BxState.On => "已禁用",
    BxState.Off => "未禁用",
    BxState.Mixed => "部分禁用",
    _ => "未知",
};
