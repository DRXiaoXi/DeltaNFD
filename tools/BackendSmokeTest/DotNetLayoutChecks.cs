
using System.Text.Json;
using System.Text.Json.Nodes;
using DeltaNFD.Services;

internal static class DotNetLayoutChecks
{
    internal static async Task RunAsync()
    {
        int count = 0;
        void Check(bool pass, string name) { if (!pass) throw new Exception(name); count++; Console.WriteLine("PASS: " + name); }
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-dotnet-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var runtimeSource = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            var sourceRoot = Path.GetFullPath(Path.Combine(runtimeSource, "..", "..", ".."));
            var version = Path.GetFileName(runtimeSource);
            var bundled = Path.Combine(root, "bundled"); Directory.CreateDirectory(bundled);
            foreach (var file in Directory.EnumerateFiles(runtimeSource, "*.dll"))
                File.Copy(file, Path.Combine(bundled, Path.GetFileName(file)));
            var fxr = Directory.GetDirectories(Path.Combine(sourceRoot, "host", "fxr"))
                .Where(p => Version.TryParse(Path.GetFileName(p), out var v) && v.Major == 8)
                .OrderByDescending(p => Version.Parse(Path.GetFileName(p))).First();
            File.Copy(Path.Combine(fxr, "hostfxr.dll"), Path.Combine(bundled, "hostfxr.dll"));
            var config = Path.Combine(bundled, "Tool.Name.runtimeconfig.json");
            var deps = Path.Combine(bundled, "Tool.Name.deps.json");
            File.Copy(Path.Combine(runtimeSource, "Microsoft.NETCore.App.deps.json"), deps);
            void WriteConfig(bool selfContained, string name = "Microsoft.NETCore.App", string v = "8.0.0") =>
                File.WriteAllText(config, JsonSerializer.Serialize(new { runtimeOptions = selfContained
                    ? (object)new { includedFrameworks = new[] { new { name, version = v } } }
                    : new { framework = new { name, version = v } } }));
            Task<DotNetCheckResult> Probe(params string[] roots) => DotNetEnvironmentProbe.CheckAsync([new("fixture", config)], roots);
            WriteConfig(true, v: version);
            var good = await Probe();
            Check(good.Level == DotNetCheckLevel.Complete && good.Components.Single().Bundled, "自带运行时完整时，无系统 .NET 仍通过");
            var systemDeps = File.ReadAllText(deps);
            void WritePublishedDeps(string package = "runtimepack.Microsoft.NETCore.App.Runtime.win-x64", string? runtimeVersion = null)
            {
                var document = JsonNode.Parse(systemDeps)!;
                var target = document["targets"]![document["runtimeTarget"]!["name"]!.GetValue<string>()]!.AsObject();
                var entry = target.First(p => p.Key.StartsWith("Microsoft.NETCore.App.Runtime.win-x64/", StringComparison.Ordinal));
                var renamed = package + "/" + (runtimeVersion ?? version);
                target[renamed] = entry.Value!.DeepClone(); target.Remove(entry.Key);
                if (document["libraries"] is JsonObject libraries && libraries[entry.Key] is { } definition)
                { libraries[renamed] = definition.DeepClone(); libraries.Remove(entry.Key); }
                File.WriteAllText(deps, document.ToJsonString());
            }
            WritePublishedDeps();
            Check((await Probe()).Level == DotNetCheckLevel.Complete, "正式自包含 runtimepack 清单无系统 .NET 也通过");
            File.WriteAllBytes(Path.Combine(bundled, "coreclr.dll"), []);
            Check((await Probe(sourceRoot)).NeedsAppRepair, "正式布局 coreclr 缺损仍拒绝，系统 .NET 不掩盖缺损");
            File.Copy(Path.Combine(runtimeSource, "coreclr.dll"), Path.Combine(bundled, "coreclr.dll"), true);
            WritePublishedDeps("runtimepack.Microsoft.NETCore.App.Runtime.win-x86");
            Check((await Probe()).NeedsAppRepair, "runtimepack x86 清单不能冒充 x64");
            WritePublishedDeps(runtimeVersion: "8.0.0");
            Check((await Probe()).NeedsAppRepair, "runtimepack 版本必须匹配 runtimeconfig 声明");
            WritePublishedDeps("fake.Microsoft.NETCore.App.Runtime.win-x64");
            Check((await Probe()).NeedsAppRepair, "未知清单前缀不被宽泛匹配放行");
            File.WriteAllText(deps, systemDeps);
            File.WriteAllBytes(Path.Combine(bundled, "System.Runtime.dll"), []);
            var damaged = await Probe(sourceRoot);
            Check(damaged.NeedsAppRepair && !damaged.NeedsSystemRuntime, "系统 .NET 不能掩盖工具自带文件缺损");
            File.Copy(Path.Combine(runtimeSource, "System.Runtime.dll"), Path.Combine(bundled, "System.Runtime.dll"), true);
            var host = Path.Combine(bundled, "hostfxr.dll");
            var hostBytes = File.ReadAllBytes(host);
            var x86 = (byte[])hostBytes.Clone();
            var pe = BitConverter.ToInt32(x86, 0x3c); x86[pe + 4] = 0x4c; x86[pe + 5] = 0x01;
            File.WriteAllBytes(host, x86);
            Check((await Probe()).NeedsAppRepair, "x86 原生 hostfxr 不能满足 x64");
            File.WriteAllBytes(host, hostBytes);
            File.Delete(host);
            Check((await Probe()).NeedsAppRepair, "缺少自带 hostfxr 不通过");
            File.WriteAllBytes(host, hostBytes);
            File.WriteAllText(deps, "{}");
            Check((await Probe()).NeedsAppRepair, "缺少可核对的依赖清单不报完整");
            WriteConfig(false);
            var mixed = await Probe(sourceRoot);
            Check(mixed.NeedsAppRepair && !mixed.NeedsSystemRuntime
                && mixed.Level != DotNetCheckLevel.Complete && mixed.Details.Contains("部署冲突"),
                "框架依赖配置混入本地 hostfxr 时，即使系统 .NET 完整也拒绝并要求修复工具");
            Check((await Probe()).NeedsAppRepair, "混合部署不能误引导用户仅安装系统 .NET");
            File.Delete(host);
            Check((await Probe()).NeedsSystemRuntime, "框架依赖部署无 .NET 8 时提示系统安装");
            Check((await Probe(sourceRoot)).Level == DotNetCheckLevel.Complete, "系统 .NET 8 核验通过且不要求 WindowsDesktop 或 SDK");
            var emptySystem = Path.Combine(root, "system");
            Directory.CreateDirectory(Path.Combine(emptySystem, "shared", "Microsoft.NETCore.App", "10.0.1"));
            Check((await Probe(emptySystem)).NeedsSystemRuntime, ".NET 10 不代替默认所需 .NET 8");
            WriteConfig(false, "Microsoft.WindowsDesktop.App");
            Check((await Probe(sourceRoot)).NeedsAppRepair, "非工具声明的桌面框架不凭注册表猜测通过");
            WriteConfig(false);
            File.Delete(config);
            Check((await Probe(sourceRoot)).NeedsAppRepair, "配置缺失需修复工具而非安装系统运行时");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { await DotNetEnvironmentProbe.CheckAsync([], [], cancel.Token); throw new Exception("cancellation ignored"); }
            catch (OperationCanceledException) { Check(true, "检测尊重取消"); }

            Console.WriteLine($".NET build layout checks: {count} passed. Read-only system lookup; all writes isolated; no installer executed.");
        }
        finally
        {
            var target = Path.GetFullPath(root);
            if (target.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(target).StartsWith("DeltaNFD-dotnet-checks-", StringComparison.Ordinal))
                Directory.Delete(target, true);
        }
    }
}
