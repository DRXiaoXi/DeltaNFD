using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace DeltaNFD.Services;

public enum DotNetCheckLevel { Complete, Incomplete, Failed }
public sealed record DotNetApp(string Name, string ConfigPath);
public sealed record DotNetComponent(string Name, string Source, string Version, bool Complete, bool Bundled, string Detail);
public sealed record DotNetCheckResult(DotNetCheckLevel Level, string Summary, IReadOnlyList<DotNetComponent> Components)
{
    public bool NeedsAppRepair => Components.Any(c => c.Bundled && !c.Complete);
    public bool NeedsSystemRuntime => Components.Any(c => !c.Bundled && !c.Complete);
    public string Details => string.Join(Environment.NewLine, Components.Select(c =>
        $"{c.Name}：{(c.Complete ? "文件检查通过" : "未通过")} · {c.Version} · {c.Source}\n{c.Detail}"));
}

/// <summary>仅开发构建工具使用：核验 runtimeconfig 和自包含部署文件；不编入产品程序。</summary>
public static class DotNetEnvironmentProbe
{
    private static readonly string[] CoreFiles = ["coreclr.dll", "clrjit.dll", "hostpolicy.dll", "System.Private.CoreLib.dll", "System.Runtime.dll", "netstandard.dll"];

    public static Task<DotNetCheckResult> CheckAsync(IReadOnlyList<DotNetApp> apps, IReadOnlyList<string> systemRoots,
        CancellationToken token = default) => Task.Run(() => Check(apps, systemRoots, token), token);

    private static DotNetCheckResult Check(IReadOnlyList<DotNetApp> apps, IReadOnlyList<string> roots, CancellationToken token)
    {
        var results = new List<DotNetComponent>();
        var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            token.ThrowIfCancellationRequested();
            bool bundled = false;
            bool resolved = false;
            try
            {
                using var config = JsonDocument.Parse(File.ReadAllText(app.ConfigPath));
                var options = config.RootElement.GetProperty("runtimeOptions");
                var selfContained = options.TryGetProperty("includedFrameworks", out var frameworks);
                bundled = selfContained;
                var required = selfContained ? frameworks.EnumerateArray().ToArray()
                    : options.TryGetProperty("frameworks", out frameworks) ? frameworks.EnumerateArray().ToArray()
                    : [options.GetProperty("framework")];
                if (required.Length != 1 || required[0].GetProperty("name").GetString() != "Microsoft.NETCore.App"
                    || !Version.TryParse(required[0].GetProperty("version").GetString(), out var version) || version.Major != 8)
                    throw new InvalidDataException("运行时需求不是本工具支持的 Microsoft.NETCore.App 8；请修复工具文件。");
                resolved = true;
                var appDir = Path.GetDirectoryName(Path.GetFullPath(app.ConfigPath))!;
                string runtimeDir, deps, hostfxr;
                if (selfContained)
                {
                    runtimeDir = appDir;
                    deps = app.ConfigPath[..^".runtimeconfig.json".Length] + ".deps.json";
                    hostfxr = Path.Combine(appDir, "hostfxr.dll");
                }
                else
                {
                    // Apphost loads an adjacent hostfxr before consulting the system installation.
                    // That host uses the app directory as its .NET root. A stale self-contained
                    // host next to a framework-dependent config must not pass on system files.
                    var localHostFxr = Path.Combine(appDir, "hostfxr.dll");
                    var hasLocalHost = File.Exists(localHostFxr);
                    bundled = hasLocalHost;
                    var exact = options.TryGetProperty("rollForward", out var roll) && roll.GetString() == "Disable";
                    var effectiveRoots = hasLocalHost ? new[] { appDir } : roots;
                    var candidates = effectiveRoots.SelectMany(root => InstalledRuntimes(root, version, exact))
                        .OrderByDescending(c => c.Version).ToArray();
                    if (candidates.Length == 0)
                    {
                        results.Add(new(app.Name, hasLocalHost ? "工具目录：" + appDir : "系统 x64 运行时",
                            version.ToString(), false, bundled, hasLocalHost
                                ? "部署冲突：配置依赖框架，但同目录 hostfxr.dll 将运行时查找限定到工具目录，未找到兼容框架。系统 .NET 不能弥补此布局；请修复工具安装或重新生成完整自包含版本。"
                                : "未找到兼容的 .NET 8 x64；.NET 6/9/10 或 x86 不能替代。"));
                        continue;
                    }
                    runtimeDir = candidates[0].Directory;
                    deps = Path.Combine(runtimeDir, "Microsoft.NETCore.App.deps.json");
                    hostfxr = hasLocalHost ? localHostFxr : candidates[0].HostFxr;
                    version = candidates[0].Version;
                }
                var key = runtimeDir + "|" + deps + "|" + hostfxr + "|" + version;
                if (!cache.TryGetValue(key, out var detail))
                {
                    detail = ValidateRuntime(runtimeDir, deps, hostfxr, version, token);
                    cache.Add(key, detail);
                }
                results.Add(new(app.Name, (bundled ? "工具自带：" : "系统：") + runtimeDir,
                    version.ToString(), detail.Length == 0, bundled, detail.Length == 0
                        ? "按依赖清单检查所需文件、PE 格式及原生组件 x64 架构；不等同于所有运行路径的功能验证。" : detail));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Invalid/missing app configuration cannot be fixed by installing a system runtime.
                results.Add(new(app.Name, app.ConfigPath, "无法确认", false, !resolved || bundled, ex.Message));
            }
        }
        if (results.Count == 0) return new(DotNetCheckLevel.Failed, "未获得工具运行时需求，检测失败。", results);
        var bad = results.Count(c => !c.Complete);
        return new(bad == 0 ? DotNetCheckLevel.Complete : DotNetCheckLevel.Incomplete,
            bad == 0 ? ".NET 8 x64 所需文件检查通过（按各组件实际部署配置核对）。"
                : $"{bad} 个组件的 .NET 检查未通过。" + (results.Any(c => c.Bundled && !c.Complete)
                    ? "工具自带文件或配置缺损，请重新生成完整部署。" : "缺少兼容框架；请生成完整自包含部署。"), results);
    }

    private sealed record Installed(Version Version, string Directory, string HostFxr);
    private static IEnumerable<Installed> InstalledRuntimes(string root, Version minimum, bool exact)
    {
        var shared = Path.Combine(root, "shared", "Microsoft.NETCore.App");
        if (!Directory.Exists(shared)) yield break;
        var fxrRoot = Path.Combine(root, "host", "fxr");
        var fxr = Directory.Exists(fxrRoot) ? Directory.EnumerateDirectories(fxrRoot)
            .Select(p => (Path: p, Version: Version.TryParse(Path.GetFileName(p), out var v) ? v : null))
            .Where(c => c.Version is not null && c.Version.Major >= 8).OrderByDescending(c => c.Version)
            .Select(c => Path.Combine(c.Path, "hostfxr.dll")).FirstOrDefault() : null;
        foreach (var dir in Directory.EnumerateDirectories(shared))
            if (Version.TryParse(Path.GetFileName(dir), out var v) && v.Major == 8 && v >= minimum && (!exact || v == minimum))
                yield return new(v, dir, fxr ?? Path.Combine(fxrRoot, "missing", "hostfxr.dll"));
    }

    private static string ValidateRuntime(string directory, string depsPath, string hostfxr, Version expectedVersion, CancellationToken token)
    {
        using var deps = JsonDocument.Parse(File.ReadAllText(depsPath));
        var targetName = deps.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()!;
        var target = deps.RootElement.GetProperty("targets").GetProperty(targetName);
        var assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in target.EnumerateObject().Where(p => IsRequiredRuntimeLibrary(p.Name, expectedVersion)))
            foreach (var group in new[] { "runtime", "native" })
                if (library.Value.TryGetProperty(group, out var files))
                    foreach (var file in files.EnumerateObject())
                    {
                        var name = Path.GetFileName(file.Name.Replace('/', '\\'));
                        if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                        assets.Add(Path.Combine(directory, name));
                    }
        if (!CoreFiles.All(f => assets.Contains(Path.Combine(directory, f))))
            throw new InvalidDataException(".NET x64 依赖清单缺失或不完整。");
        assets.Add(hostfxr);
        var failed = new List<string>();
        foreach (var file in assets)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var stream = File.OpenRead(file);
                using var pe = new PEReader(stream);
                var nativeRequired = new[] { "coreclr.dll", "clrjit.dll", "hostpolicy.dll", "hostfxr.dll" }.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase);
                if (pe.PEHeaders.PEHeader is null || (nativeRequired && pe.HasMetadata)
                    || (!pe.HasMetadata && pe.PEHeaders.CoffHeader.Machine != Machine.Amd64))
                    throw new InvalidDataException("PE 格式或 x64 架构不符");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { failed.Add(Path.GetFileName(file) + "（" + ex.Message + "）"); }
        }
        return failed.Count == 0 ? "" : $"{failed.Count} 个所需文件缺失/不可读/格式异常：" + string.Join("；", failed.Take(6));
    }

    private static bool IsRequiredRuntimeLibrary(string name, Version expectedVersion)
    {
        // A system runtime's own deps uses Microsoft.NETCore.App.Runtime.win-x64/VERSION;
        // SDK self-contained app publication uses runtimepack.Microsoft.NETCore.App.Runtime.win-x64/VERSION.
        // Match the complete package identity, architecture and declared version in both layouts.
        const string sdkPrefix = "runtimepack.";
        if (name.StartsWith(sdkPrefix, StringComparison.Ordinal)) name = name[sdkPrefix.Length..];
        var parts = name.Split('/');
        return parts.Length == 2 && parts[0] == "Microsoft.NETCore.App.Runtime.win-x64"
            && Version.TryParse(parts[1], out var actual) && actual == expectedVersion;
    }
}
