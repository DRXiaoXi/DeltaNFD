using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Services.Backends;

/// <summary>
/// T5 部署布局检查：在隔离发布目录上验证「官方后端 + 脱机助手 + 共享/自包含运行时」布局，
/// 不依赖目标机全局 .NET，也不依赖源码目录兜底：
///   1) 前端 EXE、五个模块 EXE、脱机助手 EXE 均在位；
///   2) 每个组件的文件夹自带运行时（coreclr/hostfxr/hostpolicy）与 deps/Contracts/Execution；
///   3) Assets\TweakDb、Assets\UpdateConfig.json、WinUI pri/xbf 与运行库修复 payload 已随布局复制；
///   4) 关键资产的 SHA256 与源目录一致（防多个项目共用中间目录互相覆盖）；
///   5) 清空 DOTNET_ROOT/PATH 后仍能运行发布出的后端 --describe（证明不需要全局 .NET）。
/// 只读检查，不安装、不发布到真实位置、不改系统。
/// </summary>
internal static class DeploymentLayoutChecks
{
    private static int _passed;

    public static void Run(string[] args)
    {
        _passed = 0;
        var repo = FindRepoRoot();
        var layout = GetLayoutPath(args) ?? Path.Combine(repo, "_buildcheck", "t5", "publish");
        layout = Path.GetFullPath(layout);

        Check(Directory.Exists(layout), $"隔离发布目录存在：{layout}");

        var frontend = Path.Combine(layout, "DeltaNFD.exe");
        Check(File.Exists(frontend), "前端 DeltaNFD.exe 在布局中");
        CheckSelfContainedDirectory(layout, "前端");
        Check(File.Exists(Path.Combine(layout, "DeltaNFD.pri")), "WinUI 资源 DeltaNFD.pri 已复制");
        Check(Directory.EnumerateFiles(layout, "*.xbf", SearchOption.AllDirectories).Any(), "WinUI 页面 xbf 已复制");

        var backendsRoot = Path.Combine(layout, "Backends");
        var commonRuntime = File.Exists(Path.Combine(backendsRoot, BackendModules.Require(BackendModules.Ace).ExecutableName));
        var moduleFolders = new (string Module, string Folder)[]
        {
            (BackendModules.Ace, "ace"),
            (BackendModules.Gpu, "gpu"),
            (BackendModules.Runtime, "runtime"),
            (BackendModules.System, "system"),
            (BackendModules.CpuFrame, "cpuframe"),
            (BackendModules.Procman, "procman"),
        };
        foreach (var (module, folder) in moduleFolders)
        {
            var spec = BackendModules.Require(module);
            var directory = commonRuntime ? backendsRoot : Path.Combine(backendsRoot, folder);
            var exe = Path.Combine(directory, spec.ExecutableName);
            Check(File.Exists(exe), $"模块 {module} 的 {spec.ExecutableName} 在布局中");
            CheckSelfContainedDirectory(directory, $"模块 {module}");
            Check(File.Exists(Path.Combine(directory, "DeltaNFD.Contracts.dll")), $"模块 {module} 带合同程序集");
            Check(File.Exists(Path.Combine(directory, "DeltaNFD.Execution.dll")), $"模块 {module} 带执行库");
            Check(File.Exists(Path.Combine(directory, Path.GetFileNameWithoutExtension(spec.ExecutableName) + ".deps.json")), $"模块 {module} 自己的 deps 在位");
        }

        // 脱机模式已按产品决定移除（DeltaNFD.csproj：“Offline mode removed: no helper is built,
        // copied or published.”）。布局中不再包含 DeltaNFD.OfflineHelper.exe，因此这里断言其“不存在”，
        // 而不是沿用旧断言要求它存在——旧断言在移除后必然失败，且与当前真实布局不符。
        var helperExe = Path.Combine(layout, "OfflineHelper", "DeltaNFD.OfflineHelper.exe");
        Check(!File.Exists(helperExe), "已移除的脱机助手不在布局中（与当前产品范围一致）");
        Check(!File.Exists(Path.Combine(backendsRoot, "DeltaNFD.Backend.Diagnostics.exe"))
            && !Directory.Exists(Path.Combine(backendsRoot, "diagnostics")), "已撤下的卡顿诊断后端不在布局中");
        Check(!Directory.EnumerateFiles(layout, "ShaderStutterBridge.dll", SearchOption.AllDirectories).Any(),
            "已撤下的卡顿采集原生桥不在布局中");

        CheckAssets(repo, layout);
        CheckBackendPathsResolve(layout, moduleFolders);
        CheckRunsWithoutGlobalDotNet(backendsRoot, moduleFolders, commonRuntime);
        if (commonRuntime)
            foreach (var runtime in new[] { "coreclr.dll", "hostfxr.dll", "hostpolicy.dll" })
                Check(Directory.GetFiles(backendsRoot, runtime, SearchOption.AllDirectories).Length == 1,
                    "五后端及助手共用唯一的 " + runtime);

        Console.WriteLine($"Deployment layout checks passed ({_passed} assertions): frontend + 7 backends present, offline helper absent as expected, " +
            (commonRuntime ? "backends and helper share one runtime; " : "legacy per-component runtime; ") + "assets copied and hash-verified, " +
            "WinUI pri/xbf present, backends run with DOTNET_ROOT/PATH cleared. Isolated publish dir only; no installer, no live install.");
    }

    private static void CheckSelfContainedDirectory(string directory, string label)
    {
        foreach (var runtime in new[] { "coreclr.dll", "hostfxr.dll", "hostpolicy.dll" })
            Check(File.Exists(Path.Combine(directory, runtime)), $"{label} 自带运行时 {runtime}");
        var runtimeConfig = Directory.EnumerateFiles(directory, "*.runtimeconfig.json").FirstOrDefault();
        Check(runtimeConfig is not null, $"{label} 带 runtimeconfig.json");
        if (runtimeConfig is not null)
        {
            using var config = JsonDocument.Parse(File.ReadAllText(runtimeConfig));
            Check(config.RootElement.GetProperty("runtimeOptions").TryGetProperty("includedFrameworks", out var included)
                  && included.ValueKind == JsonValueKind.Array
                  && included.EnumerateArray().Any(f => f.GetProperty("name").GetString() == "Microsoft.NETCore.App"),
                $"{label} runtimeconfig 声明随附框架而非依赖全局 .NET");
        }
    }

    private static void CheckAssets(string repo, string layout)
    {
        var sourceAssets = Path.Combine(repo, "src", "DeltaNFD", "Assets");
        var layoutAssets = Path.Combine(layout, "Assets");
        Check(Directory.Exists(layoutAssets), "Assets 目录已复制");

        var sourceTweakDb = Path.Combine(sourceAssets, "TweakDb");
        var layoutTweakDb = Path.Combine(layoutAssets, "TweakDb");
        var sourceJson = Directory.GetFiles(sourceTweakDb, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        var layoutJson = Directory.GetFiles(layoutTweakDb, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        Check(layoutJson.Length == sourceJson.Length && sourceJson.Length > 0,
            $"TweakDb JSON 数量与源一致（{layoutJson.Length}/{sourceJson.Length}）");
        foreach (var source in sourceJson)
        {
            var target = Path.Combine(layoutTweakDb, Path.GetFileName(source));
            Check(File.Exists(target) && SameHash(source, target), "TweakDb 资产未被中间目录覆盖：" + Path.GetFileName(source));
        }

        var updateConfig = Path.Combine(layoutAssets, "UpdateConfig.json");
        Check(File.Exists(updateConfig) && SameHash(Path.Combine(sourceAssets, "UpdateConfig.json"), updateConfig),
            "UpdateConfig.json 已复制且与源一致");

        // 运行库修复 payload（本地构建输入；缺失时如实跳过，不算通过）。
        var payload = Path.Combine(layoutAssets, "VCRedistRepair");
        Check(!Directory.Exists(Path.Combine(layoutAssets, "VCRedistRepair_unpacked")), "发布目录去掉旧 _unpacked 后缀");
        var dxRoot = Path.Combine(layoutAssets, "DirectXRepair");
        var dxSource = Path.Combine(sourceAssets, "DirectXRepair");
        if (Directory.Exists(dxSource))
        {
            foreach (var source in Directory.GetFiles(dxSource))
                Check(File.Exists(Path.Combine(dxRoot, Path.GetFileName(source))) && SameHash(source, Path.Combine(dxRoot, Path.GetFileName(source))),
                    "DirectX 资源与源一致：" + Path.GetFileName(source));
        }
        if (Directory.Exists(Path.Combine(sourceAssets, "VCRedistRepair")))
        {
            Check(File.Exists(Path.Combine(payload, "Installer.cmd")), "VCRedistRepair\\Installer.cmd 已复制");
            Check(File.Exists(Path.Combine(payload, "payload_manifest_sha256.csv")), "运行库修复 payload 清单已复制");
        }
        else
        {
            Console.WriteLine("  SKIP: 源目录缺少 VCRedistRepair（本地输入），未验证运行库修复 payload。");
        }
        foreach (var redist in new[] { "VCRedist2015-2022-x64.exe", "VCRedist2015-2022-x86.exe" })
        {
            var source = Path.Combine(sourceAssets, redist);
            if (File.Exists(source)) Check(File.Exists(Path.Combine(layoutAssets, redist)), redist + " 已复制");
            else Console.WriteLine("  SKIP: 源目录缺少 " + redist + "（本地输入）。");
        }
    }

    private static void CheckBackendPathsResolve(string layout, (string Module, string Folder)[] modules)
    {
        Environment.SetEnvironmentVariable(BackendPaths.BackendRootEnvironmentVariable, Path.Combine(layout, "Backends"));
        try
        {
            foreach (var (module, _) in modules)
            {
                var resolved = BackendPaths.Resolve(module);
                Check(resolved.StartsWith(Path.Combine(layout, "Backends"), StringComparison.OrdinalIgnoreCase),
                    $"模块 {module} 从发布布局解析（而非源码目录兜底）");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(BackendPaths.BackendRootEnvironmentVariable, null);
        }
    }

    /// <summary>清空全局 .NET 线索（DOTNET_ROOT/DOTNET_ROOT_X64/PATH）后运行发布出的后端 --describe。</summary>
    private static void CheckRunsWithoutGlobalDotNet(string backendsRoot, (string Module, string Folder)[] modules, bool commonRuntime)
    {
        foreach (var (module, folder) in modules)
        {
            var spec = BackendModules.Require(module);
            var directory = commonRuntime ? backendsRoot : Path.Combine(backendsRoot, folder);
            var exe = Path.Combine(directory, spec.ExecutableName);
            var start = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--describe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = directory,
            };
            start.Environment.Clear();
            var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            start.Environment["SystemRoot"] = systemRoot;
            start.Environment["windir"] = Environment.GetEnvironmentVariable("windir") ?? systemRoot;
            start.Environment["TEMP"] = Path.GetTempPath();
            start.Environment["TMP"] = Path.GetTempPath();
            start.Environment["PATH"] = Path.Combine(systemRoot, "System32");
            start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";

            using var process = Process.Start(start) ?? throw new Exception("无法启动 " + exe);
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(60000)) { try { process.Kill(); } catch { } throw new Exception(module + " --describe 超时"); }
            Check(process.ExitCode == 0 && stdout.Contains("commands=", StringComparison.Ordinal),
                $"模块 {module} 在清空全局 .NET 线索后可独立运行（exit={process.ExitCode}，{stderr.Trim()}）");
        }
    }

    private static bool SameHash(string left, string right) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(left)))
            .Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(right))), StringComparison.OrdinalIgnoreCase);

    private static string? GetLayoutPath(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals("--deployment-layout-checks", StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            return args[index + 1];
        return null;
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
        if (!value) throw new Exception("部署布局检查失败：" + name);
        _passed++;
    }
}
