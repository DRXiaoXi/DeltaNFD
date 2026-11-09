using DeltaNFD.Backends;
using DeltaNFD.Services;
using DeltaNFD.Services.Backends;

internal static class NvProfileResetBackendChecks
{
    public static void Run()
    {
        var checks = 0;
        void Check(bool v, string label) { if (!v) throw new Exception(label); checks++; Console.WriteLine("PASS: " + label); }
        var command = BackendCommands.GpuDriverProfileReset;
        var ok = BackendResult.Ok(command, "verified", OperationResult.Ok("verified").WithDiagnostics("fixture"));
        var mapped = BackendNvProfileService.MapProfileResetResult(ok);
        Check(mapped.Success && mapped.DiagnosticsDirectory == "fixture", "verified receipt and local backup directory preserved");
        foreach (var status in new[] { BackendResultStatuses.Failed, BackendResultStatuses.Cancelled, BackendResultStatuses.Partial, "unknown" })
            Check(!BackendNvProfileService.MapProfileResetResult(ok with { Status = status }).Success,
                "success payload cannot override envelope " + status);
        foreach (var result in new[] { ok with { RequiresReview = true }, ok with { PendingRestore = true },
            BackendResult.PartialResult(command, "unknown", [], new OperationResult { Success = false, OutcomeUnknown = true, Message = "unknown" }) })
        {
            mapped = BackendNvProfileService.MapProfileResetResult(result);
            Check(!mapped.Success && mapped.OutcomeUnknown, "review or unknown flags never become success");
        }
        Check(!BackendNvProfileService.MapProfileResetResult(ok with { Data = null }).Success, "missing receipt cannot verify reset");
        mapped = BackendNvProfileService.MapProfileResetResult(ok with { Data = null, RequiresReview = true });
        Check(!mapped.Success && mapped.OutcomeUnknown, "missing receipt retains review requirement");
        var snapshot = new ReconcileSnapshot { Module = BackendModules.Gpu, Complete = true, Summary = "fixture",
            RecoveryScopes = [new() { Id = "spoof", Description = "fixture", Available = true }] };
        var review = new BackendUnconfirmedStore.Review(BackendModules.Gpu, "fixture", "fixture", true)
            { Command = command, CanRestore = true };
        Check(BackendRecovery.RecoveryChoices(review, snapshot).Count == 0,
            "profile reset cannot be cleared by unrelated display-name backup recovery");
        Check(BackendRecovery.RecoveryChoices(review with { Command = BackendCommands.GpuSpoofApply }, snapshot).Count == 1,
            "existing display-name recovery remains available for its own records");
        Check(BackendModules.RequiresWriteIntent(BackendModules.Gpu, command), "reset is protected by durable write intent");
        Console.WriteLine($"NVIDIA profile reset backend checks passed ({checks} assertions); simulated receipts only.");
    }
}
