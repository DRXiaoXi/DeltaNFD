using System.Text.Json;
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
        var indicator = BackendCommands.GpuDlssIndicatorSet;
        Check(BackendModules.IsAllowed(BackendModules.Gpu, indicator) && BackendModules.RequiresWriteIntent(BackendModules.Gpu, indicator)
            && !BackendModules.IsAllowed(BackendModules.System, indicator), "indicator writes are GPU-only and have a durable write intent");
        foreach (var requested in new[] { false, true })
        {
            var encoded = JsonSerializer.SerializeToElement(new NvDlssIndicatorArgs { Enabled = requested }, BackendJson.SerializerOptions);
            Check(encoded.EnumerateObject().Count() == 1 && BackendJson.Deserialize<NvDlssIndicatorArgs>(encoded).Enabled == requested,
                "indicator wire contract is exactly one explicit boolean " + requested);
        }
        foreach (var bad in new[] { "{}", "{\"enabled\":null}", "{\"enabled\":0}", "{\"Enabled\":true}", "{\"enabled\":true,\"registryPath\":\"arbitrary\"}", "{\"enabled\":true,\"value\":1}" })
        {
            using var json = JsonDocument.Parse(bad);
            bool rejected = false;
            try { _ = BackendJson.Deserialize<NvDlssIndicatorArgs>(json.RootElement); }
            catch (Exception ex) when (ex is JsonException or BackendContractException) { rejected = true; }
            Check(rejected, "indicator rejects invalid or extra members " + bad);
        }
        var ready = new BackendReady(Convert.ToBase64String(new byte[32]), BackendModules.Gpu, "0.9403.0", 0, 1, 1,
            @"C:\fixture.exe", new string('a', 64));
        var advertised = JsonSerializer.SerializeToElement(ready with { NvSettingsSaveVersion = NvDriverSettingPolicy.SaveProtocolVersion }, BackendJson.SerializerOptions);
        var legacy = JsonSerializer.SerializeToElement(ready, BackendJson.SerializerOptions);
        Check(!legacy.TryGetProperty("nvSettingsSaveVersion", out _) && BackendMessages.ParseReady(legacy).NvSettingsSaveVersion == 0,
            "old GPU save capability defaults to unsupported; other old handshake fields are unchanged");
        Check(BackendMessages.ParseReady(advertised).NvSettingsSaveVersion == NvDriverSettingPolicy.SaveProtocolVersion, "new GPU reports exact Nvidia save capability");
        foreach (var scopedWrite in new[] { BackendCommands.GpuDriverApply, BackendCommands.GpuDriverRestore, indicator })
        {
            Check(!BackendClient.SupportsNvSettingsSave(scopedWrite, 0) && !BackendClient.SupportsNvSettingsSave(scopedWrite, 2)
                && BackendClient.SupportsNvSettingsSave(scopedWrite, NvDriverSettingPolicy.SaveProtocolVersion), "mixed binaries cannot send this write " + scopedWrite);
            var scopedReview = review with { Command = scopedWrite };
            Check(BackendRecovery.RecoveryChoices(scopedReview, snapshot).Count == 0
                && BackendRecovery.NvSettingsReviewScope(scopedReview) == (scopedWrite == indicator ? "dlss-indicator" : "driver-settings"),
                "saved setting review is scoped and never offers display name restoration: " + scopedWrite);
        }
        Check(BackendClient.SupportsNvSettingsSave(BackendCommands.GpuDriverRead, 0), "old read-only driver command remains available");
        foreach (var write in new[] { BackendCommands.GpuDriverApply, indicator })
        {
            var receipt = BackendResult.Ok(write, "fixture", OperationResult.Ok("verified save"));
            Check(BackendNvProfileService.Map(receipt, "save").Success, "NVIDIA write only recognizes a verified OK receipt " + write);
            Check(!BackendNvProfileService.Map(receipt with { Status = BackendResultStatuses.Failed }, "save").Success, "failed envelope cannot override receipt " + write);
            mapped = BackendNvProfileService.Map(receipt with { Data = null }, "save");
            Check(!mapped.Success && mapped.OutcomeUnknown, "bare OK envelope cannot verify persistence " + write);
            mapped = BackendNvProfileService.Map(BackendResult.PartialResult(write, "partial", [], requiresReview: false), "save");
            Check(!mapped.Success && mapped.IsPartial && !mapped.OutcomeUnknown, "bare known partial is not success " + write);
            mapped = BackendNvProfileService.Map(BackendResult.PartialResult(write, "recheck", [], new OperationResult { Message = "readback mismatch", OutcomeUnknown = true }), "save");
            Check(!mapped.Success && mapped.OutcomeUnknown && mapped.Message.Contains("readback mismatch"), "post-write uncertainty and detailed result survive map " + write);
            mapped = BackendNvProfileService.Map(BackendResult.Failed("backend.transport-unconfirmed", "transport"), "save");
            Check(!mapped.Success && mapped.OutcomeUnknown, "uncertain transport cannot turn into a known pre-write denial " + write);
            mapped = BackendNvProfileService.Map(BackendResult.Failed("backend.nv-settings-unsupported", "old backend"), "save");
            Check(!mapped.Success && !mapped.OutcomeUnknown, "old backend rejection is known and does not simulate write " + write);
        }
        Console.WriteLine($"NVIDIA settings/reset backend checks passed ({checks} assertions); simulated receipts only.");
    }
}
