using System.Text.Json;
using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

internal static class BackendRecoveryGuidanceChecks
{
    internal static void Run()
    {
        var count = 0;
        void Check(bool ok, string why) { if (!ok) throw new IOException(why); count++; }
        var record = new BackendUnconfirmedStore.Review(BackendModules.System, "original-fingerprint", "original record", true)
            { CanRestore = true, Command = "system.future.unknown", Reason = "future-unrecognized-error" };
        ReconcileSnapshot Snapshot(string module, bool complete = true, bool available = true) => new()
        {
            Module = module, Complete = complete, Summary = "fixture state",
            RecoveryScopes = [new() { Id = "tweaks", Description = "fixture backup", Available = available }],
        };
        string Text(BackendRecoveryAdvice advice) => advice.Title + advice.Explanation + advice.Steps;
        var snapshot = Snapshot(BackendModules.System);
        Check(Text(BackendRecoveryGuidance.ForReview(record, snapshot)).Contains("按可信备份恢复"), "trusted backup remains the preferred recovery path");
        var noBackup = BackendRecoveryGuidance.ForReview(record, Snapshot(BackendModules.System, available: false));
        Check(noBackup.Title.Contains("没有可用") && noBackup.Steps.Contains("确认保留当前状态"), "no backup explains an explicit manual choice");
        Check(noBackup.Steps.Contains("不会还原") && noBackup.Steps.Contains("暂不处理"), "manual acknowledgement is not presented as restoration");
        var unreadable = record with { Fingerprint = "" };
        Check(BackendRecoveryGuidance.ForReview(unreadable, snapshot).Title == "操作记录无法读取", "corrupt or unreadable evidence has its own guidance");
        Check(!BackendRecoveryGuidance.ForReview(unreadable, snapshot).Steps.Contains("确认保留当前状态"), "corrupt evidence is never advised for acknowledgement");
        var live = record with { CanAcknowledge = false, CanRestore = false, ChildrenAlive = true };
        Check(BackendRecoveryGuidance.ForReview(live, snapshot).Title == "关联程序仍未结束", "live child blocks backup advice even with a stale complete snapshot");
        Check(Text(BackendRecoveryGuidance.ForReview(live, snapshot)).Contains("勿按名单强制结束"), "no blanket process termination advice");
        var unknown = live with { ChildrenAlive = false, ChildrenUnknown = true };
        var unknownAdvice = BackendRecoveryGuidance.ForReview(unknown, snapshot);
        Check(unknownAdvice.Title == "执行者状态尚未确认" && unknownAdvice.Steps.Contains("Windows 的“重启”"), "unknown process state has a bounded next action");
        Check(unknownAdvice.Steps.Contains("不代表设置已还原"), "reboot advice does not claim restoration");
        Check(BackendRecoveryGuidance.ForReview(unknown with { ChildrenUnknown = false }, null).Title == "执行者状态尚未确认", "unconfirmed backend identity is also covered");
        Check(BackendRecoveryGuidance.ForReview(record, snapshot, "read failed").Title == "核对暂时无法完成", "failed refresh does not reuse stale recovery choices");
        var ace = record with { Module = BackendModules.Ace };
        Check(BackendRecoveryGuidance.ForReview(ace, Snapshot(BackendModules.Ace, complete: false)).Title == "核对暂时无法完成", "ACE incomplete scan does not suggest acknowledgement");
        var runtime = record with { Module = BackendModules.Runtime, Command = BackendCommands.RuntimeRepair };
        var installAdvice = BackendRecoveryGuidance.ForReview(runtime, null);
        Check(installAdvice.Title.Contains("专门核对") && installAdvice.Steps.Contains("建立重启核对点"), "installation keeps its dedicated transaction route");
        Check(!installAdvice.Steps.Contains("确认保留当前状态"), "installation is not bypassed by generic guidance");
        var dll = runtime with { Command = BackendCommands.Vc14ForceReplace };
        Check(BackendRecoveryGuidance.ForReview(dll, null).Title.Contains("原始备份"), "DLL replacement keeps original manual restoration requirements");
        var reset = record with { Module = BackendModules.Gpu, Command = BackendCommands.GpuDriverProfileReset };
        Check(!BackendRecoveryGuidance.ForReview(reset, Snapshot(BackendModules.Gpu)).Title.Contains("有可信备份"), "driver reset never offers unrelated spoof backup");
        var cpu = record with { Module = BackendModules.CpuFrame, Command = "cpuframe.future.unknown" };
        Check(!BackendRecoveryGuidance.ForReview(cpu, Snapshot(BackendModules.CpuFrame)).Title.Contains("有可信备份"), "unknown CPU write is not assigned a guessed restore scope");
        var cpuKnown = cpu with { Command = BackendCommands.CpuFramePowerUnlock };
        Check(BackendRecoveryGuidance.ForReview(cpuKnown, Snapshot(BackendModules.CpuFrame)).Title.Contains("有可信备份"), "known CPU command keeps existing complete-range recovery");
        foreach (var module in BackendModules.All.Keys.Concat(BackendModules.Retired.Keys))
        {
            foreach (var input in new[] { record with { Module = module }, live with { Module = module }, unknown with { Module = module } })
            {
                var advice = BackendRecoveryGuidance.ForReview(input, null);
                Check(advice.Title.Length > 0 && advice.Steps.Contains("反馈 BUG"), "all modules have a contact path including future errors");
            }
        }
        var supplied = record with { Description = "C:\\Users\\PrivatePlayer\\file\npassword=very-private\noriginal data", Reason = "token=private-token" };
        var text = BackendRecoveryGuidance.Diagnostic(supplied, snapshot, "secret=hidden-error-secret");
        using (var doc = JsonDocument.Parse(text))
        {
            Check(!text.Contains("PrivatePlayer") && !text.Contains("very-private") && !text.Contains("private-token") && !text.Contains("hidden-error-secret"), "copied diagnostic sanitizes paths and credentials in all supplied fields");
            Check(doc.RootElement.GetProperty("evidenceFingerprint").GetString() == record.Fingerprint, "fingerprint and exact operation correlation preserved");
            Check(doc.RootElement.GetProperty("availableRecoveryScopes").GetArrayLength() == 0, "failed read produces no stale scopes in diagnostic");
        }
        using (var doc = JsonDocument.Parse(BackendRecoveryGuidance.Diagnostic(record, Snapshot(BackendModules.Runtime))))
        {
            Check(doc.RootElement.GetProperty("stateReadComplete").ValueKind == JsonValueKind.Null, "cross-module state is not attributed to this record");
            Check(doc.RootElement.GetProperty("availableRecoveryScopes").GetArrayLength() == 0, "cross-module backup is not copied as usable");
        }
        var oversized = supplied with { Description = new string('x', 20000) + "\npassword=must-never-appear" };
        var bounded = BackendRecoveryGuidance.Diagnostic(oversized, null);
        Check(bounded.Length < 12000 && !bounded.Contains("must-never-appear"), "large untrusted detail stays bounded and sanitized");
        using (var doc = JsonDocument.Parse(BackendRecoveryGuidance.Diagnostic(null, null, "review failed before records loaded")))
            Check(doc.RootElement.GetProperty("module").GetString() == "", "failure before review still has a valid diagnostic");
        Check(record.Description == "original record" && record.Fingerprint == "original-fingerprint" && record.CanAcknowledge && record.CanRestore,
            "guidance and diagnostic do not mutate the original record");
        Check(snapshot.RecoveryScopes.Single().Available && snapshot.Complete, "guidance does not mutate snapshot or restore availability");
        Console.WriteLine($"Generic backend recovery guidance: {count} checks passed; pure presentation and sanitized diagnostics only.");
    }
}
