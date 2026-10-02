using System.Diagnostics;
using DeltaNFD.Native;
using DeltaNFD.Services;

internal static class OfflineCpuSetNativeChecks
{
    public static async Task RunIsolatedAsync()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定冒烟程序路径。");
        var start = new ProcessStartInfo { FileName = exe, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--offline-cpu-set-native-child");
        using var child = Process.Start(start) ?? throw new InvalidOperationException("无法启动临时 CPU Sets 子进程。");
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (TimeoutException)
        {
            try { child.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException("临时 CPU Sets 子进程超时，已结束该子进程。");
        }
        var output = await stdout;
        var error = await stderr;
        if (child.ExitCode != 0)
            throw new InvalidOperationException($"临时进程 CPU Sets 原值/写入/复核/还原检查失败（{child.ExitCode}）：{error}{output}");
        _ = output;
        Console.WriteLine("Temporary child-process CPU Sets apply/readback/restore check passed.");
    }

    public static void RunCurrentProcess()
    {
        var api = Environment.OSVersion.Version.Build >= 22000
            ? OfflineCpuSetApi.Windows11Masks
            : OfflineCpuSetApi.Windows10Ids;
        if (!OfflineCpuSetCatalog.TryRead(out var catalog, out var catalogError))
            throw new InvalidOperationException("系统 CPU Sets 只读查询失败：" + catalogError);

        var selectedCandidates = catalog.Where(x => x.Group == 0 && x.IsAvailable && x.LogicalProcessorIndex < 64).ToArray();
        if (selectedCandidates.Length == 0)
            throw new InvalidOperationException("没有找到可用的组 0 CPU Set。");
        var selected = selectedCandidates[0];

        using var self = Process.GetCurrentProcess();
        var handle = CpuSets.OpenProcessForDefaultCpuSets(self.Id);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("无法以最小权限打开临时测试进程。");

        var originalRead = false;
        var originalIds = Array.Empty<uint>();
        var originalMasks = Array.Empty<CpuSetGroupMask>();
        var originalWasSet = false;
        var desiredMask = 1UL << selected.LogicalProcessorIndex;
        try
        {
            if (api == OfflineCpuSetApi.Windows10Ids)
            {
                originalRead = CpuSets.TryGetDefaultCpuSetIds(handle, out originalIds);
                if (!originalRead) throw new InvalidOperationException("无法读取 Windows 10 CPU Set ID 原值。");
                originalWasSet = originalIds.Length != 0;
            }
            else
            {
                originalRead = CpuSets.TryGetDefaultCpuSetMasks(handle, out originalMasks);
                if (!originalRead) throw new InvalidOperationException("无法读取 Windows 11 CPU Set 掩码原值。");
                originalWasSet = originalMasks.Length != 0;
            }

            var set = api == OfflineCpuSetApi.Windows10Ids
                ? CpuSets.TrySetDefaultCpuSetIds(handle, [selected.Id])
                : CpuSets.TrySetDefaultCpuSetMasks(handle, [new CpuSetGroupMask(0, desiredMask)]);
            if (!set) throw new InvalidOperationException("设置临时测试进程默认 CPU Sets 失败。");

            var verified = api == OfflineCpuSetApi.Windows10Ids
                ? CpuSets.TryGetDefaultCpuSetIds(handle, out var actualIds) && actualIds.SequenceEqual(new[] { selected.Id })
                : CpuSets.TryGetDefaultCpuSetMasks(handle, out var actualMasks) &&
                  actualMasks.SequenceEqual(new[] { new CpuSetGroupMask(0, desiredMask) });
            if (!verified) throw new InvalidOperationException("CPU Sets 写入后读回不匹配。");
        }
        finally
        {
            try
            {
                if (originalRead)
                {
                    var restored = api == OfflineCpuSetApi.Windows10Ids
                        ? CpuSets.TrySetDefaultCpuSetIds(handle, originalWasSet ? originalIds : [])
                        : CpuSets.TrySetDefaultCpuSetMasks(handle, originalWasSet ? originalMasks : []);
                    if (!restored) throw new InvalidOperationException("临时测试进程 CPU Sets 原值写回失败。");

                    var verifiedRestore = api == OfflineCpuSetApi.Windows10Ids
                        ? CpuSets.TryGetDefaultCpuSetIds(handle, out var checkIds) &&
                          (originalWasSet ? checkIds.SequenceEqual(originalIds) : checkIds.Length == 0)
                        : CpuSets.TryGetDefaultCpuSetMasks(handle, out var checkMasks) &&
                          (originalWasSet ? checkMasks.SequenceEqual(originalMasks) : checkMasks.Length == 0);
                    if (!verifiedRestore) throw new InvalidOperationException("临时测试进程 CPU Sets 原值恢复复核失败。");
                }
            }
            finally { CpuSets.CloseHandleSafe(handle); }
        }

        Console.WriteLine($"临时子进程 CPU Sets API 读原值、应用、读回及还原通过（{api}；ID {selected.Id}）。");
    }
}
