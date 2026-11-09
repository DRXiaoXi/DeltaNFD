using DeltaNFD.Inspection;
using Microsoft.Win32;

namespace DeltaNFD.Services;

public sealed partial class SystemTweakService
{
    internal Task<OperationResult> EnableMemorySupportAsync(LowMemoryAction action) =>
        new MemorySupportControl(_backups, new MemoryIo()).EnableAsync(action);
    private Task<OperationResult> RestoreSysMainMemoryAsync() => new MemorySupportControl(_backups, new MemoryIo()).RestoreSysMainAsync();
    private sealed class MemoryIo : IMemorySupportIo
    {
        private const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\SysMain";
        public string? Blocker()
        {
            if (!ElevationHelper.IsElevated) return ElevationHelper.NotElevatedMessage;
            try { return DeltaForceLocator.IsGameRunning() ? "请先退出三角洲。" : null; }
            catch { return "无法确认游戏运行状态。"; }
        }
        public MemoryServiceOriginal ReadSysMain()
        {
            var service = SecurityFeatureInspection.ReadService("SysMain");
            if (service.Exists != true || service.Start is not (2 or 3 or 4) || service.RuntimeState is not (1 or 4))
                throw new InvalidOperationException("SysMain 配置或运行状态待确认。");
            using var key = Registry.LocalMachine.OpenSubKey(ServiceKey);
            var raw = key?.GetValue("DelayedAutoStart");
            if (raw is not null and not int) throw new InvalidDataException("SysMain 延迟启动配置不可读。");
            return new(service.Start.Value, service.RuntimeState == 4, raw as int?);
        }
        public async Task<int> ConfigureSysMainAsync(int start, bool delayed)
        {
            var target = start switch { 2 => delayed ? "delayed-auto" : "auto", 3 => "demand", 4 => "disabled", _ => throw new InvalidOperationException("启动类型无效") };
            var result = await RunProcessCaptureAsync("sc.exe", "config SysMain start= " + target, TimeSpan.FromSeconds(20));
            return result.ExitCode;
        }
        public async Task<int> SetSysMainRunningAsync(bool running)
        {
            if (ReadSysMain().Running == running) return 0;
            var result = await RunProcessCaptureAsync("sc.exe", running ? "start SysMain" : "stop SysMain", TimeSpan.FromSeconds(20));
            if (result.ExitCode != 0) return result.ExitCode;
            for (var i = 0; i < 20; i++)
            {
                var status = SecurityFeatureInspection.ReadService("SysMain").RuntimeState;
                if (status == (running ? 4u : 1u)) return 0;
                await Task.Delay(250);
            }
            return -1;
        }
        public void RestoreDelayed(int? value)
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServiceKey, writable: true) ?? throw new IOException("无法写入 SysMain 原值。");
            if (value is null) key.DeleteValue("DelayedAutoStart", false); else key.SetValue("DelayedAutoStart", value.Value, RegistryValueKind.DWord);
        }
        private static string Property(string property) => property is "MemoryCompression" or "PageCombining" ? property : throw new InvalidOperationException("内存功能不在范围内");
        public async Task<bool?> ReadMmAsync(string property)
        {
            var result = await RunPowerShellAsync("[int](Get-MMAgent -ErrorAction Stop)." + Property(property), TimeSpan.FromSeconds(20));
            return result.ExitCode != 0 ? null : result.StdOut?.Trim() switch { "0" => false, "1" => true, _ => null };
        }
        public async Task<int> EnableMmAsync(string property) =>
            (await RunPowerShellAsync("Enable-MMAgent -" + Property(property) + " -ErrorAction Stop", TimeSpan.FromSeconds(30))).ExitCode;
    }
}
