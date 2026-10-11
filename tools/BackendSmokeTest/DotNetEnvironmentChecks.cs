using System.Security.Cryptography;
using System.Text.Json;
using DeltaNFD.Services;

internal static class DotNetEnvironmentChecks
{
    internal static async Task RunAsync()
    {
        int count = 0;
        void Check(bool pass, string name) { if (!pass) throw new Exception(name); count++; Console.WriteLine("PASS: " + name); }
        void Reject(Action run, string name)
        {
            try { run(); } catch (InvalidDataException) { Check(true, name); return; }
            throw new Exception("Expected rejection: " + name);
        }
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-dotnet-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sourceRoot = DotNetEnvironmentService.SystemRoots().First();
            var runtimeSource = Directory.GetDirectories(Path.Combine(sourceRoot, "shared", "Microsoft.NETCore.App"))
                .Where(p => Version.TryParse(Path.GetFileName(p), out var v) && v.Major == 8)
                .OrderByDescending(p => Version.Parse(Path.GetFileName(p))).First();
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

            var hash = new string('a', 128);
            object Runtime(string v, string rid, string url, string h) => new { runtime = new { version = v,
                files = new[] { new { rid, name = "dotnet-runtime-win-x64.exe", url, hash = h } } } };
            var official = "https://builds.dotnet.microsoft.com/dotnet/Runtime/8.0.31/dotnet-runtime-8.0.31-win-x64.exe";
            string Metadata(params object[] releases) => JsonSerializer.Serialize(new { releases });
            var chosen = DotNetRepairService.SelectInstaller(Metadata(Runtime("8.0.30", "win-x64", official, hash),
                Runtime("8.0.31", "win-x64", official, hash), Runtime("10.0.1", "win-x64", official, hash),
                Runtime("8.0.99-preview.1", "win-x64", official, hash)));
            Check(chosen.Version == new Version(8, 0, 31), "只选 .NET 8 最新稳定补丁");
            Reject(() => DotNetRepairService.SelectInstaller(Metadata(Runtime("8.0.31", "win-x86", official, hash))), "拒绝 x86 安装包");
            Reject(() => DotNetRepairService.SelectInstaller(Metadata(Runtime("8.0.31", "win-x64", "https://builds.dotnet.microsoft.com.evil/a.exe", hash))), "拒绝伪装微软域名");
            Reject(() => DotNetRepairService.SelectInstaller(Metadata(Runtime("8.0.31", "win-x64", official.Replace("https:", "http:"), hash))), "拒绝明文 HTTP");
            Reject(() => DotNetRepairService.SelectInstaller(Metadata(Runtime("8.0.31", "win-x64", official, "bad"))), "缺失合法 SHA512 拒绝启动");
            using var bytes = new MemoryStream(new byte[] { 1, 2, 3 });
            Check(DotNetRepairService.VerifyHash(bytes, Convert.ToHexString(SHA512.HashData(bytes.ToArray()))), "SHA512 正确通过");
            Check(!DotNetRepairService.VerifyHash(bytes, hash), "安装器篡改不通过");
            Check(DotNetRepairService.ExitMessage(1638).Contains("不能视为安装成功"), "版本冲突不是成功");
            Check(DotNetRepairService.ExitMessage(3010).Contains("重启"), "重启要求单独说明");
            Check(DotNetRepairService.ExitMessage(1602).Contains("取消"), "用户取消单独说明");
            Check(DotNetRepairService.ExitMessage(1618).Contains("另一个安装任务"), "安装忙单独说明");
            var offlineRoot = Path.Combine(root, "offline");
            var offlineFolder = Path.Combine(offlineRoot, "Assets", "DotNetRepair");
            Directory.CreateDirectory(offlineFolder);
            Check(DotNetRepairService.FindBundledInstaller(offlineRoot) is null, "缺少离线包时允许选择官方下载路径");
            File.WriteAllBytes(Path.Combine(offlineFolder, BundledDotNetRuntime.FileName), [1, 2, 3]);
            Reject(() => DotNetRepairService.FindBundledInstaller(offlineRoot), "已存在但损坏的离线包拒绝启动，不静默绕过");
            Check(DotNetRepairService.BundledInstaller.Version == new Version(8, 0, 31)
                && DotNetRepairService.AllowedDownload(DotNetRepairService.BundledInstaller.Url), "离线版本固定为微软 .NET 8 x64 正式版");
            var payloadRoot = Path.Combine(Directory.GetCurrentDirectory(), "src", "DeltaNFD");
            if (File.Exists(Path.Combine(payloadRoot, "Assets", "DotNetRepair", BundledDotNetRuntime.FileName)))
                Check(DotNetRepairService.FindBundledInstaller(payloadRoot) is not null, "真实离线安装器通过编译内固定 SHA512（仅验证，不执行）");
            var snapshot = Path.Combine(Directory.GetCurrentDirectory(), "_buildcheck", "dotnet8-official-metadata.json");
            if (File.Exists(snapshot))
                Check(DotNetRepairService.SelectInstaller(File.ReadAllText(snapshot)).Version.Major == 8, "实际微软元数据快照可解析（不下载/执行安装器）");
            Console.WriteLine($".NET environment checks: {count} passed. Read-only system lookup; all writes isolated; no installer executed.");
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
