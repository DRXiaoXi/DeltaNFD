using System.Diagnostics;
using System.Xml.Linq;
using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

/// <summary>
/// T6 模块边界检查（只读）：
///   1) 前端 csproj 不引用执行库 DeltaNFD.Execution（只引用 Contracts + Shared）；
///   2) 五个后端 csproj 只引用 DeltaNFD.Execution（+ 合同），不引用主程序、不含 WinUI/WindowsAppSDK；
///   3) 执行库 csproj 不含任何指向 ../DeltaNFD/ 的 &lt;Compile Include&gt;（零主工程源码链接）；
///   4) 合同程序集无项目引用、目标框架为 net8.0，源码不引用执行库（无执行实现）；
///   5) 共享库文件无主工程重复副本（物理搬迁唯一副本），且不引用执行库/主程序、不含 WinUI；
///   6) 后端缺失时 BackendPaths.Resolve 明确失败且不静默回退（在临时目录中运行子进程，规避源码兜底）；
///   7) 版本/哈希不兼容由握手判定明确拒绝（直接驱动 BackendClient 的同一判定逻辑）。
/// 只读检查，不安装、不运行真实后端写入、不改系统。
/// </summary>
internal static class ModuleBoundaryChecks
{
    private static int _passed;

    public static void Run(string[] args)
    {
        _passed = 0;
        var repo = FindRepoRoot();

        CheckFrontendBoundary(repo);
        CheckBackendBoundaries(repo);
        CheckExecutionNoSourceLinks(repo);
        CheckContractsExecutionFree(repo);
        CheckSharedSingleCopy(repo);
        CheckMigratedExecutionPrimitives(repo);
        CheckOfflineHelperBoundary(repo);
        CheckMissingBackendFailsClearly();
        CheckVersionAndHashMismatch();

        Console.WriteLine($"模块边界检查全部通过（{_passed} 项断言）：前端不引用执行库；五个后端只引用执行库+合同、" +
            "无 WinUI/主程序依赖；执行库零主工程源码链接；合同无执行实现、无项目引用；共享库无重复副本；" +
            "后端缺失/版本不兼容/映像哈希不一致均明确失败，绝不静默回退到前端执行。");
    }

    private static void CheckFrontendBoundary(string repo)
    {
        var doc = LoadCsproj(Path.Combine(repo, "src", "DeltaNFD", "DeltaNFD.csproj"));
        var refs = ProjectReferences(doc).Select(Normalize).ToArray();
        Check(refs.All(r => !r.Contains("DeltaNFD.Execution", StringComparison.OrdinalIgnoreCase)),
            "前端不引用执行库 DeltaNFD.Execution");
        Check(refs.Any(r => r.Contains("DeltaNFD.Contracts", StringComparison.OrdinalIgnoreCase)),
            "前端引用合同程序集 DeltaNFD.Contracts");
        Check(refs.Any(r => r.Contains("DeltaNFD.Shared", StringComparison.OrdinalIgnoreCase)),
            "前端引用共享基础设施库 DeltaNFD.Shared（T6 物理搬迁后）");
    }

    private static void CheckBackendBoundaries(string repo)
    {
        var backends = Directory.GetDirectories(Path.Combine(repo, "src"), "DeltaNFD.Backend.*");
        // 项目目录数量应与 BackendModules.All 的实际注册数保持一致。
        var inRepo = DeltaNFD.Backends.BackendModules.All.Values.Where(s => s.Id != DeltaNFD.Backends.BackendModules.ShaderKiller).ToArray();
        var expected = inRepo.Length;
        Check(backends.Length == expected,
            $"后端项目数与注册模块数一致（找到 {backends.Length} 个，注册 {expected} 个）");
        // 每个注册模块都必须有对应的 EXE 项目，避免只加注册不加工程或反之。
        Check(!backends.Any(d => Path.GetFileName(d) == "DeltaNFD.Backend.ShaderKiller"),
            "私有着色卡顿杀手后端不进入公开 src");
        foreach (var spec in inRepo)
        {
            Check(backends.Any(d => string.Equals(Path.GetFileName(d), spec.ExecutableName.Replace(".exe", ""),
                    StringComparison.OrdinalIgnoreCase)),
                $"{spec.Id} 的 {spec.ExecutableName} 存在对应后端项目");
        }
        foreach (var backend in backends.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(backend);
            var csproj = Directory.GetFiles(backend, "*.csproj").Single();
            var doc = LoadCsproj(csproj);
            var refs = ProjectReferences(doc).Select(Normalize).ToArray();
            Check(refs.Contains("DeltaNFD.Execution", StringComparer.OrdinalIgnoreCase) ||
                  refs.Any(r => r.EndsWith("DeltaNFD.Execution.csproj", StringComparison.OrdinalIgnoreCase)),
                $"{name} 引用执行库 DeltaNFD.Execution");
            Check(refs.All(r => r.Contains("DeltaNFD.Execution", StringComparison.OrdinalIgnoreCase) ||
                                r.Contains("DeltaNFD.Contracts", StringComparison.OrdinalIgnoreCase)),
                $"{name} 只引用执行库与合同程序集（无主程序/WinUI 项目引用）");
            Check(!refs.Any(r => r.EndsWith("DeltaNFD.csproj", StringComparison.OrdinalIgnoreCase)),
                $"{name} 不引用主程序 DeltaNFD.csproj");

            var packages = PackageReferences(doc).Select(Normalize).ToArray();
            Check(packages.All(p => !p.Contains("WindowsAppSDK", StringComparison.OrdinalIgnoreCase) &&
                                    !p.Contains("Windows.SDK.BuildTools", StringComparison.OrdinalIgnoreCase) &&
                                    !p.Contains("CommunityToolkit", StringComparison.OrdinalIgnoreCase)),
                $"{name} 不含 WinUI/WindowsAppSDK 包依赖");
        }
    }

    private static void CheckExecutionNoSourceLinks(string repo)
    {
        var csproj = Path.Combine(repo, "src", "DeltaNFD.Execution", "DeltaNFD.Execution.csproj");
        var doc = LoadCsproj(csproj);
        var includes = CompileIncludes(doc).Select(Normalize).ToArray();
        var intoMainProject = includes
            .Where(i => i.Contains(@"..\DeltaNFD\", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Check(intoMainProject.Length == 0,
            "执行库 csproj 无任何指向 ../DeltaNFD/ 的 <Compile Include>（零主工程源码链接）" +
            (intoMainProject.Length == 0 ? "" : "；仍存在：" + string.Join("；", intoMainProject)));
        var refs = ProjectReferences(doc).Select(Normalize).ToArray();
        Check(refs.Any(r => r.Contains("DeltaNFD.Shared", StringComparison.OrdinalIgnoreCase)),
            "执行库引用共享基础设施库 DeltaNFD.Shared");
        Check(!refs.Any(r => r.EndsWith("DeltaNFD.csproj", StringComparison.OrdinalIgnoreCase)),
            "执行库不引用主程序");
    }

    private static void CheckContractsExecutionFree(string repo)
    {
        var contractsDir = Path.Combine(repo, "src", "DeltaNFD.Contracts");
        var doc = LoadCsproj(Path.Combine(contractsDir, "DeltaNFD.Contracts.csproj"));
        var refs = ProjectReferences(doc).ToArray();
        Check(refs.Length == 0, "合同程序集不含任何项目引用（不可能含执行实现）");
        Check(refs.All(r => !r.Contains("DeltaNFD.Execution", StringComparison.OrdinalIgnoreCase)),
            "合同程序集不引用执行库");
        var tfm = (string?)doc.Descendants("TargetFramework").FirstOrDefault()?.Value ?? "";
        Check(tfm.Equals("net8.0", StringComparison.OrdinalIgnoreCase),
            $"合同程序集目标框架为 net8.0（非 windows，无执行实现）：{tfm}");
        foreach (var file in Directory.EnumerateFiles(contractsDir, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            Check(!text.Contains("DeltaNFD.Execution", StringComparison.Ordinal),
                "合同源文件不引用执行库：" + Path.GetFileName(file));
        }
    }

    private static void CheckSharedSingleCopy(string repo)
    {
        var sharedDir = Path.Combine(repo, "src", "DeltaNFD.Shared");
        var frontendDir = Path.Combine(repo, "src", "DeltaNFD");
        var files = Directory.EnumerateFiles(sharedDir, "*.cs", SearchOption.AllDirectories).ToArray();
        Check(files.Length > 0, "共享库包含已物理搬迁的基础设施源码");
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(sharedDir, file);
            Check(!File.Exists(Path.Combine(frontendDir, relative)),
                "共享文件在主工程中无重复副本：" + relative.Replace('\\', '/'));
        }

        var doc = LoadCsproj(Path.Combine(sharedDir, "DeltaNFD.Shared.csproj"));
        var packages = PackageReferences(doc).Select(Normalize).ToArray();
        Check(packages.All(p => !p.Contains("WindowsAppSDK", StringComparison.OrdinalIgnoreCase) &&
                                !p.Contains("CommunityToolkit", StringComparison.OrdinalIgnoreCase)),
            "共享库不含 WinUI/WindowsAppSDK 依赖");
        var refs = ProjectReferences(doc).Select(Normalize).ToArray();
        Check(refs.All(r => !r.Contains("DeltaNFD.Execution", StringComparison.OrdinalIgnoreCase) &&
                            !r.EndsWith("DeltaNFD.csproj", StringComparison.OrdinalIgnoreCase)),
            "共享库不引用执行库或主程序（无循环）");
    }

    private static void CheckMigratedExecutionPrimitives(string repo)
    {
        foreach (var name in new[] { "MemoryPurge", "ProcessPerf", "NvApiDrs", "Privilege", "CpuSets" })
        {
            Check(!File.Exists(Path.Combine(repo, "src", "DeltaNFD.Shared", "Native", name + ".cs")), name + " 不编入前端共享库");
            Check(File.Exists(Path.Combine(repo, "src", "DeltaNFD.Execution", "Native", name + ".cs")), name + " 由执行库持有");
        }
        Check(!File.Exists(Path.Combine(repo, "src", "DeltaNFD.Execution", "Native", "CoreParkingLoop.cs")),
            "退休的核心常驻轮询不再编入执行库");
        var frontendService = File.ReadAllText(Path.Combine(repo, "src", "DeltaNFD", "Services", "GameProcessService.cs"));
        Check(!frontendService.Contains("EmptyWorkingSet(", StringComparison.Ordinal)
            && !frontendService.Contains(".Kill(", StringComparison.Ordinal)
            && !frontendService.Contains("Process.GetProcesses(", StringComparison.Ordinal), "前端进程维护服务不再本地执行");
        Check(!File.Exists(Path.Combine(repo, "src", "DeltaNFD.Shared", "Services", "CpuTopologyService.cs")), "CPU 拓扑执行实现不再编入前端依赖");
        Check(!File.Exists(Path.Combine(repo, "src", "DeltaNFD", "Services", "OfflineCpuSetScheduler.cs")), "脱机 CPU 执行实现移出前端");
        var coordinator = File.ReadAllText(Path.Combine(repo, "src", "DeltaNFD", "Services", "OfflineModeCoordinator.cs"));
        Check(!coordinator.Contains("RestoreRecordedAssignments(", StringComparison.Ordinal)
            && coordinator.Contains("RestoreOfflineCpuSetsAsync(", StringComparison.Ordinal), "前端脱机恢复只调用独立后端");
        var sharedAssembly = typeof(AppSettingsStore).Assembly;
        foreach (var name in new[] { "FrameTweaksService", "FramePowerLifecycle", "CpuTopologyService" })
            Check(sharedAssembly.GetType("DeltaNFD.Services." + name) is null, "前端共享程序集不含执行类型：" + name);
        var mutators = new[] { "SetProcessDefaultCpuSets", "SetProcessDefaultCpuSetMasks", "SetThreadSelectedCpuSets",
            "SetThreadSelectedCpuSetMasks", "NtSetSystemInformation", "NtSetTimerResolution", "AdjustTokenPrivileges" };
        foreach (var type in sharedAssembly.GetTypes())
        foreach (var method in type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance))
        {
            var import = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Runtime.InteropServices.DllImportAttribute>(method);
            if (import is not null) Check(!mutators.Contains(import.EntryPoint ?? method.Name), "共享程序集不导入修改型原生 API：" + method.Name);
        }
    }

    private static void CheckOfflineHelperBoundary(string repo)
    {
        var doc = LoadCsproj(Path.Combine(repo, "src", "DeltaNFD.OfflineHelper", "DeltaNFD.OfflineHelper.csproj"));
        var refs = ProjectReferences(doc).Select(Normalize).ToArray();
        Check(refs.Any(r => r.Contains("DeltaNFD.Shared", StringComparison.OrdinalIgnoreCase)),
            "脱机助手引用共享基础设施库 DeltaNFD.Shared");
        Check(refs.Any(r => r.Contains("DeltaNFD.Execution", StringComparison.OrdinalIgnoreCase)),
            "脱机助手引用执行库 DeltaNFD.Execution");
        var packages = PackageReferences(doc).Select(Normalize).ToArray();
        Check(packages.All(p => !p.Contains("WindowsAppSDK", StringComparison.OrdinalIgnoreCase)),
            "脱机助手不含 WinUI/WindowsAppSDK 依赖");
    }

    /// <summary>
    /// 后端缺失时明确失败：在临时目录复制本测试程序集（脱离仓库源码兜底），
    /// 用空的后端根运行子进程，断言 BackendPaths.Resolve 对五个模块都抛出明确错误。
    /// </summary>
    private static void CheckMissingBackendFailsClearly()
    {
        if (!string.Equals(Path.GetFileName(Environment.ProcessPath), "BackendSmokeTest.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("自复制边界夹具必须通过 BackendSmokeTest.exe 启动，禁止复制 dotnet 运行时目录。");
        var source = Path.GetDirectoryName(Environment.ProcessPath)
                     ?? throw new Exception("无法确定测试程序目录。");
        var tempRoot = Path.Combine(Path.GetTempPath(), "DeltaNFD-module-boundary", Guid.NewGuid().ToString("N"));
        var childDir = Path.Combine(tempRoot, "child");
        var emptyBackendRoot = Path.Combine(tempRoot, "empty-backends");
        Directory.CreateDirectory(emptyBackendRoot);
        CopyDirectory(source, childDir);

        var childExe = Path.Combine(childDir, "BackendSmokeTest.exe");
        Check(File.Exists(childExe), "已在临时目录复制边界检查子进程");
        var start = new ProcessStartInfo
        {
            FileName = childExe,
            Arguments = $"--module-boundary-missing-child \"{emptyBackendRoot}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = childDir,
        };
        start.Environment[BackendPaths.BackendRootEnvironmentVariable] = emptyBackendRoot;
        using var process = Process.Start(start) ?? throw new Exception("无法启动边界检查子进程。");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(60000)) { try { process.Kill(); } catch { } throw new Exception("边界检查子进程超时。"); }
        Check(process.ExitCode == 0 && stdout.Contains("missing-ok", StringComparison.Ordinal),
            $"后端缺失时五个模块均明确失败且无回退（exit={process.ExitCode}，stdout={stdout.Trim()}，stderr={stderr.Trim()}）");

        try { Directory.Delete(tempRoot, recursive: true); } catch { /* 临时目录清理失败不影响结论 */ }
    }

    /// <summary>子进程：后端根为空时，五个模块的 Resolve 都必须抛出「未找到…不会回退」的明确错误。</summary>
    internal static int RunMissingChild(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("missing-child: 缺少空后端根参数。"); return 2; }
        Environment.SetEnvironmentVariable(BackendPaths.BackendRootEnvironmentVariable, args[1]);
        foreach (var module in BackendModules.All.Keys)
        {
            try
            {
                var resolved = BackendPaths.Resolve(module);
                Console.Error.WriteLine($"missing-child: 模块 {module} 意外解析到 {resolved}");
                return 1;
            }
            catch (BackendContractException ex)
            {
                if (!ex.Message.Contains("未找到官方后端", StringComparison.Ordinal) ||
                    !ex.Message.Contains("不会回退为主程序内执行", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine($"missing-child: 模块 {module} 错误信息不明确：{ex.Message}");
                    return 1;
                }
            }
        }

        Console.WriteLine("missing-ok");
        return 0;
    }

    /// <summary>版本/哈希不兼容：直接驱动 BackendClient 握手中的同一判定逻辑，必须明确拒绝。</summary>
    private static void CheckVersionAndHashMismatch()
    {
        BackendClient.EnsureCompatibleModuleVersion(AppVersion.NumericText);
        Check(true, "同版本后端通过握手版本判定");

        var versionRejected = false;
        try { BackendClient.EnsureCompatibleModuleVersion("0.0.0"); }
        catch (BackendContractException ex) { versionRejected = ex.Message.Contains("版本不兼容", StringComparison.Ordinal); }
        Check(versionRejected, "版本不兼容后端被明确拒绝（无静默成功）");

        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        BackendClient.EnsureImageHashMatches(hash, hash);
        Check(true, "映像哈希一致时通过握手完整性判定");

        var hashRejected = false;
        try { BackendClient.EnsureImageHashMatches(new string('0', 64), new string('a', 64)); }
        catch (BackendContractException ex) { hashRejected = ex.Message.Contains("哈希", StringComparison.Ordinal); }
        Check(hashRejected, "映像哈希不一致（可能被替换）被明确拒绝（无静默成功）");
    }

    private static XDocument LoadCsproj(string path) => XDocument.Load(path);

    private static IEnumerable<string> ProjectReferences(XDocument doc) =>
        doc.Descendants("ProjectReference").Select(e => e.Attribute("Include")?.Value ?? "").Where(v => v.Length > 0);

    private static IEnumerable<string> PackageReferences(XDocument doc) =>
        doc.Descendants("PackageReference").Select(e => e.Attribute("Include")?.Value ?? "").Where(v => v.Length > 0);

    private static IEnumerable<string> CompileIncludes(XDocument doc) =>
        doc.Descendants("Compile").Select(e => e.Attribute("Include")?.Value ?? "").Where(v => v.Length > 0);

    private static string Normalize(string value) => value.Replace('/', '\\');

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "DeltaNFD", "DeltaNFD.csproj"))) return dir.FullName;
        }
        throw new Exception("未能从测试输出定位仓库根目录。");
    }

    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception("模块边界检查失败：" + name);
        _passed++;
    }
}
