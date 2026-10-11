using DeltaNFD.Services;

internal static class RuntimeBulkUninstallChecks
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-runtime-bulk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(root, "isolated-test.marker"), id.ToString("D")); AppDataPaths.ConfigureIsolatedDataRoot(root, id);
        int passed = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); passed++; Console.WriteLine("PASS: " + name); }
        VcRedistEntry Entry(string name, string key = "A") => new() { DisplayName = name, Architecture = "x64", KeyName = key, UninstallKind = "Msi", DisplayVersion = "14.42.34433", UninstallString = "fixture-not-executed" };
        var good = Entry("Microsoft Visual C++ 2022 Redistributable (x64)");
        var old = Entry("Microsoft Visual C++ 2005 Redistributable", "B");
        var filtered = RuntimeBulkUninstall.Preview([good, old,
            Entry("Microsoft Visual C++ Debug Redistributable", "C"), Entry("Microsoft Visual C++ 2022 SDK", "D"),
            Entry("Microsoft Visual Studio Tools for Office Runtime", "E"), Entry(".NET Runtime", "F"), Entry("DirectX Runtime", "G")]);
        Check(filtered.Entries.Count == 2, "只列 C++ 可再发行组件，排除 Debug/SDK/VSTO/.NET/DirectX");
        Check(RuntimeBulkUninstall.Preview([old, good]).Fingerprint == filtered.Fingerprint, "清单顺序变化不导致错误指纹");
        Check(RuntimeBulkUninstall.Preview([good, withVersion()]).Fingerprint != filtered.Fingerprint, "版本/卸载登记变化会改变指纹");
        VcRedistEntry withVersion() => new() { DisplayName = old.DisplayName, Architecture = old.Architecture, KeyName = old.KeyName,
            UninstallKind = old.UninstallKind, DisplayVersion = "8.0.1", UninstallString = old.UninstallString };
        async Task<OperationResult> Run(Fake environment, string? fingerprint = null, CancellationToken token = default) =>
            await new RuntimeBulkUninstall(environment, new RuntimeRepairDiagnostics(Path.Combine(root, Guid.NewGuid().ToString("N"))))
                .RunAsync(fingerprint ?? RuntimeBulkUninstall.Preview(environment.Entries).Fingerprint, new Progress<string>(), token);
        var fake = new Fake([good, old]);
        var result = await Run(fake, "stale"); Check(!result.Success && fake.Executed.Count == 0, "未确认或清单变化不卸载");
        fake = new([good]) { Blocker = "防护开启" }; result = await Run(fake);
        Check(!result.Success && fake.Executed.Count == 0, "防护/预检阻塞不卸载");
        fake = new([good, old]); result = await Run(fake);
        Check(result.Success && fake.Executed.Count == 2 && fake.Scans >= 2, "全量卸载后复检零遗留才成功");
        fake = new([good, old]) { Response = new(1603, "fixture failure") }; result = await Run(fake);
        Check(!result.Success && fake.Executed.Count == 1, "一项失败停止后续条目");
        fake = new([good, old]) { Response = new(null, "fixture still running", OutcomeUnknown: true) }; result = await Run(fake);
        Check(!result.Success && result.OutcomeUnknown && fake.Executed.Count == 1, "事务未知保留证据，不继续或假报完成");
        fake = new([good, old]) { Response = new(3010, "fixture reboot") }; result = await Run(fake);
        Check(!result.Success && result.RequiresReboot && fake.Executed.Count == 1, "要求重启时停止后续，不自动重启");
        fake = new([good]) { KeepEntries = true }; result = await Run(fake);
        Check(!result.Success && result.IsPartial, "退出码成功但复检仍登记不算全量成功");
        fake = new([good]) { BlockOnSecond = true }; result = await Run(fake);
        Check(!result.Success && fake.Executed.Count == 0, "执行前再次预检变化拒绝操作");
        fake = new([good]); using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); result = await Run(fake, token: cancellation.Token);
        Check(!result.Success && fake.Executed.Count == 0, "取消只停止后续步骤，未启动卸载");
        var duplicateRejected = false;
        try { _ = RuntimeBulkUninstall.Preview([good, good]); } catch (InvalidDataException) { duplicateRejected = true; }
        Check(duplicateRejected, "重复登记拒绝创建卸载清单");
        Console.WriteLine($"Runtime bulk uninstall checks: {passed} passed. All installation/removal simulated, state isolated.");
    }
    private sealed class Fake(List<VcRedistEntry> entries) : IRuntimeBulkUninstallEnvironment
    {
        public List<VcRedistEntry> Entries { get; } = [.. entries];
        public List<VcRedistEntry> Executed { get; } = [];
        public RuntimeStepResult Response { get; init; } = new(0);
        public bool KeepEntries { get; init; }
        public string? Blocker { get; init; }
        public bool BlockOnSecond { get; init; }
        public int Scans, Preflights;
        public Task<string?> PreflightAsync(CancellationToken token) => Task.FromResult(BlockOnSecond && ++Preflights > 1 ? "fixture changed" : Blocker);
        public Task<List<VcRedistEntry>> ScanAsync(CancellationToken token) { Scans++; return Task.FromResult(new List<VcRedistEntry>(Entries)); }
        public Task<RuntimeStepResult> ExecuteAsync(VcRedistEntry entry, IProgress<string>? progress, CancellationToken token)
        { Executed.Add(entry); if (!KeepEntries && Response.Success) Entries.Remove(entry); return Task.FromResult(Response); }
    }
}
