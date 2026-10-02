using DeltaNFD.Services;

/// <summary>
/// ACE 检测闸门只读检查：验证「三角洲进程运行时禁止对 ACE 组件做检测」这条需求。
///
/// 通过 <see cref="AceService"/> 的测试接缝注入「三角洲是否在运行」的判定，
/// 从而在不真的启动游戏的前提下覆盖暂停分支。全部为只读：不结束进程、不删服务、
/// 不改注册表（绝不调用 CleanAsync）。
/// </summary>
internal static class AceGuardChecks
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeltaNFD_AceGuardChecks_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new TweakBackupStore(Path.Combine(root, "backups.json"));

            // 自定义主进程模式（§43）下 ACE 功能整体为 Delta-only，闸门在 IsCustom 之后才生效，
            // 此时只能校验"不适用"语义，避免在开了自定义模式的机器上误报失败。
            if (GameTargetService.Default.IsCustom)
            {
                var customScan = await new AceService(store, null, static () => true).ScanAsync();
                if (customScan.BlockedByGame)
                    throw new Exception("自定义主进程模式下不应报告 BlockedByGame（应为 Delta-only 说明）");
                if (customScan.BlockingProcesses.Count == 0)
                    throw new Exception("自定义主进程模式下应给出 Delta-only 说明");
                Console.WriteLine("ACE 检测闸门检查通过（当前为自定义主进程模式，仅校验 Delta-only 语义）");
                return;
            }

            // 1) 三角洲运行中：必须完全跳过，且结果里不能留下任何"读过 ACE"的痕迹。
            var blockedScan = await new AceService(store, null, static () => true).ScanAsync();
            if (!blockedScan.BlockedByGame)
                throw new Exception("三角洲运行中时 ScanAsync 未标记 BlockedByGame");
            if (blockedScan.Services.Count != 0 || blockedScan.Paths.Count != 0 || blockedScan.RegistryKeys.Count != 0)
                throw new Exception("三角洲运行中时 ScanAsync 仍读取了 ACE 服务/目录/注册表");
            if (blockedScan.AnythingFound)
                throw new Exception("三角洲运行中时 AnythingFound 应为 false");
            if (blockedScan.CanClean)
                throw new Exception("三角洲运行中时 CanClean 必须为 false（不得被判为可清理）");

            var blockedCore = await new AceService(store, null, static () => true).CheckCoreFilesAsync();
            if (blockedCore.Level != AceCoreCheckLevel.Blocked)
                throw new Exception($"三角洲运行中时 ACE-CORE 检测应为 Blocked，实际为 {blockedCore.Level}");
            if (blockedCore.SysCount != 0 || blockedCore.Files.Count != 0)
                throw new Exception("三角洲运行中时 ACE-CORE 检测仍返回了文件明细");

            // 2) 三角洲未运行：闸门必须放行，且确实执行了组件枚举（空结果会暴露"忘记放行"）。
            var freeScan = await new AceService(store, null, static () => false).ScanAsync();
            if (freeScan.BlockedByGame)
                throw new Exception("三角洲未运行时 ScanAsync 被误判为 BlockedByGame");
            if (freeScan.Services.Count == 0)
                throw new Exception("三角洲未运行时 ScanAsync 未执行 ACE 服务枚举");

            var freeCore = await new AceService(store, null, static () => false).CheckCoreFilesAsync();
            if (freeCore.Level == AceCoreCheckLevel.Blocked)
                throw new Exception("三角洲未运行时 ACE-CORE 检测被误判为 Blocked");

            Console.WriteLine(
                $"ACE 检测闸门检查通过：运行中完全跳过组件扫描与 ACE-CORE 检测（服务 {blockedScan.Services.Count} 项、"
                + $"目录 {blockedScan.Paths.Count} 项均为空）且禁止清理；未运行时正常枚举 {freeScan.Services.Count} 项服务");
        }
        finally
        {
            var fullRoot = Path.GetFullPath(root);
            var tempPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (fullRoot.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(fullRoot, recursive: true);
        }
    }
}
