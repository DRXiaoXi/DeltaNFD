using DeltaNFD.Services;

internal static class GpuSpoofRecoveryChecks
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD-gpu-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "settings.json");
        const string target = @"SYSTEM\CurrentControlSet\Enum\PCI\VEN_10DE&DEV_28E0\4&test&0";
        const string native = "@oem118.inf,%nvidia_dev.28e0%;NVIDIA GeForce RTX 4060 Laptop GPU";
        var count = 0;
        void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); count++; }
        void Refuse(Action run, string reason)
        { try { run(); } catch (IOException) { count++; return; } throw new Exception(reason); }
        void Save(AppSettings settings) { if (!AppSettings.Save(path, settings)) throw new IOException("fixture save failed"); }
        DisplayAdapterInfo Adapter(string desc = native, bool backup = false, bool masked = false, string adapterPath = target) => new()
            { RegistryPath = adapterPath, DeviceDesc = desc, DisplayName = "RTX 4060 Laptop GPU", HasBackup = backup, IsMasked = masked };
        try
        {
            Save(new() { RuntimeGuardEnabled = true, RuntimeDetectionEnabled = false, UpdateServer = UpdateServer.GitHub });
            var pending = GpuSpoofRestoreState.Schedule(target, path);
            Check(pending.Pending && pending.Path == target && Guid.TryParse(pending.Id, out _), "Schedule assigns a persistent generation");
            Check(GpuSpoofRestoreState.Same(pending, GpuSpoofRestoreState.Capture(AppSettingsStore.Read(path))), "Schedule persisted exact task");
            Refuse(() => GpuSpoofRestoreState.Schedule(target, path), "Existing pending task cannot be overwritten");
            GpuSpoofRestoreState.Complete(pending, target, path);
            var done = AppSettingsStore.Read(path);
            Check(!done.TempSpoofRestorePending && done.TempSpoofRestorePath == "" && done.TempSpoofRestoreId == "", "Completion clears all pending identity fields");
            Check(done.RuntimeGuardEnabled && !done.RuntimeDetectionEnabled && done.UpdateServer == UpdateServer.GitHub, "Unrelated preferences remain");
            GpuSpoofRestoreState.Complete(pending, target, path);
            Check(!AppSettingsStore.Read(path).TempSpoofRestorePending, "Repeated completion is idempotent");

            var fresh = GpuSpoofRestoreState.Schedule(target, path);
            Refuse(() => GpuSpoofRestoreState.Complete(pending, target, path), "A late completion cannot clear a new same-path task");
            Check(AppSettingsStore.Read(path).TempSpoofRestoreId == fresh.Id, "New generation is preserved");
            GpuSpoofRestoreState.Complete(fresh, target + "other", path);
            Check(AppSettingsStore.Read(path).TempSpoofRestorePending, "Restoring another adapter leaves pending task intact");

            Check(GpuSpoofRestoreState.CanReconcile(fresh, Adapter()), "Native descriptor and no backup permit explicit reconciliation");
            foreach (var adapter in new[] { Adapter("NVIDIA GeForce GTX 750", masked: true), Adapter(backup: true), Adapter(adapterPath: target + "other"), Adapter("@fake"), Adapter(native + "\nGTX 750") })
                Check(!GpuSpoofRestoreState.CanReconcile(fresh, adapter), "Masked/backup/mismatch/invalid descriptor cannot be reconciled");
            Check(!GpuSpoofRestoreState.CanReconcile(fresh, null), "Missing device is not treated as restored");
            Check(!GpuSpoofRestoreState.CanReconcile(new(false, target, ""), Adapter()), "No pending task is not a reconciliation candidate");

            var order = new List<string>();
            GpuSpoofRestoreState.Finish(() => order.Add("restore+verify"), () => order.Add("persist"), () => order.Add("remove-backup"));
            Check(order.SequenceEqual(new[] { "restore+verify", "persist", "remove-backup" }), "Restore is verified and state durable before backup deletion");
            foreach (var failAt in new[] { "restore", "persist" })
            {
                var removed = false;
                Refuse(() => GpuSpoofRestoreState.Finish(() => { if (failAt == "restore") throw new IOException("readback"); },
                    () => { if (failAt == "persist") throw new IOException("disk"); }, () => removed = true), "Injected failure must propagate");
                Check(!removed, "Backup remains on restoration/persistence failure");
            }

            Directory.CreateDirectory(path + ".tmp");
            var backupRetained = true;
            Refuse(() => GpuSpoofRestoreState.Finish(() => { }, () => GpuSpoofRestoreState.Complete(fresh, target, path), () => backupRetained = false), "Real settings save failure cannot report success");
            Check(backupRetained && AppSettingsStore.Read(path).TempSpoofRestorePending, "Save refusal preserves pending record and backup");
            Directory.Delete(path + ".tmp");

            File.WriteAllText(path, "{broken");
            Refuse(() => { try { AppSettingsStore.UpdateRequired(path, s => s.TempSpoofRestorePending = false); } catch (System.Text.Json.JsonException ex) { throw new IOException("malformed", ex); } }, "Corrupt settings cannot be overwritten with defaults");
            Check(File.ReadAllText(path) == "{broken", "Corrupt original retained");
            try { AppSettingsStore.ReadRequired(path); throw new Exception("Corrupt settings treated as absent"); }
            catch (System.Text.Json.JsonException) { count++; }
            Save(new() { TempSpoofRestorePending = true, TempSpoofRestorePath = target }); // Legacy versions had no task id.
            var service = new Fake { Current = Adapter() };
            var preview = await GpuSpoofPendingRecovery.PreviewAsync(service, path);
            Check(preview is not null && preview.Pending.Id == "", "Old no-id stuck record can be previewed");
            Check(AppSettingsStore.Read(path).TempSpoofRestorePending, "Read-only preview cannot implicitly complete a task");
            await GpuSpoofPendingRecovery.CompleteAsync(service, preview!, path);
            Check(!AppSettingsStore.Read(path).TempSpoofRestorePending && service.ModifyingCalls == 0, "Old stuck task reconciles without device writes");

            Save(new() { TempSpoofRestorePending = true, TempSpoofRestorePath = target });
            preview = await GpuSpoofPendingRecovery.PreviewAsync(service, path);
            service.Current = Adapter("NVIDIA GeForce GTX 750", masked: true);
            try { await GpuSpoofPendingRecovery.CompleteAsync(service, preview!, path); throw new Exception("Changed device accepted"); }
            catch (IOException) { Check(AppSettingsStore.Read(path).TempSpoofRestorePending, "Changed device after confirmation remains pending"); }
            service.Current = Adapter();
            preview = await GpuSpoofPendingRecovery.PreviewAsync(service, path);
            service.Current = Adapter(native.Replace("oem118", "oem119"));
            try { await GpuSpoofPendingRecovery.CompleteAsync(service, preview!, path); throw new Exception("Changed driver descriptor accepted"); }
            catch (IOException) { Check(AppSettingsStore.Read(path).TempSpoofRestorePending, "Even a new native driver descriptor needs fresh consent"); }
            service.Current = Adapter();
            preview = await GpuSpoofPendingRecovery.PreviewAsync(service, path);
            AppSettingsStore.UpdateRequired(path, s => s.TempSpoofRestoreId = "new-task");
            try { await GpuSpoofPendingRecovery.CompleteAsync(service, preview!, path); throw new Exception("Changed task accepted"); }
            catch (IOException) { Check(AppSettingsStore.Read(path).TempSpoofRestorePending, "Changed task after confirmation remains pending"); }
            service.Error = true;
            try { await GpuSpoofPendingRecovery.PreviewAsync(service, path); throw new Exception("Read failure accepted"); }
            catch (IOException) { Check(AppSettingsStore.Read(path).TempSpoofRestorePending, "Unreadable device/backup never clears the task"); }
            Check(service.ModifyingCalls == 0, "Reconciliation uses only adapter reads and configuration state");
            Console.WriteLine($"GPU spoof recovery: {count} checks passed. Temporary configuration and mock devices only; no registry, driver or task changes.");
        }
        finally
        {
            var full = Path.GetFullPath(root);
            if (Path.GetDirectoryName(full) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                || !Path.GetFileName(full).StartsWith("DeltaNFD-gpu-recovery-", StringComparison.Ordinal)) throw new IOException("Unsafe fixture cleanup");
            Directory.Delete(full, true);
        }
    }

    private sealed class Fake : IGpuSpoofService
    {
        internal DisplayAdapterInfo? Current;
        internal bool Error;
        internal int ModifyingCalls;
        public IReadOnlyList<string> PresetNames => [];
        public Task<List<DisplayAdapterInfo>> GetAdaptersAsync() => Error ? Task.FromException<List<DisplayAdapterInfo>>(new IOException("device/backup unreadable"))
            : Task.FromResult(Current is null ? new List<DisplayAdapterInfo>() : new List<DisplayAdapterInfo> { Current });
        public Task<OperationResult> SpoofAsync(DisplayAdapterInfo adapter, string name) { ModifyingCalls++; throw new Exception("Device write forbidden"); }
        public Task<OperationResult> RestoreAsync(DisplayAdapterInfo adapter) { ModifyingCalls++; throw new Exception("Device write forbidden"); }
        public Task<OperationResult> SpoofByPathAsync(string path, string name) { ModifyingCalls++; throw new Exception("Device write forbidden"); }
        public Task<OperationResult> RestoreByPathAsync(string path) { ModifyingCalls++; throw new Exception("Device write forbidden"); }
        public Task<IReadOnlyList<GpuSpoofInvalidation>> CheckInvalidationAsync() => Task.FromResult<IReadOnlyList<GpuSpoofInvalidation>>([]);
    }
}
