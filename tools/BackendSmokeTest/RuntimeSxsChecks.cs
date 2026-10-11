using System.Diagnostics;
using DeltaNFD.Services;

internal static class RuntimeSxsChecks
{
    internal static void Run(string root, Action<bool, string> check)
    {
        var identity = new RuntimeSxsIdentity("Microsoft.VC90.CRT", "9.0.30729.7523", "amd64", "1fc8b3b9a1e18e3b", null);
        var manifest = RuntimeSxsResolver.BuildManifest(identity);
        check(manifest.Contains("Microsoft.VC90.CRT") && manifest.Contains("processorArchitecture=\"amd64\""), "SxS 需求来自已验证的程序集身份与架构");
        foreach (var bad in new[] { identity with { Name = "Unknown.Runtime" }, identity with { Architecture = "arm64" }, identity with { PublicKeyToken = "fake" }, identity with { Version = "bad" } })
        {
            bool rejected = false; try { RuntimeSxsResolver.Validate(bad); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "未知程序集身份、架构、签名标识或版本拒绝解析");
        }
        bool escaped = false; try { RuntimeSxsResolver.ResolveDirectory(root, "../escape"); } catch (IOException) { escaped = true; }
        check(escaped, "解析目录越界拒绝，不猜 DLL 路径");
        var directory = Path.Combine(root, "sxs-fixture"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "probe.dll"); File.Copy(Environment.ProcessPath!, path);
        var vi = FileVersionInfo.GetVersionInfo(path);
        var version = new Version(vi.FileMajorPart, vi.FileMinorPart, vi.FileBuildPart, vi.FilePrivatePart);
        var regular = new RuntimeMsiFile(Guid.NewGuid().ToString("B"), "probe.dll", "probe.dll", version) { Condition = "legacy-platform" };
        var sxs = regular with { Component = Guid.NewGuid().ToString("B"), Assembly = identity, Condition = "" };
        var package = new RuntimeMsiPackage(Guid.NewGuid().ToString("B"), "fixture", "Microsoft Corporation", "9.0", "x64", [regular, sxs]);
        var runner = new RuntimeRepairProcessRunner(root, () => false);
        RuntimeMsiInstallBackend Backend(int state) => new(runner, (_, _) => throw new RuntimeComponentPathException(regular.Component, state), assemblyDirectory: _ => directory);
        check(Backend(-7).VerifyFiles(package).Success, "条件禁用旧组件由实际核验的同版 SxS DLL 覆盖");
        check(!Backend(-1).VerifyFiles(package).Success, "UNKNOWN 普通组件不能被当作条件禁用而跳过");
        check(!Backend(-7).VerifyFiles(package with { Files = [regular with { Condition = "" }, sxs] }).Success, "无平台条件的组件缺失不得忽略");
        check(!Backend(-7).VerifyFiles(package with { Files = [regular] }).Success, "无已验证替代程序集时 NOTUSED 仍不能通过");
        File.WriteAllText(path, "broken");
        check(!Backend(-7).VerifyFiles(package).Success, "替代 DLL 损坏不能让条件组件复检通过");
    }
}
