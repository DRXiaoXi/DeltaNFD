using DeltaNFD.Backends;

namespace DeltaNFD.Services.Backends;

/// <summary>定位官方后端 EXE。共享布局位于 Backends 根；兼容旧模块子目录及开发输出。</summary>
internal static class BackendPaths
{
    public const string BackendRootEnvironmentVariable = "DELTANFD_BACKEND_ROOT";

    public static string BackendsRoot => Path.Combine(AppContext.BaseDirectory, "Backends");

    /// <summary>解析模块 EXE 的绝对路径；找不到时抛 <see cref="BackendContractException"/>（绝不偷偷回退前端执行）。</summary>
    public static string Resolve(string moduleId)
    {
        var spec = BackendModules.Require(moduleId);
        var candidates = CandidatePaths(spec);
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new BackendContractException(
            $"未找到官方后端 {spec.DisplayName}（{spec.ExecutableName}）。已检查：" +
            string.Join("；", candidates) + "。请重新安装或修复程序文件，不会回退为主程序内执行。");
    }

    private static IEnumerable<string> CandidatePaths(BackendModuleSpec spec)
    {
        var overrideRoot = Environment.GetEnvironmentVariable(BackendRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            yield return Path.Combine(overrideRoot, spec.Id, spec.ExecutableName);
            yield return Path.Combine(overrideRoot, spec.ExecutableName);
        }

        yield return Path.Combine(BackendsRoot, spec.ExecutableName);
        yield return Path.Combine(BackendsRoot, spec.Id, spec.ExecutableName);

        // 开发期兜底：从当前输出目录向上找到解决方案根，再定位各后端项目的构建输出。
        foreach (var root in EnumerateAncestors(AppContext.BaseDirectory))
        {
            var sourceDir = Path.Combine(root, "src", "DeltaNFD.Backend." + ModuleFolder(spec.Id));
            if (!Directory.Exists(sourceDir))
            {
                continue;
            }

            foreach (var configuration in new[] { "Debug", "Release" })
            {
                foreach (var platform in new[] { "x64", "Any CPU" })
                {
                    yield return Path.Combine(sourceDir, "bin", platform, configuration, "net8.0-windows", "win-x64", spec.ExecutableName);
                }
                yield return Path.Combine(sourceDir, "bin", configuration, "net8.0-windows", "win-x64", spec.ExecutableName);
            }
        }
    }

    private static string ModuleFolder(string moduleId) => moduleId switch
    {
        BackendModules.Ace => "Ace",
        BackendModules.Gpu => "Gpu",
        BackendModules.Runtime => "Runtime",
        BackendModules.CpuFrame => "CpuFrame",
        BackendModules.Procman => "Procman",
        BackendModules.System => "SystemMaintenance",
        _ => moduleId,
    };

    private static IEnumerable<string> EnumerateAncestors(string path)
    {
        var current = new DirectoryInfo(path);
        for (var depth = 0; current is not null && depth < 10; depth++, current = current.Parent)
        {
            yield return current.FullName;
        }
    }
}
