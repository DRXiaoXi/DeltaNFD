using DeltaNFD.Services;
using DeltaNFD.ViewModels;
using System.Text.Json;

internal static class RuntimeDetectionPreferenceChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool pass, string name)
        {
            if (!pass) throw new InvalidOperationException(name);
            count++;
        }
        Check(new AppSettings().RuntimeDetectionEnabled, "New users default to detection on");
        Check(JsonSerializer.Deserialize<AppSettings>("{\"RuntimeGuardEnabled\":true}") is { RuntimeDetectionEnabled: true, RuntimeGuardEnabled: true }, "Old configuration keeps detection on and guard unchanged");
        var settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new AppSettings
            { RuntimeDetectionEnabled = false, RuntimeGuardEnabled = true, UpdateMode = UpdateMode.FullInstaller }));
        Check(settings is { RuntimeDetectionEnabled: false, RuntimeGuardEnabled: true, UpdateMode: UpdateMode.FullInstaller }, "Disabled preference survives restart serialization independently of guard/update choices");

        var saved = true;
        var preference = new RuntimeDetectionPreference(() => saved, value => saved = value);
        var calls = 0;
        Task<int> Read() { calls++; return Task.FromResult(42); }
        var first = await preference.ReadAsync(Read);
        Check(first.Value == 42 && preference.IsCurrent(first.Snapshot) && calls == 1, "Enabled detection calls the backend");
        preference.SetEnabled(false);
        var disabled = await preference.ReadAsync(Read);
        Check(!disabled.Snapshot.Enabled && !preference.IsCurrent(disabled.Snapshot) && calls == 1, "Disabled detection never calls the backend");
        Check(!saved && !preference.IsCurrent(first.Snapshot), "Closing invalidates existing results and persists");
        preference.SetEnabled(true);
        Check(saved && !preference.IsCurrent(first.Snapshot), "Reopening cannot revive pre-disable results");

        var deferred = new TaskCompletionSource<int>();
        var late = preference.ReadAsync(() => deferred.Task);
        preference.SetEnabled(false);
        preference.SetEnabled(true);
        var current = await preference.ReadAsync(Read);
        deferred.SetResult(13);
        var stale = await late;
        Check(!preference.IsCurrent(stale.Snapshot) && preference.IsCurrent(current.Snapshot), "Off/on rejects a late scan while retaining a fresh scan");
        var failed = await preference.ReadAsync<int>(() => Task.FromException<int>(new IOException("scan error")));
        Check(failed.Error?.Message == "scan error" && preference.IsCurrent(failed.Snapshot), "An enabled backend failure remains an error");
        var deferredError = new TaskCompletionSource<int>();
        var lateError = preference.ReadAsync(() => deferredError.Task);
        preference.SetEnabled(false);
        deferredError.SetException(new IOException("old error"));
        Check(!preference.IsCurrent((await lateError).Snapshot), "A late error cannot replace the disabled state");
        saved = true;
        preference.Reload();
        Check(preference.Enabled, "External restored setting is reloaded");

        var cannotSave = new RuntimeDetectionPreference(() => true, _ => { });
        try { cannotSave.SetEnabled(false); throw new InvalidOperationException("Save failure was hidden"); }
        catch (IOException) { Check(cannotSave.Enabled, "Failed persistence keeps the real saved switch value"); }
        var deniedSave = new RuntimeDetectionPreference(() => true, _ => throw new IOException("denied"));
        try { deniedSave.SetEnabled(false); throw new InvalidOperationException("Save exception was hidden"); }
        catch (IOException) { Check(deniedSave.Enabled, "Write denial does not silently disable detection"); }

        Check(DashboardHealthPresentation.Label(DashboardHealthState.Disabled) == "已关闭", "Disabled uses a neutral distinct label");
        Check(DashboardHealthPresentation.Summary(false, DashboardHealthState.Normal, DashboardHealthState.Normal, DashboardHealthState.Disabled) == "部分组件检测已关闭", "Disabled is not reported as all components healthy");
        Check(DashboardHealthPresentation.Summary(false, DashboardHealthState.Abnormal, DashboardHealthState.Normal, DashboardHealthState.Disabled).Contains("异常"), "Other component errors remain visible");
        Check(DashboardHealthPresentation.Summary(false, DashboardHealthState.Warning, DashboardHealthState.Normal, DashboardHealthState.Disabled).Contains("提醒"), "Other warnings remain visible");

        Check(RuntimeRepairGuidance.ForResult(OperationResult.Ok("success"), false) == "", "Successful repair does not claim failure");
        Check(RuntimeRepairGuidance.ForResult(OperationResult.Skip("no changes"), false) == "", "Skipped operation does not claim failure");
        Check(RuntimeRepairGuidance.ForResult(OperationResult.Fail("cancelled"), true) == "", "Cancellation does not claim the tool cannot repair");
        Check(RuntimeRepairGuidance.ForResult(new OperationResult { Success = true, IsPartial = true }, false).Length > 0, "Partial results are not treated as complete repair");
        var message = RuntimeRepairGuidance.ForResult(OperationResult.Fail("error 1402, diagnostic path"), false);
        Check(message.Contains("工具未能解决") && message.Contains("14.42.34433") && message.Contains("x64、x86"), "Failure admits limitation and identifies the manual baseline");
        Check(message.Contains("关闭上方“运行库检测”") && message.Contains("运行库防护"), "Manual guidance includes optional detection and separate guard control");
        Check(RuntimeRepairGuidance.DialogText("error 1402, diagnostic path", message).StartsWith("error 1402, diagnostic path"), "Original failure and diagnostics are retained");
        var diagnostic = OperationResult.Fail("error 1402").WithDiagnostics("C:\\fixture\\logs");
        Check(RuntimeRepairGuidance.OriginalText(diagnostic).Contains("C:\\fixture\\logs"), "Separate diagnostics field is visible in the result");
        Check(RuntimeRepairGuidance.OriginalText(OperationResult.Fail("error C:\\fixture\\logs").WithDiagnostics("C:\\fixture\\logs")) == "error C:\\fixture\\logs", "Existing diagnostic text is not duplicated");
        var unknown = RuntimeRepairGuidance.ForResult(new OperationResult { OutcomeUnknown = true }, false);
        Check(unknown.IndexOf("等待安装事务结束", StringComparison.Ordinal) < unknown.IndexOf("可自行", StringComparison.Ordinal), "Unknown outcome defers manual uninstall until the transaction is reconciled");
        var reboot = RuntimeRepairGuidance.ForResult(new OperationResult { RequiresReboot = true }, false);
        Check(reboot.IndexOf("按原提示重启", StringComparison.Ordinal) < reboot.IndexOf("可自行", StringComparison.Ordinal), "Restart is handled before manual actions");
        Console.WriteLine($"Runtime detection preference and manual guidance: {count} checks passed. Simulated reads/settings only; no host runtime, registry or service changes.");
    }
}
