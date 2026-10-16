using DeltaNFD.Services;

internal static class OfflineModeStateChecks
{
    public static void Run()
    {
        var defaultState = new OfflineModeState();
        Require(!defaultState.OfflineModeEnabled && defaultState.Status == OfflineModeStatus.Off &&
                !defaultState.BlocksNormalAutomation,
            "a missing offline-mode file must default to the normal mode");

        var tempDirectory = Path.Combine(Path.GetTempPath(), $"DeltaNFD_OfflineState_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var path = Path.Combine(tempDirectory, "offline-mode.json");
        try
        {
            var store = new OfflineModeStateStore(path);
            var written = store.TryUpdate(state =>
            {
                state.OfflineModeEnabled = true;
                state.Status = OfflineModeStatus.Ready;
                state.SavedPreferences = new OfflineModePreferences
                {
                    FrameModeWasActive = true,
                    DualCcdGameCcdIndex = 1,
                    GameAffinityRuleMask = 0x55,
                    FramePowerLockTargetGuid = "{01234567-89AB-CDEF-0123-456789ABCDEF}",
                };
                state.CpuSetChanges.Add(new OfflineCpuSetChange
                {
                    ProcessId = 1234,
                    StartTimeUtcTicks = 638950000000000000,
                    ExecutablePath = @"C:\Games\DeltaForce\DeltaForceClient-Win64-Shipping.exe",
                    Purpose = "target-game",
                    Api = OfflineCpuSetApi.Windows10Ids,
                    OriginalAssignmentWasSet = true,
                    OriginalCpuSetIds = [7, 11],
                    Status = OfflineCpuSetRecordStatus.Applied,
                });
            }, out var error);
            Require(written, "valid offline state should save: " + error);

            var secondReader = new OfflineModeStateStore(path);
            Require(secondReader.TryRead(out var readBack, out error), "saved offline state should reload: " + error);
            Require(readBack.OfflineModeEnabled && readBack.BlocksNormalAutomation, "active offline state must gate ordinary automation");
            Require(readBack.SavedPreferences?.FrameModeWasActive == true && readBack.SavedPreferences.DualCcdGameCcdIndex == 1,
                "saved preferences should round-trip");
            Require(readBack.CpuSetChanges.Count == 1 && readBack.CpuSetChanges[0].OriginalCpuSetIds.SequenceEqual(new uint[] { 7, 11 }),
                "CPU Set restoration data should round-trip");

            Require(!store.TryUpdate(state => { state.SavedPreferences = null; state.OfflineModeEnabled = true; }, out _),
                "an enabled state without its preference snapshot must be rejected");
            Require(secondReader.TryRead(out var unchanged, out error) && unchanged.OfflineModeEnabled,
                "rejected state must leave the previously saved state intact");

            Require(OfflineModeExecutionLock.TryAcquire(store, out var lease, out error), "first offline operation lock should be acquired: " + error);
            using (lease)
            {
                Require(!OfflineModeExecutionLock.TryAcquire(store, out _, out _), "a second operation must not overlap");
            }
            Require(OfflineModeExecutionLock.TryAcquire(store, out var afterRelease, out error), "lock should release with its file handle: " + error);
            afterRelease?.Dispose();

            var helperLease = OfflineModeHelperInstanceLease.TryAcquire(store, out error);
            Require(helperLease is not null, "first helper instance lease should be acquired: " + error);
            using (helperLease)
                Require(OfflineModeHelperInstanceLease.TryAcquire(store, out _) is null, "a second helper process must be refused");
            using var helperLeaseAfterExit = OfflineModeHelperInstanceLease.TryAcquire(store, out error);
            Require(helperLeaseAfterExit is not null, "helper lease should be released when the owner exits: " + error);

            File.WriteAllText(path, "{broken", System.Text.Encoding.UTF8);
            Require(!secondReader.TryRead(out _, out error) && error.Contains("损坏", StringComparison.Ordinal),
                "corrupt state must fail closed rather than reset to defaults");

            Console.WriteLine("脱机状态持久化、恢复清单和损坏文件 fail-closed 检查通过。");
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            try
            {
                var lockPath = Path.Combine(tempDirectory, "offline-operation.lock");
                if (File.Exists(lockPath)) File.Delete(lockPath);
                var helperLockPath = Path.Combine(tempDirectory, "offline-helper.instance.lock");
                if (File.Exists(helperLockPath)) File.Delete(helperLockPath);
                Directory.Delete(tempDirectory, recursive: false);
            }
            catch { }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
