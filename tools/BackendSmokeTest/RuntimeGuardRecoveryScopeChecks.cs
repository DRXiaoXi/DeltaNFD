using DeltaNFD.Services;

internal static class RuntimeGuardRecoveryScopeChecks
{
    internal static void Run()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        RuntimeGuardStatus Status(int managed = 0, int external = 0, int total = 3, bool applied = false,
            bool ifeoPending = false, bool ue4Pending = false, bool denied = false, string issue = "", bool outsideScope = false) => new()
        {
            InstalledRedists = [], IfeoCount = managed + external, IfeoManagedCount = managed, IfeoExternalCount = external,
            ScopeManagedCount = outsideScope ? 0 : managed, ScopeBlockedCount = outsideScope ? 0 : managed + external,
            IfeoTotal = total, IfeoApplied = applied, IfeoRestorePending = ifeoPending,
            Ue4RestorePending = ue4Pending, Ue4PrereqDenied = denied, Ue4PrereqFound = denied,
            GuardIssue = issue, Mode = total == 11 ? RuntimeGuardMode.Full : RuntimeGuardMode.BasicV14,
        };
        var clean = Status();
        Check(RuntimeGuardRecoveryReport.Build(clean) is { Complete: true } snapshot && !snapshot.RecoveryScopes.Single().Available, "无残留时不伪造恢复义务");
        Check(RuntimeGuardRecoveryReport.BuildOutcome(OperationResult.Ok("closed"), clean).Verified, "关闭成功且完整复检无残留才通过");
        foreach (var status in new[] { Status(1), Status(2), Status(1,total:11), Status(10,total:11), Status(3,applied:true),
            Status(11,total:11,applied:true), Status(8,outsideScope:true), Status(ifeoPending:true), Status(ue4Pending:true), Status(denied:true) })
        {
            var report = RuntimeGuardRecoveryReport.Build(status);
            Check(report.Complete && report.RecoveryScopes.Single().Available, "部分/全部/范围外本工具拦截及旧残留均提供恢复入口");
            Check(!RuntimeGuardRecoveryReport.BuildOutcome(OperationResult.Ok("claimed closed"),status).Verified, "返回成功但仍有残留不能复核通过");
        }
        Check(RuntimeGuardRecoveryReport.Build(Status(1)).Summary.Contains("部分"), "部分生效不再写成未生效、无需恢复");
        var external = Status(external:1);
        var externalReport = RuntimeGuardRecoveryReport.Build(external);
        Check(!externalReport.RecoveryScopes.Single().Available && externalReport.RecoveryScopes.Single().Reason.Contains("强制清理"), "外部拦截仍需显式强制清理，不扩大普通恢复权限");
        Check(!RuntimeGuardRecoveryReport.BuildOutcome(OperationResult.Ok("ours removed"),external).Verified, "外部拦截还在时不宣称所有拦截已消失");
        Check(RuntimeGuardRecoveryReport.Build(Status(1,1)).RecoveryScopes.Single().Available, "混合拦截仍允许先恢复本工具项");
        var unknown = Status(issue:"ACL could not be read");
        Check(!RuntimeGuardRecoveryReport.Build(unknown).Complete && !RuntimeGuardRecoveryReport.BuildOutcome(OperationResult.Ok("closed"),unknown).Verified, "读取不完整不能冒充复核正常");
        Check(RuntimeGuardRecoveryReport.Build(Status(1,issue:"state damaged")).RecoveryScopes.Single().Available, "已知部分拦截即使有读取提醒仍提供关闭重试");
        Check(!RuntimeGuardRecoveryReport.BuildOutcome(OperationResult.Fail("close failed"),clean).Verified, "关闭失败不能因快照干净改成成功");
        Check(!RuntimeGuardRecoveryReport.Build(Status(managed:-1)).Complete, "无效计数不作为完整证据");
        var source = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "src/DeltaNFD.Backend.Runtime/RuntimeCommands.cs"));
        Check(source.Contains("RuntimeGuardRecoveryReport.BuildOutcome") && source.Contains("RuntimeGuardRecoveryReport.Build(await"), "真实后端使用本次受测的状态判定");
        Check(!source.Contains("State.StartsWith(\"拦截已生效\""), "恢复复核不再靠显示文案前缀");
        Console.WriteLine($"运行库防护恢复范围检查通过：{count} 项；全部合成状态，无系统改写。");
    }
}
