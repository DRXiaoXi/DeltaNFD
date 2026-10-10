using System.Diagnostics;
using DeltaNFD.Services;

internal static class RuntimeMsiInstallerChecks
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        var step = new RuntimeRepairStep("test-msi", "2005 (x64)", "2005 (x64)", "msi", "2005/x64/vcredist.msi");
        async Task<RuntimeStepResult> Run(Fake fake, CancellationToken token = default, TimeSpan? timeout = null, bool skipHealthy = false, bool strong = false) =>
            await new RuntimeMsiInstaller(fake, new RuntimeRepairDiagnostics(root)).RunAsync("fixture.msi", step,
                Path.Combine(root, Guid.NewGuid().ToString("N") + ".log"), timeout ?? TimeSpan.FromSeconds(5), null, token,
                skipHealthyInstalled: skipHealthy, forceSameVersion: strong);
        var installed = new Fake();
        var healthy = new Fake { Product = Fake.Existing() with { Version = Fake.Payload().Version } };
        check((await Run(healthy, skipHealthy: true)).Skipped && healthy.Commands.Count == 0,
            "兼容性修复跳过同版本且 DLL 正常的已装产品，不重复执行 MSI");
        var strongHealthy = new Fake { Product = Fake.Existing() with { Version = Fake.Payload().Version } };
        check((await Run(strongHealthy, skipHealthy: false, strong: true)).Success && strongHealthy.Commands.Count == 1
            && strongHealthy.Commands[0].Contains("REINSTALL=ALL") && strongHealthy.Commands[0].Contains("REINSTALLMODE=vemus"),
            "强力模式覆盖同版本文件但不使用会降级较新文件的 a 策略");
        check((await Run(installed)).Success && installed.Commands.Count == 1
            && installed.Commands[0].Contains("REINSTALL=ALL") && installed.Commands[0].Contains("REINSTALLMODE=vomus"),
            "首次默认修复：已有同一 2005 MSI 直接用修复参数，一次完成，不先触发 1638");
        var absent = new Fake { Product = new(Fake.Code, -1), AfterInstall = Fake.Existing() with { Version = Fake.Payload().Version } };
        check((await Run(absent)).Success && absent.Commands.Count == 1 && !absent.Commands[0].Contains("REINSTALL=ALL"),
            "首次缺失分支：未安装时普通安装，不能给不存在的产品强加修复参数");
        var race = new Fake { Product = new(Fake.Code, -1), OnConflict = Fake.Existing(), Results = new([new(1638), new(0)]) };
        check((await Run(race)).Success && race.Commands.Count == 2 && race.Commands[1].Contains("REINSTALL=ALL"),
            "扫描后产品出现：1638 后身份匹配才允许一次修复重试");
        check(race.Budgets[1] <= race.Budgets[0], "修复重试共享单步剩余预算，不能重新获得完整预算");
        var missingIdentity = new Fake { Product = new(Fake.Code, -1), Results = new([new(1638)]) };
        check(!(await Run(missingIdentity)).Success && missingIdentity.Commands.Count == 1, "1638 后仍未找到同一产品，保持失败而非强行重装");
        var repeat = new Fake { Product = new(Fake.Code, -1), OnConflict = Fake.Existing(), Results = new([new(1638), new(1638)]) };
        check(!(await Run(repeat)).Success && repeat.Commands.Count == 2, "修复再次冲突就停止，不循环安装");
        foreach (var bad in new[] { Fake.Existing() with { Architecture = "x86" }, Fake.Existing() with { Publisher = "Other" },
            Fake.Existing() with { Name = "Other product" }, Fake.Existing() with { State = 1 }, Fake.Existing() with { Version = "unknown" } })
        {
            var fake = new Fake { Product = bad };
            check(!(await Run(fake)).Success && fake.Commands.Count == 0, "MSI 身份/版本未知或不匹配时不启动：" + bad);
        }
        var newer = new Fake { Product = Fake.Existing() with { Version = "8.0.62000.0" } };
        var retained = await Run(newer);
        check(retained.Success && retained.Skipped && retained.RetainedNewer && newer.Commands.Count == 0 && newer.FileChecks == 1,
            "较新 MSI 核对文件后保留，不回退或执行旧安装器");
        var strongNewer = new Fake { Product = Fake.Existing() with { Version = "8.0.62000.0" } };
        check((await Run(strongNewer, strong: true)).RetainedNewer && strongNewer.Commands.Count == 0, "强力模式仍保留较新产品");
        var badFiles = new Fake { FilesValid = false };
        check(!(await Run(badFiles)).Success && badFiles.FileChecks == 1, "MSI 返回 0 但 DLL 复核失败不能显示修复完成");
        var oldVersion = new Fake { KeepOldVersion = true };
        check(!(await Run(oldVersion)).Success && oldVersion.FileChecks == 0,
            "旧 8.0.61000 产品返回 0 但未升级到 8.0.61186 时，不能用版本登记冒充升级成功");
        var reboot = new Fake { FilesValid = false, Results = new([new(3010)]) };
        var pending = await Run(reboot);
        check(!pending.Success && pending.RequiresReboot && !pending.PostcheckPassed, "重启后才能完成文件替换时保留待核对状态，不伪报通过");
        var unknown = new Fake { Results = new([new(1638, OutcomeUnknown: true)]) };
        check((await Run(unknown)).OutcomeUnknown && unknown.Commands.Count == 1 && unknown.FileChecks == 0, "安装结果未知时不重试、不执行成功复核");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); var fake = new Fake(); var cancelled = false;
            try { await Run(fake, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && fake.Commands.Count == 0, "起始取消不启动 MSI");
        }
        using (var cancel = new CancellationTokenSource())
        {
            var fake = new Fake { AfterRun = cancel.Cancel };
            check(!(await Run(fake, cancel.Token)).Success && fake.FileChecks == 0,
                "安装返回后取消复核，不能把默认 PostcheckPassed 当作实际验证成功");
        }
        var expired = new Fake();
        check(!(await Run(expired, timeout: TimeSpan.Zero)).Success && expired.Commands.Count == 0, "耗尽预算不启动 MSI");
        var wrongPackage = new Fake { Package = Fake.Payload() with { Architecture = "x86" } };
        check(!(await Run(wrongPackage)).Success && wrongPackage.Commands.Count == 0, "所选 x64 分支不能执行 x86 包");

        var filePath = Path.Combine(root, "probe.dll");
        File.Copy(Environment.ProcessPath!, filePath);
        var fileVersion = FileVersionInfo.GetVersionInfo(filePath);
        var expected = new Version(fileVersion.FileMajorPart, fileVersion.FileMinorPart, fileVersion.FileBuildPart, fileVersion.FilePrivatePart);
        var package = Fake.Payload() with { Files = [new(Fake.Code, "probe.dll", "probe.dll", expected)] };
        var backend = new RuntimeMsiInstallBackend(new RuntimeRepairProcessRunner(root, () => false), (_, _) => filePath);
        check(backend.VerifyFiles(package).Success, "真实文件复核读取自有 PE 的版本和架构，不执行文件");
        var sourceRoot = Path.Combine(root, "verified-source");
        Directory.CreateDirectory(sourceRoot); File.Copy(filePath, Path.Combine(sourceRoot, "probe.dll"));
        var hashed = RuntimeMsiInstallBackend.WithSourceHashes(package, sourceRoot);
        check(backend.VerifyFiles(hashed).Success, "同版本文件与已校验源文件哈希相符才通过强力复核");
        var bytes = File.ReadAllBytes(filePath);
        var pe = BitConverter.ToInt32(bytes, 0x3c);
        var section = pe + 24 + BitConverter.ToUInt16(bytes, pe + 20);
        bytes[BitConverter.ToInt32(bytes, section + 20) + 16] ^= 0x5a;
        File.WriteAllBytes(filePath, bytes);
        check(!backend.VerifyFiles(hashed).Success, "保持 PE 头、版本不变但损坏代码字节的同版本文件不再误报成功");
        File.Copy(Path.Combine(sourceRoot, "probe.dll"), filePath, true);
        check(!backend.VerifyFiles(package with { Architecture = "x86" }).Success, "真实 PE 架构不符会拒绝");
        check(!backend.VerifyFiles(package with { Files = [new(Fake.Code, "probe.dll", "probe.dll", new Version(99, 0))] }).Success,
            "真实 DLL 版本低于包要求会拒绝");
        File.Delete(filePath);
        check(!backend.VerifyFiles(package).Success, "登记存在但 DLL 文件丢失会拒绝");
        File.WriteAllText(filePath, "not a PE");
        check(!backend.VerifyFiles(package).Success, "损坏的 DLL PE 头会拒绝");
    }

    private sealed class Fake : IRuntimeMsiInstallBackend
    {
        internal const string Code = "{AD8A2FA1-06E7-4B0D-927D-6E54B3D31028}";
        internal static RuntimeMsiPackage Payload() => new(Code, "Microsoft Visual C++ 2005 Redistributable (x64)",
            "Microsoft Corporation", "8.0.61186", "x64", [new(Code, "msvcr80.dll", "msvcr80.dll", new Version(8, 0, 50727, 6229))]);
        internal static RuntimeMsiProduct Existing() => new(Code, 5, Payload().Name, Payload().Publisher, "8.0.61000.0", "x64");
        internal RuntimeMsiPackage Package = Payload();
        internal RuntimeMsiProduct Product = Existing();
        internal RuntimeMsiProduct? OnConflict, AfterInstall;
        internal Queue<RuntimeStepResult> Results = new();
        internal List<string[]> Commands = [];
        internal List<TimeSpan> Budgets = [];
        internal bool FilesValid = true;
        internal bool KeepOldVersion;
        internal int FileChecks;
        internal Action? AfterRun;
        public RuntimeMsiPackage ReadPackage(string path) => Package;
        public RuntimeMsiProduct ReadProduct(string product) => Product;
        public OperationResult VerifyFiles(RuntimeMsiPackage package) { FileChecks++; return FilesValid ? OperationResult.Ok("fixture files verified") : OperationResult.Fail("fixture DLL missing"); }
        public Task<RuntimeStepResult> RunAsync(ProcessStartInfo info, string label, TimeSpan timeout, IProgress<string>? progress, CancellationToken token)
        {
            Commands.Add(info.ArgumentList.ToArray()); Budgets.Add(timeout);
            var result = Results.Count > 0 ? Results.Dequeue() : new RuntimeStepResult(0);
            if (result.ExitCode == 1638 && OnConflict is not null) Product = OnConflict;
            if (result.ExitCode is 0 or 3010 && AfterInstall is not null) Product = AfterInstall;
            else if (result.ExitCode is 0 or 3010 && !KeepOldVersion) Product = Product with { Version = Package.Version };
            AfterRun?.Invoke();
            return Task.FromResult(result);
        }
    }
}
