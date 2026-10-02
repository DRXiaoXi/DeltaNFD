using System.Reflection;
using System.Text;
using DeltaNFD.Services;

internal static class RuntimeRepairChecks
{
    internal static async Task RunAsync()
    {
        var clean = new VcRedistScanReport { All = new(), Abnormal = new(), Suboptimal = new(), Advice = "" };
        var missing = new VcRedistScanReport { All = new(), Abnormal = new(), Advice = "", Suboptimal = new()
        {
            new() { Branch = "2005 (x64)", InstalledVersion = "", RecommendedVersion = "8.0.61186" },
            new() { Branch = "2008 (x86)", InstalledVersion = "", RecommendedVersion = "9.0.30729.7523" },
        } };
        var good = new RuntimePackageResult("2005\\x64\\vcredist.msi", 0, "msi-1.log", "");
        var bad = new RuntimePackageResult("2005\\x64\\vcredist.msi", 1603, "msi-1.log", "Error 1935 HRESULT: 0x80070005");
        Assert(RuntimeRepairDiagnostics.BuildResult(0, new[] { good }, Array.Empty<string>(), clean, false, "fixture", "").Success,
            "逐包及复检通过才成功");
        var result = RuntimeRepairDiagnostics.BuildResult(0, new[] { bad }, Array.Empty<string>(), missing, false, "fixture", "");
        Assert(!result.Success && result.Message.Contains("1603") && result.Message.Contains("0x80070005")
            && result.Message.Contains("2008 (x86)"), "脚本 0 不能覆盖旧包失败及复检缺失");
        Assert(!RuntimeRepairDiagnostics.BuildResult(0, new[] { good }, Array.Empty<string>(), missing, false, "fixture", "").Success,
            "包返回成功但复检缺失仍失败");
        Assert(!RuntimeRepairDiagnostics.BuildResult(0, new[] { good }, Array.Empty<string>(), null, false, "fixture", "").Success,
            "复检异常不能成功");
        Assert(!RuntimeRepairDiagnostics.BuildResult(1, new[] { good }, Array.Empty<string>(), clean, false, "fixture", "").Success,
            "脚本失败不能成功");
        Assert(!RuntimeRepairDiagnostics.BuildResult(0, new[] { good }, new[] { "v14 安装失败" }, clean, false, "fixture", "").Success,
            "v14 或卸载失败不得被复检掩盖");
        var reboot = good with { ExitCode = 3010 };
        Assert(RuntimeRepairDiagnostics.BuildResult(0, new[] { reboot }, Array.Empty<string>(), clean, false, "fixture", "").RequiresReboot,
            "3010 聚合重启状态");
        result = RuntimeRepairDiagnostics.BuildResult(0, new[] { bad, reboot }, Array.Empty<string>(), missing, false, "fixture", "");
        Assert(!result.Success && result.RequiresReboot, "失败和需重启必须同时保留");
        Assert(!new RuntimePackageResult("test.msi", 1638, "", "").Success, "MSI 版本冲突不能直接当作安装成功");

        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD_RepairChecks_" + Guid.NewGuid().ToString("N"));
        var diagnostics = new RuntimeRepairDiagnostics(root);
        try
        {
            var dir = diagnostics.DirectoryPath;
            File.WriteAllText(Path.Combine(dir, "msi-1.log"), "Error 1935 HRESULT: 0x80070005", Encoding.Unicode);
            File.WriteAllText(Path.Combine(dir, "install-results.txt"), "1603|2005\\x64\\vcredist.msi|msi-1.log\n", Encoding.UTF8);
            File.WriteAllText(Path.Combine(dir, "completed.txt"), "1\n", Encoding.UTF8);
            var packages = diagnostics.ReadPackageResults();
            Assert(packages.Count == 1 && !packages[0].Success && packages[0].ErrorDetails.Contains("0x80070005"),
                "逐包结果与 Unicode MSI 详细错误读取");
            File.WriteAllText(Path.Combine(dir, "completed.txt"), "2");
            ExpectInvalid(diagnostics, "完成标记数量错误");
            File.Delete(Path.Combine(dir, "completed.txt"));
            ExpectInvalid(diagnostics, "流程中断缺少完成标记");
            File.WriteAllText(Path.Combine(dir, "completed.txt"), "1");
            File.WriteAllText(Path.Combine(dir, "install-results.txt"), "0|test.msi|..\\outside.log");
            ExpectInvalid(diagnostics, "日志路径越界拒绝");
            File.WriteAllText(Path.Combine(dir, "install-results.txt"), "0|..\\test.msi|msi-1.log");
            ExpectInvalid(diagnostics, "包路径越界拒绝");

            var content = new string('a', 6000) + "\nEND";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
            using var reader = new StreamReader(stream, Encoding.UTF8);
            await diagnostics.CaptureOutputAsync(reader, "output-fixture.log", CancellationToken.None);
            var output = File.ReadAllText(Path.Combine(dir, "output-fixture.log"));
            Assert(output.StartsWith(new string('a', 6000)) && output.Contains("END"), "输出不再仅保留最后 4000 字符");
            var sourceScript = Path.Combine(Directory.GetCurrentDirectory(), "src", "DeltaNFD", "Assets", "VCRedistRepair_unpacked", "Installer.cmd");
            if (File.Exists(sourceScript)) await CheckBatchHelperAsync(diagnostics, sourceScript, root);
        }
        finally
        {
            // 仅删除本测试创建并确认位于临时根目录内的专属目录。
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("测试清理路径越界");
            Directory.Delete(root, true);
        }

        var assets = Path.Combine(Directory.GetCurrentDirectory(), "src", "DeltaNFD", "Assets");
        if (Directory.Exists(Path.Combine(assets, "VCRedistRepair_unpacked")))
        {
            var verify = typeof(RuntimeGuardService).GetMethod("VerifyRepairPackage", BindingFlags.NonPublic | BindingFlags.Static)!;
            var check = (OperationResult)verify.Invoke(null, new object?[] { assets, Path.Combine(assets, "VCRedistRepair_unpacked"), null })!;
            Assert(check.Success, "更新后的脚本及 791 项安装数据哈希必须通过：" + check.Message);
            var script = File.ReadAllText(Path.Combine(assets, "VCRedistRepair_unpacked", "Installer.cmd"));
            Assert(script.Contains("/L*V") && script.Contains("completed.txt") && script.Contains("exit /b 1"), "安装脚本必须保留诊断和失败汇总");
        }
        else Console.WriteLine("SKIP: payload integrity (private runtime payload is not present in this checkout).");
        Console.WriteLine("Runtime repair checks passed: partial failure, postcheck, reboot, missing diagnostics, traversal, UTF-16 MSI logs, full output, payload integrity. No real installer was executed.");
    }

    private static async Task CheckBatchHelperAsync(RuntimeRepairDiagnostics diagnostics, string sourceScript, string root)
    {
        var script = File.ReadAllText(sourceScript);
        var begin = script.IndexOf(":dnfInstall\n", StringComparison.Ordinal);
        if (begin < 0) begin = script.IndexOf(":dnfInstall\r\n", StringComparison.Ordinal);
        var end = script.IndexOf("\n:chVBC", begin, StringComparison.OrdinalIgnoreCase);
        Assert(begin >= 0 && end > begin, "诊断子程序必须可定位");
        var helper = script[begin..end];
        var fakeDirectory = Path.Combine(root, "fake installer 中文");
        Directory.CreateDirectory(fakeDirectory);
        foreach (var source in Directory.GetFiles(AppContext.BaseDirectory))
            File.Copy(source, Path.Combine(fakeDirectory, Path.GetFileName(source)), true);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "BackendSmokeTest.exe"), Path.Combine(fakeDirectory, "msiexec.exe"));
        var dir = diagnostics.DirectoryPath;
        File.WriteAllText(Path.Combine(dir, "install-results.txt"), "");
        var fixture = Path.Combine(root, "fixture.cmd");
        File.WriteAllText(fixture, "@echo off\r\nchcp 65001 >nul\r\nsetlocal EnableDelayedExpansion\r\n"
            + "set dnfcount=0\r\nset dnffailed=0\r\nset msiprop=--runtime-installer-fixture\r\nset verbshort=\r\n"
            + "call :dnfInstall \"2005\\x64\\vcredist.msi\"\r\ncall :dnfInstall \"2008\\x86\\vc_red.msi\"\r\n"
            + ">\"!DELTANFD_REPAIR_LOG_DIR!\\completed.txt\" echo !dnfcount!\r\n"
            + "if !dnffailed! neq 0 exit /b 1\r\nexit /b 0\r\n" + helper, new UTF8Encoding(false));
        var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /s /c \"\"{fixture}\"\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["SysPath"] = fakeDirectory;
        psi.Environment["DELTANFD_REPAIR_LOG_DIR"] = dir;
        using var process = System.Diagnostics.Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
        catch { process.Kill(true); await process.WaitForExitAsync(); throw; }
        var text = await stdout + await stderr;
        Assert(process.ExitCode == 1, "真实批处理子程序必须汇总失败：" + text);
        var packages = diagnostics.ReadPackageResults();
        Assert(packages.Count == 2 && packages[0].ExitCode == 1603 && packages[1].ExitCode == 3010,
            "失败后继续、3010 原样记录、中文和空格日志路径");
    }

    private static void ExpectInvalid(RuntimeRepairDiagnostics diagnostics, string message)
    {
        try { diagnostics.ReadPackageResults(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
